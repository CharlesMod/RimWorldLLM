"""The director: turns bridge events into leased, grounded, short lines (SPEC §7).

One ``step()`` polls events, writes witness-only memories, submits work to the lease manager,
starts what the windows allow, and saves dirty minds. Work runs as asyncio tasks; ``drain()``
waits for them (tests and replay use it).
"""
from __future__ import annotations

import asyncio
import hashlib
import logging
import random
import time
from typing import Callable

from . import memory
from .bridge import BridgeClient, BridgeError, EventPoller
from .builders import display_name, ensure_mind_built, short_name, talk_weight
from .config import Config
from .conversation import Conversation, template_memory, template_relation
from .ledger import Ledger
from .lease import Cls, Lease, LeaseManager, Now, QuotaTracker, WorkItem, class_of
from .llm import LLMClient, LLMError
from .minds import Mind, Relation
from .passes import Dropped, SanitizeCtx, Spoken, speak
from .prompt import build_prefix, scene_from_bridge
from .sanitize import name_tokens, prompt_cap_words

log = logging.getLogger("hermesvoice")


class Director:
    def __init__(self, cfg: Config, bridge: BridgeClient, llm: LLMClient,
                 clock: Callable[[], float] = time.monotonic, rng: random.Random | None = None):
        self.cfg, self.bridge, self.llm, self.clock = cfg, bridge, llm, clock
        self.rng = rng or random.Random(7)
        self.minds: dict[int, Mind] = {}
        self.revs: dict[int, int] = {}
        self.dirty: set[int] = set()
        self.names: dict[int, str] = {}          # short names
        self.name_tokens: dict[int, set[str]] = {}
        self.weights: dict[int, float] = {}
        self.ledger = Ledger()
        q = cfg.quotas
        self.leases = LeaseManager(cfg.pawn_slots, cfg.narrator_slot,
                                   QuotaTracker(q.pawn_per_day, q.pair_per_day, q.ticks_per_day,
                                                q.min_gap_ticks_ambient, q.min_gap_real_s_ambient),
                                   cfg.pause_ambient_at_speed)
        self.poller = EventPoller(bridge)
        self.tasks: set[asyncio.Task] = set()
        self.bridge_cfg: dict = {}
        self.narrator_lines: list[str] = []      # coverage lines (no bridge endpoint for them yet)
        self.said: list[dict] = []               # what was shown: {pawn, text, intent, conv}
        self.dropped: list[tuple[int, str]] = []
        self._conv_n = 0
        self.line_count: dict[int, int] = {}      # per-pawn lines spoken (drives the tic rule)
        self._started = False

    # ---- clock ----------------------------------------------------------
    def now(self) -> Now:
        return Now(self.bridge.env.tick, self.clock())

    # ---- loading --------------------------------------------------------
    async def start(self) -> None:
        await self.bridge.health()
        await self.refresh_config()
        await self.load_colony()
        self._started = True

    async def refresh_config(self) -> None:
        self.bridge_cfg = await self.bridge.config()
        self.leases.pause_ambient_at_speed = int(
            self.bridge_cfg.get("pause_ambient_at_speed", self.cfg.pause_ambient_at_speed))

    async def colony_ids(self) -> list[int]:
        col = await self.bridge.colony()
        self.ledger.load_colony(col)
        for c in col.get("colonists", []):
            self._remember_name(c["id"], c.get("name"))
        return [int(c["id"]) for c in col.get("colonists", [])]

    def _remember_name(self, pid: int, name) -> None:
        if isinstance(name, dict):
            self.names[pid] = name.get("nick") or name.get("first") or str(pid)
            self.name_tokens[pid] = name_tokens(" ".join(str(v) for v in name.values() if v))
        elif isinstance(name, str):
            self.names[pid] = name.split()[0]
            self.name_tokens[pid] = name_tokens(name)

    async def load_colony(self) -> None:
        for pid in await self.colony_ids():
            await self.load_pawn(pid)

    async def load_pawn(self, pid: int) -> Mind | None:
        try:
            snap = await self.bridge.pawn(pid)
            blob, rev = await self.bridge.get_mind(pid)
        except BridgeError as e:
            log.warning("cannot load pawn %s: %s", pid, e)
            return None
        self._remember_name(pid, snap.get("name"))
        mind = Mind.from_blob(blob, pid, short_name(snap))
        self.revs[pid] = rev
        self.minds[pid] = mind
        self.weights[pid] = talk_weight(snap)
        if await ensure_mind_built(mind, snap, self.names, self.llm, self.cfg.use_llm_voice_cards):
            self.dirty.add(pid)
        return mind

    async def reload_minds(self) -> None:
        """save_epoch changed: drop the working minds and re-read them from /mind."""
        for t in list(self.tasks):
            t.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        self.tasks.clear()
        self.minds.clear(); self.revs.clear(); self.dirty.clear()
        self.ledger.clear()
        self.leases.leases.clear(); self.leases.queue.clear(); self.leases.last_signature.clear()
        self.poller.reset()
        self.bridge.epoch_changed = False
        if self.bridge.env.save_epoch == 0:
            return                      # main menu: nothing to load
        await self.refresh_config()
        await self.load_colony()

    # ---- main loop ------------------------------------------------------
    async def step(self) -> None:
        events = await self.poller.poll()
        if self.bridge.epoch_changed:
            await self.reload_minds()
            return
        if self.poller.last_gap:
            await self.colony_ids()      # resync the ledger from /colony
        await self.ingest(events)
        await self.take_talk()
        self.leases.expire(self.now())
        self.leases.expire_leases(self.now())
        self.launch()
        await self.save_dirty()

    async def run(self, stop: asyncio.Event | None = None) -> None:
        stop = stop or asyncio.Event()
        await self.start()
        while not stop.is_set():
            try:
                await self.step()
            except BridgeError as e:
                log.warning("bridge: %s", e)
            await asyncio.sleep(self.cfg.poll_interval_s)

    async def drain(self) -> None:
        while self.tasks:
            await asyncio.gather(*list(self.tasks), return_exceptions=True)

    # ---- events ---------------------------------------------------------
    async def ingest(self, events: list[dict]) -> None:
        for e in events:
            self.ledger.apply(e)
            for pid in e.get("witnesses", []):
                if pid not in self.minds:
                    await self.load_pawn(pid)
            written = memory.remember_event(self.minds, e, self.names)
            self.dirty.update(pid for pid, _ in written)
            self.react(e)

    def react(self, e: dict) -> None:
        etype = e.get("type", "")
        if etype in ("player_talk",):
            return                                   # handled via /talk
        if self.bridge_cfg.get("events", {}).get(etype) is False:
            return
        cls = class_of(etype)
        sev = float(e.get("severity", 0))
        floor = self.cfg.ambient_min_severity if cls <= Cls.SOCIAL else self.cfg.event_min_severity
        if sev < floor:
            return                                   # salience first: low scores never reach the model
        now = self.now()
        from .lease import REQUEST_EXPIRY
        ex = REQUEST_EXPIRY[cls]
        if ex.ticks is not None and now.tick - e.get("tick", now.tick) > ex.ticks:
            return                                   # a backlog event, too old to speak about
        ids = [int(p["id"]) for p in e.get("pawns", [])]
        roles = {int(p["id"]): p.get("role", "") for p in e.get("pawns", [])}
        cands = [i for i in ids if i in self.minds and i in e.get("witnesses", ids)]
        cands += [int(w) for w in e.get("witnesses", []) if int(w) in self.minds and int(w) not in cands]
        if etype == "social" and len([i for i in ids if i in self.minds]) >= 2:
            a, b = [i for i in ids if i in self.minds][:2]
            item = self.leases.make_item(a, "social", now, e, listener=b, payload={"conversation": True})
            self.leases.submit(item, now, self.bridge.env.speed)
            return
        n = 2 if sev >= 0.7 else 1
        picked = 0
        for pid in cands:
            if picked >= n:
                break
            if cls <= Cls.SOCIAL:
                w = min(1.0, self.weights.get(pid, 1.0) * float(self.bridge_cfg.get("talk_frequency", 1.0)) * 0.7)
                roll = int(hashlib.sha1(f"{e.get('seq')}:{pid}".encode()).hexdigest(), 16) % 1000 / 1000
                if roll >= w:
                    continue
            item = self.leases.make_item(pid, etype, now, e)
            r = self.leases.submit(item, now, self.bridge.env.speed)
            if r == "queued":
                picked += 1

    async def take_talk(self) -> None:
        try:
            reqs = await self.bridge.talk()
        except BridgeError:
            return
        for t in reqs:
            pid = int(t["pawn"])
            if pid not in self.minds:
                await self.load_pawn(pid)
            if pid not in self.minds or any(q.payload.get("talk_id") == t["id"] for q in self.leases.queue):
                continue
            running = any(l.data.get("talk_id") == t["id"] for l in self.leases.leases.values())
            if running:
                continue
            item = self.leases.make_item(pid, "player_talk", self.now(), None, cls=Cls.PLAYER,
                                         payload={"talk_id": t["id"], "text": t.get("text", ""),
                                                  "from": t.get("from", "player")})
            self.leases.submit(item, self.now(), self.bridge.env.speed)

    # ---- launching work -------------------------------------------------
    def launch(self) -> None:
        now = self.now()
        for item, lease in self.leases.pop_runnable(now):
            if item.payload.get("conversation"):
                self._spawn(self.run_conversation(item, lease))
            else:
                self._spawn(self.serve(item, lease))
        for item in list(self.leases.queue):
            if self.leases.narrator_should_cover(item, now):
                self.leases.queue.remove(item)
                self._spawn(self.narrator_cover(item))

    def _spawn(self, coro) -> None:
        t = asyncio.ensure_future(coro)
        self.tasks.add(t)
        t.add_done_callback(self.tasks.discard)
        t.add_done_callback(self._report)

    @staticmethod
    def _report(t: asyncio.Task) -> None:
        if not t.cancelled() and t.exception():
            log.error("task failed: %r", t.exception())

    # ---- building a pawn's lease prefix ---------------------------------
    async def _prepare(self, pid: int, trigger: str, event: dict | None, lease: Lease) -> tuple[SanitizeCtx, dict, str] | None:
        mind = self.minds[pid]
        snap = await self.bridge.pawn(pid)
        scene = await self.bridge.scene(pid)
        present_ids = {q["pawn"] for q in scene.get("present", []) if q.get("pawn") in self.minds}
        rel_ids = set()
        for r in snap.get("relations", []):
            p = r.get("pawn")
            if isinstance(p, int):
                rel_ids.add(p)
                mind.relations.setdefault(str(p), Relation(name=self.names.get(p, str(p)))).opinion = r.get("opinion")
        ev_ids = {int(p["id"]) for p in (event or {}).get("pawns", [])}
        known = (present_ids | rel_ids | ev_ids | {pid}) & set(self.names)
        sc = scene_from_bridge(snap, scene, trigger, self.names, known)
        mems = memory.select_memories(mind, self.now().tick, present_ids | ev_ids,
                                      trigger + " " + " ".join(sc.present))
        for m in mems:
            known |= {p for p in m.people if p in self.names}
        led = self.ledger.known_to(pid)
        prefix = build_prefix(mind, mems, [e.summary for e in led], sc)
        lease.data.update(prefix=prefix, turns=[], snap=snap)
        known_tokens = set().union(*(self.name_tokens.get(k, set()) for k in known))
        all_tokens = set().union(*self.name_tokens.values()) if self.name_tokens else set()
        ctx = SanitizeCtx(self.name_tokens.get(pid, set()), known_tokens, all_tokens, prompt_cap_words(prefix))
        sig = hashlib.sha1("|".join([trigger, sc.state, sc.where, ",".join(sc.present), sc.job]).encode()).hexdigest()
        return ctx, scene, sig

    def _listener(self, scene: dict, pid: int, prefer: int | None) -> int | None:
        if prefer is not None:
            return prefer
        for q in scene.get("present", []):
            if q.get("can_hear", True) and q.get("pawn") in self.minds and q["pawn"] != pid:
                return q["pawn"]
        return None

    # ---- a single voice -------------------------------------------------
    async def serve(self, item: WorkItem, lease: Lease) -> None:
        pid = item.pawn_id
        try:
            if item.kind == "player_talk":
                trigger = f"The player says to you: {item.payload['text']}"
                lease.data["talk_id"] = item.payload["talk_id"]
            else:
                trigger = (item.event or {}).get("summary", item.kind)
            try:
                prep = await self._prepare(pid, trigger, item.event, lease)
            except BridgeError as e:
                self.dropped.append((pid, f"bridge:{e.code}"))
                return
            ctx, scene, sig = prep
            if item.cls <= Cls.SOCIAL and self.leases.nothing_new(pid, sig):
                self.dropped.append((pid, "nothing_new"))
                return
            mind = self.minds[pid]
            listener = self._listener(scene, pid, item.listener)
            lname = self.names.get(listener) if listener is not None else None
            lease.generating = True
            res = await speak(self.llm, slot=lease.slot, mind=mind, name=self.names[pid], prefix=lease.data["prefix"],
                              turns=[], turn_no=self.line_count.get(pid, 0),
                              ctx=ctx, listener_name=lname, max_tokens=self.cfg.llm.line_max_tokens)
            lease.generating = False
            if isinstance(res, Dropped):
                self.dropped.append((pid, res.reason))
                return
            now = self.now()
            if lease.preempted:
                self.dropped.append((pid, "preempted"))
                return
            if item.expired(now):
                self.dropped.append((pid, "expired"))     # an expired line is dropped silently
                return
            ok = await self._say(pid, res, listener=listener, expires=item.expires_tick,
                                 talk_id=item.payload.get("talk_id"), combat=bool((item.event or {}).get("type") in ("injury", "raid_start")))
            if ok:
                self.leases.quotas.record(pid, listener, now)
                self.leases.mark_spoke(pid, sig)
                mind.last_line_tick = now.tick
                self.line_count[pid] = self.line_count.get(pid, 0) + 1
                self.dirty.add(pid)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("serve failed")
        finally:
            lease.generating = False
            if not lease.ended:
                self.leases.release(lease, "done")

    async def _say(self, pid: int, sp: Spoken, *, listener: int | None, expires: int | None,
                   conv: str | None = None, talk_id: str | None = None, combat: bool = False) -> bool:
        try:
            r = await self.bridge.say(pid, sp.text, listener=listener, intent=sp.intent, conversation_id=conv,
                                      combat=combat, talk_id=talk_id, expires_tick=expires)
        except BridgeError as e:
            self.dropped.append((pid, f"say:{e.code}"))
            return False
        applied = r.get("applied")
        self.said.append({"pawn": pid, "name": self.names.get(pid), "text": sp.text, "intent": sp.intent,
                          "conv": conv, "applied": applied, "refused": r.get("refused")})
        # the agent records only what the mod applied
        if applied and listener in self.minds and sp.intent in ("confide", "gossip"):
            m = memory.remember_told(self.minds[listener], pid, self.names.get(pid, "someone"),
                                     f"{self.names.get(pid)} told me: {sp.text}", self.bridge.env.tick)
            if sp.intent == "confide":
                m.visibility = "private"
            self.dirty.add(listener)
        return True

    # ---- conversations --------------------------------------------------
    async def run_conversation(self, item: WorkItem, lease_a: Lease) -> None:
        a, b = item.pawn_id, item.listener
        lease_b = None
        try:
            now = self.now()
            item_b = self.leases.make_item(b, "social", now, item.event, listener=a, cls=Cls.SOCIAL)
            lease_b = self.leases.acquire(item_b, now)
            if lease_b is None:                # only one window: fall back to a single voice
                await self.serve(item, lease_a)
                return
            self._conv_n += 1
            conv = Conversation(f"c-{self._conv_n}", a, b, {a: self.names[a], b: self.names[b]})
            summary = (item.event or {}).get("summary", "")
            leases = {a: lease_a, b: lease_b}
            ctxs = {}
            for pid, other, trig in ((a, b, f"{summary} You start talking to {self.names[b]}."),
                                     (b, a, f"{summary} {self.names[a]} is talking to you.")):
                ctxs[pid] = (await self._prepare(pid, trig, item.event, leases[pid]))[0]
            start_jobs = {pid: leases[pid].data["snap"].get("job") for pid in (a, b)}
            while not conv.ended:
                sp_id = conv.next_speaker()
                li = leases[sp_id]
                lis = conv.listener_of(sp_id)
                li.generating = True
                res = await speak(self.llm, slot=li.slot, mind=self.minds[sp_id], name=self.names[sp_id],
                                  prefix=li.data["prefix"], turns=conv.turns_for_prompt(),
                                  turn_no=len(conv.turns) // 2, ctx=ctxs[sp_id],
                                  listener_name=self.names[lis], max_tokens=self.cfg.llm.line_max_tokens)
                li.generating = False
                if isinstance(res, Dropped):
                    self.dropped.append((sp_id, res.reason))
                    conv.ended = "dropped"
                    break
                if lease_a.preempted or lease_b.preempted:
                    conv.ended = "preempted"
                    break
                if item.expired(self.now()) and not conv.turns:
                    conv.ended = "expired"
                    break
                ok = await self._say(sp_id, res, listener=lis, expires=item.expires_tick if not conv.turns else None,
                                     conv=conv.id)
                if not ok:
                    conv.ended = "apart"       # the mod's liveness check refused: dead, away or out of range
                    break
                conv.add(sp_id, res.text, res.intent)
                self.leases.quotas.record(sp_id, lis, self.now())
                self.line_count[sp_id] = self.line_count.get(sp_id, 0) + 1
                for l in (lease_a, lease_b):
                    self.leases.touch(l, self.now())
                apart = job_changed = False
                try:
                    sc = await self.bridge.scene(sp_id)
                    apart = not any(q.get("pawn") == lis and q.get("can_hear", True) for q in sc.get("present", []))
                    jobs = {pid: (await self.bridge.pawn(pid)).get("job") for pid in (a, b)}
                    if len(conv.turns) == 1:
                        start_jobs = jobs       # the mod may set a talking job on the first line
                    else:
                        job_changed = jobs != start_jobs
                except BridgeError:
                    apart = True
                conv.ended = conv.check_end(self.rng, apart=apart, job_changed=job_changed,
                                            preempted=lease_a.preempted or lease_b.preempted)
            if conv.turns:
                await self.finish_conversation(conv, leases, ctxs)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("conversation failed")
        finally:
            for l in (lease_a, lease_b):
                if l is not None:
                    l.generating = False
                    if not l.ended:
                        self.leases.release(l, "conversation_end")

    async def finish_conversation(self, conv: Conversation, leases: dict[int, Lease], ctxs: dict) -> None:
        """One memory and one relation update per pawn, written by that pawn's window (template fallback)."""
        tick = self.bridge.env.tick
        for me in (conv.a, conv.b):
            other = conv.b if me == conv.a else conv.a
            mind = self.minds[me]
            li = leases[me]
            base = [("system", li.data["prefix"]),
                    ("user", "=== TURNS SO FAR ===\n" + "\n".join(f"{n}: {t}" for n, t in conv.turns_for_prompt()))]
            mem_text = await self._ask_line(base, "=== REQUEST ===\nIn one first-person sentence, what do you remember of this talk?",
                                            li.slot, me, "memory")
            feel = await self._ask_line(base, f"=== REQUEST ===\nIn one short line, how do you feel about {self.names[other]} now?",
                                        li.slot, me, "relation")
            t_feel, t_hist = template_relation(conv, me)
            mind.memories.append(memory.Memory(
                id=mind.next_mem_id(), tick=tick, text=mem_text or template_memory(conv, me), source="saw",
                importance=0.35 + (0.2 if any(t.intent in ("insult", "confide") for t in conv.turns) else 0),
                people=[me, other], etype="conversation"))
            rel = mind.relations.setdefault(str(other), Relation(name=self.names[other]))
            rel.feeling, rel.history, rel.updated_tick = feel or t_feel, t_hist, tick
            mind.unreflected_importance += 0.3
            self.dirty.add(me)

    async def _ask_line(self, msgs, request: str, slot: int, pid: int, kind: str) -> str | None:
        try:
            m = msgs[:-1] + [("user", msgs[-1][1] + "\n" + request)]
            c = await self.llm.complete(m, slot=slot, kind=kind, max_tokens=40, stop=["\n"], pawn=pid)
        except LLMError:
            return None
        t = c.text.strip().strip('"')
        return t if 2 <= len(t.split()) <= 40 else None

    # ---- narrator -------------------------------------------------------
    async def narrator_cover(self, item: WorkItem) -> None:
        """Both windows busy and nothing preemptable: one third-person line on slot 0."""
        self.leases.narrator_busy = True
        try:
            summary = (item.event or {}).get("summary", item.kind)
            c = await self.llm.complete(
                [("system", "You narrate a RimWorld colony in dry, plain third person. One short sentence."),
                 ("user", f"What just happened: {summary}\nWrite the narrator's one line.")],
                slot=self.cfg.narrator_slot, kind="narrator", max_tokens=48, stop=["\n"])
            if not item.expired(self.now()):
                self.narrator_lines.append(c.text.strip())
        except LLMError:
            pass
        finally:
            self.leases.narrator_busy = False

    # ---- saving ---------------------------------------------------------
    async def save_dirty(self) -> None:
        for pid in sorted(self.dirty):
            mind = self.minds.get(pid)
            if mind is None:
                self.dirty.discard(pid)
                continue
            blob = mind.to_blob(self.bridge.env.tick)
            try:
                try:
                    self.revs[pid] = await self.bridge.put_mind(pid, blob, self.revs.get(pid, 0))
                except BridgeError as e:
                    if e.code != "conflict":
                        raise
                    _, self.revs[pid] = await self.bridge.get_mind(pid)   # stale rev: take the mod's, retry once
                    self.revs[pid] = await self.bridge.put_mind(pid, blob, self.revs[pid])
                self.dirty.discard(pid)
            except BridgeError as e:
                log.warning("mind save failed for %s: %s", pid, e)
                if e.code in ("too_large",):
                    self.dirty.discard(pid)

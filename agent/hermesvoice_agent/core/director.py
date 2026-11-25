"""The director: turns events into leased, grounded, short lines.

One ``step()`` polls events, writes witness-only memories, submits work to the lease manager,
starts what the windows allow, and saves dirty minds. Work runs as asyncio tasks; ``drain()``
waits for them (tests and replay use it). Everything game-specific goes through the adapter;
the transport is any ``Bridge`` and events come from any ``EventSource``.
"""
from __future__ import annotations

import asyncio
import hashlib
import logging
import random
import time
from typing import Callable

from . import memory
from .adapter import GameAdapter
from .bridge import Bridge, BridgeError, EventSource
from .builders import ensure_mind_built
from .config import Config
from .conversation import Conversation, template_memory, template_relation
from .ledger import Ledger
from .lease import REQUEST_EXPIRY, Cls, Lease, LeaseManager, Now, QuotaTracker, WorkItem
from .llm import LLMClient, LLMError
from .minds import Mind, Relation
from .passes import Dropped, SanitizeCtx, Spoken, speak
from .prompt import build_prefix
from .sanitize import prompt_cap_words

log = logging.getLogger("hermesvoice")


class Director:
    def __init__(self, cfg: Config, bridge: Bridge, llm: LLMClient, adapter: GameAdapter,
                 events: EventSource | None = None,
                 clock: Callable[[], float] = time.monotonic, rng: random.Random | None = None):
        from .bridge import EventPoller
        self.cfg, self.bridge, self.llm, self.adapter, self.clock = cfg, bridge, llm, adapter, clock
        self.rng = rng or random.Random(7)
        self.events: EventSource = events or EventPoller(bridge, normalize=adapter.normalize_event)
        self.day = adapter.units_per_day
        self.minds: dict[int, Mind] = {}
        self.revs: dict[int, int] = {}
        self.dirty: set[int] = set()
        self.names: dict[int, str] = {}          # short names, for every actor seen
        self.name_tokens: dict[int, set[str]] = {}
        self.weights: dict[int, float] = {}
        self.ledger = Ledger(adapter.is_ledger_end)
        q = cfg.quotas
        self.leases = LeaseManager(cfg.actor_slots, cfg.narrator_slot,
                                   QuotaTracker(q.actor_per_day, q.pair_per_day, self.day,
                                                q.min_gap_game_ambient, q.min_gap_real_s_ambient),
                                   cfg.pause_ambient_at_speed, adapter.priority)
        self.tasks: set[asyncio.Task] = set()
        self.bridge_cfg: dict = {}
        self.narrator_lines: list[str] = []      # coverage lines
        self.said: list[dict] = []               # what was shown
        self.dropped: list[tuple[int, str]] = []
        self.line_count: dict[int, int] = {}     # per-actor lines spoken (drives the habit rule)
        self._conv_n = 0
        self._started = False

    # ---- clock ----------------------------------------------------------
    def now(self) -> Now:
        return Now(self.bridge.env.game, self.clock())

    # ---- loading --------------------------------------------------------
    async def start(self) -> None:
        await self.bridge.health()
        await self.refresh_config()
        await self.load_world()
        self._started = True

    async def refresh_config(self) -> None:
        self.bridge_cfg = await self.bridge.config()
        self.leases.pause_ambient_at_speed = int(
            self.bridge_cfg.get("pause_ambient_at_speed", self.cfg.pause_ambient_at_speed))

    async def roster_ids(self) -> list[int]:
        resp = await self.bridge.roster()
        self.ledger.load_world(self.adapter.roster_ledger(resp))
        ids = []
        for aid, raw in self.adapter.roster(resp):
            self._remember_name(aid, raw)
            ids.append(aid)
        return ids

    def _remember_name(self, aid: int, raw) -> None:
        ni = self.adapter.name_info(raw)
        if ni.short:
            self.names[aid] = ni.short
            self.name_tokens[aid] = set(ni.tokens)

    async def load_world(self) -> None:
        for aid in await self.roster_ids():
            if not self.adapter.is_controlled(aid):
                await self.load_actor(aid)

    async def load_actor(self, aid: int) -> Mind | None:
        try:
            snap = await self.bridge.actor(aid)
            blob, rev = await self.bridge.get_mind(aid)
        except BridgeError as e:
            log.warning("cannot load actor %s: %s", aid, e)
            return None
        self._remember_name(aid, self.adapter.snapshot_name(snap))
        mind = Mind.from_blob(blob, aid, self.names.get(aid, ""))
        self.revs[aid] = rev
        self.minds[aid] = mind
        self.weights[aid] = self.adapter.talk_weight(snap)
        if await ensure_mind_built(mind, snap, self.names, self.adapter, self.llm, self.cfg.use_llm_voice_cards):
            self.dirty.add(aid)
        return mind

    async def reload_minds(self) -> None:
        """The world was reloaded (epoch changed): drop the working minds and re-read them."""
        for t in list(self.tasks):
            t.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        self.tasks.clear()
        self.minds.clear(); self.revs.clear(); self.dirty.clear()
        self.ledger.clear()
        self.leases.leases.clear(); self.leases.queue.clear(); self.leases.last_signature.clear()
        self.events.reset()
        self.bridge.epoch_changed = False
        if self.bridge.env.epoch == 0:
            return                      # no world loaded: nothing to read
        await self.refresh_config()
        await self.load_world()

    # ---- main loop ------------------------------------------------------
    async def step(self) -> None:
        events = await self.events.poll()
        if self.bridge.epoch_changed:
            await self.reload_minds()
            return
        if self.events.last_gap:
            await self.roster_ids()      # events were lost: resync the ledger from the roster
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
            self.ledger.apply(e, [int(a["id"]) for a in e.get("actors", [])])
            for aid in e.get("witnesses", []):
                if aid not in self.minds and not self.adapter.is_controlled(aid):
                    await self.load_actor(aid)
            written = memory.remember_event(self.minds, e, self.adapter)
            self.dirty.update(aid for aid, _ in written)
            self.react(e)

    def react(self, e: dict) -> None:
        etype = e.get("type", "")
        cls = self.adapter.priority(etype)
        if cls == Cls.PLAYER:
            return                                   # player talk arrives through take_talk
        if self.bridge_cfg.get("events", {}).get(etype) is False:
            return
        sev = float(e.get("severity", 0))
        floor = self.cfg.ambient_min_severity if cls <= Cls.SOCIAL else self.cfg.event_min_severity
        if sev < floor:
            return                                   # salience first: low scores never reach the model
        now = self.now()
        ex = REQUEST_EXPIRY[cls]
        if ex.units is not None and now.game - e.get("game", now.game) > ex.units:
            return                                   # a backlog event, too old to speak about
        roles = [(int(a["id"]), a.get("role", "")) for a in e.get("actors", [])]
        ids = [i for i, _ in roles]
        witnesses = [int(w) for w in e.get("witnesses", ids)]
        pair = self.adapter.conversation_pair(e)
        if pair and pair[0] in self.minds and pair[1] in self.minds:
            item = self.leases.make_item(pair[0], etype, now, e, listener=pair[1], cls=Cls.SOCIAL,
                                         payload={"conversation": True})
            self.leases.submit(item, now, self.bridge.env.speed)
            return
        cands = [i for i in ids if i in self.minds and i in witnesses]
        cands += [w for w in witnesses if w in self.minds and w not in cands]
        n = 2 if sev >= 0.7 else 1
        picked = 0
        for aid in cands:
            if picked >= n:
                break
            if cls <= Cls.SOCIAL:
                w = min(1.0, self.weights.get(aid, 1.0) * float(self.bridge_cfg.get("talk_frequency", 1.0)) * 0.7)
                roll = int(hashlib.sha1(f"{e.get('seq')}:{aid}".encode()).hexdigest(), 16) % 1000 / 1000
                if roll >= w:
                    continue
            item = self.leases.make_item(aid, etype, now, e)
            if self.leases.submit(item, now, self.bridge.env.speed) == "queued":
                picked += 1

    async def take_talk(self) -> None:
        field = self.adapter.protocol.actor_field
        try:
            reqs = await self.bridge.talk()
        except BridgeError:
            return
        for t in reqs:
            aid = int(t[field])
            if aid not in self.minds and not self.adapter.is_controlled(aid):
                await self.load_actor(aid)
            if aid not in self.minds or any(q.payload.get("talk_id") == t["id"] for q in self.leases.queue):
                continue
            if any(l.data.get("talk_id") == t["id"] for l in self.leases.leases.values()):
                continue
            item = self.leases.make_item(aid, "player_talk", self.now(), None, cls=Cls.PLAYER,
                                         payload={"talk_id": t["id"], "text": t.get("text", ""),
                                                  "from": t.get("from", "player")})
            self.leases.submit(item, self.now(), self.bridge.env.speed)

    # ---- launching work -------------------------------------------------
    def launch(self) -> None:
        now = self.now()
        for item, lease in self.leases.pop_runnable(now):
            self._spawn(self.run_conversation(item, lease) if item.payload.get("conversation")
                        else self.serve(item, lease))
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

    # ---- building an actor's lease prefix -------------------------------
    async def _prepare(self, aid: int, trigger: str, event: dict | None, lease: Lease):
        mind = self.minds[aid]
        snap = await self.bridge.actor(aid)
        scene = await self.bridge.scene(aid)
        present_ids = {i for i, _ in self.adapter.scene_present(scene) if i in self.minds}
        rel_ids = set()
        for other, opinion in self.adapter.relations(snap):
            rel_ids.add(other)
            mind.relations.setdefault(str(other), Relation(name=self.names.get(other, str(other)))).opinion = opinion
        ev_ids = {int(a["id"]) for a in (event or {}).get("actors", [])}
        known = (present_ids | rel_ids | ev_ids | {aid}) & set(self.names)
        sc = self.adapter.build_scene(snap, scene, trigger, self.names, known)
        mems = memory.select_memories(mind, self.now().game, present_ids | ev_ids,
                                      trigger + " " + " ".join(sc.present), self.day)
        for m in mems:
            known |= {p for p in m.people if p in self.names}
        led = self.ledger.known_to(aid)
        prefix = build_prefix(self.adapter.block_a, mind, mems, [e.summary for e in led], sc)
        lease.data.update(prefix=prefix, turns=[], snap=snap)
        known_tokens = set().union(*(self.name_tokens.get(k, set()) for k in known))
        all_tokens = set().union(*self.name_tokens.values()) if self.name_tokens else set()
        ctx = SanitizeCtx(self.name_tokens.get(aid, set()), known_tokens, all_tokens,
                          prompt_cap_words(prefix), set(self.adapter.common_words))
        sig = hashlib.sha1("|".join([trigger, sc.state, sc.where, ",".join(sc.present), sc.job]).encode()).hexdigest()
        return ctx, scene, sig

    def _listener(self, scene: dict, aid: int, prefer: int | None) -> int | None:
        if prefer is not None:
            return prefer
        for i, can_hear in self.adapter.scene_present(scene):
            if can_hear and i in self.minds and i != aid:
                return i
        return None

    # ---- a single voice -------------------------------------------------
    async def serve(self, item: WorkItem, lease: Lease) -> None:
        aid = item.actor_id
        try:
            if item.kind == "player_talk":
                trigger = f"The player says to you: {item.payload['text']}"
                lease.data["talk_id"] = item.payload["talk_id"]
            else:
                trigger = (item.event or {}).get("summary", item.kind)
            try:
                ctx, scene, sig = await self._prepare(aid, trigger, item.event, lease)
            except BridgeError as e:
                self.dropped.append((aid, f"bridge:{e.code}"))
                return
            if item.cls < Cls.URGENT and self.leases.nothing_new(aid, sig):
                self.dropped.append((aid, "nothing_new"))
                return
            mind = self.minds[aid]
            listener = self._listener(scene, aid, item.listener)
            lname = self.names.get(listener) if listener is not None else None
            lease.generating = True
            res = await speak(self.llm, slot=lease.slot, mind=mind, name=self.names[aid], prefix=lease.data["prefix"],
                              turns=[], turn_no=self.line_count.get(aid, 0), ctx=ctx, listener_name=lname,
                              labels=self.adapter.intent_labels, max_tokens=self.cfg.llm.line_max_tokens)
            lease.generating = False
            if isinstance(res, Dropped):
                self.dropped.append((aid, res.reason))
                return
            now = self.now()
            if lease.preempted:
                self.dropped.append((aid, "preempted"))
                return
            if item.expired(now):
                self.dropped.append((aid, "expired"))     # an expired line is dropped silently
                return
            ok = await self._say(aid, res, listener=listener, expires=item.expires_at,
                                 talk_id=item.payload.get("talk_id"),
                                 combat=bool(item.event and self.adapter.is_urgent_visual(item.event)))
            if ok:
                self.leases.quotas.record(aid, listener, now)
                self.leases.mark_spoke(aid, sig)
                mind.last_line_time = now.game
                self.line_count[aid] = self.line_count.get(aid, 0) + 1
                self.dirty.add(aid)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("serve failed")
        finally:
            lease.generating = False
            if not lease.ended:
                self.leases.release(lease, "done")

    async def _say(self, aid: int, sp: Spoken, *, listener: int | None, expires: int | None,
                   conv: str | None = None, talk_id: str | None = None, combat: bool = False) -> bool:
        try:
            r = await self.bridge.say(aid, sp.text, listener=listener, intent=sp.intent, conversation_id=conv,
                                      combat=combat, talk_id=talk_id, expires_at=expires)
        except BridgeError as e:
            self.dropped.append((aid, f"say:{e.code}"))
            return False
        applied = r.get("applied")
        self.said.append({"actor": aid, "name": self.names.get(aid), "text": sp.text, "intent": sp.intent,
                          "conv": conv, "applied": applied, "refused": r.get("refused")})
        told = self.adapter.told_intents()
        # the agent records only what the game applied
        if applied and listener in self.minds and sp.intent in told:
            m = memory.remember_told(self.minds[listener], aid, self.names.get(aid, "someone"),
                                     f"{self.names.get(aid)} told me: {sp.text}", self.bridge.env.game)
            m.visibility = told[sp.intent]
            self.dirty.add(listener)
        return True

    # ---- conversations --------------------------------------------------
    async def run_conversation(self, item: WorkItem, lease_a: Lease) -> None:
        a, b = item.actor_id, item.listener
        lease_b = None
        try:
            now = self.now()
            item_b = self.leases.make_item(b, item.kind, now, item.event, listener=a, cls=Cls.SOCIAL)
            lease_b = self.leases.acquire(item_b, now)
            if lease_b is None:                # only one window: fall back to a single voice
                await self.serve(item, lease_a)
                return
            self._conv_n += 1
            conv = Conversation(f"c-{self._conv_n}", a, b, {a: self.names[a], b: self.names[b]})
            summary = (item.event or {}).get("summary", "")
            leases = {a: lease_a, b: lease_b}
            ctxs = {}
            for aid, trig in ((a, f"{summary} You start talking to {self.names[b]}."),
                              (b, f"{summary} {self.names[a]} is talking to you.")):
                ctxs[aid] = (await self._prepare(aid, trig, item.event, leases[aid]))[0]
            start_jobs = {aid: self.adapter.snapshot_job(leases[aid].data["snap"]) for aid in (a, b)}
            while not conv.ended:
                sp_id = conv.next_speaker()
                li = leases[sp_id]
                lis = conv.listener_of(sp_id)
                li.generating = True
                res = await speak(self.llm, slot=li.slot, mind=self.minds[sp_id], name=self.names[sp_id],
                                  prefix=li.data["prefix"], turns=conv.turns_for_prompt(),
                                  turn_no=len(conv.turns) // 2, ctx=ctxs[sp_id],
                                  listener_name=self.names[lis], labels=self.adapter.intent_labels,
                                  max_tokens=self.cfg.llm.line_max_tokens)
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
                ok = await self._say(sp_id, res, listener=lis, expires=item.expires_at if not conv.turns else None,
                                     conv=conv.id)
                if not ok:
                    conv.ended = "apart"       # the game's liveness check refused: gone or out of range
                    break
                conv.add(sp_id, res.text, res.intent)
                self.leases.quotas.record(sp_id, lis, self.now())
                self.line_count[sp_id] = self.line_count.get(sp_id, 0) + 1
                for l in (lease_a, lease_b):
                    self.leases.touch(l, self.now())
                apart = job_changed = False
                try:
                    sc = await self.bridge.scene(sp_id)
                    apart = not any(i == lis and ch for i, ch in self.adapter.scene_present(sc))
                    jobs = {aid: self.adapter.snapshot_job(await self.bridge.actor(aid)) for aid in (a, b)}
                    if len(conv.turns) == 1:
                        start_jobs = jobs       # the game may set a talking job on the first line
                    else:
                        job_changed = jobs != start_jobs
                except BridgeError:
                    apart = True
                conv.ended = conv.check_end(self.rng, apart=apart, job_changed=job_changed,
                                            preempted=lease_a.preempted or lease_b.preempted)
            if conv.turns:
                await self.finish_conversation(conv, leases)
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

    async def finish_conversation(self, conv: Conversation, leases: dict[int, Lease]) -> None:
        """One memory and one relation update per actor, written by that actor's window (template fallback)."""
        now = self.bridge.env.game
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
                id=mind.next_mem_id(), game=now, text=mem_text or template_memory(conv, me), source="saw",
                importance=0.35 + (0.2 if any(t.intent in self.adapter.told_intents() for t in conv.turns) else 0),
                people=[me, other], etype="conversation"))
            rel = mind.relations.setdefault(str(other), Relation(name=self.names[other]))
            rel.feeling, rel.history, rel.updated_time = feel or t_feel, t_hist, now
            mind.unreflected_importance += 0.3
            self.dirty.add(me)

    async def _ask_line(self, msgs, request: str, slot: int, aid: int, kind: str) -> str | None:
        try:
            m = msgs[:-1] + [("user", msgs[-1][1] + "\n" + request)]
            c = await self.llm.complete(m, slot=slot, kind=kind, max_tokens=40, stop=["\n"], actor=aid)
        except LLMError:
            return None
        t = c.text.strip().strip('"')
        return t if 2 <= len(t.split()) <= 40 else None

    # ---- narrator -------------------------------------------------------
    async def narrator_cover(self, item: WorkItem) -> None:
        """All windows busy and nothing preemptable: one third-person line on the narrator slot."""
        self.leases.narrator_busy = True
        try:
            summary = (item.event or {}).get("summary", item.kind)
            c = await self.llm.complete(
                [("system", self.adapter.narrator_prompt),
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
        for aid in sorted(self.dirty):
            mind = self.minds.get(aid)
            if mind is None:
                self.dirty.discard(aid)
                continue
            blob = mind.to_blob(self.bridge.env.game, self.day)
            try:
                try:
                    self.revs[aid] = await self.bridge.put_mind(aid, blob, self.revs.get(aid, 0))
                except BridgeError as e:
                    if e.code != "conflict":
                        raise
                    _, self.revs[aid] = await self.bridge.get_mind(aid)   # stale rev: take the game's, retry once
                    self.revs[aid] = await self.bridge.put_mind(aid, blob, self.revs[aid])
                self.dirty.discard(aid)
            except BridgeError as e:
                log.warning("mind save failed for %s: %s", aid, e)
                if e.code in ("too_large",):
                    self.dirty.discard(aid)

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

from . import agegate, memory
from .adapter import GameAdapter
from .bridge import Bridge, BridgeError, EventSource
from .builders import ensure_mind_built
from .chronicle import Chronicle
from .config import Config
from .conversation import Conversation, template_memory, template_relation
from .idle import IdleRunner
from .ledger import Ledger
from .agegate import GateStats
from .wants import WantsStats
from .loopguard import LoopStats, find_repeat, retry_request
from .lease import REQUEST_EXPIRY, Cls, Lease, LeaseManager, Now, QuotaTracker, VoiceBudget, WorkItem
from .llm import LLMClient, LLMError
from .minds import Mind, Relation
from .passes import Dropped, SanitizeCtx, Spoken, line_pass, speak
from .prompt import build_prefix
from .sanitize import prompt_cap_words

log = logging.getLogger("hermesvoice")


def merge_player_edits(mind: Mind, theirs: dict | None) -> bool:
    """After a PUT /mind conflict: the player's edits in the game's blob win over ours (director's notes
    and notes_rev, an edited voice card); every other field stays ours. Returns True if anything was taken."""
    if not theirs:
        return False
    took = False
    if theirs.get("notes_edited_by_player"):
        mind.notes = theirs.get("notes", mind.notes)
        mind.notes_rev = int(theirs.get("notes_rev", mind.notes_rev))
        mind.notes_edited_by_player = True
        took = True
    v = theirs.get("voice") or {}
    if v.get("edited_by_player"):
        try:
            mind.voice = type(mind.voice).model_validate(v)
            took = True
        except Exception:
            pass
    return took


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
                                                q.min_gap_game_ambient, q.min_gap_real_s_ambient, q.ambient_share),
                                   cfg.pause_ambient_at_speed, adapter.priority,
                                   VoiceBudget(cfg.voice_lines_per_min, cfg.voice_burst, cfg.voice_fast_scale))
        self.tasks: set[asyncio.Task] = set()
        self.bridge_cfg: dict = {}
        self.narrator_lines: list[str] = []      # coverage lines
        self.said: list[dict] = []               # what was shown
        self.shout_until: dict[int, float] = {}  # per-actor real time before which another shout is said normally
        self.dropped: list[tuple[int, str]] = []
        self.line_count: dict[int, int] = {}     # per-actor lines spoken (drives the habit rule)
        self._conv_n = 0
        self.loop = LoopStats()                  # repeat guard counters and trace (core/loopguard.py)
        self.ages: dict[int, float | None] = {}      # biological age per actor; missing or None counts as a minor
        self.tool_backend = None                 # a toolloop Backend; wants with follow-through is off while None
        self.tool_mode = "auto"                  # toolloop protocol: auto | grammar | json | native
        self.wants = WantsStats()                # core/wants.py
        self.wants_last: dict[int, int] = {}     # game time of each actor's last wants decision
        self.gate = GateStats()                  # age gate counters and trace (core/agegate.py)
        self.recent_own: dict[int, list[str]] = {}   # each speaker's last lines, across conversations
        self._started = False
        self.chronicle = Chronicle(cfg.data_dir, self.day)
        self.idle = IdleRunner(self)
        self._upkeep_day = -1
        self._last_cover: Now | None = None
        self._raw_last: float | None = None
        self._play = 0.0                          # play clock: real seconds with the game's pauses taken out
        self._speed_seen = 1

    # ---- clock ----------------------------------------------------------
    def now(self) -> Now:
        """Both clocks. ``real`` is the play clock (it stands still while the game is paused, so nothing expires
        in a pause), ``rate`` the game units per real second at the speed the game last reported."""
        raw = self.clock()
        env = self.bridge.env
        if self._raw_last is not None and raw > self._raw_last and not env.paused:
            self._play += raw - self._raw_last
        self._raw_last = raw if self._raw_last is None else max(self._raw_last, raw)
        if env.speed >= 1:
            self._speed_seen = env.speed
        return Now(env.game, self._play, float(self.adapter.units_per_second(self._speed_seen)))

    def poll_delay(self) -> float:
        """Real seconds between polls: shorter as the game speeds up, so a poll never spans more game time than at 1x
        (much) and the event ring is drained often."""
        return self.cfg.poll_interval_s / max(1.0, (self.now().rate / 60.0) ** 0.5)

    def wire_expiry(self, item: WorkItem) -> int | None:
        """expires_tick for the mod: the item's deadline at the current speed, plus half a second of slack for the
        request itself, so the mod only refuses what is truly late."""
        now = self.now()
        dl = item.deadline(now.rate)
        return None if dl is None else dl + int(0.5 * now.rate)

    # ---- age gate (core/agegate.py) ---------------------------------------
    def is_minor(self, aid: int | None) -> bool:
        """Under 18; an actor whose age was never read counts as a minor."""
        return aid is not None and agegate.is_minor(self.ages.get(aid))

    def minor_ids(self) -> set[int]:
        return {i for i in self.names if self.is_minor(i)}

    def minor_tokens(self) -> set[str]:
        return set().union(*(self.name_tokens.get(i, set()) for i in self.minor_ids())) if self.names else set()

    def _romantic(self) -> frozenset[str]:
        return getattr(self.adapter, "romantic_intents", frozenset())

    def _learn_age(self, aid: int, snap: dict) -> None:
        fn = getattr(self.adapter, "snapshot_age", None)
        self.ages[aid] = fn(snap) if fn else None

    async def _learn_ages(self, ids) -> None:
        for i in ids:
            if i in self.names and i not in self.ages:
                try:
                    self._learn_age(i, await self.bridge.actor(i))
                except BridgeError:
                    self.ages[i] = None

    def labels_for(self, minor: bool) -> list[str]:
        return agegate.labels_for(self.adapter.intent_labels, self._romantic(), minor)

    def tools_allowed(self, names, minor: bool) -> set[str]:
        """The tool-loop allowed set for a scene: romance-type tools are removed when a minor is involved."""
        return agegate.allowed_tools(names, self._romantic(), minor)

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
        self._learn_age(aid, snap)
        mind = Mind.from_blob(blob, aid, self.names.get(aid, ""))
        self.revs[aid] = rev
        self.minds[aid] = mind
        self.weights[aid] = self.adapter.talk_weight(snap)
        if not blob:
            mind.last_reflect_time = self.bridge.env.game      # the interval starts when we first meet them
        if await ensure_mind_built(mind, snap, self.names, self.adapter, self.llm, self.cfg.use_llm_voice_cards):
            self.dirty.add(aid)
        return mind

    async def reload_minds(self) -> None:
        """The world was reloaded (epoch changed): drop the working minds and re-read them."""
        for t in list(self.tasks):
            t.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        self.tasks.clear()
        self.idle.preempt("reload")
        self.minds.clear(); self.revs.clear(); self.dirty.clear()
        self._upkeep_day = -1
        self.ledger.clear()
        self.leases.leases.clear(); self.leases.queue.clear(); self.leases.last_signature.clear()
        self.events.reset()
        self.bridge.epoch_changed = False
        if self.bridge.env.epoch == 0:
            return                      # no world loaded: nothing to read
        await self.refresh_config()
        await self.load_world()
        self.chronicle.rollback(self.bridge.env.game)      # the chronicle follows the save back in time

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
        self.idle.preempt_if_wanted()                # idle work yields to actor windows at once
        self.daily_upkeep()
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
            await asyncio.sleep(self.poll_delay())

    async def drain(self) -> None:
        while self.tasks:
            await asyncio.wait(list(self.tasks))
            self.tasks.difference_update([t for t in self.tasks if t.done()])   # cancelled idle jobs finish too
            await asyncio.sleep(0)

    # ---- events ---------------------------------------------------------
    async def ingest(self, events: list[dict]) -> None:
        for e in events:
            self.ledger.apply(e, [int(a["id"]) for a in e.get("actors", [])])
            for aid in e.get("witnesses", []):
                if aid not in self.minds and not self.adapter.is_controlled(aid):
                    await self.load_actor(aid)
            written = memory.remember_event(self.minds, e, self.adapter)
            self.dirty.update(aid for aid, _ in written)
            if self.adapter.chronicle_worthy(e):
                self.chronicle.note(e.get("game", self.bridge.env.game), e.get("summary", ""),
                                    float(e.get("severity", 0.3)), self.adapter.chronicle_trigger(e))
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
        vanilla = getattr(self.adapter, "vanilla_social", lambda ev: None)(e)
        units, floor = (ex.units, ex.floor_real_s)
        if vanilla:                                  # the line must attach to the game's own entry in time
            units, floor = min(units, vanilla["window_ticks"]), max(floor, self.cfg.vanilla_attach_floor_s, float(vanilla.get("window_real_s", 0.0)))
        age = max(0, now.game - int(e.get("game", now.game)))
        if units is not None and age > max(units, int(floor * now.rate)):
            return                                   # a backlog event, too old to speak about
        # the moment's clock starts when it happened, not when we heard of it
        born = Now(now.game - age, now.real - age / max(now.rate, 1.0), now.rate)
        roles = [(int(a["id"]), a.get("role", "")) for a in e.get("actors", [])]
        ids = [i for i, _ in roles]
        witnesses = [int(w) for w in e.get("witnesses", ids)]
        if vanilla and vanilla.get("romantic") and (self.is_minor(vanilla["initiator"]) or self.is_minor(vanilla["recipient"])):
            self.gate.skipped_events += 1             # no words, no memory of words, for a romantic move with a minor
            self.gate.note("vanilla_romance", ref=vanilla["ref"], initiator=vanilla["initiator"], recipient=vanilla["recipient"])
            return
        if vanilla and vanilla["initiator"] in self.minds and vanilla["recipient"] in self.minds:
            # The game's own interaction: one line for it, in its priority tier (chitchat only when idle).
            vcls = Cls.SOCIAL if vanilla["tier"] == "high" else Cls.AMBIENT
            item = self.leases.make_item(vanilla["initiator"], "vanilla_social", born, e, listener=vanilla["recipient"],
                                         cls=vcls, payload={"vanilla": vanilla}, units=units, floor_real_s=floor)
            self.leases.submit(item, now, self.bridge.env.speed)
            return
        pair = self.adapter.conversation_pair(e)
        if pair and pair[0] in self.minds and pair[1] in self.minds:
            item = self.leases.make_item(pair[0], etype, born, e, listener=pair[1], cls=Cls.SOCIAL,
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
            item = self.leases.make_item(aid, etype, born, e)
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
            if self.leases.narrator_should_cover(item, now) and self._cover_allowed(item, now):
                self.leases.queue.remove(item)
                self._last_cover = now
                self._spawn(self.narrator_cover(item))
        self.idle.run_once()

    def _cover_allowed(self, item: WorkItem, now: Now) -> bool:
        """The narrator speaks rarely: a minimum gap on both clocks, bypassed by deaths and major threats."""
        if item.event and self.adapter.is_major_threat(item.event):
            return True
        last = self._last_cover
        if last is None:
            return True
        return (now.game - last.game >= self.cfg.narrator_min_gap_game
                or now.real - last.real >= self.cfg.narrator_min_gap_real_s)

    def _spawn(self, coro) -> asyncio.Task:
        t = asyncio.ensure_future(coro)
        self.tasks.add(t)
        t.add_done_callback(self.tasks.discard)
        t.add_done_callback(self._report)
        return t

    def daily_upkeep(self) -> None:
        """Once per game day: merge similar old memories and drop decayed ones, for every mind."""
        today = self.bridge.env.game // self.day
        if today == self._upkeep_day:
            return
        self._upkeep_day = today
        for aid, mind in self.minds.items():
            r = memory.upkeep(mind, self.bridge.env.game, self.day, self.cfg.memory_floor,
                              self.cfg.memory_merge_similarity)
            if r["merged"] or r["dropped"]:
                self.dirty.add(aid)

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
        self._learn_age(aid, snap)
        await self._learn_ages(self.names)
        minors = self.minor_ids()
        hearers = {i for i, can_hear in self.adapter.scene_present(scene) if can_hear and i != aid}
        sc = self.adapter.build_scene(snap, scene, trigger, self.names, known, minors)
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
                          prompt_cap_words(prefix), set(self.adapter.common_words),
                          third_person=mind.voice.speaks_third_person(self.name_tokens.get(aid, set())),
                          minor_involved=self.is_minor(aid) or bool(hearers & minors), minor_names=self.minor_tokens())
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
            vanilla = item.payload.get("vanilla")
            if item.kind == "player_talk":
                trigger = f"The player says to you: {item.payload['text']}"
                lease.data["talk_id"] = item.payload["talk_id"]
            elif vanilla:
                trigger = vanilla["sentence"] or (item.event or {}).get("summary", item.kind)
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
            ctx.secrets = memory.secret_texts(mind, lname)
            ctx.minor_involved = ctx.minor_involved or self.is_minor(listener)
            minor = ctx.minor_involved
            if item.kind == "player_talk" and agegate.violation(item.payload["text"], involves_minor=minor,
                                                                minor_names=ctx.minor_names):
                # a romantic or sexual message to or about a minor never reaches the model
                self.gate.skipped_player_text += 1
                self.gate.note("player_text", actor=aid)
                self.dropped.append((aid, "minor_safety"))
                return
            lease.generating = True
            if vanilla:
                # Words for the game's own interaction: no intent pass, the game applies its own effects.
                s, _ = await line_pass(self.llm, slot=lease.slot, mind=mind, name=self.names[aid], prefix=lease.data["prefix"],
                                       turns=[], turn_no=self.line_count.get(aid, 0), ctx=ctx,
                                       max_tokens=self.cfg.llm.line_max_tokens,
                                       request=self.adapter.vanilla_request(vanilla, self.names[aid], lname or "them"))
                res = Spoken(s.text, self.adapter.intent_labels[0]) if s.ok else Dropped(s.reason)
            else:
                res = await speak(self.llm, slot=lease.slot, mind=mind, name=self.names[aid], prefix=lease.data["prefix"],
                                  turns=[], turn_no=self.line_count.get(aid, 0), ctx=ctx, listener_name=lname,
                                  labels=self.labels_for(minor), max_tokens=self.cfg.llm.line_max_tokens)
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
            ok = await self._say(aid, res, listener=listener, expires=self.wire_expiry(item),
                                 talk_id=item.payload.get("talk_id"),
                                 combat=bool(item.event and self.adapter.is_urgent_visual(item.event)) and not vanilla,
                                 event=item.event, vanilla_ref=vanilla["ref"] if vanilla else None, minor=minor)
            if ok:
                self.leases.quotas.record(aid, listener, now, pair=not vanilla)
                self.leases.mark_spoke(aid, sig)
                mind.last_line_time = now.game
                self.line_count[aid] = self.line_count.get(aid, 0) + 1
                self._remember_own(aid, res.text)
                self.dirty.add(aid)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("serve failed")
        finally:
            lease.generating = False
            if not lease.ended:
                self.leases.release(lease, "done")

    def _pick_about(self, aid: int, listener: int | None, event: dict | None) -> int | None:
        """The third actor a gossip line is about: one in the event, else the one the speaker feels most about."""
        skip = {aid, listener}
        for a in (event or {}).get("actors", []):
            if int(a["id"]) not in skip and int(a["id"]) in self.names:
                return int(a["id"])
        best, score = None, -1.0
        for k, r in self.minds[aid].relations.items():
            if k.lstrip("-").isdigit() and int(k) not in skip and int(k) in self.names:
                s = abs(r.opinion or 0)
                if s > score:
                    best, score = int(k), s
        return best

    def _delivery(self, aid: int, sp: Spoken, combat: bool, event: dict | None) -> str | None:
        """The delivery the intent pass chose, bounded: a shout needs the scene (combat, an insult, a major threat)
        and a pause since this speaker's last shout; otherwise it is said normally. None = let the mod guess."""
        if sp.delivery not in ("normal", "shout", "whisper"):
            return None
        if sp.delivery != "shout":
            return sp.delivery
        loud = combat or sp.intent == "insult" or bool(event and self.adapter.is_major_threat(event))
        now = self.clock()
        if not loud or now < self.shout_until.get(aid, -1.0):
            return "normal"
        self.shout_until[aid] = now + self.cfg.shout_gap_real_s
        return "shout"

    async def _say(self, aid: int, sp: Spoken, *, listener: int | None, expires: int | None,
                   conv: str | None = None, talk_id: str | None = None, combat: bool = False,
                   event: dict | None = None, vanilla_ref: str | None = None, minor: bool = False) -> bool:
        if agegate.refuse_intent(sp.intent, self._romantic(), minor or self.is_minor(aid) or self.is_minor(listener)):
            self.gate.refused_intents += 1                       # last guard before the write
            self.gate.note("intent", actor=aid, intent=sp.intent)
            sp = Spoken(sp.text, self.adapter.intent_labels[0])
        about = None
        if sp.intent in self.adapter.about_intents():
            about = self._pick_about(aid, listener, event)
            if about is None or listener is None:
                sp = Spoken(sp.text, self.adapter.intent_labels[0], sp.delivery)   # nobody to gossip about
        kind = self._delivery(aid, sp, combat, event)
        try:
            r = await self.bridge.say(aid, sp.text, listener=listener, intent=sp.intent, conversation_id=conv,
                                      combat=combat, talk_id=talk_id, expires_at=expires, about=about,
                                      vanilla_ref=vanilla_ref, kind=kind)
        except BridgeError as e:
            self.dropped.append((aid, f"say:{e.code}"))
            return False
        applied = r.get("applied")
        self.leases.budget.spend(self.now())
        self.said.append({"actor": aid, "name": self.names.get(aid), "text": sp.text, "intent": sp.intent, "delivery": kind,
                          "conv": conv, "applied": applied, "refused": r.get("refused")})
        told = self.adapter.told_intents()
        # the agent records only what the game applied
        if applied and listener in self.minds and sp.intent in told:
            if memory.record_told_line(self.minds, aid, listener, self.names, sp.text, self.bridge.env.game,
                                       told[sp.intent], about):
                self.dirty.update((aid, listener))
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
            pair_minor = self.is_minor(a) or self.is_minor(b)
            for c in ctxs.values():
                c.minor_involved = c.minor_involved or pair_minor
            start_jobs = {aid: self.adapter.snapshot_job(leases[aid].data["snap"]) for aid in (a, b)}
            last_turn_real = self.now().real
            while not conv.ended:
                if conv.turns:
                    await self._pace(last_turn_real, (lease_a, lease_b))
                    if lease_a.preempted or lease_b.preempted:
                        conv.ended = "preempted"
                        break
                conv.turn_cap = min(conv.turn_cap, self._turn_cap(self.now().rate))
                sp_id = conv.next_speaker()
                li = leases[sp_id]
                lis = conv.listener_of(sp_id)
                li.generating = True
                ctxs[sp_id].secrets = memory.secret_texts(self.minds[sp_id], self.names.get(lis))
                check, on_check = self._repeat_check(conv, sp_id)
                res = await speak(self.llm, slot=li.slot, mind=self.minds[sp_id], name=self.names[sp_id],
                                  prefix=li.data["prefix"], turns=conv.turns_for_prompt(),
                                  turn_no=len(conv.turns) // 2, ctx=ctxs[sp_id],
                                  listener_name=self.names[lis], labels=self.labels_for(ctxs[sp_id].minor_involved),
                                  max_tokens=self.cfg.llm.line_max_tokens,
                                  check=check, retries=self.cfg.repeat_retries, on_check=on_check)
                li.generating = False
                if isinstance(res, Dropped):
                    self.dropped.append((sp_id, res.reason))
                    # a repeat that survived its retry ends the talk with no line: never a repeat on screen
                    conv.ended = "repeat" if res.reason == "repeat" else "dropped"
                    break
                if lease_a.preempted or lease_b.preempted:
                    conv.ended = "preempted"
                    break
                if item.expired(self.now()) and not conv.turns:
                    conv.ended = "expired"
                    break
                ok = await self._say(sp_id, res, listener=lis, expires=self.wire_expiry(item) if not conv.turns else None,
                                     conv=conv.id, event=item.event, minor=ctxs[sp_id].minor_involved)
                if not ok:
                    conv.ended = "apart"       # the game's liveness check refused: gone or out of range
                    break
                conv.add(sp_id, res.text, res.intent)
                self._remember_own(sp_id, res.text)
                last_turn_real = self.now().real
                self.leases.quotas.record(sp_id, lis, self.now(), pair=len(conv.turns) == 1)
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

    @staticmethod
    def _turn_cap(rate: float) -> int:
        """Fewer turns as the game speeds up (6 at 1x, 4 at 2x, 3 at 3x, 2 at dev speed)."""
        return 6 if rate <= 60 else 4 if rate <= 180 else 3 if rate <= 360 else 2

    async def _pace(self, last_real: float, leases) -> None:
        """Give the last line time to be read before the next turn: ``turn_gap_ticks`` of game time, but at least
        ``turn_gap_floor_real_s`` of play time. Ends at once if a window was taken for something more important."""
        now = self.now()
        wait = max(self.cfg.turn_gap_floor_real_s, self.cfg.turn_gap_ticks / max(now.rate, 1.0)) - (now.real - last_real)
        while wait > 0 and not any(l.preempted for l in leases):
            step = min(wait, 0.25)
            await asyncio.sleep(step)
            if not self.bridge.env.paused:           # a pause is not waiting time: the line is still on screen
                wait -= step
            for l in leases:
                self.leases.touch(l, self.now())

    def _repeat_check(self, conv: Conversation, speaker: int):
        """(check, on_check) for speak(): refuse a turn that repeats an earlier turn of this conversation or the
        speaker's own recent lines; count and trace every check (self.loop)."""
        if not self.cfg.repeat_guard:
            return None, None
        turns = [(t.speaker, t.text) for t in conv.turns]
        own = list(self.recent_own.get(speaker, []))
        hits: dict[str, object] = {}

        def check(text: str):
            hit = find_repeat(text, speaker, turns, own)
            if hit is None:
                return None
            hits[text] = hit
            return "repeat", retry_request(self.names[speaker], hit)

        def on_check(ev: dict) -> None:
            st = self.loop
            if ev["attempt"] == 0:
                st.checked += 1
            hit = hits.get(ev["text"])
            if ev["refused"]:
                if ev["attempt"] == 0:
                    st.caught += 1
                    st.retried += 1 if self.cfg.repeat_retries > 0 else 0
                if ev["attempt"] >= self.cfg.repeat_retries:
                    st.ended += 1
            elif ev["attempt"] > 0:
                st.recovered += 1
            st.trace.append({"conv": conv.id, "speaker": speaker, "turn": len(conv.turns), "attempt": ev["attempt"],
                             "text": ev["text"], "refused": ev["refused"],
                             "against": getattr(hit, "other", None), "score": round(getattr(hit, "score", 0.0), 3),
                             "kind": getattr(hit, "kind", None)})
        return check, on_check

    def _remember_own(self, aid: int, text: str) -> None:
        lst = self.recent_own.setdefault(aid, [])
        lst.append(text)
        del lst[:-max(1, self.cfg.repeat_own_recent)]

    async def finish_conversation(self, conv: Conversation, leases: dict[int, Lease]) -> None:
        """One memory and one relation update per actor, written by that actor's window (template fallback).
        From 3x the templates are used at once: the windows are freed instead of waiting on four more model calls,
        and the memory is written either way."""
        now = self.bridge.env.game
        fast = self.now().rate >= self.cfg.fast_summary_rate
        for me in (conv.a, conv.b):
            other = conv.b if me == conv.a else conv.a
            mind = self.minds[me]
            li = leases[me]
            base = [("system", li.data["prefix"]),
                    ("user", "=== TURNS SO FAR ===\n" + "\n".join(f"{n}: {t}" for n, t in conv.turns_for_prompt()))]
            mem_text = feel = None
            if not fast:
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
                    theirs, self.revs[aid] = await self.bridge.get_mind(aid)   # stale rev: take the game's, retry once
                    if merge_player_edits(mind, theirs):
                        blob = mind.to_blob(self.bridge.env.game, self.day)
                    self.revs[aid] = await self.bridge.put_mind(aid, blob, self.revs[aid])
                self.dirty.discard(aid)
            except BridgeError as e:
                log.warning("mind save failed for %s: %s", aid, e)
                if e.code in ("too_large",):
                    self.dirty.discard(aid)

"""Idle-time work: reflection, diaries (an actor's idle window) and the chronicle (the narrator slot).

Rules (SPEC §5, §7):
- Only when no window is leased for real work and nothing is queued. One idle job at a time: the three
  slots share one machine's tokens per second, so idle work never runs beside a live conversation.
- Lowest priority. The moment actor work is queued or leased, the running idle job is cancelled (its LLM call
  is dropped, nothing is applied) and its window is released.
- Order: reflection, then the chronicle, then diaries, then wants with follow-through (core/wants.py).
"""
from __future__ import annotations

import asyncio
import hashlib
import logging
from typing import TYPE_CHECKING

from . import gamedata as gd
from . import memory
from .bridge import BridgeError
from .lease import Cls, Lease
from .llm import LLMError
from .wants import run_wants
from .reflection import apply_reflection, reflection_due, run_reflection
from .sanitize import prompt_cap_words, sanitize_prose

if TYPE_CHECKING:
    from .director import Director

log = logging.getLogger("hermesvoice")

MAX_DIARY_MEMORIES = 10


class IdleRunner:
    def __init__(self, d: "Director"):
        self.d = d
        self.task: asyncio.Task | None = None
        self.lease: Lease | None = None
        self.kind = ""
        self.fail_until: dict[tuple[str, int], float] = {}
        self.log: list[dict] = []            # what idle work produced (reflections, diaries, chronicle entries)

    # ---- state ------------------------------------------------------------
    def active(self) -> bool:
        return self.task is not None and not self.task.done()

    def actors_want_windows(self) -> bool:
        ls = self.d.leases
        return bool(ls.queue) or any(l.cls > Cls.IDLE for l in ls.leases.values())

    def preempt(self, reason: str = "window_wanted") -> bool:
        """Cancel the running idle job at once. Nothing it was producing is applied."""
        if not self.active():
            return False
        if self.lease is not None:
            self.lease.preempted = True
            self.d.leases.release(self.lease, "preempted")
            self.d.dropped.append((self.lease.actor_id, f"idle_preempted:{self.kind}"))
        else:
            self.d.dropped.append((-1, f"idle_preempted:{self.kind}"))
        self.d.leases.narrator_busy = False if self.kind == "chronicle" else self.d.leases.narrator_busy
        self.task.cancel()
        self.task, self.lease = None, None
        return True

    def preempt_if_wanted(self) -> None:
        if self.actors_want_windows():
            self.preempt()

    # ---- picking ----------------------------------------------------------
    def _ok(self, kind: str, aid: int) -> bool:
        return self.fail_until.get((kind, aid), -1.0) <= self.d.clock()

    def _fail(self, kind: str, aid: int) -> None:
        self.fail_until[(kind, aid)] = self.d.clock() + self.d.cfg.idle_retry_real_s

    def due_reflection(self) -> list[int]:
        d, now = self.d, self.d.bridge.env.game
        c = d.cfg
        due = [(m.unreflected_importance, aid) for aid, m in d.minds.items()
               if reflection_due(m, now, d.day, c.reflection_importance, c.reflection_every_days)
               and self._ok("reflect", aid)]
        return [aid for _, aid in sorted(due, reverse=True)]

    def diary_target(self, mind) -> int | None:
        d = self.d
        now = d.bridge.env.game
        today, frac = now // d.day, (now % d.day) / d.day
        target = today if frac >= d.cfg.diary_day_fraction else today - 1
        if target < 0 or mind.last_diary_day >= target:
            return None
        if not any(m.game // d.day == target for m in mind.memories):
            return None
        return target

    def due_diary(self) -> list[tuple[int, int]]:
        if not self.d.cfg.diary_enabled:
            return []
        out = [(aid, t) for aid, m in sorted(self.d.minds.items())
               if (t := self.diary_target(m)) is not None and self._ok("diary", aid)]
        return out

    def run_once(self) -> None:
        """Called every step after actor work was launched."""
        d = self.d
        if self.actors_want_windows():
            self.preempt()
            return
        if self.active() or not d.leases.narrator_may_do_idle_work():
            return
        for aid in self.due_reflection():
            if self._start_actor("reflect", aid, self._reflect(aid)):
                return
        if d.cfg.chronicle_enabled and d.chronicle.due(d.bridge.env.game // d.day) is not None \
                and self._ok("chronicle", -1):
            self.kind = "chronicle"
            d.leases.narrator_busy = True
            self.task = d._spawn(self._chronicle())
            return
        for aid, target in self.due_diary():
            if self._start_actor("diary", aid, self._diary(aid, target)):
                return
        for aid in self.due_wants():
            if self._start_actor("wants", aid, self._wants(aid)):
                return

    def due_wants(self) -> list[int]:
        """Wants with follow-through (core/wants.py), paced by the existing ambient pacing: off while no tool backend
        is set, off while ambient talk is paused (speed), at least the ambient per-actor game gap between decisions,
        and thinned by the player's talk-frequency setting."""
        d = self.d
        if d.tool_backend is None or d.leases.ambient_paused(d.bridge.env.speed):
            return []
        freq = float(d.bridge_cfg.get("talk_frequency", 1.0))
        if freq <= 0 or d.bridge_cfg.get("effects") == "off":
            return []
        now, gap = d.bridge.env.game, d.cfg.quotas.min_gap_game_ambient
        out = []
        for aid in sorted(d.minds):
            if now - d.wants_last.get(aid, -10**12) < gap or not self._ok("wants", aid):
                continue
            roll = int(hashlib.sha1(f"wants:{aid}:{now // max(gap, 1)}".encode()).hexdigest(), 16) % 1000 / 1000
            if roll < min(1.0, d.weights.get(aid, 1.0) * freq * 0.5):
                out.append(aid)
        return out

    async def _wants(self, aid: int) -> None:
        d = self.d
        d.wants_last[aid] = d.bridge.env.game
        self.lease.generating = True
        try:
            await run_wants(d, aid, d.tool_backend, d.tool_mode)
        except Exception:
            log.exception("wants failed")
            self._fail("wants", aid)

    def _start_actor(self, kind: str, aid: int, coro) -> bool:
        d = self.d
        item = d.leases.make_item(aid, kind, d.now(), cls=Cls.IDLE)
        lease = d.leases.acquire(item, d.now())
        if lease is None:
            coro.close()
            return False
        self.kind, self.lease = kind, lease
        lease.data["idle"] = True
        self.task = d._spawn(self._guard(coro, lease))
        return True

    async def _guard(self, coro, lease: Lease) -> None:
        try:
            await coro
        finally:
            lease.generating = False
            if not lease.ended:
                self.d.leases.release(lease, "idle_done")
            if self.lease is lease:
                self.task, self.lease = None, None

    # ---- the jobs ---------------------------------------------------------
    async def _reflect(self, aid: int) -> None:
        d = self.d
        mind = d.minds[aid]
        now = d.bridge.env.game
        lease = self.lease
        lease.generating = True
        res = await run_reflection(d.llm, d.adapter, mind, d.names, slot=lease.slot, now=now, day=d.day,
                                   max_tokens=d.cfg.reflection_max_tokens, name_tokens=d.name_tokens,
                                   common_words=set(d.adapter.common_words),
                                   involves_minor=d.is_minor(aid), minor_names=d.minor_tokens())
        lease.generating = False
        if lease.preempted:
            return
        if res is None:
            self._fail("reflect", aid)
            d.dropped.append((aid, "reflect_failed"))
            return
        name_ids = {n.lower(): i for i, n in d.names.items()}
        out = apply_reflection(mind, res, now, name_ids)
        out.update(memory.upkeep(mind, now, d.day, d.cfg.memory_floor, d.cfg.memory_merge_similarity))
        d.dirty.add(aid)
        self.log.append({"kind": "reflection", "actor": aid, "name": d.names.get(aid), "game": now, **out})

    async def _diary(self, aid: int, target: int) -> None:
        d = self.d
        mind = d.minds[aid]
        now = d.bridge.env.game
        lease = self.lease
        mems = [m for m in memory.weight_sorted(mind, now, d.day) if m.game // d.day == target][:MAX_DIARY_MEMORIES]
        mems.sort(key=lambda m: (m.game, m.id))
        today = "\n".join(f"- {gd.clean(m.text, 'event')}" for m in mems)
        user = (f"=== WHO YOU ARE ===\n{gd.data_section('bio', gd.clean(mind.fixed.text, 'line', max_len=600, multiline=True))}"
                f"=== WHO YOU ARE BECOMING ===\n{gd.data_section('character', gd.clean(mind.evolving_bio or '(nothing yet)', 'desc'))}"
                f"=== WHAT HAPPENED TODAY ===\n{gd.data_section('events', today)}"
                f"=== REQUEST ===\n{d.adapter.diary_request}")
        lease.generating = True
        try:
            c = await d.llm.complete([("system", d.adapter.diary_system + "\n\n" + gd.DATA_RULE), ("user", user)], slot=lease.slot,
                                     kind="diary", max_tokens=d.cfg.diary_max_tokens, stop=[], temperature=0.8,
                                     actor=aid)
        except LLMError:
            lease.generating = False
            self._fail("diary", aid)
            return
        lease.generating = False
        if lease.preempted:
            return
        toks = d.name_tokens
        allnames = set().union(*toks.values()) if toks else set()
        known = set(toks.get(aid, set())).union(*(toks.get(p, set()) for m in mems for p in m.people))
        s = sanitize_prose(c.text, known_names=known, all_names=allnames, prompt_words=prompt_cap_words(user),
                           common_words=set(d.adapter.common_words), speaker_names=toks.get(aid, set()),
                           min_words=5, max_words=90, involves_minor=d.is_minor(aid), minor_names=d.minor_tokens())
        if not s.ok:
            self._fail("diary", aid)
            d.dropped.append((aid, f"diary:{s.reason}"))
            return
        mind.add_diary(target, now, s.text)
        d.dirty.add(aid)
        self.log.append({"kind": "diary", "actor": aid, "name": d.names.get(aid), "day": target, "game": now,
                         "text": s.text})
        try:
            await d.bridge.thought(aid, s.text, kind="diary")
        except BridgeError as e:
            log.warning("diary thought refused for %s: %s", aid, e)

    async def _chronicle(self) -> None:
        d = self.d
        try:
            today = d.bridge.env.game // d.day
            led = [e.summary for e in sorted(d.ledger.entries.values(), key=lambda e: (e.opened_time, e.ledger_id))]
            e = await d.chronicle.write(d.llm, d.adapter, slot=d.cfg.narrator_slot, today=today, ledger_lines=led,
                                        name_tokens=d.name_tokens, budget=d.cfg.narrator_context_tokens,
                                        max_tokens=d.cfg.chronicle_max_tokens,
                                        common_words=set(d.adapter.common_words), minor_names=d.minor_tokens())
            if e is None:
                self._fail("chronicle", -1)
                return
            self.log.append({"kind": "chronicle", "day": e.day, "entry_kind": e.kind, "game": e.game,
                             "text": e.text, "summary": d.chronicle.state.summary})
            if d.cfg.chronicle_bridge:
                try:
                    await d.bridge.chronicle(e.day, e.text, e.kind)
                except (BridgeError, AttributeError) as ex:
                    log.warning("chronicle not written to the bridge: %s", ex)
        finally:
            d.leases.narrator_busy = False
            if self.lease is None:
                self.task = None

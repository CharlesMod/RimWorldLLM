"""Documentary voice: one wry narrator sentence at a big moment, and one recap sentence per game day.

Contract (small on purpose; a C# port follows it):
- ``note(event)``: the director hands every event. The adapter's ``commentary_moment(event)`` turns it into a ``Moment``
  (kind, dedupe key, text, weight, urgent) or None. Moments below ``MIN_WEIGHT`` are ignored, a repeat of the same key
  inside ``DEDUPE_GAME`` is ignored, the pending list is capped.
- ``run_once()`` (every step): speak at most one pending moment, else the recap of a finished game day. Only while the
  vivarium is there, the narrator plays and commentary is on, the narrator slot is free (``narrator_may_do_idle_work``)
  and no idle job holds it. Gates: ``commentary_min_gap_real_s`` real seconds since the last line AND
  ``commentary_min_gap_game`` game units; an urgent moment (the adapter marks them) skips both gaps but never
  the ``URGENT_FLOOR_REAL_S`` floor. The recap needs only the floor.
- ``preempt_if_wanted()``: a steward cycle owns slot 0 from its start; the running call is cancelled and nothing is posted.
- One narrator call (kind "narrator", stop at newline), game text through ``gd.clean``, the answer through
  ``sanitize_line`` (age gate included), posted as ``POST /chat who=comment``. A dropped line is not retried and the
  moment is spent.
"""
from __future__ import annotations

import asyncio
import logging
import re
from dataclasses import dataclass
from typing import TYPE_CHECKING

from . import gamedata as gd
from .bridge import BridgeError
from .llm import LLMError
from .sanitize import prompt_cap_words, sanitize_line

if TYPE_CHECKING:
    from .director import Director

log = logging.getLogger("rimworldllm")

MIN_WEIGHT = 0.4            # a moment lighter than this is never spoken about
DEDUPE_GAME = 2500          # same key again within this many units (about an hour) is dropped
MAX_PENDING = 8
STALE_GAME = 30000          # a non-urgent moment older than this (half a day) is no longer news
URGENT_FLOOR_REAL_S = 30.0  # nothing is spoken sooner than this after the last line, urgent or not
RECAP_EVENTS = 6            # notable events a recap is built from (heaviest first, told in order)
DAY_KEEP = 24               # moments remembered for the recap of a day


@dataclass(frozen=True)
class Moment:
    kind: str
    key: str                 # dedupe key: kind plus who/what
    text: str                # what happened, one line (cleaned before it reaches a prompt)
    weight: float            # 0..1 interest
    urgent: bool
    game: int
    minor: bool = False      # a minor is in it: the age gate sees it


class Commentary:
    def __init__(self, d: "Director"):
        self.d = d
        self.pending: list[Moment] = []
        self.seen: dict[str, int] = {}                  # key -> game time last accepted
        self.day_moments: dict[int, list[Moment]] = {}
        self.recap_day: int | None = None               # a finished day whose recap is owed
        self.recapped: set[int] = set()
        self.last_real = float("-inf")
        self.last_game = -10**9
        self.task: asyncio.Task | None = None
        self.unsupported = False                        # the mod refused who=comment
        self.log: list[dict] = []                       # what was said: {"kind","day","text"}
        self.drops: list[str] = []

    def reset(self) -> None:
        """A new save: nothing pending, no day remembered, no gap owed."""
        self.pending.clear(); self.seen.clear(); self.day_moments.clear(); self.recapped.clear()
        self.recap_day, self.task = None, None
        self.last_real, self.last_game = float("-inf"), -10**9
        self.d.leases.narrator_busy = False

    # ---- intake -----------------------------------------------------------
    def enabled(self) -> bool:
        d = self.d
        if self.unsupported or not d.cfg.commentary:
            return False
        vv = d.steward.vv
        return bool(vv) and bool(vv.get("playing")) and bool((vv.get("settings") or {}).get("commentary", True))

    def note(self, e: dict) -> None:
        fn = getattr(self.d.adapter, "commentary_moment", None)
        if fn is None or not self.d.cfg.commentary:
            return
        m = fn(e, self.d)
        if m is None or m.weight < MIN_WEIGHT:
            return
        if m.game - self.seen.get(m.key, -10**9) < DEDUPE_GAME:
            return
        self.seen[m.key] = m.game
        if len(self.seen) > 200:
            for k in sorted(self.seen, key=self.seen.get)[:100]:
                del self.seen[k]
        self.pending.append(m)
        del self.pending[:-MAX_PENDING]
        day = m.game // self.d.day
        self.day_moments.setdefault(day, []).append(m)
        del self.day_moments[day][:-DAY_KEEP]
        for old in [k for k in self.day_moments if k < day - 2]:
            del self.day_moments[old]

    # ---- running ----------------------------------------------------------
    def active(self) -> bool:
        return self.task is not None and not self.task.done()

    def preempt_if_wanted(self) -> None:
        d = self.d
        if self.active() and (d.leases.narrator_owner is not None or not self.enabled()):
            self.task.cancel()
            self.task = None
            d.leases.narrator_busy = False
            d.dropped.append((-1, "commentary_preempted"))

    def _pick(self, now_game: int, real_gap: float) -> Moment | None:
        c = self.d.cfg
        self.pending = [m for m in self.pending if m.urgent or now_game - m.game <= STALE_GAME]
        if real_gap < URGENT_FLOOR_REAL_S:
            return None
        game_gap_ok = now_game - self.last_game >= c.commentary_min_gap_game
        real_ok = real_gap >= c.commentary_min_gap_real_s
        ok = [m for m in self.pending if m.urgent or (game_gap_ok and real_ok)]
        return max(ok, key=lambda m: (m.weight, m.game)) if ok else None

    def run_once(self) -> None:
        d = self.d
        if self.active():
            return
        today = d.bridge.env.game // d.day
        if self.recap_day is None and d.cfg.commentary_recap:
            done = [k for k in self.day_moments if k < today and k not in self.recapped]
            if done:
                self.recap_day = max(done)
                self.recapped.update(done)              # older unspoken days are dropped: one line per day, the latest
        if not self.pending and self.recap_day is None:
            return
        if not self.enabled() or not d.leases.narrator_may_do_idle_work() or d.idle.active():
            return
        real_gap = d.now().real - self.last_real
        m = self._pick(d.bridge.env.game, real_gap)
        if m is not None:
            self.pending.remove(m)
            self.task = d._spawn(self._speak("moment", m.kind, [m], m.game // d.day))
        elif self.recap_day is not None and real_gap >= URGENT_FLOOR_REAL_S:
            day, self.recap_day = self.recap_day, None
            ms = self.day_moments.get(day, [])
            if ms:
                self.task = d._spawn(self._speak("recap", "recap", ms, day))

    # ---- one line ---------------------------------------------------------
    def messages(self, mode: str, ms: list[Moment], day: int) -> list[tuple[str, str]]:
        d = self.d
        a = d.adapter
        if mode == "recap":
            top = sorted(sorted(ms, key=lambda m: -m.weight)[:RECAP_EVENTS], key=lambda m: m.game)
            body = "\n".join(f"- {gd.clean(m.text, 'event')}" for m in top)
            user = (f"=== THE DAY (day {day + 1}) ===\n{gd.data_section('events', body)}"
                    f"=== REQUEST ===\n{a.commentary_recap_request}")
        else:
            body = f"- {gd.clean(ms[0].text, 'event')}"
            user = (f"=== WHAT JUST HAPPENED ===\n{gd.data_section('moment', body)}"
                    f"=== REQUEST ===\n{a.commentary_request}")
        return [("system", a.commentary_prompt + "\n\n" + gd.DATA_RULE), ("user", user)]

    async def _speak(self, mode: str, kind: str, ms: list[Moment], day: int) -> None:
        d = self.d
        d.leases.narrator_busy = True
        try:
            msgs = self.messages(mode, ms, day)
            try:
                c = await d.llm.complete(msgs, slot=d.cfg.narrator_slot, kind="narrator", max_tokens=48, stop=["\n"])
            except LLMError:
                return
            names = set().union(*d.name_tokens.values()) if d.name_tokens else set()
            s = sanitize_line(c.text, speaker_names=set(), known_names=names, all_names=names,
                              prompt_words=prompt_cap_words(msgs[1][1]), common_words=set(d.adapter.common_words),
                              involves_minor=any(m.minor for m in ms), minor_names=d.minor_tokens())
            if not s.ok:
                self.drops.append(f"{kind}:{s.reason}")
                d.dropped.append((-1, f"commentary:{s.reason or 'sanitize'}"))
                return
            try:
                await d.bridge.post_chat("comment", s.text)
            except BridgeError as e:
                if e.status in (400, 404):
                    self.unsupported = True              # an older mod without who=comment: stay quiet
                log.warning("commentary not posted: %s", e)
                return
            self.last_real = d.now().real
            self.last_game = d.bridge.env.game
            self.log.append({"mode": mode, "kind": kind, "day": day, "text": s.text})
            del self.log[:-100]
        finally:
            d.leases.narrator_busy = False
            self.task = None

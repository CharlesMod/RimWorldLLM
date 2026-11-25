"""Lease manager (SPEC §4, §7).

Windows are slots 1..N, each leased to one pawn at a time; slot 0 is the narrator.
- A lease takes the first free pawn window (no slot affinity: measured worthless in M0).
- Priority: PLAYER > URGENT (death, break, raid) > INJURY (injury, tend) > SOCIAL > AMBIENT.
  A higher class may end a lower-class lease early (preemption).
- Expiry by class on a dual clock (game ticks and real seconds, whichever comes first).
- Quotas per pawn and per pair per game day; skip-if-nothing-new; ambient pause at high speed.
- Narrator: idle-only work, and coverage when the windows are busy.
"""
from __future__ import annotations

import enum
import itertools
from dataclasses import dataclass, field
from typing import Callable


class Cls(enum.IntEnum):
    AMBIENT = 1     # level-up, ambient thought
    SOCIAL = 2
    INJURY = 3      # injury, tend
    URGENT = 4      # death, mental break, raid start
    PLAYER = 5      # player talk


EVENT_CLASS: dict[str, Cls] = {
    "player_talk": Cls.PLAYER,
    "death": Cls.URGENT, "mental_break": Cls.URGENT, "raid_start": Cls.URGENT,
    "injury": Cls.INJURY, "tend": Cls.INJURY,
    "social": Cls.SOCIAL,
    "level_up": Cls.AMBIENT, "thought": Cls.AMBIENT,
}


def class_of(event_type: str) -> Cls:
    return EVENT_CLASS.get(event_type, Cls.AMBIENT)


@dataclass(frozen=True)
class Expiry:
    ticks: int | None
    real_s: float | None


# SPEC §7: player never; urgent 5 s; social and ambient 20 s; event reactions 5,000 ticks.
REQUEST_EXPIRY: dict[Cls, Expiry] = {
    Cls.PLAYER: Expiry(None, None),
    Cls.URGENT: Expiry(600, 5.0),
    Cls.INJURY: Expiry(5000, 60.0),
    Cls.SOCIAL: Expiry(1200, 20.0),
    Cls.AMBIENT: Expiry(1200, 20.0),
}
# how long a lease may sit without activity before it ends by rule
LEASE_IDLE_TTL_S: dict[Cls, float] = {
    Cls.PLAYER: 300.0, Cls.URGENT: 30.0, Cls.INJURY: 30.0, Cls.SOCIAL: 40.0, Cls.AMBIENT: 15.0}


@dataclass
class Now:
    tick: int
    real: float


@dataclass
class WorkItem:
    id: int
    pawn_id: int
    cls: Cls
    kind: str                      # event type or "talk", "reflect", "diary"
    created: Now
    expires_tick: int | None
    expires_real: float | None
    event: dict | None = None
    listener: int | None = None
    payload: dict = field(default_factory=dict)

    def expired(self, now: Now) -> bool:
        return ((self.expires_tick is not None and now.tick >= self.expires_tick) or
                (self.expires_real is not None and now.real >= self.expires_real))


@dataclass
class Lease:
    slot: int
    pawn_id: int
    cls: Cls
    kind: str
    started: Now
    touched_real: float
    conversation_id: str | None = None
    generating: bool = False
    preempted: bool = False        # set when a higher class took the window; holder must stop
    ended: bool = False
    end_reason: str = ""
    turn_no: int = 0
    data: dict = field(default_factory=dict)    # prefix, turns, ... owned by the director

    def idle_expired(self, now: Now) -> bool:
        return now.real - self.touched_real > LEASE_IDLE_TTL_S[self.cls]


class QuotaTracker:
    """Per pawn and per ordered pair per game day, plus an ambient gap on both clocks."""

    def __init__(self, pawn_per_day=12, pair_per_day=4, ticks_per_day=60000,
                 min_gap_ticks=1500, min_gap_real_s=20.0):
        self.pawn_per_day, self.pair_per_day, self.tpd = pawn_per_day, pair_per_day, ticks_per_day
        self.min_gap_ticks, self.min_gap_real_s = min_gap_ticks, min_gap_real_s
        self.pawn_counts: dict[tuple[int, int], int] = {}
        self.pair_counts: dict[tuple[int, int, int], int] = {}
        self.last_spoke: dict[int, Now] = {}

    def _day(self, tick: int) -> int:
        return tick // self.tpd

    def allowed(self, pawn: int, other: int | None, cls: Cls, now: Now) -> tuple[bool, str]:
        if cls >= Cls.INJURY:       # urgent/player/injury bypass quotas
            return True, ""
        day = self._day(now.tick)
        if self.pawn_counts.get((pawn, day), 0) >= self.pawn_per_day:
            return False, "pawn_quota"
        if other is not None and self.pair_counts.get((min(pawn, other), max(pawn, other), day), 0) >= self.pair_per_day:
            return False, "pair_quota"
        last = self.last_spoke.get(pawn)
        if last and cls <= Cls.SOCIAL:
            if now.tick - last.tick < self.min_gap_ticks and now.real - last.real < self.min_gap_real_s:
                return False, "gap"       # both clocks say too soon
        return True, ""

    def record(self, pawn: int, other: int | None, now: Now) -> None:
        day = self._day(now.tick)
        self.pawn_counts[(pawn, day)] = self.pawn_counts.get((pawn, day), 0) + 1
        if other is not None:
            k = (min(pawn, other), max(pawn, other), day)
            self.pair_counts[k] = self.pair_counts.get(k, 0) + 1
        self.last_spoke[pawn] = now


class LeaseManager:
    def __init__(self, pawn_slots: list[int], narrator_slot: int = 0,
                 quotas: QuotaTracker | None = None, pause_ambient_at_speed: int = 3):
        self.pawn_slots = list(pawn_slots)
        self.narrator_slot = narrator_slot
        self.leases: dict[int, Lease] = {}          # slot -> lease
        self.queue: list[WorkItem] = []
        self.quotas = quotas or QuotaTracker()
        self.pause_ambient_at_speed = pause_ambient_at_speed
        self._ids = itertools.count(1)
        self.last_signature: dict[int, str] = {}    # skip-if-nothing-new
        self.narrator_busy = False
        self.log: list[tuple[str, str]] = []        # (what, why) trail for the debug view and tests

    # ---- requests -------------------------------------------------------
    def ambient_paused(self, speed: int) -> bool:
        return speed >= self.pause_ambient_at_speed

    def make_item(self, pawn_id: int, kind: str, now: Now, event: dict | None = None,
                  listener: int | None = None, cls: Cls | None = None, payload: dict | None = None) -> WorkItem:
        c = cls if cls is not None else class_of(kind)
        ex = REQUEST_EXPIRY[c]
        return WorkItem(next(self._ids), pawn_id, c, kind, now,
                        None if ex.ticks is None else now.tick + ex.ticks,
                        None if ex.real_s is None else now.real + ex.real_s,
                        event, listener, payload or {})

    def submit(self, item: WorkItem, now: Now, speed: int = 1) -> str:
        """Queue a request. Returns 'queued', or why it was refused."""
        if item.cls <= Cls.SOCIAL and self.ambient_paused(speed):
            return self._why("drop", "ambient_paused")
        ok, why = self.quotas.allowed(item.pawn_id, item.listener, item.cls, now)
        if not ok:
            return self._why("drop", why)
        for q in self.queue:        # merge duplicates: same pawn and kind
            if q.pawn_id == item.pawn_id and q.kind == item.kind and q.listener == item.listener:
                if item.cls > q.cls:
                    q.cls = item.cls
                return self._why("merged", item.kind)
        self.queue.append(item)
        return "queued"

    def _why(self, what: str, why: str) -> str:
        self.log.append((what, why))
        return f"{what}:{why}"

    def expire(self, now: Now) -> list[WorkItem]:
        dead = [q for q in self.queue if q.expired(now)]
        self.queue = [q for q in self.queue if not q.expired(now)]
        for q in dead:
            self.log.append(("expired", f"{q.kind}:{q.pawn_id}"))
        return dead

    def expire_leases(self, now: Now) -> list[Lease]:
        out = []
        for slot, l in list(self.leases.items()):
            if not l.generating and l.idle_expired(now):
                self.release(l, "idle_expired")
                out.append(l)
        return out

    # ---- leasing --------------------------------------------------------
    def lease_for(self, pawn_id: int) -> Lease | None:
        return next((l for l in self.leases.values() if l.pawn_id == pawn_id), None)

    def free_slots(self) -> list[int]:
        return [s for s in self.pawn_slots if s not in self.leases]

    def acquire(self, item: WorkItem, now: Now) -> Lease | None:
        """First free window; else preempt the lowest strictly-lower-class lease; else None."""
        if item.expired(now):
            return None
        existing = self.lease_for(item.pawn_id)
        if existing:
            return None            # pawn already inhabited; the item stays queued
        free = self.free_slots()
        slot = free[0] if free else None
        if slot is None:
            victims = sorted((l for l in self.leases.values() if l.cls < item.cls),
                             key=lambda l: (l.cls, l.started.real))
            if not victims:
                return None
            v = victims[0]
            v.preempted = True
            slot = v.slot
            self.release(v, "preempted")
        lease = Lease(slot=slot, pawn_id=item.pawn_id, cls=item.cls, kind=item.kind,
                      started=now, touched_real=now.real)
        self.leases[slot] = lease
        self.log.append(("lease", f"slot{slot}:pawn{item.pawn_id}:{item.cls.name}"))
        return lease

    def release(self, lease: Lease, reason: str = "done") -> None:
        lease.ended, lease.end_reason = True, reason
        if self.leases.get(lease.slot) is lease:
            del self.leases[lease.slot]
        self.log.append(("release", f"slot{lease.slot}:{reason}"))

    def touch(self, lease: Lease, now: Now) -> None:
        lease.touched_real = now.real

    def pop_runnable(self, now: Now) -> list[tuple[WorkItem, Lease]]:
        """Highest class first (then oldest): lease as many queued items as windows allow."""
        self.expire(now)
        started = []
        for item in sorted(self.queue, key=lambda q: (-q.cls, q.created.real, q.id)):
            lease = self.acquire(item, now)
            if lease:
                self.queue.remove(item)
                started.append((item, lease))
        return started

    # ---- skip-if-nothing-new -------------------------------------------
    def nothing_new(self, pawn_id: int, signature: str) -> bool:
        return self.last_signature.get(pawn_id) == signature

    def mark_spoke(self, pawn_id: int, signature: str) -> None:
        self.last_signature[pawn_id] = signature

    # ---- narrator -------------------------------------------------------
    def windows_idle(self) -> bool:
        return not self.leases and not self.queue

    def narrator_may_do_idle_work(self) -> bool:
        """Chronicle, diary, reflection: only when no window is leased and nothing is queued."""
        return not self.narrator_busy and self.windows_idle()

    def narrator_should_cover(self, item: WorkItem, now: Now) -> bool:
        """Both windows busy, nothing preemptable, event still live: the narrator covers it."""
        if self.narrator_busy or item.expired(now) or self.free_slots():
            return False
        return not any(l.cls < item.cls for l in self.leases.values())

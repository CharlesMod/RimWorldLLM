"""Lease manager.

Windows are slots 1..N, each leased to one actor at a time; slot 0 is the narrator.
- A lease takes the first free actor window (no slot affinity).
- Priority: PLAYER > URGENT > REACTIVE > SOCIAL > AMBIENT (the adapter maps event types to classes).
  A higher class may end a lower-class lease early (preemption).
- Expiry by class on a dual clock (game units and real seconds, whichever comes first).
- Quotas per actor and per pair per game day; skip-if-nothing-new; ambient pause at high speed.
- Narrator: idle-only work, and coverage when the windows are busy.
"""
from __future__ import annotations

import enum
import itertools
from dataclasses import dataclass, field
from typing import Callable


class Cls(enum.IntEnum):
    IDLE = 0        # reflection and diaries: only in idle time, preempted by anything
    AMBIENT = 1     # background chatter
    SOCIAL = 2
    REACTIVE = 3    # reactions to events that happened to someone
    URGENT = 4      # the most pressing events
    PLAYER = 5      # player talk


@dataclass(frozen=True)
class Expiry:
    """A request's life on the dual clock (SPEC §7, docs/SPEED.md).

    units: how long the moment stays on screen, in game units.
    real_s: a hard cap in real *play* seconds (the clock stops while the game is paused).
    floor_real_s: the least real time a line is given, so a fast game does not outrun the model:
    the game window is max(units, floor_real_s * units_per_second_now). Never above the cap."""
    units: int | None
    real_s: float | None
    floor_real_s: float = 0.0


# player never; urgent 600 units (5 s cap); event reactions 3,000 units (60 s cap); social and ambient 1,200 units (20 s cap).
# The floors keep a line alive long enough for the model to write it at 2x to 4x.
REQUEST_EXPIRY: dict[Cls, Expiry] = {
    Cls.IDLE: Expiry(None, None),
    Cls.PLAYER: Expiry(None, None),
    Cls.URGENT: Expiry(600, 5.0, 3.0),
    Cls.REACTIVE: Expiry(3000, 60.0, 6.0),
    Cls.SOCIAL: Expiry(1200, 20.0, 4.0),
    Cls.AMBIENT: Expiry(1200, 20.0, 4.0),
}
# how long a lease may sit without activity before it ends by rule
LEASE_IDLE_TTL_S: dict[Cls, float] = {
    Cls.IDLE: 300.0, Cls.PLAYER: 300.0, Cls.URGENT: 30.0, Cls.REACTIVE: 30.0, Cls.SOCIAL: 40.0, Cls.AMBIENT: 15.0}


@dataclass
class Now:
    """game: game units. real: play seconds (real time with pauses taken out). rate: game units per real second now."""
    game: int
    real: float
    rate: float = 60.0


@dataclass
class WorkItem:
    id: int
    actor_id: int
    cls: Cls
    kind: str                      # event type or "talk", "reflect", "diary"
    created: Now
    expires_at: int | None
    expires_real: float | None
    event: dict | None = None
    listener: int | None = None
    payload: dict = field(default_factory=dict)
    severity: float = 0.0
    floor_real_s: float = 0.0

    def deadline(self, rate: float) -> int | None:
        """The game time after which the line is late, at the current speed: the game-time window or the real floor,
        whichever is longer."""
        if self.expires_at is None:
            return None
        return max(self.expires_at, self.created.game + int(self.floor_real_s * rate))

    def expired(self, now: Now) -> bool:
        dl = self.deadline(now.rate)
        return ((dl is not None and now.game >= dl) or
                (self.expires_real is not None and now.real >= self.expires_real))


@dataclass
class Lease:
    slot: int
    actor_id: int
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
    """Per actor and per ordered pair per game day, plus an ambient gap on both clocks."""

    def __init__(self, actor_per_day=12, pair_per_day=4, units_per_day=60000,
                 min_gap_game=1500, min_gap_real_s=20.0, ambient_share=0.75):
        self.actor_per_day, self.pair_per_day, self.tpd = actor_per_day, pair_per_day, units_per_day
        self.ambient_share = ambient_share      # ambient chatter may use this share of a day's lines; the rest is kept for social events
        self.min_gap_game, self.min_gap_real_s = min_gap_game, min_gap_real_s
        self.actor_counts: dict[tuple[int, int], int] = {}
        self.pair_counts: dict[tuple[int, int, int], int] = {}
        self.last_spoke: dict[int, Now] = {}

    def _day(self, game: int) -> int:
        return game // self.tpd

    def allowed(self, actor: int, other: int | None, cls: Cls, now: Now) -> tuple[bool, str]:
        if cls >= Cls.REACTIVE:       # reactive and above bypass quotas
            return True, ""
        day = self._day(now.game)
        used = self.actor_counts.get((actor, day), 0)
        if used >= self.actor_per_day:
            return False, "actor_quota"
        if cls <= Cls.AMBIENT and used >= int(self.actor_per_day * self.ambient_share):
            return False, "ambient_quota"
        if other is not None and self.pair_counts.get((min(actor, other), max(actor, other), day), 0) >= self.pair_per_day:
            return False, "pair_quota"
        last = self.last_spoke.get(actor)
        if last and cls <= Cls.SOCIAL:
            if now.game - last.game < self.min_gap_game and now.real - last.real < self.min_gap_real_s:
                return False, "gap"       # both clocks say too soon
        return True, ""

    def record(self, actor: int, other: int | None, now: Now, pair: bool = True) -> None:
        """A line was spoken. The pair's count is per talk, not per line: a conversation passes pair=False after its
        first turn, or one six-turn talk would use up the pair's four for the day."""
        day = self._day(now.game)
        self.actor_counts[(actor, day)] = self.actor_counts.get((actor, day), 0) + 1
        if other is not None and pair:
            k = (min(actor, other), max(actor, other), day)
            self.pair_counts[k] = self.pair_counts.get(k, 0) + 1
        self.last_spoke[actor] = now


class VoiceBudget:
    """How much the community speaks per real minute, whatever the game speed (SPEC §7, docs/SPEED.md).

    A token bucket on the play clock: ``per_min`` lines a minute, ``burst`` at once. Every spoken line costs a token (a
    conversation pays per turn); a request below URGENT waits in the queue until a token is free, and the queue hands the
    token to the most important request (class, then severity). At 4x the rate is scaled by ``fast_scale``."""

    def __init__(self, per_min: float = 5.0, burst: float = 5.0, fast_scale: float = 0.5, fast_rate: float = 600.0):
        self.per_min, self.burst, self.fast_scale, self.fast_rate = per_min, burst, fast_scale, fast_rate
        self.tokens = burst
        self._last: float | None = None

    def refill(self, now: Now) -> None:
        if self._last is not None and now.real > self._last:
            k = self.fast_scale if now.rate >= self.fast_rate else 1.0
            self.tokens = min(self.burst, self.tokens + (now.real - self._last) * self.per_min / 60.0 * k)
        self._last = now.real if self._last is None else max(self._last, now.real)

    def ready(self, now: Now, cost: float = 1.0) -> bool:
        self.refill(now)
        return self.tokens >= cost

    def spend(self, now: Now, n: float = 1.0) -> None:
        self.refill(now)
        self.tokens = max(-self.burst, self.tokens - n)


class LeaseManager:
    MAX_QUEUE = 16
    AMBIENT_RESERVE = 3.0        # tokens that must be in the budget before ambient chatter may start

    def __init__(self, actor_slots: list[int], narrator_slot: int = 0,
                 quotas: QuotaTracker | None = None, pause_ambient_at_speed: int = 4,
                 priority: Callable[[str], Cls] | None = None, budget: VoiceBudget | None = None):
        self.budget = budget or VoiceBudget(per_min=1e9, burst=1e9)      # unlimited unless the director sets one
        self.priority = priority or (lambda kind: Cls.AMBIENT)
        self.actor_slots = list(actor_slots)
        self.narrator_slot = narrator_slot
        self.leases: dict[int, Lease] = {}          # slot -> lease
        self.queue: list[WorkItem] = []
        self.quotas = quotas or QuotaTracker()
        self.pause_ambient_at_speed = pause_ambient_at_speed
        self._ids = itertools.count(1)
        self.last_signature: dict[int, str] = {}    # skip-if-nothing-new
        self.narrator_busy = False
        self.narrator_owner: str | None = None      # "steward" while a steward cycle holds slot 0: idle narrator work yields
        self.log: list[tuple[str, str]] = []        # (what, why) trail for the debug view and tests

    # ---- requests -------------------------------------------------------
    def ambient_paused(self, speed: int) -> bool:
        return speed >= self.pause_ambient_at_speed

    def make_item(self, actor_id: int, kind: str, now: Now, event: dict | None = None,
                  listener: int | None = None, cls: Cls | None = None, payload: dict | None = None,
                  severity: float | None = None, units: int | None = None,
                  floor_real_s: float | None = None) -> WorkItem:
        c = cls if cls is not None else self.priority(kind)
        ex = REQUEST_EXPIRY[c]
        u = units if (units is not None and ex.units is not None) else ex.units
        fl = ex.floor_real_s if floor_real_s is None else floor_real_s
        if severity is None:
            severity = float((event or {}).get("severity", 0.0))
        return WorkItem(next(self._ids), actor_id, c, kind, now,
                        None if u is None else now.game + max(u, int(fl * now.rate)),
                        None if ex.real_s is None else now.real + ex.real_s,
                        event, listener, payload or {}, severity, fl)

    def submit(self, item: WorkItem, now: Now, speed: int = 1) -> str:
        """Queue a request. Returns 'queued', or why it was refused."""
        if item.cls <= Cls.SOCIAL and self.ambient_paused(speed):
            return self._why("drop", "ambient_paused")
        # the game's own interactions are already rate-limited by the game: no pair quota for them
        ok, why = self.quotas.allowed(item.actor_id, None if item.payload.get("vanilla") else item.listener, item.cls, now)
        if not ok:
            return self._why("drop", why)
        for q in self.queue:        # merge duplicates: same actor and kind (ambient events coalesce; the strongest stays)
            if q.actor_id == item.actor_id and q.kind == item.kind and q.listener == item.listener:
                if item.cls > q.cls:
                    q.cls = item.cls
                q.severity = max(q.severity, item.severity)
                return self._why("merged", item.kind)
        if len(self.queue) >= self.MAX_QUEUE:      # bounded: the least important request makes room, or is the one refused
            victim = min(self.queue, key=lambda q: (q.cls, q.severity, -q.created.real, -q.id))
            if (item.cls, item.severity) <= (victim.cls, victim.severity):
                return self._why("drop", "queue_full")
            self.queue.remove(victim)
            self.log.append(("evicted", f"{victim.kind}:{victim.actor_id}"))
        self.queue.append(item)
        return "queued"

    def _why(self, what: str, why: str) -> str:
        self.log.append((what, why))
        return f"{what}:{why}"

    def expire(self, now: Now) -> list[WorkItem]:
        dead = [q for q in self.queue if q.expired(now)]
        self.queue = [q for q in self.queue if not q.expired(now)]
        for q in dead:
            self.log.append(("expired", f"{q.kind}:{q.actor_id}"))
        return dead

    def expire_leases(self, now: Now) -> list[Lease]:
        out = []
        for slot, l in list(self.leases.items()):
            if not l.generating and l.idle_expired(now):
                self.release(l, "idle_expired")
                out.append(l)
        return out

    # ---- leasing --------------------------------------------------------
    def lease_for(self, actor_id: int) -> Lease | None:
        return next((l for l in self.leases.values() if l.actor_id == actor_id), None)

    def free_slots(self) -> list[int]:
        return [s for s in self.actor_slots if s not in self.leases]

    def acquire(self, item: WorkItem, now: Now) -> Lease | None:
        """First free window; else preempt the lowest strictly-lower-class lease; else None."""
        if item.expired(now):
            return None
        existing = self.lease_for(item.actor_id)
        if existing:
            return None            # actor already inhabited; the item stays queued
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
        lease = Lease(slot=slot, actor_id=item.actor_id, cls=item.cls, kind=item.kind,
                      started=now, touched_real=now.real)
        self.leases[slot] = lease
        self.log.append(("lease", f"slot{slot}:actor{item.actor_id}:{item.cls.name}"))
        return lease

    def release(self, lease: Lease, reason: str = "done") -> None:
        lease.ended, lease.end_reason = True, reason
        if self.leases.get(lease.slot) is lease:
            del self.leases[lease.slot]
        self.log.append(("release", f"slot{lease.slot}:{reason}"))

    def touch(self, lease: Lease, now: Now) -> None:
        lease.touched_real = now.real

    def pop_runnable(self, now: Now) -> list[tuple[WorkItem, Lease]]:
        """Most important first (class, then severity, then oldest): lease as many queued items as windows and the
        voice budget allow."""
        self.expire(now)
        started = []
        for item in sorted(self.queue, key=lambda q: (-q.cls, -q.severity, q.created.real, q.id)):
            # ambient chatter only fills slack: it needs a reserve of tokens, so the consequential lines are never starved by it
            if item.cls < Cls.URGENT and not self.budget.ready(now, self.AMBIENT_RESERVE if item.cls <= Cls.AMBIENT else 1.0):
                continue                      # the community has spoken enough for now; it waits, or expires unspoken
            lease = self.acquire(item, now)
            if lease:
                self.queue.remove(item)
                started.append((item, lease))
        return started

    # ---- skip-if-nothing-new -------------------------------------------
    def nothing_new(self, actor_id: int, signature: str) -> bool:
        return self.last_signature.get(actor_id) == signature

    def mark_spoke(self, actor_id: int, signature: str) -> None:
        self.last_signature[actor_id] = signature

    # ---- narrator -------------------------------------------------------
    def windows_idle(self, starving: bool = False) -> bool:
        """No window leased for real work and nothing queued. Starving idle work (SPEC §7) ignores ambient and social
        chatter, so a busy fast game still gets its diaries and chronicle."""
        floor = Cls.REACTIVE if starving else Cls.AMBIENT
        return (not any(l.cls >= max(floor, Cls.AMBIENT) for l in self.leases.values() if l.cls > Cls.IDLE)
                and not any(q.cls >= floor for q in self.queue))

    def narrator_may_do_idle_work(self, starving: bool = False) -> bool:
        """Chronicle, diary, reflection: only when no window is leased and nothing is queued."""
        return not self.narrator_busy and self.narrator_owner is None and self.windows_idle(starving)

    def narrator_should_cover(self, item: WorkItem, now: Now) -> bool:
        """Both windows busy, nothing preemptable, event still live: the narrator covers it."""
        if self.narrator_busy or self.narrator_owner is not None or item.expired(now) or self.free_slots():
            return False
        return not any(l.cls < item.cls for l in self.leases.values())

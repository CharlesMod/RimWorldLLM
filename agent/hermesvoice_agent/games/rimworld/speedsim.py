"""Speed-scaled simulation: one synthetic colony day (or a recorded events file) played at game speed 1-4.

The same events, in game ticks, are compressed in real time; the director runs unchanged against the fake
bridge with a fake LLM that has realistic latency (3 slots, jitter). Time is virtual: a loop whose clock jumps to
the next timer, so a day at speed 1 (1000 s) costs a second of CPU and results are deterministic.

``python -m hermesvoice_agent.games.rimworld.speedsim`` prints the table; tests/test_speed_sim.py asserts bounds.
"""
from __future__ import annotations

import asyncio
import random
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

import httpx

from ...core.bridge import BridgeClient
from ...core.config import Config
from ...core.director import Director
from ...core.fakes import FakeLLM
from .adapter import RimWorldAdapter
from .fakes import FakeBridge

DAY = 60000
TICKS_PER_S = {1: 60, 2: 180, 3: 360, 4: 900}


# ---- virtual time ----------------------------------------------------------------------------------------------
class VirtualLoop(asyncio.SelectorEventLoop):
    """An event loop whose clock only moves when everything is waiting: it jumps to the next timer."""

    def __init__(self) -> None:
        super().__init__()
        self._vt = 0.0
        real_select = self._selector.select

        def select(timeout=None):
            r = real_select(0)
            if not r and timeout:
                self._vt += timeout
            return r
        self._selector.select = select      # type: ignore[method-assign]

    def time(self) -> float:
        return self._vt


def run_virtual(coro):
    loop = VirtualLoop()
    try:
        return loop.run_until_complete(coro)
    finally:
        loop.close()


# ---- the model server -------------------------------------------------------------------------------------------
class SimLLM(FakeLLM):
    """FakeLLM text, with latency: 3 slots share the machine, so at most ``slots`` calls decode at once.
    Numbers from M0 on the reference model: line 0.1-0.25 s (0.3 cold), intent 0.11 s, long jobs seconds."""

    LAT = {"line": (0.10, 0.25), "intent": (0.09, 0.13), "memory": (0.15, 0.30), "relation": (0.15, 0.30),
           "narrator": (0.15, 0.30), "diary": (1.2, 2.5), "chronicle": (2.3, 3.5), "chronicle_summary": (1.5, 2.5),
           "reflect": (3.0, 5.0)}

    def __init__(self, rng: random.Random, slots: int = 3):
        super().__init__()
        self.rng, self.slots = rng, slots
        self.sem: asyncio.Semaphore | None = None
        self.in_flight = 0
        self.busy_s = 0.0
        self._last = 0.0
        self.max_in_flight = 0
        self.kinds: dict[str, int] = {}
        self.seen_slots: set[int] = set()

    def _tick(self, loop) -> None:
        t = loop.time()
        self.busy_s += self.in_flight * (t - self._last)
        self._last = t

    async def complete(self, messages, *, slot, kind="line", **kw):
        loop = asyncio.get_running_loop()
        if self.sem is None:
            self.sem = asyncio.Semaphore(self.slots)
        async with self.sem:
            self._tick(loop)
            self.in_flight += 1
            self.max_in_flight = max(self.max_in_flight, self.in_flight)
            try:
                lo, hi = self.LAT.get(kind, (0.1, 0.3))
                cold = slot is not None and slot not in self.seen_slots
                self.seen_slots.add(slot if slot is not None else -1)
                await asyncio.sleep(self.rng.uniform(lo, hi) + (0.15 if cold and kind == "line" else 0.0))
                self.kinds[kind] = self.kinds.get(kind, 0) + 1
                return await FakeLLM.complete(self, messages, slot=slot, kind=kind, **kw)
            finally:
                self._tick(loop)
                self.in_flight -= 1


# ---- the colony ------------------------------------------------------------------------------------------------
@dataclass
class SimEvent:
    tick: int
    type: str
    actors: list[tuple[int, str]]
    witnesses: list[int]
    severity: float
    summary: str
    data: dict = field(default_factory=dict)


def colony_day(seed: int = 3, pawns: int = 4, days: int = 1, raid_at: int = 30000, raid_size: int = 40) -> list[SimEvent]:
    """A synthetic colony day: thoughts, level ups, chitchat and a few consequential social interactions, injuries
    and tends, one mental break, and a raid (``raid_size`` events inside 1,080 ticks, three seconds at speed 3)."""
    r = random.Random(seed)
    ids = list(range(1, pawns + 1))
    ev: list[SimEvent] = []
    n = 0

    def near(who: list[int]) -> list[int]:
        return sorted(set(who) | {i for i in ids if r.random() < 0.6})

    def add(t, typ, actors, sev, summary, data=None, wit=None):
        nonlocal n
        n += 1
        ev.append(SimEvent(int(t), typ, actors, wit if wit is not None else near([a for a, _ in actors]), sev,
                           f"{summary} #{n}", data or {}))

    for d in range(days):
        base = d * DAY
        for _ in range(40):
            p = r.choice(ids)
            add(base + r.randrange(DAY), "thought", [(p, "subject")], r.uniform(0.2, 0.45), f"Pawn{p} had a passing thought")
        for _ in range(6):
            p = r.choice(ids)
            add(base + r.randrange(DAY), "level_up", [(p, "subject")], r.uniform(0.25, 0.45), f"Pawn{p} gained a skill level")
        for i in range(22):                       # the game's own interactions: chitchat (idle) and consequential (high)
            a, b = r.sample(ids, 2)
            high = i % 4 == 0
            add(base + r.randrange(DAY), "social", [(a, "initiator"), (b, "recipient")], 0.5 if high else 0.26,
                f"Pawn{a} and Pawn{b} spoke", {"vanilla": True, "ref": f"v{d}-{i}", "tier": "high" if high else "idle",
                                               "interaction": "Insult" if high else "Chitchat",
                                               "vanilla_text": "A sentence the game wrote.", "window_ticks": 417},
                wit=[a, b])
        for _ in range(8):                        # a pair meets and talks (a conversation)
            a, b = r.sample(ids, 2)
            add(base + r.randrange(DAY), "social", [(a, "initiator"), (b, "recipient")], 0.4,
                f"Pawn{a} and Pawn{b} fell into talk", wit=[a, b])
        for _ in range(8):
            p = r.choice(ids)
            t = base + r.randrange(DAY)
            add(t, "injury", [(p, "victim")], r.uniform(0.3, 0.7), f"Pawn{p} was hurt")
            add(t + r.randrange(200, 1500), "tend", [(r.choice(ids), "doctor"), (p, "patient")], 0.3, f"Pawn{p} was tended")
        add(base + 15000, "mental_break", [(2, "subject")], 0.8, "Pawn2 snapped")
        if d == 0:
            add(base + raid_at, "raid_start", [(1, "subject")], 0.95, "Raiders attack the colony", wit=ids)
            for _ in range(raid_size - 1):
                p = r.choice(ids)
                add(base + raid_at + r.randrange(1, 1080), r.choice(["injury", "injury", "thought", "tend"]),
                    [(p, "victim")], r.uniform(0.3, 0.8), f"Pawn{p} in the fight")
    return sorted(ev, key=lambda e: e.tick)


# ---- running ---------------------------------------------------------------------------------------------------
@dataclass
class Schedule:
    """Game speed over real time: [(real_s, speed, paused)], the first entry at 0."""
    steps: list[tuple[float, int, bool]]

    @classmethod
    def fixed(cls, speed: int) -> "Schedule":
        return cls([(0.0, speed, False)])

    def at(self, t: float) -> tuple[int, bool]:
        cur = self.steps[0]
        for s in self.steps:
            if s[0] <= t:
                cur = s
        return cur[1], cur[2]


class SimTransport(httpx.AsyncBaseTransport):
    """Frame-bound request latency on the mock bridge; records every /say with real and game time."""

    def __init__(self, fb: FakeBridge, rng: random.Random, attach: "AttachModel", loop_time):
        self.fb, self.rng, self.attach, self.now = fb, rng, attach, loop_time
        self.says: list[dict] = []
        self.thoughts: list[dict] = []      # every /thought: real time, game tick, ticks per real second then

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        await asyncio.sleep(self.rng.uniform(0.008, 0.03))
        is_say = request.method == "POST" and request.url.path == "/say"
        resp = self.fb.transport.handle_request(request)
        if request.method == "POST" and request.url.path == "/thought" and resp.status_code == 200:
            import json
            body = json.loads(request.content)
            if body.get("kind", "thought") == "thought":
                self.thoughts.append({"real": self.now(), "tick": self.fb.tick, "pawn": body.get("pawn"),
                                      "tps": float(TICKS_PER_S[self.fb.speed])})
        if is_say:
            import json
            body = json.loads(request.content)
            ok = resp.status_code == 200
            rec = {"real": self.now(), "tick": self.fb.tick, "body": body, "ok": ok, "status": resp.status_code}
            if ok and body.get("vanilla_ref"):
                rec["attach"] = self.attach.try_attach(body["vanilla_ref"], self.fb.tick, self.now())
            self.says.append(rec)
        return resp


def clouds_shown(thoughts: list[dict], *, real_gaps: bool, per_pawn_s: int = 120, global_s: int = 20) -> int:
    """The mod's ThoughtPacing as a model (no speech balloons in the way): a cloud shows when this pawn's and the
    global gap have passed. ``real_gaps`` False = the old rule (gaps in game ticks, so they shrink as speed rises);
    True = the gaps are real seconds at the current speed (ThoughtPacing.GapTicks)."""
    last: dict = {}
    last_g = -10**12
    n = 0
    for t in thoughts:
        scale = max(60.0, t["tps"]) if real_gaps else 60.0
        pp, gg = per_pawn_s * scale, global_s * scale
        if t["pawn"] in last and t["tick"] - last[t["pawn"]] < pp or t["tick"] - last_g < gg:
            continue
        last[t["pawn"]] = last_g = t["tick"]
        n += 1
    return n


@dataclass
class AttachModel:
    """The mod's pending table for the game's own interactions: a line attaches within the game-time window OR
    the real-time floor (``floor_s`` = 0 reproduces the old ticks-only rule)."""
    window_ticks: int = 417
    floor_s: float = 5.0
    reg: dict = field(default_factory=dict)
    results: dict = field(default_factory=dict)

    def register(self, ref: str, tick: int, real: float) -> None:
        self.reg[ref] = (tick, real)

    def try_attach(self, ref: str, tick: int, real: float) -> str:
        if ref not in self.reg or ref in self.results:
            return "unknown"
        t0, r0 = self.reg[ref]
        ok = tick - t0 <= self.window_ticks or (self.floor_s > 0 and real - r0 <= self.floor_s)
        self.results[ref] = "attached" if ok else "expired"
        return self.results[ref]


@dataclass
class Metrics:
    speed: str
    real_s: float
    game_ticks: int
    produced: int = 0
    ingested: int = 0
    lines: int = 0
    lines_per_game_hour: float = 0.0
    lines_per_real_min: float = 0.0
    dropped_late: int = 0
    late_share: float = 0.0
    delay_game_median: float = 0.0
    delay_game_p90: float = 0.0
    delay_game_max: float = 0.0
    delay_real_p90: float = 0.0
    convs_done: int = 0
    convs_cut: int = 0
    ring_gaps: int = 0
    events_lost: int = 0
    queue_max: int = 0
    queue_mean: float = 0.0
    slot_util: float = 0.0
    idle: dict = field(default_factory=dict)
    idle_days: dict = field(default_factory=dict)
    effects_for_dropped: int = 0
    memories_missed: int = 0
    memories_expected: int = 0
    vanilla_high: int = 0
    vanilla_high_attached: int = 0
    attach_share: float = 0.0
    drop_reasons: dict = field(default_factory=dict)
    say_refused: int = 0
    queue_expired: int = 0
    shed: int = 0
    lease_log: dict = field(default_factory=dict)
    said_by_kind: dict = field(default_factory=dict)
    thoughts: int = 0                  # thought jobs that reached the bridge
    clouds_old: int = 0                # clouds shown under tick-count gaps (before)
    clouds: int = 0                    # clouds shown under real-second gaps (now)
    clouds_per_real_min: float = 0.0
    crashed: str = ""


def _pct(xs: list[float], p: float) -> float:
    if not xs:
        return 0.0
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * len(xs)))]


async def _simulate(events: list[SimEvent], schedule: Schedule, cfg: Config, *, seed: int, pawns: int,
                    total_ticks: int, attach_floor_s: float, label: str, window_ticks: int = 417,
                    max_real_s: float = 4000.0) -> Metrics:
    loop = asyncio.get_running_loop()
    rng = random.Random(seed)
    fb = FakeBridge()
    fb.tick = 0
    for i in range(1, pawns + 1):
        fb.add_pawn(i, f"Pawn{i}")
    attach = AttachModel(window_ticks=window_ticks, floor_s=attach_floor_s)
    tr = SimTransport(fb, rng, attach, loop.time)
    adapter = RimWorldAdapter()
    bridge = BridgeClient(httpx.AsyncClient(transport=tr), "http://fake", fb.token, adapter.protocol)
    llm = SimLLM(random.Random(seed + 1))
    with tempfile.TemporaryDirectory(prefix="hermesvoice-speedsim-") as tmp:
        cfg = cfg.model_copy(update={"data_dir": Path(tmp)})
        d = Director(cfg, bridge, llm, adapter, clock=loop.time, rng=random.Random(seed + 2))
        # ---- probes (the director is not modified for them)
        delays_game: list[float] = []
        delays_real: list[float] = []
        ev_real: dict[int, float] = {}
        queue_samples: list[int] = []
        convs: list[tuple[str, int]] = []
        ingested: set[int] = set()
        mem_check = [0, 0]                         # witnessed (expected, missing) right after ingest
        orig_say, orig_fin, orig_ingest = d._say, d.finish_conversation, d.ingest

        async def spy_say(aid, sp, **kw):
            e = kw.get("event")
            if e is not None:
                delays_game.append(fb.tick - int(e.get("game", fb.tick)))
                delays_real.append(loop.time() - ev_real.get(int(e.get("seq", -1)), loop.time()))
            return await orig_say(aid, sp, **kw)

        async def spy_fin(conv, leases):
            convs.append((conv.ended, len(conv.turns)))
            return await orig_fin(conv, leases)

        async def spy_ingest(events_):
            for e in events_:
                ingested.add(int(e["seq"]))
            r = await orig_ingest(events_)
            for e in events_:                      # right after ingest: later upkeep may forget by design
                for w in e.get("witnesses", []):
                    if int(w) in d.minds:
                        mem_check[0] += 1
                        if not any(mm.event_seq == e["seq"] for mm in d.minds[int(w)].memories):
                            mem_check[1] += 1
            return r
        d._say, d.finish_conversation, d.ingest = spy_say, spy_fin, spy_ingest      # type: ignore[method-assign]
        await d.start()
        produced: list[dict] = []
        pending = list(events)
        t0 = loop.time()
        stop = asyncio.Event()

        async def agent():
            while not stop.is_set():
                try:
                    await d.step()
                except Exception as e:      # a crash is a result, not an exception
                    m.crashed = repr(e)
                    stop.set()
                    return
                queue_samples.append(len(d.leases.queue))
                await asyncio.sleep(d.poll_delay())

        m = Metrics(label, 0.0, 0)
        task = asyncio.ensure_future(agent())
        dt = 0.05
        while fb.tick < total_ticks and not stop.is_set() and loop.time() - t0 < max_real_s:
            speed, paused = schedule.at(loop.time() - t0)
            fb.speed, fb.paused = speed, paused
            if not paused:
                fb.tick += int(TICKS_PER_S[speed] * dt)
            while pending and pending[0].tick <= fb.tick:
                e = pending.pop(0)
                ev = fb.add_event(e.type, e.actors, e.summary, e.witnesses, e.severity, None, dict(e.data))
                ev["tick"] = e.tick
                ev_real[ev["seq"]] = loop.time()
                produced.append(ev)
                if e.data.get("vanilla"):
                    attach.register(e.data["ref"], e.tick, loop.time())
            await asyncio.sleep(dt)
        # let the tail drain
        for _ in range(60):
            await asyncio.sleep(1.0)
            if not d.tasks and not d.leases.queue and not d.idle.active():
                break
        stop.set()
        await asyncio.sleep(2.0)
        await d.drain()
        task.cancel()
        real = loop.time() - t0
        llm._tick(loop)

    # ---- numbers
    m.real_s, m.game_ticks = real, fb.tick
    m.produced, m.ingested = len(produced), len(ingested)
    ok_says = [s for s in tr.says if s["ok"]]
    m.lines = len(ok_says)
    m.lines_per_game_hour = m.lines / max(1e-9, fb.tick / (DAY / 24))
    m.lines_per_real_min = m.lines / max(1e-9, real / 60)
    late = [r for _, r in d.dropped if "expired" in r]
    q_expired = sum(1 for w, _ in d.leases.log if w == "expired")
    for w, why in d.leases.log:
        if w in ("drop", "merged", "expired"):
            k = f"{w}:{why.split(':')[0]}" if w != "expired" else "expired"
            m.lease_log[k] = m.lease_log.get(k, 0) + 1
    for s_ in ok_says:
        k = "vanilla" if s_["body"].get("vanilla_ref") else ("conv" if s_["body"].get("conversation_id") else "line")
        m.said_by_kind[k] = m.said_by_kind.get(k, 0) + 1
    m.queue_expired = q_expired
    m.thoughts = len(tr.thoughts)
    m.clouds_old = clouds_shown(tr.thoughts, real_gaps=False)
    m.clouds = clouds_shown(tr.thoughts, real_gaps=True)
    m.clouds_per_real_min = m.clouds / max(1e-9, real / 60)
    m.dropped_late = len(late)                 # generated, then too late to show
    m.shed = q_expired                         # never generated: expired in the queue behind more important lines
    m.late_share = m.dropped_late / max(1, m.dropped_late + m.lines)
    m.delay_game_median, m.delay_game_p90, m.delay_game_max = _pct(delays_game, .5), _pct(delays_game, .9), max(delays_game, default=0)
    m.delay_real_p90 = _pct(delays_real, .9)
    m.convs_done = sum(1 for r, _ in convs if r in ("chance", "turn_cap", "repetition"))
    m.convs_cut = sum(1 for r, _ in convs if r not in ("chance", "turn_cap", "repetition"))
    m.ring_gaps = d.events.gaps
    m.events_lost = len({e["seq"] for e in produced} - ingested)
    m.queue_max, m.queue_mean = max(queue_samples, default=0), sum(queue_samples) / max(1, len(queue_samples))
    m.slot_util = llm.busy_s / max(1e-9, real * llm.slots)
    for i in d.idle.log:
        m.idle[i["kind"]] = m.idle.get(i["kind"], 0) + 1
        day = int(i.get("game", 0)) // DAY
        m.idle_days.setdefault(i["kind"], set()).add(day if i["kind"] != "diary" else i.get("day", day))
    m.idle_days = {k: sorted(v) for k, v in m.idle_days.items()}
    said_texts = {(s["actor"], s["text"]) for s in d.said}
    m.effects_for_dropped = sum(1 for s in ok_says if s["body"].get("intent", "none") != "none" and (s["body"]["pawn"], s["body"]["text"]) not in said_texts)
    exp, miss = mem_check
    m.memories_expected, m.memories_missed = exp, miss
    hi = [e for e in events if e.data.get("vanilla") and e.data.get("tier") == "high"]
    m.vanilla_high = len(hi)
    m.vanilla_high_attached = sum(1 for e in hi if attach.results.get(e.data["ref"]) == "attached")
    m.attach_share = m.vanilla_high_attached / max(1, m.vanilla_high)
    for _, r in d.dropped:
        k = r.split(":")[0] if not r.startswith("idle_preempted") else "idle_preempted"
        m.drop_reasons[k] = m.drop_reasons.get(k, 0) + 1
    m.say_refused = len(tr.says) - len(ok_says)
    return m


def simulate(speed: int | Schedule = 1, *, events: list[SimEvent] | None = None, cfg: Config | None = None,
             seed: int = 3, pawns: int = 4, days: float = 1.5, attach_floor_s: float = 5.0, label: str = "") -> Metrics:
    sched = speed if isinstance(speed, Schedule) else Schedule.fixed(speed)
    evs = events if events is not None else colony_day(seed, pawns)
    return run_virtual(_simulate(evs, sched, cfg or Config(bridge_url="http://fake", bridge_token="tok"), seed=seed,
                                 pawns=pawns, total_ticks=int(days * DAY), attach_floor_s=attach_floor_s,
                                 label=label or (f"x{speed}" if isinstance(speed, int) else "sched")))


COLUMNS = [("lines", "lines"), ("lines_per_game_hour", "ln/ghr"), ("lines_per_real_min", "ln/min"), ("late_share", "late%"), ("shed", "shed"),
           ("delay_game_median", "dly50"), ("delay_game_p90", "dly90"), ("convs_done", "conv ok"), ("convs_cut", "cut"),
           ("events_lost", "lost"), ("ring_gaps", "gaps"), ("queue_max", "qmax"), ("slot_util", "util"),
           ("memories_missed", "memmiss"), ("attach_share", "attach"), ("effects_for_dropped", "fx-drop"),
           ("clouds_old", "cl-old"), ("clouds", "clouds"), ("clouds_per_real_min", "cl/min")]


def table(ms: list[Metrics]) -> str:
    rows = ["speed  real_s " + " ".join(f"{h:>8}" for _, h in COLUMNS) + "  idle(refl/diary/chron)"]
    for m in ms:
        cells = []
        for k, _ in COLUMNS:
            v = getattr(m, k)
            cells.append(f"{v:8.2f}" if isinstance(v, float) else f"{v:8d}")
        rows.append(f"{m.speed:<6} {m.real_s:6.0f} " + " ".join(cells) +
                    f"  {m.idle.get('reflection', 0)}/{m.idle.get('diary', 0)}/{m.idle.get('chronicle', 0)}"
                    + (f"  CRASH {m.crashed}" if m.crashed else ""))
    return "\n".join(rows)


def main() -> None:
    import sys
    floor = float(sys.argv[1]) if len(sys.argv) > 1 else 5.0
    ms = [simulate(s, attach_floor_s=floor) for s in (1, 2, 3, 4)]
    print(table(ms))
    for m in ms:
        print(m.speed, "drops:", m.drop_reasons, "lease:", m.lease_log, "said:", m.said_by_kind, "idle days:", m.idle_days)


if __name__ == "__main__":
    main()

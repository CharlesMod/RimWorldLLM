"""The speed simulation: the whole agent against a modelled game at 1x..4x on a virtual clock. Bounds, not exact
numbers: they hold the design goals in docs/SPEED.md (voiced at 3x, never stale, nothing lost from memory, safe at 4x)."""
from functools import lru_cache

import pytest

from hermesvoice_agent.games.rimworld import speedsim as ss

DAY = ss.DAY


@lru_cache(maxsize=None)
def day(speed: int, floor: float = 5.0):
    return ss.simulate(speed, attach_floor_s=floor)


@pytest.mark.parametrize("speed", [1, 2, 3, 4])
def test_nothing_lost_or_unsafe_at_any_speed(speed):
    m = day(speed)
    assert not m.crashed
    assert m.events_lost == 0 and m.ring_gaps == 0               # every event reached the agent, the ring never overran
    assert m.memories_missed == 0 and m.memories_expected > 0    # a witnessed event is always remembered
    assert m.effects_for_dropped == 0                            # no gameplay effect for a line that was dropped
    assert m.queue_max <= 16                                     # bounded queue
    assert m.say_refused == 0
    assert m.late_share <= 0.10                                  # lines that arrive too late are rare, shed in queue instead
    assert m.idle_days.get("reflection") and m.idle_days.get("chronicle")   # idle work still runs


def test_x3_is_voiced_about_like_x1_per_real_minute():
    a, c = day(1), day(3)
    assert c.lines_per_real_min >= 0.9 * a.lines_per_real_min
    assert 2.0 <= c.lines_per_real_min <= 6.0                    # a ceiling too: the budget, not a flood
    assert c.lines >= 12
    assert c.convs_cut == 0


def test_lines_are_never_stale_in_game_time():
    for sp, bound in ((1, 1500), (2, 3000), (3, 3200), (4, 4200)):
        m = day(sp)
        assert m.delay_game_p90 <= bound, (sp, m.delay_game_p90)
    assert day(3).delay_real_p90 <= 15.0                         # real seconds from event to line, at 3x


def test_x4_may_be_quiet_but_not_silent():
    m = day(4)
    assert 3 <= m.lines <= 40 and m.lines_per_real_min <= 6.0


def test_higher_speed_does_less_idle_work_per_real_time_but_never_none():
    for sp in (1, 2, 3, 4):
        m = day(sp)
        assert m.idle.get("diary", 0) >= 1 and m.idle.get("chronicle", 0) >= 1
        assert m.idle.get("reflection", 0) >= 4                   # at least one a pawn (4 pawns) over the run


def test_raid_burst_at_speed_3():
    evs = ss.colony_day(5, 4, raid_at=20000, raid_size=60)
    m = ss.simulate(3, events=evs, seed=5)
    assert not m.crashed and m.queue_max <= 16
    assert m.events_lost == 0 and m.memories_missed == 0 and m.effects_for_dropped == 0
    assert m.late_share <= 0.10


def test_pause_unpause_and_speed_changes():
    sched = ss.Schedule([(0.0, 1, False), (120.0, 3, False), (240.0, 3, True), (300.0, 2, False),
                         (360.0, 4, False), (400.0, 1, False), (500.0, 3, False)])
    m = ss.simulate(sched, seed=4)
    assert not m.crashed and m.queue_max <= 16
    assert m.events_lost == 0 and m.memories_missed == 0 and m.effects_for_dropped == 0
    assert m.late_share <= 0.10 and m.lines >= 8


def _sparse_vanilla(n=6, gap_ticks=8000):
    pairs = [(1, 2), (3, 4), (2, 3), (4, 1), (1, 3), (2, 4)]
    return [ss.SimEvent(2000 + i * gap_ticks, "social", [(pairs[i][0], "initiator"), (pairs[i][1], "recipient")], list(pairs[i]), 0.5,
                        f"Pawn{pairs[i][0]} and Pawn{pairs[i][1]} spoke #{i}",
                        {"vanilla": True, "ref": f"s{i}", "tier": "high", "interaction": "Insult",
                         "vanilla_text": "A sentence the game wrote.", "window_ticks": 417})
            for i in range(n)]


@pytest.mark.parametrize("speed", [1, 2, 3])
def test_vanilla_high_lines_that_are_sent_attach(speed):
    m = ss.simulate(speed, events=_sparse_vanilla(), days=1.0, attach_floor_s=5.0, pawns=4)
    sent = m.said_by_kind.get("vanilla", 0)
    assert m.vanilla_high == 6 and sent >= 3, (speed, m.drop_reasons)      # the rest are "nothing new" for that pawn
    assert m.vanilla_high_attached >= sent - 1, (speed, m.vanilla_high_attached, sent)


def test_x4_pauses_chatter_including_vanilla_voicing():
    m = ss.simulate(4, events=_sparse_vanilla(), days=1.0, attach_floor_s=5.0, pawns=4)
    assert not m.crashed and m.lines == 0 and m.memories_missed == 0     # quiet by design, memories still written


def test_real_time_floor_keeps_attaches_under_load():
    """A busy colony at 3x: the ticks-only window (417 ticks = 1.2 s) loses lines the real floor keeps."""
    old = sum(ss.simulate(3, attach_floor_s=0.0, seed=sd).vanilla_high_attached for sd in (3, 5, 6))
    new = sum(ss.simulate(3, attach_floor_s=5.0, seed=sd).vanilla_high_attached for sd in (3, 5, 6))
    assert new > old


def test_cloud_gaps_are_real_seconds():
    th = [{"real": i * 0.5, "tick": i * 450, "pawn": i % 6, "tps": 900.0} for i in range(400)]   # 4x, a thought every 0.5 s
    old = ss.clouds_shown(th, real_gaps=False)
    new = ss.clouds_shown(th, real_gaps=True)
    real_min = 400 * 0.5 / 60
    assert old / real_min > 20 and new / real_min <= 3.2          # 20 s global gap = at most 3 a real minute

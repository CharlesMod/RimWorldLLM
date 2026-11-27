"""Documentary voice (core/commentary.py, games/rimworld/moments.py, comment_try)."""
import json
from pathlib import Path

from conftest import run
from rimworldllm_agent.core.commentary import DEDUPE_GAME, MIN_WEIGHT
from rimworldllm_agent.core.fakes import FakeLLM
from rimworldllm_agent.games.rimworld import comment_try

D = 60000
SAMPLE = Path(__file__).parent / "data" / "comment_events_sample.json"


def ev(t, game, actors=(), data=None, summary="", **kw):
    return {"seq": game, "game": game, "type": t, "map": 0, "actors": [{"id": a, "role": "subject"} for a in actors],
            "summary": summary or f"{t} happened", "severity": 0.6, "witnesses": [], "data": data or {}, **kw}


def comments(w):
    return [e["text"] for e in w.fb.chat if e["who"] == "comment"]


async def boot(world, playing=True, settings=None, line="The colony endures another raid, as colonies do."):
    w = await world.go()
    w.llm.script["narrator"] = [line] * 20
    w.d.steward.vv = {"playing": playing, "settings": settings if settings is not None else {}}
    return w


async def tick(w, game, real):
    w.real = real
    w.d.bridge.env.game = game
    w.d.commentary.run_once()
    await w.d.drain()


def test_moments_picked_and_ignored(world):
    async def go():
        w = await boot(world)
        c = w.d.commentary
        got = set()
        for e in [ev("raid_start", 100), ev("death", 200, [2]), ev("birth", 300, data={"pawn": {"id": 9}}),
                  ev("gathering", 400, data={"kind": "wedding"}), ev("masterwork", 500, data={"thing": "x", "quality": "masterwork"}),
                  ev("ship_part", 600, data={"thing": "r"}), ev("colony_lost", 700), ev("funeral", 800),
                  ev("mental_break", 900, [1], data={"state": "SocialFighting"})]:
            c.note(e)
            got |= {m.kind for m in c.pending}
        assert got == {"raid_start", "death", "birth", "wedding", "masterwork", "ship_part", "colony_lost", "funeral", "social_fight"}
        c.pending.clear()
        for e in [ev("tend", 1000), ev("gathering", 1100, data={"kind": "meeting"}), ev("letter", 1200, summary="Trade caravan arrives"),
                  ev("social", 1300)]:
            c.note(e)
        assert c.pending == []
        c.note(ev("letter", 5000, summary="A wanderer joins the colony"))
        c.note(ev("letter", 9000, summary="Quest accepted: Bandit camp"))
        assert {m.kind for m in c.pending} == {"joiner", "quest"} and all(m.weight >= MIN_WEIGHT for m in c.pending)
    run(go())


def test_dedupe_window(world):
    async def go():
        w = await boot(world)
        c = w.d.commentary
        c.note(ev("death", 1000, [2]))
        c.note(ev("death", 1000 + DEDUPE_GAME - 1, [2]))
        c.note(ev("death", 1000, [3]))                       # another pawn: a new moment
        assert len(c.pending) == 2
        c.note(ev("death", 1000 + DEDUPE_GAME, [2]))
        assert len(c.pending) == 3
    run(go())


def test_posts_a_comment_line(world):
    async def go():
        w = await boot(world)
        w.d.commentary.note(ev("raid_start", 100, summary="Pirates attack from the east."))
        await tick(w, 100, 1000.0)
        assert comments(w) == ["The colony endures another raid, as colonies do."]
        call = [c for c in w.llm.calls if c["kind"] == "narrator"][-1]
        assert call["slot"] == w.cfg.narrator_slot and "Pirates attack" in call["messages"][1][1]
    run(go())


def test_rate_limits_and_urgent_bypass(world):
    async def go():
        w = await boot(world)
        c = w.d.commentary
        c.note(ev("funeral", 100, [1]))
        await tick(w, 100, 1000.0)
        assert len(comments(w)) == 1
        c.note(ev("ship_part", 5000, data={"thing": "a"}))             # normal: needs 180 s real AND 15000 game
        await tick(w, 5000, 1100.0)
        assert len(comments(w)) == 1
        await tick(w, 5000, 1300.0)                                    # real gap met, game gap not
        assert len(comments(w)) == 1
        c.note(ev("death", 5100, [2]))                                 # urgent: skips both gaps, but not the 30 s floor
        await tick(w, 5100, 1301.0)
        assert len(comments(w)) == 2 and c.log[-1]["kind"] == "death"
        await tick(w, 5200, 1600.0)                                    # the ship part still waits for the game gap
        assert len(comments(w)) == 2
        await tick(w, 5100 + 15000, 1900.0)
        assert len(comments(w)) == 3
    run(go())


def test_urgent_floor_real(world):
    async def go():
        w = await boot(world)
        c = w.d.commentary
        c.note(ev("death", 100, [1]))
        await tick(w, 100, 1000.0)
        c.note(ev("death", 200, [2]))
        await tick(w, 200, 1010.0)
        assert len(comments(w)) == 1
        await tick(w, 200, 1031.0)
        assert len(comments(w)) == 2
    run(go())


def test_only_when_playing_and_enabled(world):
    async def go():
        w = await boot(world, playing=False)
        w.d.commentary.note(ev("death", 100, [2]))
        await tick(w, 100, 1000.0)
        assert comments(w) == []
        w.d.steward.vv = {"playing": True, "settings": {"commentary": False}}
        await tick(w, 100, 1100.0)
        assert comments(w) == []
        w.d.steward.vv = {}                                           # no vivarium (older mod)
        await tick(w, 100, 1200.0)
        assert comments(w) == []
        w.d.steward.vv = {"playing": True, "settings": {"commentary": True}}
        w.cfg.commentary = False
        await tick(w, 100, 1300.0)
        assert comments(w) == []
        w.cfg.commentary = True
        await tick(w, 100, 1400.0)
        assert len(comments(w)) == 1
    run(go())


def test_waits_for_the_narrator_slot(world):
    async def go():
        w = await boot(world)
        w.d.commentary.note(ev("death", 100, [2]))
        w.d.leases.narrator_owner = "steward"
        await tick(w, 100, 1000.0)
        assert comments(w) == []
        w.d.leases.narrator_owner = None
        w.d.leases.narrator_busy = True                               # another slot-0 job
        await tick(w, 100, 1001.0)
        assert comments(w) == []
        w.d.leases.narrator_busy = False
        await tick(w, 100, 1002.0)
        assert len(comments(w)) == 1
    run(go())


def test_steward_preempts_a_running_comment(world):
    async def go():
        import asyncio
        w = await boot(world)
        gate = asyncio.Event()
        orig = w.llm.complete

        async def slow(*a, **k):
            await gate.wait()
            return await orig(*a, **k)
        w.llm.complete = slow
        w.d.commentary.note(ev("death", 100, [2]))
        w.real, w.d.bridge.env.game = 1000.0, 100
        w.d.commentary.run_once()
        await asyncio.sleep(0)
        assert w.d.commentary.active() and w.d.leases.narrator_busy
        w.d.leases.narrator_owner = "steward"                         # a steward cycle starts
        w.d.commentary.preempt_if_wanted()
        gate.set()
        await w.d.drain()
        assert comments(w) == [] and not w.d.leases.narrator_busy and not w.d.commentary.active()
        assert (-1, "commentary_preempted") in w.d.dropped
    run(go())


def test_recap_once_per_day(world):
    async def go():
        w = await boot(world, line="A day of small mercies.")
        c = w.d.commentary
        c.note(ev("death", D + 100, [2], summary="Bram died."))
        c.note(ev("birth", D + 5000, data={"pawn": {"id": 9}}, summary="A baby was born."))
        await tick(w, D + 5100, 1000.0)                               # the death speaks
        await tick(w, D + 6000, 1200.0)                               # the birth (urgent) speaks
        n = len(comments(w))
        await tick(w, D + 9000, 1400.0)
        assert len(comments(w)) == n                                  # same day: no recap
        await tick(w, 2 * D + 10, 1500.0)                             # the day turned
        assert [e["mode"] for e in c.log].count("recap") == 1
        recap_call = [x for x in w.llm.calls if x["kind"] == "narrator"][-1]
        assert "Bram died." in recap_call["messages"][1][1] and "day 2" in recap_call["messages"][1][1]
        await tick(w, 2 * D + 20, 1700.0)
        await tick(w, 3 * D + 20, 1900.0)                             # day 3 had no moments: nothing
        assert [e["mode"] for e in c.log].count("recap") == 1
    run(go())


def test_recap_off(world):
    async def go():
        w = await boot(world)
        w.cfg.commentary_recap = False
        w.d.commentary.note(ev("funeral", 100, [1]))
        await tick(w, 100, 1000.0)
        await tick(w, D + 5, 1500.0)
        assert [e["mode"] for e in w.d.commentary.log] == ["moment"]
    run(go())


def test_sanitize_and_age_gate_drop(world):
    async def go():
        w = await boot(world, line="I am sorry, as an AI language model I cannot comment on that.")
        w.d.commentary.note(ev("death", 100, [2]))
        await tick(w, 100, 1000.0)
        assert comments(w) == [] and w.d.commentary.drops
        w.llm.script["narrator"] = ["Bram kissed Ana passionately in the moonlight, naked."]
        w.d.names[1], w.d.name_tokens[1], w.d.ages[1] = "Ana", {"Ana"}, 12
        w.d.names[2], w.d.name_tokens[2], w.d.ages[2] = "Bram", {"Bram"}, 30
        w.d.commentary.note(ev("gathering", 70000, [1, 2], data={"kind": "wedding"}))
        await tick(w, 70000, 2000.0)
        assert comments(w) == []
        assert any(r[1].startswith("commentary:") for r in w.d.dropped)
    run(go())


def test_old_mod_without_comment_stops_asking(world):
    async def go():
        from rimworldllm_agent.core.bridge import BridgeError
        w = await boot(world)

        async def refuse(who, text, **k):
            raise BridgeError("bad_request", "who", status=400)
        w.d.bridge.post_chat = refuse
        w.d.commentary.note(ev("death", 100, [2]))
        await tick(w, 100, 1000.0)
        assert w.d.commentary.unsupported and not w.d.commentary.enabled()
    run(go())


def test_director_wires_events_into_commentary(world):
    async def go():
        w = await boot(world)
        w.fb.playing = True
        w.fb.push_event("death", actors=[{"id": 2, "role": "subject"}], summary="Bram died.", severity=1.0) \
            if hasattr(w.fb, "push_event") else None
        await w.d.ingest([ev("death", 100, [2], summary="Bram died.")])
        assert [m.kind for m in w.d.commentary.pending] == ["death"]
    run(go())


def test_comment_try_cli_on_sample():
    events = json.loads(SAMPLE.read_text())
    llm = FakeLLM(script={"narrator": [f"The colony faces moment number {i} with quiet dignity." for i in range(10)]})
    rows = run(comment_try.try_events(events, llm))
    lines = {r["kind"]: r["line"] for r in rows}
    for k in ("raid_start", "death", "birth", "gathering", "masterwork", "ship_part", "recap"):
        assert lines[k], k
    assert lines["tend"] is None
    assert sum(1 for r in rows if r["mode"] == "recap") == 1


def test_comment_try_main_prints(capsys):
    assert comment_try.main(["--events", str(SAMPLE)]) == 0
    out = capsys.readouterr().out
    assert "[raid_start]" in out and "[recap]" in out

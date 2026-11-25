"""Item 8: first-time reactions (core/firsts.py, the adapter's first_of table, Director.ingest/react)."""
from conftest import run
from hermesvoice_agent.core import firsts as F
from hermesvoice_agent.core.minds import Mind
from hermesvoice_agent.games.rimworld import RimWorldAdapter

AD = RimWorldAdapter()
DAY = 60000


async def step(w):
    await w.d.step(); await w.d.drain()


def cue(w, what="snow", witnesses=(1, 2, 3), actors=(), summary="It began to snow."):
    return w.fb.add_event("first_cue", list(actors), summary, list(witnesses), 0.4, data={"cue": what})


def line_calls(w):
    return [c for c in w.llm.calls if c["kind"] == "line"]


def test_first_of_table():
    assert AD.first_of({"type": "first_cue", "data": {"cue": "eclipse"}}) == ("eclipse", "seen an eclipse")
    assert AD.first_of({"type": "first_cue", "data": {"cue": "bogus"}}) is None
    assert AD.first_of({"type": "raid_start"}) == ("raid", "seen a raid")
    assert AD.first_of({"type": "death", "data": {"animal": False}})[0] == "colonist_death"
    assert AD.first_of({"type": "death", "data": {"animal": True}}) is None
    assert AD.first_of({"type": "injury"}) is None
    assert len({k for k in ("snow", "eclipse", "aurora", "solar_flare", "corpse", "beautiful_room", "no_table")}) == 7


def test_pick_speakers_is_stable_and_bounded():
    fm = {i: "x" for i in range(1, 9)}
    a = F.pick_speakers(fm, 5)
    assert len(a) == 2 and a == F.pick_speakers(fm, 5)
    c = F.DayCap()
    assert c.allow(1, 3, 1); c.spend(1, 3); assert not c.allow(1, 3, 1) and c.allow(1, 4, 1) and c.allow(2, 3, 1)


def test_first_snow_is_recorded_for_everyone_and_voiced_by_at_most_two(world):
    async def go():
        w = await world.go()
        cue(w)
        await step(w)
        return w
    w = run(go())
    assert all("first:snow" in w.d.minds[i].firsts for i in (1, 2, 3))
    calls = line_calls(w)
    assert 1 <= len(calls) <= F.MAX_SPEAKERS_PER_EVENT
    assert "The first time you have ever seen snow fall: It began to snow." in calls[0]["messages"][0][1]
    assert all(any(m.etype == "first_cue" and m.pinned for m in w.d.minds[i].memories) for i in (1, 2, 3))
    assert len(w.fb.say_log) == len(calls)


def test_second_occurrence_is_silent_and_writes_nothing(world):
    async def go():
        w = await world.go()
        cue(w)
        await step(w)
        n_calls, n_mem = len(w.llm.calls), {i: len(w.d.minds[i].memories) for i in (1, 2, 3)}
        w.fb.tick += 5000
        cue(w, summary="Snow fell again.")
        await step(w)
        return w, n_calls, n_mem
    w, n_calls, n_mem = run(go())
    assert len(w.llm.calls) == n_calls and {i: len(w.d.minds[i].memories) for i in (1, 2, 3)} == n_mem


def test_firsts_per_actor_per_day_cap(world):
    async def go():
        w = await world.go()
        w.cfg.firsts_per_day = 1
        for what in ("snow", "eclipse", "aurora"):
            cue(w, what, witnesses=(1,), summary=f"A {what}.")
            w.fb.tick += 400
            await step(w)
        return w
    w = run(go())
    assert set(w.d.minds[1].firsts) >= {"first:snow", "first:eclipse", "first:aurora"}   # recorded even when not voiced
    assert len(line_calls(w)) == 1                                                       # one first-time line that day


def test_recorded_even_when_the_line_is_dropped(world):
    async def go():
        w = await world.go()
        w.llm.fail = True
        cue(w, "corpse", witnesses=(1,), summary="Ana saw a corpse.")
        await step(w)
        return w
    w = run(go())
    assert "first:corpse" in w.d.minds[1].firsts and not w.fb.say_log


def test_switch_off_is_todays_behaviour(world):
    async def go():
        world.cfg.firsts = False
        w = await world.go()
        cue(w)
        w.fb.add_event("raid_start", [], "A raid.", [1], 0.8, data={})
        await step(w)
        return w
    w = run(go())
    assert not any(x.startswith("first:") for x in w.d.minds[1].firsts)
    assert "first_cue" not in w.d.minds[1].firsts                       # the signal event never reached the memory
    assert not any("first time" in c["messages"][0][1] for c in line_calls(w))


def test_existing_event_types_get_the_first_time_trigger_once(world):
    async def go():
        w = await world.go()
        w.fb.add_event("raid_start", [], "Raid by pirates.", [1], 0.8, data={})
        await step(w)
        first = [c for c in line_calls(w) if "first time you have ever seen a raid" in c["messages"][0][1]]
        w.fb.tick += 70000
        w.fb.add_event("raid_start", [], "Another raid.", [1], 0.8, data={})
        await step(w)
        again = [c for c in line_calls(w) if "Another raid" in c["messages"][0][1]]
        return w, first, again
    w, first, again = run(go())
    assert first and "first:raid" in w.d.minds[1].firsts
    assert again and not any("first time" in c["messages"][0][1] for c in again)


def test_animal_death_is_not_a_colonist_first(world):
    async def go():
        w = await world.go()
        w.fb.add_event("death", [(3, "victim")], "A muffalo died.", [1], 1.0, data={"animal": True})
        await step(w)
        return w
    w = run(go())
    assert "first:colonist_death" not in w.d.minds[1].firsts


def test_blob_round_trip_keeps_firsts():
    m = Mind(actor_id=1, firsts=["raid", "first:snow"])
    assert Mind.from_blob(m.to_blob(), 1, "Ana").firsts == ["raid", "first:snow"]

"""Aliveness item 4: colony lore coined after a major entry, stored as chronicle kind "lore", reused in prompts."""
from conftest import World, run
from hermesvoice_agent.core import lore, recap
from hermesvoice_agent.core.lore import LoreBook, parse_entry

DEATH = "Bram died of his wounds in the evening, and the others buried him without speeches."


async def step(w):
    w.real += 30.0
    await w.d.step(); await w.d.drain()


def lore_calls(w):
    return [c for c in w.llm.calls if c["kind"] == "lore"]


def test_parse_entry():
    assert parse_entry("The Long Winter: the months the food ran short.") == ("The Long Winter", "the months the food ran short")
    assert parse_entry("none") is None and parse_entry("Winter: cold") is None          # a name is 2-5 words
    assert parse_entry("no colon here at all") is None and parse_entry("One Two Three Four Five Six: x") is None


def test_lore_book_caps_dedupes_and_matches_the_scene():
    b = LoreBook(3)
    for i in range(5):
        b.add(f"Name Number{i}", f"thing{i}", i)
    assert [e.name for e in b.entries] == ["Name Number2", "Name Number3", "Name Number4"]
    b.add("name number3", "again", 9)
    assert len(b.entries) == 3 and b.entries[-1].what == "again"
    b.add("The Old Mill", "where the first raid hit", 10)
    assert [e.name for e in b.relevant("they met at the mill by the east gate")] == ["The Old Mill"]
    assert b.relevant("nothing alike here") == [] and b.names_line("nothing alike here") == ""
    line = b.names_line("the mill raid", label="Colony names")
    assert line.startswith("Colony names: The Old Mill")


def test_coined_after_a_major_event_stored_cached_and_reused(world):
    async def go():
        w = await world.go()
        w.llm.script["chronicle"] = [DEATH]
        w.llm.script["lore"] = ["The Hollow Funeral: Bram's burial without speeches"]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1, 3], 1.0)
        await step(w); await step(w)
        calls = lore_calls(w)
        assert len(calls) == 1 and calls[0]["slot"] == w.cfg.narrator_slot
        assert w.d.lore.lines() == ["The Hollow Funeral: Bram's burial without speeches"]
        stored = [e for e in w.fb.chronicle if e["kind"] == "lore"]
        assert stored and stored[0]["text"] == "The Hollow Funeral: Bram's burial without speeches"
        assert [e["kind"] for e in w.fb.chronicle].count("major") == 1
        # a later chronicle prompt carries the names, with data rule wrapping
        w.llm.script["chronicle"] = [DEATH]
        w.d.chronicle.note(w.d.bridge.env.game, "Cy was hurt.", 1.0, True)
        w.llm.script["lore"] = ["none"]
        await step(w); await step(w)
        user = [c for c in w.llm.calls if c["kind"] == "chronicle"][-1]["messages"][1][1]
        assert "SHARED NAMES" in user and "The Hollow Funeral" in user
        # block D gets the matching names
        sc = type("S", (), {"where": "the hall", "present": []})()
        assert w.d._common_talk(1, "the funeral of Bram", sc, {1}, None)[0].startswith("Colony names: The Hollow Funeral")
        assert w.d._common_talk(1, "supper", sc, {1}, None) == []
        return w
    run(go())


def test_lore_costs_nothing_for_day_entries_and_none_stores_nothing(world):
    async def go():
        w = await world.go()
        w.llm.script["chronicle"] = [DEATH]
        w.llm.script["lore"] = ["none"]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1, 3], 1.0)
        await step(w); await step(w)
        assert len(lore_calls(w)) == 1 and not w.d.lore.entries
        assert not [e for e in w.fb.chronicle if e["kind"] == "lore"]
    run(go())


def test_lore_off_is_todays_behaviour(tmp_path):
    w = World(tmp_path); w.cfg.lore = False
    async def go():
        await w.go()
        w.llm.script["chronicle"] = [DEATH]
        w.llm.script["lore"] = ["The Hollow Funeral: a burial"]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1, 3], 1.0)
        await step(w); await step(w)
        assert not lore_calls(w) and not w.d.lore.entries
        user = [c for c in w.llm.calls if c["kind"] == "chronicle"][0]["messages"][1][1]
        assert "SHARED NAMES" not in user
    run(go())


def test_unusable_lore_is_dropped_minors_and_romance(world):
    async def go():
        w = await world.go()
        w.d.ages[1] = 12                                   # Ana is a minor
        for bad in ["I'm sorry, I can't do that: refusing", "Cy And Ana: their lovers tryst in the barn", "Just a plain sentence."]:
            w.llm.script["lore"] = [bad]
            assert await lore.coin(w.d, DEATH, 5, 0) is None
        assert not w.d.lore.entries
    run(go())


def test_lore_survives_in_the_save_and_reloads(world):
    async def go():
        w = await world.go()
        w.fb.chronicle.append({"day": 2, "kind": "lore", "text": "Bram's Folly: the half-built tower", "tick": 5})
        w.fb.chronicle.append({"day": 3, "kind": "day", "text": "A quiet day passed.", "tick": 6})
        w.fb.reload_game()
        await w.d.step()                                   # the epoch changed: lore is read back from the mod
        assert w.d.lore.lines() == ["Bram's Folly: the half-built tower"]
    run(go())


def test_recap_ignores_lore_entries(world):
    async def go():
        w = await world.go()
        w.fb.chronicle += [{"day": 0, "kind": "day", "text": "Ana dug the first trench.", "tick": 1},
                           {"day": 1, "kind": "lore", "text": "Bram's Folly: the half-built tower", "tick": 2},
                           {"day": 2, "kind": "lore", "text": "The Long Winter: the hungry months", "tick": 3}]
        w.llm.script["recap"] = ["Ana dug the first trench.\nThe days went by.\nWork continued."]
        w.fb.reload_game()
        await w.d.step(); await w.d.drain()
        assert not [c for c in w.llm.calls if c["kind"] == "recap"] and not w.fb.recap_log   # one real entry: no recap
        w.fb.chronicle.append({"day": 3, "kind": "day", "text": "Cy was hurt by raiders.", "tick": 4})
        w.fb.reload_game()
        await w.d.step(); await w.d.drain()
        user = [c for c in w.llm.calls if c["kind"] == "recap"][0]["messages"][1][1]
        assert "Ana dug" in user and "Cy was hurt" in user and "Folly" not in user and "Winter" not in user
    run(go())

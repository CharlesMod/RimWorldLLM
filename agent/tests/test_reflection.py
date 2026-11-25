"""Step 1: reflection. One structured call, idle window, block C changes only here."""
import json

from conftest import run
from hermesvoice_agent.core.lease import Cls, Lease, Now
from hermesvoice_agent.core.memory import record_told_line  # noqa: F401
from hermesvoice_agent.core.minds import BIO_HISTORY_KEEP, Memory, Mind, Relation
from hermesvoice_agent.core.prompt import block_c
from hermesvoice_agent.core.concerns import reflect_schema
from hermesvoice_agent.core.reflection import (REFLECT_SCHEMA, ReflectMerge, ReflectRelation, ReflectResult,
                                               apply_reflection, parse_result, reflection_due)

DAY = 60000


def result(bio="I am harder than I was, and tired of the cold.", wants=("Get warm",), rels=(), merge=()):
    return json.dumps({"bio": bio, "wants": list(wants), "relations": list(rels), "merge": list(merge)})


def fresh_lease():
    return Lease(slot=1, actor_id=1, cls=Cls.AMBIENT, kind="x", started=Now(0, 0), touched_real=0)


def test_reflection_due_on_importance_or_interval():
    m = Mind(actor_id=1)
    assert not reflection_due(m, 10 * DAY, DAY, 3.0, 3.0)            # nothing new: never
    m.unreflected_importance = 0.5
    assert not reflection_due(m, 2 * DAY, DAY, 3.0, 3.0)
    assert reflection_due(m, 3 * DAY, DAY, 3.0, 3.0)                 # the interval ran out
    m.last_reflect_time = 3 * DAY
    assert not reflection_due(m, 4 * DAY, DAY, 3.0, 3.0)
    m.unreflected_importance = 3.0
    assert reflection_due(m, 4 * DAY, DAY, 3.0, 3.0)                 # importance passed the threshold


def test_parse_result_tolerates_fences_and_junk():
    assert parse_result("```json\n" + result() + "\n```").wants == ["Get warm"]
    assert parse_result("no json here") is None
    assert parse_result('{"bio": 3}') is None
    assert set(REFLECT_SCHEMA["required"]) == {"bio", "wants", "relations", "merge"}


def test_apply_reflection_bio_history_wants_relations_merge():
    m = Mind(actor_id=1, name="Ana")
    m.relations["2"] = Relation(name="Bram", opinion=-30)
    for i in range(3):
        m.memories.append(Memory(id=f"m{i}", game=i, text=f"I saw Bram snub me {i}", importance=0.3, people=[2]))
    m.memories.append(Memory(id="m9", game=9, text="Cy died.", importance=1.0, pinned=True))
    m.unreflected_importance = 4.0
    res = ReflectResult(
        bio="I am wary of Bram now.", wants=["a", "b", "c", "d"],
        relations=[ReflectRelation(who="Bram", feeling="cold toward him", history="he keeps snubbing me"),
                   ReflectRelation(who="Nobody", feeling="x", history="y")],
        merge=[ReflectMerge(ids=["m0", "m1", "m2"], summary="Bram snubbed me again and again."),
               ReflectMerge(ids=["m0", "m9"], summary="should be refused, m9 is pinned")])
    out = apply_reflection(m, res, 5 * DAY, {"bram": 2, "ana": 1})
    assert m.evolving_bio == "I am wary of Bram now." and m.bio_history[-1].rev == 1
    assert m.bio_history[-1].game == 5 * DAY and m.wants == ["a", "b", "c"]
    assert m.relations["2"].feeling == "cold toward him" and "Nobody" not in {r.name for r in m.relations.values()}
    assert out["merged"] == 3 and {x.id for x in m.memories} >= {"m9"}
    assert any(x.summary_of == 3 for x in m.memories) and not any(x.id == "m0" for x in m.memories)
    assert m.unreflected_importance == 0 and m.last_reflect_time == 5 * DAY
    assert m.c_relations and m.c_relations[0].name == "Bram"


def test_bio_history_capped_at_ten_with_revs():
    m = Mind(actor_id=1)
    for i in range(14):
        m.add_bio_diff(i * 100, f"bio {i}")
    assert len(m.bio_history) == BIO_HISTORY_KEEP == 10
    assert [d.rev for d in m.bio_history] == list(range(5, 15))
    assert m.undo_bio() and m.evolving_bio == "bio 12"


def test_prefix_bytes_unchanged_until_a_reflection_runs(world):
    """A-E for a lease: relation updates, opinion changes and new relation lines do not move block C."""
    async def go():
        w = await world.go()
        d = w.d
        mind = d.minds[1]
        mind.memories.append(Memory(id="m1", game=500, text="I burned the stew.", importance=0.5, people=[1]))
        ctx, _, _ = await d._prepare(1, "Ana looks around.", None, fresh_lease())
        l0 = fresh_lease(); await d._prepare(1, "Ana looks around.", None, l0)
        p0, c0 = l0.data["prefix"], block_c(mind)
        # what ordinary play does: conversations rewrite relation lines, the game moves opinions
        mind.relations["2"] = Relation(name="Bram", feeling="irritated", history="he laughed at me", opinion=-40)
        mind.unreflected_importance += 2.0
        w.fb.pawns[1]["relations"] = [{"pawn": 2, "opinion": -60}]
        l1 = fresh_lease(); await d._prepare(1, "Ana looks around.", None, l1)
        assert l1.data["prefix"] == p0 and block_c(mind) == c0
        # a reflection runs: only now does block C change, and only block C
        w.llm.script["reflect"] = [result(bio="I have stopped trusting Bram.", wants=["Avoid Bram"],
                                          rels=[{"who": "Bram", "feeling": "I do not trust him", "history": "he laughed at me"}])]
        mind.unreflected_importance = 5.0
        await d.step(); await d.drain()
        l2 = fresh_lease(); await d._prepare(1, "Ana looks around.", None, l2)
        p2, c2 = l2.data["prefix"], block_c(mind)
        assert c2 != c0 and "stopped trusting Bram" in c2 and "Avoid Bram" in c2 and "do not trust him" in c2
        assert p2 == p0.replace(c0, c2)
        return w
    run(go())


def test_reflection_runs_in_an_idle_window_with_a_schema_and_is_saved(world):
    async def go():
        w = await world.go()
        w.llm.script["reflect"] = [result()]
        w.d.minds[1].unreflected_importance = 9.0
        await w.d.step(); await w.d.drain()
        await w.d.step(); await w.d.drain()          # the next step saves the dirty mind
        return w
    w = run(go())
    calls = [c for c in w.llm.calls if c["kind"] == "reflect"]
    assert len(calls) == 1 and calls[0]["json_schema"] == reflect_schema(REFLECT_SCHEMA, True) and calls[0]["slot"] in (1, 2)
    assert w.d.minds[1].evolving_bio.startswith("I am harder")
    assert w.d.idle.log[0]["kind"] == "reflection" and w.d.idle.log[0]["actor"] == 1
    blob, _ = w.fb.minds[1]
    assert blob["evolving_bio"].startswith("I am harder") and blob["bio_history"][0]["rev"] == 1


def test_reflection_by_interval_and_failure_backs_off(world):
    async def go():
        w = await world.go()
        m = w.d.minds[2]
        m.unreflected_importance = 0.5
        await w.d.step(); await w.d.drain()
        assert not [c for c in w.llm.calls if c["kind"] == "reflect"]       # too soon
        w.fb.tick = 4 * DAY
        w.llm.script["reflect"] = ["not json"]
        await w.d.step(); await w.d.drain()
        assert sum(c["kind"] == "reflect" for c in w.llm.calls) == 1
        assert m.unreflected_importance == 0.5 and (2, "reflect_failed") in w.d.dropped
        await w.d.step(); await w.d.drain()                                 # backed off: no hammering
        assert sum(c["kind"] == "reflect" for c in w.llm.calls) == 1
        w.real += 100
        await w.d.step(); await w.d.drain()
        assert sum(c["kind"] == "reflect" for c in w.llm.calls) == 2
        assert m.unreflected_importance == 0 and m.last_reflect_time >= 4 * DAY
    run(go())


def test_ungrounded_bio_is_rejected(world):
    async def go():
        w = await world.go()
        w.llm.script["reflect"] = [result(bio="I have grown close to Zorbax Quillfeather.")]
        w.d.minds[1].unreflected_importance = 9.0
        await w.d.step(); await w.d.drain()
        assert w.d.minds[1].evolving_bio == "" and w.d.minds[1].unreflected_importance == 9.0
    run(go())


# ---- the player's edits survive a PUT /mind conflict ----------------------------------------
def test_player_notes_win_on_conflict_and_feed_block_c(world):
    async def go():
        w = await world.go()
        d = w.d
        d.minds[1].evolving_bio = "mine"
        d.dirty.add(1)
        blob, rev = w.fb.minds.get(1, (None, 0))
        # the player edits notes in the mod: blob rev moves on, notes_edited_by_player set
        theirs = dict(d.minds[1].to_blob(0, DAY)); theirs.update(notes="Never forgives the raid.", notes_rev=3,
                                                                 notes_edited_by_player=True, evolving_bio="stale")
        w.fb.minds[1] = (theirs, rev + 5)
        await d.save_dirty()
        saved, _ = w.fb.minds[1]
        assert saved["notes"] == "Never forgives the raid." and saved["notes_rev"] == 3
        assert saved["notes_edited_by_player"] is True and saved["evolving_bio"] == "mine"   # ours merged
        assert d.minds[1].notes == "Never forgives the raid." and 1 not in d.dirty
        assert "Never forgives the raid." in block_c(d.minds[1])
        # without the flag the agent's blob is written as before
        theirs2 = dict(saved); theirs2.update(notes="agent junk", notes_edited_by_player=False)
        w.fb.minds[1] = (theirs2, w.fb.minds[1][1] + 1)
        d.minds[1].notes = "ours"; d.dirty.add(1)
        await d.save_dirty()
        assert w.fb.minds[1][0]["notes"] == "ours"
    run(go())


def test_player_voice_edit_wins_on_conflict(world):
    async def go():
        w = await world.go()
        d = w.d
        d.dirty.add(2)
        _, rev = w.fb.minds.get(2, (None, 0))
        theirs = d.minds[2].to_blob(0, DAY)
        theirs["voice"] = dict(theirs["voice"], register_line="Mumbles in rhyme.", edited_by_player=True)
        w.fb.minds[2] = (theirs, rev + 2)
        d.minds[2].wants = ["ours"]
        await d.save_dirty()
        saved, _ = w.fb.minds[2]
        assert saved["voice"]["register_line"] == "Mumbles in rhyme." and saved["voice"]["edited_by_player"]
        assert saved["wants"] == ["ours"] and d.minds[2].voice.edited_by_player
    run(go())

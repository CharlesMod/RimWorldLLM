"""Item 7: the voice card's tics evolve slowly at reflection (core/habits.py)."""
import json

from conftest import run
from rimworldllm_agent.core import habits as H
from rimworldllm_agent.core.config import Config
from rimworldllm_agent.core.minds import Mind, VoiceCard
from rimworldllm_agent.core.prompt import block_b
from rimworldllm_agent.core.reflection import (REFLECT_SCHEMA, ReflectResult, apply_reflection, parse_result,
                                               reflection_messages, schema_for)
from rimworldllm_agent.games.rimworld import RimWorldAdapter

DAY = 60000
AD = RimWorldAdapter()


def mind(tics=("Short sentences", "Dry jokes", "Sighs first"), **kw):
    return Mind(actor_id=1, name="Ana", voice=VoiceCard(register_line="dry", tics=list(tics), **kw))


def reply(habit="mutters about the cold"):
    return json.dumps({"bio": "The recent days have left a mark on me, and I am changing slowly.", "wants": ["Get warm"],
                       "relations": [], "merge": [], "habit": habit})


def test_replaces_the_oldest_tic_and_is_undoable():
    m = mind()
    assert H.apply_habit(m, "mutters about the cold", 5 * DAY, DAY, 3.0)
    assert m.voice.tics == ["Dry jokes", "Sighs first", "mutters about the cold"]
    assert m.habit_history[-1].removed == "Short sentences" and m.last_habit_game == 5 * DAY
    assert "mutters about the cold" in block_b(m) and "Short sentences" not in block_b(m)
    assert H.undo_habit(m)
    assert m.voice.tics == ["Short sentences", "Dry jokes", "Sighs first"] and m.last_habit_game == -1
    assert not H.undo_habit(m)


def test_appends_while_fewer_than_three_tics():
    m = mind(tics=("Short sentences",))
    assert H.apply_habit(m, "hums when tired", DAY, DAY, 3.0)
    assert m.voice.tics == ["Short sentences", "hums when tired"] and m.habit_history[-1].removed == ""
    assert H.undo_habit(m) and m.voice.tics == ["Short sentences"]


def test_at_most_once_per_interval_and_never_after_a_player_edit_and_no_repeats():
    m = mind()
    assert H.apply_habit(m, "one", 5 * DAY, DAY, 3.0)
    assert not H.apply_habit(m, "two", 6 * DAY, DAY, 3.0) and "two" not in m.voice.tics   # too soon
    assert H.apply_habit(m, "two", 8 * DAY, DAY, 3.0)
    assert not H.apply_habit(m, "TWO", 20 * DAY, DAY, 3.0)                                 # already a tic
    p = mind(edited_by_player=True)
    assert not H.habit_allowed(p, 99 * DAY, DAY, 3.0) and not H.apply_habit(p, "x y", 99 * DAY, DAY, 3.0)
    assert p.voice.tics == ["Short sentences", "Dry jokes", "Sighs first"]


def test_history_is_capped_and_trimmed_in_the_blob():
    m = mind()
    for i in range(10):
        H.apply_habit(m, f"habit number {i}", (10 * i + 5) * DAY, DAY, 3.0)
    assert len(m.habit_history) == 6 and len(m.voice.tics) == 3
    blob = m.to_blob(100 * DAY, DAY, limit=1)       # force every trim step
    assert blob["habit_history"] == [] and Mind.from_blob(blob, 1, "Ana").habit_history == []
    assert Mind.from_blob(None, 1, "Ana").last_habit_game == -1       # an old blob loads with the defaults


def test_schema_and_prompt_only_when_habits_asked():
    assert schema_for(False) is REFLECT_SCHEMA and "habit" not in schema_for(False)["properties"]
    s = schema_for(True)
    assert "habit" in s["properties"] and s["required"][-1] == "habit" and "habit" not in REFLECT_SCHEMA["properties"]
    m = mind()
    off = reflection_messages(AD, m, {1: "Ana"}, DAY, DAY)[1][1]
    on = reflection_messages(AD, m, {1: "Ana"}, DAY, DAY, habits=True)[1][1]
    assert "habit" not in off.lower() and "YOUR VERBAL HABITS NOW" in on and "Dry jokes" in on
    assert 20 <= (len(on.split()) - len(off.split())) * 1.3 <= 110            # token estimate: words x 1.3
    assert parse_result(reply()).habit == "mutters about the cold" and parse_result(
        '{"bio": "x y z w", "wants": [], "relations": [], "merge": []}').habit == ""


def test_apply_reflection_applies_habit_only_when_enabled():
    res = ReflectResult(bio="I am quieter now than before.", wants=["a"], habit="mutters about the cold")
    m = mind()
    assert "habit" not in apply_reflection(m, res, 5 * DAY, {})          # habits off: a stray field is ignored
    assert m.voice.tics[0] == "Short sentences"
    out = apply_reflection(m, res, 5 * DAY, {}, habit_every_days=3.0, day=DAY)
    assert out["habit"] == "mutters about the cold" and m.voice.tics[-1] == "mutters about the cold"


def test_end_to_end_reflection_changes_a_tic_then_waits(world):
    async def go():
        w = await world.go()
        m = w.d.minds[1]
        before = list(m.voice.tics)
        w.llm.script["reflect"] = [reply(), reply("sighs before every answer")]
        m.unreflected_importance = 9.0
        await w.d.step(); await w.d.drain()
        first = list(m.voice.tics)
        w.fb.tick += DAY
        m.unreflected_importance = 9.0
        m.last_reflect_time = 0
        w.fb.tick += DAY // 2
        await w.d.step(); await w.d.drain()                              # inside the interval: not asked, no change
        return w, before, first
    w, before, first = run(go())
    calls = [c for c in w.llm.calls if c["kind"] == "reflect"]
    assert first[-1] == "mutters about the cold" and first != before
    assert "habit" in calls[0]["json_schema"]["properties"]
    assert w.d.minds[1].voice.tics == first
    if len(calls) > 1:
        assert "habit" not in calls[1]["json_schema"]["properties"]


def test_off_switch_is_todays_call_and_ungrounded_or_bad_habits_are_blanked(world):
    async def go(habits, text):
        w = world
        w.cfg.habits = habits
        w.cfg.concerns = False          # isolate the habit switch (concerns add their own schema fields)
        await w.go()
        w.llm.script["reflect"] = [reply(text)]
        w.d.minds[1].unreflected_importance = 9.0
        await w.d.step(); await w.d.drain()
        return w
    w = run(go(False, "mutters about the cold"))
    c = [x for x in w.llm.calls if x["kind"] == "reflect"][0]
    assert c["json_schema"] is REFLECT_SCHEMA and "habit" not in c["messages"][1][1].lower()
    assert w.d.minds[1].voice.tics[0] == "Short sentences" or len(w.d.minds[1].habit_history) == 0
    assert not w.d.minds[1].habit_history


def test_bad_habits_dropped(world):
    async def go(text):
        w = world
        await w.go()
        w.llm.script["reflect"] = [reply(text)]
        w.d.minds[1].unreflected_importance = 9.0
        await w.d.step(); await w.d.drain()
        return w
    w = run(go("Zed Quillon taught me to whistle"))      # an invented name: blanked, the bio is still kept
    assert not w.d.minds[1].habit_history and w.d.minds[1].evolving_bio.startswith("The recent days")

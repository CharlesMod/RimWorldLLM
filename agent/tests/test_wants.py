"""Wants with follow-through: one small social action through the tool loop, executed only through the bridge."""
import json

from conftest import run
from rimworldllm_agent.core.toolloop import Reply
from rimworldllm_agent.core.wants import run_wants


class Fake:
    def __init__(self, script):
        self.script, self.calls = list(script), []

    async def chat(self, messages, *, grammar=None, tools=None, tool_choice=None, max_tokens=160, temperature=0.2, timeout=30.0):
        self.calls.append({"messages": messages, "grammar": grammar})
        item = self.script.pop(0) if len(self.script) > 1 else self.script[0]
        return item if isinstance(item, Reply) else Reply(text=item)


def call(tool, **a):
    return json.dumps({"tool": tool, "args": a})


DONE = json.dumps({"done": True})


async def _go(world, script, ages=None, scene_present=None):
    w = world
    for pid, a in (ages or {}).items():
        w.fb.pawns[pid]["age"] = a
    if scene_present is not None:
        w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear", "present": scene_present}
    await w.go()
    fake = Fake(script)
    out = await run_wants(w.d, 1, fake, "grammar")
    return w, fake, out


def test_seek_out_goes_through_the_bridge(world):
    w, f, out = run(_go(world, [call("seek_out", target="Bram", topic="the spare medicine"), DONE]))
    assert [x["target"] for x in w.fb.social_log] == [2] and w.fb.social_log[0]["action"] == "seek_out"
    assert w.fb.social_log[0]["topic"] == "the spare medicine" and w.fb.social_log[0]["pawn"] == 1
    assert w.d.wants.acted == 1 and out["executed"][0][0] == "seek_out"


def test_at_most_one_social_action(world):
    w, f, out = run(_go(world, [call("seek_out", target="Bram", topic="bread"), call("avoid", target="Cy"), DONE]))
    assert len(w.fb.social_log) == 1
    assert any(e[2] for e in out["executed"] if e[0] == "avoid") or len(out["executed"]) == 1


def test_remember_stores_a_private_memory_with_a_scaled_importance(world):
    w, f, out = run(_go(world, [call("remember", fact="Bram hides bread", importance="very important"), DONE]))
    m = w.d.minds[1].memories[-1]
    assert m.text == "Bram hides bread" and m.visibility == "private" and abs(m.importance - 0.75) < 1e-6
    assert 1 in w.d.dirty and w.fb.social_log == []


def test_do_nothing_and_garbage_both_end_in_nothing(world):
    w, f, out = run(_go(world, [call("do_nothing"), DONE]))
    assert w.fb.social_log == [] and w.d.wants.nothing == 1
    w2, f2, out2 = run(_go(type(world)(world.cfg.data_dir / "b"), ["not json at all"]))
    assert w2.fb.social_log == [] and out2["outcome"] in ("fallback", "gave_up", "final")


def test_unknown_target_is_rejected_by_the_grammar_or_validator(world):
    w, f, out = run(_go(world, [call("seek_out", target="Nobody", topic="x"), DONE]))
    assert w.fb.social_log == []


def test_nobody_present_removes_social_tools(world):
    w, f, out = run(_go(world, [call("do_nothing"), DONE], scene_present=[]))
    assert set(out["allowed"]) == {"remember", "do_nothing"}


def test_unsupported_mod_op_is_a_clean_miss(world):
    async def go():
        world.fb.social_supported = False
        return await _go(world, [call("seek_out", target="Bram", topic="bread"), DONE])
    w, f, out = run(go())
    assert out["unsupported"] and w.d.wants.acted == 0 and w.fb.social_log == []


def test_romantic_topic_is_refused_for_a_minor_actor_or_target(world):
    w, f, out = run(_go(world, [call("seek_out", target="Bram", topic="go kiss behind the barn"), DONE], ages={1: 15}))
    assert w.fb.social_log == [] and w.d.wants.refused >= 1
    # adult actor, minor target
    from conftest import World
    w2 = World(world.cfg.data_dir / "c")
    w2, f2, o2 = run(_go(w2, [call("seek_out", target="Bram", topic="to flirt with him"), DONE], ages={2: 16}))
    assert w2.fb.social_log == [] and w2.d.wants.refused >= 1
    # the same topic between adults with no minor in the scene is not an age-gate matter
    w3 = World(world.cfg.data_dir / "d")
    w3, f3, o3 = run(_go(w3, [call("seek_out", target="Bram", topic="to flirt with him"), DONE]))
    assert len(w3.fb.social_log) == 1


def test_romance_tools_never_allowed_with_a_minor(world):
    async def go():
        await world.go()
        world.d.adapter.romantic_intents = frozenset({"seek_out"})     # stand-in: a romance-type tool name
        world.fb.pawns[3]["age"] = 14
        world.d.ages[3] = 14
        return world.d.tools_allowed({"seek_out", "avoid", "remember"}, True)
    assert run(go()) == {"avoid", "remember"}


def test_prompt_is_delimited_data_and_names_are_cleaned(world):
    w, f, out = run(_go(world, [call("do_nothing"), DONE]))
    p = "\n".join(m["content"] for m in f.calls[0]["messages"])
    assert '<game_data kind="present">' in p and '<game_data kind="wants">' in p and "game_data" in p.split("=== YOUR WANTS")[0]


def test_idle_window_schedules_wants_with_pacing(world):
    async def go():
        w = await world.go()
        d = w.d
        assert d.idle.due_wants() == []                    # off with no tool backend
        d.tool_backend = Fake([call("do_nothing"), DONE]); d.tool_mode = "grammar"
        w.fb.tick += 100000
        w.d.bridge.env.game = w.fb.tick
        d.bridge_cfg["talk_frequency"] = 1.0
        d.weights = {i: 2.0 for i in d.minds}
        due = d.idle.due_wants()
        d.bridge_cfg["talk_frequency"] = 0
        off = d.idle.due_wants()
        d.bridge_cfg["talk_frequency"] = 1.0
        d.wants_last = {i: d.bridge.env.game for i in d.minds}
        gapped = d.idle.due_wants()
        d.wants_last = {}
        d.bridge.env.speed = d.leases.pause_ambient_at_speed   # ambient pause (4x by default since the speed work)
        fast = d.idle.due_wants()
        return due, off, gapped, fast
    due, off, gapped, fast = run(go())
    assert due == sorted(due) and len(due) == 3
    assert off == [] and gapped == [] and fast == []


def test_director_runs_a_wants_window_end_to_end(world):
    async def go():
        w = await world.go()
        d = w.d
        d.tool_backend = Fake([call("seek_out", target="Bram", topic="the bread"), DONE]); d.tool_mode = "grammar"
        w.fb.tick += 100000
        d.bridge.env.game = w.fb.tick
        d.weights = {i: 2.0 for i in d.minds}
        await d.step(); await d.drain()
        return w
    w = run(go())
    assert len(w.fb.social_log) >= 1 and w.d.wants.runs >= 1

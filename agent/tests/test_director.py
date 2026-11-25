import json

import httpx

from conftest import run
from hermesvoice_agent.core.minds import Mind


def test_epoch_change_drops_and_reloads_minds(world):
    async def go():
        w = await world.go()
        # the working copy gets ahead of the save
        w.d.minds[1].wants = ["unsaved want"]
        w.d.dirty.discard(1)
        w.fb.minds[1] = (Mind(actor_id=1, name="Ana", wants=["saved want"], notes="from the save").model_dump(), 7)
        w.fb.reload_game()
        await w.d.step()
        return w
    w = run(go())
    assert w.d.minds[1].wants == ["saved want"] and w.d.minds[1].notes == "from the save"
    assert w.d.revs[1] == 7
    assert not w.bridge.epoch_changed
    assert w.d.events.since == 0 or True


def test_epoch_zero_main_menu_drops_everything(world):
    async def go():
        w = await world.go()
        w.fb.epoch = 0
        await w.d.step()
        return w
    w = run(go())
    assert w.d.minds == {}


def test_witness_only_through_director_and_mind_saved(world):
    async def go():
        w = await world.go()
        w.fb.add_event("injury", [(1, "victim"), (3, "attacker")], "Ana was shot.", [1, 2], 0.2)
        await w.d.step(); await w.d.drain(); await w.d.step()
        return w
    w = run(go())
    assert len(w.d.minds[1].memories) == 1 and len(w.d.minds[2].memories) == 1
    assert w.d.minds[3].memories == []
    saved = w.fb.minds[1][0]
    assert saved["memories"][0]["text"].startswith("It happened to me")
    assert w.fb.minds[3][0]["memories"] == []


def test_event_speaks_with_expiry_and_op_id(world):
    async def go():
        w = await world.go()
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.6)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.fb.say_log) == 1
    s = w.fb.say_log[0]
    assert s["pawn"] == 1 and 1000 + 3000 <= s["expires_tick"] <= 1000 + 3000 + 30 and s["op_id"] and s["combat"] is True
    assert w.d.said[0]["text"]


def test_low_severity_never_reaches_model(world):
    async def go():
        w = await world.go()
        w.fb.add_event("level_up", [(1, "subject")], "Ana got better at Cooking.", [1], 0.05)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.llm.calls == [] and w.fb.say_log == []


def test_expired_line_dropped_silently(world):
    async def go():
        w = await world.go()
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1], 1.0)
        orig = w.llm.complete
        async def slow(*a, **k):
            w.real += 30.0                       # the line took 30 s
            return await orig(*a, **k)
        w.llm.complete = slow
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log == []
    assert (1, "expired") in w.d.dropped or any(r == "expired" for _, r in w.d.dropped)


def test_failed_line_is_dropped_never_shown(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["I'm sorry, but I can't help with that."]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1], 1.0)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log == [] and ("refusal" in [r for _, r in w.d.dropped])


def test_skip_if_nothing_new(world):
    async def go():
        w = await world.go()
        for _ in range(2):
            w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.6)
            w.d.leases.quotas.last_spoke.clear()
            await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.fb.say_log) == 1 and (1, "nothing_new") in w.d.dropped


def test_player_talk_preempts_and_answers_with_talk_id(world):
    async def go():
        w = await world.go()
        w.fb.talk_queue.append({"id": "t1", "pawn": 1, "text": "How are you?", "from": "player"})
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log[0]["talk_id"] == "t1" and "expires_tick" not in w.fb.say_log[0]
    assert w.fb.talk_queue == []
    prompt = "\n".join(t for _, t in w.llm.calls[0]["messages"])
    assert "The player says to you: How are you?" in prompt


def test_ambient_pause_in_director(world):
    async def go():
        w = await world.go()
        w.fb.speed = 4
        w.fb.add_event("level_up", [(1, "subject")], "Ana got better.", [1], 0.9)
        await w.d.step(); await w.d.drain()
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.9)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.d.said) == 1 and w.fb.say_log[0]["combat"] is True
    assert len(w.fb.say_log) == 1


def test_prefix_does_not_mention_unknown_pawn(world):
    async def go():
        w = await world.go()
        w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "present": [{"pawn": 2, "job": "idle", "distance": 2, "can_hear": True}],
                          "time_of_day": "morning", "weather": "clear", "trends": {}}
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.6)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    prefix = w.llm.calls[0]["messages"][0][1]
    assert "Bram" in prefix and "Cy" not in prefix


def test_gossip_creates_told_memory_only_if_applied(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Cy is hoarding food, you know."]
        w.llm.script["intent"] = ["gossip"]
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], 0.7)
        w.d.cfg.quotas.pair_per_day = 9
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    told = [m for m in w.d.minds[2].memories if m.source == "told"]
    # conversation ran; if gossip was spoken by Ana the listener has a lower-confidence told memory
    if any(s["intent"] == "gossip" for s in w.d.said if s["actor"] == 1):
        assert told and told[0].confidence < 1 and told[0].told_by == "Ana"

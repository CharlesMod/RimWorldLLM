"""Words for the game's own social interactions: the line prompt says what the interaction was, no effect is added."""
from conftest import run
from rimworldllm_agent.core.lease import Cls


def vanilla(w, interaction="Insult", label="insult", text="Ana insulted Bram's cooking.", tier="high", ref="v1-1"):
    return w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], f"Ana -> Bram: {label}.", [1, 2], 0.5,
                          data={"interaction": interaction, "interaction_label": label, "vanilla": True, "ref": ref,
                                "tier": tier, "vanilla_text": text})


def test_insult_line_prompt_says_insult_and_subject_and_carries_ref(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Your stew is slop, Bram."]
        vanilla(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.fb.say_log) == 1
    s = w.fb.say_log[0]
    assert s["pawn"] == 1 and s["listener"] == 2 and s["vanilla_ref"] == "v1-1" and s["intent"] == "none"
    line_calls = [c for c in w.llm.calls if c.get("kind") == "line"]
    assert len(line_calls) == 1
    user = line_calls[0]["messages"][-1][1]
    assert "an insult" in user and "Ana insulted Bram's cooking." in user
    assert not [c for c in w.llm.calls if c.get("kind") == "intent"]      # no intent pass, so no second effect


def test_prompt_fits_each_kind(world):
    ad = world.adapter
    for key, word in [("KindWords", "kind words"), ("Slight", "slights"), ("DeepTalk", "personal"), ("Chitchat", "small talk")]:
        e = ad.normalize_event(vanilla(world, key, key.lower()))
        info = ad.vanilla_social(e)
        assert word in ad.vanilla_request(info, "Ana", "Bram")


def test_unknown_interaction_uses_its_label(world):
    e = world.adapter.normalize_event(vanilla(world, "ModdedThing", "stare awkwardly"))
    info = world.adapter.vanilla_social(e)
    assert "stare awkwardly" in world.adapter.vanilla_request(info, "Ana", "Bram")


def test_plain_social_event_is_not_vanilla(world):
    e = world.adapter.normalize_event(world.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "x", [1, 2], 0.5,
                                                         data={"interaction": "Insult"}))
    assert world.adapter.vanilla_social(e) is None


def test_tier_sets_priority_chitchat_is_ambient(world):
    async def go():
        w = await world.go()
        seen = []
        orig = w.d.leases.submit
        w.d.leases.submit = lambda item, now, speed=1: (seen.append((item.kind, item.cls, item.payload.get("vanilla", {}).get("ref"))), orig(item, now, speed))[1]
        vanilla(w, "Insult", "insult", ref="a")
        vanilla(w, "Chitchat", "chitchat", text="Ana chatted with Bram.", tier="idle", ref="b")
        await w.d.step()
        return seen
    seen = run(go())
    assert ("vanilla_social", Cls.SOCIAL, "a") in seen
    assert ("vanilla_social", Cls.AMBIENT, "b") in seen


def test_dropped_line_says_nothing(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = [""]          # sanitised away
        vanilla(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log == []

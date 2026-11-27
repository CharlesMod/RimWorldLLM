"""Age gate: nothing romantic or sexual for or about an actor under 18, at four points (core/agegate.py)."""
import random

import pytest

from conftest import run
from rimworldllm_agent.core import agegate as G
from rimworldllm_agent.core.sanitize import sanitize_line, sanitize_prose


class Always(random.Random):
    def random(self):
        return 0.0


ROMANTIC = ["Hey, want to go kiss behind the barn?", "I love you, you know that.", "You look so sexy tonight.",
            "Come to my bed.", "How about a date with me?", "Pass the bread, handsome.", "s e x y now", "s3xy", "Let's make love."]
PLAIN = ["Get the medicine from storage.", "Feed the animals then.", "The raid ended at date five.", "Mind the ice on the step.",
         "He proposed a new rota.", "Hm. Back hurts. Move the crate.", "You look tired, sit down."]


def test_age_unknown_counts_as_minor():
    assert G.is_minor(None) and G.is_minor("x") and G.is_minor(float("nan")) and G.is_minor(-1)
    assert G.is_minor(17) and G.is_minor(17.9) and not G.is_minor(18) and not G.is_minor(30)


@pytest.mark.parametrize("t", ROMANTIC)
def test_vocabulary_catches_romantic_lines(t):
    assert G.romantic(t)


@pytest.mark.parametrize("t", PLAIN)
def test_ordinary_colony_talk_is_not_romantic(t):
    assert not G.romantic(t)


# ---- (c) the sanitiser ------------------------------------------------------------------------------------------
def _san(t, **kw):
    return sanitize_line(t, speaker_names={"Ana"}, known_names={"Bram", "Cy"}, all_names={"Ana", "Bram", "Cy"}, **kw)


def test_sanitizer_drops_romance_when_a_minor_is_involved_or_named():
    for t in ROMANTIC:
        s = _san(t, involves_minor=True)
        assert not s.ok and s.reason == "minor_safety", t
    # a minor named in the line, speaker and listener adults
    s = _san("Cy is so handsome, I love Cy.", minor_names={"Cy"})
    assert s.reason == "minor_safety"
    # no minor involved: adults may be romantic; ordinary lines are never touched
    assert _san("Come here, you handsome fool.").ok
    for t in PLAIN:
        assert _san(t, involves_minor=True, minor_names={"Cy"}).ok, t


def test_sanitizer_applies_to_prose():
    s = sanitize_prose("Ana spent the evening flirting with Cy by the fire and it felt wonderful.",
                       known_names={"Cy"}, all_names={"Ana", "Cy"}, prompt_words=set(), minor_names={"Cy"})
    assert s.reason == "minor_safety"
    s = sanitize_prose("Ana spent the evening flirting with Bram by the fire and it felt wonderful.",
                       known_names={"Bram"}, all_names={"Ana", "Bram"}, prompt_words=set(), minor_names={"Cy"},
                       speaker_names={"Ana"})
    assert s.ok
    assert sanitize_prose("Cy kissed the cheek of the cat and it was a good day for the farm.", known_names={"Cy"},
                          all_names={"Cy"}, prompt_words=set(), involves_minor=True).reason == "minor_safety"


# ---- (b) intent labels --------------------------------------------------------------------------------------------
def test_label_filter_and_refusal():
    labels = ["none", "kind", "flirt", "romance"]
    assert G.labels_for(labels, {"flirt", "romance"}, True) == ["none", "kind"]
    assert G.labels_for(labels, {"flirt", "romance"}, False) == labels
    assert G.labels_for(["flirt", "kind"], {"flirt"}, True) == ["flirt", "kind"]     # the 'nothing' label is never removed
    assert G.refuse_intent("flirt", {"flirt"}, True) and not G.refuse_intent("flirt", {"flirt"}, False)
    assert not G.refuse_intent("kind", {"flirt"}, True)


# ---- (d) tool loop allowed set ---------------------------------------------------------------------------------
def test_allowed_tools_drop_romance_tools_for_minors():
    tools = {"seek_out", "avoid", "remember", "do_nothing", "flirt", "romance"}
    assert G.allowed_tools(tools, {"flirt", "romance"}, True) == {"seek_out", "avoid", "remember", "do_nothing"}
    assert G.allowed_tools(tools, {"flirt", "romance"}, False) == tools


# ---- (a) prompt building ----------------------------------------------------------------------------------------
def _world_with_ages(world, ages):
    for pid, a in ages.items():
        world.fb.pawns[pid]["age"] = a
    return world


def test_prompt_carries_minor_flags_and_rule(world):
    async def go():
        w = _world_with_ages(world, {1: 14, 2: 30, 3: 30})
        await w.go()
        lease = w.d.leases.acquire(w.d.leases.make_item(1, "x", w.d.now(), None), w.d.now())
        await w.d._prepare(1, "Bram says hello.", None, lease)
        return w, lease
    w, lease = run(go())
    prefix = lease.data["prefix"]
    assert "You are a minor (under 18)." in prefix and "Age group: minor, under 18" in prefix
    assert G.MINOR_RULE in prefix
    assert w.d.is_minor(1) and not w.d.is_minor(2)


def test_prompt_marks_minors_present_and_adult_prompt_has_no_flag(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 12})
        await w.go()
        lease = w.d.leases.acquire(w.d.leases.make_item(1, "x", w.d.now(), None), w.d.now())
        await w.d._prepare(1, "Bram says hello.", None, lease)
        return lease
    p = run(go()).data["prefix"]
    assert "Cy (minor, " in p and "You are a minor" not in p and "Age group: minor" not in p


def test_labels_offered_exclude_romance_for_minor_exchange(world):
    async def go():
        w = await world.go()
        w.adapter.intent_labels = ["none", "kind", "flirt"]
        return w
    w = run(go())
    assert "flirt" in w.d.labels_for(False) and "flirt" not in w.d.labels_for(True)


# ---- the director paths -----------------------------------------------------------------------------------------
def _conv(w, a=1, b=2):
    w.d.rng = Always()
    w.fb.add_event("social", [(a, "speaker"), (b, "listener")], "A talk.", [a, b], 0.7)
    w.d.cfg.quotas.pair_per_day = 9


def _spoken(w):
    return [s["text"] for s in w.d.said]


def test_conversation_with_a_minor_drops_romantic_lines(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 15, 3: 30})
        await w.go()
        w.llm.script["line"] = ["Hey, want to go kiss behind the barn?", "Hm. In the cold room, second shelf, friend.",
                                "Hm. Go on then, fetch the medicine."]
        _conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert (1, "minor_safety") in w.d.dropped
    assert not any(G.romantic(t) for t in _spoken(w))


def test_adult_pair_may_be_romantic_with_no_minor_around(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 30})
        await w.go()
        w.llm.script["line"] = ["Come here, you handsome fool, sit by me.", "Hm. Fine, fine, friend."]
        _conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert "Come here, you handsome fool, sit by me." in _spoken(w)
    assert (1, "minor_safety") not in w.d.dropped


def test_group_minor_overhearing_adults_blocks_romance_between_them(world):
    # Ana and Bram are adults; Cy is a minor in the scene and can hear
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 16})
        await w.go()
        w.llm.script["line"] = ["Come here, you handsome fool, sit by me.", "Hm. Fine, fine, friend.",
                                "Hm. Go on then, fetch the medicine."]
        _conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert (1, "minor_safety") in w.d.dropped and not any(G.romantic(t) for t in _spoken(w))


def test_group_minor_out_of_earshot_does_not_block_adults(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 16})
        w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear",
                          "present": [{"pawn": 2, "job": "Cooking", "can_hear": True}, {"pawn": 3, "job": "Sowing", "can_hear": False}]}
        w.fb.scenes[2] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear",
                          "present": [{"pawn": 1, "job": "Cooking", "can_hear": True}, {"pawn": 3, "job": "Sowing", "can_hear": False}]}
        await w.go()
        w.llm.script["line"] = ["Come here, you handsome fool, sit by me.", "Hm. Fine, fine, friend."]
        _conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert "Come here, you handsome fool, sit by me." in _spoken(w)


def test_adults_naming_a_minor_romantically_is_dropped(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 16})
        w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear",
                          "present": [{"pawn": 2, "job": "Cooking", "can_hear": True}]}
        w.fb.scenes[2] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear",
                          "present": [{"pawn": 1, "job": "Cooking", "can_hear": True}]}
        await w.go()
        w.llm.script["line"] = ["Cy is so handsome, I could kiss Cy.", "Hm. Fine, fine, friend.", "Hm. Go on then, fetch it."]
        _conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert (1, "minor_safety") in w.d.dropped and not any("Cy" in t and G.romantic(t) for t in _spoken(w))


def test_unknown_age_is_treated_as_minor(world):
    async def go():
        w = await world.go()
        del w.fb.pawns[2]["age"]
        w.d.ages.pop(2)
        w.d.ages[2] = w.adapter.snapshot_age(w.fb.pawns[2])
        return w
    w = run(go())
    assert w.d.ages[2] is None and w.d.is_minor(2)


def test_intent_refusal_is_the_last_guard(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 15, 3: 30})
        await w.go()
        w.adapter.__class__.intent_labels = ["none", "kind", "flirt"]
        from rimworldllm_agent.core.passes import Spoken
        ok = await w.d._say(1, Spoken("Good morning.", "flirt"), listener=2, expires=None)
        return w, ok
    try:
        w, ok = run(go())
    finally:
        from rimworldllm_agent.games.rimworld import RimWorldAdapter
        RimWorldAdapter.intent_labels = ["none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain"]
    assert ok and w.d.said[-1]["intent"] == "none" and w.d.gate.refused_intents == 1


def test_vanilla_romance_with_a_minor_gets_no_words(world):
    async def go(ages):
        w = _world_with_ages(world, ages)
        await w.go()
        w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "Ana made a romantic advance on Bram.", [1, 2], 0.8,
                       data={"vanilla": True, "ref": "r1", "interaction": "RomanceAttempt", "tier": "high",
                             "vanilla_text": "Ana tried to romance Bram."})
        w.d.rng = Always()
        await w.d.step(); await w.d.drain()
        return w
    w = run(go({1: 30, 2: 15, 3: 30}))
    assert w.d.said == [] and w.d.gate.skipped_events == 1 and not w.d.leases.queue


def test_vanilla_romance_between_adults_still_gets_words(world, tmp_path):
    from conftest import World
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 30})
        await w.go()
        w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "Ana made a romantic advance on Bram.", [1, 2], 0.8,
                       data={"vanilla": True, "ref": "r1", "interaction": "RomanceAttempt", "tier": "high",
                             "vanilla_text": "Ana tried to romance Bram."})
        w.llm.script["line"] = ["Come here, you handsome fool."]
        w.d.rng = Always()
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.d.gate.skipped_events == 0 and len(w.d.said) == 1


def test_player_romantic_talk_to_or_about_a_minor_never_reaches_the_model(world):
    async def go():
        w = _world_with_ages(world, {1: 14, 2: 30, 3: 30})
        w.fb.talk_queue.append({"id": "t1", "pawn": 1, "text": "Do you want to kiss me?", "from": "player"})
        w.fb.talk_queue.append({"id": "t2", "pawn": 2, "text": "Tell Ana she is sexy.", "from": "player"})
        await w.go()
        before = len(w.llm.calls)
        await w.d.take_talk()
        await w.d.step(); await w.d.drain()
        return w, before
    w, before = run(go())
    assert w.d.gate.skipped_player_text == 2
    assert [c for c in w.llm.calls[before:] if c["kind"] in ("line", "intent")] == []
    assert w.d.said == []


def test_player_ordinary_talk_to_a_minor_is_answered(world):
    async def go():
        w = _world_with_ages(world, {1: 14, 2: 30, 3: 30})
        w.fb.talk_queue.append({"id": "t1", "pawn": 1, "text": "How is the harvest going?", "from": "player"})
        await w.go()
        w.llm.script["line"] = ["Hm. The corn is nearly ready, friend."]
        await w.d.take_talk()
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.d.gate.skipped_player_text == 0 and len(w.d.said) == 1


def test_romantic_prose_about_a_minor_is_dropped_in_a_diary_path(world):
    async def go():
        w = _world_with_ages(world, {1: 30, 2: 30, 3: 16})
        await w.go()
        return w
    w = run(go())
    assert w.d.minor_tokens() >= {"Cy"} and "Ana" not in w.d.minor_tokens()

"""Delivery (POST /say kind) decided inside the intent pass, bounded; short map thoughts (POST /thought surface)."""
import pytest
from conftest import run
from hermesvoice_agent.core.minds import Memory
from hermesvoice_agent.core.passes import parse_delivery, parse_intent
from hermesvoice_agent.core.prompt import DELIVERIES, grammar_for
from hermesvoice_agent.games.rimworld import RimWorldAdapter

DAY = 60000
LABELS = RimWorldAdapter().intent_labels


async def step(w):
    await w.d.step(); await w.d.drain()


def social(w, sev=0.7):
    w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], sev)
    w.d.cfg.quotas.pair_per_day = 9


# ---- parsing and grammar ---------------------------------------------------------------
def test_intent_and_delivery_parse_from_one_completion():
    assert parse_intent("insult shout", LABELS) == "insult" and parse_delivery("insult shout") == "shout"
    assert parse_delivery("none whisper") == "whisper"
    assert parse_delivery("kind") is None and parse_delivery("kind loud") is None   # an old reply: the mod guesses
    assert parse_intent("none normal", LABELS) == "none"


def test_grammar_is_one_constrained_completion_and_old_form_is_kept():
    g = grammar_for(LABELS, DELIVERIES)
    assert g.startswith('root ::= intent " " delivery') and '"shout"' in g and '"insult"' in g
    assert grammar_for(LABELS) == "root ::= " + " | ".join(f'"{i}"' for i in LABELS)


def test_the_delivery_costs_no_extra_call(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Get out of my kitchen."]
        w.llm.script["intent"] = ["insult shout"]
        social(w)
        await step(w)
        return w
    w = run(go())
    calls = [c for c in w.llm.calls if c["kind"] == "intent"]
    assert calls and all(c["grammar"].startswith('root ::= intent " " delivery') for c in calls)
    assert not [c for c in w.llm.calls if c["kind"] == "thought"]


# ---- what /say carries -------------------------------------------------------------------
def test_say_carries_kind_from_the_intent_pass(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Keep it down, they will hear."]
        w.llm.script["intent"] = ["confide whisper"]
        social(w)
        await step(w)
        return w
    w = run(go())
    assert w.fb.say_log and w.fb.say_log[0]["kind"] == "whisper"


def test_missing_delivery_sends_no_kind(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Nice day for it."]
        w.llm.script["intent"] = ["none"]                       # an old model reply / custom endpoint
        social(w)
        await step(w)
        return w
    w = run(go())
    assert w.fb.say_log and "kind" not in w.fb.say_log[0]


def test_a_shout_in_a_calm_scene_is_said_normally(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Pass the salt."] * 4
        w.llm.script["intent"] = ["none shout"] * 4
        social(w)
        await step(w)
        return w
    w = run(go())
    assert w.fb.say_log and all(s["kind"] == "normal" for s in w.fb.say_log)


def test_a_shout_needs_the_scene_and_a_pause(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["You useless fool."] * 6
        w.llm.script["intent"] = ["insult shout"] * 6
        social(w)
        await step(w)
        return w
    w = run(go())
    for pawn in {s["pawn"] for s in w.fb.say_log}:
        kinds = [s["kind"] for s in w.fb.say_log if s["pawn"] == pawn]
        assert kinds[0] == "shout" and "shout" not in kinds[1:]   # an insult justifies one; the gap stops a burst


def test_combat_justifies_a_shout(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Get down!"]
        w.llm.script["intent"] = ["none shout"]
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.9)
        await step(w)
        return w
    w = run(go())
    assert any(s["combat"] and s.get("kind") in (None, "shout") for s in w.fb.say_log)


# ---- thoughts ----------------------------------------------------------------------------
def inject(w, aid, text, game, **kw):
    w.d.minds[aid].memories.append(Memory(id=w.d.minds[aid].next_mem_id(), game=game, text=text, **kw))


def test_thought_is_short_sent_with_surface_and_paced(world):
    async def go():
        w = await world.go()
        w.d.cfg.thought_enabled = True
        inject(w, 1, "I saw Bram eat alone.", 1000, people=[2])
        w.llm.script["thought"] = ["Bram should not eat alone."]
        await step(w)
        first = [t for t in w.fb.thought_log if t["kind"] == "thought"]
        assert len(first) == 1 and first[0]["pawn"] == 1
        assert first[0]["surface"] == first[0]["text"] == "Bram should not eat alone."
        assert len(first[0]["surface"]) <= 90
        inject(w, 1, "I found a ripe berry bush.", 2000)           # new material, but too soon
        await step(w); await step(w)
        assert len([t for t in w.fb.thought_log if t["kind"] == "thought"]) == 1
        w.real += w.d.cfg.thought_every_real_s + 1
        await step(w)
        return w
    w = run(go())
    assert len([t for t in w.fb.thought_log if t["kind"] == "thought"]) == 2


def test_a_long_thought_never_reaches_the_map_and_private_memories_are_not_used(world):
    async def go():
        w = await world.go()
        w.d.cfg.thought_enabled = True
        inject(w, 1, "I hid a secret from Bram.", 1000, visibility="private")
        await step(w)
        assert not [c for c in w.llm.calls if c["kind"] == "thought"]        # nothing public to think about
        inject(w, 1, "I saw Bram eat alone.", 1000, people=[2])
        w.llm.script["thought"] = ["I keep going over how he sat there with his plate and nobody came and "
                                   "nobody asked and it nags me."]
        await step(w)
        return w
    w = run(go())
    assert not [t for t in w.fb.thought_log if t["kind"] == "thought"]
    assert any(d[1].startswith("thought:") for d in w.d.dropped)


def test_thoughts_off_by_setting(world):
    async def go():
        w = await world.go()
        inject(w, 1, "I saw Bram eat alone.", 1000, people=[2])
        await step(w)
        return w
    w = run(go())
    assert not [c for c in w.llm.calls if c["kind"] == "thought"]


@pytest.mark.parametrize("age,shown", [(12, False), (30, True)])
def test_a_romantic_thought_about_a_minor_never_reaches_the_map(world, age, shown):
    async def go():
        w = await world.go()
        w.d.cfg.thought_enabled = True
        w.d.ages.update({1: 30, 2: age})
        inject(w, 1, "I saw Bram by the fire.", 1000, people=[2])
        w.llm.script["thought"] = ["I want to kiss Bram tonight."]
        await step(w)
        return w
    w = run(go())
    got = [t for t in w.fb.thought_log if t["kind"] == "thought"]
    assert bool(got) is shown
    if not shown:
        assert (1, "thought:minor_safety") in w.d.dropped

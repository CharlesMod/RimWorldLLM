"""Item 9: bystander reactions (core/bystanders.py)."""
from conftest import run
from rimworldllm_agent.core import bystanders as B
from rimworldllm_agent.core.minds import Mind, Relation
from rimworldllm_agent.games.rimworld import RimWorldAdapter

AD = RimWorldAdapter()


async def step(w):
    await w.d.step(); await w.d.drain()


def three_windows(w):
    if len(w.d.leases.actor_slots) == 2:
        w.d.leases.actor_slots.append(3)         # the two talkers use two windows; the bystander waits for a third


def insult(w, witnesses=(3,), sev=0.5, interaction="Insult"):
    w.d.cfg.quotas.pair_per_day = 9
    three_windows(w)
    return w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "Ana -> Bram: insult.", list(witnesses), sev,
                          data={"interaction": interaction, "interaction_label": "insult"})


def reaction_calls(w):
    return [c for c in w.llm.calls if c["kind"] == "reaction"]


def clouds(w, aid):
    return [t for t in w.fb.thought_log if t.get("pawn") == aid and t.get("surface")]


def test_pick_prefers_the_strongest_opinion_and_skips_parties_and_gaps():
    minds = {i: Mind(actor_id=i) for i in (1, 2, 3, 4, 5)}
    minds[4].relations["1"] = Relation(name="Ana", opinion=-40)
    minds[5].relations["2"] = Relation(name="Bram", opinion=15)
    bs = B.Bystanders()
    assert B.pick([1, 2, 3, 4, 5], [1, 2], minds, bs, 100.0, 120.0) == 4
    bs.mark(4, 100.0)
    assert B.pick([1, 2, 3, 4, 5], [1, 2], minds, bs, 150.0, 120.0) == 5       # 4 is inside its gap
    assert B.pick([1, 2], [1, 2], minds, bs, 150.0, 120.0) is None             # only the parties
    assert B.pick([4], [1, 2], minds, bs, 400.0, 120.0) == 4                   # gap over
    assert B.pick([9], [1, 2], minds, bs, 400.0, 120.0) is None                # no mind for 9


def test_clean_reaction_rules():
    kw = dict(prompt="", aid=3, name_tokens={1: {"Ana"}, 2: {"Bram"}, 3: {"Cy"}}, common_words=set(), minor=False, minor_names=set())
    assert B.clean_reaction("Not again.", **kw) == "Not again."
    assert B.clean_reaction("Ha. About time.", **kw) == "Ha. About time."
    assert B.clean_reaction("Here we go, Zed.", **kw) is None                    # an invented name
    assert B.clean_reaction("Ana started it.", **kw) is None                     # naming a party
    assert B.clean_reaction("Oh.", **kw) is None                                  # too short
    assert B.clean_reaction("This is the seventh time this week.", **kw) is None  # too long (7 words)
    assert B.clean_reaction("I want to kiss her so much", **{**kw, "minor": True, "minor_names": {"Bram"}}) is None


def test_prompt_is_small_and_wrapped():
    m = Mind(actor_id=3, name="Cy")
    m.voice.register_line = "dry"
    m.relations["1"] = Relation(name="Ana", opinion=-20)
    msgs = B.messages(AD, m, {1: "Ana", 2: "Bram", 3: "Cy"}, [1, 2], "Ana -> Bram: insult.", False)
    flat = "\n".join(t for _, t in msgs)
    assert "<data" in flat or "data" in flat.lower()
    assert "Ana (you feel -20 about them)" in flat and "Bram" in flat
    assert len(flat.split()) * 1.3 < 330            # estimate: words x 1.3 (system rule + data rule included)


def test_vanilla_insult_makes_one_bystander_cloud_in_a_free_window(world):
    async def go():
        w = await world.go()
        insult(w, witnesses=(3,))
        await step(w)
        return w
    w = run(go())
    calls = reaction_calls(w)
    assert len(calls) == 1 and calls[0]["actor"] == 3 and calls[0]["slot"] in (1, 2, 3)
    cl = clouds(w, 3)
    assert len(cl) == 1 and cl[0]["text"] == cl[0]["surface"] == "Not again, you two."
    assert [t.text for t in w.d.minds[3].inner if t.kind == "reaction"] == ["Not again, you two."]
    assert not clouds(w, 1) and not clouds(w, 2)                                  # the parties show nothing


def test_one_reaction_per_argument_and_a_per_pawn_gap(world):
    async def go():
        w = await world.go()
        w.fb.add_pawn(4, "Dee")
        w.fb.add_pawn(5, "Eli")
        await w.d.load_actor(4); await w.d.load_actor(5)
        insult(w, witnesses=(3, 4, 5))
        await step(w)
        n1 = len(reaction_calls(w))
        w.fb.tick += 50
        insult(w, witnesses=(3, 4, 5))                                             # another one right after
        await step(w)
        n2 = len(reaction_calls(w))
        return w, n1, n2
    w, n1, n2 = run(go())
    assert n1 == 1 and n2 == 2                          # one per argument, and the second picks someone not in a gap
    who = [c["actor"] for c in reaction_calls(w)]
    assert len(set(who)) == 2


def test_not_a_witness_not_a_party_and_not_an_argument(world):
    async def go():
        w = await world.go()
        insult(w, witnesses=(1, 2))                                                # only the parties heard it
        w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "Ana chatted.", [3], 0.5,
                       data={"interaction": "Chitchat"})
        await step(w)
        return w
    w = run(go())
    assert not reaction_calls(w) and not w.fb.thought_log


def test_template_when_windows_are_busy_and_nothing_at_dev_speed(world):
    async def go():
        w = await world.go()
        insult(w, witnesses=(3,))
        w.d.leases.actor_slots[:] = []                                            # no actor window at all
        await step(w)
        busy = (len(reaction_calls(w)), [t["text"] for t in clouds(w, 3)])
        w.fb.speed = 4
        w.fb.tick += 100000
        w.d.leases.actor_slots[:] = [1, 2, 3]
        insult(w, witnesses=(3,))
        await step(w)
        return w, busy
    w, busy = run(go())
    assert busy[0] == 0 and busy[1] and busy[1][0] in AD.bystander_templates
    assert len(clouds(w, 3)) == 1                                                 # speed 4: ambient pause, no second cloud


def test_applied_say_insult_uses_the_scene_present_list(world):
    async def go():
        w = await world.go()
        w.fb.pawns[1]["relations"] = []
        w.d.minds[3].relations["1"] = Relation(name="Ana", opinion=-10)
        w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "time_of_day": "morning", "weather": "clear",
                          "present": [{"pawn": 2, "job": "idle", "can_hear": True}, {"pawn": 3, "job": "idle", "can_hear": True}]}
        three_windows(w)
        w.llm.script["intent"] = ["insult normal I want him gone"]
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], 0.7)
        w.d.cfg.quotas.pair_per_day = 9
        await step(w)
        w.d.leases.budget.tokens = 5.0                    # the talk spent the voice budget; the cloud waited behind it
        await step(w)
        return w
    w = run(go())
    assert any(s["intent"] == "insult" for s in w.fb.say_log)
    assert len(clouds(w, 3)) == 1 and reaction_calls(w)[0]["actor"] == 3


def test_age_gate_minor_rule_in_prompt_and_romantic_reaction_dropped(world):
    async def go():
        w = await world.go()
        w.fb.pawns[2]["age"] = 12
        w.d.ages[2] = 12.0
        w.llm.script["reaction"] = ["Ugh, kiss already, you two."]
        insult(w, witnesses=(3,))
        await step(w)
        return w
    w = run(go())
    c = reaction_calls(w)[0]
    assert "minor" in c["messages"][0][1].lower()
    assert not clouds(w, 3) and w.d.bystand.dropped == 1


def test_switch_off_and_llm_failure(world):
    async def go():
        world.cfg.bystanders = False
        w = await world.go()
        insult(w, witnesses=(3,))
        await step(w)
        return w
    w = run(go())
    assert not reaction_calls(w) and not w.fb.thought_log

    async def go2(w2):
        w2.llm.fail = True
        await w2.go()
        insult(w2, witnesses=(3,))
        await step(w2)
        return w2
    from conftest import World
    import tempfile, pathlib
    with tempfile.TemporaryDirectory() as td:
        w2 = run(go2(World(pathlib.Path(td))))
        assert not clouds(w2, 3) and w2.d.bystand.dropped == 1

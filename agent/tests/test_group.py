"""Item 10: gathering talk (core/group.py): group scenes, eulogies, vows, speeches."""
from conftest import run
from hermesvoice_agent.core import group as G
from hermesvoice_agent.core.minds import Mind, Relation

LINES = ["Remember the night the wolves came to the gate.", "I do, and Bram held that door alone.",
         "Somebody pass the salt, this stew needs help.", "We built this place out of splinters and stubbornness.",
         "The first winter nearly broke every last one of us.", "Still here though, and still arguing over supper.",
         "Cy burned the porridge again, I swear it.", "Best porridge I ever pushed away politely."]


async def step(w):
    await w.d.step()
    await w.d.drain()


def four(w):
    w.fb.add_pawn(4, "Dee")
    w.d.cfg.group_turns = [5, 4, 2, 0]


def gather(w, kind, ids, lead=None, ledger="gathering:1", severity=0.45, **data):
    actors = [(i, "lead" if i == lead else "participant") for i in ids]
    d = {"phase": "start", "kind": kind, "key": f"k{w.fb.seq + 1}", "label": "x"}
    if lead is not None:
        d["lead"] = lead
    d.update(data)
    return w.fb.add_event("gathering", actors, f"{kind} begins", list(ids), severity, ledger_id=ledger, data=d)


def lines(w):
    return [c for c in w.llm.calls if c["kind"] == "line"]


def test_cap_by_speed_and_ceremony_floor():
    gt = [5, 4, 2, 0]
    assert [G.turn_cap("meal", r, gt, 4) for r in (60, 180, 360, 900)] == [5, 4, 2, 0]
    assert [G.turn_cap("funeral", r, gt, 4) for r in (60, 180, 360, 900)] == [3, 3, 2, 1]
    assert G.turn_cap("funeral", 60, gt, 2) == 2


def test_plan_order_and_no_repeat_in_a_row():
    minds = {i: Mind(actor_id=i) for i in (1, 2, 3, 4)}
    assert G.plan_speakers("funeral", 3, [], [1, 2, 3, 4], minds, {}, 4)[0] == 3
    assert G.plan_speakers("wedding", None, [4, 2], [1, 2, 3, 4], minds, {}, 4)[:2] == [4, 2]
    assert G.plan_speakers("meal", None, [], [1, 2, 3, 4], minds, {1: 0.2, 2: 2.0, 3: 1.0}, 3, opener=1) == [1, 2, 3]
    g = G.Gathering("g", "meal", "k", None, [], None, [1, 2, 3], [1, 2, 3], {1: "A", 2: "B", 3: "C"})
    seq = []
    for _ in range(7):
        s = G.next_speaker(g)
        seq.append(s)
        g.turns.append(G.Turn(s, "x", "y"))
    assert all(a != b for a, b in zip(seq, seq[1:])) and set(seq) == {1, 2, 3}


def test_meal_scene_rotates_windows_and_block_f_carries_every_turn(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    gather(w, "meal", [1, 2, 3, 4], room="Kitchen")
    run(step(w))
    said = [s for s in w.d.said if s["conv"]]
    assert len(said) == 5 and len({s["conv"] for s in said}) == 1
    assert all(s["intent"] == "none" for s in said)
    speakers = [s["actor"] for s in said]
    assert all(a != b for a, b in zip(speakers, speakers[1:])) and len(set(speakers)) == 4
    assert w.d.group.rotations >= 2                                  # four speakers on two windows
    last = lines(w)[-1]["messages"][1][1]
    assert all(t in last for t in LINES[:4])                          # block F: all earlier turns
    assert not [c for c in w.llm.calls if c["kind"] == "intent"]      # no intent or aim call in a group
    assert w.d.leases.leases == {}
    assert w.d.group.ends.get("turn_cap") == 1
    assert w.fb.say_log and all(s.get("listener") != s["pawn"] for s in w.fb.say_log if s.get("listener"))


def test_each_participant_keeps_one_memory(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    gather(w, "meal", [1, 2, 3, 4])
    run(step(w))
    for i in (1, 2, 3, 4):
        mem = [m for m in w.d.minds[i].memories if m.etype == "gathering" and m.text.startswith("I was at")]
        assert len(mem) == 1 and mem[0].importance == 0.3
    assert not [m for m in w.d.minds[1].memories if m.text == "meal begins"]      # the bare event is not a memory


def test_speed_caps_meal_turns_and_4x_has_no_meal_scene(world):
    w = run(world.go())
    four(w)
    w.fb.speed = 3
    w.llm.script["line"] = list(LINES)
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    assert len([s for s in w.d.said if s["conv"]]) == 2
    w.fb.speed = 4
    w.d.group.seen.clear()
    n = len(w.d.said)
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    assert len(w.d.said) == n


def test_story_pick_uses_chronicle_people_and_is_not_repeated(world):
    w = run(world.go())
    four(w)
    d = w.d
    d.chronicle.note(d.bridge.env.game, "Ana and Bram held the gate against the wolves all night long.", 0.6, False)
    got = G.pick_story(d, [1, 2, 3], d.bridge.env.game)
    assert got and "wolves" in got[0] and got[1] in (1, 2)
    d.group.told[got[0]] = d.bridge.env.game
    assert G.pick_story(d, [1, 2, 3], d.bridge.env.game) is None
    assert G.pick_story(d, [3, 4], d.bridge.env.game) is None


def test_meal_opener_retells_the_story_in_the_prompt(world):
    w = run(world.go())
    four(w)
    w.d.chronicle.note(w.d.bridge.env.game, "Ana and Bram held the gate against the wolves all night long.", 0.6, False)
    w.llm.script["line"] = list(LINES)
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    first = lines(w)[0]["messages"]
    assert "wolves" in first[0][1] and "Bring it up" in first[1][1]
    assert w.d.said[0]["actor"] in (1, 2)


def test_funeral_lead_eulogy_with_token_cap_and_old_event_is_silent(world):
    w = run(world.go())
    four(w)
    w.d.bridge_cfg = {"events": {"gathering": True}}
    w.fb.add_event("funeral", [], "A funeral is starting.", [1, 2, 3], 0.5, ledger_id="funeral:9", data={"phase": "start"})
    gather(w, "funeral", [1, 2, 3], lead=3, severity=0.6, deceased_name="Old Tam", ledger="gathering:2")
    w.llm.script["line"] = ["Tam kept us fed through the worst of the winter.", "He taught me to mend a fence.", "Rest easy now, old man."]
    run(step(w))
    ls = lines(w)
    assert ls[0]["actor"] == 3 and len(ls) == 3
    assert "Old Tam" in ls[0]["messages"][0][1] and "eulogy" in ls[0]["messages"][1][1]
    said = [s for s in w.d.said if s["conv"]]
    assert [s["actor"] for s in said][0] == 3
    assert not [s for s in w.d.said if not s["conv"]]                  # the old funeral event spoke no second line
    assert any("said" in n.text for n in w.d.chronicle.state.pending if "funeral" in n.text)


def test_funeral_event_still_reacts_when_gatherings_are_off(world):
    w = run(world.go())
    w.d.cfg.gatherings = False
    w.fb.add_event("funeral", [], "A funeral is starting.", [1, 2], 0.5, ledger_id="funeral:9", data={"phase": "start"})
    gather(w, "funeral", [1, 2, 3], lead=3, severity=0.6)
    run(step(w))
    assert not [s for s in w.d.said if s["conv"]]
    assert w.d.said                                                    # today's behaviour for the funeral


def test_wedding_adults_say_vows_in_order(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = ["I choose you, today and every morning after.", "And I choose you, with all my stubborn heart.",
                            "Long may you two argue happily."]
    gather(w, "wedding", [1, 2, 3, 4], severity=0.6, couple=[2, 1])
    run(step(w))
    said = [s for s in w.d.said if s["conv"]]
    assert [s["actor"] for s in said][:2] == [2, 1] and len(said) == 3
    assert "vow" in lines(w)[0]["messages"][1][1]


def test_wedding_with_a_minor_or_unknown_age_has_no_scene(world):
    w = run(world.go())
    four(w)
    w.fb.pawns[2]["age"] = 17
    w.d.ages.pop(2, None)
    gather(w, "wedding", [1, 2, 3], severity=0.6, couple=[1, 2])
    run(step(w))
    assert not lines(w) and not w.d.said and w.d.gate.skipped_events == 1
    w.fb.pawns[2]["age"] = 30
    w.d.ages.pop(2, None)
    del w.fb.pawns[1]["age"]
    w.d.ages.pop(1, None)
    gather(w, "wedding", [1, 2, 3], severity=0.6, couple=[1, 2])
    run(step(w))
    assert not lines(w)


def test_wedding_minor_guest_does_not_speak_and_no_missing_couple(world):
    w = run(world.go())
    four(w)
    w.fb.pawns[4]["age"] = 12
    w.d.ages.pop(4, None)
    w.llm.script["line"] = ["I choose you, today and every morning after.", "And I choose you, with all my stubborn heart.",
                            "Long may you two argue happily."]
    gather(w, "wedding", [1, 2, 3, 4], severity=0.6, couple=[1, 2])
    run(step(w))
    assert 4 not in [s["actor"] for s in w.d.said]
    w.d.group.seen.clear()
    n = len(w.d.said)
    gather(w, "wedding", [1, 2, 3], severity=0.6)                       # no couple named
    run(step(w))
    assert len(w.d.said) == n


def test_minor_at_a_meal_carries_the_minor_rule(world):
    w = run(world.go())
    four(w)
    w.fb.pawns[3]["age"] = 14
    w.d.ages.pop(3, None)
    w.llm.script["line"] = list(LINES)
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    assert w.d.said
    from hermesvoice_agent.core.agegate import MINOR_RULE
    assert all(MINOR_RULE in c["messages"][0][1] for c in lines(w))


def test_speech_leader_then_listeners(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = ["We keep faith with the work of our own hands.", "Well said, truly.", "I needed that today."]
    gather(w, "speech", [1, 2, 3], lead=1, severity=0.6, ideo="The Work Precept")
    run(step(w))
    assert [s["actor"] for s in w.d.said if s["conv"]][0] == 1
    assert "Work Precept" in lines(w)[0]["messages"][0][1]


def test_switches_off_means_no_scene_and_no_memory(world):
    w = run(world.go())
    four(w)
    w.d.cfg.gatherings = False
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    assert not lines(w) and not w.d.said
    assert not [m for i in (1, 2, 3) for m in w.d.minds[i].memories if m.etype == "gathering"]
    w.d.cfg.gatherings = True
    w.d.cfg.meal_talk = False
    gather(w, "meal", [1, 2, 3])
    run(step(w))
    assert not lines(w)
    w.d.cfg.meal_talk = True
    w.d.bridge_cfg = {"events": {"gathering": False}}
    gather(w, "party", [1, 2, 3])
    run(step(w))
    assert not lines(w)


def test_gathering_end_stops_a_scene_and_urgent_work_preempts(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    e = gather(w, "party", [1, 2, 3, 4])
    key = e["data"]["key"]
    w.d.group.over.add(key)
    run(step(w))
    assert not [s for s in w.d.said if s["conv"]] and w.d.group.ends.get("over") == 1


def test_budget_is_spent_per_line(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    before = w.d.leases.budget.tokens
    gather(w, "meal", [1, 2, 3, 4])
    run(step(w))
    n = len([s for s in w.d.said if s["conv"]])
    assert n and w.d.leases.budget.tokens <= before - n + 1.0


def test_scene_ends_when_the_others_are_apart(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    for i in (1, 2, 3, 4):
        w.fb.scenes[i] = {"room": {"role": "Kitchen", "impressiveness": 30, "clean": "clean", "temp": 20},
                          "present": [{"pawn": i, "job": "Wander", "distance": 3, "can_hear": False}]}
    gather(w, "meal", [1, 2, 3, 4])
    run(step(w))
    assert len([s for s in w.d.said if s["conv"]]) == 1 and w.d.group.ends.get("apart") == 1


def test_urgent_work_preempts_the_scene(world):
    w = run(world.go())
    four(w)
    w.llm.script["line"] = list(LINES)
    orig = w.llm.complete

    async def complete(*a, **k):
        r = await orig(*a, **k)
        for l in w.d.leases.leases.values():
            l.preempted = True
        return r
    w.llm.complete = complete
    gather(w, "meal", [1, 2, 3, 4])
    run(step(w))
    assert not [s for s in w.d.said if s["conv"]] and w.d.group.ends.get("preempted") == 1

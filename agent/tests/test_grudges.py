"""Grudge and make-up loop (core/grudges.py): phases on game time, deterministic decision, outcome closes the loop."""
from conftest import run
from hermesvoice_agent.core import concerns as cn
from hermesvoice_agent.core import grudges as gr
from hermesvoice_agent.core.minds import Relation

DAY = 60000


async def step(w, ticks=0, real=None):
    """Advance game ticks; real (play) seconds follow at 1x unless given, as at a faster speed."""
    w.fb.tick += ticks
    w.real += ticks / 60 if real is None else real
    await w.d.step()
    await w.d.drain()


def insult(w, a, b, kind="Insult"):
    return w.fb.add_event("social", [(a, "initiator"), (b, "recipient")], f"{w.d.names[a]} -> {w.d.names[b]}: {kind}.",
                          [a, b], severity=0.5, data={"interaction": kind, "interaction_label": kind.lower()})


def actions(w, action=None):
    return [(b["pawn"], b["action"], b["target"], b.get("topic")) for b in w.fb.social_log
            if action is None or b["action"] == action]


def grudge(w, holder):
    gs = w.d.minds[holder].grudges
    return gs[-1] if gs else None


def test_decide_is_deterministic_from_traits_and_opinion():
    assert gr.decide(-50, "", "kind", True, 10) == ("offender", "apologise")      # a guilty, gentle offender walks over
    assert gr.decide(-50, "", "abrasive", True, 10) == ("holder", "confront")
    assert gr.decide(-50, "kind", "abrasive", False, 10) == ("holder", "make up")
    assert gr.decide(20, "", "", False, 10) == ("holder", "make up")               # opinion recovered
    assert gr.decide(-20, "", "", False, 10) == ("holder", "confront")
    assert gr.decide(40, "abrasive", "", False, 10) == ("holder", "confront")
    assert gr.outcome_of(["none", "kind"]) == "made_up"
    assert gr.outcome_of(["kind", "insult"]) == "escalated" and gr.outcome_of(["none"]) == "neutral"


def test_an_insult_event_starts_a_grudge_and_avoids_directly(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        return w
    w = run(go())
    g = grudge(w, 2)
    assert g.against == 1 and g.phase == "avoid" and "Ana insulted Bram" in g.cause
    assert actions(w) == [(2, "avoid", 1, "")] or actions(w) == [(2, "avoid", 1, None)]
    assert grudge(w, 1) is None


def test_a_slight_counts_too_but_chitchat_does_not(world):
    async def go():
        w = await world.go()
        insult(w, 3, 1, "Slight")
        insult(w, 3, 2, "Chitchat")
        await step(w)
        return w
    w = run(go())
    assert grudge(w, 1) and not w.d.minds[2].grudges


def test_an_applied_insult_line_starts_one_but_a_refused_or_other_intent_does_not(world):
    async def go():
        w = await world.go()
        d = w.d
        from hermesvoice_agent.core.passes import Spoken
        await d._say(1, Spoken("You useless fool.", "insult"), listener=2, expires=None)
        await d._say(3, Spoken("Hello there.", "kind"), listener=1, expires=None)
        await step(w)
        return w
    w = run(go())
    assert grudge(w, 2) and grudge(w, 2).against == 1 and not w.d.minds[1].grudges


def test_phases_avoid_then_cooldown_then_seek_out_with_guilt_apology(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)                              # kind Ana insults abrasive Bram: Ana feels guilty
        await step(w)
        g1 = cn.open_concerns(w.d.minds[1])
        await step(w, int(0.3 * DAY))
        ph1 = grudge(w, 2).phase
        n_before = len(actions(w, "seek_out"))
        await step(w, int(0.6 * DAY))
        return w, g1, ph1, n_before
    w, g1, ph1, n_before = run(go())
    assert g1 and g1[0].kind == "guilt" and g1[0].about == [2]
    assert ph1 == "cooldown" and n_before == 0
    assert grudge(w, 2).phase == "resolve"
    assert actions(w, "seek_out") == [(1, "seek_out", 2, "apologise")]       # the offender walks over


def test_confront_when_opinion_is_low_and_nobody_is_kind(world):
    async def go():
        w = await world.go()
        w.fb.pawns[1]["traits"] = []
        insult(w, 3, 2)                              # Cy (no traits) insults abrasive Bram; Bram's opinion of Cy is low
        w.fb.pawns[2]["relations"] = [{"pawn": 3, "opinion": -40}]
        await step(w)
        await step(w, int(0.8 * DAY))
        return w
    w = run(go())
    assert actions(w, "seek_out") == [(2, "seek_out", 3, "confront")]


def test_make_up_when_the_holder_is_kind(world):
    async def go():
        w = await world.go()
        insult(w, 2, 1)                              # abrasive Bram insults kind Ana; Bram is not guilty
        await step(w)
        await step(w, int(0.8 * DAY))
        return w
    w = run(go())
    assert actions(w, "seek_out") == [(1, "seek_out", 2, "make up")]
    assert not cn.open_concerns(w.d.minds[2])        # an abrasive offender carries no guilt


def _arrive(w, mover, target, intents):
    w.llm.script["intent"] = list(intents)
    w.fb.add_event("social", [(mover, "initiator"), (target, "recipient")], f"{w.d.names[mover]} -> {w.d.names[target]}: Chitchat.",
                   [mover, target], severity=0.1, data={"interaction": "Chitchat"})


def test_arrival_chat_is_told_what_it_is_about_and_kind_intents_make_them_up(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.8 * DAY))                # Ana walks over to apologise
        _arrive(w, 1, 2, ["kind normal", "comfort normal", "kind normal", "none normal"])
        await step(w)
        return w
    w = run(go())
    line_calls = [c for c in w.llm.calls if c["kind"] == "line"]
    flat = " ".join(m[1] for c in line_calls for m in c["messages"])
    assert "has come to apologise to" in flat and "The quarrel: Ana insulted Bram" in flat
    g = grudge(w, 2)
    assert g.phase == "done" and g.ended == "made_up"
    assert any(m.etype == "makeup" for m in w.d.minds[1].memories) and any(m.etype == "makeup" for m in w.d.minds[2].memories)
    assert any("made up" in n.text and not n.major for n in w.d.chronicle.state.pending)
    guilt = [c for c in w.d.minds[1].concerns if c.kind == "guilt"]
    assert guilt and guilt[0].status == "resolved"
    assert w.d.grudges.made_up == 1


def test_an_insult_in_the_talk_escalates_once_then_fades(world):
    async def go():
        w = await world.go()
        w.d.cfg.diary_enabled = w.d.cfg.chronicle_enabled = False      # no idle job holds the second window
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.8 * DAY))
        _arrive(w, 1, 2, ["insult shout", "insult shout", "insult shout", "none normal"])
        await step(w)
        g_a = grudge(w, 2)
        a = (g_a.phase, g_a.renewals)
        await step(w, int(0.8 * DAY))                # the renewed round runs
        _arrive(w, 1, 2, ["insult shout", "insult shout", "insult shout", "none normal"])
        await step(w)
        return w, a
    w, a = run(go())
    assert a == ("avoid", 1)
    g = grudge(w, 2)
    assert g.phase == "done" and g.ended == "faded"
    assert w.d.grudges.escalated == 2 and w.d.grudges.made_up == 0
    assert len(actions(w, "avoid")) >= 2             # the renewal avoided again


def test_a_neutral_talk_retries_then_fades_after_max_attempts(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.8 * DAY))
        for _ in range(5):
            await step(w, int(0.3 * DAY))
        return w
    w = run(go())
    g = grudge(w, 2)
    assert g.phase == "done" and g.ended == "faded" and len(actions(w, "seek_out")) == w.d.cfg.grudge_max_attempts


def test_refused_seek_out_retries_later(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        w.fb.social_supported = False                # the mod answers 404
        await step(w, int(0.8 * DAY))
        a = grudge(w, 2).attempts
        w.fb.social_supported = True
        await step(w, int(0.3 * DAY))
        return w, a
    w, a = run(go())
    assert a == 1 and len(actions(w, "seek_out")) == 1


def test_no_social_actions_without_wants_or_with_effects_off(world):
    async def go(key, val):
        w = await world.go()
        w.d.bridge_cfg[key] = val
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.8 * DAY))
        return w
    w = run(go("wants_enabled", False))
    assert w.fb.social_log == [] and not w.d.minds[2].grudges


def test_effects_off_means_no_grudge_actions(tmp_path):
    from conftest import World
    w = World(tmp_path)
    run(go_off(w))
    assert w.fb.social_log == [] and not w.d.minds[2].grudges


async def go_off(w):
    await w.go()
    w.d.bridge_cfg["effects"] = "off"
    insult(w, 1, 2)
    await step(w)
    await step(w, int(0.8 * DAY))


def test_switch_off_is_todays_behaviour(tmp_path):
    from conftest import World
    w = World(tmp_path)
    w.d.cfg.grudges = False

    async def go():
        await w.go()
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.8 * DAY))
    run(go())
    assert w.fb.social_log == [] and not w.d.minds[2].grudges


def test_minors_are_safe_the_topics_are_fixed_words_and_nothing_romantic():
    for approach in (gr.APOLOGISE, gr.MAKE_UP, gr.CONFRONT):
        assert approach in ("apologise", "make up", "confront")
    from hermesvoice_agent.core import agegate
    from hermesvoice_agent.core.config import Config
    assert not agegate.violation(gr.GUILT_TEXT.format(who="Bram"), involves_minor=True)
    # a romantic interaction is not an offence
    from hermesvoice_agent.games.rimworld.concern_rules import social_offence
    e = {"type": "social", "actors": [{"id": 1, "role": "initiator"}, {"id": 2, "role": "recipient"}],
         "data": {"interaction": "RomanceAttempt"}}
    assert social_offence(e) is None


def test_social_fight_naming_its_target_is_an_offence():
    from hermesvoice_agent.games.rimworld.concern_rules import social_offence
    e = {"type": "mental_break", "actors": [{"id": 1, "role": "subject"}], "data": {"state": "SocialFighting", "target": 2}}
    assert social_offence(e) == (1, 2, "picked a fight with")
    e["data"].pop("target")
    assert social_offence(e) is None


def test_a_second_insult_while_holding_a_grudge_does_not_stack(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2); await step(w)
        insult(w, 1, 2); await step(w)
        return w
    w = run(go())
    assert len(w.d.minds[2].grudges) == 1 and len(actions(w, "avoid")) == 1


def test_finished_grudges_are_dropped_after_a_day(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2); await step(w)
        for _ in range(6):
            await step(w, int(0.3 * DAY))
        await step(w, int(1.5 * DAY))
        return w
    assert run(go()).d.minds[2].grudges == []


def test_real_time_floor_holds_phases_at_high_speed(world):
    """At 4x a game day passes in about a real minute: the phases still wait for their play-time floors."""
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        await step(w, int(0.3 * DAY), real=20)       # the avoid game time has passed, the real floor (120 s) has not
        ph_fast = grudge(w, 2).phase
        await step(w, 0, real=110)
        ph_avoid_done = grudge(w, 2).phase
        await step(w, int(0.6 * DAY), real=30)       # the cooldown game time has passed; 160 s of the 300 s floor
        seeks_early = len(actions(w, "seek_out"))
        await step(w, 0, real=150)
        return ph_fast, ph_avoid_done, seeks_early, len(actions(w, "seek_out"))
    ph_fast, ph_avoid_done, seeks_early, seeks = run(go())
    assert ph_fast == "avoid" and ph_avoid_done == "cooldown"
    assert seeks_early == 0 and seeks == 1


def test_real_time_floor_does_not_count_a_pause(world):
    async def go():
        w = await world.go()
        insult(w, 1, 2)
        await step(w)
        w.fb.paused = True
        await step(w, int(0.3 * DAY), real=500)      # wall time while paused is not play time
        paused = grudge(w, 2).phase
        w.fb.paused = False
        await step(w, 0, real=1)
        await step(w, 0, real=125)
        return paused, grudge(w, 2).phase
    paused, after = run(go())
    assert paused == "avoid" and after == "cooldown"

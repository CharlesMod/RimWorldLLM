"""Speech register: lasting base (block B), temporary states (scene, block G), and the inline prompts."""
import copy

import pytest

from conftest import run
from hermesvoice_agent.core import speech
from hermesvoice_agent.core.minds import Mind, Memory
from hermesvoice_agent.core.prompt import Scene, block_b, block_e, block_g, build_prefix
from hermesvoice_agent.games.rimworld import RimWorldAdapter, register as R
from hermesvoice_agent.games.rimworld.builders import build_fixed_bio, build_voice_card, facts_hash
from hermesvoice_agent.games.rimworld.fakes import default_pawn, rich_pawn

AD = RimWorldAdapter()
DAY = 60000


def pawn(**kw):
    kw.setdefault("skills", [])
    kw.setdefault("traits", [])
    return default_pawn(1, "Ana", **kw)


def intel(level, passion="none"):
    return [{"skill": "Intellectual", "level": level, "passion": passion}]


def cond(d, **kw):
    return {"def": d, "label": d.lower(), "part": kw.pop("part", ""), "severity": kw.pop("severity", 0.5), "tended": False, **kw}


def with_health(p, conditions=(), **h):
    p["health"] = {**p["health"], "conditions": list(conditions), **h}
    return p


# ---- base register ------------------------------------------------------------------------
@pytest.mark.parametrize("level,passion,want", [
    (1, "none", 2), (2, "none", 2), (5, "none", 3), (7, "none", 3), (7, "major", 4), (10, "none", 4),   # 3.5 rounds up
    (10, "major", 4), (14, "none", 4), (14, "major", 5), (18, "minor", 5), (20, "major", 5), (0, "none", 2),
])
def test_intellectual_skill_and_passion(level, passion, want):
    assert R.base_register(pawn(skills=intel(level, passion))) == want


def test_no_intellectual_skill_is_plain():
    assert R.base_register(pawn()) == 3
    assert R.base_register(pawn(skills=[{"skill": "Cooking", "level": 20, "passion": "major"}])) == 3


def test_too_smart_trait_and_genes():
    assert R.base_register(pawn(traits=[{"def": "TooSmart", "label": "too smart"}])) == 4
    assert R.base_register(pawn(traits=[{"def": "x", "label": "Too Smart"}])) == 4          # label fallback
    assert R.base_register(pawn(genes=[{"def": "AptitudeRemarkable_Intellectual", "label": "x"}])) == 4
    assert R.base_register(pawn(genes=[{"def": "aptitudestrong_intellectual", "label": "x"}])) == 4    # 3.5 rounds up, any case
    assert R.base_register(pawn(genes=[{"def": "AptitudeTerrible_Intellectual", "label": "x"}])) == 2
    assert R.base_register(pawn(genes=[{"def": "AptitudePoor_Intellectual", "label": "x"}])) == 3     # 2.5 rounds up
    assert R.base_register(pawn(genes=[{"def": "Learning_Slow", "label": "slow study"}])) == 3         # 2.75
    assert R.base_register(pawn(genes=[{"def": "?", "label": "Quick Study"}, {"def": "?", "label": "dark hair"}])) == 3   # label, 3.25
    assert R.base_register(pawn(genes=["quick study", "remarkable intellectual aptitude"])) == 4       # plain labels, 4.25
    assert R.base_register(pawn(genes=[{"def": "AptitudeTerrible_Intellectual", "label": "x"}], skills=intel(1))) == 1


def test_dementia_lowers_the_base():
    p = with_health(pawn(skills=intel(10)), [cond("Dementia")])
    assert R.base_register(p) == 2                                                  # 3.5 - 1.5 = 2.0 (10 -> +0.5)
    assert R.base_register(with_health(pawn(), [cond("Alzheimers")])) == 2          # 1.5 rounds up


@pytest.mark.parametrize("age,want", [(6, 1), (10, 2), (15, 4), (30, 5)])
def test_child_ages_cap_the_register(age, want):
    p = pawn(age=age, skills=intel(20, "major"), traits=[{"def": "TooSmart", "label": "too smart"}])
    assert R.base_register(p) == want


def test_clamped_low():
    p = pawn(skills=intel(0), genes=[{"def": "AptitudeTerrible_Intellectual", "label": "x"}],
             health={"conditions": [cond("Dementia")], "missing_parts": []})
    assert R.base_register(p) == 1


# ---- temporary states ----------------------------------------------------------------------
def test_drunk_lowers_then_recovers():
    p = pawn(skills=intel(15))
    assert R.base_register(p) == 4 and R.effective_register(p) == 4
    drunk = with_health(copy.deepcopy(p), [cond("AlcoholHigh", severity=0.3)])
    assert R.effective_register(drunk) == 3 and R.base_register(drunk) == 4          # base unchanged
    very = with_health(copy.deepcopy(p), [cond("AlcoholHigh", severity=0.7)])
    assert R.effective_register(very) == 2 and "very drunk" in R.temp_states(very)[0][0]
    sober = with_health(copy.deepcopy(p), [])
    assert R.temp_states(sober) == [] and R.speech_now(sober) == ("", "")           # a later snapshot drops it
    assert R.effective_register(sober) == 4


def test_drug_highs_and_clamp_at_one():
    for d in ("SmokeleafHigh", "GoJuiceHigh", "YayoHigh", "FlakeHigh", "PsychiteTeaHigh", "WakeUpHigh"):
        assert R.effective_register(with_health(pawn(), [cond(d)])) == 2, d
    p = with_health(pawn(skills=intel(1)), [cond("AlcoholHigh", severity=0.9)], consciousness=0.2)
    assert R.effective_register(p) == 1                                              # base 2, -2, -2 clamps


def test_consciousness_thresholds():
    assert R.temp_states(with_health(pawn(), consciousness=0.9)) == []
    assert R.temp_states(with_health(pawn(), consciousness=0.8)) == []
    assert R.temp_states(with_health(pawn(), consciousness=0.7))[0][1] == -1
    assert "foggy" in R.temp_states(with_health(pawn(), consciousness=0.6))[0][0]
    assert R.temp_states(with_health(pawn(), consciousness=0.49)) == [("dazed, struggles to form words", -2)]
    assert R.temp_states(with_health(pawn(), [cond("Anesthetic")], consciousness=1.0))[0][1] == -2


def test_head_injury_talking_and_rest():
    assert R.temp_states(with_health(pawn(), [cond("Gunshot", part="Brain")]))[0][1] == -1
    assert R.temp_states(with_health(pawn(), [cond("Dementia", part="Brain")])) == []     # lasting, not temporary
    t = R.temp_states(with_health(pawn(), talking=0.4))
    assert t[0][1] == -1 and "not the vocabulary" in t[0][0]
    assert R.temp_states(with_health(pawn(), talking=0.5)) == []
    p = pawn(); p["needs"]["rest"] = 0.05
    assert R.temp_states(p) == [("exhausted", -1)]
    p["needs"]["rest"] = 0.1
    assert R.temp_states(p) == []


def test_states_stack_worst_first():
    p = with_health(pawn(skills=intel(15)), [cond("AlcoholHigh", severity=0.2)], consciousness=0.6)
    ts = R.temp_states(p)
    assert [s for _, s in ts] == [-1, -1] and R.effective_register(p) == 2
    p = with_health(pawn(), [cond("AlcoholHigh", severity=0.8), cond("SmokeleafHigh")])
    assert [s for _, s in R.temp_states(p)] == [-2, -1]


# ---- wording -------------------------------------------------------------------------------
def mk_mind(p, edited=False):
    m = Mind(actor_id=p["id"], name="Ana")
    m.fixed = build_fixed_bio(p)
    m.voice = build_voice_card(p)
    m.voice.edited_by_player = edited
    return m


def test_block_b_has_words_line_beside_the_style():
    p = pawn(skills=intel(15))
    b = block_b(mk_mind(p))
    assert "Words: clear and well-spoken, a good vocabulary (your style above still applies)." in b
    assert b.index("Register:") < b.index("Words:") < b.index("Habits:")
    assert not any(ch.isdigit() for ch in b.split("Words:")[1].split("\n")[0])         # no numbers in the prompt line


def test_player_edited_card_has_no_base_line_but_keeps_its_wording():
    p = pawn(skills=intel(15))
    m = mk_mind(p, edited=True)
    m.voice.register_line = "Talks like a pirate."
    b = block_b(m)
    assert "Words:" not in b and "Register: Talks like a pirate." in b
    sc = Scene(speech_now=R.speech_now(with_health(p, [cond("AlcoholHigh")]))[0])
    assert "Right now: drunk" in block_e(sc)                                           # the scene still carries it


def test_scene_line_only_with_a_temp_state():
    sober = pawn(skills=intel(15))
    sc = AD.build_scene(sober, {}, "t", {}, set())
    assert "Right now" not in block_e(sc) and sc.speech_now == "" and sc.speech_short == ""
    drunk = with_health(copy.deepcopy(sober), [cond("AlcoholHigh", severity=0.2)])
    sc = AD.build_scene(drunk, {}, "t", {}, set())
    e = block_e(sc)
    assert "Right now: drunk: words slur" in e and "plain everyday speech" in e
    assert sc.speech_short == "plain everyday speech"


def test_block_g_phrase_only_with_a_temp_state():
    m = mk_mind(pawn())
    assert "words right now" not in block_g(m, "Ana", 1)
    g = block_g(m, "Ana", 1, speech="simple words and short plain sentences")
    assert "Your words right now: simple words and short plain sentences." in g
    assert g.count("\n") < 6                                                            # stays short


def test_words_in_the_card_follow_the_facts_hash():
    p = pawn(skills=intel(15))
    h = facts_hash(p)
    q = copy.deepcopy(p); q["skills"] = intel(3)
    assert facts_hash(q) != h
    assert build_fixed_bio(q).extras["words"] == R.REGISTER.to_words(3)


# ---- through the director --------------------------------------------------------------------
def test_drunk_pawn_gets_the_scene_line_and_g_phrase_in_the_line_calls(world):
    async def go():
        w = await world.go()
        w.fb.pawns[1]["skills"] = intel(15)
        with_health(w.fb.pawns[1], [cond("AlcoholHigh", severity=0.8)])
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana and Bram argued.", [1, 2], 0.6)
        w.d.cfg.quotas.min_gap_game_ambient = 0
        w.d.rng.seed(0)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    lines = [c for c in w.llm.calls if c["kind"] == "line" and c["actor"] == 1]
    assert lines
    sys_, user = lines[0]["messages"][0][1], lines[0]["messages"][-1][1]
    assert "Right now: very drunk" in sys_ and "Your words right now:" in user
    other = [c for c in w.llm.calls if c["kind"] == "line" and c["actor"] == 2][0]["messages"]
    assert "Right now:" not in other[0][1] and "words right now" not in other[-1][1]


def test_dream_prompt_has_the_register_line(world):
    from test_dreams import ready, sleep, step, calls
    async def go():
        w = await ready(world)
        w.fb.pawns[1]["skills"] = intel(1)
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    flat = "\n".join(t for _, t in calls(w)[0]["messages"])
    assert "Write it the way Ana would: simple words and short plain sentences." in flat


def test_diary_prompt_has_the_register_line_and_a_child_writes_simply(world):
    async def go():
        w = await world.go()
        w.fb.pawns[1].update(age=6, skills=intel(20, "major"))
        w.d.minds[1].memories.append(Memory(id=w.d.minds[1].next_mem_id(), game=5000, text="I saw Bram eat alone."))
        w.llm.script["diary"] = ["Today I kept my head down and got the stew finished before dark."]
        w.fb.tick = int(0.7 * DAY)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    flat = "\n".join(t for _, t in [c for c in w.llm.calls if c["kind"] == "diary"][0]["messages"])
    assert "Write it the way Ana would: very simple words" in flat


def test_thought_prompt_has_the_register_line(world):
    async def go():
        w = await world.go()
        w.d.cfg.thought_enabled = True
        w.fb.pawns[1]["traits"] = [{"def": "TooSmart", "label": "too smart"}]
        w.fb.pawns[1]["skills"] = intel(15, "major")
        w.d.minds[1].memories.append(Memory(id=w.d.minds[1].next_mem_id(), game=1000, text="I saw Bram eat alone.", people=[2]))
        w.llm.script["thought"] = ["Bram should not eat alone."]
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    flat = "\n".join(t for _, t in [c for c in w.llm.calls if c["kind"] == "thought"][0]["messages"])
    assert "Write it the way Ana would: articulate, a wide vocabulary" in flat


def test_bystander_prompt_has_the_register_line():
    from hermesvoice_agent.core import bystanders as B
    m = Mind(actor_id=3, name="Cy")
    msgs = B.messages(AD, m, {1: "Ana", 2: "Bram", 3: "Cy"}, [1, 2], "x.", False, words="plain everyday speech")
    assert "Write it the way Cy would: plain everyday speech." in "\n".join(t for _, t in msgs)
    assert "Write it the way" not in "\n".join(t for _, t in B.messages(AD, m, {1: "Ana", 2: "Bram"}, [1, 2], "x.", False))


def test_register_line_helper():
    assert speech.register_line("Ana", None) == ""
    assert speech.register_line("Ana", "plain").startswith("Write it the way Ana would: plain.")


def test_temporary_conditions_stay_out_of_frozen_bio():
    from hermesvoice_agent.games.rimworld.builders import build_fixed_bio
    from hermesvoice_agent.games.rimworld.fakes import default_pawn
    p = default_pawn(1, "Ana")
    p["health"]["conditions"] = [{"def": "AlcoholHigh", "label": "drunk", "severity": 0.7},
                                 {"def": "Scar", "label": "old scar", "part": "Left leg"}]
    text = build_fixed_bio(p).text
    assert "drunk" not in text and "old scar" in text

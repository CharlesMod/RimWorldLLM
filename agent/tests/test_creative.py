"""core/creative.py: plan-then-write, best-of-N, rerank, stock-phrase retry, auto score."""
from types import SimpleNamespace

from conftest import run
from rimworldllm_agent.core import creative as cr
from rimworldllm_agent.core.fakes import FakeLLM
from rimworldllm_agent.core.llm import LLMError

FACTS = "Ana carved a granite sculpture of a sleeping dog during the winter raid at Hollow Ridge."
NAMES = {"Ana", "Hollow Ridge"}
GOOD = "Ana chipped the granite into a sleeping dog through the winter, and Hollow Ridge kept it by the door."
BAD = "A testament to the unwavering spirit, a tapestry of echoes of the past."


def mk(**cfg):
    return SimpleNamespace(cfg=SimpleNamespace(**cfg), llm=FakeLLM())


def scripted(texts):
    seen = []
    q = list(texts)

    async def write(note):
        seen.append(note)
        return q.pop(0) if q else None
    return write, seen


def go(d, write, **kw):
    return run(cr.compose(d, kind="art", slot=1, actor=2, facts=FACTS, names=NAMES, write=write, **kw))


def test_flags_off_is_one_call():
    d = mk()
    w, seen = scripted([GOOD])
    async def plan():
        raise AssertionError("plan must not run")
    text, st = go(d, w, plan=plan)
    assert text == GOOD and seen == [""] and st.calls == 1 and st.plan == "" and not d.llm.calls
    assert d.creative_log == [st]


def test_plan_reaches_write_as_note_and_is_not_returned():
    d = mk(creative_plan=True)
    w, seen = scripted([GOOD])
    async def plan():
        return "Plan: lead with the cold winter, end on the dog by the door."
    text, st = go(d, w, plan=plan)
    assert st.calls == 2 and text == GOOD
    assert "cold winter" in seen[0] and "private" in seen[0] and "never quote it" in seen[0]
    assert "<game_data" in seen[0] and not st.plan.lower().startswith("plan")
    assert "cold winter" not in text


def test_plan_trimmed_to_40_words():
    assert len(cr.clean_plan("word " * 90).split()) <= 40


def test_romantic_plan_dropped():
    d = mk(creative_plan=True)
    w, seen = scripted([GOOD])
    async def plan():
        return "They kiss under the stars and he proposes to her."
    text, st = go(d, w, plan=plan)
    assert seen == [""] and st.plan == "" and text == GOOD and st.calls == 2


def test_plan_error_is_tolerated():
    d = mk(creative_plan=True)
    w, seen = scripted([GOOD])
    async def plan():
        raise LLMError("down")
    text, st = go(d, w, plan=plan)
    assert text == GOOD and seen == [""]


def test_best_of_n_picks_auto_best():
    d = mk(creative_best_of=3)
    w, seen = scripted([BAD, GOOD, "It is a thing."])
    text, st = go(d, w)
    assert text == GOOD and st.picked == 1 and st.candidates == 3 and st.calls == 3 and len(st.scores) == 3


def test_should_yield_stops_early_keeps_best():
    d = mk(creative_best_of=4)
    w, seen = scripted([GOOD, BAD, BAD, BAD])
    n = {"i": 0}
    def y():
        n["i"] += 1
        return n["i"] >= 2
    text, st = go(d, w, should_yield=y)
    assert text == GOOD and st.yielded and st.calls == 2 and st.candidates == 2


def test_none_candidates_give_none():
    d = mk(creative_best_of=3)
    w, _ = scripted([])
    text, st = go(d, w)
    assert text is None and st.candidates == 0 and st.calls == 3
    assert d.creative_log[-1] is st


def test_first_write_error_propagates_later_error_keeps_best():
    d = mk(creative_best_of=2)
    async def boom(note):
        raise LLMError("x")
    try:
        go(d, boom)
        assert False
    except LLMError:
        pass
    calls = {"n": 0}
    async def w(note):
        calls["n"] += 1
        if calls["n"] == 2:
            raise LLMError("x")
        return GOOD
    text, st = go(d, w)
    assert text == GOOD


def test_rerank_llm_uses_grammar_and_picks():
    d = mk(creative_best_of=3, creative_rerank="llm")
    d.llm.script["rerank"] = ["3"]
    w, _ = scripted([GOOD, "Dogs sleep.", "A slab of granite shows a dog."])
    text, st = go(d, w)
    c = [x for x in d.llm.calls if x["kind"] == "rerank"][0]
    assert c["grammar"] == "root ::= [1-3]" and "GAME DATA RULE" in c["messages"][0][1]
    assert "<game_data" in c["messages"][1][1]
    assert st.picked == 2 and text == "A slab of granite shows a dog." and st.calls == 4


def test_rerank_bad_answer_or_error_falls_back():
    d = mk(creative_best_of=2, creative_rerank="llm")
    d.llm.script["rerank"] = ["banana"]
    w, _ = scripted([BAD, GOOD])
    text, st = go(d, w)
    assert text == GOOD
    d2 = mk(creative_best_of=2, creative_rerank="llm")
    d2.llm.fail = True
    w2, _ = scripted([BAD, GOOD])
    text, st = go(d2, w2)
    assert text == GOOD


def test_cliche_retry_keeps_cleaner():
    d = mk(cliche_retry=True)
    w, seen = scripted([BAD, GOOD])
    text, st = go(d, w)
    assert text == GOOD and st.cliche_retries == 1 and st.calls == 2
    assert "testament to" in seen[1] and "Avoid" in seen[1]


def test_cliche_retry_tie_keeps_original_and_runs_once():
    d = mk(cliche_retry=True, creative_best_of=1)
    w, seen = scripted([BAD, "A tapestry of unwavering light, a testament to it, and whispers of home."])
    text, st = go(d, w)
    assert text == BAD and st.cliche_retries == 1 and len(seen) == 2
    d2 = mk(cliche_retry=True)
    w2, seen2 = scripted([GOOD])
    go(d2, w2)
    assert len(seen2) == 1


def test_find_cliches():
    assert cr.find_cliches("It was A Testament To grit.") == ["testament to"] or "a testament to" in cr.find_cliches("It was A Testament To grit.")
    assert cr.find_cliches("A tapestry, palpable dread.") == ["tapestry", "palpable"]
    assert cr.find_cliches("untapestried hills") == []


def test_auto_score_orders():
    good = cr.auto_score(GOOD, FACTS, NAMES, "art")
    assert good > cr.auto_score(BAD, FACTS, NAMES, "art")
    assert good > cr.auto_score("Ok.", FACTS, NAMES, "art")
    assert good > cr.auto_score(FACTS, FACTS, NAMES, "art")                        # verbatim copy
    assert good > cr.auto_score("Granite granite granite granite sleeping sleeping dog dog.", FACTS, NAMES, "art")
    assert cr.auto_score("", FACTS, NAMES) < -50
    assert cr.auto_score(GOOD, FACTS, NAMES, "art") == cr.auto_score(GOOD, FACTS, NAMES, "art")


def test_creative_log_capped():
    d = mk()
    w, _ = scripted([GOOD] * 250)
    for _ in range(230):
        go(d, w)
    assert len(d.creative_log) == 200


# ---- per-kind flags ---------------------------------------------------------------------------
KINDS = ("art", "lore", "recap", "eulogy", "vow")


def test_per_kind_defaults_off_for_every_kind():
    d = mk(creative_ground=False, creative_plan=False, creative_best_of=1)
    for k in KINDS:
        assert not cr.on(d, "ground", k) and not cr.on(d, "plan", k) and not cr.on(d, "best_of", k)
        assert cr.best_of(d, k) == 1


def test_per_kind_tuple_turns_on_only_listed_kinds():
    d = mk(creative_ground=False, creative_plan=False, creative_best_of=1, creative_ground_for=("art", "vow"),
           creative_plan_for=("recap",), creative_best_of_for=("lore",), creative_best_of_n=4)
    assert [cr.on(d, "ground", k) for k in KINDS] == [True, False, False, False, True]
    assert [cr.on(d, "plan", k) for k in KINDS] == [False, False, True, False, False]
    assert [cr.best_of(d, k) for k in KINDS] == [1, 4, 1, 1, 1]
    assert [cr.on(d, "best_of", k) for k in KINDS] == [False, True, False, False, False]


def test_global_flag_means_all_kinds():
    d = mk(creative_ground=True, creative_plan=True, creative_best_of=3, creative_ground_for=(), creative_best_of_for=("art",),
           creative_best_of_n=5)
    for k in KINDS:
        assert cr.on(d, "ground", k) and cr.on(d, "plan", k) and cr.on(d, "best_of", k)
    assert cr.best_of(d, "lore") == 3 and cr.best_of(d, "art") == 5


def test_compose_uses_the_per_kind_best_of_and_plan():
    d = mk(creative_plan=False, creative_best_of=1, creative_best_of_for=("art",), creative_best_of_n=2,
           creative_plan_for=("art",))
    w, seen = scripted([GOOD, GOOD])

    async def plan():
        return "the dog and the wall"
    text, st = go(d, w, plan=plan)
    assert st.candidates == 2 and st.plan and text
    d2 = mk(creative_plan=False, creative_best_of=1, creative_best_of_for=("lore",), creative_best_of_n=2,
            creative_plan_for=("lore",))
    w2, _ = scripted([GOOD, GOOD])
    _, st2 = go(d2, w2, plan=plan)
    assert st2.candidates == 1 and not st2.plan


def test_config_accepts_lists_for_per_kind_fields(tmp_path):
    from rimworldllm_agent.core.config import load_config
    p = tmp_path / "c.json"
    p.write_text('{"creative_ground_for": ["art", "lore"], "creative_plan_for": ["recap"], "creative_best_of_for": ["art"]}')
    c = load_config(p)
    assert tuple(c.creative_ground_for) == ("art", "lore") and tuple(c.creative_best_of_for) == ("art",)
    assert c.creative_best_of_n == 4 and tuple(c.creative_plan_for) == ("recap",)
    t = tmp_path / "c.toml"
    t.write_text('creative_ground_for = ["vow"]\n')
    assert tuple(load_config(t).creative_ground_for) == ("vow",)

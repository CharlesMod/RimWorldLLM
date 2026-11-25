import json
import random

import pytest

from conftest import run
from hermesvoice_agent.core import loopguard as G


def test_similarity_short_replies_are_legitimate():
    for line in ("Yes.", "No.", "Fine.", "Copy that.", "Okay then."):
        assert G.similarity(line, line) == 0.0
    assert G.find_repeat("Yes.", 2, [(1, "Yes."), (2, "Yes.")]) is None


def test_verbatim_and_near_repeats_are_caught():
    prior = [(1, "Okay so I'm bored, like seriously nothing to do right now..."), (2, "Hm. Go feed the animals.")]
    assert G.find_repeat("Okay so I'm bored, like seriously nothing to do right now...", 1, prior)   # verbatim, own
    h = G.find_repeat("Okay so I am bored, seriously, nothing to do right now.", 1, prior)
    assert h and h.own
    # parroting the other's line is also a repeat
    h = G.find_repeat("Hm. Go feed the animals now.", 1, prior)
    assert h and not h.own
    # a cut-down copy of the speaker's own line
    assert G.find_repeat("I watch the perimeter.", 2, [(2, "I watch the perimeter. The muffalo is good meat.")])


def test_habit_openers_do_not_make_lines_alike():
    prior = [(1, "Okay so I'm trying, but this rifle feels like it's gonna snap...")]
    assert G.find_repeat("Okay so Biscuit brought some herbs for me to check out...", 1, prior) is None
    assert G.find_repeat("Naturally, the hunger is a fleeting affliction in this bleak establishment.", 2,
                         [(2, "Naturally, the provisions are as sparse as the prospects for this dreary existence.")]) is None


def test_own_lines_across_conversations_need_a_higher_bar():
    own = ["Hm. Back hurts. Get that pan over here."]
    assert G.find_repeat("Hm. Back hurts. Get that pan over here.", 1, [], own)
    assert G.find_repeat("Hm. Back hurts, so move the crate.", 1, [], own) is None


def test_offline_bench_numbers():
    # the saved bench conversations: no false alarm, most labelled loops caught (bench/models/loopcheck.py)
    import importlib.util, pathlib
    p = pathlib.Path(__file__).parents[2] / "bench/models/loopcheck.py"
    if not (p.parent / "results").exists():
        pytest.skip("bench results not present")
    spec = importlib.util.spec_from_file_location("loopcheck", p)
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    per = m.evaluate(G.THRESHOLD)
    tp = sum(x["tp"] for x in per.values()); fp = sum(x["fp"] for x in per.values()); fn = sum(x["fn"] for x in per.values())
    assert fp == 0 and tp >= 11 and tp / (tp + fn) >= 0.7


class Always(random.Random):
    def random(self):
        return 0.0


def _start_conv(w):
    w.d.rng = Always()
    w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], 0.7)
    w.d.cfg.quotas.pair_per_day = 9


def test_looping_fake_is_retried_then_ended_without_a_repeat(world):
    async def go():
        w = await world.go()
        w.d.cfg.repeat_retries = 1
        # Ana opens, Bram answers, then Ana loops on her own opener twice (first try and the retry)
        w.llm.script["line"] = ["Where did you put the spare medicine for the leg?",
                                "Hm. In the cold room, second shelf.",
                                "Where did you put the spare medicine for the leg?",      # repeat
                                "Where did you put the spare medicine for that leg?"]     # retry repeats too
        _start_conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    spoken = [s["text"] for s in w.d.said]
    assert spoken == ["Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf."]
    assert len({t for t in spoken}) == len(spoken)                     # never a repeat on screen
    assert (1, "repeat") in w.d.dropped
    st = w.d.loop
    assert (st.caught, st.retried, st.recovered, st.ended) == (1, 1, 0, 1)
    assert st.checked == 3 and [t["refused"] for t in st.trace] == [False, False, True, True]
    # the retry's prompt carried the reason
    retry_prompt = "\n".join(t for _, t in [c for c in w.llm.calls if c["kind"] == "line"][3]["messages"])
    assert "refused because it repeats your own earlier line" in retry_prompt


def test_retry_that_is_clean_continues_the_conversation(world):
    async def go():
        w = await world.go()
        w.d.cfg.repeat_retries = 1
        w.llm.script["line"] = ["Where did you put the spare medicine for the leg?",
                                "Hm. In the cold room, second shelf.",
                                "Hm. In the cold room, second shelf.",     # Ana parrots Bram
                                "Thanks, I will fetch it before the bleeding gets worse.",
                                "Mind the ice on the step as you go past the freezer."]
        _start_conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    spoken = [s["text"] for s in w.d.said]
    assert spoken[:3] == ["Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                          "Thanks, I will fetch it before the bleeding gets worse."]
    st = w.d.loop
    assert st.caught >= 1 and st.recovered >= 1 and st.retried == st.caught
    assert [(t['attempt'], t['refused']) for t in st.trace[:4]] == [(0, False), (0, False), (0, True), (1, False)]
    assert len(set(spoken)) == len(spoken)


def test_guard_can_be_turned_off_and_retries_zero_ends_at_once(world):
    async def go(retries):
        w = await world.go()
        w.d.cfg.repeat_retries = retries
        w.llm.script["line"] = ["Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                                "Where did you put the spare medicine for the leg?"]
        _start_conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go(0))
    assert len(w.d.said) == 2 and w.d.loop.ended == 1 and w.d.loop.retried == 0


def test_own_recent_lines_carry_across_conversations(world):
    async def go():
        w = await world.go()
        w.d._remember_own(1, "Where did you put the spare medicine for the leg?")
        w.llm.script["line"] = ["Where did you put the spare medicine for the leg?", "Hm. Cold room, second shelf, friend.",
                                "Fetch it quickly then, before the leg gets worse.",
                                "Mind the ice on the step as you go past the freezer."]
        _start_conv(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.d.loop.caught >= 1 and w.d.loop.trace[0]["kind"] == "own" and w.d.loop.trace[0]["refused"]

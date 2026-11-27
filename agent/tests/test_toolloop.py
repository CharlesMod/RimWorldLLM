"""The tool harness against a scripted fake model that misbehaves in every listed way. No GPU, no network."""
import asyncio
import json
import random

import pytest

from conftest import run
from rimworldllm_agent.core.toolloop import (Action, BackendError, CHANGE, IMPORTANCE, Param, Reply, Scale, ToolHarness, ToolSpec,
                                              build_grammar, choose_protocol, parse_text, probe, run_loop, Capabilities)
from rimworldllm_agent.core.toolloop import harness as harness_mod


def tools():
    return [ToolSpec("say", "Say a line.", [Param("line", max_len=60)]),
            ToolSpec("remember", "Store a fact.", [Param("fact"), Param("importance", "scale", scale=IMPORTANCE)]),
            ToolSpec("set_mood", "Feelings.", [Param("target", "enum", choices=["Pip", "Lux"]), Param("delta", "scale", scale=CHANGE)],
                     result_scales={"opinion": CHANGE}),
            ToolSpec("count", "A number.", [Param("n", "int", lo=1, hi=5)]),
            ToolSpec("do_nothing", "Nothing.")]


class Fake:
    """Replies come from a script: Reply, str, an Exception to raise, or a callable(messages)->those. The last entry repeats."""
    def __init__(self, script):
        self.script, self.calls = list(script), []

    async def chat(self, messages, *, grammar=None, tools=None, tool_choice=None, max_tokens=160, temperature=0.2, timeout=30.0):
        self.calls.append({"messages": messages, "grammar": grammar, "tools": tools, "timeout": timeout})
        item = self.script.pop(0) if len(self.script) > 1 else self.script[0]
        if callable(item):
            item = item(messages)
        if isinstance(item, BaseException):
            raise item
        if asyncio.iscoroutine(item):
            item = await item
        return item if isinstance(item, Reply) else Reply(text=item)


def J(**kw):
    return json.dumps(kw)


def call(tool, **args):
    return json.dumps({"tool": tool, "args": args})


class Rec:
    def __init__(self):
        self.calls = []

    def __call__(self, name, values):
        self.calls.append((name, values))
        return {"opinion": values.get("delta", 0)} if name == "set_mood" else "ok"


def go(fake, rec=None, protocol="grammar", **kw):
    rec = rec or Rec()
    kw.setdefault("fallback", Action("call", "do_nothing"))
    r = run(run_loop(fake, tools(), system="You are a cook.", user="Do things.", executor=rec, protocol=protocol, **kw))
    return r, rec


def test_happy_path_call_then_final_and_scale_translation():
    f = Fake([call("remember", fact="Lux stole bread", importance="very important"), J(final="Noted.")])
    r, rec = go(f)
    assert r.outcome == "final" and r.final_text == "Noted." and r.retries == 0 and r.fallbacks == 0
    assert rec.calls == [("remember", {"fact": "Lux stole bread", "importance": 4})]       # words became the game's number
    # we own the tool-result turn: plain text in a user turn, no tool role, grammar on every step
    second = f.calls[1]["messages"]
    assert second[-1]["role"] == "user" and "Result of remember: ok" in second[-1]["content"]
    assert all(m["role"] != "tool" for m in second) and all(c["grammar"] for c in f.calls)


def test_numbers_shown_back_as_words():
    f = Fake([call("set_mood", target="Pip", delta="a little better"), J(done=True)])
    r, rec = go(f)
    assert rec.calls[0][1]["delta"] == 1
    assert 'Result of set_mood: {"opinion": "a little better"}' in f.calls[1]["messages"][-1]["content"]


def test_empty_replies_retry_then_fallback_never_silent():
    f = Fake([""])
    r, rec = go(f)
    assert r.outcome == "fallback" and r.fallbacks == 1 and r.retries == 2 and len(f.calls) == 3
    assert rec.calls == [("do_nothing", {})]
    assert len(r.failures) == 3 and all("empty" in x for x in r.failures)


def test_retry_prompt_states_the_reason_and_second_try_recovers():
    f = Fake(["I think I will say something", call("say", line="Hello."), J(done=True)])
    r, rec = go(f)
    assert r.retries == 1 and r.fallbacks == 0 and rec.calls == [("say", {"line": "Hello."})]
    assert "rejected" in f.calls[1]["messages"][-1]["content"] and "not one JSON object" in f.calls[1]["messages"][-1]["content"]


def test_hallucinated_tool_is_never_executed():
    f = Fake([call("launch_missiles", target="Pip")])
    r, rec = go(f)
    assert r.outcome == "fallback" and rec.calls == [("do_nothing", {})]
    assert "unknown tool" in r.failures[0] and "say, remember" in r.failures[0]


@pytest.mark.parametrize("bad", [
    call("count", n=100), call("count", n=-20), call("count", n="three"), call("count", n=2.5),
    call("remember", fact="x", importance=100), call("remember", fact="x", importance="pretty important"),
    call("set_mood", target="Zed", delta="worse"), call("say", line=""), call("say", line="x" * 200), call("say"),
    call("say", line="hi", extra=1), call("say", line="<|tool_call>call:say{x}"), call("say", line=["a"])])
def test_invalid_arguments_never_reach_the_executor_in_grammar_mode(bad):
    f = Fake([bad])
    r, rec = go(f)
    assert rec.calls == [("do_nothing", {})] and r.outcome == "fallback" and r.retries == 2


def test_tolerant_mode_maps_free_text_to_the_nearest_bin_but_still_rejects_range_errors():
    f = Fake([call("remember", fact="x", importance="pretty important"), call("count", n=100), J(done=True)])
    r, rec = go(f, protocol="json")
    assert rec.calls[0] == ("remember", {"fact": "x", "importance": 3})
    assert r.retries == 1 and r.outcome == "done"


def test_endless_tool_calls_end_at_the_step_budget_and_last_step_forbids_calls():
    n = iter(range(1000))
    f = Fake([lambda m: call("say", line=f"line {next(n)}")])
    r, rec = go(f, max_steps=3)
    assert r.outcome == "stopped" and len(rec.calls) == 2 and len(r.executed) == 2
    assert r.reason == "retries_exhausted"          # the closing step refused every further call
    last_grammar = [c["grammar"] for c in f.calls if c["grammar"]][-1]
    assert "call0" not in last_grammar and "final" in last_grammar            # the closing step cannot call a tool


def test_repeating_the_same_call_is_refused_and_ends_the_loop():
    f = Fake([call("say", line="Hi.")])
    r, rec = go(f, max_steps=6)
    assert rec.calls == [("say", {"line": "Hi."})]
    assert r.outcome == "stopped" and r.reason == "repeat"
    assert any("already" in x for x in r.failures)


def test_fallback_after_actions_is_opt_in():
    f = Fake([call("say", line="Hi."), "garbage"])
    r, rec = go(f, fallback_after_actions=True)
    assert r.outcome == "fallback" and rec.calls[-1] == ("do_nothing", {})
    r, rec = go(Fake([call("say", line="Hi."), "garbage"]))
    assert r.outcome == "stopped" and rec.calls == [("say", {"line": "Hi."})]


def test_timeout_falls_back_within_the_time_budget():
    async def hang(messages):
        await asyncio.sleep(30)
    f = Fake([lambda m: hang(m)])
    import time
    t0 = time.monotonic()
    r, rec = go(f, time_budget_s=0.6, step_timeout_s=0.2)
    assert time.monotonic() - t0 < 3 and r.outcome == "fallback" and rec.calls == [("do_nothing", {})]
    assert any(t.kind == "timeout" for t in r.trace)


@pytest.mark.parametrize("exc", [BackendError("HTTP 500"), RuntimeError("boom"), ValueError("x"), ConnectionError("down")])
def test_backend_faults_never_raise(exc):
    r, rec = go(Fake([exc]))
    assert r.outcome == "fallback" and not r.completed and rec.calls == [("do_nothing", {})]
    assert all(t.kind == "backend_error" for t in r.trace)


def test_executor_fault_is_not_a_model_fault_and_is_told_to_the_model():
    def bad(name, values):
        raise RuntimeError("door jammed")
    f = Fake([call("say", line="Hi."), J(final="Sorry.")])
    r, _ = go(f, rec=bad)
    assert r.outcome == "final" and r.retries == 0 and r.executed[0].error.startswith("RuntimeError")
    assert "door jammed" in f.calls[1]["messages"][-1]["content"]


def test_validator_hook_can_reject_names_and_triggers_a_retry_with_its_reason():
    def v(name, values):
        return "Zed is not here" if values.get("line") == "Zed" else None
    f = Fake([call("say", line="Zed"), call("say", line="Pip"), J(done=True)])
    r, rec = go(f, validator=v)
    assert rec.calls == [("say", {"line": "Pip"})] and "Zed is not here" in f.calls[1]["messages"][-1]["content"]


def test_raw_tool_syntax_in_a_final_line_is_rejected():
    f = Fake([J(final="<|tool_call>call:say{line:<|\"|>hi<|\"|>}<tool_call|>"), J(final="call:say{x:1}"), J(final="Fine.")])
    r, _ = go(f)
    assert r.final_text == "Fine." and r.retries == 2


def test_final_line_must_be_single_line_non_empty_and_bounded():
    f = Fake([J(final="a\nb"), J(final="  "), J(final="x" * 300), J(final="ok")])
    r, _ = go(f, max_retries=3)
    assert r.final_text == "ok" and r.retries == 3


def test_fallback_can_be_the_last_valid_line_via_a_factory():
    f = Fake(["garbage"])
    r, _ = go(f, fallback=lambda res: Action("final", text="I will think on it."))
    assert r.outcome == "fallback" and r.final_text == "I will think on it."


def test_no_fallback_means_gave_up_not_an_exception():
    r, rec = go(Fake(["garbage"]), fallback=None)
    assert r.outcome == "gave_up" and rec.calls == []


def test_invalid_fallback_is_recorded_not_executed():
    r, rec = go(Fake(["garbage"]), fallback=Action("call", "count", {"n": 99}))
    assert rec.calls == [] and any("fallback invalid" in x for x in r.failures)


def test_the_trace_records_everything():
    f = Fake(["nope", call("say", line="Hi."), J(done=True)])
    r, _ = go(f)
    assert [(t.step, t.attempt, t.kind) for t in r.trace] == [(0, 0, "invalid"), (0, 1, "call"), (1, 0, "done")]
    assert r.trace[0].raw == "nope" and r.trace[0].error and all(t.ms >= 0 for t in r.trace)
    d = r.to_dict()
    assert d["outcome"] == "done" and d["trace"][0]["raw"] == "nope" and d["executed"][0]["tool"] == "say"


# ---------------------------------------------------------------- native protocol

def native_call(name, **args):
    return Reply(tool_calls=[{"id": "c1", "name": name, "arguments": json.dumps(args)}])


def test_native_protocol_empty_reply_after_a_tool_result_ends_the_loop_cleanly():
    f = Fake([native_call("say", line="Hi."), Reply(text="")])
    r, rec = go(f, protocol="native")
    assert r.outcome == "done" and r.implicit_done and rec.calls == [("say", {"line": "Hi."})]
    assert f.calls[1]["messages"][-1]["role"] == "tool" and f.calls[0]["tools"]


def test_native_protocol_empty_first_reply_is_a_failure_and_raw_gemma_syntax_is_salvaged():
    f = Fake([Reply(text=""), Reply(text='say{line:<|"|>Hi there.<|"|>}'), Reply(text="All done, boss.")])
    r, rec = go(f, protocol="native")
    assert rec.calls == [("say", {"line": "Hi there."})] and r.outcome == "final" and r.final_text == "All done, boss."
    assert r.retries == 1 and any(t.salvaged for t in r.trace)


def test_native_bad_arguments_are_rejected_like_any_other():
    f = Fake([native_call("count", n=100)])
    r, rec = go(f, protocol="native")
    assert rec.calls == [("do_nothing", {})]


# ---------------------------------------------------------------- fuzz: the invariants hold whatever the model does

GARBAGE = ["", " ", "{", "}", "null", "[]", "{}", '{"tool":null}', '{"tool":"say","args":null}', '{"tool":"say","args":{"line":5}}',
           call("say", line="ok"), call("count", n=3), call("count", n=99), J(final="fine"), J(final=""), J(done=True), J(done=False),
           "```json\n" + call("say", line="fenced") + "\n```", 'say{line:<|"|>raw<|"|>}', "<|tool_call>call:nope{}<tool_call|>", "\x00\x01", "é" * 500]


@pytest.mark.parametrize("seed", range(40))
@pytest.mark.parametrize("protocol", ["grammar", "json", "native"])
def test_fuzz_loop_always_terminates_cleanly_with_valid_executed_calls(seed, protocol):
    rng = random.Random(seed)
    script = [rng.choice(GARBAGE) for _ in range(30)] + [lambda m: rng.choice(GARBAGE)]
    f = Fake(script)
    r, rec = go(f, protocol=protocol, max_steps=4, time_budget_s=5)
    assert r.outcome in ("final", "done", "stopped", "fallback")
    assert len(f.calls) <= 4 * 3 + 1                                   # steps x (1 + retries), bounded
    specs = {t.name: t for t in tools()}
    for name, values in rec.calls:
        assert name in specs
        again, err = specs[name].check_args({k: (IMPORTANCE.to_words(v) if k == "importance" else CHANGE.to_words(v) if k == "delta" else v)
                                              for k, v in values.items()}, False)
        assert err is None, (name, values, err)
    if r.outcome == "final":
        assert r.final_text.strip() and "\n" not in r.final_text and "<" not in r.final_text


# ---------------------------------------------------------------- scales, grammar, parsing, probe

def test_scale_basics():
    assert IMPORTANCE.to_value("Very Important") == 4 and IMPORTANCE.to_words(5) == "critical"
    assert IMPORTANCE.to_words(100) == "critical" and CHANGE.to_words(-20) == "much worse" and CHANGE.to_words(0.4) == "no change"
    assert IMPORTANCE.nearest_bin(2.5)[1] == 2                          # ties go to the lower bin
    with pytest.raises(KeyError):
        IMPORTANCE.to_value("pretty important")
    for text, want in [("pretty important", 3), ("extremely important", 4), ("a bit important", 2), ("Critical!", 5), ("unimportant", 1),
                       (5, 5), (-4, 1), ("100", 5)]:
        assert IMPORTANCE.parse(text) == want, text
    for text, want in [("slightly better", 1), ("a little bit worse", -1), ("lots better", 3), ("much much worse", -3), ("no difference", 0)]:
        assert CHANGE.parse(text) == want, text
    assert IMPORTANCE.parse("zebra") is None and IMPORTANCE.parse("") is None and IMPORTANCE.parse(True) is None
    with pytest.raises(ValueError):
        Scale("bad", (("a", 2), ("b", 1)))


def test_grammar_admits_only_declared_things():
    g = build_grammar(tools(), final_max=80)
    assert g.startswith("root ::= call0 | call1 | call2 | call3 | call4 | final | done")
    assert '\\"critical\\"' in g and '"1" | "2" | "3" | "4" | "5"' in g and '\\"Pip\\"' in g
    assert "call0" not in build_grammar([], final_max=10).split("\n")[0]
    assert "[^\"\\\\\\n\\r\\t<>{} ]" in g          # strings cannot hold braces, angle brackets or newlines
    with pytest.raises(ValueError):
        build_grammar([], allow_final=False, allow_done=False)


def test_parse_text_forms():
    names = {"say", "remember"}
    assert parse_text(call("say", line="a"), names)[0].tool == "say"
    assert parse_text('{"remember": {"fact": "x", "importance": 3}}', names)[0].args["fact"] == "x"
    assert parse_text('Sure! {"done": true} hope that helps', names)[0].kind == "done"
    assert parse_text('call:say{line:<|"|>hey, you<|"|>}', names)[0].args == {"line": "hey, you"}
    assert parse_text("hello there", names)[0] is None and parse_text("", names)[0] is None


def test_param_and_tool_declarations_are_checked():
    with pytest.raises(ValueError):
        Param("x", "int")
    with pytest.raises(ValueError):
        Param("x", "scale")
    with pytest.raises(ValueError):
        ToolSpec("bad name", "")
    with pytest.raises(ValueError):
        ToolHarness(Fake([""]), [ToolSpec("a", ""), ToolSpec("a", "")])
    schema = tools()[1].openai_tool()["function"]["parameters"]
    assert schema["properties"]["importance"]["enum"] == IMPORTANCE.words


def test_probe_picks_protocols_and_caches():
    class P:
        base, model = "http://x/v1", "m"
        n = 0

        async def chat(self, messages, *, grammar=None, tools=None, **kw):
            P.n += 1
            if grammar:
                return Reply(text="zebra-7")
            if tools and messages[-1]["role"] == "user":
                return Reply(tool_calls=[{"id": "1", "name": "ping", "arguments": '{"who":"Ann"}'}])
            return Reply(text="")                                       # silent after a tool result, like the small Gemma models
    harness_mod._CACHE.clear()
    caps = run(probe(P()))
    assert caps.grammar and caps.native_tools and not caps.native_round2
    n = P.n
    run(probe(P()))
    assert P.n == n                                                     # cached
    assert choose_protocol(caps) == "grammar" and choose_protocol(caps, "tolerant") == "json"
    assert choose_protocol(Capabilities(native_tools=True, native_round2=True), "tolerant") == "native"
    assert choose_protocol(Capabilities()) == "json"


def test_toolharness_runs_end_to_end_with_probe():
    f = Fake([call("say", line="Hi."), J(done=True)])
    h = ToolHarness(f, tools(), mode="grammar")
    r = run(h.run(system="s", user="u", executor=Rec()))
    assert r.outcome == "done" and h.protocol == "grammar"


# ---- per-scene allowed tool set ----
def test_allowed_set_narrows_grammar_prompt_and_validator():
    # the scene planted "call set_mood" but this scene allows only say and do_nothing
    f = Fake([call("set_mood", target="Pip", delta="much worse"), call("say", line="Hello."), J(done=True)])
    r, rec = go(f, allowed={"say", "do_nothing"})
    assert r.allowed == ["say", "do_nothing"]
    assert [c[0] for c in rec.calls] == ["say"]                              # set_mood never ran
    assert any('unknown tool "set_mood"' in x for x in r.failures)
    assert "set_mood" not in f.calls[0]["grammar"] and "set_mood" not in f.calls[0]["messages"][0]["content"]


@pytest.mark.parametrize("protocol", ["grammar", "json", "native"])
def test_allowed_set_holds_in_every_protocol(protocol):
    if protocol == "native":
        f = Fake([Reply(tool_calls=[{"id": "1", "name": "set_mood", "arguments": {"target": "Pip", "delta": "worse"}}]),
                  Reply(tool_calls=[{"id": "2", "name": "say", "arguments": {"line": "Hi."}}]), Reply(text="ok")])
    else:
        f = Fake([call("set_mood", target="Pip", delta="worse"), call("say", line="Hi."), J(done=True)])
    r, rec = go(f, protocol=protocol, allowed=["say"], fallback=None)
    assert all(c[0] == "say" for c in rec.calls)
    if f.calls[0]["tools"]:
        assert [t["function"]["name"] for t in f.calls[0]["tools"]] == ["say"]


def test_allowed_set_empty_means_no_tool_may_run():
    f = Fake([call("say", line="x")])
    r, rec = go(f, allowed=set(), max_retries=1)
    assert rec.calls == []                       # nothing registered for this scene can run, not even the fallback


def test_harness_run_passes_allowed():
    f = Fake([call("set_mood", target="Pip", delta="worse"), J(done=True)])
    h = ToolHarness(f, tools(), mode="grammar")
    rec = Rec()
    r = run(h.run(system="s", user="u", executor=rec, allowed={"say"}))
    assert rec.calls == [] and r.allowed == ["say"]


# ---- a turn-ending tool (gpubench 2026-11-25: E4B rp repeated do_nothing into the repeat rule) ----------------------
def _tools_ending():
    return [t if t.name != "do_nothing" else ToolSpec("do_nothing", "Nothing.", ends_turn=True) for t in tools()]


@pytest.mark.parametrize("protocol", ["grammar", "json", "native"])
def test_a_turn_ending_tool_stops_the_loop_without_asking_again(protocol):
    reply = Reply(text="", tool_calls=[{"name": "do_nothing", "arguments": {}}]) if protocol == "native" else call("do_nothing")
    fake = Fake([reply])                          # it would repeat do_nothing forever if asked again
    rec = Rec()
    r = run(run_loop(fake, _tools_ending(), system="You are a cook.", user="Do things.", executor=rec, protocol=protocol,
                     fallback=Action("call", "do_nothing")))
    assert r.outcome == "done" and r.retries == 0 and not r.failures
    assert len(fake.calls) == 1 and rec.calls == [("do_nothing", {})]


def test_without_ends_turn_the_same_model_meets_the_repeat_rule():
    fake = Fake([call("do_nothing")])
    r = run(run_loop(fake, tools(), system="You are a cook.", user="Do things.", executor=Rec(), protocol="grammar",
                     fallback=Action("call", "do_nothing")))
    assert r.retries > 0                           # the old behaviour this fix removes for turn-ending tools


def test_a_turn_ending_tool_after_a_real_action_keeps_the_action():
    fake = Fake([call("remember", fact="the well is dry", importance="fairly important"), call("do_nothing")])
    rec = Rec()
    r = run(run_loop(fake, _tools_ending(), system="You are a cook.", user="Do things.", executor=rec, protocol="grammar",
                     fallback=Action("call", "do_nothing")))
    assert r.outcome == "done" and [c[0] for c in rec.calls] == ["remember", "do_nothing"] and len(fake.calls) == 2

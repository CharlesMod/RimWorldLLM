"""Sampler use: None = the engine decides; X-Easy-Sampler rp/structured; official Gemma values only for a
non-engine endpoint whose model is a known Gemma 4 build."""
import json

import httpx
import pytest

from conftest import run
from rimworldllm_agent.core.config import LLMConfig
from rimworldllm_agent.core.llm import HttpLLM, is_known_gemma4, sampler_profile
from rimworldllm_agent.core.toolloop import OpenAIBackend

ENGINE_STATUS = {"phase": "ready", "sampler": {"policy": "enforce"}}
FIELDS = ("temperature", "top_k", "top_p", "min_p", "repeat_penalty", "dry_multiplier")


def server(engine: bool, seen: list):
    def h(req: httpx.Request):
        if req.method == "GET":
            if engine and req.url.path == "/engine/v1/status":
                return httpx.Response(200, json=ENGINE_STATUS)
            return httpx.Response(404, json={})
        seen.append((req.url.path, dict(req.headers), json.loads(req.content)))
        if req.url.path.endswith("/completion"):
            return httpx.Response(200, json={"content": "ok"})
        return httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]})
    return httpx.AsyncClient(transport=httpx.MockTransport(h))


def test_config_defaults_are_none():
    c = LLMConfig()
    assert (c.temperature, c.top_k, c.top_p, c.min_p, c.repeat_penalty, c.engine) == (None,) * 6
    assert c.line_dry is False


@pytest.mark.parametrize("kind,want", [("line", "rp"), ("diary", "rp"), ("narrator", "rp"), ("chronicle", "rp"),
                                       ("voice_card", "rp"), ("intent", "structured"), ("reflect", "structured")])
def test_engine_gets_header_and_no_sampler_fields(kind, want):
    seen = []
    llm = HttpLLM(LLMConfig(base_url="http://h", model="google/gemma-4-E4B-it"), client=server(True, seen))
    run(llm.complete([("user", "u")], slot=1, kind=kind, temperature=0.7))
    _, hd, body = seen[0]
    assert hd["x-easy-sampler"] == want
    assert not any(k in body for k in FIELDS), body   # not even the per-call temperature: the engine decides


def test_schema_or_grammar_means_structured():
    assert sampler_profile("whatever", grammar="root ::= x") == "structured"
    assert sampler_profile("whatever", json_schema={"type": "object"}) == "structured"
    assert sampler_profile("line") == "rp"


def test_native_mode_also_sends_header():
    seen = []
    llm = HttpLLM(LLMConfig(base_url="http://h", mode="native"), client=server(True, seen))
    run(llm.complete([("user", "u")], slot=0, kind="intent", max_tokens=4, stop=[], grammar='root ::= "a"'))
    path, hd, body = seen[0]
    assert path == "/completion" and hd["x-easy-sampler"] == "structured" and "temperature" not in body


def test_forced_config_values_still_sent():
    seen = []
    cfg = LLMConfig(base_url="http://h", temperature=0.5, min_p=0.1)
    run(HttpLLM(cfg, client=server(True, seen)).complete([("user", "u")], slot=1))
    body = seen[0][2]
    assert body["temperature"] == 0.5 and body["min_p"] == 0.1 and "top_k" not in body


@pytest.mark.parametrize("model", ["google/gemma-4-E4B-it", "gemma4:e2b", "unsloth/gemma-4-26B-A4B-it-GGUF", "gemma4-rp-maxxed-Q3_K_XL"])
def test_non_engine_known_gemma_gets_official_values(model):
    seen = []
    llm = HttpLLM(LLMConfig(base_url="http://h", model=model), client=server(False, seen))
    run(llm.complete([("user", "u")], slot=1, kind="line", temperature=0.9))
    _, hd, body = seen[0]
    assert "x-easy-sampler" not in hd
    assert (body["temperature"], body["top_k"], body["top_p"]) == (1.0, 64, 0.95)
    assert "min_p" not in body and "dry_multiplier" not in body


@pytest.mark.parametrize("model", ["", "gpt-4o", "llama-3.1-8b", "gemma-3-27b", "gemma-2-9b"])
def test_non_engine_unknown_model_gets_nothing(model):
    seen = []
    llm = HttpLLM(LLMConfig(base_url="http://h", model=model), client=server(False, seen))
    run(llm.complete([("user", "u")], slot=1, kind="line", temperature=0.9))
    _, hd, body = seen[0]
    assert "x-easy-sampler" not in hd and not any(k in body for k in FIELDS)


def test_detection_is_cached_and_config_overrides_it():
    seen = []
    llm = HttpLLM(LLMConfig(base_url="http://h"), client=server(True, seen))
    run(llm.complete([("user", "u")], slot=1))
    run(llm.complete([("user", "u")], slot=1))
    assert llm._engine is True and len(seen) == 2
    # engine=False in the config: no probe, no header, even though it would answer as the engine
    seen2 = []
    llm = HttpLLM(LLMConfig(base_url="http://h", engine=False, model="gemma-4-E4B-it"), client=server(True, seen2))
    run(llm.complete([("user", "u")], slot=1))
    assert "x-easy-sampler" not in seen2[0][1] and seen2[0][2]["top_k"] == 64


def test_unreachable_probe_is_not_cached():
    def h(req):
        raise httpx.ConnectError("down")
    llm = HttpLLM(LLMConfig(base_url="http://h"), client=httpx.AsyncClient(transport=httpx.MockTransport(h)))
    with pytest.raises(Exception):
        run(llm.complete([("user", "u")], slot=1))
    assert llm._engine is None


def test_toolloop_backend_structured_header_and_no_temperature():
    seen = []
    be = OpenAIBackend("http://h", model="google/gemma-4-E4B-it", client=server(True, seen))
    r = run(be.chat([{"role": "user", "content": "u"}], temperature=0.2))
    assert r.text == "ok"
    _, hd, body = seen[0]
    assert hd["x-easy-sampler"] == "structured" and "temperature" not in body


def test_toolloop_backend_non_engine():
    seen = []
    be = OpenAIBackend("http://h", model="gemma-4-E4B-it", client=server(False, seen))
    run(be.chat([{"role": "user", "content": "u"}], temperature=0.2))
    _, hd, body = seen[0]
    assert "x-easy-sampler" not in hd and (body["temperature"], body["top_k"], body["top_p"]) == (1.0, 64, 0.95)
    seen.clear()
    be = OpenAIBackend("http://h", model="some-model", client=server(False, seen))
    run(be.chat([{"role": "user", "content": "u"}], temperature=0.2))
    assert "temperature" not in seen[0][2]
    seen.clear()
    be = OpenAIBackend("http://h", model="some-model", client=server(False, seen), honor_temperature=True)
    run(be.chat([{"role": "user", "content": "u"}], temperature=0.2))
    assert seen[0][2]["temperature"] == 0.2


def test_is_known_gemma4():
    assert is_known_gemma4("google/gemma-4-E4B-it") and not is_known_gemma4("gemma-3-27b")

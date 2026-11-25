import json

import httpx
import pytest

from conftest import run
from hermesvoice_agent.calllog import CallLog
from hermesvoice_agent.config import LLMConfig
from hermesvoice_agent.endpoint_test import test_endpoint
from hermesvoice_agent.llm import HttpLLM, gemma_prompt, normalise_base


def client(handler):
    return httpx.AsyncClient(transport=httpx.MockTransport(handler))


def ok_server(paths_seen=None, models=("m1",)):
    def h(req):
        if paths_seen is not None:
            paths_seen.append((req.method, req.url.path))
        if req.url.path.endswith("/models"):
            return httpx.Response(200, json={"data": [{"id": m} for m in models]})
        return httpx.Response(200, json={"choices": [{"message": {"content": "Hi there"}}]})
    return h


def test_normalise_base():
    assert normalise_base("localhost:8080") == "http://localhost:8080/v1"
    assert normalise_base("http://h:1/v1/") == "http://h:1/v1"
    assert normalise_base("http://h:1/v1/chat/completions") == "http://h:1/v1"
    assert normalise_base("http://h:1/") == "http://h:1/v1"


def test_endpoint_green_and_timed():
    seen = []
    r = run(test_endpoint(LLMConfig(base_url="http://h:1"), client(ok_server(seen))))
    assert r.ok and r.completion_ms is not None and r.models == ["m1"]
    assert ("GET", "/v1/models") in seen and ("POST", "/v1/chat/completions") in seen


def test_endpoint_404_retries_once_without_v1():
    seen = []
    def h(req):
        seen.append(req.url.path)
        if req.url.path.startswith("/v1"):
            return httpx.Response(404, json={})
        return ok_server()(req)
    r = run(test_endpoint(LLMConfig(base_url="http://h:1/v1"), client(h)))
    assert r.ok and seen[0] == "/v1/models" and seen[1] == "/models" and r.base_url == "http://h:1"
    seen.clear()
    r = run(test_endpoint(LLMConfig(base_url="http://h:1"), client(lambda q: httpx.Response(404, json={}))))
    assert r.status == "red" and "404" in r.cause


def test_endpoint_503_is_amber_and_causes_one_line():
    r = run(test_endpoint(LLMConfig(base_url="http://h"), client(lambda q: httpx.Response(503, json={}))))
    assert r.status == "amber" and "loading" in r.cause and "\n" not in r.cause
    r = run(test_endpoint(LLMConfig(base_url="http://h"), client(lambda q: httpx.Response(401, json={}))))
    assert r.status == "red" and "API key" in r.cause
    def boom(q): raise httpx.ConnectError("x")
    r = run(test_endpoint(LLMConfig(base_url="http://h"), client(boom)))
    assert r.status == "red" and "refused" in r.cause
    def comp503(q):
        return ok_server()(q) if q.url.path.endswith("models") else httpx.Response(503, json={})
    r = run(test_endpoint(LLMConfig(base_url="http://h"), client(comp503)))
    assert r.status == "amber"
    r = run(test_endpoint(LLMConfig(base_url="http://h", model="nope"), client(ok_server())))
    assert r.status == "red" and "nope" in r.cause


def test_call_log_has_cache_stats_and_effective_prefill(tmp_path):
    def h(req):
        body = json.loads(req.content)
        assert body["id_slot"] == 1 and body["cache_prompt"] is True
        assert body["chat_template_kwargs"] == {"enable_thinking": False}
        return httpx.Response(200, json={"choices": [{"message": {"content": "Eat."}}],
                                         "timings": {"prompt_n": 154, "cache_n": 1721, "prompt_ms": 260.0,
                                                     "predicted_n": 3, "predicted_ms": 40.0}})
    log = CallLog(tmp_path / "calls.jsonl")
    llm = HttpLLM(LLMConfig(base_url="http://h"), log, client(h))
    c = run(llm.complete([("system", "s"), ("user", "u")], slot=1, kind="line", pawn=5))
    assert c.text == "Eat."
    row = json.loads((tmp_path / "calls.jsonl").read_text().splitlines()[0])
    assert row["prompt_n"] == 154 and row["cache_n"] == 1721 and row["prompt_ms"] == 260.0 and row["predicted_ms"] == 40.0
    assert row["raw_prefill_tps"] == pytest.approx(154 / 0.26, rel=1e-3)
    assert row["effective_prefill_tps"] == pytest.approx((154 + 1721) / 0.26, rel=1e-3)
    assert row["kind"] == "line" and row["slot"] == 1 and row["pawn"] == 5


def test_native_mode_payload():
    seen = {}
    def h(req):
        seen["url"] = str(req.url); seen["body"] = json.loads(req.content)
        return httpx.Response(200, json={"content": "Eat.", "timings": {"prompt_n": 5, "cache_n": 100, "prompt_ms": 50}})
    llm = HttpLLM(LLMConfig(base_url="http://h:1", mode="native"), client=client(h))
    run(llm.complete([("system", "S"), ("user", "U"), ("assistant", "line"), ("user", "label")],
                     slot=2, kind="intent", max_tokens=4, stop=[], grammar='root ::= "none"'))
    assert seen["url"] == "http://h:1/completion"
    b = seen["body"]
    assert b["id_slot"] == 2 and b["grammar"] and b["n_predict"] == 4
    assert b["prompt"] == ("<|turn>system\nS<turn|>\n<|turn>user\nU<turn|>\n"
                           "<|turn>model\n<|channel>thought\n<channel|>line<turn|>\n"
                           "<|turn>user\nlabel<turn|>\n<|turn>model\n<|channel>thought\n<channel|>")


def test_gemma_prompt_prefix_shared_between_line_and_intent_pass():
    base = [("system", "S"), ("user", "U")]
    line = gemma_prompt(base)
    intent = gemma_prompt(base + [("assistant", "x"), ("user", "l")])
    stem = line.removesuffix("<|turn>model\n<|channel>thought\n<channel|>")
    assert intent.startswith(stem)

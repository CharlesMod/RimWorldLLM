"""The Test-button logic: normalise the URL, retry once with/without /v1 on 404, GET /models,
an 8-token completion with thinking off, timed. 503 = loading (amber). One cause line per failure."""
from __future__ import annotations

import time
from dataclasses import dataclass, field

import httpx

from .config import LLMConfig
from .llm import normalise_base


@dataclass
class EndpointResult:
    status: str                 # green | amber | red
    cause: str                  # one line
    base_url: str = ""
    models: list[str] = field(default_factory=list)
    completion_ms: float | None = None
    model_ms: float | None = None

    @property
    def ok(self) -> bool:
        return self.status == "green"


def _cause(r: httpx.Response | None, exc: Exception | None, what: str) -> tuple[str, str]:
    if exc is not None:
        if isinstance(exc, httpx.ConnectError):
            return "red", f"{what}: connection refused or host unreachable; is the server running at that address?"
        if isinstance(exc, httpx.TimeoutException):
            return "red", f"{what}: timed out; the server is too slow or blocked"
        return "red", f"{what}: {type(exc).__name__}"
    assert r is not None
    c = r.status_code
    if c == 503:
        return "amber", f"{what}: 503, the model is still loading; try again in a few seconds"
    if c in (401, 403):
        return "red", f"{what}: {c}, the API key is missing or wrong"
    if c == 404:
        return "red", f"{what}: 404, not an OpenAI-compatible endpoint (check the URL, with or without /v1)"
    if c == 400:
        return "red", f"{what}: 400, the server rejected the request ({r.text[:80].strip()})"
    return "red", f"{what}: HTTP {c}"


async def test_endpoint(cfg: LLMConfig, client: httpx.AsyncClient | None = None, timeout: float = 20.0) -> EndpointResult:
    own = client is None
    http = client or httpx.AsyncClient(timeout=timeout)
    headers = {"Authorization": f"Bearer {cfg.api_key}"} if cfg.api_key else {}
    base = normalise_base(cfg.base_url)
    try:
        # GET /models, retry once with the other /v1 spelling on 404
        r = exc = None
        t0 = time.perf_counter()
        for attempt in (0, 1):
            try:
                r = await http.get(base + "/models", headers=headers)
                exc = None
            except httpx.HTTPError as e:
                r, exc = None, e
            if r is not None and r.status_code == 404 and attempt == 0:
                base = base.removesuffix("/v1") if base.endswith("/v1") else base + "/v1"
                continue
            break
        model_ms = (time.perf_counter() - t0) * 1000
        if exc is not None or r.status_code >= 400:
            st, cause = _cause(r, exc, "GET /models")
            return EndpointResult(st, cause, base)
        models: list[str] = []
        try:
            models = [m.get("id", "") for m in r.json().get("data", [])]
        except (ValueError, AttributeError):
            pass
        if cfg.model and models and cfg.model not in models:
            return EndpointResult("red", f"model '{cfg.model}' is not served here (server has: {', '.join(models[:3])})",
                                  base, models, None, model_ms)
        body = {"messages": [{"role": "user", "content": "Say hi."}], "max_tokens": 8, "temperature": 0,
                "chat_template_kwargs": {"enable_thinking": False}}
        if cfg.model or models:
            body["model"] = cfg.model or models[0]
        t0 = time.perf_counter()
        try:
            r = await http.post(base + "/chat/completions", json=body, headers=headers)
            exc = None
        except httpx.HTTPError as e:
            r, exc = None, e
        ms = (time.perf_counter() - t0) * 1000
        if exc is not None or r.status_code >= 400:
            st, cause = _cause(r, exc, "completion")
            return EndpointResult(st, cause, base, models, None, model_ms)
        try:
            text = r.json()["choices"][0]["message"].get("content") or ""
        except (ValueError, KeyError, IndexError):
            return EndpointResult("red", "completion: reply was not an OpenAI chat completion", base, models, ms, model_ms)
        return EndpointResult("green", f"ok: {len(models)} model(s), 8-token completion in {ms:.0f} ms",
                              base, models, ms, model_ms)
    finally:
        if own:
            await http.aclose()

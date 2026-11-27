"""The one place that talks HTTP: an OpenAI-compatible chat endpoint, with llama.cpp extras when present."""
from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from typing import Any, Protocol

import httpx

from ..llm import GEMMA4_OFFICIAL, AwayGate, LLMAway, detect_engine, is_known_gemma4, parse_yielded


class BackendError(Exception):
    """The server was unreachable, slow, or answered with something unusable. Never a model fault."""


class BackendAway(BackendError):
    """The engine answered 503 code "yielded": the model server machine is running a game. Not a model fault."""
    def __init__(self, message: str, retry_after: float = 5.0):
        super().__init__(f"model paused on the server: {message}")
        self.message, self.retry_after = message, retry_after


@dataclass
class Reply:
    text: str = ""
    tool_calls: list[dict] = field(default_factory=list)   # [{"id", "name", "arguments": str|dict}]
    finish: str | None = None
    reasoning: str = ""
    ms: float = 0.0


class Backend(Protocol):
    async def chat(self, messages: list[dict], *, grammar: str | None = None, tools: list[dict] | None = None,
                   tool_choice: str | None = None, max_tokens: int = 160, temperature: float = 0.2,
                   timeout: float = 30.0, slot: int | None = None) -> Reply: ...


class OpenAIBackend:
    """POST {base}/chat/completions. ``extra`` goes into every body (id_slot, cache_prompt, repetition control...).

    Gemma-family models on llama.cpp need ``chat_template_kwargs: {enable_thinking: false}``; it is the default
    and ignored by servers that do not know it.
    """

    def __init__(self, base_url: str, *, model: str = "", api_key: str = "", client: httpx.AsyncClient | None = None,
                 extra: dict | None = None, engine: bool | None = None, sampler: dict | None = None,
                 honor_temperature: bool = False):
        u = base_url.strip().rstrip("/")
        self.base = u if u.endswith("/v1") else u + "/v1"
        self.model, self.api_key = model, api_key
        self.http = client or httpx.AsyncClient()
        self.extra = {"cache_prompt": True, "chat_template_kwargs": {"enable_thinking": False}, **(extra or {})}
        # Sampling: tool-loop steps are the engine's "structured" profile (X-Easy-Sampler). The
        # per-call ``temperature`` is sent only with honor_temperature=True (benches); otherwise the
        # engine decides. ``sampler`` forces fields. Against a non-engine endpoint the official
        # Gemma 4 values go out only when ``model`` is a known Gemma 4 build, else nothing.
        self.gate = AwayGate()
        self._engine, self.sampler, self.honor_temperature = engine, dict(sampler or {}), honor_temperature

    async def aclose(self):
        await self.http.aclose()

    async def chat(self, messages, *, grammar=None, tools=None, tool_choice=None, max_tokens=160, temperature=0.2, timeout=30.0,
                   slot=None) -> Reply:
        """``slot``: pin the request to one llama.cpp slot (id_slot); None leaves it to the server."""
        try:
            self.gate.check()                       # backing off after "yielded": no request
        except LLMAway as e:
            raise BackendAway(e.message, e.retry_after) from None
        headers = {"Authorization": f"Bearer {self.api_key}"} if self.api_key else {}
        if self._engine is None:
            self._engine = await detect_engine(self.http, self.base, headers)
        samp = dict(self.sampler)
        if self.honor_temperature:
            samp.setdefault("temperature", temperature)
        if not self._engine and is_known_gemma4(self.model):
            for k, v in GEMMA4_OFFICIAL.items():
                samp.setdefault(k, v)
        body: dict[str, Any] = {"messages": messages, "max_tokens": max_tokens, **samp, **self.extra}
        if self._engine:
            headers["X-Easy-Sampler"] = "structured"
        if self.model:
            body["model"] = self.model
        if slot is not None:
            body["id_slot"] = slot
        if grammar:
            body["grammar"] = grammar
        if tools:
            body["tools"] = tools
            body["tool_choice"] = tool_choice or "auto"
        t0 = time.perf_counter()
        try:
            r = await self.http.post(self.base + "/chat/completions", json=body, headers=headers, timeout=timeout)
        except httpx.HTTPError as e:
            raise BackendError(f"unreachable ({type(e).__name__})") from e
        ms = (time.perf_counter() - t0) * 1000
        y = parse_yielded(r.status_code, r.content, r.headers.get("Retry-After"))
        if y:
            self.gate.went_away(*y)
            raise BackendAway(*y)
        if r.status_code >= 400:
            raise BackendError(f"HTTP {r.status_code}: {r.text[:160]}")
        self.gate.ok()
        try:
            ch = r.json()["choices"][0]
            m = ch.get("message") or {}
        except (ValueError, KeyError, IndexError, TypeError) as e:
            raise BackendError("reply was not a chat completion") from e
        calls = []
        for i, tc in enumerate(m.get("tool_calls") or []):
            f = tc.get("function") or {}
            calls.append({"id": tc.get("id") or f"call_{i}", "name": f.get("name") or "", "arguments": f.get("arguments")})
        content = m.get("content")
        return Reply(content if isinstance(content, str) else "", calls, ch.get("finish_reason"),
                     m.get("reasoning_content") or "", ms)

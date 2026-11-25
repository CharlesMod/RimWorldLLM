"""LLM clients.

One interface, ``LLMClient.complete(messages, slot, ...)``, two wire modes:

- ``openai``: POST /v1/chat/completions (works on any OpenAI-compatible server; on llama.cpp
  ``id_slot`` and ``cache_prompt`` are passed as extras and ignored elsewhere).
- ``native``: POST /completion on llama.cpp with the Gemma turn format built by hand, as bench/m0
  measured it (thinking off = an empty thought channel after the model marker). Gives id_slot
  control, GBNF and cache stats.

Both log prompt_n, cache_n, prompt_ms, predicted_ms and raw/effective prefill via CallLog.
"""
from __future__ import annotations

import json
import re
import time
from dataclasses import dataclass
from typing import Protocol

import httpx

from .calllog import CallLog, CallStats
from .config import LLMConfig

Messages = list[tuple[str, str]]   # [("system"|"user"|"assistant", text), ...]

THOUGHT_RE = re.compile(r"<\|channel>.*?<channel\|>", re.S)
THINK_RE = re.compile(r"<think>.*?</think>", re.S)


class LLMError(Exception):
    def __init__(self, cause: str, status: int = 0):
        super().__init__(cause)
        self.cause, self.status = cause, status


@dataclass
class Completion:
    text: str
    stats: CallStats


class LLMClient(Protocol):
    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None,
                       grammar: str | None = None, temperature: float | None = None,
                       pawn: int | None = None) -> Completion: ...


def gemma_prompt(messages: Messages) -> str:
    """Hand-built Gemma turns with thinking off (bench/m0/lib.py raw())."""
    out = []
    for role, text in messages:
        r = "model" if role == "assistant" else role
        if r == "model":
            out.append(f"<|turn>model\n<|channel>thought\n<channel|>{text}<turn|>\n")
        else:
            out.append(f"<|turn>{r}\n{text}<turn|>\n")
    # generation prompt
    if messages and messages[-1][0] != "assistant":
        out.append("<|turn>model\n<|channel>thought\n<channel|>")
    return "".join(out)


def strip_thought(t: str) -> str:
    return THINK_RE.sub("", THOUGHT_RE.sub("", t))


class HttpLLM:
    def __init__(self, cfg: LLMConfig, calllog: CallLog | None = None,
                 client: httpx.AsyncClient | None = None):
        self.cfg = cfg
        self.calllog = calllog or CallLog(None)
        self.http = client or httpx.AsyncClient(timeout=cfg.timeout_s)
        self.base = normalise_base(cfg.base_url)

    async def aclose(self):
        await self.http.aclose()

    def _headers(self) -> dict:
        return {"Authorization": f"Bearer {self.cfg.api_key}"} if self.cfg.api_key else {}

    def _sampler(self, temperature: float | None) -> dict:
        c = self.cfg
        return {"temperature": c.temperature if temperature is None else temperature,
                "top_k": c.top_k, "top_p": c.top_p, "min_p": c.min_p}

    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None,
                       grammar: str | None = None, temperature: float | None = None,
                       pawn: int | None = None) -> Completion:
        stop = ["\n"] if stop is None else stop
        t0 = time.perf_counter()
        st = CallStats(kind=kind, slot=slot, pawn=pawn)
        try:
            if self.cfg.mode == "native":
                body = {"prompt": gemma_prompt(messages), "n_predict": max_tokens, "stop": stop,
                        "cache_prompt": True, **self._sampler(temperature)}
                if slot is not None:
                    body["id_slot"] = slot
                if grammar:
                    body["grammar"] = grammar
                r = await self.http.post(self.base.removesuffix("/v1") + "/completion",
                                         json=body, headers=self._headers())
                data = self._ok(r)
                text = data.get("content", "")
                tim = data.get("timings", {})
            else:
                body = {"messages": [{"role": ("assistant" if ro == "assistant" else ro), "content": c}
                                     for ro, c in messages],
                        "max_tokens": max_tokens, "cache_prompt": True,
                        "chat_template_kwargs": {"enable_thinking": False},
                        **self._sampler(temperature)}
                if stop:
                    body["stop"] = stop
                if self.cfg.model:
                    body["model"] = self.cfg.model
                if slot is not None:
                    body["id_slot"] = slot
                if grammar:
                    body["grammar"] = grammar
                r = await self.http.post(self.base + "/chat/completions", json=body,
                                         headers=self._headers())
                data = self._ok(r)
                text = data["choices"][0]["message"].get("content") or ""
                tim = data.get("timings", {})
                if not tim and "usage" in data:
                    st.prompt_n = data["usage"].get("prompt_tokens", 0)
                    st.predicted_n = data["usage"].get("completion_tokens", 0)
        except httpx.HTTPError as e:
            st.ok, st.error = False, type(e).__name__
            st.wall_s = time.perf_counter() - t0
            self.calllog.record(st)
            raise LLMError(f"server unreachable ({type(e).__name__})") from e
        except LLMError as e:
            st.ok, st.error = False, e.cause
            st.wall_s = time.perf_counter() - t0
            self.calllog.record(st)
            raise
        st.wall_s = time.perf_counter() - t0
        if tim:
            st.prompt_n = int(tim.get("prompt_n", 0))
            st.cache_n = int(tim.get("cache_n", 0))
            st.prompt_ms = float(tim.get("prompt_ms", 0))
            st.predicted_n = int(tim.get("predicted_n", 0))
            st.predicted_ms = float(tim.get("predicted_ms", 0))
        self.calllog.record(st)
        return Completion(text, st)

    @staticmethod
    def _ok(r: httpx.Response) -> dict:
        if r.status_code >= 400:
            raise LLMError(f"HTTP {r.status_code}", r.status_code)
        try:
            return r.json()
        except ValueError:
            raise LLMError("reply was not JSON", r.status_code)


def normalise_base(url: str) -> str:
    """Return the base ending in /v1 (no trailing slash)."""
    u = url.strip().rstrip("/")
    if not re.match(r"^https?://", u):
        u = "http://" + u
    for suf in ("/chat/completions", "/completions", "/models"):
        if u.endswith(suf):
            u = u[: -len(suf)]
    if not u.endswith("/v1"):
        u += "/v1"
    return u

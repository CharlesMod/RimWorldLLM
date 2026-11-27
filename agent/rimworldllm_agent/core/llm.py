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
import logging
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


class LLMAway(LLMError):
    """The engine answered 503 code "yielded": the model server machine is running a game. Not a fault."""
    def __init__(self, message: str, retry_after: float = 5.0):
        super().__init__("yielded", 503)
        self.message, self.retry_after = message, retry_after


log = logging.getLogger("rimworldllm")
AWAY_CAP_S = 30.0


def parse_yielded(status: int, body: bytes | str, retry_after_header: str | None) -> tuple[str, float] | None:
    """(message, retry_after seconds) when this is the engine's "yielded" 503 (string code), else None.
    A plain 503 has a numeric code (loading) and is not away."""
    if status != 503:
        return None
    try:
        d = json.loads(body)
        e = d.get("error") if isinstance(d, dict) else None
        if not isinstance(e, dict) or e.get("code") != "yielded":
            return None
        msg = str(e.get("message") or "paused")
    except (ValueError, TypeError):
        return None
    try:
        ra = float(retry_after_header) if retry_after_header is not None else 5.0
    except ValueError:
        ra = 5.0
    return msg, min(max(ra, 0.0), AWAY_CAP_S)


class AwayGate:
    """Back-off after a "yielded" answer: until ``until`` calls are refused without touching the network.
    One quiet log line when the model goes away and one when it is back."""
    def __init__(self):
        self.until = 0.0
        self.away = False
        self.message = ""

    def check(self) -> None:
        if self.until and time.monotonic() < self.until:
            raise LLMAway(self.message, self.until - time.monotonic())

    def went_away(self, message: str, retry_after: float) -> None:
        self.until = time.monotonic() + retry_after
        self.message = message
        if not self.away:
            self.away = True
            log.info("model paused on the server: %s; retrying in %ds", message, round(retry_after))

    def ok(self) -> None:
        self.until = 0.0
        if self.away:
            self.away = False
            log.info("model is back on the server")


@dataclass
class Completion:
    text: str
    stats: CallStats


class LLMClient(Protocol):
    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None,
                       grammar: str | None = None, temperature: float | None = None,
                       actor: int | None = None, json_schema: dict | None = None) -> Completion: ...


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


# Google's generation_config.json, identical for every Gemma 4 build we ship
# (gemma-4-26B-A4B-it, -E4B-it, -E2B-it and the QAT repos). The engine's catalog holds the
# same values (easyEngine docs/SAMPLERS.md); the agent uses these only for a non-engine endpoint.
GEMMA4_OFFICIAL = {"temperature": 1.0, "top_k": 64, "top_p": 0.95}

_GEMMA4_SIZES = ("e2b", "e4b", "26b", "a4b", "rpmaxxed")

# Calls whose output is machine-read JSON or a tool step use the engine's "structured" profile;
# every other call is spoken or written text ("rp").
STRUCTURED_KINDS = frozenset({"intent", "reflect", "reflection", "tool", "toolloop"})


def is_known_gemma4(model: str) -> bool:
    """True when a model name is one of the Gemma 4 builds whose official sampling we know."""
    n = re.sub(r"[^a-z0-9]+", "", (model or "").lower())
    return "gemma4" in n and any(z in n for z in _GEMMA4_SIZES)


def sampler_profile(kind: str, *, grammar: str | None = None, json_schema: dict | None = None) -> str:
    return "structured" if (kind in STRUCTURED_KINDS or grammar or json_schema) else "rp"


def engine_status_url(base: str) -> str:
    return base.removesuffix("/v1") + "/engine/v1/status"


async def detect_engine(http: httpx.AsyncClient, base: str, headers: dict | None = None) -> bool | None:
    """Is ``base`` the hermes engine? True/False when answered; None when the server could not be
    reached (do not cache that)."""
    try:
        r = await http.get(engine_status_url(base), headers=headers or {}, timeout=3.0)
    except httpx.HTTPError:
        return None
    if r.status_code != 200:
        return False
    try:
        d = r.json()
    except ValueError:
        return False
    return isinstance(d, dict) and "phase" in d and "sampler" in d


def sampler_fields(cfg: LLMConfig, *, engine: bool, kind: str = "line") -> dict:
    """Request fields for sampling. Engine: only what the config forces (None = engine decides).
    Other servers: the config's values, plus the official Gemma 4 values for any of
    temperature/top_k/top_p the config leaves unset when the model name is a known Gemma 4 build;
    for any other model, nothing."""
    s = {k: getattr(cfg, k) for k in ("temperature", "top_k", "top_p", "min_p", "repeat_penalty")
         if getattr(cfg, k) is not None}
    if not engine and is_known_gemma4(cfg.model):
        for k, v in GEMMA4_OFFICIAL.items():
            s.setdefault(k, v)
    if cfg.line_dry and kind == "line":
        s.update(dry_multiplier=cfg.dry_multiplier, dry_base=cfg.dry_base, dry_allowed_length=cfg.dry_allowed_length,
                 dry_penalty_last_n=cfg.dry_penalty_last_n)
    return s


class HttpLLM:
    def __init__(self, cfg: LLMConfig, calllog: CallLog | None = None,
                 client: httpx.AsyncClient | None = None):
        self.cfg = cfg
        self.calllog = calllog or CallLog(None)
        self.http = client or httpx.AsyncClient(timeout=cfg.timeout_s)
        self.base = normalise_base(cfg.base_url)
        self._engine: bool | None = cfg.engine
        self.gate = AwayGate()

    async def aclose(self):
        await self.http.aclose()

    def _headers(self) -> dict:
        return {"Authorization": f"Bearer {self.cfg.api_key}"} if self.cfg.api_key else {}

    async def is_engine(self) -> bool:
        if self._engine is None:
            self._engine = await detect_engine(self.http, self.base, self._headers())
            return bool(self._engine)
        return self._engine

    async def _sampling(self, kind: str, grammar: str | None, json_schema: dict | None) -> tuple[dict, dict]:
        """(body fields, extra headers). The per-call ``temperature`` argument is not used: the
        engine's profile for the call's kind decides, or the config when it forces a value."""
        eng = await self.is_engine()
        fields = sampler_fields(self.cfg, engine=eng, kind=kind)
        return fields, ({"X-Hermes-Sampler": sampler_profile(kind, grammar=grammar, json_schema=json_schema)} if eng else {})

    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None,
                       grammar: str | None = None, temperature: float | None = None,
                       actor: int | None = None, json_schema: dict | None = None) -> Completion:
        stop = ["\n"] if stop is None else stop
        self.gate.check()                      # backing off after "yielded": no request, no calllog record
        t0 = time.perf_counter()
        st = CallStats(kind=kind, slot=slot, actor=actor)
        try:
            samp, sh = await self._sampling(kind, grammar, json_schema)
            if self.cfg.mode == "native":
                body = {"prompt": gemma_prompt(messages), "n_predict": max_tokens, "stop": stop,
                        "cache_prompt": True, **samp}
                if slot is not None:
                    body["id_slot"] = slot
                if grammar:
                    body["grammar"] = grammar
                if json_schema:
                    body["json_schema"] = json_schema
                r = await self.http.post(self.base.removesuffix("/v1") + "/completion",
                                         json=body, headers={**self._headers(), **sh})
                data = self._ok(r)
                self.gate.ok()
                text = data.get("content", "")
                tim = data.get("timings", {})
            else:
                body = {"messages": [{"role": ("assistant" if ro == "assistant" else ro), "content": c}
                                     for ro, c in messages],
                        "max_tokens": max_tokens, "cache_prompt": True,
                        "chat_template_kwargs": {"enable_thinking": False},
                        **samp}
                if stop:
                    body["stop"] = stop
                if self.cfg.model:
                    body["model"] = self.cfg.model
                if slot is not None:
                    body["id_slot"] = slot
                if grammar:
                    body["grammar"] = grammar
                if json_schema:
                    body["json_schema"] = json_schema
                r = await self.http.post(self.base + "/chat/completions", json=body,
                                         headers={**self._headers(), **sh})
                data = self._ok(r)
                self.gate.ok()
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
            if isinstance(e, LLMAway):
                self.gate.went_away(e.message, e.retry_after)
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
        y = parse_yielded(r.status_code, r.content, r.headers.get("Retry-After"))
        if y:
            raise LLMAway(*y)
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

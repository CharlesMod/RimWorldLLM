"""Capability probe and the one-object front door: ``ToolHarness(backend, tools).run(...)``."""
from __future__ import annotations

import json
from dataclasses import dataclass, asdict

from .backend import Backend, BackendError
from .loop import Executor, Fallback, LoopResult, Validator, run_loop
from .spec import ToolSpec

_PING = {"type": "function", "function": {"name": "ping", "description": "Report that you are here.",
         "parameters": {"type": "object", "properties": {"who": {"type": "string"}}, "required": ["who"], "additionalProperties": False}}}
_ROUND2_PROMPTS = ["Say hello to Ann by calling ping with who set to Ann.", "Call ping with who set to Bob.", "Use ping now, who is Cy."]


@dataclass
class Capabilities:
    grammar: bool = False
    native_tools: bool = False
    native_round2: bool = False       # a reply (text or a call) came back after a tool result, every time
    notes: str = ""

    def to_dict(self) -> dict:
        return asdict(self)


_CACHE: dict[str, Capabilities] = {}


async def probe(backend: Backend, key: str | None = None, *, force: bool = False, rounds: int = 3) -> Capabilities:
    """Ask the endpoint what it can do. Cached by ``key`` (default: the backend's base+model) for the process."""
    k = key or f"{getattr(backend, 'base', id(backend))}|{getattr(backend, 'model', '')}"
    if k in _CACHE and not force:
        return _CACHE[k]
    caps = Capabilities()
    notes = []
    # grammar: an unconstrained model would not say exactly one of two made-up words
    try:
        ok = 0
        for p in ("Write anything you like.", "Say a color."):
            r = await backend.chat([{"role": "user", "content": p}], grammar='root ::= "zebra-7" | "quokka-9"', max_tokens=8, temperature=0.8, timeout=20)
            ok += r.text.strip() in ("zebra-7", "quokka-9")
        caps.grammar = ok == 2
    except BackendError as e:
        notes.append(f"grammar probe: {e}")
    # native tool calls and the reply after a tool result
    try:
        good = 0
        any_call = False
        for p in _ROUND2_PROMPTS[:rounds]:
            msgs = [{"role": "system", "content": "You act by calling tools."}, {"role": "user", "content": p}]
            r = await backend.chat(msgs, tools=[_PING], tool_choice="auto", max_tokens=60, temperature=0.2, timeout=30)
            if not r.tool_calls:
                continue
            any_call = True
            tc = r.tool_calls[0]
            try:
                args = tc["arguments"] if isinstance(tc["arguments"], dict) else json.loads(tc["arguments"])
                assert isinstance(args, dict) and tc["name"] == "ping"
            except Exception:
                continue
            msgs2 = msgs + [{"role": "assistant", "content": "", "tool_calls": [{"id": tc["id"], "type": "function", "function": {"name": "ping", "arguments": json.dumps(args)}}]},
                            {"role": "tool", "tool_call_id": tc["id"], "name": "ping", "content": "ok"}]
            r2 = await backend.chat(msgs2, tools=[_PING], tool_choice="auto", max_tokens=60, temperature=0.2, timeout=30)
            if r2.text.strip() or r2.tool_calls:
                good += 1
        caps.native_tools = any_call
        caps.native_round2 = any_call and good == rounds
    except BackendError as e:
        notes.append(f"native probe: {e}")
    caps.notes = "; ".join(notes)
    _CACHE[k] = caps
    return caps


def choose_protocol(caps: Capabilities, mode: str = "auto") -> str:
    """auto: grammar when the endpoint has it. tolerant: never grammar. Otherwise native only if its round 2 works."""
    if mode in ("grammar", "json", "native"):
        return mode
    if mode == "auto" and caps.grammar:
        return "grammar"
    return "native" if caps.native_tools and caps.native_round2 else "json"


class ToolHarness:
    """Register tools once; ``run`` a loop per request. The protocol is probed once and cached."""

    def __init__(self, backend: Backend, tools: list[ToolSpec], *, mode: str = "auto", cache_key: str | None = None,
                 max_steps: int = 4, max_retries: int = 2, time_budget_s: float = 40.0, step_timeout_s: float = 20.0):
        names = [t.name for t in tools]
        if len(set(names)) != len(names):
            raise ValueError("duplicate tool names")
        self.backend, self.tools, self.mode, self.cache_key = backend, tools, mode, cache_key
        self.limits = dict(max_steps=max_steps, max_retries=max_retries, time_budget_s=time_budget_s, step_timeout_s=step_timeout_s)
        self.caps: Capabilities | None = None
        self.protocol: str | None = None

    async def ensure(self) -> str:
        if self.protocol is None:
            if self.mode in ("grammar", "json", "native"):
                self.protocol = self.mode
            else:
                self.caps = await probe(self.backend, self.cache_key)
                self.protocol = choose_protocol(self.caps, self.mode)
        return self.protocol

    async def run(self, *, system: str, user: str, executor: Executor, validator: Validator | None = None,
                  fallback: Fallback = None, **over) -> LoopResult:
        proto = await self.ensure()
        return await run_loop(self.backend, self.tools, system=system, user=user, executor=executor, protocol=proto,
                              validator=validator, fallback=fallback, **{**self.limits, **over})

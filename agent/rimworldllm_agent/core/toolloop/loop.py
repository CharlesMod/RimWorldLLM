"""The tool loop: every model step is constrained or validated, retried a bounded number of times, then
replaced by a deterministic fallback. It always ends, never raises for a model fault, and returns a trace.

Protocols (how a step is asked for and read):
  grammar  every step is decoded under a GBNF we build from the tools; the reply is one JSON object
  json     no grammar; the same JSON-in-text protocol, read tolerantly, validated, retried
  native   the endpoint's own tool calling (tools=, tool role); chosen only when a probe shows it works

We own the tool-result turn in grammar and json: results go into the next prompt as plain text in a user turn,
not through the model's tool-role template (see docs/TOOL-ROUND2.md for why).
"""
from __future__ import annotations

import asyncio
import inspect
import json
import time
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Iterable, Union

from .backend import Backend, BackendAway, BackendError, Reply
from .parse import RawStep, parse_text
from .spec import ToolSpec, build_grammar, has_raw_syntax, render_system, PROTOCOL

PROTOCOLS = ("grammar", "json", "native")
NEXT_PROMPT = "Reply with the next JSON object: another tool call, a final line, or done."


@dataclass
class Action:
    """A step in game terms: ``args`` are in the model's own words (scale phrases), not game numbers."""
    kind: str                     # call | final | done
    tool: str = ""
    args: dict = field(default_factory=dict)
    text: str = ""


@dataclass
class StepTrace:
    step: int
    attempt: int
    protocol: str
    raw: str = ""
    raw_calls: list = field(default_factory=list)
    kind: str = ""                # call | final | done | invalid | backend_error | timeout
    error: str = ""
    ms: float = 0.0
    salvaged: str = ""


@dataclass
class Executed:
    tool: str
    args: dict                    # model words
    values: dict                  # game values after translation
    result: str = ""
    error: str = ""               # executor fault (not a model fault)
    fallback: bool = False


@dataclass
class LoopResult:
    outcome: str = "gave_up"      # final | done | stopped | fallback | gave_up
    reason: str = ""              # why a non-normal ending happened
    final_text: str = ""
    executed: list[Executed] = field(default_factory=list)
    trace: list[StepTrace] = field(default_factory=list)
    protocol: str = ""
    retries: int = 0
    fallbacks: int = 0
    failures: list[str] = field(default_factory=list)
    implicit_done: bool = False
    allowed: list[str] = field(default_factory=list)   # the tool set this run was allowed (per scene)
    elapsed_s: float = 0.0

    @property
    def completed(self) -> bool:
        return self.outcome in ("final", "done", "stopped")

    def to_dict(self) -> dict:
        return json.loads(json.dumps(self.__dict__, default=lambda o: o.__dict__))


Executor = Callable[[str, dict], Union[Any, Awaitable[Any]]]
Validator = Callable[[str, dict], Union[str, None, Awaitable[Union[str, None]]]]
Fallback = Union[Action, Callable[[LoopResult], Union[Action, None]], None]


async def _maybe(x):
    return await x if inspect.isawaitable(x) else x


def _one_line(text: str, tolerant: bool, max_len: int) -> tuple[str, str]:
    raw = text or ""
    if ("\n" in raw.strip() or "\r" in raw.strip()) and not tolerant:
        return "", "the final line must be a single line"
    t = " ".join(raw.split())
    if not t:
        return "", "the final line is empty"
    if len(t) > max_len:
        return "", f"the final line is longer than {max_len} characters"
    if has_raw_syntax(t) or any(c in t for c in "<>{}"):
        return "", "the final line contains tool syntax"
    return t, ""


async def run_loop(backend: Backend, tools: list[ToolSpec], *, system: str, user: str, executor: Executor,
                   protocol: str = "grammar", validator: Validator | None = None, fallback: Fallback = None,
                   max_steps: int = 4, max_retries: int = 2, time_budget_s: float = 40.0, step_timeout_s: float = 20.0,
                   final_max: int = 200, max_tokens: int = 200, temperature: float = 0.2,
                   fallback_after_actions: bool = False, allowed: Iterable[str] | None = None,
                   slot: int | None = None) -> LoopResult:
    """``allowed``: the tool names this scene may use. The grammar, the prompt and the validator see only those, so
    text in the scene that asks for another tool cannot widen the set. Names not registered are ignored."""
    if protocol not in PROTOCOLS:
        raise ValueError(f"unknown protocol {protocol}")           # a caller bug, not a model fault
    if allowed is not None:
        keep = set(allowed)
        tools = [t for t in tools if t.name in keep]
    res = LoopResult(protocol=protocol, allowed=[t.name for t in tools])
    t_start = time.monotonic()
    deadline = t_start + time_budget_s
    by_name = {t.name: t for t in tools}
    tolerant = protocol != "grammar"
    native_tools = [t.openai_tool() for t in tools] if protocol == "native" else None
    pin = {} if slot is None else {"slot": slot}       # only a caller that pins a slot sends it, so plain backends need no new argument
    if protocol == "native":
        hist: list[dict] = [{"role": "system", "content": system}, {"role": "user", "content": user}]
    else:
        hist = [{"role": "system", "content": render_system(system, tools)}, {"role": "user", "content": user}]
    seen: set[str] = set()
    steps_done = 0
    reason = ""
    try:
        while True:
            if steps_done >= max_steps:
                reason = "step_budget"
                break
            closing = steps_done == max_steps - 1 and max_steps > 1
            grammar = None
            if protocol == "grammar":
                grammar = build_grammar([] if closing else tools, final_max=final_max)
            step_msgs = list(hist)
            if closing and protocol != "grammar":
                step_msgs.append({"role": "user", "content": "This is the last step: no more tool calls. Reply with a final line or done."})
            accepted, fail_reason = None, ""
            attempt = 0
            while attempt <= max_retries:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    reason = "time_budget"
                    break
                if attempt:
                    res.retries += 1
                tr = StepTrace(step=steps_done, attempt=attempt, protocol=protocol)
                res.trace.append(tr)
                t0 = time.monotonic()
                reply: Reply | None = None
                try:
                    reply = await asyncio.wait_for(
                        backend.chat(step_msgs, grammar=grammar, tools=native_tools, tool_choice="auto" if native_tools else None,
                                     max_tokens=max_tokens, temperature=temperature, timeout=min(step_timeout_s, remaining), **pin),
                        timeout=min(step_timeout_s, remaining) + 1.0)
                except asyncio.TimeoutError:
                    tr.kind, tr.error = "timeout", "the model took too long"
                except BackendAway as e:
                    tr.kind, tr.error = "away", str(e)      # the model is paused on the server: not a model fault
                except BackendError as e:
                    tr.kind, tr.error = "backend_error", str(e)
                except Exception as e:                       # a broken backend is still not the caller's problem
                    tr.kind, tr.error = "backend_error", f"{type(e).__name__}: {e}"[:200]
                tr.ms = (time.monotonic() - t0) * 1000
                if reply is not None:
                    tr.raw, tr.raw_calls = reply.text, reply.tool_calls
                    actions, err, implicit = await _interpret(reply, protocol, by_name, tolerant, final_max, validator,
                                                              seen, steps_done == 0 and not res.executed, bool(res.executed), closing)
                    tr.salvaged = "; ".join(sorted({a[1] for a in actions if a[1]}))
                    if err:
                        tr.kind, tr.error = "invalid", err
                    else:
                        tr.kind = actions[0][0].kind if actions else "done"
                        accepted = (actions, implicit)
                if accepted is not None:
                    break
                fail_reason = tr.error
                res.failures.append(f"step {steps_done} attempt {attempt}: {tr.error}")
                if tr.kind == "away":
                    reason = "away"                         # retrying would only be refused again
                    break
                attempt += 1
                step_msgs = list(hist) + [
                    {"role": "assistant", "content": (reply.text if reply and reply.text.strip() else "(no reply)")[:600]},
                    {"role": "user", "content": f"That reply was rejected: {tr.error}. {NEXT_PROMPT if protocol != 'native' else 'Try again.'}"}]
                if closing and protocol != "grammar":
                    step_msgs.append({"role": "user", "content": "Remember: no more tool calls now."})
            if accepted is None:
                if reason not in ("time_budget", "away"):
                    reason = "repeat" if "already" in fail_reason else "retries_exhausted"
                break
            actions, implicit = accepted
            steps_done += 1
            if implicit:
                res.implicit_done = True
            first = actions[0][0] if actions else Action("done")
            if first.kind == "final":
                res.outcome, res.final_text = "final", first.text
                break
            if first.kind == "done":
                res.outcome = "done"
                break
            await _run_calls(actions, by_name, executor, res, seen, hist, protocol, user)
            if any(a.kind == "call" and by_name.get(a.tool) is not None and by_name[a.tool].ends_turn for a, _, _ in actions):
                res.outcome = "done"                                 # a turn-ending tool ran: nothing more to ask
                break
    except Exception as e:                                     # the loop itself must not raise either
        res.failures.append(f"loop fault {type(e).__name__}: {e}"[:200])
        reason = reason or "loop_fault"
    if res.outcome not in ("final", "done"):
        res.reason = reason
        if res.executed and not fallback_after_actions:
            res.outcome = "stopped"                              # what already ran stands; the turn is not empty-handed
        else:
            await _apply_fallback(fallback, by_name, executor, res, tools)
    res.elapsed_s = time.monotonic() - t_start
    return res


async def _interpret(reply: Reply, protocol: str, by_name: dict[str, ToolSpec], tolerant: bool, final_max: int,
                     validator: Validator | None, seen: set[str], first_step: bool, had_calls: bool,
                     closing: bool) -> tuple[list[tuple[Action, str, dict]], str, bool]:
    """-> (list of (action, salvage-note, game-values), error, implicit_done)."""
    steps: list[tuple[RawStep, str]] = []
    if protocol == "native" and reply.tool_calls:
        for tc in reply.tool_calls:
            a = tc.get("arguments")
            if isinstance(a, str):
                try:
                    a = json.loads(a) if a.strip() else {}
                except ValueError:
                    return [], f"the arguments of {tc.get('name')} were not valid JSON", False
            steps.append((RawStep("call", tool=tc.get("name") or "", args=a), ""))
    else:
        text = reply.text or ""
        if protocol == "native":
            if not text.strip():
                if had_calls:
                    return [], "", True                         # the model ended its turn after a tool result: done
                return [], "the reply was empty", False
            s, why = parse_text(text, set(by_name))
            steps.append((s, s.salvaged) if s else (RawStep("final", text=text), ""))
        else:
            s, why = parse_text(text, set(by_name))
            if not s:
                return [], why, False
            steps.append((s, s.salvaged))
    out: list[tuple[Action, str, dict]] = []
    batch_seen: set[str] = set()
    for s, note in steps:
        if s.kind == "done":
            out.append((Action("done"), note, {}))
        elif s.kind == "final":
            t, err = _one_line(s.text, tolerant, final_max)
            if err:
                return [], err, False
            out.append((Action("final", text=t), note, {}))
        else:
            if closing:
                return [], "no more tool calls are allowed now; reply with a final line or done", False
            spec = by_name.get(s.tool)
            if spec is None:
                return [], f'unknown tool "{s.tool}"; the tools are: ' + ", ".join(by_name), False
            values, err = spec.check_args(s.args, tolerant)
            if err:
                return [], err, False
            if validator is not None:
                try:
                    msg = await _maybe(validator(spec.name, values))
                except Exception as e:
                    msg = f"the check failed ({type(e).__name__})"
                if msg:
                    return [], str(msg), False
            key = spec.call_key(values)
            if key in seen or key in batch_seen:
                return [], f"{spec.name} with these arguments was already done", False
            batch_seen.add(key)
            out.append((Action("call", spec.name, s.args if isinstance(s.args, dict) else {}), note, values))
    if len(out) > 1 and any(a.kind != "call" for a, _, _ in out):
        return [], "mixed a final line or done with tool calls in one step", False
    return out, "", False


async def _run_calls(actions, by_name, executor, res: LoopResult, seen: set[str], hist: list[dict], protocol: str, user: str = ""):
    done_calls: list[tuple[Action, Executed]] = []
    for a, _, values in actions:
        ex = Executed(a.tool, dict(a.args), dict(values))
        try:
            r = await _maybe(executor(a.tool, values))
            ex.result = by_name[a.tool].render_result(r)
        except Exception as e:
            ex.error = f"{type(e).__name__}: {e}"[:200]
            ex.result = f"error: {ex.error}"
        seen.add(by_name[a.tool].call_key(values))
        res.executed.append(ex)
        done_calls.append((a, ex))
    if protocol == "native":
        hist.append({"role": "assistant", "content": "", "tool_calls": [
            {"id": f"call_{len(res.executed) - len(done_calls) + i}", "type": "function",
             "function": {"name": a.tool, "arguments": json.dumps(a.args, ensure_ascii=False)}} for i, (a, _) in enumerate(done_calls)]})
        for i, (a, ex) in enumerate(done_calls):
            hist.append({"role": "tool", "tool_call_id": f"call_{len(res.executed) - len(done_calls) + i}", "name": a.tool, "content": ex.result})
    else:
        # grammar and json steps carry exactly one call (parse_text reads one object)
        hist.append({"role": "assistant", "content": json.dumps({"tool": done_calls[0][0].tool, "args": done_calls[0][0].args}, ensure_ascii=False)})
        lines = "\n".join(f"Result of {a.tool}: {ex.result}" for a, ex in done_calls)
        hist.append({"role": "user", "content": f"{lines}\n{NEXT_PROMPT}"})


async def _apply_fallback(fallback: Fallback, by_name, executor, res: LoopResult, tools: list[ToolSpec]):
    act = None
    try:
        act = fallback(res) if callable(fallback) and not isinstance(fallback, Action) else fallback
    except Exception as e:
        res.failures.append(f"fallback factory fault {type(e).__name__}")
    res.outcome = "fallback" if act else "gave_up"
    if act is None:
        return
    res.fallbacks += 1
    if act.kind == "final":
        res.final_text = " ".join(act.text.split())
    elif act.kind == "call":
        spec = by_name.get(act.tool)
        values, err = spec.check_args(act.args, True) if spec else ({}, "unknown fallback tool")
        if err:
            res.failures.append(f"fallback invalid: {err}")
            return
        ex = Executed(act.tool, dict(act.args), values, fallback=True)
        try:
            ex.result = spec.render_result(await _maybe(executor(act.tool, values)))
        except Exception as e:
            ex.error = f"{type(e).__name__}: {e}"[:200]
        res.executed.append(ex)

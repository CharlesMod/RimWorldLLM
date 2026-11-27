"""Turn model text into a step. Strict JSON in grammar mode; tolerant salvage outside it."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Any

GEMMA_QUOTE = '<|"|>'


@dataclass
class RawStep:
    """What the model said, before validation. kind: call, final, done."""
    kind: str
    tool: str = ""
    args: Any = None
    text: str = ""
    salvaged: str = ""       # non-empty when a repair was needed to read it


def json_objects(text: str) -> list[str]:
    """Top-level balanced {...} slices, string-aware."""
    out, depth, start, instr, esc = [], 0, -1, False, False
    for i, ch in enumerate(text):
        if instr:
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                instr = False
            continue
        if ch == '"' and depth:
            instr = True
        elif ch == "{":
            if depth == 0:
                start = i
            depth += 1
        elif ch == "}" and depth:
            depth -= 1
            if depth == 0:
                out.append(text[start:i + 1])
    return out


def _from_obj(o: Any, names: set[str] = frozenset()) -> RawStep | None:
    if not isinstance(o, dict):
        return None
    if len(o) == 1 and names:                      # {"remember": {...}}: the tool name used as the key
        k, v = next(iter(o.items()))
        if k in names and isinstance(v, dict):
            return RawStep("call", tool=k, args=v, salvaged="tool name as key")
    if o.get("done") in (True, "true", 1) and len(o) == 1:
        return RawStep("done")
    for k in ("final", "say", "reply"):
        if isinstance(o.get(k), str) and len(o) == 1:
            return RawStep("final", text=o[k])
    for k in ("tool", "name", "tool_name", "function"):
        if isinstance(o.get(k), str):
            rest = {k: v for k, v in o.items() if k not in ("tool", "name", "tool_name", "function", "args", "arguments", "parameters")}
            args = o.get("args", o.get("arguments", o.get("parameters", rest)))
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except ValueError:
                    pass
            return RawStep("call", tool=o[k], args=args)
    for k in ("tool_call", "call", "function_call"):
        if isinstance(o.get(k), dict):
            return _from_obj(o[k], names)
    return None


_GEMMA = re.compile(r"(?:<\|tool_call>)?\s*(?:call:)?([A-Za-z_]\w*)\s*\{(.*?)\}\s*(?:<tool_call\|>|$)", re.S)


def gemma_args(body: str) -> dict | None:
    """``line:<|"|>text<|"|>,importance:3`` to a dict. None when it cannot be read."""
    b = body.replace(GEMMA_QUOTE, '"')
    b = re.sub(r'([{,]\s*)([A-Za-z_]\w*)\s*:', r'\1"\2":', "{" + b + "}")
    try:
        d = json.loads(b)
    except ValueError:
        return None
    return d if isinstance(d, dict) else None


def parse_text(text: str, tool_names: set[str]) -> tuple[RawStep | None, str]:
    """(step, why-not). Strict: one JSON object. Salvage: fenced or surrounded JSON, Gemma raw call syntax."""
    t = (text or "").strip()
    if not t:
        return None, "the reply was empty"
    try:
        s = _from_obj(json.loads(t), tool_names)
        if s:
            return s, ""
    except ValueError:
        pass
    t2 = re.sub(r"^```(?:json)?\s*|\s*```$", "", t).strip()
    for cand in json_objects(t2):
        try:
            s = _from_obj(json.loads(cand), tool_names)
        except ValueError:
            continue
        if s:
            s.salvaged = "json inside other text"
            return s, ""
    m = _GEMMA.search(t)
    if m and m.group(1) in tool_names:
        args = gemma_args(m.group(2)) if m.group(2).strip() else {}
        if args is not None:
            return RawStep("call", tool=m.group(1), args=args, salvaged="gemma raw call syntax"), ""
    return None, "the reply was not one JSON object of the allowed forms"

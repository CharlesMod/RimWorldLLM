"""Tool registration: what a game declares, and what the harness derives from it (grammar, prompt text, schemas)."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Any, Callable

from .scale import Scale

# Characters a free-text argument may not contain. They keep raw tool syntax (<|tool_call>, {call:...}) out of
# anything that is spoken or stored, whatever the model tries.
FORBIDDEN_TEXT = '<>{}\\"\n\r\t'
RAW_SYNTAX = re.compile(r"<\|?[a-z_]*(tool|channel|turn)[a-z_]*\|?>|\btool_call\b|\bcall:\w+\s*\{|<\|\"\|>", re.I)
MAX_INT_ENUM = 40   # integer ranges up to this size become literal alternatives in the grammar


def has_raw_syntax(text: str) -> bool:
    return bool(RAW_SYNTAX.search(text))


@dataclass
class Param:
    """One tool parameter. ``kind`` is text, int, scale or enum."""
    name: str
    kind: str = "text"
    description: str = ""
    max_len: int = 200            # text: characters
    min_len: int = 1              # text: characters (a non-blank one is always required)
    lo: int | None = None         # int bounds, inclusive
    hi: int | None = None
    scale: Scale | None = None
    choices: list[str] | None = None   # enum values (names the caller knows are valid, for example)
    required: bool = True
    same_call: bool = False       # commentary (a reason, say): two calls that differ only here are the same call

    def __post_init__(self):
        if self.kind not in ("text", "int", "scale", "enum"):
            raise ValueError(f"bad param kind {self.kind}")
        if self.kind == "scale" and self.scale is None:
            raise ValueError("scale param needs a Scale")
        if self.kind == "enum" and not self.choices:
            raise ValueError("enum param needs choices")
        if self.kind == "int" and (self.lo is None or self.hi is None or self.lo > self.hi):
            raise ValueError("int param needs lo <= hi")

    # ---- what the model is told ----
    def hint(self) -> str:
        if self.kind == "text":
            return f"short text, at most {self.max_len} characters"
        if self.kind == "int":
            return f"a whole number from {self.lo} to {self.hi}"
        if self.kind == "scale":
            return "one of: " + " | ".join(f'"{w}"' for w in self.scale.words)
        return "one of: " + " | ".join(f'"{c}"' for c in self.choices)

    # ---- grammar ----
    def gbnf(self) -> str:
        if self.kind == "text":
            return f"tstr{self.max_len}"
        if self.kind == "int":
            if self.hi - self.lo + 1 <= MAX_INT_ENUM:
                return "(" + " | ".join(f'"{n}"' for n in range(self.lo, self.hi + 1)) + ")"
            return "integer"
        words = self.scale.words if self.kind == "scale" else self.choices
        return "(" + " | ".join(_lit(json.dumps(w, ensure_ascii=False)) for w in words) + ")"

    # ---- schema for native tool calling ----
    def json_schema(self) -> dict:
        d: dict[str, Any]
        if self.kind == "text":
            d = {"type": "string", "maxLength": self.max_len, "minLength": self.min_len}
        elif self.kind == "int":
            d = {"type": "integer", "minimum": self.lo, "maximum": self.hi}
        elif self.kind == "scale":
            d = {"type": "string", "enum": self.scale.words}
        else:
            d = {"type": "string", "enum": list(self.choices)}
        if self.description:
            d["description"] = self.description
        return d

    # ---- validation and translation (model value -> game value) ----
    def check(self, raw: Any, tolerant: bool) -> tuple[Any, str | None]:
        """Return (game value, error). ``tolerant`` accepts near misses the grammar would have made impossible."""
        if self.kind == "text":
            if not isinstance(raw, str):
                return None, f"{self.name} must be text"
            t = " ".join(raw.split())
            if not t:
                return None, f"{self.name} is empty"
            if len(t) > self.max_len:
                return None, f"{self.name} is longer than {self.max_len} characters"
            if has_raw_syntax(t):
                return None, f"{self.name} contains tool syntax"
            if any(c in t for c in "<>{}"):
                return None, f"{self.name} contains characters < > {{ }}"
            return t, None
        if self.kind == "int":
            if isinstance(raw, bool):
                return None, f"{self.name} must be a whole number"
            if isinstance(raw, str) and re.fullmatch(r"\s*-?\d+\s*", raw):
                raw = int(raw)
            if isinstance(raw, float) and raw.is_integer():
                raw = int(raw)
            if not isinstance(raw, int):
                return None, f"{self.name} must be a whole number"
            if not self.lo <= raw <= self.hi:
                return None, f"{self.name} must be from {self.lo} to {self.hi}, not {raw}"
            return raw, None
        if self.kind == "scale":
            if isinstance(raw, str) and self.scale.has(raw):
                return self.scale.to_value(raw), None
            if tolerant:
                v = self.scale.parse(raw)
                if v is not None:
                    return v, None
            return None, f'{self.name} must be one of: ' + ", ".join(f'"{w}"' for w in self.scale.words)
        # enum
        if isinstance(raw, str):
            for c in self.choices:
                if c == raw:
                    return c, None
            if tolerant:
                low = {c.lower(): c for c in self.choices}
                if raw.strip().lower() in low:
                    return low[raw.strip().lower()], None
        return None, f'{self.name} must be one of: ' + ", ".join(f'"{c}"' for c in self.choices)


@dataclass
class ToolSpec:
    """A tool the model may call. ``result_scales`` turns numbers in a dict result back into words for the model.
    ``ends_turn``: once called, the turn is over (a "do nothing" choice); the loop does not ask the model again, so a
    model that would only repeat the same call never meets the repeat rule."""
    name: str
    description: str
    params: list[Param] = field(default_factory=list)
    result_scales: dict[str, Scale] = field(default_factory=dict)
    ends_turn: bool = False

    def call_key(self, values: dict[str, Any]) -> str:
        """What makes two calls the same call (the repeat rule): the values, without the commentary params."""
        skip = {p.name for p in self.params if p.same_call}
        return self.name + json.dumps({k: v for k, v in values.items() if k not in skip}, sort_keys=True, default=str)

    def __post_init__(self):
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", self.name):
            raise ValueError(f"bad tool name {self.name}")

    def signature(self) -> str:
        if not self.params:
            return f"{self.name}()"
        return f"{self.name}(" + ", ".join(p.name + ("" if p.required else "?") for p in self.params) + ")"

    def openai_tool(self) -> dict:
        return {"type": "function", "function": {
            "name": self.name, "description": self.description,
            "parameters": {"type": "object", "properties": {p.name: p.json_schema() for p in self.params},
                           "required": [p.name for p in self.params if p.required], "additionalProperties": False}}}

    def check_args(self, raw: Any, tolerant: bool) -> tuple[dict[str, Any], str | None]:
        """Validate raw model args and translate to game values. Unknown keys are an error."""
        if raw is None:
            raw = {}
        if not isinstance(raw, dict):
            return {}, "args must be an object"
        names = {p.name for p in self.params}
        extra = sorted(set(raw) - names)
        if extra:
            return {}, f"unknown argument {extra[0]} for {self.name}"
        out: dict[str, Any] = {}
        for p in self.params:
            if p.name not in raw:
                if p.required:
                    return {}, f"missing argument {p.name} for {self.name}"
                continue
            v, err = p.check(raw[p.name], tolerant)
            if err:
                return {}, err
            out[p.name] = v
        return out, None

    def render_result(self, result: Any) -> str:
        if isinstance(result, dict):
            r = {k: (self.result_scales[k].to_words(v) if k in self.result_scales and isinstance(v, (int, float)) and not isinstance(v, bool) else v)
                 for k, v in result.items()}
            return json.dumps(r, ensure_ascii=False)
        return "ok" if result is None else str(result)


# ------------------------------------------------------------------ grammar

def _lit(quoted_json: str) -> str:
    """A JSON string literal as a GBNF terminal."""
    return '"' + quoted_json.replace("\\", "\\\\").replace('"', '\\"') + '"'


def _tstr(n: int) -> str:
    # first char is not a space, so a string is never blank; the rest is bounded
    cls = '[^"\\\\\\n\\r\\t<>{} ]'
    rest = '[^"\\\\\\n\\r\\t<>{}]'
    return f'"\\"" {cls} {rest}{{0,{max(n - 1, 0)}}} "\\""'


def build_grammar(tools: list[ToolSpec], *, final_max: int = 200, allow_final: bool = True, allow_done: bool = True) -> str:
    """GBNF for exactly one step: a tool call, a final line, or done.

    {"tool":"name","args":{...}}  |  {"final":"one line"}  |  {"done":true}
    """
    # optional params are always written in the grammar; the validator accepts both forms
    alts: list[str] = []
    lens = {final_max} if allow_final else set()
    for t in tools:
        for p in t.params:
            if p.kind == "text":
                lens.add(p.max_len)
    rules: list[str] = []
    for n in sorted(lens):
        rules.append(f"tstr{n} ::= {_tstr(n)}")
    rules.append('integer ::= "-"? [0-9]{1,6}')
    for i, t in enumerate(tools):
        parts = []
        for j, p in enumerate(t.params):
            parts.append(('"," ' if j else "") + f'"\\"{p.name}\\":" {p.gbnf()}')
        body = '"{\\"tool\\":\\"' + t.name + '\\",\\"args\\":{" ' + " ".join(parts) + (" " if parts else "") + '"}}"'
        rules.append(f"call{i} ::= {body}")
        alts.append(f"call{i}")
    if allow_final:
        rules.append(f'final ::= "{{\\"final\\":" " "? tstr{final_max} "}}"')   # the space the PROTOCOL text shows; without it the 26B sometimes wrote a scrap like ":"
        alts.append("final")
    if allow_done:
        rules.append('done ::= "{\\"done\\":true}"')
        alts.append("done")
    if not alts:
        raise ValueError("no tools and no final or done: nothing the model may say")
    return "root ::= " + " | ".join(alts) + "\n" + "\n".join(rules) + "\n"


# ------------------------------------------------------------------ prompt text

PROTOCOL = (
    "Reply with exactly one JSON object and nothing else. The object is one of:\n"
    '  {"tool": "<name>", "args": {...}}   call one tool\n'
    '  {"final": "<one spoken line>"}       say a last line and stop\n'
    '  {"done": true}                       stop; nothing more to do\n'
)


def render_tools(tools: list[ToolSpec]) -> str:
    lines = ["Tools:"]
    for t in tools:
        lines.append(f"- {t.signature()}: {t.description}")
        for p in t.params:
            lines.append(f"    {p.name}: {p.description + '; ' if p.description else ''}{p.hint()}")
    return "\n".join(lines)


def render_system(base: str, tools: list[ToolSpec]) -> str:
    return f"{base.strip()}\n\n{PROTOCOL}\n{render_tools(tools)}"

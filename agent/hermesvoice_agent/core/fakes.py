"""Deterministic fake LLM for tests and offline replay (game-agnostic)."""
from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass, field

from .calllog import CallStats
from .llm import Completion, Messages


_CANNED = {
    "line": ["Hm. Noted.", "Not my favourite thing.", "Right. What now?", "That is going to hurt tomorrow.",
             "Keep your head down.", "I have seen worse. Not much worse.", "Somebody get the medicine."],
}


_DEFAULTS = {
    "reflect": '{"bio": "The recent days have left a mark on me, and I am changing slowly.", '
               '"wants": ["Get through the week"], "relations": [], "merge": []}',
    "diary": "Another day behind me. I kept my head down and got through it.",
    "thought": "That stew had better be hot.",
    "chronicle": "The community got through another day with what it had, and little more than that.",
    "art": "Hana made this over two quiet evenings, and it shows a lot of patience for a plain piece.",
    "chronicle_summary": "Earlier days: the community endured and kept working.",
}


@dataclass
class FakeLLM:
    """Deterministic: a line is picked by hashing (actor, turn count, last user text).
    ``script`` maps kind -> list of replies consumed in order (then falls back to canned)."""
    script: dict[str, list[str]] = field(default_factory=dict)
    calls: list[dict] = field(default_factory=list)
    slot_prompts: dict[int, str] = field(default_factory=dict)
    fail: bool = False

    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None, grammar: str | None = None,
                       temperature: float | None = None, actor: int | None = None, json_schema: dict | None = None) -> Completion:
        from .llm import LLMError
        if self.fail:
            raise LLMError("fake failure")
        flat = "\n".join(t for _, t in messages)
        self.calls.append({"kind": kind, "slot": slot, "messages": messages, "grammar": grammar, "actor": actor,
                           "json_schema": json_schema})
        q = self.script.get(kind)
        if q:
            text = q.pop(0)
        elif kind == "intent":
            last_line = messages[-2][1] if len(messages) >= 2 else ""
            text = ("kind" if re.search(r"\b(thanks|friend|sit|eat)\b", last_line, re.I) else
                    "insult" if re.search(r"\b(idiot|useless|fool)\b", last_line, re.I) else "none")
        elif kind in _DEFAULTS:
            text = _DEFAULTS[kind]
        elif kind == "line":
            h = int(hashlib.sha1((str(actor) + flat[-200:]).encode()).hexdigest(), 16)
            text = _CANNED["line"][h % len(_CANNED["line"])]
        else:
            text = "Fine."
        # crude cache model: shared leading characters with the slot's previous prompt count as cached
        prev = self.slot_prompts.get(slot if slot is not None else -1, "")
        n = prev[: next((i for i, (x, y) in enumerate(zip(prev, flat)) if x != y), min(len(prev), len(flat)))]
        self.slot_prompts[slot if slot is not None else -1] = flat
        st = CallStats(kind=kind, slot=slot, actor=actor, prompt_n=max(1, (len(flat) - len(n)) // 4),
                       cache_n=len(n) // 4, prompt_ms=10.0, predicted_n=max(1, len(text) // 4),
                       predicted_ms=20.0)
        return Completion(text, st)

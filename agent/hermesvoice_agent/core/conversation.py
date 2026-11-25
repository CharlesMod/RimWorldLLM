"""Conversations (SPEC §7; milestone 4 groundwork, kept simple).

Two windows, append-only turns, an opener obliges a reply only when it calls for one, and the
end is by rule: turn cap, decaying continue chance, repetition check, actors apart, either actor's
job changing, preemption. At the end each actor gets one memory and one relation update.
"""
from __future__ import annotations

import random
import re
from dataclasses import dataclass, field

from .memory import keywords

_OBLIGE = re.compile(r"\?\s*$|^(?:can|could|will|would|do|did|are|is|why|what|where|who|how)\b|"
                     r"^(?:give|bring|get|come|help|stop|tell|look|move|sit|eat|go|pass)\b", re.I)
OBLIGING_INTENTS = {"insult", "confide", "comfort"}


def opener_obliges(line: str, intent: str = "none") -> bool:
    """Question, insult, request or confession calls for an answer."""
    return bool(_OBLIGE.search(line.strip())) or intent in OBLIGING_INTENTS


def repetitive(line: str, prior: list[str], threshold: float = 0.7) -> bool:
    a = keywords(line) or set(line.lower().split())
    for p in prior[-3:]:
        b = keywords(p) or set(p.lower().split())
        if a and b and len(a & b) / len(a | b) >= threshold:
            return True
        if line.strip().lower() == p.strip().lower():
            return True
    return False


@dataclass
class Turn:
    speaker: int
    name: str
    text: str
    intent: str = "none"


@dataclass
class Conversation:
    id: str
    a: int                      # opener
    b: int
    names: dict[int, str]
    turns: list[Turn] = field(default_factory=list)
    obliged: bool = False
    ended: str = ""
    turn_cap: int = 6
    continue_p0: float = 0.85
    decay: float = 0.7

    def next_speaker(self) -> int:
        return self.a if len(self.turns) % 2 == 0 else self.b

    def listener_of(self, speaker: int) -> int:
        return self.b if speaker == self.a else self.a

    def add(self, speaker: int, text: str, intent: str = "none") -> Turn:
        t = Turn(speaker, self.names[speaker], text, intent)
        self.turns.append(t)
        if len(self.turns) == 1:
            self.obliged = opener_obliges(text, intent)
        return t

    def turns_for_prompt(self) -> list[tuple[str, str]]:
        """Append-only block F: every turn so far, own lines and heard speech alike."""
        return [(t.name, t.text) for t in self.turns]

    def continue_chance(self) -> float:
        return self.continue_p0 * self.decay ** max(0, len(self.turns) - 1)

    def check_end(self, rng: random.Random, *, apart: bool = False, job_changed: bool = False,
                  preempted: bool = False) -> str:
        """Returns the end reason or ''. Evaluated after each turn."""
        if preempted:
            return "preempted"
        if apart:
            return "apart"
        if job_changed:
            return "job_changed"
        if len(self.turns) >= self.turn_cap:
            return "turn_cap"
        last = self.turns[-1] if self.turns else None
        if last and repetitive(last.text, [t.text for t in self.turns[:-1]]):
            return "repetition"
        if len(self.turns) == 1 and self.obliged:
            return ""                      # opener obliges a reply: the second turn happens
        if rng.random() > self.continue_chance():
            return "chance"
        return ""


def template_memory(conv: Conversation, me: int) -> str:
    other = conv.names[conv.listener_of(me)] if me in (conv.a, conv.b) else "someone"
    other = conv.names[conv.b if me == conv.a else conv.a]
    last = conv.turns[-1].text if conv.turns else ""
    first = conv.turns[0]
    who = "I" if first.speaker == me else other
    return (f"I talked with {other}. {who} started it: \"{first.text}\" "
            f"It ended on: \"{last}\"")


def template_relation(conv: Conversation, me: int) -> tuple[str, str]:
    """(feeling, history line) from the intents that went across; deterministic fallback."""
    other_id = conv.b if me == conv.a else conv.a
    intents = [t.intent for t in conv.turns if t.speaker == other_id]
    if any(i in ("insult", "slight") for i in intents):
        feel = "Wary after the way they spoke to me."
    elif any(i in ("kind", "comfort", "confide") for i in intents):
        feel = "Warmer toward them after that talk."
    else:
        feel = "Nothing much changed."
    return feel, f"Last talked about: {conv.turns[0].text[:60]}"

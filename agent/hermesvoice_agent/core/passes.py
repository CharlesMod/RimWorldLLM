"""Line pass and intent pass: a line (stop at newline, 48-token cap), then the
GBNF-constrained intent in the same slot. A failed line is dropped, never shown."""
from __future__ import annotations

from dataclasses import dataclass, field

from .llm import LLMClient, LLMError, Messages
from .minds import Mind
from .prompt import DELIVERIES, grammar_for, intent_messages, line_messages
from .sanitize import Sanitized, sanitize_line


@dataclass
class SanitizeCtx:
    speaker_names: set[str]
    known_names: set[str]
    all_names: set[str]
    prompt_words: set[str]
    common_words: set[str] = field(default_factory=set)
    secrets: list[str] = field(default_factory=list)   # private memory texts the listener must not hear quoted
    third_person: bool = False                         # the speaker's voice card speaks of itself by name


@dataclass
class Spoken:
    text: str
    intent: str
    delivery: str | None = None      # "normal" | "shout" | "whisper"; None = the pass did not say (the mod guesses)


@dataclass
class Dropped:
    reason: str


async def line_pass(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                    turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                    max_tokens: int = 48, request: str | None = None) -> tuple[Sanitized, Messages]:
    msgs = line_messages(prefix, turns, mind, name, turn_no, request)
    try:
        c = await llm.complete(msgs, slot=slot, kind="line", max_tokens=max_tokens,
                               stop=["\n"], actor=mind.actor_id)
    except LLMError as e:
        return Sanitized(None, f"llm_error:{e.cause}"), msgs
    s = sanitize_line(c.text, speaker_names=ctx.speaker_names, known_names=ctx.known_names,
                      all_names=ctx.all_names, prompt_words=ctx.prompt_words, common_words=ctx.common_words, secrets=ctx.secrets,
                      narration_guard=not ctx.third_person,
                      truncated=c.stats.predicted_n >= max_tokens)
    return s, msgs


def parse_intent(text: str, labels: list[str]) -> str:
    t = text.strip().lower().strip(".:\"' ")
    if t in labels:
        return t
    first = t.split()[0] if t.split() else ""
    return first if first in labels else labels[0]    # unparseable means the "nothing" label


def parse_delivery(text: str) -> str | None:
    words = text.strip().lower().replace(",", " ").split()
    return next((w.strip(".:\"'") for w in words[1:] if w.strip(".:\"'") in DELIVERIES), None)


async def intent_pass(llm: LLMClient, *, slot: int, msgs: Messages, line: str,
                      listener_name: str | None, labels: list[str], actor: int | None = None) -> tuple[str, str | None]:
    """Same warm slot: prefix is byte-identical to the line pass, so only the tail is prefilled.
    One constrained completion gives the social label and the delivery."""
    if listener_name is None:
        return labels[0], None
    try:
        c = await llm.complete(intent_messages(msgs, line, listener_name, DELIVERIES), slot=slot, kind="intent",
                               max_tokens=8, stop=[], grammar=grammar_for(labels, DELIVERIES), temperature=0.0,
                               actor=actor)
    except LLMError:
        return labels[0], None
    return parse_intent(c.text, labels), parse_delivery(c.text)


async def speak(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                listener_name: str | None, labels: list[str], max_tokens: int = 48,
                request: str | None = None) -> Spoken | Dropped:
    s, msgs = await line_pass(llm, slot=slot, mind=mind, name=name, prefix=prefix, turns=turns,
                              turn_no=turn_no, ctx=ctx, max_tokens=max_tokens, request=request)
    if not s.ok:
        return Dropped(s.reason)
    intent, delivery = await intent_pass(llm, slot=slot, msgs=msgs, line=s.text, listener_name=listener_name,
                               labels=labels, actor=mind.actor_id)
    return Spoken(s.text, intent, delivery)

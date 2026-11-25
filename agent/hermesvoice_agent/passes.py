"""Line pass and intent pass (SPEC §4): a line (stop at newline, 48-token cap), then the
GBNF-constrained intent in the same slot. A failed line is dropped, never shown."""
from __future__ import annotations

from dataclasses import dataclass

from .llm import LLMClient, LLMError, Messages
from .minds import Mind
from .prompt import GRAMMAR_INTENT, INTENTS, intent_messages, line_messages
from .sanitize import Sanitized, sanitize_line


@dataclass
class SanitizeCtx:
    speaker_names: set[str]
    known_names: set[str]
    all_names: set[str]
    prompt_words: set[str]


@dataclass
class Spoken:
    text: str
    intent: str


@dataclass
class Dropped:
    reason: str


async def line_pass(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                    turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                    max_tokens: int = 48, request: str | None = None) -> tuple[Sanitized, Messages]:
    msgs = line_messages(prefix, turns, mind, name, turn_no, request)
    try:
        c = await llm.complete(msgs, slot=slot, kind="line", max_tokens=max_tokens,
                               stop=["\n"], pawn=mind.pawn_id)
    except LLMError as e:
        return Sanitized(None, f"llm_error:{e.cause}"), msgs
    s = sanitize_line(c.text, speaker_names=ctx.speaker_names, known_names=ctx.known_names,
                      all_names=ctx.all_names, prompt_words=ctx.prompt_words,
                      truncated=c.stats.predicted_n >= max_tokens)
    return s, msgs


def parse_intent(text: str) -> str:
    t = text.strip().lower().strip(".:\"' ")
    if t in INTENTS:
        return t
    first = t.split()[0] if t.split() else ""
    return first if first in INTENTS else "none"      # unparseable means none


async def intent_pass(llm: LLMClient, *, slot: int, msgs: Messages, line: str,
                      listener_name: str | None, pawn: int | None = None) -> str:
    """Same warm slot: prefix is byte-identical to the line pass, so only the tail is prefilled."""
    if listener_name is None:
        return "none"
    try:
        c = await llm.complete(intent_messages(msgs, line, listener_name), slot=slot, kind="intent",
                               max_tokens=4, stop=[], grammar=GRAMMAR_INTENT, temperature=0.0, pawn=pawn)
    except LLMError:
        return "none"
    return parse_intent(c.text)


async def speak(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                listener_name: str | None, max_tokens: int = 48,
                request: str | None = None) -> Spoken | Dropped:
    s, msgs = await line_pass(llm, slot=slot, mind=mind, name=name, prefix=prefix, turns=turns,
                              turn_no=turn_no, ctx=ctx, max_tokens=max_tokens, request=request)
    if not s.ok:
        return Dropped(s.reason)
    intent = await intent_pass(llm, slot=slot, msgs=msgs, line=s.text, listener_name=listener_name,
                               pawn=mind.pawn_id)
    return Spoken(s.text, intent)

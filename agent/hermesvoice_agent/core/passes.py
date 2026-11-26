"""Line pass and intent pass: a line (stop at newline, 48-token cap), then the
GBNF-constrained intent in the same slot. A failed line is dropped, never shown."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable

from .llm import LLMClient, LLMError, Messages
from .minds import Mind
from .inner import AIM_EXTRA_TOKENS, sanitize_aim
from .prompt import (AIM_ONLY_GRAMMAR, DELIVERIES, aim_messages, grammar_for, intent_messages,
                     line_messages)
from .sanitize import Sanitized, sanitize_line


@dataclass
class SanitizeCtx:
    speaker_names: set[str]
    known_names: set[str]
    all_names: set[str]
    prompt_words: set[str]
    common_words: set[str] = field(default_factory=set)
    secrets: list[str] = field(default_factory=list)   # private memory texts the listener must not hear quoted
    minor_involved: bool = False                       # the speaker or the listener is under 18 (or of unknown age)
    minor_names: set[str] = field(default_factory=set)  # name tokens of every minor in the world
    third_person: bool = False                         # the speaker's voice card speaks of itself by name
    intent_labels: tuple[str, ...] = ()                # cut a label the model leaks after the line ("... kind")


@dataclass
class Spoken:
    text: str
    intent: str
    delivery: str | None = None      # "normal" | "shout" | "whisper"; None = the pass did not say (the mod guesses)
    aim: str | None = None           # the private aim (sanitised, age-gated); None = off or dropped (never drops the line)
    aim_gated: bool = False          # the age gate touched the aim (it was dropped for it)


@dataclass
class Dropped:
    reason: str


async def line_pass(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                    turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                    max_tokens: int = 48, request: str | None = None,
                    aim: str | None = None, speech: str | None = None) -> tuple[Sanitized, Messages]:
    msgs = line_messages(prefix, turns, mind, name, turn_no, request, aim, speech)
    try:
        c = await llm.complete(msgs, slot=slot, kind="line", max_tokens=max_tokens,
                               stop=["\n"], actor=mind.actor_id)
    except LLMError as e:
        return Sanitized(None, f"llm_error:{e.cause}"), msgs
    s = sanitize_line(c.text, speaker_names=ctx.speaker_names, known_names=ctx.known_names,
                      all_names=ctx.all_names, prompt_words=ctx.prompt_words, common_words=ctx.common_words, secrets=ctx.secrets,
                      narration_guard=not ctx.third_person,
                      involves_minor=ctx.minor_involved, minor_names=ctx.minor_names,
                      intent_labels=ctx.intent_labels,
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


def parse_intent_aim(text: str, labels: list[str]) -> tuple[str, str | None, str]:
    """"label delivery aim words...": the label, the delivery (None if absent) and the rest as the raw aim."""
    words = text.strip().split()
    label = labels[0]
    if words and words[0].lower().strip(".:\"' ") in labels:
        label = words.pop(0).lower().strip(".:\"' ")
    elif words:
        words.pop(0)
    delivery = None
    if words and words[0].lower().strip(".:\"',") in DELIVERIES:
        delivery = words.pop(0).lower().strip(".:\"',")
    return label, delivery, " ".join(words)


async def intent_pass_aim(llm: LLMClient, *, slot: int, msgs: Messages, line: str, listener_name: str | None,
                          labels: list[str], actor: int | None = None, aim: bool = False,
                          solo_aim: bool = True) -> tuple[str, str | None, str]:
    """The intent pass, optionally with the private aim in the same call (aim=True). Returns (label, delivery, raw aim);
    the raw aim is "" when off or not asked. With no listener there is no call, unless aim and solo_aim: then an
    aim-only call (root ::= aim)."""
    if listener_name is None:
        if not (aim and solo_aim):
            return labels[0], None, ""
        try:
            c = await llm.complete(aim_messages(msgs, line), slot=slot, kind="intent", max_tokens=AIM_EXTRA_TOKENS + 4,
                                   stop=[], grammar=AIM_ONLY_GRAMMAR, temperature=0.0, actor=actor)
        except LLMError:
            return labels[0], None, ""
        return labels[0], None, c.text
    try:
        c = await llm.complete(intent_messages(msgs, line, listener_name, DELIVERIES, aim), slot=slot, kind="intent",
                               max_tokens=8 + (AIM_EXTRA_TOKENS if aim else 0), stop=[],
                               grammar=grammar_for(labels, DELIVERIES, aim), temperature=0.0, actor=actor)
    except LLMError:
        return labels[0], None, ""
    if aim:
        return parse_intent_aim(c.text, labels)
    return parse_intent(c.text, labels), parse_delivery(c.text), ""


async def intent_pass(llm: LLMClient, *, slot: int, msgs: Messages, line: str,
                      listener_name: str | None, labels: list[str], actor: int | None = None) -> tuple[str, str | None]:
    """Same warm slot: prefix is byte-identical to the line pass, so only the tail is prefilled.
    One constrained completion gives the social label and the delivery."""
    label, delivery, _ = await intent_pass_aim(llm, slot=slot, msgs=msgs, line=line, listener_name=listener_name,
                                               labels=labels, actor=actor)
    return label, delivery


async def speak(llm: LLMClient, *, slot: int, mind: Mind, name: str, prefix: str,
                turns: list[tuple[str, str]], turn_no: int, ctx: SanitizeCtx,
                listener_name: str | None, labels: list[str], max_tokens: int = 48,
                request: str | None = None, aim: bool = False, solo_aim: bool = True, fresh_aim: str | None = None,
                check: Callable[[str], tuple[str, str] | None] | None = None,
                retries: int = 0, on_check: Callable[[dict], None] | None = None,
                speech: str | None = None) -> Spoken | Dropped:
    """A line, then its intent. ``check(text)`` is a further validation of a sanitised line: it returns None when
    the line is fine, or (reason code, request text for the retry). A refused line is retried up to ``retries``
    times with that request, then the result is Dropped(reason code). ``on_check`` gets one event per check."""
    s, msgs = await line_pass(llm, slot=slot, mind=mind, name=name, prefix=prefix, turns=turns,
                              turn_no=turn_no, ctx=ctx, max_tokens=max_tokens, request=request, aim=fresh_aim,
                              speech=speech)
    attempt = 0
    while s.ok and check is not None:
        bad = check(s.text)
        if on_check:
            on_check({"attempt": attempt, "text": s.text, "refused": bool(bad), "reason": bad[0] if bad else ""})
        if bad is None:
            break
        if attempt >= retries:
            return Dropped(bad[0])
        attempt += 1
        s, msgs = await line_pass(llm, slot=slot, mind=mind, name=name, prefix=prefix, turns=turns,
                                  turn_no=turn_no, ctx=ctx, max_tokens=max_tokens, request=bad[1], aim=fresh_aim,
                                  speech=speech)
    if not s.ok:
        return Dropped(s.reason)
    intent, delivery, raw_aim = await intent_pass_aim(llm, slot=slot, msgs=msgs, line=s.text, listener_name=listener_name,
                                                      labels=labels, actor=mind.actor_id, aim=aim, solo_aim=solo_aim)
    aim_text, gated = sanitize_aim(raw_aim, ctx)
    return Spoken(s.text, intent, delivery, aim_text, gated)

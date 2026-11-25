"""The "Previously on <colony>..." recap shown when the player loads a save (aliveness item 12).

One narrator-slot call, once per save epoch, built from the save's own chronicle (the mod's copy is the truth after a
load). The result is 3 short lines POSTed to /recap; the mod decides whether to show them (only right after a load, once
per epoch). It is not a bubble and spends no voice budget. A failed or unusable call falls back to a recap built from
the chronicle text with no model."""
from __future__ import annotations

import logging
import re
from typing import TYPE_CHECKING

from . import agegate
from . import gamedata as gd
from .bridge import BridgeError
from .llm import LLMError
from .sanitize import prompt_cap_words, sanitize_prose

if TYPE_CHECKING:
    from .director import Director

log = logging.getLogger(__name__)

MIN_ENTRIES = 2          # fewer chronicle entries than this: nothing to recap
USE_ENTRIES = 4          # the last few entries go into the prompt
LINES = 3
LINE_CHARS = 160
MAX_TOKENS = 90

RECAP_SYSTEM = (
    "You write the 'Previously on...' recap that greets the player when a saved RimWorld colony is loaded, like the "
    "start of a TV episode. Third person, past tense, plain words, a little dry. Use only what the chronicle entries "
    "say; never invent people, places or numbers, never propose or predict events, never address the player.")
RECAP_REQUEST = (
    f"Write exactly {LINES} lines, one short sentence each, under 20 words, most recent last, the last line ending on "
    "what is still unresolved. No title, no numbering, no quotes.")

_LEAD = re.compile(r"^\s*(?:[-*•>]+|\d+[.)])\s*")
_SENT = re.compile(r"(?<=[.!?])\s+")


def messages(entries: list[dict], day_label=lambda e: int(e.get("day", 0)) + 1) -> list[tuple[str, str]]:
    """System + user messages for the recap call. Every chronicle text is cleaned and wrapped as game data."""
    body = "\n".join(f"Day {day_label(e)}: {gd.clean(e.get('text', ''), 'event', max_len=320)}"
                     for e in entries[-USE_ENTRIES:])
    user = f"=== CHRONICLE (oldest first) ===\n{gd.data_section('chronicle', body)}=== REQUEST ===\n{RECAP_REQUEST}"
    return [("system", RECAP_SYSTEM + "\n\n" + gd.DATA_RULE), ("user", user)]


def parse_lines(raw: str) -> list[str]:
    out = []
    for ln in raw.splitlines():
        ln = _LEAD.sub("", ln).strip()
        if ln:
            out.append(ln)
    return out[:LINES]


def fallback_lines(entries: list[dict], minor_names: set[str] | None = None) -> list[str]:
    """No model: the first sentence of each of the last 3 entries, oldest first."""
    out = []
    for e in entries[-LINES:]:
        t = gd.clean(e.get("text", ""), "event", max_len=400)
        first = _SENT.split(t, 1)[0].strip()
        if not first or agegate.violation(first, minor_names=minor_names or ()):
            continue
        out.append(gd._cut(first, LINE_CHARS))
    return out


async def build(d: "Director", entries: list[dict]) -> list[str]:
    """The recap lines (1 to 3), from the model when it gives usable lines, else from the chronicle itself."""
    minors = d.minor_tokens()
    msgs = messages(entries)
    allnames = set().union(*d.name_tokens.values()) if d.name_tokens else set()
    lines: list[str] = []
    try:
        c = await d.llm.complete(msgs, slot=d.cfg.narrator_slot, kind="recap", max_tokens=MAX_TOKENS, stop=[],
                                 temperature=0.7)
        pw = prompt_cap_words(msgs[1][1])
        for raw in parse_lines(c.text):
            s = sanitize_prose(raw, known_names=allnames, all_names=allnames, prompt_words=pw,
                               common_words=set(d.adapter.common_words), min_words=3, max_words=30,
                               minor_names=minors)
            if s.ok:
                lines.append(gd._cut(s.text, LINE_CHARS))
    except LLMError as ex:
        log.info("recap call failed: %s", ex)
    if len(lines) < 2:
        lines = fallback_lines(entries, minors)
    return lines[:LINES]


async def run(d: "Director", epoch: int) -> bool:
    """Once per epoch: read the save's chronicle, write the recap, POST it. True when the mod took it."""
    if epoch == 0 or d.recap_epoch == epoch or not d.cfg.recap_enabled or d.bridge_cfg.get("recap") is False:
        return False
    d.recap_epoch = epoch
    try:
        entries = await d.bridge.get_chronicle()
    except (BridgeError, AttributeError) as ex:
        log.info("recap skipped, no chronicle: %s", ex)
        return False
    entries = [e for e in entries if str(e.get("text", "")).strip()]
    if len(entries) < MIN_ENTRIES:
        return False
    lines = await build(d, entries)
    if not lines or d.bridge.env.epoch != epoch:      # nothing usable, or another load came meanwhile
        return False
    try:
        await d.bridge.recap(epoch, lines)
    except BridgeError as ex:
        log.info("recap not shown: %s", ex)
        return False
    return True

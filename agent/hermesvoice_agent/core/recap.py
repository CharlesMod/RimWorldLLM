"""The "Previously on ..." recap shown when the player loads a save (aliveness item 12).

One narrator-slot call, once per save epoch, built from the save's own chronicle (the mod's copy is the truth after a
load). The result is 3 short lines POSTed to /recap; the mod decides whether to show them (only right after a load, once
per epoch). It is not a bubble and spends no voice budget. A failed or unusable call falls back to a recap built from
the chronicle text with no model."""
from __future__ import annotations

import logging
import re
from typing import TYPE_CHECKING

from . import agegate
from . import creative
from . import gamedata as gd
from . import grounding
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


_LEAD = re.compile(r"^\s*(?:[-*•>]+|\d+[.)])\s*")
_SENT = re.compile(r"(?<=[.!?])\s+")


RANK_WINDOW = 12         # creative_ground: the last this-many entries are ranked
RANK_TOP = 4             # ... and the top ones go in, biggest first
GROUND_REQUEST = ("Write exactly 3 lines, one short sentence each, under 20 words. Lead with the entry marked (biggest): "
                  "name who it happened to and what changed. Then the others in the order they happened; keep separate "
                  "events separate. End on what is still unresolved. No title, no numbering, no quotes.")


def messages(adapter, entries: list[dict], day_label=lambda e: int(e.get("day", 0)) + 1,
             ground: bool = False) -> list[tuple[str, str]]:
    """System + user messages for the recap call. Every chronicle text is cleaned and wrapped as game data.
    ``ground`` (creative_ground) ranks the last RANK_WINDOW entries by importance and lists the top ones biggest first."""
    if ground:
        top = grounding.rank_entries(adapter, entries, RANK_WINDOW, RANK_TOP)
        # the top entries in the order they happened (ranked order made the model merge separate events), the biggest marked
        ids = {id(e) for e in top}
        order = [e for e in entries[-RANK_WINDOW:] if id(e) in ids]
        body = "\n".join(("(biggest) " if top and e is top[0] else "") + f"Day {day_label(e)}: "
                         f"{gd.clean(e.get('text', ''), 'event', max_len=320)}" for e in order)
        user = (f"=== CHRONICLE (oldest first) ===\n{gd.data_section('chronicle', body)}"
                f"=== REQUEST ===\n{getattr(adapter, 'recap_request_ground', GROUND_REQUEST)}")
    else:
        body = "\n".join(f"Day {day_label(e)}: {gd.clean(e.get('text', ''), 'event', max_len=320)}"
                         for e in entries[-USE_ENTRIES:])
        user = f"=== CHRONICLE (oldest first) ===\n{gd.data_section('chronicle', body)}=== REQUEST ===\n{adapter.recap_request}"
    return [("system", adapter.recap_system + "\n\n" + gd.DATA_RULE + "\n" + agegate.MINOR_RULE), ("user", user)]


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
    msgs = messages(d.adapter, entries, ground=creative.on(d, "ground", "recap"))
    allnames = set().union(*d.name_tokens.values()) if d.name_tokens else set()
    pw = prompt_cap_words(msgs[1][1])

    async def write(note: str) -> str | None:
        m = msgs if not note else msgs[:-1] + [(msgs[-1][0], msgs[-1][1].rstrip("\n") + "\n" + note)]
        c = await d.llm.complete(m, slot=d.cfg.narrator_slot, kind="recap", max_tokens=MAX_TOKENS, stop=[],
                                 temperature=0.7)
        got = []
        for raw in parse_lines(c.text):
            s = sanitize_prose(raw, known_names=allnames, all_names=allnames, prompt_words=pw,
                               common_words=set(d.adapter.common_words), min_words=3, max_words=30,
                               minor_names=minors)
            if s.ok:
                got.append(gd._cut(s.text, LINE_CHARS))
        return "\n".join(got) if len(got) >= 2 else None

    async def plan() -> str | None:
        pm = msgs[:-1] + [(msgs[-1][0], msgs[-1][1].split("=== REQUEST ===")[0] + "=== REQUEST ===\n" + grounding.PLAN_ASK)]
        c = await d.llm.complete(pm, slot=d.cfg.narrator_slot, kind="recap", max_tokens=grounding.PLAN_TOKENS,
                                 stop=["\n"], temperature=0.7)
        return c.text

    lines: list[str] = []
    try:
        text, _ = await creative.compose(d, kind="recap", slot=d.cfg.narrator_slot, actor=None,
                                         facts=msgs[1][1].split("=== REQUEST ===")[0], names=set(),
                                         write=write, plan=plan,
                                         should_yield=lambda: grounding.other_reactive_waiting(d, None))
        lines = text.splitlines() if text else []
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
    entries = [e for e in entries if str(e.get("text", "")).strip() and e.get("kind") != "lore"]   # lore is not history
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


def late_due(d: "Director") -> bool:
    """A late connect: the mod says a recap is still open (GET /config recap_open) for an epoch we have not tried."""
    epoch = d.bridge.env.epoch
    return (bool(epoch) and d.bridge_cfg.get("recap_open") is True and d.recap_epoch != epoch
            and d.cfg.recap_enabled and d.bridge_cfg.get("recap") is not False)

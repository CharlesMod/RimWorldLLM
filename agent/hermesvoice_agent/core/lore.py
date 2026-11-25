"""Shared lore (aliveness item 4): shared nicknames for places and events ("the Long Winter", "Bram's Folly").

Coined by the narrator in one small extra call after a major chronicle entry (kind "lore", about 24 tokens: "Name: what
it refers to", or "none"). Stored once in the mod's chronicle book as kind "lore" (text "Name: what it refers to") so it
travels with the save, and cached here; the oldest fade past ``max_entries``. Reused in block D (the entries that match
the scene) and in the chronicle prompt, so the narrator keeps one set of names.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass

from . import agegate
from . import gamedata as gd
from .bridge import BridgeError
from .llm import LLMError
from .memory import keywords
from .sanitize import prompt_cap_words, sanitize_prose

log = logging.getLogger(__name__)

MAX_TOKENS = 24
NAME_WORDS = (2, 5)
WHAT_CHARS = 60
NAMES_LINE_CHARS = 150
_NONE = re.compile(r"^\W*none\W*$", re.I)


@dataclass
class LoreEntry:
    name: str
    what: str
    game: int = 0
    day: int = 0

    @property
    def text(self) -> str:
        return f"{self.name}: {self.what}"


def parse_entry(text: str) -> tuple[str, str] | None:
    """"Name: what" with a 2-5 word name and a short referent; None for "none" or anything else."""
    t = " ".join(text.split()).strip(" \"'")
    if not t or _NONE.match(t) or ":" not in t:
        return None
    name, what = (x.strip(" \"'.") for x in t.split(":", 1))
    if not (NAME_WORDS[0] <= len(name.split()) <= NAME_WORDS[1]) or len(name) > 40 or not what:
        return None
    return name, what[:WHAT_CHARS].rstrip()


class LoreBook:
    def __init__(self, max_entries: int = 12):
        self.max = max_entries
        self.entries: list[LoreEntry] = []

    def add(self, name: str, what: str, game: int = 0, day: int = 0) -> LoreEntry:
        key = name.casefold()
        self.entries = [e for e in self.entries if e.name.casefold() != key]      # a re-coined name replaces itself
        e = LoreEntry(name, what, game, day)
        self.entries.append(e)
        del self.entries[:-self.max]                                              # the oldest fade
        return e

    def load(self, bridge_entries: list[dict]) -> None:
        """Replace the cache with the kind "lore" entries the mod holds (the save's truth), oldest first."""
        self.entries = []
        for e in bridge_entries:
            if e.get("kind") != "lore":
                continue
            p = parse_entry(str(e.get("text", "")))
            if p:
                self.add(p[0], p[1], 0, int(e.get("day", 0) or 0))

    def rollback(self, game: int) -> None:
        self.entries = [e for e in self.entries if e.game <= game]

    def lines(self) -> list[str]:
        return [e.text for e in self.entries]

    def relevant(self, scene_text: str, k: int = 3) -> list[LoreEntry]:
        """The entries sharing words with the scene (name or referent), best first; none when nothing matches."""
        words = keywords(scene_text)
        scored = []
        for e in self.entries:
            hit = len(words & keywords(e.text))
            if hit:
                scored.append((hit, e.game, e))
        scored.sort(key=lambda t: (-t[0], -t[1]))
        return [e for _, _, e in scored[:k]]

    def names_line(self, scene_text: str, k: int = 3, label: str = "Shared names") -> str:
        """The block D line ("Shared names: ..."), or "" when nothing is relevant."""
        parts, used = [], 0
        for e in self.relevant(scene_text, k):
            t = gd.clean(e.text, "line", max_len=60)
            if used + len(t) > NAMES_LINE_CHARS:
                break
            parts.append(t)
            used += len(t) + 2
        return f"{label}: " + "; ".join(parts) if parts else ""


def messages(adapter, entry_text: str, existing: list[str]) -> list[tuple[str, str]]:
    have = "\n".join(f"- {gd.clean(t, 'line', max_len=80)}" for t in existing) or "- none yet"
    user = (f"=== CHRONICLE ENTRY ===\n{gd.data_section('entry', gd.clean(entry_text, 'event', max_len=700))}"
            f"=== NAMES ALREADY IN USE ===\n{gd.data_section('names', have)}"
            f"=== REQUEST ===\n{adapter.lore_request}")
    return [("system", adapter.lore_system + "\n\n" + gd.DATA_RULE), ("user", user)]


async def coin(d, entry_text: str, game: int, day: int) -> LoreEntry | None:
    """One small narrator call after a major entry. Stores and returns the new entry, or None ("none", unusable text)."""
    if not d.cfg.lore:
        return None
    msgs = messages(d.adapter, entry_text, d.lore.lines())
    try:
        c = await d.llm.complete(msgs, slot=d.cfg.narrator_slot, kind="lore", max_tokens=MAX_TOKENS, stop=["\n"],
                                 temperature=0.7)
    except LLMError as ex:
        log.info("lore call failed: %s", ex)
        return None
    if _NONE.match(c.text or ""):
        return None
    allnames = set().union(*d.name_tokens.values()) if d.name_tokens else set()
    minors = d.minor_tokens()
    s = sanitize_prose(c.text, known_names=allnames, all_names=allnames, prompt_words=prompt_cap_words(msgs[1][1]),
                       common_words=set(d.adapter.common_words), min_words=3, max_words=14, minor_names=minors)
    if not s.ok:
        return None
    p = parse_entry(s.text)
    if p is None or agegate.violation(s.text, minor_names=minors):
        return None
    e = d.lore.add(p[0], p[1], game, day)
    try:
        await d.bridge.chronicle(day, e.text, "lore")
    except (BridgeError, AttributeError) as ex:
        log.info("lore kept in the agent only: %s", ex)
    return e


async def load(d) -> None:
    """Read the save's lore from the mod (after a start or a save_epoch change)."""
    try:
        d.lore.load(await d.bridge.get_chronicle())
    except (BridgeError, AttributeError) as ex:
        log.info("no lore read: %s", ex)

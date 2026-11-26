"""Shared helpers for creative_ground: richer, cleaned game facts for art, recap, lore and ceremony prompts.

Everything returned is game text already passed through gamedata.clean; the callers wrap it in data sections.
Game-specific wording and word lists come from the adapter (``creative_card``, ``recap_importance``).
"""
from __future__ import annotations

import re
from typing import Iterable

from . import gamedata as gd
from .bridge import BridgeError
from .lease import Cls

_STOP = frozenset("the a an and or of in on at to for with from by his her their its this that was were is are it he she they "
                  "them there then when while after before day today night morning evening".split())
_CAP_WORD = re.compile(r"\b[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,})?\b")
_AFTER_ARTICLE = re.compile(r"\b(?:the|a|an)\s+([a-z][a-z-]{2,}(?:\s+[a-z][a-z-]{2,})?)", re.I)
_NOT_REFERENT = frozenset("day days hour hours night morning evening time way rest while first last other same".split())


async def actor_card(d, aid: int) -> dict:
    """{"traits": [...], "backstory_title", "mood", "mood_thoughts": [...]}: empty when the game gives none."""
    fn = getattr(d.adapter, "creative_card", None)
    if fn is None:
        return {}
    try:
        snap = await d.bridge.actor(aid)
    except (BridgeError, AttributeError, KeyError):
        return {}
    try:
        return fn(snap) or {}
    except Exception:                                   # a snapshot shape we do not know: no card, never a crash
        return {}


def card_lines(card: dict) -> list[str]:
    out = []
    if card.get("traits"):
        out.append("Traits: " + ", ".join(card["traits"]))
    if card.get("backstory_title"):
        out.append(f"Background: {card['backstory_title']}")
    if card.get("mood"):
        t = f"Mood now: {card['mood']}"
        if card.get("mood_thoughts"):
            t += " (" + ", ".join(card["mood_thoughts"]) + ")"
        out.append(t)
    return out


def tokens_of(names: Iterable[str]) -> set[str]:
    return {w.casefold() for n in names for w in re.findall(r"[A-Za-z']{3,}", str(n))}


def mentions(text: str, tokens: set[str]) -> bool:
    """True when the text names one of the (casefolded) tokens as a whole word."""
    if not tokens:
        return False
    return any(w.casefold() in tokens for w in re.findall(r"[A-Za-z']{3,}", text or ""))


def pick_mentioning(texts: list[str], tokens: set[str], k: int, fallback_recent: bool = True) -> list[str]:
    """Up to ``k`` texts naming the tokens (newest kept); when none do, the newest ``k``."""
    hit = [t for t in texts if mentions(t, tokens)][-k:]
    if hit or not fallback_recent:
        return hit
    return texts[-k:]


def importance(adapter, text: str, kind: str = "") -> int:
    score = 0
    for pat, w in getattr(adapter, "recap_importance", ()):
        if re.search(pat, text or "", re.I):
            score += int(w)
    if kind == "major":
        score += 4
    return score


def rank_entries(adapter, entries: list[dict], window: int = 12, top: int = 4) -> list[dict]:
    """The most important of the last ``window`` entries, biggest first (recency breaks ties), at most ``top``."""
    pool = list(enumerate(entries[-window:]))
    ranked = sorted(pool, key=lambda p: (-importance(adapter, str(p[1].get("text", "")), str(p[1].get("kind", ""))), -p[0]))
    return [e for _, e in ranked[:top]]


def referents(texts: list[str], names: Iterable[str], k: int = 8) -> list[str]:
    """Concrete things a nickname could be built from: the people named, then capitalised names and "the <thing>"
    phrases that appear in the texts. Cleaned, deduplicated, at most ``k``."""
    out: list[str] = []
    seen: set[str] = set()

    def add(x: str) -> None:
        x = gd.clean(x, "label").strip(" .,;:")
        key = x.casefold()
        if x and key not in seen and key not in _STOP and key not in _NOT_REFERENT:
            seen.add(key)
            out.append(x)

    blob = " ".join(texts)
    for n in names:
        if n and re.search(r"\b" + re.escape(str(n)) + r"\b", blob):
            add(str(n))
    for m in _AFTER_ARTICLE.finditer(blob):
        w = m.group(1).strip()
        if not all(p.casefold() in _STOP | _NOT_REFERENT for p in w.split()):
            add(w.split()[-1] if len(w.split()) > 2 else w)
    for m in _CAP_WORD.finditer(blob):
        w = m.group(0)
        if w.split()[0].casefold() not in _STOP:
            add(w)
    return out[:k]


def other_reactive_waiting(d, me: int | None) -> bool:
    """True when a waiting or running item of class REACTIVE or higher belongs to another actor."""
    ls = getattr(d, "leases", None)
    if ls is None:
        return False
    return (any(q.cls >= Cls.REACTIVE and q.actor_id != me for q in ls.queue)
            or any(l.cls >= Cls.REACTIVE and l.actor_id != me for l in ls.leases.values()))


def idle_should_yield(d) -> bool:
    """For idle jobs (art, lore): stop extra candidates when actors want windows."""
    idle = getattr(d, "idle", None)
    fn = getattr(idle, "actors_want_windows", None)
    return bool(fn()) if fn else False


PLAN_TOKENS = 48
PLAN_ASK = ("Make a private plan in one line: two concrete details from the data above you will use, and the feeling "
            "the piece carries. Plan only, in under 35 words.")

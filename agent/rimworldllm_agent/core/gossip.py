"""Gossip support (aliveness item 3): what a speaker could pass on, and what the group "says" about each actor.

Both are rule-based (no model call) and game-neutral. The chain bookkeeping itself lives in memory.record_told_line.
Secrets keep their own rules: only public memories are ever offered for passing on or counted in a reputation, and a
line that pairs romantic wording with a minor (speaker, listener or the subject) is never offered or kept.
"""
from __future__ import annotations

import re
from typing import Iterable

from . import agegate
from . import gamedata as gd
from .memory import keywords
from .minds import Memory, Mind

_TOLD = re.compile(r"^.{1,60}? told me:\s*")
HINT_PREFIX = "Something you could pass on: "
HINT_CHARS = 110                 # whole hint line, about 30 tokens
REP_CHARS = 70


def strip_teller(text: str) -> str:
    """"Bram told me: X" -> "X" (the retelling's own words are what travels)."""
    return _TOLD.sub("", text, count=1).strip()


def _clip(text: str, n: int) -> str:
    text = " ".join(text.split())
    if len(text) <= n:
        return text
    cut = text[:n].rsplit(" ", 1)[0].rstrip(",;:- ")
    return (cut or text[:n]) + "..."


def _heard_by(listener: Mind, lname: str, m: Memory) -> bool:
    """Has this listener already got this item (same event, in the chain, told to them, or near-identical words)?"""
    if m.event_seq is not None and any(x.event_seq == m.event_seq for x in listener.memories):
        return True
    if lname and (lname in m.chain or lname in m.told_to):
        return True
    words = keywords(strip_teller(m.text))
    for x in listener.memories:
        xw = keywords(strip_teller(x.text))
        if words and xw and len(words & xw) / len(words | xw) >= 0.6:
            return True
    return False


def pass_on_candidates(speaker: Mind, listener: Mind, listener_id: int, present: set[int],
                       minors: Iterable[int] = (), k: int = 2) -> list[Memory]:
    """At most ``k`` public memories of the speaker about absent third actors that the listener has not heard."""
    minors = set(minors)
    sid = speaker.actor_id
    out: list[tuple[float, int, Memory]] = []
    for m in speaker.memories:
        if m.visibility != "public" or m.summary_of or m.confidence < 0.3:
            continue
        third = {p for p in m.people if p not in (sid, listener_id, m.told_by_id)}
        if not third or third & present or listener_id in m.people:
            continue
        if agegate.romantic(m.text) and ({sid, listener_id} | third) & minors:
            continue
        if _heard_by(listener, listener.name, m):
            continue
        out.append((m.importance * m.confidence, m.game, m))
    out.sort(key=lambda t: (-t[0], -t[1], t[2].id))
    return [m for _, _, m in out[:k]]


def pass_on_hint(speaker: Mind, listener: Mind, listener_id: int, present: set[int],
                 minors: Iterable[int] = (), k: int = 2) -> str:
    """The block D line, or "" when there is nothing to pass on."""
    ms = pass_on_candidates(speaker, listener, listener_id, present, minors, k)
    if not ms:
        return ""
    each = (HINT_CHARS - len(HINT_PREFIX)) // len(ms) - 2
    return HINT_PREFIX + "; ".join(_clip(gd.clean(strip_teller(m.text), "event"), each) for m in ms)


def reputations(minds: dict[int, Mind], minors: Iterable[int] = (), floor: float = 0.1) -> dict[int, str]:
    """Per subject actor, the group's line about them, from heard/told public memories across all minds.

    The strongest item (importance x confidence), one per event, in the words of its last retelling; when several
    minds hold it the count is added. Rebuilt whole each time, so it follows the memories as they fade."""
    minors = set(minors)
    best: dict[int, tuple[float, str]] = {}
    holders: dict[int, dict[object, set[int]]] = {}
    for owner, mind in minds.items():
        for m in mind.memories:
            if m.source == "saw" or m.visibility != "public" or m.summary_of:
                continue
            w = m.importance * m.confidence
            if w < floor:
                continue
            text = gd.clean(strip_teller(m.text), "event")
            for subj in (p for p in m.people if p not in (owner, m.told_by_id)):
                if agegate.romantic(text) and ({subj, owner} & minors):
                    continue
                key = m.event_seq if m.event_seq is not None else text.lower()
                holders.setdefault(subj, {}).setdefault(key, set()).add(owner)
                if subj not in best or w > best[subj][0]:
                    best[subj] = (w, text)
    out = {}
    for subj, (_, text) in best.items():
        n = max(len(v) for v in holders[subj].values())
        out[subj] = _clip(text, REP_CHARS) + (f" ({n} people)" if n > 1 else "")
    return out

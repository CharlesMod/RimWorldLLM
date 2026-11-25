"""Witness-only memory: events become first-person memories only for witnesses.

Importance comes from game numbers (via the adapter), never from a model call. Selection scores recency + importance +
relevance (keyword and entity overlap) and takes the top 2-4.
"""
from __future__ import annotations

import re

from .minds import DEFAULT_DAY_UNITS, HALF_LIFE_DAYS, Memory, Mind

_WORD = re.compile(r"[a-z']{3,}")
_STOP = {"the", "and", "was", "with", "that", "his", "her", "for", "from", "into", "has", "had",
         "were", "are", "you", "your", "this", "then", "they", "them", "its", "who", "but", "not"}


def keywords(text: str) -> set[str]:
    return {w for w in _WORD.findall(text.lower()) if w not in _STOP}


def remember_event(minds: dict[int, Mind], event: dict, adapter, now_time: int | None = None) -> list[tuple[int, Memory]]:
    """Write a memory into each witness's mind (and only theirs). Returns what was written.

    Importance, pinning and the first-person wording come from the adapter (game numbers)."""
    written = []
    game = event.get("game", now_time or 0)
    people = [int(a["id"]) for a in event.get("actors", [])]
    for pid in event.get("witnesses", []):
        mind = minds.get(int(pid))
        if mind is None:
            continue
        if event.get("seq") is not None and any(m.event_seq == event["seq"] for m in mind.memories):
            continue                                 # idempotent per event
        imp = max(0.0, min(1.0, adapter.importance(event, int(pid))))
        mem = Memory(
            id=mind.next_mem_id(), game=game, text=adapter.first_person(event, int(pid)),
            source="saw", confidence=1.0, visibility="public", importance=imp,
            pinned=adapter.is_pinned(event, int(pid), mind.firsts), people=people,
            etype=event.get("type", ""), event_seq=event.get("seq"))
        mind.memories.append(mem)
        if mem.etype and mem.etype not in mind.firsts:
            mind.firsts.append(mem.etype)
        mind.unreflected_importance += imp
        written.append((int(pid), mem))
    return written


def remember_told(mind: Mind, teller_id: int, teller_name: str, text: str, game: int,
                  importance: float = 0.3, people: list[int] | None = None) -> Memory:
    """Gossip or confiding: lower confidence, source traceable to the teller."""
    mem = Memory(id=mind.next_mem_id(), game=game, text=text, source="told", told_by=teller_name,
                 told_by_id=teller_id, confidence=0.6, visibility="public", importance=importance,
                 people=people or [teller_id], etype="told")
    mind.memories.append(mem)
    return mem


def score_memory(mind: Mind, m: Memory, now_time: int, scene_people: set[int],
                 scene_words: set[str], day: int = DEFAULT_DAY_UNITS) -> float:
    age_days = max(0, now_time - m.game) / day
    recency = 1.0 if m.pinned else 0.5 ** (age_days / HALF_LIFE_DAYS)
    ent = len(scene_people & set(m.people)) / max(1, len(scene_people)) if scene_people else 0.0
    mw = keywords(m.text)
    kw = len(scene_words & mw) / max(1, min(len(scene_words), len(mw))) if scene_words and mw else 0.0
    relevance = min(1.0, ent + kw)
    return (0.30 * recency + 0.40 * m.importance * m.confidence + 0.30 * relevance)


def select_memories(mind: Mind, now_time: int, scene_people: set[int], scene_text: str, day: int = DEFAULT_DAY_UNITS,
                    k_min: int = 2, k_max: int = 4, floor: float = 0.25) -> list[Memory]:
    """Top 2-4: always k_min if available, up to k_max while the score clears the floor."""
    words = keywords(scene_text)
    scored = sorted(
        ((score_memory(mind, m, now_time, scene_people, words, day), m) for m in mind.memories),
        key=lambda sm: (-sm[0], -sm[1].game, sm[1].id))
    chosen = [m for i, (s, m) in enumerate(scored[:k_max]) if i < k_min or s >= floor]
    return sorted(chosen, key=lambda m: (m.game, m.id))   # stable order for the prompt


def merge_old_memories(mind: Mind, now_time: int, older_than_days: float = 8.0,
                       day: int = DEFAULT_DAY_UNITS) -> int:
    """Merge similar old ordinary memories (same event type) into one summary line, then drop them."""
    cutoff = now_time - older_than_days * day
    groups: dict[str, list[Memory]] = {}
    for m in mind.memories:
        if not m.pinned and m.game < cutoff and not m.summary_of and m.etype:
            groups.setdefault(m.etype, []).append(m)
    merged = 0
    for et, ms in groups.items():
        if len(ms) < 3:
            continue
        for m in ms:
            mind.memories.remove(m)
        top = max(ms, key=lambda m: m.importance)
        mind.memories.append(Memory(
            id=mind.next_mem_id(), game=max(m.game for m in ms),
            text=f"Over those days there were {len(ms)} {et.replace('_', ' ')} moments; the worst: {top.text}",
            importance=top.importance * 0.8, people=sorted({p for m in ms for p in m.people}),
            etype=et, summary_of=len(ms)))
        merged += len(ms)
    return merged

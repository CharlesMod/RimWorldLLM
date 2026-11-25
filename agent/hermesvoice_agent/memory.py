"""Witness-only memory (SPEC §5): events become first-person memories only for witnesses.

Importance comes from game numbers (severity, mood offset, opinion of the person involved,
whether someone died), never from a model call. Selection scores recency + importance +
relevance (keyword and entity overlap) and takes the top 2-4.
"""
from __future__ import annotations

import re

from .minds import HALF_LIFE_DAYS, TICKS_PER_DAY, Memory, Mind

_WORD = re.compile(r"[a-z']{3,}")
_STOP = {"the", "and", "was", "with", "that", "his", "her", "for", "from", "into", "has", "had",
         "were", "are", "you", "your", "this", "then", "they", "them", "its", "who", "but", "not"}


def keywords(text: str) -> set[str]:
    return {w for w in _WORD.findall(text.lower()) if w not in _STOP}


def event_pawn_ids(event: dict) -> list[int]:
    return [int(p["id"]) for p in event.get("pawns", [])]


def importance_of(event: dict, pawn_id: int, mind: Mind | None = None) -> float:
    """0..1 from game numbers only."""
    sev = float(event.get("severity", 0.0))
    data = event.get("data") or {}
    imp = sev
    if event.get("type") == "death" or data.get("died"):
        imp = 1.0
    if "mood_offset" in data:
        imp = max(imp, min(1.0, abs(float(data["mood_offset"])) / 20.0))
    if data.get("opinion") is not None:
        imp += min(0.3, abs(float(data["opinion"])) / 100.0 * 0.3)   # opinion of the person involved
    for p in event.get("pawns", []):
        if int(p["id"]) == pawn_id and p.get("role") in ("victim", "subject", "target"):
            imp += 0.15   # it happened to me
    return max(0.0, min(1.0, imp))


def is_pinned(event: dict, pawn_id: int, mind: Mind) -> bool:
    data = event.get("data") or {}
    if event.get("type") == "death" or data.get("died") or data.get("betrayal"):
        return True
    if float(event.get("severity", 0)) >= 0.9:
        return True
    return event.get("type", "") not in mind.firsts and event.get("type") in (
        "death", "mental_break", "raid_start", "quest", "funeral", "lodger_arrive")


def first_person(event: dict, pawn_id: int, names: dict[int, str]) -> str:
    summary = event.get("summary", "").strip()
    roles = {int(p["id"]): p.get("role", "") for p in event.get("pawns", [])}
    if pawn_id in roles and roles[pawn_id] in ("victim", "subject", "target"):
        return f"It happened to me: {summary}"
    if pawn_id in roles:
        return f"I was part of it: {summary}"
    return f"I saw it: {summary}"


def remember_event(minds: dict[int, Mind], event: dict, names: dict[int, str],
                   now_tick: int | None = None) -> list[tuple[int, Memory]]:
    """Write a memory into each witness's mind (and only theirs). Returns what was written."""
    written = []
    tick = event.get("tick", now_tick or 0)
    for pid in event.get("witnesses", []):
        mind = minds.get(int(pid))
        if mind is None:
            continue
        # idempotent per event
        if event.get("seq") is not None and any(m.event_seq == event["seq"] for m in mind.memories):
            continue
        imp = importance_of(event, int(pid), mind)
        mem = Memory(
            id=mind.next_mem_id(), tick=tick, text=first_person(event, int(pid), names),
            source="saw", confidence=1.0, visibility="public", importance=imp,
            pinned=is_pinned(event, int(pid), mind), people=event_pawn_ids(event),
            etype=event.get("type", ""), event_seq=event.get("seq"))
        mind.memories.append(mem)
        if mem.etype and mem.etype not in mind.firsts:
            mind.firsts.append(mem.etype)
        mind.unreflected_importance += imp
        written.append((int(pid), mem))
    return written


def remember_told(mind: Mind, teller_id: int, teller_name: str, text: str, tick: int,
                  importance: float = 0.3, people: list[int] | None = None) -> Memory:
    """Gossip or confiding: lower confidence, source traceable to the teller."""
    mem = Memory(id=mind.next_mem_id(), tick=tick, text=text, source="told", told_by=teller_name,
                 told_by_id=teller_id, confidence=0.6, visibility="public", importance=importance,
                 people=people or [teller_id], etype="told")
    mind.memories.append(mem)
    return mem


def score_memory(mind: Mind, m: Memory, now_tick: int, scene_people: set[int],
                 scene_words: set[str]) -> float:
    age_days = max(0, now_tick - m.tick) / TICKS_PER_DAY
    recency = 1.0 if m.pinned else 0.5 ** (age_days / HALF_LIFE_DAYS)
    ent = len(scene_people & set(m.people)) / max(1, len(scene_people)) if scene_people else 0.0
    mw = keywords(m.text)
    kw = len(scene_words & mw) / max(1, min(len(scene_words), len(mw))) if scene_words and mw else 0.0
    relevance = min(1.0, ent + kw)
    return (0.30 * recency + 0.40 * m.importance * m.confidence + 0.30 * relevance)


def select_memories(mind: Mind, now_tick: int, scene_people: set[int], scene_text: str,
                    k_min: int = 2, k_max: int = 4, floor: float = 0.25) -> list[Memory]:
    """Top 2-4: always k_min if available, up to k_max while the score clears the floor."""
    words = keywords(scene_text)
    scored = sorted(
        ((score_memory(mind, m, now_tick, scene_people, words), m) for m in mind.memories),
        key=lambda sm: (-sm[0], -sm[1].tick, sm[1].id))
    chosen = [m for i, (s, m) in enumerate(scored[:k_max]) if i < k_min or s >= floor]
    return sorted(chosen, key=lambda m: (m.tick, m.id))   # stable order for the prompt


def merge_old_memories(mind: Mind, now_tick: int, older_than_days: float = 8.0) -> int:
    """Merge similar old ordinary memories (same event type) into one summary line, then drop them."""
    cutoff = now_tick - older_than_days * TICKS_PER_DAY
    groups: dict[str, list[Memory]] = {}
    for m in mind.memories:
        if not m.pinned and m.tick < cutoff and not m.summary_of and m.etype:
            groups.setdefault(m.etype, []).append(m)
    merged = 0
    for et, ms in groups.items():
        if len(ms) < 3:
            continue
        for m in ms:
            mind.memories.remove(m)
        top = max(ms, key=lambda m: m.importance)
        mind.memories.append(Memory(
            id=mind.next_mem_id(), tick=max(m.tick for m in ms),
            text=f"Over those days there were {len(ms)} {et.replace('_', ' ')} moments; the worst: {top.text}",
            importance=top.importance * 0.8, people=sorted({p for m in ms for p in m.people}),
            etype=et, summary_of=len(ms)))
        merged += len(ms)
    return merged

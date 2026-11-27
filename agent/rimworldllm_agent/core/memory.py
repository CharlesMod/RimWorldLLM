"""Witness-only memory: events become first-person memories only for witnesses.

Importance comes from game numbers (via the adapter), never from a model call. Selection scores recency + importance +
relevance (keyword and entity overlap) and takes the top 2-4.
"""
from __future__ import annotations

import re

from . import agegate
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
            source="saw", confidence=1.0, importance=imp,
            visibility="private" if adapter.is_private(event, int(pid)) else "public",
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


def weight_sorted(mind: Mind, now_time: int, day: int = DEFAULT_DAY_UNITS) -> list[Memory]:
    """Memories, heaviest (decayed importance, pinned first) first."""
    return sorted(mind.memories, key=lambda m: (-mind.memory_weight(m, now_time, day), -m.game, m.id))


# ---- upkeep: merge, decay, pin ------------------------------------------------------------
def similarity(a: Memory, b: Memory) -> float:
    """Cheap: shared actors (Jaccard) weighted 0.4 plus word overlap (Jaccard) weighted 0.6."""
    pa, pb = set(a.people), set(b.people)
    pj = len(pa & pb) / len(pa | pb) if pa | pb else 0.0
    wa, wb = keywords(a.text), keywords(b.text)
    wj = len(wa & wb) / len(wa | wb) if wa | wb else 0.0
    return 0.4 * pj + 0.6 * wj


def merge_old_memories(mind: Mind, now_time: int, older_than_days: float = 8.0,
                       day: int = DEFAULT_DAY_UNITS, threshold: float = 0.5, min_group: int = 3) -> int:
    """Merge clusters of similar old ordinary memories into one summary line, then drop the originals.
    Pinned memories and anything private or told-to-some are left alone. Returns how many were folded."""
    cutoff = now_time - older_than_days * day
    pool = [m for m in mind.memories
            if not m.pinned and m.game < cutoff and not m.summary_of and m.visibility == "public"]
    pool.sort(key=lambda m: (m.game, m.id))
    clusters: list[list[Memory]] = []
    for m in pool:
        best = max(clusters, key=lambda c: min(similarity(m, x) for x in c), default=None)
        if best is not None and min(similarity(m, x) for x in best) >= threshold:
            best.append(m)
        else:
            clusters.append([m])
    merged = 0
    for ms in clusters:
        if len(ms) < min_group:
            continue
        for m in ms:
            mind.memories.remove(m)
        top = max(ms, key=lambda m: m.importance)
        et = next((m.etype for m in ms if m.etype), "")
        what = et.replace("_", " ") if et and all(m.etype == et for m in ms) else "similar"
        mind.memories.append(Memory(
            id=mind.next_mem_id(), game=max(m.game for m in ms),
            text=f"Over those days there were {len(ms)} {what} moments; the worst: {top.text}",
            importance=top.importance * 0.8, people=sorted({p for m in ms for p in m.people}),
            etype=et if what != "similar" else "summary", summary_of=len(ms)))
        merged += len(ms)
    return merged


def drop_decayed(mind: Mind, now_time: int, floor: float = 0.03, day: int = DEFAULT_DAY_UNITS) -> int:
    """Drop unpinned memories whose decayed weight fell under the floor."""
    dead = [m for m in mind.memories if not m.pinned and mind.memory_weight(m, now_time, day) < floor]
    for m in dead:
        mind.memories.remove(m)
    return len(dead)


def upkeep(mind: Mind, now_time: int, day: int = DEFAULT_DAY_UNITS, floor: float = 0.03,
           threshold: float = 0.5) -> dict:
    """Merge similar old memories, then drop the decayed ones. Deterministic, no model call."""
    merged = merge_old_memories(mind, now_time, day=day, threshold=threshold)
    dropped = drop_decayed(mind, now_time, floor, day)
    return {"merged": merged, "dropped": dropped}


# ---- gossip, confiding and secrets --------------------------------------------------------
GOSSIP_CONFIDENCE = 0.5
CONFIDE_CONFIDENCE = 0.7


def _source_memory(speaker: Mind, line: str, about: int | None, private: bool,
                   allow_told: bool = False) -> Memory | None:
    """The speaker's own memory a told line most likely draws on. With ``allow_told`` (gossip chains) a memory the
    speaker was itself told can be retold, but never a private one."""
    words = keywords(line)
    best, score = None, (-1.0, -1.0)
    for m in speaker.memories:
        if m.source == "told" and not private and not allow_told:
            continue
        if allow_told and not private and m.visibility == "private":
            continue
        if private and m.visibility != "private":
            continue
        if about is not None and not private and about not in m.people:
            continue
        sc = (len(words & keywords(m.text)) / max(1, len(words)), m.importance)
        if sc > score:
            best, score = m, sc
    return best


def record_told_line(minds: dict[int, Mind], speaker: int, listener: int, names: dict[int, str], line: str,
                     game: int, visibility: str, about: int | None = None, *, chain: bool = False,
                     hop_decay: float = 0.8, minor_ids: set[int] | None = None) -> Memory | None:
    """A gossip (``public``) or confide (``private``) line the game applied.

    The listener gets a ``told by speaker`` memory at reduced confidence. The speaker's source memory
    gets visibility bookkeeping: told_to grows; a confided private memory becomes ``told to [listener]``.

    With ``chain`` (gossip only): the memory carries the chain of tellers and the hop count, confidence decays by
    ``hop_decay`` per earlier retelling, the original event_seq is kept, and a listener who already holds the same
    event keeps the higher-confidence version (``sources`` counts the tellers) instead of piling up a second memory.
    A line that pairs romantic wording with a minor (speaker, listener or the subject) is never recorded."""
    lm = minds.get(listener)
    if lm is None:
        return None
    if minor_ids and {speaker, listener, about} & minor_ids and agegate.romantic(line):
        return None
    sname, lname = names.get(speaker, "someone"), names.get(listener, "someone")
    private = visibility == "private"
    sm = minds.get(speaker)
    use_chain = chain and not private
    src = _source_memory(sm, line, about, private, use_chain) if sm is not None else None
    conf = (CONFIDE_CONFIDENCE if private else GOSSIP_CONFIDENCE) * (src.confidence if src else 1.0)
    people = [speaker] + ([about] if about is not None else [])
    if src is not None:
        if lname not in src.told_to:
            src.told_to.append(lname)
        if private and src.visibility == "private":
            src.visibility = "told"
    chain_names: list[str] = []
    hops, seq = 0, None
    if use_chain:
        src_hops = (src.hops or (1 if src.source == "told" else 0)) if src else 0
        base = list(src.chain) if src and src.chain else ([src.told_by] if src and src.told_by else [])
        chain_names, hops = base + [sname], src_hops + 1
        conf *= hop_decay ** src_hops
        seq = src.event_seq if src else None
        if lname in chain_names:
            return None                            # it came from this listener in the first place
        old = next((m for m in lm.memories if seq is not None and m.event_seq == seq and m.source != "saw"), None)
        if old is not None:                        # (a witness still gets the teller's version beside their own)
            old.sources += 1
            if conf > old.confidence:
                old.text, old.confidence = f"{sname} told me: {line}", round(conf, 3)
                old.told_by, old.told_by_id, old.chain, old.hops = sname, speaker, chain_names, hops
                if src is not None:
                    old.importance = max(old.importance, max(0.3, 0.6 * src.importance))
            return old
    mem = remember_told(lm, speaker, sname, f"{sname} told me: {line}",
                        game, importance=max(0.3, 0.6 * src.importance) if src else 0.3, people=people)
    mem.confidence, mem.visibility = round(conf, 3), visibility
    if use_chain:
        mem.chain, mem.hops, mem.event_seq = chain_names, hops, seq
    return mem


def can_state(m: Memory, listener_name: str | None) -> bool:
    """May this memory be said outright to this listener?"""
    if m.visibility == "public":
        return True
    if m.visibility == "told":
        return listener_name is not None and listener_name in m.told_to
    return False                              # private: hint, never state


def secret_texts(mind: Mind, listener_name: str | None) -> list[str]:
    """Memory texts that must not be quoted to this listener (hinting is fine, quoting is not)."""
    return [m.text for m in mind.memories if not can_state(m, listener_name)]

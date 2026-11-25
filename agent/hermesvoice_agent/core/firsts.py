"""First-time reactions (item 8): the first time a actor witnesses a kind of thing, it may react once, out loud.

The adapter says which events are firsts (``first_of(event) -> (key, phrase) | None``); this module keeps the bookkeeping:
a first is recorded in ``Mind.firsts`` as ``"first:<key>"`` for every witness whether or not a line is ever spoken (memory
never depends on speech), and at most ``cfg.firsts_per_day`` first-time lines are queued per actor per game day. Later
occurrences take the normal path.
"""
from __future__ import annotations

from .minds import Mind

PREFIX = "first:"
MAX_SPEAKERS_PER_EVENT = 2          # an everyone-on-the-map cue (weather, an eclipse) is voiced by at most this many actors


def key_of(key: str) -> str:
    return PREFIX + key


def seen(mind: Mind, key: str) -> bool:
    return key_of(key) in mind.firsts


def marks(minds: dict[int, Mind], event: dict, adapter) -> dict[int, str]:
    """{actor id: phrase} for each witness in ``minds`` who has not met this kind of thing before; {} for a non-first event."""
    fn = getattr(adapter, "first_of", None)
    hit = fn(event) if fn else None
    if hit is None:
        return {}
    key, phrase = hit
    out = {}
    for w in event.get("witnesses", []):
        m = minds.get(int(w))
        if m is not None and not seen(m, key):
            out[int(w)] = phrase
    return out


def record(minds: dict[int, Mind], event: dict, adapter, fm: dict[int, str]) -> list[int]:
    """Write the firsts into the minds. Returns the ids changed."""
    hit = adapter.first_of(event)
    if hit is None:
        return []
    changed = []
    for aid in fm:
        m = minds.get(aid)
        if m is not None and not seen(m, hit[0]):
            m.firsts.append(key_of(hit[0]))
            changed.append(aid)
    return changed


def pick_speakers(fm: dict[int, str], seq: object, limit: int = MAX_SPEAKERS_PER_EVENT) -> list[int]:
    """Which marked actors voice a cue: a stable shuffle by event and actor, so the same actors are not always first."""
    import hashlib
    return sorted(fm, key=lambda a: hashlib.sha1(f"{seq}:{a}".encode()).hexdigest())[:limit]


class DayCap:
    """First-time lines per actor per game day."""

    def __init__(self) -> None:
        self.n: dict[tuple[int, int], int] = {}

    def allow(self, aid: int, day: int, cap: int) -> bool:
        return self.n.get((aid, day), 0) < cap

    def spend(self, aid: int, day: int) -> None:
        self.n[(aid, day)] = self.n.get((aid, day), 0) + 1
        for k in [k for k in self.n if k[1] < day - 1]:
            del self.n[k]

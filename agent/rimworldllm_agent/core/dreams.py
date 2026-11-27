"""Dreams (aliveness item 6): a sleeping actor gets one surreal thought cloud per night.

The mod publishes "sleep_start" (the sleeper its only witness) when an actor falls asleep in a bed. The director hands
it to ``DreamBook.note``; the idle runner takes due jobs (``take``) and ``run_dream`` writes the fragment with one short
LLM call, then sends it as POST /thought kind "dream" with a surface line and adds it to the inner stream.

A dream is dropped when the actor is no longer asleep (snapshot ``asleep`` is false) or when too much game time has passed.
The sleep event writes no memory (the director returns before the memory step).
"""
from __future__ import annotations

import hashlib
import logging
from dataclasses import dataclass
from typing import TYPE_CHECKING

from . import agegate, inner, memory, speech
from . import gamedata as gd
from .bridge import BridgeError
from .llm import LLMError
from .prompt import BIO_LINE_CAP
from .sanitize import prompt_cap_words, sanitize_prose

if TYPE_CHECKING:
    from .director import Director

log = logging.getLogger("rimworldllm")

SLEEP_EVENT = "sleep_start"
DREAM_KIND = "dream"
DREAM_MIN_WORDS = 3
DREAM_MAX_WORDS = 16
DREAM_MEMORIES = 3
DREAM_THOUGHTS = 2


@dataclass
class DreamJob:
    actor: int
    game: int               # when the actor fell asleep
    night: int


def enabled(d: "Director") -> bool:
    return bool(d.cfg.dreams) and d.bridge_cfg.get("dreams") is not False and d.bridge_cfg.get("events", {}).get("sleep") is not False


def night_of(game: int, day: int) -> int:
    """Nights run 18:00 to 18:00, so a sleep that crosses midnight is one night."""
    return (game + day // 4) // day


class DreamBook:
    def __init__(self) -> None:
        self.pending: dict[int, DreamJob] = {}
        self.dreamt: dict[int, dict[int, int]] = {}          # actor -> night -> dreams already made

    def note(self, e: dict, d: "Director", now_game: int) -> list[int]:
        """Queue a dream for the sleeper of a sleep_start event. Returns the actors queued."""
        if e.get("type") != SLEEP_EVENT or not enabled(d):
            return []
        game = int(e.get("game", now_game))
        night = night_of(game, d.day)
        out = []
        for a in e.get("actors", []):
            aid = int(a["id"])
            if aid not in d.minds or aid in self.pending:
                continue
            made = self.dreamt.setdefault(aid, {})
            if made.get(night, 0) >= d.cfg.dreams_per_night:
                continue
            made[night] = made.get(night, 0) + 1              # the night's one roll is spent whether or not it dreams
            for old in [n for n in made if n < night - 2]:
                del made[old]
            roll = int(hashlib.sha1(f"dream:{aid}:{night}".encode()).hexdigest(), 16) % 1000 / 1000
            if roll >= d.cfg.dream_chance:
                continue
            self.pending[aid] = DreamJob(aid, game, night)
            out.append(aid)
        return out

    def stale(self, job: DreamJob, now_game: int, d: "Director") -> bool:
        return now_game - job.game > d.cfg.dream_max_age_game or now_game < job.game

    def due(self, d: "Director") -> list[DreamJob]:
        """Jobs worth starting, oldest first; stale ones are dropped here."""
        now = d.bridge.env.game
        out = []
        for aid, job in sorted(self.pending.items(), key=lambda kv: (kv[1].game, kv[0])):
            if self.stale(job, now, d):
                del self.pending[aid]
                d.dropped.append((aid, "dream:late"))
            elif dream_memories(d, aid):
                out.append(job)
            else:
                del self.pending[aid]
                d.dropped.append((aid, "dream:no_memories"))
        return out


def dream_memories(d: "Director", aid: int) -> list:
    """Today's (and yesterday evening's) public memories, heaviest first: a private one never reaches the map."""
    mind = d.minds[aid]
    now = d.bridge.env.game
    floor = now - d.day - d.day // 4
    return [m for m in memory.weight_sorted(mind, now, d.day) if m.game >= floor and m.visibility == "public"][:DREAM_MEMORIES]


def _asleep(snap: dict) -> bool:
    return snap.get("asleep", True) is not False             # an older mod without the field: the delay is the guard


async def run_dream(d: "Director", job: DreamJob, lease) -> str | None:
    """Write and send one dream. Returns the text, or None when nothing was sent (the reason goes to d.dropped)."""
    aid = job.actor
    mind = d.minds[aid]
    d.dream_book.pending.pop(aid, None)
    try:
        snap = await d.bridge.actor(aid)
    except BridgeError:
        return None
    if not _asleep(snap):
        d.dropped.append((aid, "dream:woke"))
        return None
    mems = dream_memories(d, aid)
    if not mems:
        return None
    stream = inner.recent_inner(mind, DREAM_THOUGHTS) if d.cfg.inner_monologue else []
    minors = d.minor_tokens()
    involves_minor = d.is_minor(aid) or any(d.is_minor(p) for m in mems for p in m.people)
    mem_lines = "\n".join(f"- {gd.clean(m.text, 'event')}" for m in mems)
    words = await speech.register_phrase(d, aid, mind, snap)
    user = (f"=== WHO YOU ARE ===\n{gd.data_section('bio', gd.clean(mind.fixed.text, 'line', max_len=BIO_LINE_CAP, multiline=True))}"
            f"=== WHAT HAPPENED TODAY ===\n{gd.data_section('memories', mem_lines)}"
            + (f"=== WHAT YOU WERE THINKING ===\n{gd.data_section('thoughts', inner.thoughts_text(stream))}" if stream else "") +
            f"=== REQUEST ===\n{speech.register_line(mind.name, words)}{d.adapter.dream_request}")
    system = d.adapter.dream_system + "\n\n" + gd.DATA_RULE + (("\n\n" + agegate.MINOR_RULE) if involves_minor else "")
    lease.generating = True
    try:
        c = await d.llm.complete([("system", system), ("user", user)], slot=lease.slot, kind=DREAM_KIND,
                                 max_tokens=d.cfg.dream_max_tokens, stop=["\n"], temperature=1.0, actor=aid)
    except LLMError:
        lease.generating = False
        return None
    lease.generating = False
    if lease.preempted:
        return None
    toks = d.name_tokens
    allnames = set().union(*toks.values()) if toks else set()
    known = set(toks.get(aid, set())).union(*(toks.get(p, set()) for m in mems for p in m.people))
    s = sanitize_prose(c.text, known_names=known, all_names=allnames, prompt_words=prompt_cap_words(user),
                       common_words=set(d.adapter.common_words), speaker_names=toks.get(aid, set()),
                       min_words=DREAM_MIN_WORDS, max_words=DREAM_MAX_WORDS, involves_minor=involves_minor,
                       minor_names=minors)
    text = s.text if s.ok else None
    if text is not None and (len(text) > d.cfg.thought_max_chars
                             or agegate.violation(text, involves_minor=involves_minor, minor_names=minors)):
        text = None
    if text is None:
        d.dropped.append((aid, f"dream:{s.reason if not s.ok else 'rejected'}"))
        return None
    try:                                                     # the actor may have woken while the model wrote
        if not _asleep(await d.bridge.actor(aid)):
            d.dropped.append((aid, "dream:woke"))
            return None
    except BridgeError:
        return None
    now = d.bridge.env.game
    if d.cfg.inner_monologue:
        inner.add_inner(mind, text, DREAM_KIND, now, d.cfg.inner_stream_keep)
        d.dirty.add(aid)
    d.idle.log.append({"kind": "dream", "actor": aid, "name": d.names.get(aid), "game": now, "text": text})
    try:
        await d.bridge.thought(aid, text, kind=DREAM_KIND, surface=text)
    except BridgeError as e:
        log.warning("dream refused for %s: %s", aid, e)
    return text

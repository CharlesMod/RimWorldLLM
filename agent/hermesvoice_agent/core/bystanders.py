"""Bystander reactions (item 9): when two actors argue in front of others, one witness shows a tiny reaction cloud.

One bystander per argument (the witness with the strongest feelings about the pair), a per-actor gap, a very small LLM call
(kind "reaction", about 12 tokens) run in a free window as an AMBIENT-class item so it waits behind real talk, or a template
line when every window is busy. The cloud also joins the bystander's inner stream (kind "reaction").
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass, field

from . import agegate
from . import gamedata as gd
from .minds import Mind
from .sanitize import prompt_cap_words, sanitize_prose

REACTION_MAX_TOKENS = 12
MIN_WORDS, MAX_WORDS = 2, 6


@dataclass
class Bystanders:
    """Per-actor rate limit (play clock) and counters, for the debug view and tests."""
    last: dict[int, float] = field(default_factory=dict)
    asked: int = 0
    shown: int = 0
    templated: int = 0
    dropped: int = 0
    skipped: int = 0
    log: list[dict] = field(default_factory=list)

    def ready(self, aid: int, play_now: float, gap: float) -> bool:
        t = self.last.get(aid)
        return t is None or play_now - t >= gap

    def mark(self, aid: int, play_now: float) -> None:
        self.last[aid] = play_now


def pick(witnesses: list[int], parties: list[int], minds: dict[int, Mind], bs: Bystanders, play_now: float,
         gap: float) -> int | None:
    """The one bystander: a witness we have a mind for, not a party, past its gap, and the one with the strongest opinion of
    either party (ties: the lowest id, so it is stable)."""
    best, score = None, -1
    for w in sorted(set(int(x) for x in witnesses)):
        if w in parties or w not in minds or not bs.ready(w, play_now, gap):
            continue
        rels = minds[w].relations
        s = max((abs(rels[str(p)].opinion or 0) for p in parties if str(p) in rels), default=0)
        if s > score:
            best, score = w, s
    return best


def template(templates: tuple[str, ...], aid: int, key: object) -> str:
    h = int(hashlib.sha1(f"{aid}:{key}".encode()).hexdigest(), 16)
    return templates[h % len(templates)]


def messages(adapter, mind: Mind, names: dict[int, str], parties: list[int], summary: str, minor: bool):
    """The whole prompt, kept small: who you are in one line, what you saw, what you feel about the two."""
    reg = gd.clean(mind.voice.register_line or "plain and dry", "desc")
    seen = []
    for p in parties:
        r = mind.relations.get(str(p))
        op = f" (you feel {r.opinion:+d} about them)" if r and r.opinion is not None else ""
        seen.append(f"{gd.name(names.get(p, ''))}{op}")
    body = f"{' and '.join(seen)} are quarrelling in front of you. {gd.clean(summary, 'event')}".strip()
    user = (f"=== WHO YOU ARE ===\n{gd.data_section('voice', f'You talk like this: {reg}')}"
            f"=== WHAT YOU SEE ===\n{gd.data_section('scene', body)}"
            f"=== REQUEST ===\n{adapter.reaction_request}")
    system = adapter.reaction_system + "\n\n" + gd.DATA_RULE + (("\n\n" + agegate.MINOR_RULE) if minor else "")
    return [("system", system), ("user", user)]


def clean_reaction(raw: str, *, prompt: str, aid: int, name_tokens: dict[int, set[str]], common_words: set[str] | None,
                   minor: bool, minor_names: set[str] | None, max_chars: int = 90) -> str | None:
    """A 2-6 word reaction naming nobody, or None. A name in it, a romantic word with a minor around, or a long line drops it."""
    all_names = set().union(*name_tokens.values()) if name_tokens else set()
    s = sanitize_prose(raw, known_names=set(), all_names=all_names, prompt_words=prompt_cap_words(prompt),
                       common_words=common_words, speaker_names=name_tokens.get(aid, set()),
                       min_words=MIN_WORDS, max_words=MAX_WORDS, involves_minor=minor, minor_names=minor_names)
    if not s.ok or not s.text or len(s.text) > max_chars:
        return None
    if agegate.violation(s.text, involves_minor=minor, minor_names=minor_names or ()):
        return None
    return s.text

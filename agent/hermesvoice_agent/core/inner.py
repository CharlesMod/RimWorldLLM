"""The inner monologue: each actor's private stream (Mind.inner), made of aims (decided in the intent pass,
after a spoken line, so an aim guides the NEXT line) and musings (the idle map thought).

Later items (concerns, dreams, reflection, diaries) read the stream through ``add_inner`` / ``recent_inner``.
"""
from __future__ import annotations

from . import agegate
from . import gamedata as gd
from .prompt import AIM_RULE  # noqa: F401  (re-exported)
from .minds import INNER_KEEP, InnerThought, Mind
from .sanitize import Sanitized, leaks_secret, sanitize_prose

AIM_MIN_WORDS = 3
AIM_MAX_WORDS = 16
# GBNF: a first-person phrase, about 12 words, 8-100 characters of plain text.
AIM_EXTRA_TOKENS = 30                   # decode room the aim needs beyond the label and delivery (8)


def add_inner(mind: Mind, text: str, kind: str, game: int, keep: int = INNER_KEEP) -> InnerThought:
    """Append one thought to the stream and cap it at ``keep``. The caller marks the mind dirty."""
    t = InnerThought(game=game, text=text, kind=kind)
    mind.inner.append(t)
    if keep > 0:
        mind.inner = mind.inner[-keep:]
    return t


def recent_inner(mind: Mind, n: int = 3, kinds: set[str] | None = None) -> list[InnerThought]:
    """The newest ``n`` entries, oldest first. ``kinds`` filters (None = every kind). Only this actor's own."""
    es = [t for t in mind.inner if kinds is None or t.kind in kinds]
    return es[-n:] if n > 0 else []


def inner_on_day(mind: Mind, day_no: int, day_units: int) -> list[InnerThought]:
    return [t for t in mind.inner if t.game // day_units == day_no]


def repeats_recent_aim(mind: Mind, text: str, n: int = 4) -> bool:
    """True when ``text`` repeats or closely rephrases one of the actor's last ``n`` aims."""
    from .loopguard import find_repeat
    prev = [(0, t.text) for t in recent_inner(mind, n, {"aim"})]
    return bool(prev) and find_repeat(text, 0, prev) is not None


def thoughts_text(entries: list[InnerThought]) -> str:
    """Game text of a stream slice, cleaned; the caller wraps it with data_section."""
    return "\n".join(f"- {gd.clean(t.text, 'line')}" for t in entries)


def fresh_aim(mind: Mind, game: int, window: int) -> str | None:
    """The speaker's newest aim if it was made within ``window`` game units."""
    for t in reversed(mind.inner):
        if t.kind == "aim":
            return t.text if 0 <= game - t.game <= window else None
    return None


def aim_line(aim: str) -> str:
    return f"Your private aim (never say it outright): {gd.clean(aim, 'line')}\n"


def sanitize_aim(raw: str, ctx) -> tuple[str | None, bool]:
    """(aim or None, touched_by_age_gate). A dropped aim never drops the line."""
    if not raw or not raw.strip():
        return None, False
    s: Sanitized = sanitize_prose(raw, known_names=ctx.known_names, all_names=ctx.all_names,
                                  prompt_words=ctx.prompt_words, common_words=ctx.common_words,
                                  speaker_names=ctx.speaker_names, min_words=AIM_MIN_WORDS, max_words=AIM_MAX_WORDS,
                                  involves_minor=ctx.minor_involved, minor_names=ctx.minor_names)
    if not s.ok:
        return None, s.reason == "minor_safety"
    if agegate.violation(s.text, involves_minor=ctx.minor_involved, minor_names=ctx.minor_names):
        return None, True
    if ctx.secrets and leaks_secret(s.text, ctx.secrets):
        return None, False
    return s.text, False


class CloudGate:
    """Contrast cloud rate limit: at most one per actor per ``gap`` play-clock seconds, and a chance."""

    def __init__(self) -> None:
        self.last: dict[int, float] = {}

    def allow(self, aid: int, play_now: float, gap: float, chance: float, roll: float) -> bool:
        last = self.last.get(aid)
        if last is not None and play_now - last < gap:
            return False
        if roll >= chance:
            return False
        self.last[aid] = play_now
        return True

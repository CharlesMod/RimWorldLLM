"""Slowly evolving habits (item 7): the voice card's tics change rarely, at reflection, from what happened.

A reflection may carry one new small verbal habit. It replaces the oldest tic (or is appended while the card has fewer
than three), at most once per ``cfg.habit_every_days`` game days, never on a voice card the player edited. Every change is
kept (``Mind.habit_history``) so ``undo_habit`` can put the old tic back. The block G rule (a habit in at most one line in
three) is unchanged: this module only changes which habits the card holds.
"""
from __future__ import annotations

from . import gamedata as gd
from .minds import HABIT_KEEP, HabitChange, Mind
from .sanitize import prompt_cap_words, sanitize_prose

MAX_TICS = 3
HABIT_MIN_WORDS = 2
HABIT_MAX_WORDS = 10


def habit_allowed(mind: Mind, game: int, day: int, every_days: float) -> bool:
    """Not on a player-edited card, and not again before ``every_days`` game days have passed."""
    if mind.voice.edited_by_player:
        return False
    return mind.last_habit_game < 0 or game - mind.last_habit_game >= every_days * day


def habits_line(mind: Mind) -> str:
    """The current tics for the reflection prompt (game text, cleaned; the caller wraps it with data_section)."""
    return "; ".join(gd.clean(t, "label") for t in mind.voice.tics) or "none yet"


def clean_habit(raw: str, *, prompt: str, name_tokens: dict[int, set[str]], actor_id: int,
                common_words: set[str] | None, involves_minor: bool, minor_names: set[str] | None) -> str:
    """The habit text, or "" when it is empty, ungrounded (invented names), romantic about a minor, or the wrong size."""
    if not raw or not raw.strip():
        return ""
    allnames = set().union(*name_tokens.values()) if name_tokens else set()
    s = sanitize_prose(raw.strip().strip('"'), known_names=set(), all_names=allnames, prompt_words=prompt_cap_words(prompt),
                       common_words=common_words, speaker_names=name_tokens.get(actor_id, set()),
                       min_words=HABIT_MIN_WORDS, max_words=HABIT_MAX_WORDS,
                       involves_minor=involves_minor, minor_names=minor_names)
    return s.text.rstrip(".") if s.ok and s.text else ""


def apply_habit(mind: Mind, habit: str, game: int, day: int, every_days: float) -> bool:
    """Replace the oldest tic with ``habit`` (append while fewer than three). False when not allowed or a repeat."""
    habit = " ".join(habit.split())
    if not habit or not habit_allowed(mind, game, day, every_days):
        return False
    tics = mind.voice.tics
    if any(habit.lower() == t.lower() for t in tics):
        return False
    removed = ""
    if len(tics) >= MAX_TICS:
        removed = tics.pop(0)
    tics.append(habit)
    mind.last_habit_game = game
    mind.habit_history.append(HabitChange(game=game, added=habit, removed=removed))
    mind.habit_history = mind.habit_history[-HABIT_KEEP:]
    return True


def undo_habit(mind: Mind) -> bool:
    """Undo the newest habit change: the added tic goes, the one it replaced comes back at the front."""
    if not mind.habit_history:
        return False
    ch = mind.habit_history.pop()
    tics = mind.voice.tics
    if ch.added in tics:
        tics.remove(ch.added)
    if ch.removed:
        tics.insert(0, ch.removed)
    mind.last_habit_game = mind.habit_history[-1].game if mind.habit_history else -1
    return True

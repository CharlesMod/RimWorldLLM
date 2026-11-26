"""The speaker's register (how simple or articulate the words are), asked of the game adapter. Core imports nothing from games/."""
from __future__ import annotations

from typing import TYPE_CHECKING

from . import gamedata as gd
from .bridge import BridgeError

if TYPE_CHECKING:
    from .director import Director
    from .minds import Mind


async def register_phrase(d: "Director", aid: int, mind: "Mind", snap: dict | None = None) -> str | None:
    """The effective register phrase for one actor now: the adapter reads a fresh snapshot (base plus any temporary state);
    with no snapshot to read, the base phrase saved in the fixed bio. None when the game has no register."""
    fn = getattr(d.adapter, "speech_register", None)
    if fn is None:
        return None
    if snap is None:
        try:
            snap = await d.bridge.actor(aid)
        except BridgeError:
            snap = None
    if snap is not None:
        return fn(snap)
    return (mind.fixed.extras or {}).get("words") or None


def register_line(name: str, phrase: str | None) -> str:
    """One line for the inline prompts (thought, diary, dream, bystander); empty when there is no phrase."""
    return f"Write it the way {gd.name(name)} would: {gd.clean(phrase, 'desc')}.\n" if phrase else ""

"""Which RimWorld events are a documentary moment, and how heavy (core/commentary.py asks the adapter).

Weights (0..1; below 0.4 nothing is said): colony_lost 1.0, death 0.95, raid_start 0.9, ship_part 0.8, birth 0.75,
legendary masterwork 0.7, wedding 0.7, a joiner or accepted quest 0.5, funeral 0.5, a social fight 0.5, any other mental
break 0.6, masterwork 0.5, party 0.4. Urgent (skip the game-hour gap, keep the 30 s floor): colony_lost, death, raid_start, birth.
"""
from __future__ import annotations

import re

from ...core.commentary import Moment

_JOIN = re.compile(r"\b(joins?|joined|wanderer|asks? to join|quest (?:accepted|begun|started)|accepted the quest|"
                   r"refugee|new colonist)\b", re.I)
_QUEST = re.compile(r"\bquest\b.*\b(accepted|begun|started)\b|\baccepted\b.*\bquest\b", re.I)


def _who(e: dict) -> str:
    return ",".join(str(a.get("id")) for a in (e.get("actors") or [])[:3])


def moment(e: dict, d=None) -> Moment | None:
    t = e.get("type", "")
    data = e.get("data") or {}
    game = int(e.get("game", 0) or 0)
    text = str(e.get("summary") or t).strip()
    minor = bool(d is not None and any(d.is_minor(int(a["id"])) for a in e.get("actors") or [] if "id" in a))

    def mk(kind, weight, urgent=False, key=None, text_=None):
        return Moment(kind, key or f"{kind}:{_who(e)}", text_ or text, weight, urgent, game, minor)

    if t == "colony_lost":
        return mk("colony_lost", 1.0, True, "colony_lost", text or "The last colonist is gone.")
    if t == "death":
        return mk("death", 0.95, True)
    if t == "raid_start":
        return mk("raid_start", 0.9, True, "raid_start")           # one comment per raid wave window
    if t == "ship_part":
        return mk("ship_part", 0.8, False, f"ship_part:{data.get('thing', '')}", f"A ship part was finished: {data.get('label') or text}")
    if t == "birth":
        p = data.get("pawn") or {}
        return mk("birth", 0.75, True, f"birth:{p.get('id', _who(e))}")
    if t == "gathering":
        k = str(data.get("kind", "")).lower()
        if k == "wedding":
            return mk("wedding", 0.7, False, f"wedding:{_who(e)}")
        if k == "party":
            return mk("party", 0.4, False, "party")
        return None
    if t == "funeral":
        return mk("funeral", 0.5)
    if t == "mental_break":
        fight = str(data.get("state", "")) == "SocialFighting"
        return mk("social_fight" if fight else "mental_break", 0.5 if fight else 0.6, False)
    if t == "masterwork":
        legendary = str(data.get("quality", "")) == "legendary"
        label = data.get("label") or data.get("thing") or "a work"
        maker = (data.get("maker") or {}).get("name", "")
        words = f"{maker} made a {'legendary' if legendary else 'masterwork'} {label}".strip()
        return mk("masterwork", 0.7 if legendary else 0.5, False, f"masterwork:{data.get('thing', '')}", words)
    if t in ("letter", "dialog", "quest"):
        blob = " ".join(str(x) for x in (e.get("summary"), data.get("title"), data.get("label"), data.get("kind")) if x)
        if _QUEST.search(blob) or t == "quest":
            return mk("quest", 0.5, False, "quest:" + blob[:40])
        if _JOIN.search(blob):
            return mk("joiner", 0.5, False, "joiner:" + blob[:40])
    return None

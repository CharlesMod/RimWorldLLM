"""The steward's combat tools (Vivarium phase 3): ``squad`` and ``defend``, and the combat view's text.

``squad`` orders the fighters (POST /act/squad), ``defend`` plans defences (POST /act/defend). The model names colonists
and threats in words ("Ana", "the pirates", "group 9012"); the ids come from the combat view (GET /combat, also the
digest's ``combat`` block). Positions are the view's own words (``cover``, ``chokepoint``, ``door``...): the mod's
CoverFinder picks the cells, so the model never gives coordinates.

``squad`` exists only while a combat view is present (and in the "combat over" cycle, for undraft and rescue); ``defend`` also when the digest has a build block.
Optional parameters follow the builder tools: an optional enum carries ``none``, an optional number 0 (the grammar
protocol writes every parameter, so unused ones are tolerated, never an error).

``render_combat`` writes the view as text (about 1.5 KB at most): threats, fighters, others, downed, fires, defences, squad.
"""
from __future__ import annotations

import re

from ...core import gamedata as gd
from ...core.toolloop import Param, ToolSpec

NONE = "none"
WHY_MAX = 80
_WHY = Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since the wounded must live); "
             "it is shown to the player beside what you did", max_len=WHY_MAX, required=False, same_call=True)   # as steward_tools._WHY (a copy: importing it would be circular)

TOOLS = ("squad", "defend")
OPS = ["draft", "move", "hold", "fire_at_will", "focus", "retreat", "rescue", "extinguish", "flee", "undraft"]
KINDS = ["turret", "sandbags", "barricade", "killbox", "traps", "wall"]
SIDES = ["threat", "n", "e", "s", "w"]
AT_DEFEND = ["chokepoint", "entrance"]
POSITIONS = ["cover", "chokepoint", "door", "wall_n", "wall_e", "wall_s", "wall_w", "home", "inside"]   # when a view lists none
COUNT_MAX = {"turret": 4, "traps": 8}
LENGTH_KINDS = ("sandbags", "barricade", "wall")
_UNUSED = {"", NONE, "n/a", "0"}
_SQUAD_WORDS = {"squad", "all", "everyone", "everybody", "fighters", "the squad"}
_FILLER = {"the", "a", "an", "those", "these", "them", "group", "groups", "threat", "threats", "enemy", "enemies", "of", "all", "pawn", "id", "attackers"}


def _w(x: object, n: int = 40) -> str:
    return gd.clean(str(x if x is not None else ""), "label", max_len=n).strip()


def _int(x: object) -> int | None:
    try:
        return int(x)           # type: ignore[arg-type]
    except (TypeError, ValueError):
        return None


def _stem(word: str) -> str:
    w = word.lower()
    if w.endswith("ies") and len(w) > 4:
        return w[:-3] + "y"
    if w.endswith("s") and not w.endswith("ss") and len(w) > 3:
        return w[:-1]
    return w


def _tokens(text: str) -> set[str]:
    return {_stem(t) for t in re.findall(r"[a-z]+", text.lower())}


def view_active(view: object) -> bool:
    return isinstance(view, dict) and view.get("active") is not False and bool(view)


class CombatKit:
    """The combat tools for one combat view (and the digest's build block, for ``defend``) and the body of each call."""

    def __init__(self, view: dict | None, pawns: dict[str, int] | None = None, build=None, after_fight: bool = False):
        self.view = view if view_active(view) else None
        self.after_fight = after_fight and self.view is None      # the "combat over" cycle: squad stays for undraft and rescue
        self.pawns = dict(pawns or {})
        self.build = build if getattr(build, "has", False) else None
        v = self.view or {}
        self.groups: list[dict] = []
        for g in v.get("threats") or []:
            gid = _int(g.get("id")) if isinstance(g, dict) else None
            if gid:
                self.groups.append({"id": gid, "ids": [i for i in (_int(x) for x in g.get("ids") or []) if i],
                                    "text": " ".join([_w(g.get("faction")), _w(g.get("kind"))] + [_w(x) for x in g.get("labels") or []])})
        pos = [p for p in (_w(x, 20) for x in v.get("positions") or []) if p]
        self.positions = pos or list(POSITIONS)
        self.rooms = dict(build.rooms) if self.build else {}
        self.stuff = list(build.stuff) if self.build else []
        self.tools = self._tools()
        self.names = {t.name for t in self.tools}

    # ---- the tools ------------------------------------------------------------------
    def _tools(self) -> list[ToolSpec]:
        out: list[ToolSpec] = []
        if self.view or self.after_fight:
            ps = [Param("op", "enum", "what to do: draft the fighters, move or retreat them to a position, hold (stand and fire, no chasing) or fire_at_will, "
                        "focus a threat, rescue the downed, extinguish fires, flee (non-fighters indoors), undraft", choices=OPS),
                  Param("pawns", "text", "names separated by commas, or squad (the current squad; for draft: every fighter)", max_len=160, required=False),
                  Param("at", "enum", "move/retreat: where to stand", choices=[NONE] + self.positions, required=False)]
            if self.rooms:
                roles = ", ".join(f"{i} {r}" if r else i for i, r in self.rooms.items())
                ps.append(Param("room", "enum", f"at door or inside: which room (room id: {roles})", choices=[NONE] + list(self.rooms), required=False))
            ps += [Param("target", "text", "focus: the threat, as its group number from THREATS or words like the pirates", max_len=60, required=False),
                   Param("below", "int", "retreat: wounded fall back under this health percent (10 to 90), 0 if unused", lo=0, hi=90, required=False), _WHY]
            out.append(ToolSpec("squad", "Order the fighters in a fight: draft, move to cover or a door, hold, focus a threat, retreat the wounded, "
                                "rescue the downed, put out fires, send non-fighters indoors, undraft.", ps))
        if self.view or self.build or self.after_fight:
            ds = [Param("kind", "enum", "killbox is a kill zone at the main entrance", choices=KINDS),
                  Param("side", "enum", "threat (the enemy side) or n/e/s/w", choices=[NONE] + SIDES, required=False),
                  Param("at", "enum", "instead of side", choices=[NONE] + AT_DEFEND, required=False),
                  Param("count", "int", "turret 1-4, traps 1-8, 0 unused", lo=0, hi=8, required=False),
                  Param("length", "int", "sandbags, barricade, wall 1-20, 0 unused", lo=0, hi=20, required=False)]
            if self.stuff:
                ds.append(Param("stuff", "enum", "material", choices=[NONE] + self.stuff, required=False))
            out.append(ToolSpec("defend", "Plan defences (blueprints); the planner picks the cells.", ds + [_WHY]))
        return out

    # ---- helpers --------------------------------------------------------------------
    @staticmethod
    def _get(v: dict, k: str):
        x = v.get(k)
        return None if (x is None or str(x).strip().lower() in _UNUSED) else x

    def _pawn_ids(self, raw: str | None) -> list[int] | None:
        """None = the squad (or, for draft, every fighter); else the named colonists' ids."""
        if raw is None or raw.strip().lower() in _UNUSED | _SQUAD_WORDS:
            return None
        ids: list[int] = []
        for part in (x.strip() for x in raw.split(",") if x.strip()):
            hit = next((n for n in self.pawns if n.lower() == part.lower()), None)
            if hit is None:
                raise ValueError(f"no colonist named {part}; the colonists are: " + ", ".join(self.pawns))
            if self.pawns[hit] not in ids:
                ids.append(self.pawns[hit])
        return ids[:30] or None

    def target(self, text: str) -> int:
        """A threat group id (or one of its pawn ids) from the model's words."""
        if not self.groups:
            raise ValueError("no threats in the view to focus")
        t = text.strip().lower()
        n = _int(re.sub(r"^(?:the\s+)?(?:group|threat|pawn|id)?\s*#?\s*", "", t))
        if n is not None:
            if any(g["id"] == n or n in g["ids"] for g in self.groups):
                return n
            raise ValueError(f"no threat numbered {n}; the threats are: " + ", ".join(f"#{g['id']} {g['text']}" for g in self.groups))
        if t in ("nearest", "closest", "the nearest", "the closest", "first"):
            return self.groups[0]["id"]
        toks = _tokens(t) - _FILLER - {_stem(f) for f in _FILLER}
        if not toks:
            if len(self.groups) == 1:
                return self.groups[0]["id"]
            raise ValueError("say which threat: " + ", ".join(f"#{g['id']} {g['text']}" for g in self.groups))
        scored = [(len(toks & _tokens(g["text"])), g) for g in self.groups]
        best = max(s for s, _ in scored)
        hits = [g for s, g in scored if s == best]
        if best == 0:
            raise ValueError(f"no threat matches {text}; the threats are: " + ", ".join(f"#{g['id']} {g['text']}" for g in self.groups))
        if len(hits) > 1:
            raise ValueError(f"{text} matches more than one threat; use a number: " + ", ".join(f"#{g['id']} {g['text']}" for g in hits))
        return hits[0]["id"]

    # ---- the bodies -----------------------------------------------------------------
    def body(self, tool: str, v: dict) -> tuple[str, dict]:
        return ("squad", self._squad(v)) if tool == "squad" else ("defend", self._defend(v))

    def _squad(self, v: dict) -> dict:
        op = v["op"]
        b: dict = {"op": op}
        ids = self._pawn_ids(self._get(v, "pawns"))
        if ids:
            b["pawns"] = ids
        at = self._get(v, "at")
        if at is not None and at not in self.positions:
            raise ValueError("no such position; the positions are: " + ", ".join(self.positions))
        if op == "move" and at is None:
            raise ValueError("move needs at, one of: " + ", ".join(self.positions))
        if op in ("move", "retreat") and at is not None:
            b["at"] = at
            room = self._get(v, "room")
            if room is not None:
                if str(room) not in self.rooms:
                    raise ValueError("no such room; the rooms are: " + (", ".join(self.rooms) or "none listed"))
                if at in ("door", "inside"):
                    b["room"] = int(room)
        if op == "focus":
            t = self._get(v, "target")
            if t is None:
                raise ValueError("focus needs target: a threat number from THREATS or words like the pirates")
            b["target"] = self.target(str(t))
        if op == "retreat":
            below = self._get(v, "below")
            if below is not None:
                if not 10 <= int(below) <= 90:
                    raise ValueError("below is a health percent from 10 to 90")
                b["below"] = round(int(below) / 100, 2)
        return b

    def _defend(self, v: dict) -> dict:
        k = v["kind"]
        b: dict = {"kind": k}
        at, side = self._get(v, "at"), self._get(v, "side")
        if at is not None:
            b["at"] = at
        elif side is not None:
            b["side"] = side
        cnt = self._get(v, "count")
        if k in COUNT_MAX and cnt is not None:
            if not 1 <= int(cnt) <= COUNT_MAX[k]:
                raise ValueError(f"{k}: count is 1 to {COUNT_MAX[k]}")
            b["count"] = int(cnt)
        ln = self._get(v, "length")
        if k in LENGTH_KINDS and ln is not None:
            b["length"] = int(ln)
        s = self._get(v, "stuff")
        if s is not None:
            if s not in self.stuff:
                raise ValueError("no such material; the stock is: " + ", ".join(self.stuff))
            b["stuff"] = s
        return b


# ---- the view as text ----------------------------------------------------------------
_LEVELS = [
    dict(threats=6, fighters=8, others=5, labels=3, detail=2),
    dict(threats=4, fighters=6, others=3, labels=2, detail=1),
    dict(threats=3, fighters=5, others=2, labels=1, detail=0),
    dict(threats=2, fighters=3, others=0, labels=0, detail=0),
]
DIGEST_CAPS = (1500, 1000, 700, 450)        # the digest's four detail steps ask for these


def _fighter(f: dict, detail: int) -> str:
    bits = [("drafted" if f.get("drafted") else "not drafted"), _w(f.get("health") or "ok", 20)]
    if f.get("weapon"):
        bits.append(_w(f["weapon"], 24) + (f" range {f['range']}" if detail and f.get("range") is not None else ""))
    if detail:
        if f.get("cover"):
            bits.append(_w(f["cover"], 24))
        if f.get("mood"):
            bits.append("mood " + _w(f["mood"], 12))
        if f.get("job"):
            bits.append(_w(f["job"], 32))
    if detail > 1 and f.get("where"):
        bits.append(_w(f["where"], 36))
    return f"- {gd.name(f.get('name'))}: " + ", ".join(b for b in bits if b)


def _render(view: dict, lv: dict) -> list[str]:
    # the contract names the block "speed", which the envelope's own "speed" (a number) shadows on the wire: take either key
    sp = next((x for x in (view.get("speed_rule"), view.get("speed")) if isinstance(x, dict)), {})
    out = ["COMBAT: fight under way" + (f" (game speed was {sp['before']}x, slowed for the fight)" if sp.get("slowed") and sp.get("before") else "")]
    names: dict[int, str] = {}
    for key in ("fighters", "others"):
        for f in view.get(key) or []:
            if isinstance(f, dict) and _int(f.get("id")):
                names[_int(f["id"])] = gd.name(f.get("name"))
    th = [g for g in view.get("threats") or [] if isinstance(g, dict)]
    out.append("THREATS" + (" (nearest first)" if len(th) > 1 else "") + ":" + (" none in sight" if not th else ""))
    for g in th[:lv["threats"]]:
        labels = [_w(x, 28) for x in (g.get("labels") or [])[:lv["labels"]]]
        out.append(f"- #{_int(g.get('id')) or '?'} {g.get('count', '?')} {_w(g.get('kind'), 14)}, {_w(g.get('faction') or 'wild', 24)}, "
                   f"{_w(g.get('where'), 40)}, {_w(g.get('heading'), 16)}" + (": " + ", ".join(labels) if labels else ""))
    if len(th) > lv["threats"]:
        out.append(f"- and {len(th) - lv['threats']} more groups")
    fs = [f for f in view.get("fighters") or [] if isinstance(f, dict)]
    out.append("FIGHTERS:" + (" none able to fight" if not fs else ""))
    out.extend(_fighter(f, lv["detail"]) for f in fs[:lv["fighters"]])
    if len(fs) > lv["fighters"]:
        out.append(f"- and {len(fs) - lv['fighters']} more: " + ", ".join(gd.name(f.get("name")) for f in fs[lv["fighters"]:lv["fighters"] + 6]))
    ot = [o for o in view.get("others") or [] if isinstance(o, dict)]
    if ot and lv["others"]:
        out.append("NOT FIGHTING: " + ", ".join(f"{gd.name(o.get('name'))} ({_w(o.get('why'), 14)}, {'safe' if o.get('safe') else 'EXPOSED'})" for o in ot[:lv["others"]])
                   + (f" and {len(ot) - lv['others']} more" if len(ot) > lv["others"] else ""))
    elif ot:
        out.append(f"NOT FIGHTING: {len(ot)} ({sum(1 for o in ot if not o.get('safe'))} exposed)")
    dn = view.get("downed") if isinstance(view.get("downed"), dict) else {}
    cs = [c for c in dn.get("colonists") or [] if isinstance(c, dict)]
    if cs or dn.get("enemies"):
        out.append("DOWNED: " + "; ".join(
            [", ".join(gd.name(c.get("name")) + (" (bleeding)" if c.get("bleeding") else "") + (f" {_w(c.get('where'), 30)}" if lv["detail"] and c.get("where") else "") for c in cs[:5])
             or "no colonists"] + ([f"{dn['enemies']} enemies"] if dn.get("enemies") else [])))
    fi = view.get("fires") if isinstance(view.get("fires"), dict) else {}
    if fi.get("count"):
        out.append(f"FIRES: {fi.get('count')} ({fi.get('home', 0)} in the home area)" + (f", {_w(fi.get('where'), 30)}" if fi.get("where") else ""))
    if view.get("defenses"):
        out.append("DEFENSES: " + _w(view["defenses"], 200))
    sq = view.get("squad") if isinstance(view.get("squad"), dict) else {}
    if sq.get("members"):
        mem = [names.get(_int(i) or 0, str(i)) for i in sq["members"][:10]]
        rt = sq.get("retreat") if isinstance(sq.get("retreat"), dict) else None
        out.append("SQUAD: " + ", ".join(mem) + f"; {_w(sq.get('stance') or 'fire_at_will', 14)}"
                   + (f"; focus #{sq['focus']}" if sq.get("focus") else "") + (f"; retreat point ({rt.get('x')},{rt.get('z')})" if rt else ""))
    return out


def render_combat(view: dict, cap: int = 1500) -> str:
    """The combat view as text of at most ``cap`` characters (detail shrinks level by level; the threat lines always stay)."""
    if not isinstance(view, dict):
        return ""
    text = ""
    for lv in _LEVELS:
        text = "\n".join(_render(view, lv))
        if len(text) <= cap:
            return text
    lines = text.split("\n")
    while len("\n".join(lines)) > cap and len(lines) > 3:
        lines.pop()
    return "\n".join(lines)[:cap]

"""The steward's Biotech tools (task rh-bio): ``mechs``, ``genes`` and ``children``.

Built from the digest's ``bio`` block (contract A): no block, no tools, and a tool (or one of its ops) appears only when
the digest holds the data it needs. Each call maps to one ``POST /act/<verb>``: mechs -> mech_mode | mech_group |
mech_gestate, genes -> genepack | xenogerm | hemogen, children -> child_schedule | feed | child_care. Optional values use
the same ``none`` / 0 convention as steward_build. Names become ids through the digest; an unknown name is a ValueError
listing the known ones. Nothing here is romantic: children only get schedules, feeding and care.
"""
from __future__ import annotations

import re

from ...core import gamedata as gd
from ...core.toolloop import Param, ToolSpec
from .steward_digest import pawn_names

NONE = "none"
WHY_MAX = 80
_WHY = Param("why", "text", "a brief reason in your own words, starting with since or because; it is shown to the player",
             max_len=WHY_MAX, required=False, same_call=True)   # a copy, as steward_build (importing steward_tools would be circular)

TOOLS = ("mechs", "genes", "children")
MODES = ["work", "escort", "recharge", "shutdown"]
PRESETS = ["school", "play", "default"]
FEED_MODES = ["never", "childcare", "urgent"]
MAX_NAMES = 12


def _words(items) -> list[str]:
    out: list[str] = []
    for x in items or []:
        w = gd.clean(str(x), "bio", max_len=40).strip()
        if w and w not in out:
            out.append(w)
    return out


def _named(items, id_key: str) -> dict[str, int]:
    """cleaned name -> id for a digest list of {name, id_key} dicts (first duplicate wins)."""
    out: dict[str, int] = {}
    for x in items or []:
        if isinstance(x, dict):
            try:
                out.setdefault(gd.name(x.get("name")), int(x[id_key]))
            except (KeyError, TypeError, ValueError):
                continue
    return {k: v for k, v in out.items() if k}


def _list(b: dict, key: str) -> list:
    v = b.get(key)
    return v if isinstance(v, list) else []


def _opt(name: str, desc: str, choices: list[str]) -> Param:
    return Param(name, "enum", desc, choices=[NONE] + choices, required=False)


class BioKit:
    """The Biotech tools for one digest, and the body of each call."""

    def __init__(self, digest: dict):
        b = digest.get("bio")
        self.has = isinstance(b, dict)
        b = b if self.has else {}
        self.pawns = pawn_names(digest)
        # grown colonists only: feeders, carers and implant targets are never children or prisoners
        kid = {gd.name(p.get("name")) for p in digest.get("pawns") or [] if isinstance(p, dict)
               and (str(p.get("role", "colonist")).lower() != "colonist" or (isinstance(p.get("age"), (int, float)) and p["age"] < 13))}
        self.adults = {n: i for n, i in self.pawns.items() if n not in kid}
        self.mechs = dict(list(_named(_list(b, "mechs"), "id").items())[:MAX_NAMES])
        self.kinds = _words(k.get("kind") if isinstance(k, dict) else k for k in _list(b, "can_gestate"))
        self.children = _named(_list(b, "children"), "pawn")
        self.babies = _named(_list(b, "babies"), "pawn")
        gb = b.get("genebank") if isinstance(b.get("genebank"), dict) else {}
        self.packs = _words(re.sub(r"\s*\([^)]*\)\s*$", "", str(p)) for p in _list(gb, "packs"))
        self.gb = gb
        self.hemogen = isinstance(b.get("hemogen"), dict)
        self.genes = bool(_list(b, "genes"))
        # prisoners for blood donation: a digest list if the mod gives one, else pawns whose role says prisoner
        pri = _named(digest.get("prisoners"), "id")
        hb = b.get("hemogen") if isinstance(b.get("hemogen"), dict) else {}
        pri.update(_named(_list(hb, "prisoners"), "id"))              # the bio block's prisoners who are not donating yet
        pri.update({gd.name(p.get("name")): int(p["id"]) for p in digest.get("pawns") or []
                    if isinstance(p, dict) and str(p.get("role", "")).lower() == "prisoner" and str(p.get("id", "")).lstrip("-").isdigit()})
        self.prisoners = pri
        self.germs = _named(_list(b, "xenogerms"), "id")
        self.implant_ok = b.get("may_implant") is True and bool(self.germs)   # the player's setting, and a xenogerm to implant
        self.carers = _words(_list(b, "carers"))
        self.mechanitors = bool(_list(b, "mechanitors"))
        self.tools = self._tools() if self.has else []
        self.names = {t.name for t in self.tools}

    # ---- the tools ------------------------------------------------------------------
    def _tools(self) -> list[ToolSpec]:
        out: list[ToolSpec] = []
        if self.mechanitors:
            ops = ["mode", "group"] + (["gestate"] if self.kinds else [])
            ps = [Param("op", "enum", "set a group's mode, move a mech to a group, or start gestating one", choices=ops),
                  Param("group", "int", "group 1-12 (0 if unused)", lo=0, hi=12, required=False),
                  _opt("mode", "mode: the group's mode", MODES)]
            if self.mechs:
                ps.append(_opt("mech", "group: which mech", list(self.mechs)))
            if self.kinds:
                ps += [_opt("kind", "gestate: which mech", self.kinds), Param("count", "int", "gestate: 1-3 (0 if unused)", lo=0, hi=3, required=False)]
            out.append(ToolSpec("mechs", "Command mechs.", ps + [_WHY]))
        gops = (["store"] if self.gb.get("unstored") or self.gb.get("genepacks") else []) \
            + (["assemble"] if self.packs else []) + (["implant"] if self.implant_ok else []) \
            + (["donate"] if (self.hemogen or self.genes) and self.prisoners else [])
        if gops and (self.genes or self.gb or self.hemogen):
            ps = [Param("op", "enum", "store genepacks, assemble or implant a xenogerm, or a prisoner donates blood", choices=gops)]
            if "assemble" in gops:
                ps += [Param("genepacks", "text", "assemble: pack names, comma separated", max_len=160, required=False),
                       Param("name", "text", "assemble: the xenogerm's name", max_len=40, required=False)]
            if "implant" in gops:
                ps.append(_opt("xenogerm", "implant: which xenogerm", list(self.germs)))
            if "implant" in gops or "assemble" in gops:
                ps.append(Param("plan", "text", "assemble/implant: why, 10-200 chars, the player reads it", max_len=200, required=False))
            if "implant" in gops and self.adults:
                ps.append(_opt("who", "implant: which colonist", list(self.adults)))
            if "donate" in gops:
                ps.append(_opt("prisoner", "donate: who", list(self.prisoners)))
            out.append(ToolSpec("genes", "Genepacks, xenogerms and blood.", ps + [_WHY]))
        cops = (["schedule"] if self.children else []) + (["feed"] if self.babies else []) \
            + (["care"] if (self.children or self.babies) and self.adults else [])
        if cops:
            ps = [Param("op", "enum", "set a child's day, who feeds a baby, or a colonist's Childcare priority", choices=cops)]
            kids = list(self.children) + list(self.babies)
            ps.append(_opt("child", "schedule/feed: the child or baby", kids))
            if "schedule" in cops:
                ps.append(_opt("preset", "schedule: the day's shape", PRESETS))
            if "feed" in cops or "care" in cops:
                ps.append(_opt("who", "feed: the feeder; care: the colonist", list(self.adults)))
            if "feed" in cops:
                ps.append(_opt("feed_mode", "feed: when", FEED_MODES))
            if "care" in cops:
                ps.append(Param("priority", "int", "care: 1 highest to 4 lowest (0 = 1)", lo=0, hi=4, required=False))
            out.append(ToolSpec("children", "Children, babies and childcare.", ps + [_WHY]))
        return out

    # ---- the bodies -----------------------------------------------------------------
    @staticmethod
    def _get(v: dict, k: str):
        x = v.get(k)
        return None if x in (None, "", NONE, 0) else x

    def body(self, tool: str, v: dict) -> tuple[str, dict]:
        verb, b = getattr(self, "_" + tool)(v)
        why = gd.clean(str(v.get("why") or ""), "why", max_len=WHY_MAX).strip()
        if why:
            b["why"] = why
        return verb, b

    def _pick(self, table: dict[str, int], name, what: str) -> int:
        n = None if name in (None, "", NONE) else str(name).strip().lower()
        for k, i in table.items():
            if k.lower() == n:
                return i
        raise ValueError(f"no {what} named {name}; the {what}s are: " + ", ".join(table))

    def _mechs(self, v: dict) -> tuple[str, dict]:
        op = v["op"]
        g = self._get(v, "group")
        if op == "mode":
            mode = self._get(v, "mode")
            if g is None or mode not in MODES:
                raise ValueError("mode needs group 1-12 and mode (" + ", ".join(MODES) + ")")
            return "mech_mode", {"group": g, "mode": mode}
        if op == "group":
            if g is None or self._get(v, "mech") is None:
                raise ValueError("group needs mech and group 1-12")
            return "mech_group", {"mech": self._pick(self.mechs, v.get("mech"), "mech"), "group": g}
        k = self._get(v, "kind")
        if k is None or k not in self.kinds:
            raise ValueError("gestate needs kind, one of: " + ", ".join(self.kinds))
        return "mech_gestate", {"kind": k, "count": self._get(v, "count") or 1}

    def _genes(self, v: dict) -> tuple[str, dict]:
        op = v["op"]
        if op == "store":
            return "genepack", {}
        if op == "assemble":
            asked = [x.strip() for x in str(v.get("genepacks") or "").split(",") if x.strip()]
            low = {p.lower(): p for p in self.packs}
            if not asked or any(a.lower() not in low for a in asked):
                raise ValueError("assemble needs genepacks from: " + ", ".join(self.packs))
            name = gd.clean(str(v.get("name") or ""), "name", max_len=40).strip()
            return "xenogerm", {"op": "assemble", "genepacks": [low[a.lower()] for a in asked], "name": name or "xenogerm", "plan": self._plan(v)}
        if op == "implant":
            x = self._pick(self.germs, v.get("xenogerm"), "xenogerm")
            return "xenogerm", {"op": "implant", "xenogerm": x, "pawn": self._pick(self.adults, v.get("who"), "colonist"), "plan": self._plan(v)}
        return "hemogen", {"op": "donate", "prisoner": self._pick(self.prisoners, v.get("prisoner"), "prisoner")}

    @staticmethod
    def _plan(v: dict) -> str:
        p = gd.clean(str(v.get("plan") or ""), "plan", max_len=200).strip()
        if len(p) < 10:
            raise ValueError("give a plan of 10 to 200 characters that the player can read")
        return p

    def _children(self, v: dict) -> tuple[str, dict]:
        op = v["op"]
        if op == "care":
            return "child_care", {"pawn": self._pick(self.adults, v.get("who"), "colonist"), "priority": v.get("priority") or 1}
        if op == "schedule":
            pre = self._get(v, "preset")
            if pre not in PRESETS:
                raise ValueError("schedule needs preset: " + ", ".join(PRESETS))
            return "child_schedule", {"pawn": self._pick({**self.children, **self.babies}, v.get("child"), "child"), "preset": pre}
        mode = self._get(v, "feed_mode") or "childcare"
        baby = self._pick(self.babies, v.get("child"), "baby")
        if self._get(v, "who") is None:
            raise ValueError("feed needs who: the colonist who feeds the baby (" + ", ".join(self.adults) + ")")
        feeder = self._pick(self.adults, v.get("who"), "colonist")
        return "feed", {"baby": baby, "feeder": feeder, "mode": mode}

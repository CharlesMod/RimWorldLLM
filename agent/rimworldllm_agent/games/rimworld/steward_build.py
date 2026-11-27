"""The steward's builder tools (Vivarium phase 2): ``build``, ``zone``, ``designate`` and ``bill``.

The model states intent ("a medium bedroom", "a stove in room 12", "a potato field", "mine steel"); the mod's planners
pick the cells. The tools are built from the digest's ``build`` block (docs/VIVARIUM.md, "Phase 2"): a tool, a kind or
an enum value appears only when the digest holds what it needs, so a digest without a build block has none of them.

Optional parameters: the grammar protocol always writes every parameter, so an optional enum carries the word
``none`` (the model's way to say "unused"), an optional number carries 0; the json and native protocols may simply
leave them out. Both forms mean the same here.

Each call maps to one ``POST /act/<verb>``: build -> room | build, zone -> zone, designate -> designate (mine, chop,
harvest, haul only: deconstruct and cancel need a place or a target, and tearing down is the player's call, so the
steward does not get them), bill -> bill. ``player_asked`` is never sent.
"""
from __future__ import annotations

from ...core import gamedata as gd
from ...core.toolloop import Param, ToolSpec

NONE = "none"
WHY_MAX = 80
_WHY = Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since meals are running low); "
             "it is shown to the player beside what you did", max_len=WHY_MAX, required=False, same_call=True)   # as steward_tools._WHY (a copy: importing it would be circular)

TOOLS = ("build", "zone", "designate", "bill")
PURPOSES = ["bedroom", "barracks", "kitchen", "dining", "storage", "freezer", "hospital", "workshop", "rec"]   # BuildPure room purposes
SIZES = ["small", "medium", "large"]
PRESETS = ["food", "meds", "raw", "all"]
BILL_OPS = ["add", "remove", "suspend", "resume"]
MAX_ROOMS, MAX_BENCHES = 12, 8
LIVING_ROOMS = ("bedroom", "barracks", "hospital", "dining", "rec", "prison")   # never cool these as a freezer


def _opt(name: str, desc: str, choices: list[str]) -> Param:
    return Param(name, "enum", desc, choices=[NONE] + choices, required=False)


def _words(items) -> list[str]:
    """Cleaned, de-duplicated words from a digest list of strings (or of {key: word} dicts, via ``_key``)."""
    out: list[str] = []
    for x in items or []:
        w = gd.clean(str(x), "build", max_len=40).strip()
        if w and w not in out:
            out.append(w)
    return out


def _ids(items, role_key: str) -> dict[str, str]:
    """id (as text) -> short description for the digest's rooms or benches."""
    out: dict[str, str] = {}
    for x in items or []:
        if not isinstance(x, dict) or x.get("id") in (None, "", 0):
            continue
        try:
            i = str(int(x["id"]))
        except (TypeError, ValueError):
            continue
        out.setdefault(i, gd.clean(str(x.get(role_key) or x.get("label") or ""), "build", max_len=40).strip())
    return out


class BuildKit:
    """The four builder tools for one digest, and the body of each call."""

    def __init__(self, digest: dict):
        b = digest.get("build")
        b = b if isinstance(b, dict) else None
        self.has = b is not None
        b = b or {}
        self.rooms = dict(list(_ids(b.get("rooms"), "role").items())[:MAX_ROOMS])
        self.benches = dict(list(_ids(b.get("benches"), "label").items())[:MAX_BENCHES])
        self.recipes: dict[str, list[str]] = {}
        for x in b.get("benches") or []:
            if isinstance(x, dict) and str(x.get("id")) in self.benches and isinstance(x.get("recipes"), list):
                self.recipes[str(x["id"])] = _words(x["recipes"])
        self.things = _words(b.get("can_build"))
        self.stuff = _words(s.get("stuff") if isinstance(s, dict) else s for s in b.get("stuff") or [])
        self.plants = _words(b.get("plants"))
        self.ores = _words(o.get("ore") if isinstance(o, dict) else o for o in b.get("ores") or [])
        self.tools = self._tools() if self.has else []
        self.names = {t.name for t in self.tools}

    # ---- the tools ------------------------------------------------------------------
    def _rooms_param(self, desc: str) -> Param:
        roles = ", ".join(f"{i} {r}" if r else i for i, r in self.rooms.items())
        return _opt("room", f"{desc} (room id: {roles})", list(self.rooms))

    def _tools(self) -> list[ToolSpec]:
        out: list[ToolSpec] = []
        stuff = [_opt("stuff", "material", self.stuff)] if self.stuff else []
        kinds = ["room"] + (["thing"] if self.things else [])
        params = [Param("kind", "enum", "room: walls and a door; thing: one building", choices=kinds),
                  _opt("purpose", "room: its use", PURPOSES), _opt("size", "room: how big", SIZES)]
        if self.things:
            params.append(_opt("thing", "thing: what", self.things))
        if self.rooms and self.things:
            params.append(self._rooms_param("thing: inside this existing room (a room planned this turn has no id yet), else near home"))
        out.append(ToolSpec(
            "build", "Plan a room or one building; the planner picks the cells.", params + stuff + [_WHY]))
        zk = (["grow"] if self.plants else []) + ["stockpile", "dump"]
        zp = [Param("kind", "enum", "grow: a field; stockpile: storage; dump: junk", choices=zk)]
        if self.plants:
            zp.append(_opt("plant", "grow: the crop", self.plants))
        zp += [_opt("preset", "stockpile: what it takes", PRESETS), _opt("size", "how big", SIZES)]
        if self.rooms:
            zp.append(self._rooms_param("stockpile: inside this room"))
        out.append(ToolSpec("zone", "Mark a growing zone, stockpile or dump; the planner picks the cells.", zp + [_WHY]))
        dk = ["chop", "harvest", "haul"] + (["mine"] if self.ores else [])
        dp = [Param("kind", "enum", "mine: ore rock; chop: trees in the way; harvest: wild plants; haul: loose items", choices=dk)]
        if self.ores:
            dp.append(_opt("ore", "mine: resource", self.ores))
        out.append(ToolSpec("designate", "Order mining, chopping, harvesting or hauling near home.", dp + [_WHY]))
        if self.benches:
            names = ", ".join(f"{i} {l}" if l else i for i, l in self.benches.items())
            out.append(ToolSpec(
                "bill", "Add, remove, suspend or resume a workbench bill.",
                [Param("op", "enum", "what to do", choices=BILL_OPS),
                 Param("bench", "enum", f"the workbench id ({names})", choices=list(self.benches)),
                 Param("recipe", "text", "the bench's recipe", max_len=60),
                 Param("count", "int", "add: make this many, 0 if unused", lo=0, hi=99, required=False),
                 Param("until", "int", "add: keep this many in stock, 0 if unused", lo=0, hi=999, required=False), _WHY]))
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

    def _room_id(self, v: dict) -> int | None:
        r = self._get(v, "room")
        if r is None:
            return None
        if str(r) not in self.rooms:
            raise ValueError("no such room; the rooms are: " + ", ".join(self.rooms))
        return int(r)

    def _stuff(self, v: dict, b: dict) -> None:
        s = self._get(v, "stuff")
        if s is not None:
            if s not in self.stuff:
                raise ValueError("no such material; the stock is: " + ", ".join(self.stuff))
            b["stuff"] = s

    def _build(self, v: dict) -> tuple[str, dict]:
        if v["kind"] == "room":
            p, s = self._get(v, "purpose"), self._get(v, "size")
            if p is None or s is None:
                raise ValueError("a room needs purpose (" + ", ".join(PURPOSES) + ") and size (" + ", ".join(SIZES) + ")")
            # thing/room are ignored for a room: the grammar writes every parameter and the 26B fills them with what it
            # means to put inside later ("cooler" for a freezer); refusing cost a step each time in the live check.
            b = {"purpose": p, "size": s}
            self._stuff(v, b)
            return "room", b
        t = self._get(v, "thing")
        if t is None or t not in self.things:
            raise ValueError("a thing needs thing, one of: " + ", ".join(self.things))
        b = {"thing": t}
        self._stuff(v, b)
        r = self._room_id(v)
        if t == "cooler":
            # live check: the 26B put the freezer's cooler into an existing bedroom/barracks in the same turn it planned
            # the freezer. A cooler goes into its freezer once that room is built and listed.
            role = self.rooms.get(str(r), "").lower() if r is not None else ""
            if r is None or any(w in role for w in LIVING_ROOMS):
                raise ValueError("a cooler goes in the wall of a freezer or storeroom listed in ROOMS; if the freezer is "
                                 "only planned, place the cooler in a later turn once it shows in ROOMS")
        if r is not None:
            b["room"] = r
        return "build", b

    def _zone(self, v: dict) -> tuple[str, dict]:
        k = v["kind"]
        b: dict = {"kind": k}
        if k == "grow":
            p = self._get(v, "plant")
            if p is None or p not in self.plants:
                raise ValueError("a growing zone needs plant, one of: " + ", ".join(self.plants))
            b["plant"] = p
        elif self._get(v, "plant") is not None:
            raise ValueError("plant is only for a growing zone")
        pre = self._get(v, "preset")
        if pre is not None and k == "stockpile":
            b["preset"] = pre
        s = self._get(v, "size")
        if s is not None:
            b["size"] = s
        r = self._room_id(v)
        if r is not None:
            if k != "stockpile":
                raise ValueError("room is only for a stockpile")
            b["room"] = r
        return "zone", b

    def _designate(self, v: dict) -> tuple[str, dict]:
        k = v["kind"]
        b: dict = {"kind": k}
        if k == "mine":
            o = self._get(v, "ore")
            if o is None or o not in self.ores:
                raise ValueError("mining needs ore, one of: " + ", ".join(self.ores))
            b["ore"] = o
        elif self._get(v, "ore") is not None:
            raise ValueError("ore is only for mining")
        return "designate", b

    def _bill(self, v: dict) -> tuple[str, dict]:
        bench = str(v["bench"])
        if bench not in self.benches:
            raise ValueError("no such workbench; the benches are: " + ", ".join(self.benches))
        recipe, op = v["recipe"], v["op"]
        b: dict = {"op": op, "bench": int(bench), "recipe": recipe}
        if op == "add":
            known = self.recipes.get(bench)
            if known:
                hit = next((r for r in known if r.lower() == recipe.strip().lower()), None)
                if hit is None:
                    raise ValueError(f"bench {bench} cannot make {recipe}; its recipes are: " + ", ".join(known))
                b["recipe"] = hit
            count, until = self._get(v, "count"), self._get(v, "until")
            if count is not None and until is not None:
                raise ValueError("give count or until, not both")
            if count is not None:
                b["count"] = count
            if until is not None:
                b["until"] = until
        return "bill", b

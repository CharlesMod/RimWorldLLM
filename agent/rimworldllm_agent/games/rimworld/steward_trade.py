"""The steward's trade and caravan tools (Vivarium phase 4): ``trade_offer``, ``trade``, ``caravan_plan``, ``caravan``.

Built from the digest's ``trade``, ``caravans`` and ``quests`` blocks (docs/VIVARIUM.md, "Phase 4"): a tool exists only
when the digest holds what it needs, and its enums come from the digest's ids. Two tools only look (GET, never an
action, so they are not in ``acting``); two act:

    trade_offer(with)                  GET  /trade/offer?with=ID        -> a compact price list
    trade(with, buy, sell, why)        POST /act/trade {with, buy, sell}
    caravan_plan(pawns, to, ...)       GET  /caravan/plan?...           -> a compact plan
    caravan(op, pawns, to, ..., why)   POST /act/caravan {op, ...}

Optional parameters follow steward_build: an optional enum carries ``none``, an optional text may be empty. Accepting
or declining a quest stays with ``choose``; ``player_asked`` is never sent.
"""
from __future__ import annotations

import re

from ...core import gamedata as gd
from ...core.toolloop import Param, ToolSpec
from .steward_digest import pawn_names

NONE = "none"
WHY_MAX = 80
_WHY = Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since components are low); "
             "it is shown to the player beside what you did", max_len=WHY_MAX, required=False, same_call=True)   # as steward_tools._WHY (a copy: importing it would be circular)

LOOKS = ("trade_offer", "caravan_plan")
ACTS = ("trade", "caravan")
MAX_LINES, MAX_COUNT, MAX_PAWNS = 12, 99999, 6
PURPOSES = ["trade", "visit", "quest", "deliver"]
OPS = ["form", "send", "cancel"]
RENDER_MAX = 1200
_BLANK = ("", "none", "nothing", "-", "n/a")

_LEAD = re.compile(r"^(\d+)\s*[x×]?\s*(.+)$")            # "20 cloth", "20x cloth"
_TRAIL_X = re.compile(r"^(.+?)\s*[x×]\s*(\d+)$")         # "cloth x20", "cloth x 20"
_TRAIL = re.compile(r"^(.+?)\s+(\d+)$")                  # "cloth 20"


def parse_lines(text: str, what: str) -> list[dict]:
    """'cloth x20, 5 components, steel 100' -> [{item, count}, ...]; ValueError names the part that does not read."""
    out: dict[str, int] = {}
    for part in re.split(r"[,;\n]|\band\b", str(text or "")):
        p = part.strip().strip(".")
        if not p:
            continue
        m = _TRAIL_X.match(p) or _TRAIL.match(p)
        if m:
            item, n = m.group(1), m.group(2)
        elif _LEAD.match(p):
            n, item = _LEAD.match(p).groups()
        else:
            raise ValueError(f"{what}: give a count for {p}; write items like: cloth x20, steel x100")
        item = gd.clean(item, "label", max_len=40).strip().lower()
        n = int(n)
        if not item or not 1 <= n <= MAX_COUNT:
            raise ValueError(f"{what}: {p} needs an item name and a count from 1 to {MAX_COUNT}")
        out[item] = out.get(item, 0) + n
    return [{"item": k, "count": v} for k, v in out.items()]


def _blank(v) -> bool:
    return str(v or "").strip().lower() in _BLANK


def _short(text, n: int = 24) -> str:
    return gd.clean(str(text or ""), "label", max_len=n).strip()


def _id(x) -> str:
    return str(x.get("id") or "").strip() if isinstance(x, dict) else ""


def _price(x: dict) -> str:
    p = x.get("price")
    return f"{x.get('item', '?')} {x.get('count', '?')}" + (f" @{p:g}" if isinstance(p, (int, float)) and not isinstance(p, bool) else "")


def render_offer(r: dict) -> str:
    """GET /trade/offer -> 'Tomas sells: steel 400 @2.1, ...; buys of ours: cloth 300 @1.1, ...; silver 1800'."""
    name = _short(r.get("name") or r.get("with"), 30)
    sells = [x for x in r.get("buys") or [] if isinstance(x, dict)]      # the route's `buys` is what we buy from them
    buys = [x for x in r.get("sells") or [] if isinstance(x, dict)]      # and its `sells` is what we may sell to them
    out = [f"{name} sells: " + (", ".join(_price(x) for x in sells[:30]) or "nothing"),
           "buys of ours: " + (", ".join(_price(x) for x in buys[:30]) or "nothing")]
    if isinstance(r.get("silver"), (int, float)):
        out.append(f"trader silver {r['silver']:g}")
    if r.get("negotiator"):
        out.append("negotiator " + _short(r["negotiator"]))
    text = "; ".join(out)
    if len(text) > RENDER_MAX:
        text = text[:RENDER_MAX - 3].rsplit(",", 1)[0] + " ..."
    return text


def render_plan(r: dict) -> str:
    """GET /caravan/plan -> '3.1 days; food needed 9.2 (pemmican 12); mass 120/210 kg; ok' (or the problems)."""
    bits = []
    if isinstance(r.get("days"), (int, float)):
        bits.append(f"{r['days']:g} days")
    if r.get("food_need") is not None:
        bits.append(f"food needed {r['food_need']}" + (f" (has {r['food_days']} days)" if r.get("food_days") is not None else ""))
    food = [x for x in r.get("food") or [] if isinstance(x, dict)]
    if food:
        bits.append("packs " + ", ".join(f"{x.get('item', '?')} {x.get('count', '?')}" for x in food[:5]))
    if r.get("mass") is not None or r.get("capacity") is not None:
        bits.append(f"mass {r.get('mass', '?')}/{r.get('capacity', '?')} kg")
    probs = [_short(p, 80) for p in r.get("problems") or []][:4]
    bits.append("ok" if r.get("ok") and not probs else "NOT OK: " + ("; ".join(probs) or "refused"))
    return "; ".join(bits)[:RENDER_MAX]


class TradeKit:
    """The trade and caravan tools for one digest, the bodies of the acting ones and the look requests."""

    def __init__(self, digest: dict):
        t = digest.get("trade")
        t = t if isinstance(t, dict) else None
        self.has = t is not None
        t = t or {}
        self.pawns = pawn_names(digest)
        self.traders = {i: x for x in t.get("traders") or [] if (i := _id(x))}
        self.places = {i: x for x in t.get("settlements") or [] if (i := _id(x))}
        cs = [c for c in digest.get("caravans") or [] if isinstance(c, dict) and _id(c)]
        self.caravans = {_id(c): c for c in cs}
        qs = [q for q in digest.get("quests") or [] if isinstance(q, dict) and _id(q)]
        self.quests = {_id(q): q for q in qs if q.get("state") != "offer"}
        self.animals = {gd.name(a.get("name")): str(a["id"]) for a in digest.get("animals") or [] if isinstance(a, dict) and a.get("id") and a.get("name")}
        # who we can deal with: visiting traders, and a settlement where one of our caravans sits
        at = {str(c["can_trade"]) for c in cs if c.get("can_trade")}
        self.partners = {**self.traders, **{i: x for i, x in self.places.items() if i in at}}
        self.dests = dict(self.places)
        for q in self.quests.values():                                     # a site quest's place is a destination too
            s = str(q.get("site") or "")
            if s and s not in self.dests:
                self.dests[s] = {"id": s, "name": _short(q.get("what") or q.get("name"), 20)}
        self.tools = self._tools() if self.has else []
        self.names = {x.name for x in self.tools}
        self.acting = self.names & set(ACTS)

    # ---- the tools ------------------------------------------------------------------
    def _with_param(self) -> Param:
        names = ", ".join(f"{i} {_short(x.get('name'), 14)}" for i, x in self.partners.items())
        return Param("with", "enum", f"who ({names})", choices=list(self.partners))

    def _tools(self) -> list[ToolSpec]:
        out: list[ToolSpec] = []
        if self.partners:
            out.append(ToolSpec("trade_offer", "Look at one trader's prices (not an action); do this before trade.", [self._with_param()]))
            out.append(ToolSpec(
                "trade", "Buy and sell with one trader at once; final.",
                [self._with_param(),
                 Param("buy", "text", "items to buy: cloth x20, steel x100 (none if unused)", max_len=200, required=False),
                 Param("sell", "text", "items to sell, same form (none if unused)", max_len=200, required=False), _WHY]))
        if self.pawns and (self.dests or self.quests or self.caravans):
            dests = [f"{i} {_short(x.get('name'), 14)}" for i, x in self.dests.items()]
            to = Param("to", "enum", "where (" + ", ".join(dests + (["home"] if self.caravans else [])) + ")",
                       choices=[NONE] + list(self.dests) + (["home"] if self.caravans else []), required=False)
            quest = Param("quest", "enum", "a quest to work, instead of to", choices=[NONE] + list(self.quests), required=False) if self.quests else None
            pawns = Param("pawns", "text", "colonist names, comma separated (none if unused)", max_len=100, required=False)
            goods = Param("goods", "text", "goods to carry: cloth x20 (none if unused)", max_len=120, required=False)
            animals = [Param("animals", "text", "pack animal names, comma separated (none if unused)", max_len=100, required=False)] if self.animals else []
            opt = lambda ps: [p for p in ps if p is not None]
            out.append(ToolSpec("caravan_plan", "Check a planned caravan's days, food and load (not an action).",
                                opt([pawns, to, quest, goods] + animals)))
            out.append(ToolSpec(
                "caravan", "Form a caravan, send a formed one, or cancel a forming one.",
                [Param("op", "enum", "form: gather and leave; send: path a caravan; cancel: a forming one", choices=OPS)]
                + opt([pawns, to, quest, goods] + animals)
                + [Param("purpose", "enum", "why it goes", choices=[NONE] + PURPOSES, required=False)]
                + ([Param("caravan", "enum", "send/cancel: which (" + ", ".join(self.caravans) + ")", choices=[NONE] + list(self.caravans), required=False)] if self.caravans else [])
                + [_WHY]))
        return out

    # ---- the bodies -----------------------------------------------------------------
    def _partner(self, v: dict) -> str:
        w = str(v.get("with") or "")
        x = self.partners.get(w)
        if x is None:
            raise ValueError("no such trader; you can trade with: " + ", ".join(self.partners))
        return w

    def _names(self, text: str, known: dict[str, int | str], what: str, max_n: int) -> list[int]:
        if _blank(text):
            return []
        low = {k.lower(): v for k, v in known.items()}
        out = []
        for n in (x.strip() for x in str(text).split(",") if x.strip()):
            if n.lower() not in low:
                raise ValueError(f"no {what} named {n}; the {what}s are: " + ", ".join(known))
            out.append(int(low[n.lower()]))
        return list(dict.fromkeys(out))

    def _where(self, v: dict) -> str | None:
        q, to = str(v.get("quest") or NONE), str(v.get("to") or NONE)
        if q != NONE:
            if q not in self.quests:
                raise ValueError("no such quest; the quests are: " + ", ".join(self.quests))
            return q
        if to == NONE:
            return None
        if to != "home" and to not in self.dests:
            raise ValueError("no such place; the places are: " + ", ".join(list(self.dests) + ["home"]))
        return to

    def _quest_check(self, v: dict) -> None:
        q = self.quests.get(str(v.get("quest") or ""))
        if q is not None and q.get("can") is False:
            raise ValueError("the quest cannot be worked now: " + (_short(q.get("why_not"), 80) or "see QUESTS"))

    def _goods(self, v: dict) -> list[dict]:
        return [] if _blank(v.get("goods")) else parse_lines(v["goods"], "goods")

    def _trade(self, v: dict) -> tuple[str, dict]:
        w = self._partner(v)
        t = self.partners[w]
        if t.get("can_trade") is False:
            raise ValueError(f"cannot trade with {_short(t.get('name'))} now: " + (_short(t.get("why_not"), 80) or "not possible"))
        buy = [] if _blank(v.get("buy")) else parse_lines(v["buy"], "buy")
        sell = [] if _blank(v.get("sell")) else parse_lines(v["sell"], "sell")
        if not buy and not sell:
            raise ValueError("give buy or sell, like: buy cloth x20, steel x100")
        if len(buy) + len(sell) > MAX_LINES:
            raise ValueError(f"at most {MAX_LINES} lines per deal")
        return "trade", {"with": w, "buy": buy, "sell": sell}

    def _caravan(self, v: dict) -> tuple[str, dict]:
        op = v["op"]
        c = str(v.get("caravan") or NONE)
        if op in ("send", "cancel"):
            if c == NONE or c not in self.caravans:
                raise ValueError(f"{op} needs caravan, one of: " + (", ".join(self.caravans) or "none exist"))
            b: dict = {"op": op, "caravan": c}
            if op == "send":
                to = self._where(v)
                if to is None:
                    raise ValueError("send needs to (a place or home) or quest")
                self._quest_check(v)
                b["to"] = to
            return "caravan", b
        pawns = self._names(v.get("pawns"), self.pawns, "colonist", MAX_PAWNS)
        if not 1 <= len(pawns) <= MAX_PAWNS:
            raise ValueError(f"form needs 1 to {MAX_PAWNS} colonists in pawns: " + ", ".join(self.pawns))
        to = self._where(v)
        if to is None or to == "home":
            raise ValueError("form needs to (a place) or quest")
        self._quest_check(v)
        b = {"op": "form", "pawns": pawns, "to": to}
        a = self._names(v.get("animals"), self.animals, "animal", 8)
        if a:
            b["animals"] = a
        g = self._goods(v)
        if g:
            b["goods"] = g
        p = str(v.get("purpose") or NONE)
        if p != NONE:
            b["purpose"] = p
        return "caravan", b

    def body(self, tool: str, v: dict) -> tuple[str, dict]:
        verb, b = getattr(self, "_" + tool)(v)
        why = gd.clean(str(v.get("why") or ""), "why", max_len=WHY_MAX).strip()
        if why:
            b["why"] = why
        return verb, b

    # ---- the looks (GET, no action) ---------------------------------------------------
    def look_request(self, tool: str, v: dict) -> tuple[str, dict]:
        """(path, query params) of a look tool call; ValueError for text that does not resolve."""
        if tool == "trade_offer":
            return "/trade/offer", {"with": self._partner(v)}
        q: dict = {}
        pawns = self._names(v.get("pawns"), self.pawns, "colonist", MAX_PAWNS)
        if pawns:
            q["pawns"] = ",".join(map(str, pawns))
        a = self._names(v.get("animals"), self.animals, "animal", 8)
        if a:
            q["animals"] = ",".join(map(str, a))
        to = self._where(v)
        if to is not None:
            q["to"] = to
        g = self._goods(v)
        if g:
            q["goods"] = ",".join(f"{x['item']}:{x['count']}" for x in g)
        if "pawns" not in q or "to" not in q:
            raise ValueError("plan needs pawns and a place (to) or a quest")
        return "/caravan/plan", q

    async def look(self, tool: str, v: dict, bridge) -> str:
        path, q = self.look_request(tool, v)
        r = await bridge.look(path, q)
        return render_offer(r) if tool == "trade_offer" else render_plan(r)

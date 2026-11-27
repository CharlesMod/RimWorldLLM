"""The steward's trade tools (Vivarium phase 4): ``trade_offer`` and ``trade``. Caravans are off: the narrator has no caravan tool.

Built from the digest's ``trade`` block (docs/VIVARIUM.md, "Phase 4"): a tool exists only when the digest holds a trader
that can be dealt with, and its enum comes from the digest's ids. ``trade_offer`` only looks (GET, never an action, so it
is not in ``acting``); ``trade`` acts:

    trade_offer(with)                  GET  /trade/offer?with=ID        -> a compact price list
    trade(with, buy, sell, why)        POST /act/trade {with, buy, sell}

An optional text may be empty. Accepting or declining a quest stays with ``choose``; ``player_asked`` is never sent.
"""
from __future__ import annotations

import re

from ...core import gamedata as gd
from ...core.toolloop import Param, ToolSpec

WHY_MAX = 80
_WHY = Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since components are low); "
             "it is shown to the player beside what you did", max_len=WHY_MAX, required=False, same_call=True)   # as steward_tools._WHY (a copy: importing it would be circular)

LOOKS = ("trade_offer",)
ACTS = ("trade",)
MAX_LINES, MAX_COUNT = 12, 99999
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


class TradeKit:
    """The trade tools for one digest, the body of the acting one and the look request."""

    def __init__(self, digest: dict):
        t = digest.get("trade")
        t = t if isinstance(t, dict) else None
        self.has = t is not None
        t = t or {}
        self.partners = {i: x for x in t.get("traders") or [] if (i := _id(x))}       # who we can deal with: the visiting traders
        self.tools = self._tools() if self.has else []
        self.names = {x.name for x in self.tools}
        self.acting = self.names & set(ACTS)

    # ---- the tools ------------------------------------------------------------------
    def _with_param(self) -> Param:
        names = ", ".join(f"{i} {_short(x.get('name'), 14)}" for i, x in self.partners.items())
        return Param("with", "enum", f"who ({names})", choices=list(self.partners))

    def _tools(self) -> list[ToolSpec]:
        if not self.partners:
            return []
        return [ToolSpec("trade_offer", "Look at one trader's prices (not an action); do this before trade.", [self._with_param()]),
                ToolSpec("trade", "Buy and sell with one trader at once; final.",
                         [self._with_param(),
                          Param("buy", "text", "items to buy: cloth x20, steel x100 (none if unused)", max_len=200, required=False),
                          Param("sell", "text", "items to sell, same form (none if unused)", max_len=200, required=False), _WHY])]

    # ---- the body -------------------------------------------------------------------
    def _partner(self, v: dict) -> str:
        w = str(v.get("with") or "")
        if w not in self.partners:
            raise ValueError("no such trader; you can trade with: " + ", ".join(self.partners))
        return w

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

    def body(self, tool: str, v: dict) -> tuple[str, dict]:
        verb, b = getattr(self, "_" + tool)(v)
        why = gd.clean(str(v.get("why") or ""), "why", max_len=WHY_MAX).strip()
        if why:
            b["why"] = why
        return verb, b

    # ---- the look (GET, no action) ----------------------------------------------------
    def look_request(self, tool: str, v: dict) -> tuple[str, dict]:
        """(path, query params) of the look tool call; ValueError for text that does not resolve."""
        return "/trade/offer", {"with": self._partner(v)}

    async def look(self, tool: str, v: dict, bridge) -> str:
        path, q = self.look_request(tool, v)
        return render_offer(await bridge.look(path, q))

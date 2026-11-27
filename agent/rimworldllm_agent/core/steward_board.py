"""The steward's goals-board tools (game-agnostic): goals, the plan, standing orders, a chat note, do nothing.

The board lives in the mod (``GET/PUT /goals``, saved with the game). During a cycle the tools only record edits;
``Edits.apply`` replays them on a board and gives the fields of the one ``PUT /goals`` that ends the cycle. A stale
revision (409) is handled by fetching the board again and replaying the same edits.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

from .toolloop import Param, ToolSpec

MAX_GOALS = 20
MAX_ORDERS = 20
TEXT_MAX = 300
BOARD_TOOLS = ("add_goal", "set_goal", "set_plan", "add_order", "drop_order", "note", "do_nothing")

_CONTROL = re.compile("[\x00-\x1f\x7f-\x9f]")


def plain(text: object, cap: int = TEXT_MAX) -> str:
    """Player or steward text for a prompt or the board: control characters out, markup characters softened, one line.
    Not the game-data filter: this is the player's own voice and the steward's own notes."""
    t = _CONTROL.sub(" ", str(text or "")).translate({ord("<"): "(", ord(">"): ")", ord("{"): "(", ord("}"): ")"})
    t = " ".join(t.split())
    return t if len(t) <= cap else t[:cap - 1].rstrip() + "…"


def board_tools(board: dict, advisor: bool = False) -> list[ToolSpec]:
    open_goals = [g["id"] for g in board.get("goals") or [] if g.get("status", "open") == "open" and g.get("id")]
    orders = [o["id"] for o in board.get("orders") or [] if o.get("id")]
    out = [
        ToolSpec("add_goal", "Add a goal to your goals board: a long-term one or a short-term one.",
                 [Param("text", "text", "the goal, one sentence", max_len=TEXT_MAX), Param("horizon", "enum", "long or short term", choices=["short", "long"])]),
    ]
    if open_goals:
        out.append(ToolSpec("set_goal", "Mark one of your open goals done or dropped.",
                            [Param("goal_id", "enum", "the goal's id", choices=open_goals), Param("status", "enum", "done or dropped", choices=["done", "dropped"])]))
    out += [
        ToolSpec("set_plan", "Replace your current plan, a few sentences on what you will do next.", [Param("text", "text", "the plan", max_len=TEXT_MAX)]),
        ToolSpec("add_order", "Record a standing order the player gave you, in their words, so it is kept and obeyed.",
                 [Param("text", "text", "the standing order", max_len=TEXT_MAX)]),
    ]
    if orders:
        out.append(ToolSpec("drop_order", "Remove a standing order the player has withdrawn.", [Param("order_id", "enum", "the order's id", choices=orders)]))
    out += [
        ToolSpec("note", "Post a short action note in the chat now, such as 'set the cook to priority 1'. At most three per turn.",
                 [Param("text", "text", "the note, a few words", max_len=120)]),
        ToolSpec("do_nothing", "Do nothing this turn.", [], ends_turn=True),
    ]
    return out


@dataclass
class Edits:
    """Recorded board edits: (op, ...) tuples, replayed on any board."""
    ops: list[tuple] = field(default_factory=list)

    def __bool__(self) -> bool:
        return bool(self.ops)

    def apply(self, board: dict) -> dict:
        """The PUT fields (goals / plan / orders, only the parts the edits touched) these edits make on ``board``.
        Raises ValueError for an edit the board cannot take (full, unknown id)."""
        goals = [dict(g) for g in board.get("goals") or []]
        orders = [dict(o) for o in board.get("orders") or []]
        plan = None
        touched: set[str] = set()
        for op in self.ops:
            kind = op[0]
            if kind == "add_goal":
                if len(goals) >= MAX_GOALS:
                    goals = _drop_oldest_finished(goals)
                if len(goals) >= MAX_GOALS:
                    raise ValueError(f"the goals board is full ({MAX_GOALS} open goals): mark some done or dropped first")
                goals.append({"text": op[1], "horizon": op[2], "status": "open"})
                touched.add("goals")
            elif kind == "set_goal":
                g = next((g for g in goals if g.get("id") == op[1]), None)
                if g is None:
                    raise ValueError(f"no goal {op[1]}")
                g["status"] = op[2]
                touched.add("goals")
            elif kind == "set_plan":
                plan = op[1]
                touched.add("plan")
            elif kind == "add_order":
                if len(orders) >= MAX_ORDERS:
                    raise ValueError(f"there are already {MAX_ORDERS} standing orders: drop one first")
                orders.append({"text": op[1], "by": "player"})
                touched.add("orders")
            elif kind == "drop_order":
                if not any(o.get("id") == op[1] for o in orders):
                    raise ValueError(f"no order {op[1]}")
                orders = [o for o in orders if o.get("id") != op[1]]
                touched.add("orders")
        out: dict = {}
        if "goals" in touched:
            out["goals"] = goals
        if "plan" in touched:
            out["plan"] = plan
        if "orders" in touched:
            out["orders"] = orders
        return out

    def try_add(self, board: dict, op: tuple) -> str | None:
        """Record ``op`` if the board can take it on top of the earlier edits; else the reason."""
        trial = Edits(self.ops + [op])
        try:
            trial.apply(board)
        except ValueError as e:
            return str(e)
        self.ops.append(op)
        return None


def _drop_oldest_finished(goals: list[dict]) -> list[dict]:
    for i, g in enumerate(goals):
        if g.get("status") in ("done", "dropped"):
            return goals[:i] + goals[i + 1:]
    return goals


def render_board(board: dict, log_lines: int = 6) -> str:
    out = []
    goals = board.get("goals") or []
    out.append("Goals: " + ("" if goals else "none yet"))
    for g in goals:
        out.append(f"- {g.get('id', '?')} [{g.get('horizon', 'short')}, {g.get('status', 'open')}] {plain(g.get('text', ''))}")
    out.append("Plan: " + (plain(board.get("plan", "")) or "none yet"))
    orders = board.get("orders") or []
    out.append("Standing orders from the player (they override your own plans): " + ("" if orders else "none"))
    for o in orders:
        out.append(f"- {o.get('id', '?')}: {plain(o.get('text', ''))}")
    log = (board.get("log") or [])[-log_lines:]
    if log:
        out.append("What you did lately:")
        out.extend(f"- {plain(x.get('text', ''), 200)}" for x in log)
    return "\n".join(out)

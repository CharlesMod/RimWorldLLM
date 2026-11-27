"""Playbook rules for the steward: ``strategy/rules.json`` (if-then rules over digest facts) evaluated safely, no eval.

Each rule's condition is parsed with ``ast.parse(mode="eval")`` and checked by a strict whitelist (boolean and/or/not,
comparisons, ``in`` / ``not in``, field names, constants, lists of constants, + - * / on numbers). Nothing else compiles:
no calls, attributes, subscripts, lambdas, comprehensions or dunder names. A rule that does not compile is dropped with a
log line. Evaluation walks the checked tree itself; code objects are never built or run.

A field that is missing (or None where a number is needed) makes the rule *skip* (three-valued: a False operand still
decides an ``and``, a True operand an ``or``). ``true`` conditions are always-on guidance and belong to the static prompt,
so they are never in the per-cycle list.
"""
from __future__ import annotations

import ast
import json
import logging
import operator
from functools import lru_cache
from pathlib import Path

log = logging.getLogger("rimworldllm.steward.rules")

RULES_PATH = Path(__file__).parent / "strategy" / "rules.json"
MAX_RULES = 8
SECTION_CAP = 1500
HEADING = "=== PLAYBOOK ALERTS ==="
_CONSTS = {"true": True, "True": True, "false": False, "False": False, "None": None, "null": None}
_CMP = {ast.Lt: operator.lt, ast.LtE: operator.le, ast.Gt: operator.gt, ast.GtE: operator.ge, ast.Eq: operator.eq, ast.NotEq: operator.ne}
_ARITH = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv}


class _Skip(Exception):
    """A needed field is missing or None: the rule does not fire."""


class RuleError(ValueError):
    pass


def _num(x) -> bool:
    return isinstance(x, (int, float)) and not isinstance(x, bool)


def _check(n: ast.AST) -> None:
    """Raise RuleError unless the tree is made only of the allowed nodes."""
    if isinstance(n, ast.Expression):
        return _check(n.body)
    if isinstance(n, ast.BoolOp):
        for v in n.values:
            _check(v)
        return None
    if isinstance(n, ast.UnaryOp):
        if not isinstance(n.op, (ast.Not, ast.USub)):
            raise RuleError("unary operator not allowed")
        return _check(n.operand)
    if isinstance(n, ast.Compare):
        for op in n.ops:
            if not isinstance(op, tuple(_CMP) + (ast.In, ast.NotIn)):
                raise RuleError("comparison not allowed")
        _check(n.left)
        for c in n.comparators:
            _check(c)
        return None
    if isinstance(n, ast.BinOp):
        if not isinstance(n.op, tuple(_ARITH)):
            raise RuleError("arithmetic operator not allowed")
        _check(n.left)
        return _check(n.right)
    if isinstance(n, ast.Name):
        if n.id.startswith("__"):
            raise RuleError("dunder name")
        return None
    if isinstance(n, ast.Constant):
        if n.value is not None and not isinstance(n.value, (int, float, str, bool)):
            raise RuleError("constant type not allowed")
        return None
    if isinstance(n, (ast.List, ast.Tuple)):
        for e in n.elts:
            if not isinstance(e, ast.Constant):
                raise RuleError("lists hold constants only")
            _check(e)
        return None
    raise RuleError(f"{type(n).__name__} not allowed")


def compile_condition(cond: str):
    """A function ``facts -> True | False | None`` (None = skipped). RuleError if the condition is not whitelisted."""
    if not isinstance(cond, str) or not cond.strip() or len(cond) > 400:
        raise RuleError("empty or too long")
    try:
        tree = ast.parse(cond.strip(), mode="eval")
    except SyntaxError as e:
        raise RuleError(f"syntax: {e.msg}") from e
    _check(tree)
    body = tree.body
    always = isinstance(body, ast.Name) and body.id in ("true", "True")

    def ev(n, f):
        if isinstance(n, ast.Constant):
            return n.value
        if isinstance(n, ast.Name):
            if n.id in _CONSTS:
                return _CONSTS[n.id]
            if n.id not in f:
                raise _Skip
            return f[n.id]
        if isinstance(n, (ast.List, ast.Tuple)):
            return [e.value for e in n.elts]
        if isinstance(n, ast.UnaryOp):
            v = ev(n.operand, f)
            if isinstance(n.op, ast.Not):
                return not v
            if not _num(v):
                raise _Skip
            return -v
        if isinstance(n, ast.BinOp):
            a, b = ev(n.left, f), ev(n.right, f)
            if not (_num(a) and _num(b)):
                raise _Skip
            try:
                return _ARITH[type(n.op)](a, b)
            except ZeroDivisionError:
                raise _Skip from None
        if isinstance(n, ast.BoolOp):
            want_and = isinstance(n.op, ast.And)
            skipped = False
            for v in n.values:
                try:
                    r = bool(ev(v, f))
                except _Skip:
                    skipped = True
                    continue
                if r != want_and:                       # a False in an and / a True in an or decides it
                    return r
            if skipped:
                raise _Skip
            return want_and
        if isinstance(n, ast.Compare):
            left = ev(n.left, f)
            for op, c in zip(n.ops, n.comparators):
                right = ev(c, f)
                if isinstance(op, (ast.In, ast.NotIn)):
                    if not isinstance(right, (list, tuple)) or left is None:
                        raise _Skip
                    ok = (left in right) if isinstance(op, ast.In) else (left not in right)
                elif isinstance(op, (ast.Eq, ast.NotEq)):
                    ok = _CMP[type(op)](left, right)
                else:
                    if not (_num(left) and _num(right)):
                        raise _Skip
                    ok = _CMP[type(op)](left, right)
                if not ok:
                    return False
                left = right
            return True
        raise _Skip

    def run(facts: dict):
        try:
            return bool(ev(body, facts))
        except _Skip:
            return None
        except (TypeError, ValueError):
            return None

    run.always_on = always
    run.research_names = _research_names(tree)
    return run


_RESEARCH_FIELDS = ("research_done", "research_current")


def _research_names(tree: ast.AST) -> frozenset:
    """The research defNames a condition names: string constants compared with research_done / research_current."""
    names = set()
    for n in ast.walk(tree):
        if not isinstance(n, ast.Compare):
            continue
        sides = [n.left, *n.comparators]
        if not any(isinstance(x, ast.Name) and x.id in _RESEARCH_FIELDS for x in sides):
            continue
        for x in sides:
            if isinstance(x, ast.Constant) and isinstance(x.value, str):
                names.add(x.value)
            elif isinstance(x, (ast.List, ast.Tuple)):
                names.update(e.value for e in x.elts if isinstance(e, ast.Constant) and isinstance(e.value, str))
    return frozenset(names)


_warned_research: set = set()


def unknown_research(names: frozenset, facts: dict) -> frozenset:
    """Names the game does not define, judged by facts["research_known"] (every ResearchProjectDef defName, sent by the
    mod). Without that list nothing can be judged and nothing is unknown. Each miss is logged once."""
    known = facts.get("research_known")
    if not names or not isinstance(known, (list, tuple)):
        return frozenset()
    miss = frozenset(n for n in names if n not in set(known))
    for n in miss - _warned_research:
        _warned_research.add(n)
        log.warning("playbook rules name research %r, which this game does not define; those rules stay off", n)
    return miss


@lru_cache(maxsize=1)
def load_rules() -> tuple:
    """The shipped rules as ``(id, priority, action, run, always_on)`` tuples; bad ones are dropped with a log line."""
    try:
        data = json.loads(RULES_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        log.warning("playbook rules not loaded: %s", e)
        return ()
    out = []
    for r in data.get("rules") or []:
        try:
            run = compile_condition(r["condition"])
            out.append((str(r["id"]), int(r["priority"]), str(r["action"]).strip(), run, run.always_on))
        except (RuleError, KeyError, TypeError, ValueError) as e:
            log.warning("playbook rule %s dropped: %s", r.get("id") if isinstance(r, dict) else "?", e)
    return tuple(out)


# ---- facts ---------------------------------------------------------------------------------
def _resource(res: list, label: str) -> int | None:
    tot, seen = 0, False
    for r in res:
        if isinstance(r, dict) and str(r.get("label", "")).strip().lower() == label and (_num(r.get("count")) or _num(r.get("loose"))):
            # stored plus loose (forbidden piles are the player's call and stay out)
            tot += int(r["count"] if _num(r.get("count")) else 0) + int(r["loose"] if _num(r.get("loose")) else 0)
            seen = True
    return tot if seen else None


def derive_facts(digest: dict) -> dict:
    """digest["facts"] (the mod's block) first, then gaps filled from the rest of the digest. Never invents a value:
    what cannot be read stays absent. (digest date.day is the day of the quadrum, so ``day`` is never derived.)"""
    f: dict = {}
    given = digest.get("facts")
    if isinstance(given, dict):
        f.update({k: v for k, v in given.items() if isinstance(k, str)})

    def fill(k, v):
        if v is not None and k not in f:
            f[k] = v

    fill("food_days", digest.get("food_days") if _num(digest.get("food_days")) else None)
    pawns = [p for p in digest.get("pawns") or [] if isinstance(p, dict)]
    cols = [p for p in pawns if str(p.get("role", "colonist")).lower() == "colonist"]
    if pawns:
        fill("colonists", len(cols))
        moods = [p["mood"] for p in cols if _num(p.get("mood"))]
        if moods:
            fill("mood_avg", round(sum(moods) / len(moods), 1))
            fill("mood_min", min(moods))
        fill("colonists_downed", sum(1 for p in cols if p.get("downed")))
    if isinstance(digest.get("beds"), list):
        beds = [b for b in digest["beds"] if isinstance(b, dict)]
        fill("beds_free", sum(1 for b in beds if not b.get("owner")))
    res = digest.get("resources")
    if isinstance(res, list):
        for k in ("steel", "wood", "components", "silver"):
            fill(k, _resource(res, k))
        med = [r for r in res if isinstance(r, dict) and str(r.get("category", "")).lower() == "medicine" and _num(r.get("count"))]
        if med:
            fill("medicine_total", sum(int(r["count"]) for r in med))
            fill("medicine_industrial", sum(int(r["count"]) for r in med if "herbal" not in str(r.get("label", "")).lower()))
    rs = digest.get("research")
    if isinstance(rs, dict) and "current" in rs:
        cur = rs.get("current")
        fill("research_current", cur.get("def") or cur.get("label") if isinstance(cur, dict) else None)
        if cur is None:
            f.setdefault("research_current", None)
    tr = digest.get("trade")
    if isinstance(tr, dict):
        fill("traders_ready", sum(1 for t in tr.get("traders") or [] if isinstance(t, dict) and t.get("can_trade", True)))
        fill("trade_needs", len([n for n in tr.get("needs") or [] if n]))
    qs = digest.get("quests")
    if isinstance(qs, list):
        fill("quest_offer_can", any(isinstance(q, dict) and q.get("state") == "offer" and q.get("can") is True for q in qs))
        fill("quest_offer_cannot", any(isinstance(q, dict) and q.get("state") == "offer" and q.get("can") is False for q in qs))
    th = digest.get("threats")
    if isinstance(th, list):
        n = sum(int(t.get("count") or 0) for t in th if isinstance(t, dict) and t.get("hostile", True) and _num(t.get("count")))
        fill("enemies_count", n)
        fill("raid_active", n > 0)
    al = digest.get("alerts")
    if isinstance(al, list):
        fill("fires", sum(1 for a in al if isinstance(a, dict) and ("fire" in str(a.get("alert", "")).lower() or str(a.get("label", "")).lower().startswith("fire"))))
    dt = digest.get("date") or {}
    season = str(dt.get("season", "")).strip().lower()
    season = {"autumn": "fall"}.get(season, season)
    if season in ("spring", "summer", "fall", "winter"):
        fill("season", season)
    if _num(digest.get("temperature")):
        fill("temp_c", digest["temperature"])
    pw = digest.get("power")
    if isinstance(pw, dict):
        if _num(pw.get("produced")) and _num(pw.get("used")):
            fill("power_net_w", pw["produced"] - pw["used"])
        if _num(pw.get("stored")):
            fill("battery_wd", pw["stored"])
    return f


# ---- selection and rendering ---------------------------------------------------------------
def fired_rules(facts: dict) -> list[tuple[int, str, str]]:
    """(priority, id, action) of the rules that fire on these facts, always-on ones left out, sorted."""
    out = []
    for rid, pri, action, run, always in load_rules():
        if always:
            continue
        if unknown_research(getattr(run, "research_names", frozenset()), facts):
            continue                                    # a research name the game lacks: the condition counts as false
        if run(facts) is True:
            out.append((pri, rid, action))
    out.sort(key=lambda t: (t[0], t[1]))
    return out


def select(fired: list[tuple[int, str, str]], limit: int = MAX_RULES) -> list[tuple[int, str, str]]:
    """All priority-1 rules, then the rest in order, at most ``limit`` in all (a priority-1 rule is never dropped)."""
    p1 = [r for r in fired if r[0] <= 1]
    rest = [r for r in fired if r[0] > 1]
    return p1 + rest[:max(0, limit - len(p1))]


def _trim(text: str, n: int) -> str:
    if len(text) <= n:
        return text
    return text[:max(n - 1, 1)].rsplit(" ", 1)[0].rstrip(" ,;:.") + "…"


def alerts_section(digest: dict, cap: int = SECTION_CAP) -> str:
    """The "=== PLAYBOOK ALERTS ===" block for the user turn, or "" when no rule fires. At most ``cap`` characters:
    lower-priority lines go first, then priority-1 actions are shortened."""
    rules = select(fired_rules(derive_facts(digest)))
    if not rules:
        return ""
    head = HEADING + "\n"

    def line(r, n=None):
        return f"[P{r[0]} {r[1]}] " + (_trim(r[2], n) if n else r[2])

    while len(head) + sum(len(line(r)) + 1 for r in rules) > cap and any(r[0] > 1 for r in rules):
        for i in range(len(rules) - 1, -1, -1):
            if rules[i][0] > 1:
                del rules[i]
                break
    budget = cap - len(head)
    body = [line(r) for r in rules]
    if sum(len(b) + 1 for b in body) > budget:
        per = max(40, budget // len(rules) - 1 - 20)
        body = [line(r, per) for r in rules]
    return head + "\n".join(body) + "\n"

"""Renders the bridge's colony digest (GET /digest) as the compact text the steward reads (about 6 KB at most).

Names, not ids, except where a tool needs an id: letters and beds. Every game string goes through core/gamedata.py
(it is data, never an instruction). When the text is over the cap, detail goes in a fixed order (see ``_LEVELS``),
and as a last resort the text is cut with a marker.
"""
from __future__ import annotations

from ...core import gamedata as gd

CAP = 6000
_SCHED_WORD = {"A": "any", "W": "work", "J": "joy", "S": "sleep", "M": "meditate"}

# detail levels, most to least: skills per pawn, resources, research options, beds, letter text chars, thoughts of schedule per pawn
_LEVELS = [
    dict(skills=4, resources=25, research=15, beds=20, letter=280, alerts=10),
    dict(skills=3, resources=16, research=8, beds=12, letter=160, alerts=8),
    dict(skills=2, resources=10, research=5, beds=8, letter=100, alerts=5),
    dict(skills=1, resources=6, research=3, beds=4, letter=60, alerts=3),
]


def pawn_names(digest: dict) -> dict[str, int]:
    """Cleaned display name -> pawn id, for the colonists and slaves the digest lists (the first one wins a duplicate)."""
    out: dict[str, int] = {}
    for p in digest.get("pawns") or []:
        try:
            out.setdefault(gd.name(p.get("name")), int(p["id"]))
        except (KeyError, TypeError, ValueError):
            continue
    return out


def _runs(sched: str) -> str:
    """'SSSSSSWWWW...' -> 'sleep 0-6, work 6-20, ...'."""
    if not sched:
        return ""
    out, start = [], 0
    for i in range(1, len(sched) + 1):
        if i == len(sched) or sched[i] != sched[start]:
            out.append(f"{_SCHED_WORD.get(sched[start], sched[start])} {start}-{i}")
            start = i
    return ", ".join(out)


def _date(d: dict) -> str:
    dt = d.get("date") or {}
    bits = [f"Day {dt.get('day', '?')} of {gd.clean(dt.get('quadrum', ''), 'label')} {dt.get('year', '')}".strip(),
            f"{dt.get('hour', '?')}:00", gd.clean(dt.get("season", ""), "label")]
    wx = gd.clean(d.get("weather", ""), "label")
    temp = d.get("temperature")
    if wx:
        bits.append(wx + (f" {temp:g}C" if isinstance(temp, (int, float)) else ""))
    if d.get("map"):
        bits.append("map " + gd.clean(d["map"], "name"))
    tm = d.get("time") or {}
    bits.append(("paused" if tm.get("paused") else "running") + f", speed {tm.get('speed', '?')}")
    return ", ".join(b for b in bits if b)


def _pawn_line(p: dict, lv: dict, common: str) -> str:
    name = gd.name(p.get("name"))
    head = f"- {name}"
    if p.get("role") and p["role"] != "colonist":
        head += f" ({gd.clean(p['role'], 'label')})"
    bits = []
    if p.get("age") is not None:
        bits.append(f"{p['age']}y")
    bits.append(f"mood {p.get('mood', '?')}")
    h = gd.clean(p.get("health", ""), "label") or "fine"
    bits.append(("DOWNED, " if p.get("downed") else "") + h)
    if p.get("mental"):
        bits.append("mental break: " + gd.clean(p["mental"], "label"))
    if p.get("drafted"):
        bits.append("DRAFTED")
    if p.get("job"):
        bits.append("doing " + gd.clean(p["job"], "label"))
    skills = [f"{gd.clean(s.get('skill', ''), 'label')} {s.get('level', '?')}" + {"major": "++", "minor": "+"}.get(s.get("passion"), "")
              for s in (p.get("skills") or [])[:lv["skills"]]]
    lines = [head + ": " + ", ".join(bits)]
    detail = []
    if skills:
        detail.append("skills " + ", ".join(skills))
    work = p.get("work") or {}
    detail.append("work " + (", ".join(f"{gd.clean(k, 'label')} {v}" for k, v in sorted(work.items(), key=lambda kv: (kv[1], kv[0]))) or "none"))
    if p.get("incapable"):
        detail.append("cannot " + ", ".join(gd.clean(x, "label") for x in p["incapable"][:4]))
    pol = [f"{k} {gd.clean(p[k], 'label')}" for k in ("outfit", "food", "drug") if p.get(k)]
    if pol:
        detail.append(", ".join(pol))
    detail.append("bed " + (f"#{p['bed']}" if p.get("bed") else "none"))
    sched = p.get("schedule") or ""
    if sched and sched != common:
        detail.append("schedule " + _runs(sched))
    lines.append("    " + "; ".join(detail))
    return "\n".join(lines)


def _render(d: dict, lv: dict) -> str:
    out = ["DATE: " + _date(d)]
    fd = d.get("food_days")
    if fd is not None:
        out.append(f"FOOD: about {fd:g} days of food" if isinstance(fd, (int, float)) else f"FOOD: {fd}")
    res = (d.get("resources") or [])[:lv["resources"]]
    if res:
        cats: dict[str, list[str]] = {}
        for r in res:
            cats.setdefault(gd.clean(r.get("category", "other"), "label"), []).append(f"{gd.clean(r.get('label', ''), 'label')} {r.get('count', '?')}")
        out.append("RESOURCES: " + "; ".join(f"{c}: {', '.join(v)}" for c, v in cats.items()))
    pawns = d.get("pawns") or []
    scheds = [p.get("schedule") for p in pawns if p.get("schedule")]
    common = max(set(scheds), key=scheds.count) if scheds else ""
    out.append(f"PEOPLE ({len(pawns)}):")
    out.extend(_pawn_line(p, lv, common) for p in pawns)
    if common:
        out.append("Schedule of everyone not listed otherwise: " + _runs(common))
    wt = d.get("work_types") or []
    if wt:
        out.append("Work types: " + ", ".join(gd.clean(w, "label") for w in wt) + f". Manual priorities: {'on' if d.get('manual_priorities') else 'off'} (1 is highest, 0 is off).")
    pol = d.get("policies") or {}
    if pol:
        out.append("POLICIES: " + "; ".join(f"{k}: " + ", ".join(gd.clean(x, 'label') for x in v[:8]) for k, v in pol.items() if v))
    beds = (d.get("beds") or [])[:lv["beds"]]
    if beds:
        out.append("BEDS: " + "; ".join(f"#{b.get('id')} {gd.clean(b.get('label', 'bed'), 'label')}" +
                                        (f" in {gd.clean(b['room'], 'label')}" if b.get("room") else "") +
                                        (f" ({gd.clean(b['owner'], 'name')})" if b.get("owner") else " (free)") for b in beds))
    al = (d.get("alerts") or [])[:lv["alerts"]]
    out.append("ALERTS: " + ("; ".join(gd.clean(a.get("label", ""), "label") + (" [CRITICAL]" if a.get("critical") else "") for a in al) or "none"))
    th = d.get("threats") or []
    out.append("THREATS: " + ("; ".join(f"{t.get('count', '?')} {gd.clean(t.get('label', ''), 'label')}" + ("" if t.get("hostile", True) else " (not hostile)") for t in th[:6]) or "none"))
    lt = (d.get("letters") or [])[:10]
    if lt:
        out.append("LETTERS NEEDING AN ANSWER:")
        for l in lt:
            ch = ", ".join(gd.clean(c, "label") for c in l.get("choices") or []) or "no choice, can be dismissed"
            ex = f", expires in {l['expires_in_hours']} h" if l.get("expires_in_hours") is not None else ""
            out.append(f"- letter #{l.get('id')}: {gd.clean(l.get('label', ''), 'label')}{' (quest)' if l.get('quest') else ''} [choices: {ch}]{ex}")
            if l.get("text") and lv["letter"]:
                out.append("    " + gd.clean(l["text"], max_len=lv["letter"]))
    rs = d.get("research") or {}
    cur = rs.get("current")
    avail = (rs.get("available") or [])[:lv["research"]]
    out.append("RESEARCH: " + (f"now {gd.clean(cur.get('label', ''), 'label')} ({round(float(cur.get('progress', 0)) * 100)}%)" if cur else "nothing selected") +
               ("; available: " + ", ".join(f"{gd.clean(a.get('label', ''), 'label')} ({a.get('cost', '?')})" for a in avail) if avail else ""))
    rm = d.get("rooms") or {}
    if rm:
        out.append("ROOMS: " + ", ".join(f"{k} {'yes' if v is True else 'no' if v is False else v}" for k, v in rm.items()))
    pw = d.get("power")
    if pw:
        out.append(f"POWER: produced {pw.get('produced', 0)}, used {pw.get('used', 0)}, stored {pw.get('stored', 0)}")
    return "\n".join(out)


def render_digest(d: dict, cap: int = CAP) -> str:
    """The digest as compact text of at most ``cap`` characters (detail shrinks level by level, then a hard cut)."""
    text = ""
    for lv in _LEVELS:
        text = _render(d, lv)
        if len(text) <= cap:
            return text
    return text[:cap - 40].rsplit("\n", 1)[0] + "\n(digest cut: too long)"

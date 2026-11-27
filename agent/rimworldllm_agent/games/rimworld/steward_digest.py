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
# the build block's detail (it shrinks first): rooms, benches, bills per bench, recipes per bench, zones, can_build, stuff,
# plants, ores. The last step keeps just BUILD/missing and a short ROOMS.
_BUILD = [
    dict(rooms=12, benches=8, bills=5, recipes=8, zones=10, can_build=40, stuff=8, plants=10, ores=6, loose=6),
    dict(rooms=8, benches=5, bills=3, recipes=4, zones=6, can_build=20, stuff=6, plants=6, ores=4, loose=4),
    dict(rooms=5, benches=3, bills=2, recipes=0, zones=4, can_build=0, stuff=4, plants=0, ores=2, loose=2),
    dict(rooms=3, benches=0, bills=0, recipes=0, zones=0, can_build=0, stuff=0, plants=0, ores=0, loose=0),
]
# the Biotech block (bio): per-mech and genepack lists go first, then the detail lines, then all but the urgent facts
_BIO = [
    dict(mechs=8, packs=8, genes=6, detail=1, urgent=0),
    dict(mechs=0, packs=0, genes=4, detail=1, urgent=0),
    dict(mechs=0, packs=0, genes=0, detail=0, urgent=0),
    dict(mechs=0, packs=0, genes=0, detail=0, urgent=1),
]
# (base level, build level, bio level): the build block shrinks all the way, then the bio block, before the rest loses detail
_STEPS = [(0, 0, 0), (0, 1, 0), (0, 2, 0), (0, 3, 0), (0, 3, 1), (0, 3, 2), (0, 3, 3), (1, 3, 3), (2, 3, 3), (3, 3, 3)]


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


def _num(v: object) -> str:
    return f"{v:g}" if isinstance(v, (int, float)) and not isinstance(v, bool) else gd.clean(v, "label") or "?"


def _words(items: object, n: int) -> list[str]:
    return [w for w in (gd.clean(x, "label") for x in (items if isinstance(items, list) else [])[:n]) if w]


def _room_line(r: dict) -> str:
    role = gd.clean(r.get("role", ""), "label")
    label = gd.clean(r.get("label", ""), "label") or role
    bits = [f"#{_num(r.get('id'))} {label}" + (f" ({role})" if role and role.lower() not in label.lower() else ""),
            f"{_num(r.get('w'))}x{_num(r.get('h'))}"]
    if r.get("impressiveness") is not None:
        bits.append(f"imp {_num(r['impressiveness'])}")
    bits += [w for k, w in (("bed", "bed"), ("door", "door"), ("roofed", "roofed")) if r.get(k)]
    return " ".join(bits)


def _build_lines(b: dict, bl: dict, counts: str) -> list[str]:
    """The BUILD block lines; ``counts`` is the old one-line room counts, merged into the ROOMS line."""
    out = []
    home = b.get("home") or {}
    head = "BUILD: " + (f"home ({_num(home.get('x'))},{_num(home.get('z'))})" if home else "home unknown")
    miss = _words(b.get("missing"), 8)
    head += "; missing: " + (", ".join(miss) if miss else "nothing")
    out.append(head)
    rooms = [r for r in (b.get("rooms") or [])[:bl["rooms"]] if isinstance(r, dict)]
    if rooms:
        out.append("ROOMS" + (f" ({counts})" if counts else "") + ": " + "; ".join(_room_line(r) for r in rooms))
    benches = [x for x in (b.get("benches") or [])[:bl["benches"]] if isinstance(x, dict)]
    for x in benches:
        line = f"BENCH: #{_num(x.get('id'))} {gd.clean(x.get('label', ''), 'label')}"
        if x.get("room"):
            line += f" ({gd.clean(x['room'], 'label')})"
        bills = _words(x.get("bills"), bl["bills"])
        recipes = _words(x.get("recipes"), bl["recipes"])
        line += ": bills " + (", ".join(bills) if bills else "none")
        if recipes:
            line += "; recipes " + ", ".join(recipes)
        out.append(line)
    zones = [z for z in (b.get("zones") or [])[:bl["zones"]] if isinstance(z, dict)]
    if zones:
        zs = []
        for z in zones:
            kind = gd.clean(z.get("kind", ""), "label")
            what = gd.clean(z.get("plant") or z.get("preset") or "", "label")
            zs.append(f"#{_num(z.get('id'))} {kind} {what} {_num(z.get('cells'))} cells".replace("  ", " "))
        out.append("ZONES: " + "; ".join(zs))
    for key, tag in (("can_build", "CAN BUILD"), ("plants", "PLANTS")):
        w = _words(b.get(key), bl[key])
        if w:
            out.append(f"{tag}: " + ", ".join(w))
    stuff = [s for s in (b.get("stuff") or [])[:bl["stuff"]] if isinstance(s, dict)]
    if stuff:
        out.append("STUFF: " + ", ".join(f"{gd.clean(s.get('stuff', ''), 'label')} {_num(s.get('count'))}" for s in stuff))
    ores = [o for o in (b.get("ores") or [])[:bl["ores"]] if isinstance(o, dict)]
    if ores:
        out.append("ORES: " + "; ".join(f"{gd.clean(o.get('ore', ''), 'label')} {_num(o.get('cells'))} cells {_num(o.get('dist'))} away" for o in ores))
    piles = [p for p in (b.get("loose") or [])[:bl["loose"]] if isinstance(p, dict)]
    if piles:
        out.append("LOOSE: " + "; ".join(_pile(p) for p in piles))
    return out


def _bio_lines(b: dict, bl: dict, ages: dict | None = None) -> list[str]:
    """The BIOTECH lines (digest ``bio``, contract A): mechs, pollution, genes, blood, genepacks, children, babies, pregnancies.
    Every key may be missing. Pregnancies are plain facts for adults only (a child's pawn id or an age under 18 drops the entry)."""
    c = lambda v: gd.clean(str(v if v is not None else ""), "label")  # noqa: E731
    L = lambda k: [x for x in (b.get(k) if isinstance(b.get(k), list) else []) if isinstance(x, dict)]  # noqa: E731
    out: list[str] = []
    kids = L("children")
    urgent = bl["urgent"]
    mech_bits = []
    for m in L("mechanitors"):
        t = f"{c(m.get('name'))} bandwidth {_num(m.get('bandwidth_used'))}/{_num(m.get('bandwidth_total'))}"
        if m.get("gestating_bandwidth"):
            t += f" ({_num(m['gestating_bandwidth'])} gestating)"
        gs = [f"group {_num(g.get('group'))} {c(g.get('mode'))} ({_num(g.get('mechs'))})" for g in (m.get("groups") or []) if isinstance(g, dict)]
        mech_bits.append(t + ("; " + ", ".join(gs) if gs else ""))
    for m in L("mechs")[:bl["mechs"]]:
        mech_bits.append(f"{c(m.get('name'))} " + (c(m["problem"]) if m.get("problem") else
                         " ".join(x for x in (c(m.get("job")), f"{_num(m['energy'])}%" if m.get("energy") is not None else "") if x)))
    if mech_bits and not urgent:
        out.append("MECHS: " + "; ".join(x.strip() for x in mech_bits))
    if bl["detail"]:
        gest = [f"{c(g.get('label'))} {c(g.get('state'))}" for g in L("gestators")]
        rech = [f"{c(r.get('label'))} {c(r.get('state'))}" + (f" ({c(r['waste'])})" if r.get("waste") else "") for r in L("rechargers")]
        if gest or rech:
            out.append("GESTATORS: " + ("; ".join(gest) or "none") + ". RECHARGERS: " + ("; ".join(rech) or "none"))
        cg = [f"{c(k.get('kind'))} {_num(k.get('bandwidth'))}" for k in L("can_gestate")]
        if cg:
            out.append("CAN GESTATE (bandwidth): " + ", ".join(cg))
    p = b.get("pollution") if isinstance(b.get("pollution"), dict) else {}
    if p and not urgent:
        bits = [f"{_num(p.get('wastepacks'))} wastepacks" + (f" ({_num(p['wastepacks_near_home'])} near home)" if p.get("wastepacks_near_home") else "")]
        if p.get("home_polluted_pct") is not None:
            bits.append(f"home {_num(p['home_polluted_pct'])}% polluted")
        if p.get("trend"):
            bits.append(c(p["trend"]))
        if p.get("toxic_buildup"):
            bits.append("toxic buildup " + ", ".join(_words(p["toxic_buildup"], 4)))
        bits.append("waste stockpile set" if p.get("waste_stockpile") else "no waste stockpile")
        if p.get("atomizer"):
            bits.append("atomizer built")
        elif p.get("can_atomize"):
            bits.append("atomizer researched, not built")
        out.append("POLLUTION: " + ", ".join(bits))
    gl = []
    for g in L("genes")[:bl["genes"]]:
        eff = _words(g.get("effects"), 6)
        if g.get("hemogen_pct") is not None:
            eff = [(e + f" {_num(g['hemogen_pct'])}%" if e == "needs hemogen" else e) for e in eff]
        gl.append(f"{c(g.get('name'))} {c(g.get('xenotype'))}" + (f" ({', '.join(eff)})" if eff else ""))
    if gl and not urgent:
        out.append("GENES: " + "; ".join(gl))
    hg = b.get("hemogen") if isinstance(b.get("hemogen"), dict) else {}
    if hg:
        t = f"{_num(hg.get('packs'))} packs"
        if hg.get("donors") and not urgent:
            t += "; donors " + ", ".join(_words(hg["donors"], 3))
        if hg.get("needing"):
            t += "; needing " + ", ".join(_words(hg["needing"], 4))
        out.append("HEMOGEN: " + t)
    gb = b.get("genebank") if isinstance(b.get("genebank"), dict) else {}
    if gb and not urgent:
        t = f"{_num(gb.get('genepacks'))} genepacks" + (f" ({_num(gb['unstored'])} unstored)" if gb.get("unstored") else "")
        t += f", {_num(gb.get('banks'))} bank" + (f" ({_num(gb['bank_space'])} free)" if gb.get("bank_space") is not None else "")
        if gb.get("assembler"):
            t += ", assembler " + c(gb["assembler"])
        pk = _words(gb.get("packs"), bl["packs"])
        out.append("GENEBANK: " + t + ("; packs " + ", ".join(pk) if pk else ""))
    germs = [c(x.get("name")) for x in L("xenogerms") if isinstance(x, dict)]
    if germs and not urgent:
        out.append("XENOGERMS: " + ", ".join(germs[:4]) + ("" if b.get("may_implant") is True else " (implanting is the player's call)"))
    if kids and not urgent:
        out.append("CHILDREN: " + "; ".join(
            f"{c(k.get('name'))} {_num(k.get('age'))} (" + ", ".join(x for x in (
                f"learning {_num(k['learning_pct'])}%" if k.get("learning_pct") is not None else "", c(k.get("learning")),
                "" if k.get("desk") else "no school desk") if x) + ")" for k in kids[:6]))
    babies = L("babies")
    if babies:
        hungry = [x for x in babies if x.get("hungry")]
        show = hungry if urgent else babies
        if show:
            out.append("BABIES: " + "; ".join(
                f"{c(x.get('name'))} {_num(x.get('age_days'))} days, " + (f"hungry {_num(x.get('food_pct'))}%" if x.get("hungry") else f"fed {_num(x.get('food_pct'))}%")
                + (f", fed by {c(x['feeder'])}" + (f" ({c(x['feeding'])})" if x.get("feeding") else "") if x.get("feeder") else ", no feeder") for x in show[:6]))
    kid_ids = {k.get("pawn") for k in kids}
    preg = [x for x in L("pregnancies") if x.get("pawn") not in kid_ids and (ages or {}).get(x.get("pawn"), 99) >= 18]
    if preg and not urgent:
        out.append("PREGNANT: " + "; ".join(f"{c(x.get('name'))} {_num(x.get('progress_pct'))}% ({_num(x.get('days_left'))} days left)" for x in preg[:4]))
    cr = _words(b.get("carers"), 6)
    if cr and not urgent:
        out.append("CARERS: " + ", ".join(cr))
    miss = _words(b.get("missing"), 6)
    if miss:
        out.append("BIO MISSING: " + ", ".join(miss))
    return out


def dialog_lines(d: dict, lv: dict | None = None) -> list[str]:
    """OPEN DIALOGS: the windows and letters that wait for a player's choice (GET /dialogs), answered with the choose tool."""
    dl = [x for x in (d.get("dialogs") or []) if isinstance(x, dict) and x.get("id")][:8]
    if not dl:
        return []
    cap = (lv or {}).get("letter", 280)
    out = ["OPEN DIALOGS (answer each with choose; one left open is answered with its default after the timeout):"]
    for x in dl:
        opts = "; ".join(f"{o.get('index')}: {gd.clean(o.get('label', ''), 'label')}" + (f" [{gd.clean(o['risk'], 'label')}]" if o.get("risk") else "")
                         for o in x.get("options") or [] if not o.get("disabled"))
        g = x.get("growth") if isinstance(x.get("growth"), dict) else None
        flds = ", ".join(gd.clean(f.get("label") or f.get("name", ""), "label") + f" (max {f.get('max_len', 40)} chars)" for f in x.get("fields") or [])
        out.append(f"- {x['id']} ({x.get('kind', '')}): {gd.clean(x.get('title', ''), 'label')}"
                   + (f" [options: {opts}]" if opts and not g else "")
                   + (_growth(g) if g else "")
                   + (f" [type a name: {flds}; invent your own fitting names]" if flds else "")
                   # a naming dialog's fallback stays hidden: shown, the model copied it instead of naming the colony
                   + (f" [default: {gd.clean(x['default'], 'label')}]" if x.get("default") and not flds else ""))
        if x.get("text") and cap:
            out.append("    " + gd.clean(x["text"], max_len=cap))
    return out


def _growth(g: dict) -> str:
    """A growth moment's real choices in words: the child, the passions (and how many), the traits with a short description."""
    c = lambda v, n="label": gd.clean(str(v or ""), n)  # noqa: E731
    who = c(g.get("child")) + (f", {c(g.get('age'))}" if g.get("age") else "")
    cur = "; ".join(p for p in (f"skills: {c(g['skills'])}" if g.get("skills") else "", f"traits: {c(g['traits'])}" if g.get("traits") else "") if p)
    pas = ", ".join(c(p) for p in g.get("passions") or [])
    tr = "; ".join(c(t.get("label")) + (f" ({gd.clean(t['description'], 'label')})" if t.get("description") else "")
                   for t in g.get("trait_choices") or [] if isinstance(t, dict))
    return (f" [child: {who}" + (f"; {cur}" if cur else "") + "]"
            + (f" [pick {int(g.get('max_passions') or 1)} passion(s) of: {pas}]" if pas else "")
            + (f" [pick 1 trait of: {tr}]" if tr else "")
            + " [answer with passions and trait; option 0 takes the first choices]")


def _pile(p: dict) -> str:
    where = f"{_num(p.get('dist'))} cells" + (f" {gd.clean(p['dir'], 'label')}" if p.get("dir") else "")
    return f"{gd.clean(p.get('label', ''), 'label')} {_num(p.get('count'))} {where}" + (" (forbidden)" if p.get("forbidden") else "")


def _res_entry(r: dict) -> str:
    def pos(k):
        v = r.get(k)
        return isinstance(v, (int, float)) and not isinstance(v, bool) and v > 0

    extra = ([f"+{_num(r['loose'])} loose"] if pos("loose") else []) + ([f"{_num(r['forbidden'])} forbidden"] if pos("forbidden") else [])
    return f"{gd.clean(r.get('label', ''), 'label')} {r.get('count', '?')}" + (f" ({', '.join(extra)})" if extra else "")


def _render(d: dict, lv: dict, bl: dict | None = None, bio: dict | None = None) -> str:
    bl = bl or _BUILD[0]
    out = ["DATE: " + _date(d)]
    fd = d.get("food_days")
    if fd is not None:
        out.append(f"FOOD: about {fd:g} days of food" if isinstance(fd, (int, float)) else f"FOOD: {fd}")
    res = (d.get("resources") or [])[:lv["resources"]]
    if res:
        cats: dict[str, list[str]] = {}
        for r in res:
            cats.setdefault(gd.clean(r.get("category", "other"), "label"), []).append(_res_entry(r))
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
    out.extend(dialog_lines(d, lv))
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
    counts = ", ".join(f"{k} {'yes' if v is True else 'no' if v is False else v}" for k, v in rm.items()) if isinstance(rm, dict) else ""
    bd = d.get("build")
    built = _build_lines(bd, bl, counts) if isinstance(bd, dict) and bd else []
    if built:
        if not any(l.startswith("ROOMS") for l in built) and counts:
            built.insert(1, "ROOMS: " + counts)
        out.extend(built)
    elif counts:
        out.append("ROOMS: " + counts)
    if isinstance(d.get("bio"), dict):
        ages = {p.get("id"): p["age"] for p in pawns if isinstance(p, dict) and isinstance(p.get("age"), (int, float))}
        out.extend(_bio_lines(d["bio"], bio or _BIO[0], ages))
    pw = d.get("power")
    if pw:
        out.append(f"POWER: produced {pw.get('produced', 0)}, used {pw.get('used', 0)}, stored {pw.get('stored', 0)}")
    return "\n".join(out)


def render_digest(d: dict, cap: int = CAP) -> str:
    """The digest as compact text of at most ``cap`` characters (detail shrinks level by level, then a hard cut)."""
    text = ""
    for base, bld, bio in _STEPS:
        text = _render(d, _LEVELS[base], _BUILD[bld], _BIO[bio])
        if len(text) <= cap:
            return text
    return text[:cap - 40].rsplit("\n", 1)[0] + "\n(digest cut: too long)"

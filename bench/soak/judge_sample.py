"""Stratified sample of soak outputs for a blind quality judge (no model text is ever used as a target).

    python judge_sample.py --runs RUN_DIR... [--flags flags.jsonl] [--probe probe.json] [--per 15] --out sample.md [--key key.json]

Per output kind: up to --per random items (seeded), plus every item the analyzer flagged (worst first). Each item carries the
context a judge needs: speaker card line (traits, register words), the event or request tail of the prompt, and for recaps the
chronicle entries they summarise. Items are numbered; key.json maps numbers back to run/kind/actor for scoring.
"""
from __future__ import annotations

import argparse, collections, json, random, re
from pathlib import Path

GROUPS = {   # judge criteria per kind family
    "speech": "in character, fits RimWorld's tone, grammar, register fits the card",
    "aim": "real subtext (a private want that differs from or deepens what is said), first person, in character",
    "dream": "surreal but grounded in the pawn's day and life",
    "lore": "an apt, colony-flavoured name for the place or thing",
    "art": "a description grounded in colony history, reads like an art description",
    "gathering": "eulogy, vow or speech fitting the occasion and the speaker",
    "recap": "'Previously on' accurate against the chronicle entries given",
    "prose": "in character, grammar, fits RimWorld's tone (diary, thought, chronicle)",
}


def family(kind: str) -> str:
    k = kind.lower()
    if k in ("aim", "cloud") or k.startswith("inner"):
        return "aim"
    if "dream" in k:
        return "dream"
    if "lore" in k:
        return "lore"
    if "art" == k or k.startswith("art"):
        return "art"
    if "gather" in k or k in ("eulogy", "vow", "speech"):
        return "gathering"
    if "recap" in k:
        return "recap"
    if k in ("diary", "thought", "chronicle", "reflection", "habit"):
        return "prose"
    return "speech"


def card_line(meta: dict, aid) -> str:
    for p in (meta.get("pawns") or []) if isinstance(meta.get("pawns"), list) else (meta.get("pawns") or {}).values():
        if str(p.get("id")) == str(aid):
            return f"{p.get('name')}, age {p.get('age')}" + (f", pair {p['pair']}" if p.get("pair") else "")
    m = (meta.get("minds_final") or {}).get(str(aid)) or {}
    return m.get("name", "")


def voice_of(meta: dict, aid) -> str:
    m = (meta.get("minds_final") or {}).get(str(aid)) or {}
    v = m.get("voice") or {}
    words = ((m.get("fixed") or {}).get("extras") or {}).get("words", "")
    traits = re.search(r"Traits: ([^\n]*)", (m.get("fixed") or {}).get("text", ""))
    return "; ".join(x for x in [v.get("register_line", ""), f"Words: {words}" if words else "",
                                 f"Traits: {traits.group(1)[:200]}" if traits else ""] if x)


def last_event(meta: dict, aid, tick) -> str:
    best = ""
    for e in meta.get("events_sent") or []:
        if tick is not None and e.get("tick", 0) > tick:
            break
        if e.get("type") == "act":
            continue
        if aid is None or any(str(x[0]) == str(aid) for x in e.get("actors") or []) or not e.get("actors"):
            best = f"{e.get('type')}: {e.get('summary')}"
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", nargs="+", required=True)
    ap.add_argument("--flags", default="")
    ap.add_argument("--per", type=int, default=15)
    ap.add_argument("--out", required=True)
    ap.add_argument("--key", default="")
    ap.add_argument("--seed", type=int, default=5)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    items, metas = [], {}
    for rd in a.runs:
        rd = Path(rd)
        meta = json.loads((rd / "meta.json").read_text())
        metas[rd.name] = meta
        for line in (rd / "outputs.jsonl").read_text().splitlines():
            o = json.loads(line)
            if o.get("text"):
                o["run"] = rd.name
                items.append(o)
    flagged = set()
    if a.flags and Path(a.flags).exists():
        for line in Path(a.flags).read_text().splitlines():
            f = json.loads(line)
            if f.get("severity") in ("violation", "review") and f.get("text"):
                flagged.add((f.get("run"), f.get("text")))
    by = collections.defaultdict(list)
    for o in items:
        by[o["kind"]].append(o)
    chosen = []
    for kind, lst in sorted(by.items()):
        worst = [o for o in lst if (o["run"], o["text"]) in flagged]
        rest = [o for o in lst if (o["run"], o["text"]) not in flagged]
        rng.shuffle(rest)
        chosen += [(kind, o, True) for o in worst] + [(kind, o, False) for o in rest[:max(0, a.per - len(worst))]]
    out, key = [], {}
    fams = collections.defaultdict(list)
    for kind, o, w in chosen:
        fams[family(kind)].append((kind, o, w))
    n = 0
    for fam, lst in fams.items():
        out.append(f"\n## Family: {fam}  (judge on: {GROUPS[fam]})\n")
        for kind, o, w in lst:
            n += 1
            meta = metas[o["run"]]
            aid = o.get("actor") or o.get("actor_id")
            ctx = []
            if aid is not None:
                ctx.append(f"speaker: {card_line(meta, aid)} | {voice_of(meta, aid)}")
            ev = last_event(meta, aid, o.get("tick"))
            if ev:
                ctx.append(f"latest event involving them: {ev}")
            if o.get("listener") is not None:
                ctx.append(f"listener: {card_line(meta, o['listener'])}")
            if fam == "recap":
                chron = [c["text"] for c in items if c["run"] == o["run"] and c["kind"] == "chronicle"]
                ctx.append("chronicle entries so far: " + " | ".join(t[:400] for t in chron[-4:]))
            out.append(f"### #{n} kind={kind}\n" + "\n".join(f"- {c}" for c in ctx) + f"\n> {o['text']}\n")
            key[n] = {"run": o["run"], "kind": kind, "actor": aid, "flagged": w, "text": o["text"]}
    Path(a.out).write_text(f"# Soak sample: {n} items\n" + "\n".join(out))
    if a.key:
        Path(a.key).write_text(json.dumps(key, indent=1))
    print(n, "items;", {f: len(v) for f, v in fams.items()})


if __name__ == "__main__":
    main()

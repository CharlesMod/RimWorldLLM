#!/usr/bin/env python3
"""Blind judge tooling for creative_probe.py runs (and score joins for judge_sample.py soak samples).

  judge_probe.py make  --runs DIR... --out sample.md --key key.json [--per-feature-arm 12] [--seed 5]
  judge_probe.py score --key key.json --verdicts verdicts.jsonl --out table.md [--json table.json]

make: a blind sample of every arm's pieces, shuffled, each with the data the piece was written from, never the arm.  The same
cases are sampled for every arm (paired), so the score view can compare arms case by case.  The judge writes one JSON line per
item: {"n": int, "score": 1-5, "why": "short"}.  score: mean, share <= 2 and n per feature x arm, plus a paired view.  A key
from judge_sample.py (n -> run/kind/actor) is scored per kind family.
"""
from __future__ import annotations

import argparse
import collections
import json
import random
import statistics
import sys
from pathlib import Path

RUBRIC = {
    "art": "Depicts the event concretely, grounded in the game data given (tale text, maker, material), fresh wording, reads like an art description.",
    "lore": "A memorable, concrete name a colonist would really use, tied to the referent (the event).",
    "recap": "Leads with the biggest events, names who and what changed, has a 'previously on' feel; accurate to the chronicle window.",
    "aim": "A private want or fear that complicates the spoken line (not a restatement), specific to this pawn and scene.",
    "eulogy": "Specific to the person, moving, grounded in what is known of them, not generic.",
    "vows": "Specific to this couple, sincere, grounded, not generic.",
}
ORDER = ["art", "lore", "recap", "aim", "eulogy", "vows"]


def load_rows(run: Path) -> list[dict]:
    f = run / "probe.jsonl" if run.is_dir() else run
    rows = [json.loads(l) for l in f.read_text(encoding="utf-8").splitlines() if l.strip()]
    for r in rows:
        r["_run"] = (run if run.is_dir() else run.parent).name
    return rows


def _extra(c: dict, who: str) -> list[str]:
    out = []
    if c.get("quality"):
        out.append(f"quality: {c['quality']}")
    if c.get(f"{who}_card"):
        out.append(f"{who} (card): " + "; ".join(c[f"{who}_card"]))
    if c.get(f"{who}_knows"):
        out.append(f"{who} remembers: " + " | ".join(c[f"{who}_knows"]))
    return out


def context_lines(r: dict) -> list[str]:
    c = r.get("context") or {}
    f = r["feature"]
    if f == "art":
        t = c.get("tale") or {}
        return [f"maker: {c.get('maker')}", f"item: {c.get('item')}, material: {c.get('material')}, title: {c.get('title')}",
                f"the game's own text for the depicted event: {c.get('game_text')}",
                f"depicted event (one line): {t.get('summary')} (pawns: {', '.join(t.get('pawns', []))})"] + _extra(c, "maker")
    if f == "lore":
        return [f"chronicle entry the name was coined for (day {c.get('day')}): {c.get('event')}"]
    if f == "recap":
        return ["chronicle window (newest last): " + " | ".join(c.get("window") or [])]
    if f == "aim":
        return [f"speaker: {c.get('speaker')}; listener: {c.get('listener')}", f"scene: {c.get('scene')}; just happened: {c.get('trigger')}",
                f"the spoken line the aim belongs to ({r.get('label')}, {r.get('delivery')}): {r.get('line')}"] + _extra(c, "speaker") \
            + ([f"speaker's opinion of the listener: {c['opinion_of_listener']}"] if c.get("opinion_of_listener") is not None else [])
    if f == "eulogy":
        return [f"speaker: {c.get('speaker')}; the deceased: {c.get('deceased')}"] + _extra(c, "speaker")
    if f == "vows":
        return [f"speaker: {c.get('speaker')}; the couple: {' and '.join(c.get('couple') or [])}"] + _extra(c, "speaker")
    return []


def make(a) -> int:
    rng = random.Random(a.seed)
    rows = [r for run in a.runs for r in load_rows(Path(run)) if r.get("text")]
    if getattr(a, "context_from", ""):
        # the same fixture gives the same context per (feature, case): fill it from a newer probe run's rows
        ctx = {(r["feature"], r["case"]): r.get("context") for r in load_rows(Path(a.context_from))}
        for r in rows:
            if (r["feature"], r["case"]) in ctx:
                r["context"] = ctx[(r["feature"], r["case"])]
    items = []
    want = set(a.features.split(",")) if getattr(a, "features", "") else set(ORDER)
    for f in ORDER:
        fr = [r for r in rows if r["feature"] == f and f in want]
        if not fr:
            continue
        arms = sorted({(r["_run"], r["arm"]) for r in fr})
        cases = sorted({(r["case"], r["rep"]) for r in fr})
        random.Random(f"{a.seed}-{f}").shuffle(cases)
        # same cases for every arm: take the first cases that have a piece in every arm, then top up
        have = {k: {x for x in arms if any(r["case"] == k[0] and r["rep"] == k[1] and (r["_run"], r["arm"]) == x for r in fr)} for k in cases}
        full = [k for k in cases if len(have[k]) == len(arms)]
        pick = (full + [k for k in cases if k not in full])[:a.per_feature_arm]
        for k in pick:
            items += [r for r in fr if (r["case"], r["rep"]) == k]
    rng.shuffle(items)
    head = ["# Blind creative sample", "", f"{len(items)} items. Score each 1-5 (5 = excellent) on the rubric for its feature. "
            'Write one JSON line per item: {"n": int, "score": 1-5, "why": "short"}.', "", "## Rubrics"]
    head += [f"- **{f}**: {RUBRIC[f]}" for f in ORDER if f in want]
    out, key = [], {}
    for n, r in enumerate(items, 1):
        out.append(f"\n### #{n}  [{r['feature']}]\n" + "\n".join(f"- {x}" for x in context_lines(r)) + "\n\n> " + r["text"].replace("\n", "\n> ") + "\n")
        key[n] = {"run": r["_run"], "arm": r["arm"], "feature": r["feature"], "case": r["case"], "rep": r["rep"], "actor": r.get("actor"), "text": r["text"]}
    Path(a.out).write_text("\n".join(head) + "\n" + "\n".join(out), encoding="utf-8")
    Path(a.key).write_text(json.dumps(key, indent=1, ensure_ascii=False), encoding="utf-8")
    print(len(items), "items;", dict(collections.Counter(r["feature"] for r in items)))
    return 0


def load_verdicts(path: Path) -> dict[int, dict]:
    v = {}
    for l in path.read_text(encoding="utf-8").splitlines():
        l = l.strip()
        if not l or not l.startswith("{"):
            continue
        d = json.loads(l)
        v[int(d["n"])] = d
    return v


def agg(scores: list[float]) -> dict:
    return {"n": len(scores), "mean": round(statistics.mean(scores), 3) if scores else None,
            "share_le2": round(sum(s <= 2 for s in scores) / len(scores), 3) if scores else None}


def score(a) -> int:
    key = {int(k): v for k, v in json.loads(Path(a.key).read_text(encoding="utf-8")).items()}
    ver = load_verdicts(Path(a.verdicts))
    res: dict = {"probe": {}, "paired": {}, "soak_families": {}}
    cell = collections.defaultdict(list)
    bycase = collections.defaultdict(dict)
    fam = collections.defaultdict(list)
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    for n, k in key.items():
        v = ver.get(n)
        if v is None:
            continue
        s = float(v["score"])
        if "feature" in k:
            cell[(k["feature"], k["arm"])].append(s)
            bycase[(k["feature"], k["case"], k["rep"])][k["arm"]] = s
        else:                                          # judge_sample.py key: run/kind/actor
            from judge_sample import family
            fam[family(k["kind"])].append(s)
    for (f, arm), sc in sorted(cell.items(), key=lambda x: (ORDER.index(x[0][0]), x[0][1])):
        res["probe"].setdefault(f, {})[arm] = agg(sc)
    for f in ORDER:
        arms = sorted({a_ for (ff, _, _), d in bycase.items() if ff == f for a_ in d})
        for i, x in enumerate(arms):
            for y in arms[i + 1:]:
                diffs = [d[x] - d[y] for (ff, _, _), d in bycase.items() if ff == f and x in d and y in d]
                if diffs:
                    res["paired"].setdefault(f, {})[f"{x} - {y}"] = {"n": len(diffs), "mean_diff": round(statistics.mean(diffs), 3),
                                                                      "wins": sum(d > 0 for d in diffs), "losses": sum(d < 0 for d in diffs)}
    for fm, sc in sorted(fam.items()):
        res["soak_families"][fm] = agg(sc)
    md = ["# Judge scores", ""]
    if res["probe"]:
        md += ["## Per feature x arm", "", "| feature | arm | n | mean | share <= 2 |", "|---|---|---|---|---|"]
        md += [f"| {f} | {arm} | {s['n']} | {s['mean']} | {s['share_le2']} |" for f, arms in res["probe"].items() for arm, s in arms.items()]
        md += ["", "## Paired (same case, first arm minus second)", "", "| feature | arms | n | mean diff | wins | losses |", "|---|---|---|---|---|---|"]
        md += [f"| {f} | {p} | {s['n']} | {s['mean_diff']} | {s['wins']} | {s['losses']} |" for f, ps in res["paired"].items() for p, s in ps.items()]
    if res["soak_families"]:
        md += ["", "## Soak sample, per family", "", "| family | n | mean | share <= 2 |", "|---|---|---|---|"]
        md += [f"| {fm} | {s['n']} | {s['mean']} | {s['share_le2']} |" for fm, s in res["soak_families"].items()]
    Path(a.out).write_text("\n".join(md) + "\n", encoding="utf-8")
    if a.json:
        Path(a.json).write_text(json.dumps(res, indent=1), encoding="utf-8")
    print("\n".join(md))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("make")
    m.add_argument("--runs", nargs="+", required=True, help="creative_probe output dirs (each holds probe.jsonl)")
    m.add_argument("--out", required=True)
    m.add_argument("--key", required=True)
    m.add_argument("--per-feature-arm", type=int, default=12)
    m.add_argument("--seed", type=int, default=5)
    m.add_argument("--features", default="", help="comma list; default all features")
    m.add_argument("--context-from", default="", help="a probe run whose per-case context replaces the rows' context")
    s = sub.add_parser("score")
    s.add_argument("--key", required=True)
    s.add_argument("--verdicts", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--json", default="")
    a = ap.parse_args(argv)
    return make(a) if a.cmd == "make" else score(a)


if __name__ == "__main__":
    sys.exit(main())

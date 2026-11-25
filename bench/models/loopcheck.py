"""Offline loop-guard evaluation over the saved bench conversations (no model needed).

    cd bench/models && python loopcheck.py [--threshold 0.6] [--show]

Every saved conversation (results/*/conv.json here and in ~/.local/share/rimworld-hermes-engine/bench/results)
is replayed turn by turn through hermesvoice_agent.core.loopguard.find_repeat. LABELS holds, by hand, the turns
that really are a loop (a new turn that repeats or rephrases an earlier one). Reported: loops caught, loops
missed, false alarms (a flagged turn not in LABELS), and per-model conversations with a loop before and
after (with the guard a flagged turn is retried or the conversation ends, so no repeat is ever shown).
"""
from __future__ import annotations
import argparse, glob, json, os, sys
from collections import defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "agent"))
from hermesvoice_agent.core import loopguard as G  # noqa: E402

ROOTS = [os.path.join(os.path.dirname(__file__), "results"),
         os.path.expanduser("~/.local/share/rimworld-hermes-engine/bench/results")]

# (model dir prefix, scene, turn index) judged by hand to repeat or rephrase an earlier turn
LABELS = {
    ("gemma4-e2b", "sc18", 2), ("gemma4-e2b", "sc18", 3), ("gemma4-e2b", "sc20", 2), ("gemma4-e2b", "sc20", 3),
    ("gemma4-e2b", "sc21", 3), ("gemma4-e2b", "sc23", 2), ("gemma4-e2b", "sc24", 3), ("gemma4-e2b", "sc02", 2),
    ("gemma4-e2b", "sc02", 3), ("gemma4-e2b", "sc01", 3),
    ("ling3-tiny", "sc18", 2), ("ling3-tiny", "sc19", 2), ("ling3-tiny", "sc22", 2), ("ling3-tiny", "sc23", 1),
    ("ling3-tiny", "sc21", 3),
}


def corpus():
    seen, out = set(), []
    for root in ROOTS:
        for f in sorted(glob.glob(root + "/*/conv.json")):
            model = f.split("/")[-2]
            for r in json.load(open(f))["rows"]:
                turns = [(t["speaker"], t["text"]) for t in r["turns"] if "text" in t]
                key = (model, r["scene"], tuple(turns))
                if key not in seen:
                    seen.add(key)
                    out.append((model, r["scene"], turns))
    return out


def evaluate(threshold: float, show: bool = False) -> dict:
    per = defaultdict(lambda: dict(convs=0, loop_convs_before=0, convs_flagged=0, flagged=0, tp=0, fp=0, fn=0))
    for model, scene, turns in corpus():
        key = next((m for m in {m for m, _, _ in LABELS} if model.startswith(m)), model[:10])
        p = per[model]
        p["convs"] += 1
        labelled = {j for (m, s, j) in LABELS if m == key and s == scene}
        flagged = set()
        for j in range(len(turns)):
            if G.find_repeat(turns[j][1], turns[j][0], turns[:j], threshold=threshold):
                flagged.add(j)
        p["loop_convs_before"] += bool(labelled)
        p["convs_flagged"] += bool(flagged)
        p["flagged"] += len(flagged)
        p["tp"] += len(flagged & labelled)
        p["fp"] += len(flagged - labelled)
        p["fn"] += len(labelled - flagged)
        if show:
            for j in sorted(flagged ^ labelled):
                print(("FALSE ALARM " if j in flagged else "MISSED      "), model, scene, j, turns[j][1])
    return per


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--threshold", type=float, default=G.THRESHOLD)
    ap.add_argument("--sweep", action="store_true")
    ap.add_argument("--show", action="store_true")
    a = ap.parse_args()
    for th in ([0.4, 0.5, 0.6, 0.7, 0.8, 0.9] if a.sweep else [a.threshold]):
        per = evaluate(th, a.show and not a.sweep)
        tot = defaultdict(int)
        for p in per.values():
            for k, v in p.items():
                tot[k] += v
        print(f"threshold {th}: loop turns labelled {tot['tp'] + tot['fn']}, caught {tot['tp']}, missed {tot['fn']}, "
              f"false alarms {tot['fp']} (of {tot['flagged']} flagged); conversations {tot['convs']}")
        if not a.sweep:
            for m, p in per.items():
                print(f"  {m}: {p}")

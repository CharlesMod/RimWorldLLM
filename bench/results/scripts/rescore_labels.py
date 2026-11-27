"""Rescore stored lines.json with the merged sanitiser's intent-label cut (main 94c33f4), offline.
usage: rescore_labels.py LABEL=dir[,dir...] ...   (dir = results/<model>)
For each row: text = strip_trailing_label(first raw line, adapter labels); rerun every check; also rerun the
full sanitize_line with intent_labels. Prints raw all-pass before/after the cut and label leaks found."""
import json, os, re, sys
sys.path.insert(0, "/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/bench/models")
from common import ADAPTER, NAME_TOKENS, ACTOR_ID, prefix_for
import lines as L
from rimworldllm_agent.core.sanitize import strip_trailing_label
LAB = ADAPTER.intent_labels
SC = {s["id"]: s for _, s in L.scenarios()}
CAPCHK = ("within_cap", "no_think")

def rescore(d):
    rows = json.load(open(os.path.join(d, "lines.json")))["rows"]
    n = before = after = leaks = 0
    for r in rows:
        raw = r["raw"].strip()
        cut = strip_trailing_label(raw, LAB)
        prefix, mind, ctx, scene = prefix_for(r["pawn"], SC[r["scene"]])
        pw = {w.lower() for w in re.findall(r"[A-Za-z']+", prefix)}
        chk = L.check_text(cut, ctx=ctx, pawn_tokens=NAME_TOKENS[ACTOR_ID[r["pawn"]]], prefix_words=pw)
        n += 1
        before += all(r["raw_checks"].values())
        after += all(v for k, v in chk.items() if k not in CAPCHK) and all(r["raw_checks"].get(k, True) for k in CAPCHK)
        leaks += cut != raw
    return n, before, after, leaks

for a in sys.argv[1:]:
    lab, ds = a.split("=", 1)
    tot = [0, 0, 0, 0]
    for d in ds.split(","):
        for i, v in enumerate(rescore(d)):
            tot[i] += v
    n, b, af, lk = tot
    print(json.dumps({"label": lab, "n": n, "raw_all_pass": round(b / n, 4), "all_pass_after_label_cut": round(af / n, 4), "label_leaks_cut": lk}))

"""Builds markdown tables from results/*.json (pasted into docs/M0-RESULTS.md)."""
import json, glob, os, re, statistics as st
from lib import *
def md(x, f="{:.0f}"): return "-" if x is None else f.format(x)
print("## Q1 tables")
KINDS = [("a_cold", "(a) cold lease"), ("b_warm", "(b) warm turn 2"), ("b_warm2", "(b) warm turn 3"), ("c_old_slot", "(c) re-lease old slot, new scene"), ("c_old_same", "(c') re-lease old slot, same scene"), ("d_other_slot", "(d) re-lease other slot, new scene"), ("d_other_same", "(d') re-lease other slot, same scene")]
for f in sorted(glob.glob(os.path.join(RES, "q1_*.json"))):
    rows = json.load(open(f)); name = os.path.basename(f)[3:-5]
    print(f"\n### {name}\n| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |\n|---|---|---|---|---|---|---|---|---|")
    for k, lab in KINDS:
        rs = [r for r in rows if r["kind"] == k]
        print(f"| {lab} | {len(rs)} | {md(med([r['ttft_s'] for r in rs]),'{:.2f}')} | {md(med([r['total_s'] for r in rs]),'{:.2f}')} | {md(med([r['prompt_n'] for r in rs]))} | {md(med([r['cache_n'] for r in rs]))} | {md(med([r['prompt_s'] for r in rs]),'{:.3f}')} | {md(med([r['raw_prefill'] for r in rs]))} | {md(med([r['eff_prefill'] for r in rs]))} |")
print("\n## Q2 tables\n| run | n | median tok/s | pooled tok/s | drafted / accepted | median line s | median TTFT s |\n|---|---|---|---|---|---|---|")
for f in sorted(glob.glob(os.path.join(RES, "q2_*.json"))):
    rows = json.load(open(f)); name = os.path.basename(f)[3:-5]
    d = sum(r["draft_n"] or 0 for r in rows); a = sum(r["draft_acc"] or 0 for r in rows)
    pt = sum(r["pred_n"] or 0 for r in rows); pm = sum(r["pred_ms"] or 0 for r in rows)
    print(f"| {name} | {len(rows)} | {med([r['tps'] for r in rows]):.1f} | {1000*pt/pm:.1f} | {d} / {a} | {med([r['total_s'] for r in rows]):.3f} | {med([r['ttft_s'] for r in rows]):.3f} |")
for nm in ["q3_conc_n6", "q3_conc_n11"]:
    p = os.path.join(RES, nm + ".json")
    if os.path.exists(p):
        R = json.load(open(p)); print(f"\n## {nm}\n| stream / condition | n | tok/s median | line latency s (median) | p90 | TTFT s | tokens/line |\n|---|---|---|---|---|---|---|")
        for k, rs in R.items():
            t = sorted(r["total_s"] for r in rs); p90 = t[int(0.9 * (len(t) - 1))]
            print(f"| {k} | {len(rs)} | {med([r['tps'] for r in rs]):.1f} | {med(t):.2f} | {p90:.2f} | {med([r['ttft_s'] for r in rs]):.2f} | {med([r['pred_n'] for r in rs]):.0f} |")

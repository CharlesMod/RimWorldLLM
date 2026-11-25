"""Q2: speculative decoding. usage: q2_spec.py <label> [n]. Server already running with the --spec-* flags under test."""
import sys, random
from lib import *
label = sys.argv[1]; N = int(sys.argv[2]) if len(sys.argv) > 2 else 60
items = []
for s in SCENES:
    items.append((s["focal"], s))
    for o in s.get("others", [])[:1]:
        if o in PAWNS: items.append((o, s))
random.Random(7).shuffle(items); items = items[:N]
wipe_slots()
sx, us = build(PAWNS[items[0][0]], items[0][1]); complete(raw(sx, us), 1)  # warm-up, discarded
rows = []
for i, (pid, sc) in enumerate(items):
    p = PAWNS[pid]
    # varied prompt shapes: first third bare, rest with a prior exchange appended
    turns = [] if i % 3 == 0 else [(PAWNS[sc["focal"]]["name"], "Hm."), ("Someone", "Say that again?")]
    sx, us = build(p, sc, turns)
    r = complete(raw(sx, us), 1 + i % 2)
    r.update(pawn=pid, scene=sc["id"], text=clean(r["text"])); rows.append(r)
save(f"q2_{label}.json", rows)
tot_d = sum(r["draft_n"] or 0 for r in rows); tot_a = sum(r["draft_acc"] or 0 for r in rows)
dec_tok = sum(r["pred_n"] or 0 for r in rows); dec_ms = sum(r["pred_ms"] or 0 for r in rows)
print(f"{label}: n={len(rows)} median tps={med([r['tps'] for r in rows]):.1f} pooled tps={1000*dec_tok/dec_ms:.1f} accept={tot_a}/{tot_d}={(tot_a/tot_d if tot_d else 0):.2f} "
      f"median line total={med([r['total_s'] for r in rows]):.3f}s ttft={med([r['ttft_s'] for r in rows]):.3f}s median pred_n={med([r['pred_n'] for r in rows])}")

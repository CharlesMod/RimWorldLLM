"""Q1: lease latency. usage: q1_lease.py <label> [reps]. Server must already be up with the flags under test."""
import sys, copy, random, json
from lib import *

label = sys.argv[1]; REPS = int(sys.argv[2]) if len(sys.argv) > 2 else 5
ids = list(PAWNS)
CANNED = ["Yeah. Whatever you say.", "Not now.", "You heard what happened?", "Keep your voice down.", "I need a minute.", "Right. Fine."]
by_pawn = {}
for s in SCENES:
    by_pawn.setdefault(s["focal"], []).append(s)

def salted(pid, tag):
    p = copy.deepcopy(PAWNS[pid]); p["age"] = p["age"] + tag; return p

def scene_for(pid, k):
    L = by_pawn[pid]; return L[k % len(L)]

rows = []
def run(kind, rep, p, scene, slot, turns=()):
    sx, us = build(p, scene, turns)
    r = complete(raw(sx, us), slot)
    r.update(kind=kind, rep=rep, pawn=p["id"], text=clean(r["text"]), prefix_tokens=None)
    rows.append(r)
    print(f"{kind:9s} rep{rep} {p['id']:8s} slot{slot} prompt_n={r['prompt_n']:5d} cache_n={r['cache_n']:5d} prompt_s={r['prompt_s']:.3f} ttft={r['ttft_s']:.3f} total={r['total_s']:.3f} eff={r['eff_prefill'] or 0:.0f}", flush=True)
    return r

wipe_slots()
# warm-up (discard): CUDA graphs etc
wp = salted(ids[0], 90); run("warmup", -1, wp, scene_for(ids[0], 0), 1); rows.pop()
for rep in range(REPS):
    x, y, z = ids[rep % 6], ids[(rep + 1) % 6], ids[(rep + 2) % 6]
    tag = 100 * (rep + 1)
    X, Y, Z = salted(x, tag + 1), salted(y, tag + 2), salted(z, tag + 3)
    # X's first lease, scene 0
    sx0 = scene_for(x, 0); sx1 = scene_for(x, 1)
    r = run("a_cold", rep, X, sx0, 1)
    t1 = [(X["name"], r["text"]), (PAWNS[y]["name"], CANNED[rep % 6])]
    r = run("b_warm", rep, X, sx0, 1, t1)
    t2 = t1 + [(X["name"], r["text"]), (PAWNS[y]["name"], CANNED[(rep + 1) % 6])]
    run("b_warm2", rep, X, sx0, 1, t2)
    # Z occupies slot 2, Y takes slot 1 (displaces X)
    run("cold_other", rep, Z, scene_for(z, 0), 2)
    run("cold_other", rep, Y, scene_for(y, 0), 1)
    # (c) X re-leased into old slot 1, new scene
    run("c_old_slot", rep, X, sx1, 1)
    # displace again, then (c2) same scene re-lease in old slot
    run("cold_other", rep, Y, scene_for(y, 1), 1)
    run("c_old_same", rep, X, sx1, 1)
    # (d) X into the other slot (slot 2 holds Z) with a third scene
    run("cold_other", rep, Y, scene_for(y, 2), 1)
    run("d_other_slot", rep, X, scene_for(x, 2), 2)
    # refill so that d_same is meaningful: X same scene again into slot 2 after Z used it
    run("cold_other", rep, Z, scene_for(z, 1), 2)
    run("d_other_same", rep, X, scene_for(x, 2), 2)
save(f"q1_{label}.json", rows)
print("kinds:")
for k in ["a_cold","b_warm","b_warm2","c_old_slot","c_old_same","d_other_slot","d_other_same"]:
    rs = [r for r in rows if r["kind"] == k]
    print(f"{k:13s} n={len(rs)} ttft={med([r['ttft_s'] for r in rs]):.3f} total={med([r['total_s'] for r in rs]):.3f} prompt_n={med([r['prompt_n'] for r in rs])} cache_n={med([r['cache_n'] for r in rs])} raw={med([r['raw_prefill'] for r in rs]):.0f} eff={med([r['eff_prefill'] for r in rs]):.0f}")

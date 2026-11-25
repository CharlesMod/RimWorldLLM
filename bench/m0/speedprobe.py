import copy
from lib import *
ids = list(PAWNS); pre = []; dec = []
wipe_slots()
for i in range(7):
    p = copy.deepcopy(PAWNS[ids[i % 6]]); p["age"] += 50 + i
    sc = SCENES[i]; sx, us = build(p, sc)
    r = complete(raw(sx, us), 1 + i % 2, temperature=0.8)
    if i == 0: continue
    pre.append(r["raw_prefill"]); dec.append(r["tps"])
print(f"{med(pre):.0f},{med(dec):.1f}")

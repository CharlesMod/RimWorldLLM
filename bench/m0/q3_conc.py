"""Q3: three slots at once. usage: q3_conc.py [reps]. Baseline server (no spec)."""
import sys, threading, copy
from lib import *
REPS = int(sys.argv[1]) if len(sys.argv) > 1 else 5
ids = list(PAWNS)
chat_scenes = [s for s in SCENES if s["kind"].startswith("idle") and s.get("others")]
def narr_prompt(rep):
    ev = [s["trigger"] for s in SCENES][rep:rep + 14] + [s["trigger"] for s in SCENES][:max(0, rep + 14 - len(SCENES))]
    ros = "\n".join(f"- {p['name']}, {p['age']}: {p['fixed_bio']}" for p in PAWNS.values())
    sysx = ("You are the narrator of a RimWorld colony: a dry, plain chronicler. You see the whole colony. You write the chronicle: "
            "past tense, third person, concrete, no melodrama, no moralising, no invented events.\n\nCOLONISTS\n" + ros +
            "\n\nEVENTS SINCE THE LAST ENTRY (oldest first)\n" + "\n".join(f"- {e}" for e in ev) + "\n")
    return f"<|turn>system\n{sysx}<turn|>\n<|turn>user\nWrite the next chronicle entry, one paragraph of about 130 words.<turn|>\n<|turn>model\n<|channel>thought\n<channel|>"

def conv(rep, pair, slots, out, tag):
    sc = chat_scenes[(rep + pair) % len(chat_scenes)]
    A = PAWNS[sc["focal"]]; B = PAWNS[sc["others"][0]]
    sB = dict(sc); sB["others"] = [sc["focal"]]
    turns = [(B["name"], sc.get("opener", "Hey."))]
    for t in range(6):
        p, s, slot = (A, sc, slots[0]) if t % 2 == 0 else (B, sB, slots[1])
        sx, us = build(p, s, turns)
        r = complete(raw(sx, us), slot); r["turn"] = t; r["tag"] = tag; r["text"] = clean(r["text"]); out.append(r)
        turns.append((p["name"], r["text"] or "..."))

def indep(rep, k, slot, out, tag):
    sc = SCENES[(rep * 3 + k) % len(SCENES)]; A = PAWNS[sc["focal"]]; turns = []
    for t in range(6):
        sx, us = build(A, sc, turns); r = complete(raw(sx, us), slot); r["turn"] = t; r["tag"] = tag; r["text"] = clean(r["text"]); out.append(r)
        turns += [(A["name"], r["text"] or "..."), ("Someone", "Go on.")]

def narr(rep, out, tag):
    r = complete(narr_prompt(rep), 0, n_predict=200, stop=[]); r["tag"] = tag; out.append(r)

def par(*fns):
    ts = [threading.Thread(target=f) for f in fns]
    [t.start() for t in ts]; [t.join() for t in ts]

wipe_slots()
complete(narr_prompt(0), 0, n_predict=20, stop=[])  # warm-up
R = {k: [] for k in ["narr_alone", "conv_alone", "indep_alone", "narr_w_conv", "conv_w_narr", "narr_w_indep", "indep_w_narr", "indep_pair"]}
for rep in range(REPS):
    narr(rep, R["narr_alone"], "narr_alone")
    conv(rep, 0, (1, 2), R["conv_alone"], "conv_alone")
    indep(rep, 0, 1, R["indep_alone"], "indep_alone")
    par(lambda: narr(rep, R["narr_w_conv"], "narr_w_conv"), lambda: conv(rep, 0, (1, 2), R["conv_w_narr"], "conv_w_narr"))
    par(lambda: narr(rep, R["narr_w_indep"], "narr_w_indep"), lambda: indep(rep, 0, 1, R["indep_w_narr"], "indep_w_narr"), lambda: indep(rep, 1, 2, R["indep_w_narr"], "indep_w_narr"))
    par(lambda: indep(rep, 0, 1, R["indep_pair"], "indep_pair"), lambda: indep(rep, 1, 2, R["indep_pair"], "indep_pair"))
    print("rep", rep, "done", flush=True)
save("q3_conc.json", R)
for k, rs in R.items():
    print(f"{k:14s} n={len(rs):3d} tps={med([r['tps'] for r in rs]):6.1f}  line total={med([r['total_s'] for r in rs]):.3f}s ttft={med([r['ttft_s'] for r in rs]):.3f}s  pred_n={med([r['pred_n'] for r in rs])}")

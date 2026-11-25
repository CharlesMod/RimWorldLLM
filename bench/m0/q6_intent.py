"""Q6: intent pass. usage: q6_intent.py <label>. Needs server with the model under test, slot 1."""
import sys, json
from lib import *
label = sys.argv[1]
L = fx("intent_labeled.json"); ids = list(PAWNS)
def intent_request(speaker, listener_name):
    return ("=== INTENT ===\nThe line you just spoke was addressed to " + listener_name + ". What was it, socially? Choose exactly one label:\n"
            "none (nothing social), kind (warm or helpful), comfort (soothes someone who is down), slight (a small put-down), insult (an open attack), "
            "confide (shares a private secret or feeling), gossip (talks about an absent person), boast (brags), complain (grumbles about a grievance).\nAnswer with the label only.")
def intent_prompt(sysx, usr, line, listener_name):
    return raw(sysx, usr) .replace("<turn|>\n<|turn>model\n<|channel>thought\n<channel|>", "") + "<turn|>\n<|turn>model\n<|channel>thought\n<channel|>" + line + "<turn|>\n<|turn>user\n" + intent_request(None, listener_name) + "<turn|>\n<|turn>model\n<|channel>thought\n<channel|>"
def mk_scene(it):
    return dict(where="somewhere in the colony", time="daytime", state="You are going about your day.", trigger=it["situation"], ledger=[], others=[it["listener"]] if it["listener"] else [], memory_ids=[])

# (1) real latency: line pass then intent pass, same warm slot
lat = []
for i, sc in enumerate(SCENES):
    p = PAWNS[sc["focal"]]; wl = PAWNS[sc["others"][0]]["name"] if sc.get("others") else "someone"
    sx, us = build(p, sc, [])
    for mode in ("grammar", "schema", "free"):
        r1 = complete(raw(sx, us), 1); line = clean(r1["text"])  # fresh line per mode so the intent prompt is never already cached
        kw = {"grammar": dict(grammar=GRAMMAR_INTENT), "schema": dict(json_schema={"enum": INTENTS}), "free": {}}[mode]
        r2 = complete(intent_prompt(sx, us, line, wl), 1, n_predict=4, stop=[] if mode != "free" else ["\n"], **kw)
        lat.append(dict(scene=sc["id"], mode=mode, line_total=r1["total_s"], line_prompt_n=r1["prompt_n"], i_total=r2["total_s"], i_ttft=r2["ttft_s"], i_prompt_n=r2["prompt_n"], i_cache_n=r2["cache_n"], i_pred_n=r2["pred_n"], out=r2["text"].strip()))
    print(i, line[:60], lat[-3]["out"], flush=True)
# (2) accuracy on hand-labelled lines
acc = []
for it in L:
    sp = PAWNS[it["speaker"]]; sc = mk_scene(it); sx, us = build(sp, sc, [])
    wl = PAWNS[it["listener"]]["name"] if it["listener"] else "someone"
    complete(raw(sx, us), 1, n_predict=1)  # prime slot with the prefix
    for mode in ("grammar", "schema"):
        kw = dict(grammar=GRAMMAR_INTENT) if mode == "grammar" else dict(json_schema={"enum": INTENTS})
        r = complete(intent_prompt(sx, us, it["line"], wl), 1, n_predict=4, stop=[], temperature=0.0, **kw)
        out = r["text"].strip().strip('"'); acc.append(dict(speaker=it["speaker"], line=it["line"], label=it["label"], mode=mode, pred=out, ok=out == it["label"], total_s=r["total_s"]))
save(f"q6_{label}.json", dict(lat=lat, acc=acc))
for mode in ("grammar", "schema", "free"):
    ls = [x for x in lat if x["mode"] == mode]
    print(mode, f"n={len(ls)} intent total med={med([x['i_total'] for x in ls]):.3f}s ttft={med([x['i_ttft'] for x in ls]):.3f} new prompt tokens={med([x['i_prompt_n'] for x in ls])} cached={med([x['i_cache_n'] for x in ls])} line total med={med([x['line_total'] for x in ls]):.3f}")
for mode in ("grammar", "schema"):
    a = [x for x in acc if x["mode"] == mode]; print(mode, "accuracy", sum(x["ok"] for x in a), "/", len(a))

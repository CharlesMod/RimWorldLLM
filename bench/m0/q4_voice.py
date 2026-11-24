"""Q4: voice distinctness. usage: q4_voice.py [gen|score]. gen needs a running server."""
import sys, re, json, copy
import numpy as np
from lib import *
NS = fx("scenes_neutral.json"); ids = list(PAWNS)
def short(p):
    m = re.search(r"'([^']+)'", p["name"]); return m.group(1) if m else p["name"].split()[0]
def fill(s, F, O):
    return s.replace("{F}", short(F)).replace("{O}", short(O))
def scene_for(pid, k, sc):
    F = PAWNS[pid]; O = PAWNS[ids[(ids.index(pid) + 1 + 2 * (k % 2)) % 6]]
    d = dict(id=sc["id"], where=fill(sc["where"], F, O), time=sc["time"], state=fill(sc["state"], F, O),
             trigger=fill(sc["trigger"], F, O), ledger=[fill(l, F, O) for l in sc.get("ledger", [])],
             others=[O["id"]], memory_ids=[m["id"] for m in F["memories"][:2]])
    return d

def gen():
    wipe_slots(); complete(raw(*build(PAWNS[ids[0]], scene_for(ids[0], 0, NS[0]))), 1)
    rows = []
    for cond, style, extra in [("A", "A", {}), ("B", "B", {}), ("A_dry", "A", {"dry_multiplier": 0.8, "dry_base": 1.75, "dry_allowed_length": 2, "dry_penalty_last_n": 512})]:
        for pid in ids:
            for k, sc in enumerate(NS):
                s = scene_for(pid, k, sc)
                r = complete(raw(*build(PAWNS[pid], s, [], style)), 1 + k % 2, **extra)
                rows.append(dict(cond=cond, pawn=pid, scene=sc["id"], text=clean(r["text"]), total_s=r["total_s"], pred_n=r["pred_n"]))
        print(cond, "done", flush=True)
    return rows

BANNED = ["a testament to", "tapestry", "i can't help but", "i can’t help but", "the weight of", "shiver", "little did", "every fiber", "a mix of", "in this harsh world",
          "a symphony", "dance of", "palpable", "heavy silence", "hung in the air", "a reminder", "resilience", "whispers of", "echo", "unspoken", "bittersweet", "stark reminder",
          "in the face of", "a harsh reality", "harsh reality", "cruel world", "survival is", "against all odds", "mixture of", "sense of"]
def score(rows):
    from sklearn.feature_extraction.text import TfidfVectorizer
    from sklearn.linear_model import LogisticRegression
    from sklearn.model_selection import StratifiedKFold, cross_val_score
    from sklearn.pipeline import make_pipeline
    mask = re.compile("|".join(sorted({re.escape(w) for p in PAWNS.values() for w in re.findall(r"[A-Za-z]+", p["name"]) if len(w) > 2} | {"Hana", "Brandt"}, key=len, reverse=True)), re.I)
    out = {}
    for cond in ["A", "B", "A_dry"]:
        rs = [r for r in rows if r["cond"] == cond]
        X = [mask.sub("<N>", r["text"]) for r in rs]; y = [r["pawn"] for r in rs]
        accs = []
        for seed in range(4):
            pipe = make_pipeline(TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5), sublinear_tf=True, min_df=2), LogisticRegression(C=20, max_iter=2000))
            accs += list(cross_val_score(pipe, X, y, cv=StratifiedKFold(5, shuffle=True, random_state=seed)))
        words = [len(r["text"].split()) for r in rs]
        slop = [r for r in rs if any(b in r["text"].lower() for b in BANNED)]
        empty = sum(1 for r in rs if not r["text"].strip())
        uniq = len(set(r["text"].lower() for r in rs))
        out[cond] = dict(n=len(rs), acc_mean=float(np.mean(accs)), acc_std=float(np.std(accs)), words_mean=float(np.mean(words)), over35=sum(w > 35 for w in words),
                         empty=empty, slop_rate=len(slop) / len(rs), slop_lines=[r["text"] for r in slop][:8], dup_lines=len(rs) - uniq,
                         per_pawn_slop={p: sum(1 for r in slop if r["pawn"] == p) for p in ids})
        import random; rnd = random.Random(3)
        out[cond]["samples"] = {p: rnd.sample([r["text"] for r in rs if r["pawn"] == p], 3) for p in ids}
    return out

if __name__ == "__main__":
    if sys.argv[1] == "gen":
        label = sys.argv[2] if len(sys.argv) > 2 else "main"; save(f"q4_lines_{label}.json", gen())
    else:
        label = sys.argv[2] if len(sys.argv) > 2 else "main"
        o = score(json.load(open(os.path.join(RES, f"q4_lines_{label}.json")))); save(f"q4_scores_{label}.json", o)
        for c, v in o.items():
            print(c, {k: (round(x, 3) if isinstance(x, float) else x) for k, x in v.items() if k not in ("samples", "slop_lines")})

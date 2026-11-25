"""Section 6: voice distinctness, the M0 q4_voice scorer logic (TF-IDF char n-grams, pawn-ID accuracy, slop rate)
run on lines generated through the agent's real line path (the M0 generator speaks a Gemma-only raw template)."""
from __future__ import annotations
import re
import numpy as np
from common import *
from hermesvoice_agent.core.passes import line_pass

NS = fx("scenes_neutral.json")
IDS = list(PAWNS)
BANNED = ["a testament to", "tapestry", "i can't help but", "i can’t help but", "the weight of", "shiver", "little did", "every fiber", "a mix of", "in this harsh world",
          "a symphony", "dance of", "palpable", "heavy silence", "hung in the air", "a reminder", "resilience", "whispers of", "echo", "unspoken", "bittersweet", "stark reminder",
          "in the face of", "a harsh reality", "harsh reality", "cruel world", "survival is", "against all odds", "mixture of", "sense of"]


def scene_for(pid, k, sc):
    F = PAWNS[pid]; O = PAWNS[IDS[(IDS.index(pid) + 1 + 2 * (k % 2)) % 6]]
    f = lambda s: s.replace("{F}", short(F["name"])).replace("{O}", short(O["name"]))
    return dict(id=sc["id"], where=f(sc["where"]), time=sc["time"], state=f(sc["state"]), trigger=f(sc["trigger"]),
                ledger=[f(l) for l in sc.get("ledger", [])], others=[O["id"]], memory_ids=[m["id"] for m in F["memories"][:2]])


async def gen(llm, per_pawn=12):
    rows = []
    for pid in IDS:
        for k, sc in enumerate(NS[:per_pawn]):
            prefix, mind, ctx, _ = prefix_for(pid, scene_for(pid, k, sc))
            s, _m = await line_pass(llm, slot=1 + k % 2, mind=mind, name=mind.name, prefix=prefix, turns=[], turn_no=0, ctx=ctx, max_tokens=48)
            lst = []
            rows.append({"pawn": pid, "scene": sc["id"], "text": (s.text or "")})
    return rows


def score(rows):
    from sklearn.feature_extraction.text import TfidfVectorizer
    from sklearn.linear_model import LogisticRegression
    from sklearn.model_selection import StratifiedKFold, cross_val_score
    from sklearn.pipeline import make_pipeline
    mask = re.compile("|".join(sorted({re.escape(w) for p in PAWNS.values() for w in re.findall(r"[A-Za-z]+", p["name"]) if len(w) > 2} | {"Hana", "Brandt"}, key=len, reverse=True)), re.I)
    rs = [r for r in rows if r["text"]]
    X = [mask.sub("<N>", r["text"]) for r in rs]; y = [r["pawn"] for r in rs]
    accs = []
    for seed in range(4):
        pipe = make_pipeline(TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5), sublinear_tf=True, min_df=2), LogisticRegression(C=20, max_iter=2000))
        accs += list(cross_val_score(pipe, X, y, cv=StratifiedKFold(5, shuffle=True, random_state=seed)))
    slop = [r for r in rs if any(b in r["text"].lower() for b in BANNED)]
    return {"n": len(rows), "kept": len(rs), "pawn_id_acc_mean": float(np.mean(accs)), "pawn_id_acc_std": float(np.std(accs)), "chance": round(1 / len(IDS), 3),
            "slop_rate": round(len(slop) / len(rs), 3), "slop_lines": [r["text"] for r in slop][:5], "dup_lines": len(rs) - len({r["text"].lower() for r in rs}),
            "samples": {p: [r["text"] for r in rs if r["pawn"] == p][:3] for p in IDS}}

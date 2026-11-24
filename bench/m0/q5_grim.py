"""Q5: grim content. usage: q5_grim.py <label> [samples]. Uses the server's own chat template (Gemma or Qwen)."""
import sys
from lib import *
label = sys.argv[1]; K = int(sys.argv[2]) if len(sys.argv) > 2 else 3
G = fx("grim.json")
rows = []
for sc in G:
    p = PAWNS[sc["focal"]]
    sx, us = build(p, sc, [])
    for k in range(K):
        r = chat_complete([{"role": "system", "content": sx}, {"role": "user", "content": us}], slot=0, n_predict=60)
        rows.append(dict(scene=sc["id"], subkind=sc.get("subkind"), pawn=sc["focal"], sample=k, text=r["text"].strip(), finish=r["finish"], total_s=r["total_s"]))
    print(sc["id"], rows[-1]["text"][:100], flush=True)
save(f"q5_{label}.json", rows)

"""Recompute the text checks on stored lines.json rows (after a checker fix) without calling the model. usage: rescore.py [results_dir]"""
import json, sys
from pathlib import Path
from common import MODELS, OUT, NAME_TOKENS, ACTOR_ID
import checks as C
import lines

res = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT
for m in MODELS:
    p = res / m / "lines.json"
    if not p.exists():
        continue
    d = json.loads(p.read_text())
    for r in d["rows"]:
        tok = NAME_TOKENS[ACTOR_ID[r["pawn"]]]
        r["raw_checks"]["in_character"] = C.in_character(r["raw"].strip(), tok)
        if r["kept"]:
            r["san_checks"]["in_character"] = C.in_character(r["sanitized"], tok)
    d["summary"] = lines.summarise(d["rows"])
    p.write_text(json.dumps(d, indent=1, ensure_ascii=False))
    print(m, d["summary"]["raw_all_pass"])

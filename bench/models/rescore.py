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

import tools, re
for m in MODELS:
    p = res / m / "tools.json"
    if not p.exists():
        continue
    d = json.loads(p.read_text())
    for ch in d:
        for r in d[ch]["rows"]:
            r["tool_in_content"] = (not r["calls"]) and bool(re.search(r"\b(?:say|remember|set_mood|start_conversation|do_nothing)\s*[\{\(]|<tool_call>|tool_call|<\|\"\|>", r["content"]))
        d[ch]["summary"] = tools.summarise(d[ch]["rows"])
    p.write_text(json.dumps(d, indent=1, ensure_ascii=False))
    print(m, "tools rescored", {ch: d[ch]["summary"]["tool_call_text_in_content"] for ch in d})

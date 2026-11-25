"""Section 3: the agent's intent pass (GBNF) and reflection JSON (REFLECT_SCHEMA)."""
from __future__ import annotations
import json, re, time
import jsonschema
from common import *
from hermesvoice_agent.core.passes import parse_intent
from hermesvoice_agent.core.prompt import grammar_for, intent_messages, line_messages
from hermesvoice_agent.core.reflection import REFLECT_SCHEMA, parse_result, run_reflection, reflection_messages
from hermesvoice_agent.core.minds import Relation

LABELS = ADAPTER.intent_labels


async def intent(llm):
    items = fx("intent_labeled.json")
    rows = []
    for it in items:
        sp = it["speaker"]
        sc = dict(where="somewhere in the colony", time="daytime", state="You are going about your day.", trigger=it["situation"],
                  ledger=[], others=[it["listener"]] if it["listener"] else [], memory_ids=[])
        prefix, mind, ctx, _ = prefix_for(sp, sc)
        lm = line_messages(prefix, [], mind, mind.name)
        ln = PAWNS[it["listener"]]["name"] if it["listener"] else None
        msgs = intent_messages(lm, it["line"], ln or "someone")
        for mode in ("grammar", "schema", "free"):
            await llm.complete(lm, slot=1, kind="line", max_tokens=1)      # prime: the agent's intent pass runs on a warm slot
            kw = {"grammar": dict(grammar=grammar_for(LABELS), stop=[], max_tokens=4), "schema": dict(json_schema={"enum": LABELS}, stop=[], max_tokens=8),
                  "free": dict(stop=["\n"], max_tokens=8)}[mode]
            lst = []
            RAW.set(lst)
            t0 = time.perf_counter()
            try:
                c = await llm.complete(msgs, slot=1, kind="intent", temperature=0.0, **kw)
                text = c.text
            except Exception as e:
                text = f"ERR {e}"
            wall = time.perf_counter() - t0
            rm = raw_of(lst[-1]) if lst else {}
            valid_json = schema_ok = False
            val = None
            if mode == "schema":
                try:
                    val = json.loads(text)
                    valid_json = True
                    jsonschema.validate(val, {"enum": LABELS})
                    schema_ok = True
                except Exception:
                    pass
                pred = val if schema_ok else LABELS[0]
            else:
                pred = parse_intent(text, LABELS)
                valid_json = schema_ok = text.strip().lower().strip(".:\"' ") in LABELS
            rows.append({"mode": mode, "line": it["line"], "label": it["label"], "pred": pred, "raw": text, "ok": pred == it["label"],
                         "valid": valid_json and schema_ok, "valid_json": valid_json, "wall_s": round(wall, 3), "finish": rm.get("finish")})
    return rows


def intent_summary(rows):
    out = {}
    for mode in ("grammar", "schema", "free"):
        rs = [r for r in rows if r["mode"] == mode]
        out[mode] = {"n": len(rs), "valid_json_rate": rate(r["valid_json"] for r in rs), "schema_valid_rate": rate(r["valid"] for r in rs),
                     "accuracy": rate(r["ok"] for r in rs), "latency_median_s": med([r["wall_s"] for r in rs]), "latency_p90_s": p90([r["wall_s"] for r in rs]),
                     "confusions": [(r["label"], r["pred"], r["line"]) for r in rs if not r["ok"]][:12]}
    return out


async def reflection(llm, runs=20):
    rows = []
    pids = list(PAWNS)
    for i in range(runs):
        pid = pids[i % len(pids)]
        mind = mind_for(pid)
        for r in PAWNS[pid]["relations"]:
            oid = next((ACTOR_ID[o] for o in PAWNS if PAWNS[o]["name"] == r["name"]), 100 + len(mind.relations))
            mind.relations[str(oid)] = Relation(name=r["name"], opinion=r["opinion"], feeling=r["kind"])
        mind.unreflected_importance = 5.0
        lst = []
        RAW.set(lst)
        t0 = time.perf_counter()
        res = await run_reflection(llm, ADAPTER, mind, NAMES, slot=i % 3, now=20000, day=60000, name_tokens=NAME_TOKENS,
                                   common_words=set(ADAPTER.common_words))
        wall = time.perf_counter() - t0
        rm = raw_of(lst[-1]) if lst else {"content": ""}
        text = rm["content"]
        jparse = jvalid = False
        a, b = text.find("{"), text.rfind("}")
        err = ""
        if a >= 0 and b > a:
            try:
                obj = json.loads(text[a:b + 1]); jparse = True
                jsonschema.validate(obj, REFLECT_SCHEMA); jvalid = True
            except Exception as e:
                err = str(e)[:160]
        rows.append({"pawn": pid, "json_parse": jparse, "schema_valid": jvalid, "pydantic_ok": parse_result(text) is not None, "accepted": res is not None,
                     "finish": rm.get("finish"), "tokens": rm.get("completion_tokens"), "wall_s": round(wall, 2), "err": err, "raw": text[:700]})
    return rows


def reflection_summary(rows):
    return {"runs": len(rows), "json_parse_rate": rate(r["json_parse"] for r in rows), "schema_valid_rate": rate(r["schema_valid"] for r in rows),
            "pydantic_ok_rate": rate(r["pydantic_ok"] for r in rows), "accepted_by_agent_rate": rate(r["accepted"] for r in rows),
            "truncated_at_cap": sum(r["finish"] == "length" for r in rows), "latency_median_s": med([r["wall_s"] for r in rows]),
            "tokens_median": med([r["tokens"] for r in rows]), "failures": [{"pawn": r["pawn"], "err": r["err"], "raw": r["raw"][:300]} for r in rows if not r["accepted"]][:4],
            "example": next((r["raw"] for r in rows if r["accepted"]), "")}

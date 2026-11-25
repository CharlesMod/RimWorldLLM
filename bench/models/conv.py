"""Section 2: 10 two-speaker exchanges of 4 turns, each speaker in its own slot, through the agent's speak()."""
from __future__ import annotations
import copy, re
from common import *
from hermesvoice_agent.core.passes import speak, Spoken, Dropped
import checks as C


def pairs(n=10):
    sc = [s for s in fx("scenes.json") if s.get("others")]
    chat = [s for s in sc if s["kind"] == "chat"] + [s for s in sc if s["kind"] != "chat"]
    return chat[:n]


async def run(llm, n=10, turns_n=4):
    rows = []
    for k, sc in enumerate(pairs(n)):
        a, b = sc["focal"], sc["others"][0]
        scb = copy.deepcopy(sc)
        scb["focal"], scb["others"] = b, [a]
        scb["memory_ids"] = [m["id"] for m in PAWNS[b]["memories"][:2]]
        spk = {a: prefix_for(a, sc), b: prefix_for(b, scb)}
        slots = {a: (2 * k) % 3, b: (2 * k + 1) % 3}
        if slots[a] == slots[b]:
            slots[b] = (slots[a] + 1) % 3
        turns, log = [], []
        for t in range(turns_n):
            sp, li = (a, b) if t % 2 == 0 else (b, a)
            prefix, mind, ctx, _ = spk[sp]
            res = await speak(llm, slot=slots[sp], mind=mind, name=mind.name, prefix=prefix, turns=list(turns), turn_no=len(turns) // 2,
                              ctx=ctx, listener_name=PAWNS[li]["name"], labels=ADAPTER.intent_labels, max_tokens=48)
            if isinstance(res, Spoken):
                turns.append((mind.name, res.text))
                log.append({"turn": t, "speaker": sp, "text": res.text, "intent": res.intent})
            else:
                log.append({"turn": t, "speaker": sp, "dropped": res.reason})
        texts = [x["text"] for x in log if "text" in x]
        leak, ans = [], []
        prev = sc["trigger"]
        for x in log:
            if "text" not in x:
                continue
            other_tokens = NAME_TOKENS[ACTOR_ID[b if x["speaker"] == a else a]]
            leak.append(C.conv_prefix_leak(x["text"], other_tokens))
            ans.append(C.answers_previous(x["text"], prev, sc["trigger"]) if x["turn"] else True)
            prev = x["text"]
        rows.append({"scene": sc["id"], "a": a, "b": b, "turns": log, "dropped": sum(1 for x in log if "dropped" in x),
                     "writes_other_line": sum(leak), "answers": ans, "rep": C.repetition(texts)})
    return rows


def summarise(rows) -> dict:
    allturns = sum(len(r["turns"]) for r in rows)
    dropped = sum(r["dropped"] for r in rows)
    ans = [x for r in rows for x in r["answers"]]
    return {"exchanges": len(rows), "turns": allturns, "turn_drop_rate": round(dropped / allturns, 4),
            "complete_exchanges": rate(r["dropped"] == 0 for r in rows),
            "answers_previous_rate": rate(ans), "writes_other_line_rate": round(sum(r["writes_other_line"] for r in rows) / max(1, allturns - dropped), 4),
            "repetition_loop_exchanges": sum(r["rep"]["loop"] for r in rows),
            "examples": [{"scene": r["scene"], "turns": [(x["speaker"], x.get("text") or "DROP:" + x["dropped"]) for x in r["turns"]]} for r in rows[:3]]}

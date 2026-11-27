"""Section 5: speed as the game sees it, with the agent's own request body, streamed for time to first token."""
from __future__ import annotations
import asyncio, json, time
import httpx
from common import *
from rimworldllm_agent.core.prompt import line_messages


def body_for(msgs, slot, cap=48):
    c = LLMConfig(base_url=ENGINE)
    return {"messages": [{"role": r, "content": t} for r, t in msgs], "max_tokens": cap, "cache_prompt": True, "stop": ["\n"],
            "chat_template_kwargs": {"enable_thinking": False}, "temperature": c.temperature, "top_k": c.top_k, "top_p": c.top_p,
            "min_p": c.min_p, "id_slot": slot, "stream": True}


async def stream_call(client, msgs, slot):
    t0 = time.perf_counter()
    ttft, text, timings, fin = None, "", {}, None
    async with client.stream("POST", f"{ENGINE}/v1/chat/completions", json=body_for(msgs, slot)) as r:
        async for ln in r.aiter_lines():
            if not ln.startswith("data:") or ln.strip() == "data: [DONE]":
                continue
            j = json.loads(ln[5:])
            if j.get("timings"):
                timings = j["timings"]
            for ch in j.get("choices", []):
                d = ch.get("delta", {}).get("content")
                if d and ttft is None:
                    ttft = time.perf_counter() - t0
                text += d or ""
                fin = ch.get("finish_reason") or fin
    total = time.perf_counter() - t0
    pn, cn = timings.get("prompt_n", 0), timings.get("cache_n", 0)
    ps = timings.get("prompt_ms", 0) / 1000
    return {"ttft_s": ttft, "total_s": total, "pred_n": timings.get("predicted_n"), "prompt_n": pn, "cache_n": cn,
            "decode_tps": timings.get("predicted_per_second"), "prompt_tps": timings.get("prompt_per_second"),
            "effective_prefill_tps": ((pn + cn) / ps) if ps > 0 else None, "finish": fin, "text": text.strip()[:80]}


def scen(i):
    sl = [(s["focal"], s) for s in fx("scenes.json")] + [(s["focal"], s) for s in fx("grim.json")]
    return sl[i % len(sl)]


LONG_REQ = "=== REQUEST ===\nWrite {n}'s next line of speech now: two or three sentences, about 30 words. One line only."


def msgs_for(pid, sc, turns=None, turn_no=0, long=False):
    prefix, mind, ctx, _ = prefix_for(pid, sc)
    req = LONG_REQ.format(n=mind.name) if long else None
    return line_messages(prefix, turns or [], mind, mind.name, turn_no, req)


def stats(rs, key):
    xs = [r[key] for r in rs if r.get(key) is not None]
    return {"median": med(xs), "p90": p90(xs)} if xs else None


def summarise(rs):
    return {"n": len(rs), "ttft_s": stats(rs, "ttft_s"), "total_s": stats(rs, "total_s"), "decode_tps": stats(rs, "decode_tps"),
            "prompt_tps": stats(rs, "prompt_tps"), "effective_prefill_tps": stats(rs, "effective_prefill_tps"),
            "prompt_n": stats(rs, "prompt_n"), "cache_n": stats(rs, "cache_n"), "pred_n": stats(rs, "pred_n"),
            "line_24_40_tokens_n": sum(1 for r in rs if r["pred_n"] and 24 <= r["pred_n"] <= 40)}


async def run(reps=12):
    out = {}
    async with httpx.AsyncClient(timeout=120) as client:
        await stream_call(client, msgs_for(*scen(0)), 0)    # load / JIT warm-up, not recorded
        # cold, slot erased: nothing cached at all
        rs = []
        for i in range(reps):
            erase_slots()
            rs.append(await stream_call(client, msgs_for(*scen(i + 3)), 0))
        out["cold_erased"] = {"summary": summarise(rs), "runs": rs}
        # cold, new pawn prefix: the slot still holds another pawn's prefix (shared block A matches)
        rs = []
        for i in range(reps):
            await stream_call(client, msgs_for(*scen(i * 2)), 0)
            rs.append(await stream_call(client, msgs_for(*scen(i * 2 + 7)), 0))
        out["cold_new_pawn"] = {"summary": summarise(rs), "runs": rs}
        # warm: same lease, turn two; prefix cached
        rs = []
        for i in range(reps):
            pid, sc = scen(i * 3 + 1)
            await stream_call(client, msgs_for(pid, sc), 1)
            rs.append(await stream_call(client, msgs_for(pid, sc, [("Someone", "Hm. Rough day.")], 1), 1))
        out["warm"] = {"summary": summarise(rs), "runs": rs}
        # 3 slots at once, cold (erased) and warm
        for label, warm in (("concurrent3_cold", False), ("concurrent3_warm", True)):
            rs, wall = [], []
            for i in range(max(4, reps // 2)):
                ss = [scen(i * 5 + j) for j in range(3)]
                if warm:
                    await asyncio.gather(*[stream_call(client, msgs_for(*ss[j]), j) for j in range(3)])
                    t0 = time.perf_counter()
                    res = await asyncio.gather(*[stream_call(client, msgs_for(*ss[j], [("Someone", "Hm. Rough day.")], 1), j) for j in range(3)])
                else:
                    erase_slots()
                    t0 = time.perf_counter()
                    res = await asyncio.gather(*[stream_call(client, msgs_for(*ss[j]), j) for j in range(3)])
                wall.append(time.perf_counter() - t0)
                rs += res
            out[label] = {"summary": summarise(rs), "round_wall_s": {"median": med(wall), "p90": p90(wall)},
                          "aggregate_decode_tps_median": med([sum(r["decode_tps"] or 0 for r in rs[k:k + 3]) for k in range(0, len(rs), 3)]), "runs": rs}
        # same three cases with a 24-40 token line (the real request yields ~10-token lines; this asks for two or three sentences)
        rs = []
        for i in range(reps):
            erase_slots()
            rs.append(await stream_call(client, msgs_for(*scen(i + 3), long=True), 0))
        out["long_cold_erased"] = {"summary": summarise(rs), "runs": rs}
        rs = []
        for i in range(reps):
            pid, sc = scen(i * 3 + 1)
            await stream_call(client, msgs_for(pid, sc, long=True), 1)
            rs.append(await stream_call(client, msgs_for(pid, sc, [("Someone", "Hm. Rough day.")], 1, long=True), 1))
        out["long_warm"] = {"summary": summarise(rs), "runs": rs}
        rs = []
        for i in range(max(4, reps // 2)):
            ss = [scen(i * 5 + j) for j in range(3)]
            await asyncio.gather(*[stream_call(client, msgs_for(*ss[j], long=True), j) for j in range(3)])
            rs += await asyncio.gather(*[stream_call(client, msgs_for(*ss[j], [("Someone", "Hm. Rough day.")], 1, long=True), j) for j in range(3)])
        out["long_concurrent3_warm"] = {"summary": summarise(rs), "runs": rs}
    out["vram_mib"] = vram_mib()
    return out

"""usage: run_model.py MODEL [section ...]   sections: lines conv struct tools speed voice (default: all)"""
import asyncio, sys, time, json
from common import *
import lines, conv, struct, tools, speed, voice


async def main(model, sections):
    st = select_model(model)
    print("ready", model, st["shape"], "vram", vram_mib(), flush=True)
    save(model, "engine_status", {"shape": st["shape"], "vram_mib": vram_mib(), "backend": st.get("backend")})
    llm = make_llm()
    try:
        if "lines" in sections:
            erase_slots(); t = time.time()
            rows = await lines.run(llm); s = lines.summarise(rows)
            save(model, "lines", {"summary": s, "rows": rows}); print("lines", round(time.time() - t), "s raw_all_pass", s["raw_all_pass"], "drop", s["sanitizer_drop_rate"], flush=True)
        if "conv" in sections:
            erase_slots()
            rows = await conv.run(llm); s = conv.summarise(rows)
            save(model, "conv", {"summary": s, "rows": rows}); print("conv", s["turn_drop_rate"], s["repetition_loop_exchanges"], flush=True)
        if "struct" in sections:
            erase_slots()
            rows = await struct.intent(llm); s = struct.intent_summary(rows)
            rr = await struct.reflection(llm); rs = struct.reflection_summary(rr)
            save(model, "struct", {"intent": {"summary": s, "rows": rows}, "reflection": {"summary": rs, "rows": rr}})
            print("intent acc", {m: s[m]["accuracy"] for m in s}, "reflect accepted", rs["accepted_by_agent_rate"], flush=True)
        if "voice" in sections:
            erase_slots()
            rows = await voice.gen(llm); sc = voice.score(rows)
            save(model, "voice", {"summary": sc, "rows": rows}); print("voice acc", sc["pawn_id_acc_mean"], flush=True)
    finally:
        await llm.aclose()
    if "tools" in sections:
        out = {}
        for choice in ("auto", "required"):
            erase_slots()
            rows = tools.run(choice); out[choice] = {"summary": tools.summarise(rows), "rows": rows}
            print("tools", choice, out[choice]["summary"]["right_tool_rate_single"], out[choice]["summary"]["tool_calls_parse_rate"], flush=True)
        save(model, "tools", out)
    if "speed" in sections:
        out = await speed.run(); save(model, "speed", out)
        print("speed warm", out["warm"]["summary"]["ttft_s"], out["warm"]["summary"]["decode_tps"], flush=True)


if __name__ == "__main__":
    model = sys.argv[1]
    secs = sys.argv[2:] or ["lines", "conv", "struct", "tools", "speed", "voice"]
    asyncio.run(main(model, secs))

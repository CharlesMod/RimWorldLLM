"""struct section against a bare llama-server (no engine): ENGINE_URL=<server> BENCH_OUT=<dir> struct_direct.py LABEL
No sampler fields are sent (cfg.model unset), so the server's defaults are the sampler under test."""
import asyncio, sys
sys.path.insert(0, "/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/bench/models")
from common import *
import structured


async def main(label):
    llm = make_llm()
    try:
        erase_slots()
        rows = await structured.intent(llm); s = structured.intent_summary(rows)
        rr = await structured.reflection(llm); rs = structured.reflection_summary(rr)
        save(label, "struct", {"intent": {"summary": s, "rows": rows}, "reflection": {"summary": rs, "rows": rr}})
        print("intent acc", {m: s[m]["accuracy"] for m in s}, "reflect accepted", rs["accepted_by_agent_rate"], flush=True)
    finally:
        await llm.aclose()

asyncio.run(main(sys.argv[1]))

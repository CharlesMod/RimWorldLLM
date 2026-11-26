"""Prefill probe: does the agent's native-mode prompt (empty thought block) make E-models think?
Builds real agent prompts (lines fixtures via bench common), renders three ways and samples N each:
  A chat (openai mode, server template, enable_thinking false)  B native gemma_prompt (empty thought)
  C bare '<|turn>model\n' prefill (M0_NOTHOUGHT).  Counts thought leaks / channel markers / empty."""
import asyncio, json, os, re, sys
from pathlib import Path
W = Path("/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/bench/models")
sys.path.insert(0, str(W)); sys.path.insert(0, str(W.parent.parent / "agent"))
import httpx
from hermesvoice_agent.core.llm import gemma_prompt
E = os.environ.get("ENGINE_URL", "http://127.0.0.1:47480")
N = int(os.environ.get("N", "20"))
SYS = ("You are Rook, the colony cook: gruff, few words, starts lines with 'Hm.'. Speak one short line in "
       "character, first person, no narration.")
SCENES = ["Pip dropped a tray of stew on the floor.", "Raiders are at the north wall.", "Moth is bleeding in the infirmary.",
          "Lux complains about the soup again.", "It is raining and the roof leaks over the stove."]
LEAK = re.compile(r"thought|<\|channel|<channel\|>|Thinking Process|\*\*|^\s*\d+\.", re.I | re.M)

async def main(model):
    res = {}
    async with httpx.AsyncClient(timeout=60) as c:
        h = {"X-Hermes-Sampler": "rp"}
        tpl = (await c.post(E + "/apply-template", headers=h, json={"messages": [{"role": "system", "content": SYS}, {"role": "user", "content": SCENES[0]}], "chat_template_kwargs": {"enable_thinking": False}})).json()
        res["template_tail"] = tpl.get("prompt", "")[-60:]
        for arm in ("chat", "native_empty_thought", "bare_turn"):
            outs = []
            for i in range(N):
                msgs = [("system", SYS), ("user", SCENES[i % len(SCENES)])]
                if arm == "chat":
                    r = (await c.post(E + "/v1/chat/completions", headers=h, json={"messages": [{"role": a, "content": t} for a, t in msgs], "max_tokens": 64, "chat_template_kwargs": {"enable_thinking": False}})).json()
                    m = r["choices"][0]["message"]; t = (m.get("reasoning_content") or "") + "|" + (m.get("content") or "")
                else:
                    p = gemma_prompt(msgs)
                    if arm == "bare_turn":
                        p = p[: -len("<|channel>thought\n<channel|>")]
                    r = (await c.post(E + "/completion", headers=h, json={"prompt": p, "n_predict": 64})).json()
                    t = r.get("content", "")
                outs.append(t)
            leaks = sum(1 for t in outs if LEAK.search(t)); empty = sum(1 for t in outs if not t.strip(" |"))
            first = [t.split("\n")[0] for t in outs]
            res[arm] = {"n": N, "thought_or_list_leak": leaks, "empty": empty,
                        "first_line_starts_Hm": sum(1 for f in first if f.strip(" |").startswith("Hm")), "samples": outs[:5]}
    print(json.dumps({"model": model, **res}, indent=1))

asyncio.run(main(sys.argv[1]))

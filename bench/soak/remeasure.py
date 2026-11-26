"""Re-send recorded soak calls with the current prompt wording, to re-measure a prompt fix on the real model cheaply.

    python remeasure.py --calls RUN/calls.jsonl --url URL [--n 6] --out OUT.json

For each case, the recorded messages of matching calls get the old wording swapped for the current one (taken from the
agent's adapter), and both versions are sent through the agent's own HttpLLM with the same kind, max_tokens and stop.
"""
from __future__ import annotations

import argparse, asyncio, json, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "agent"))
from hermesvoice_agent.core.config import LLMConfig  # noqa: E402
from hermesvoice_agent.core.llm import HttpLLM  # noqa: E402
from hermesvoice_agent.games.rimworld.adapter import RimWorldAdapter  # noqa: E402

A = RimWorldAdapter()
MAXT = {"recap": 90, "lore": 24, "thought": 32, "diary": 160}   # the agent's caps for these kinds
OLD_RECAP = ("Write exactly 3 lines, one short sentence each, under 20 words, most recent last, the last line ending on "
             "what is still unresolved. No title, no numbering, no quotes.")
OLD_LORE = "a 2 to 5 word name, a colon, then what it refers to in under 10 words"
NEW_LORE = ("a 2 to 5 word name the colonists would actually say (a nickname, never a label such as an incident or event "
            "name), a colon, then what it refers to in under 10 words")


def cases(calls, drunk_id, sober_tick):
    out = []
    for c in calls:
        msgs = [tuple(m) for m in c["messages"]]
        user = msgs[-1][1]
        if c["kind"] == "recap" and OLD_RECAP in user:
            out.append(("recap", c, msgs, msgs[:-1] + [("user", user.replace(OLD_RECAP, A.recap_request))]))
        elif c["kind"] == "lore" and OLD_LORE in user:
            out.append(("lore", c, msgs, msgs[:-1] + [("user", user.replace(OLD_LORE, NEW_LORE))]))
        elif c["kind"] in ("thought", "diary") and c.get("actor") == drunk_id and c["tick"] < sober_tick:
            for line in user.splitlines():
                if line.startswith("Write it the way "):
                    name, phrase = line[len("Write it the way "):].split(" would: ", 1)
                    new = (f"Write it the way {name} would: right now very drunk: words slur badly, barely coherent, much "
                           f"simpler and looser than usual, so {phrase}")
                    out.append((f"drunk_{c['kind']}", c, msgs, msgs[:-1] + [("user", user.replace(line, new))]))
    return out


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--calls", required=True)
    ap.add_argument("--url", required=True)
    ap.add_argument("--n", type=int, default=6)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    rd = Path(a.calls).parent
    meta = json.loads((rd / "meta.json").read_text())
    pb = meta["pairs"]["B"]
    calls = [json.loads(l) for l in Path(a.calls).read_text().splitlines()]
    llm = HttpLLM(LLMConfig(base_url=a.url, mode="openai", timeout_s=120))
    rows = []
    try:
        for name, c, old, new in cases(calls, pb["drunk"], pb["drunk_sober_at_tick"]):
            for arm, msgs in (("old", old), ("new", new)):
                for i in range(a.n):
                    comp = await llm.complete(msgs, slot=0, kind=c["kind"], max_tokens=MAXT.get(c["kind"], 120),
                                              stop=["\n"] if c["kind"] == "lore" else None)
                    rows.append({"case": name, "call_id": c["call_id"], "arm": arm, "rep": i, "text": comp.text})
    finally:
        await llm.aclose()
    Path(a.out).write_text(json.dumps(rows, indent=1))
    print(len(rows), "rows")


if __name__ == "__main__":
    asyncio.run(main())

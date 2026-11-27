"""Try the documentary voice on recorded events: one line per moment and a day recap, against a fake bridge.

    python -m rimworldllm_agent.games.rimworld.comment_try --events tests/data/comment_events_sample.json \\
        [--llm-url http://127.0.0.1:8080/v1 [--model m]]

Without --llm-url a scripted FakeLLM answers (a wiring check). The gaps are zeroed so every moment speaks.
Nothing here touches a real game.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import random
import tempfile
from pathlib import Path

import httpx

from ...core.bridge import BridgeClient
from ...core.calllog import CallLog
from ...core.config import Config
from ...core.director import Director
from ...core.fakes import FakeLLM
from ...core.llm import HttpLLM
from .adapter import RimWorldAdapter
from .fakes import FakeBridge


async def try_events(events: list[dict], llm) -> list[dict]:
    """[{"event": summary, "kind", "mode": moment|recap, "line": text or None}] in order."""
    with tempfile.TemporaryDirectory() as tmp:
        cfg = Config(data_dir=Path(tmp), bridge_url="http://fake", bridge_token="tok", wants=False,
                     commentary_min_gap_real_s=0.0, commentary_min_gap_game=0)
        fb = FakeBridge("tok")
        adapter = RimWorldAdapter()
        bridge = BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", "tok", adapter.protocol)
        real = [0.0]
        d = Director(cfg, bridge, llm, adapter, clock=lambda: real[0], rng=random.Random(1))
        await d.start()
        d.steward.vv = {"playing": True, "settings": {}}
        out: list[dict] = []
        try:
            async def step(game: int):
                real[0] += 40.0                        # clear the 30 s floor
                bridge.env.game = game
                n = len(d.commentary.log)
                d.commentary.run_once()
                await d.drain()
                return d.commentary.log[n:], d.commentary.drops[-1:] if len(d.commentary.log) == n else []
            last = 0
            for e in events:
                last = max(last, int(e.get("game", 0)))
                before = len(d.commentary.pending)
                d.commentary.note(e)
                if len(d.commentary.pending) == before:
                    out.append({"event": e.get("summary", e.get("type")), "kind": e.get("type"), "mode": "moment", "line": None})
                    continue
                said, drop = await step(last)
                out.append({"event": e.get("summary", e.get("type")), "kind": e.get("type"), "mode": "moment",
                            "line": said[0]["text"] if said else None, "drop": drop[0] if drop else None})
            said, _ = await step((last // d.day + 1) * d.day + 1)      # the next morning: the recap of the day
            out.append({"event": "(the day ends)", "kind": "recap", "mode": "recap", "line": said[0]["text"] if said else None})
        finally:
            await bridge.aclose()
        return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="comment_try", description=__doc__.split("\n")[0])
    ap.add_argument("--events", required=True, help="recorded events (JSON list, GET /events shape)")
    ap.add_argument("--llm-url", default="", help="OpenAI-compatible base url; empty = FakeLLM")
    ap.add_argument("--model", default="")
    a = ap.parse_args(argv)
    events = json.loads(Path(a.events).read_text())
    if a.llm_url:
        cfg = Config(bridge_url="http://fake", bridge_token="tok")
        cfg.llm.base_url = a.llm_url
        if a.model:
            cfg.llm.model = a.model
        with tempfile.TemporaryDirectory() as tmp:
            llm = HttpLLM(cfg.llm, CallLog(Path(tmp) / "calls.jsonl"))
            rows = asyncio.run(try_events(events, llm))
    else:
        rows = asyncio.run(try_events(events, FakeLLM()))
    for r in rows:
        why = f" (dropped: {r['drop']})" if r.get("drop") else ""
        print(f"[{r['kind']}] {r['event']}\n    -> {r['line'] if r['line'] else '(no comment)' + why}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

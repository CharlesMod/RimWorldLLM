"""Try the steward on a recorded digest: one cycle against a fake bridge, with the real tool backend.

    python -m rimworldllm_agent.games.rimworld.steward_try --digest tests/data/steward_digest_sample.json \\
        [--goals goals.json] [--chat "who cooks?"] [--playing] --llm-url http://127.0.0.1:8080/v1 [--model m]

Phase 4 samples: --digest tests/data/steward_digest_trader.json | steward_digest_orbital.json | steward_digest_settlement.json
(a visiting bulk trader; an exotic orbital trader; no traders and an accepted delivery quest to Rivermouth). The fake bridge
answers /trade/offer and /caravan/plan from the digest and accepts trade and caravan acts.

Prints the system prompt size, the user turn, each tool call with its arguments and the fake bridge's result, and the
final chat line. Nothing here touches a real game.
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
from ...core.cli import make_tool_backend
from ...core.config import Config
from ...core.director import Director
from ...core.fakes import FakeLLM
from .adapter import RimWorldAdapter
from .fakes import FakeBridge


class _Tap:
    """Wraps the tool backend to keep the first request (the system and user turns the model saw)."""

    def __init__(self, inner):
        self.inner, self.first = inner, None

    async def chat(self, messages, **kw):
        if self.first is None and kw.get("slot") is not None:     # the protocol probe of --mode auto carries no slot
            self.first = [dict(m) if isinstance(m, dict) else m for m in messages]
        return await self.inner.chat(messages, **kw)

    async def aclose(self):
        await self.inner.aclose()


async def try_once(digest: dict, goals: dict | None, chat: str, playing: bool, llm_url: str, model: str = "", mode: str = "auto",
                   dialogs: list[dict] | None = None, paused: bool = False) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        cfg = Config(data_dir=Path(tmp), bridge_url="http://fake", bridge_token="tok", wants=False, steward=True,
                     steward_min_gap_real_s=0.0)
        cfg.llm.base_url = llm_url
        if model:
            cfg.llm.model = model
        fb = FakeBridge("tok")
        fb.digest = digest
        if goals:
            fb.goals_board.update({k: v for k, v in goals.items() if k in ("goals", "plan", "orders", "log")})
        adapter = RimWorldAdapter()
        bridge = BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", "tok", adapter.protocol)
        real = [0.0]
        d = Director(cfg, bridge, FakeLLM(), adapter, clock=lambda: real[0], rng=random.Random(1))
        await d.start()
        tap = _Tap(make_tool_backend(cfg))
        d.tool_backend, d.tool_mode = tap, mode
        try:
            fb.playing = False
            await d.steward.step()                 # first sight: the chat cursor starts here
            fb.playing = playing
            if dialogs:
                # the steady state before a window opens: it was already playing and had looked once, then the game paused for the window
                d.steward.was_playing, d.steward.last_game = True, d.bridge.env.game
                await d.steward.step()             # it sees "playing" (no cycle is due), so it collects the event below
                fb.paused = paused
                fb.dialogs = list(dialogs)
                for x in dialogs:
                    fb.add_event("dialog", [], "Needs an answer: " + str(x.get("title", "")), [], severity=0.6,
                                 data={"dialog": x["id"], "kind": x.get("kind"), "title": x.get("title"),
                                       "options": [o.get("label") for o in x.get("options") or []], "default": x.get("default"), "timeout_s": 90})
            if chat:
                fb.add_chat("player", chat)
            await d.step()
            await d.drain()
        finally:
            await tap.aclose()
            await bridge.aclose()
        trace = d.steward.stats.trace[-1] if d.steward.stats.trace else {}
        return {"messages": tap.first, "trace": trace, "chat": list(fb.chat), "acts": list(fb.act_log), "board": fb.goals_board, "dialogs_left": [x["id"] for x in fb.dialogs]}


def report(out: dict) -> int:
    msgs, tr = out["messages"], out["trace"]
    if not msgs:
        print("no cycle ran:", tr.get("error", "the steward did not wake"))
        return 1
    print(f"SYSTEM PROMPT: {len(msgs[0]['content'] if isinstance(msgs[0], dict) else msgs[0][1])} chars")
    print("USER TURN:\n" + (msgs[1]["content"] if isinstance(msgs[1], dict) else msgs[1][1]) + "\n")
    print(f"CYCLE: {tr.get('reason')} ({'playing' if tr.get('playing') else 'advisor'}), ended {tr.get('outcome')} {tr.get('reason_ended') or ''}")
    for tool, args, result in tr.get("executed", []):
        print(f"TOOL {tool} {json.dumps(args)} -> {json.dumps(result)}")
    for e in out["chat"]:
        if e["who"] != "player":
            print(f"CHAT [{e['who']}] {e['text']}")
    for f in tr.get("failures") or []:
        print("REJECTED:", f)
    if "dialogs_left" in out:
        print("DIALOGS STILL OPEN:", ", ".join(out["dialogs_left"]) or "none")
    if out["acts"]:
        print("BRIDGE ACTS:", json.dumps(out["acts"]))
    if tr.get("error"):
        print("ERROR:", tr["error"])
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="steward_try", description=__doc__.split("\n")[0])
    ap.add_argument("--digest", required=True, help="a recorded GET /digest body (JSON file)")
    ap.add_argument("--goals", help="a goals board JSON file (goals, plan, orders, log)")
    ap.add_argument("--chat", default="", help="a player chat line to answer")
    ap.add_argument("--playing", action="store_true", help="the narrator is playing (otherwise advisor mode: chat only)")
    ap.add_argument("--llm-url", required=True, help="OpenAI-compatible base url")
    ap.add_argument("--model", default="")
    ap.add_argument("--mode", default="auto", choices=["auto", "grammar", "json", "native"])
    a = ap.parse_args(argv)
    digest = json.loads(Path(a.digest).read_text())
    goals = json.loads(Path(a.goals).read_text()) if a.goals else None
    if not a.chat and not a.playing:
        ap.error("nothing would wake the steward: give --chat or --playing")
    out = asyncio.run(try_once(digest, goals, a.chat, a.playing, a.llm_url, a.model, a.mode))
    return report(out)


if __name__ == "__main__":
    raise SystemExit(main())

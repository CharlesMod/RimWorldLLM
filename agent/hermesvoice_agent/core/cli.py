"""Game-agnostic CLI shell: run | test-endpoint | replay, for the adapter passed to ``main``.
``hermesvoice_agent.cli`` is the entry point that supplies one."""
from __future__ import annotations

import argparse
import asyncio
import logging

from .bridge import BridgeClient
from .calllog import CallLog
from .config import Config, load_config
from .adapter import GameAdapter
from .director import Director
from .endpoint_test import test_endpoint
from .llm import HttpLLM


def _cfg(args) -> Config:
    cfg = load_config(args.config)
    if getattr(args, "llm_url", None):
        cfg.llm.base_url = args.llm_url
    if getattr(args, "llm_mode", None):
        cfg.llm.mode = args.llm_mode
    if getattr(args, "bridge_json", None):
        cfg.bridge_json = args.bridge_json
    return cfg


async def _run(cfg: Config, adapter: GameAdapter) -> int:
    cfg.data_dir.mkdir(parents=True, exist_ok=True)
    bridge = BridgeClient.from_config(cfg, adapter.protocol, adapter.default_bridge_json())
    llm = HttpLLM(cfg.llm, CallLog(cfg.data_dir / "calls.jsonl"))
    d = Director(cfg, bridge, llm, adapter)
    try:
        await d.run()
    finally:
        await bridge.aclose()
        await llm.aclose()
    return 0


async def _test(cfg: Config) -> int:
    r = await test_endpoint(cfg.llm)
    print(f"[{r.status.upper()}] {r.cause}")
    if r.base_url:
        print(f"  endpoint: {r.base_url}")
    return 0 if r.ok else (2 if r.status == "amber" else 1)


async def _replay(cfg: Config, adapter: GameAdapter, path: str, use_llm: bool) -> int:
    llm = HttpLLM(cfg.llm, CallLog(cfg.data_dir / "replay-calls.jsonl")) if use_llm else None
    for item in await adapter.replay(path, cfg, llm):
        if "dropped" in item:
            print(f"  (actor {item['actor']} dropped: {item['dropped']})")
        else:
            tag = f" -> {item['intent']}" if item["intent"] != "none" else ""
            print(f"{item['name']}: {item['text']}{tag}")
    return 0


def main(adapter: GameAdapter, argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="hermesvoice-agent")
    ap.add_argument("--config", help="TOML or JSON config file (or $HERMESVOICE_CONFIG)")
    ap.add_argument("-v", "--verbose", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run", help="run the agent against the bridge")
    r.add_argument("--bridge-json"); r.add_argument("--llm-url"); r.add_argument("--llm-mode", choices=["openai", "native"])
    t = sub.add_parser("test-endpoint", help="check the LLM endpoint (the Test button)")
    t.add_argument("--llm-url")
    p = sub.add_parser("replay", help="replay recorded events against a fake bridge")
    p.add_argument("events"); p.add_argument("--llm", action="store_true", help="use the configured LLM, not the offline fake")
    p.add_argument("--llm-url"); p.add_argument("--llm-mode", choices=["openai", "native"])
    args = ap.parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(levelname)s %(message)s")
    logging.getLogger("httpx").setLevel(logging.WARNING)
    cfg = _cfg(args)
    if args.cmd == "run":
        return asyncio.run(_run(cfg, adapter))
    if args.cmd == "test-endpoint":
        return asyncio.run(_test(cfg))
    return asyncio.run(_replay(cfg, adapter, args.events, args.llm))



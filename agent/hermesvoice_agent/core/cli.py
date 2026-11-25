"""Game-agnostic CLI shell: run | test-endpoint | replay, for the adapter passed to ``main``.
``hermesvoice_agent.cli`` is the entry point that supplies one."""
from __future__ import annotations

import argparse
import asyncio
import logging
import time
from pathlib import Path

from .bridge import BridgeClient
from .calllog import CallLog
from .config import Config, load_config, read_bridge_json
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


log = logging.getLogger("hermesvoice.cli")
BRIDGE_POLL_S = 2.0


async def wait_for_bridge_json(path: Path, timeout: float = 0.0, *, poll: float = BRIDGE_POLL_S,
                               sleep=asyncio.sleep, clock=time.monotonic) -> bool:
    """Wait until bridge.json exists and parses (the game writes it when it starts). Polls quietly, logs once.
    timeout 0 waits forever. Returns False if the timeout passes first."""
    start = clock()
    announced = False
    while True:
        try:
            read_bridge_json(path)
            if announced:
                log.info("bridge.json appeared, starting")
            return True
        except (OSError, ValueError):
            pass
        if timeout and clock() - start >= timeout:
            return False
        if not announced:
            log.info("waiting for %s (the game has not started the bridge yet)", path)
            announced = True
        await sleep(poll)


async def _run(cfg: Config, adapter: GameAdapter, bridge_timeout: float = 0.0) -> int:
    cfg.data_dir.mkdir(parents=True, exist_ok=True)
    if not cfg.bridge_url:
        path = Path(cfg.bridge_json or adapter.default_bridge_json())
        if not await wait_for_bridge_json(path, bridge_timeout):
            log.error("bridge.json did not appear at %s within %g s", path, bridge_timeout)
            return 1
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
        k = item.get("kind")
        if k == "reflection":
            print(f"[reflection] {item['name']}: {item['bio']}  wants: {'; '.join(item['wants'])}"
                  f"  (relations {item['relations']}, merged {item['merged']})")
        elif k == "diary":
            print(f"[diary day {item['day'] + 1}] {item['name']}: {item['text']}")
        elif k == "chronicle":
            print(f"[chronicle day {item['day'] + 1}{' major' if item['entry_kind'] == 'major' else ''}] {item['text']}")
        elif "dropped" in item:
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
    r.add_argument("--bridge-timeout", type=float, default=0.0,
                   help="seconds to wait for bridge.json before giving up with exit 1 (default 0: wait forever)")
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
        return asyncio.run(_run(cfg, adapter, args.bridge_timeout))
    if args.cmd == "test-endpoint":
        return asyncio.run(_test(cfg))
    return asyncio.run(_replay(cfg, adapter, args.events, args.llm))



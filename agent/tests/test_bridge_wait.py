"""`run` waits for bridge.json (the game may not have started yet) instead of crashing."""
import asyncio
import json
import logging

from rimworldllm_agent.core.cli import _run, main, wait_for_bridge_json
from rimworldllm_agent.core.config import Config
from rimworldllm_agent.games.rimworld import RimWorldAdapter


class Clock:
    def __init__(self):
        self.t = 0.0
        self.sleeps = []

    def now(self):
        return self.t

    async def sleep(self, s):
        self.sleeps.append(s)
        self.t += s


def test_waits_polling_every_2s_and_logs_once(tmp_path, caplog):
    p = tmp_path / "bridge.json"
    c = Clock()

    async def sleeper(s):
        await c.sleep(s)
        if len(c.sleeps) == 4:
            p.write_text(json.dumps({"port": 1}))
    with caplog.at_level(logging.INFO, logger="rimworldllm.cli"):
        ok = asyncio.run(wait_for_bridge_json(p, 0, sleep=sleeper, clock=c.now))
    assert ok and c.sleeps == [2.0] * 4
    assert sum("waiting for" in r.message for r in caplog.records) == 1


def test_partial_file_is_waited_on(tmp_path):
    p = tmp_path / "bridge.json"
    p.write_text("{")
    c = Clock()

    async def sleeper(s):
        await c.sleep(s)
        p.write_text(json.dumps({"port": 1}))
    assert asyncio.run(wait_for_bridge_json(p, 0, sleep=sleeper, clock=c.now))


def test_timeout_gives_false(tmp_path):
    c = Clock()
    ok = asyncio.run(wait_for_bridge_json(tmp_path / "none.json", 7, sleep=c.sleep, clock=c.now))
    assert not ok and c.sleeps == [2.0] * 4


def test_run_exits_nonzero_on_bridge_timeout(tmp_path, monkeypatch):
    async def fake_wait(path, timeout, **kw):
        assert timeout == 3.0
        return False
    monkeypatch.setattr("rimworldllm_agent.core.cli.wait_for_bridge_json", fake_wait)
    cfg = Config(data_dir=tmp_path / "d", bridge_json=tmp_path / "bridge.json")
    assert asyncio.run(_run(cfg, RimWorldAdapter(), 3.0)) == 1


def test_main_run_passes_default_timeout_zero(tmp_path, monkeypatch):
    seen = {}

    async def fake_run(cfg, adapter, timeout=0.0):
        seen["t"] = timeout
        return 0
    monkeypatch.setattr("rimworldllm_agent.core.cli._run", fake_run)
    assert main(RimWorldAdapter(), ["run"]) == 0 and seen["t"] == 0.0
    assert main(RimWorldAdapter(), ["run", "--bridge-timeout", "5"]) == 0 and seen["t"] == 5.0

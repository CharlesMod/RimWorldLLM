"""Wants wiring: cli builds the tool-loop backend on by default; config and the bridge's wants_enabled switch it off."""
import asyncio

from conftest import run
from rimworldllm_agent.core import cli
from rimworldllm_agent.core.config import Config
from rimworldllm_agent.core.toolloop import OpenAIBackend
from rimworldllm_agent.games.rimworld.adapter import RimWorldAdapter
from test_wants import Fake, call, DONE


def _run_cli(tmp_path, monkeypatch, **kw):
    seen = {}

    class StubDirector:
        def __init__(self, cfg, bridge, llm, adapter):
            self.tool_backend, self.tool_mode = None, "unset"
            seen["d"] = self

        async def run(self):
            return None
    monkeypatch.setattr(cli, "Director", StubDirector)
    cfg = Config(data_dir=tmp_path / "d", bridge_url="http://fake", bridge_token="tok", **kw)
    cfg.llm.base_url = "http://engine:4480"
    assert asyncio.run(cli._run(cfg, RimWorldAdapter())) == 0
    return seen["d"]


def test_wants_wired_on_by_default_at_the_agents_engine(tmp_path, monkeypatch):
    d = _run_cli(tmp_path, monkeypatch)
    assert isinstance(d.tool_backend, OpenAIBackend) and d.tool_mode == "auto"
    assert d.tool_backend.base == "http://engine:4480/v1"


def test_wants_off_via_config(tmp_path, monkeypatch):
    d = _run_cli(tmp_path, monkeypatch, wants=False, steward=False)
    assert d.tool_backend is None


def test_steward_keeps_the_backend_when_wants_are_off(tmp_path, monkeypatch):
    d = _run_cli(tmp_path, monkeypatch, wants=False)       # the steward needs the tool loop even without wants
    assert isinstance(d.tool_backend, OpenAIBackend)


def test_wants_off_via_bridge_config(world):
    async def go():
        w = await world.go()
        d = w.d
        d.tool_backend = Fake([call("do_nothing"), DONE]); d.tool_mode = "grammar"
        w.fb.tick += 100000
        d.bridge.env.game = w.fb.tick
        d.weights = {i: 2.0 for i in d.minds}
        d.bridge_cfg["wants_enabled"] = True
        on = d.idle.due_wants()
        d.bridge_cfg["wants_enabled"] = False
        off = d.idle.due_wants()
        return on, off
    on, off = run(go())
    assert on and off == []


def test_seek_out_reaches_the_fake_with_everything_the_mod_needs(world):
    async def go():
        w = await world.go()
        d = w.d
        d.tool_backend = Fake([call("seek_out", target="Bram", topic="the bread"), DONE]); d.tool_mode = "grammar"
        w.fb.tick += 100000
        d.bridge.env.game = w.fb.tick
        d.weights = {i: 2.0 for i in d.minds}
        await d.step(); await d.drain()
        return w
    w = run(go())
    b = w.fb.social_log[0]
    assert b["pawn"] in w.fb.pawns and b["target"] in w.fb.pawns and b["action"] == "seek_out" and b["topic"] and b["op_id"]

import httpx
import pytest

from conftest import run
from rimworldllm_agent.core.bridge import BridgeClient, BridgeError, EventPoller
from rimworldllm_agent.games.rimworld import RimWorldAdapter
from rimworldllm_agent.games.rimworld.fakes import FakeBridge


def mk(fb=None, token="tok"):
    fb = fb or FakeBridge("tok")
    return fb, BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", token, RimWorldAdapter().protocol)


def test_envelope_and_auth():
    async def go():
        fb, b = mk()
        fb.tick, fb.speed, fb.paused = 777, 3, True
        await b.config()
        assert (b.env.game, b.env.speed, b.env.paused, b.env.epoch) == (777, 3, True, 1)
        assert (await b.health())["mod"] == "RimWorldLLM"
        _, bad = mk(fb, "wrong")
        with pytest.raises(BridgeError) as e:
            await bad.config()
        assert e.value.code == "unauthorized"
        with pytest.raises(BridgeError) as e:
            await b.actor(99)
        assert e.value.code == "not_found"
    run(go())


def test_write_op_id_expiry_duplicate():
    async def go():
        fb, b = mk(); fb.add_pawn(1, "Ana"); fb.tick = 1000
        await b.config()
        r = await b.say(1, "Hi.", intent="kind", expires_at=1500, op_id="op-1")
        assert r["applied"]["effect"] == "kind"
        body = fb.say_log[0]
        assert body["op_id"] == "op-1" and body["expires_tick"] == 1500
        with pytest.raises(BridgeError) as e:
            await b.say(1, "Hi.", op_id="op-1")
        assert e.value.code == "duplicate"
        fb.tick = 2000
        with pytest.raises(BridgeError) as e:
            await b.say(1, "late", expires_at=1500)
        assert e.value.code == "expired"
        await b.say(1, "player talk never expires")           # no expires_tick sent
        assert "expires_tick" not in fb.say_log[-1] and fb.say_log[-1]["op_id"]
        fb.say_conflict_for.add(1)
        with pytest.raises(BridgeError) as e:
            await b.say(1, "x")
        assert e.value.code == "conflict"
    run(go())


def test_poller_paging_and_gap():
    async def go():
        fb, b = mk()
        for i in range(450):
            fb.add_event("injury", [(1, "victim")], f"e{i}", [1])
        p = EventPoller(b)
        evs = await p.poll()
        assert len(evs) == 450 and [e["seq"] for e in evs] == list(range(1, 451))
        assert p.since == 450 and not p.last_gap
        assert await p.poll() == []
        fb.add_event("injury", [(1, "victim")], "new", [1])
        assert [e["summary"] for e in await p.poll()] == ["new"]
        # gap: the mod only keeps the last N events
        fb.max_kept = 10
        fb.events = fb.events[-10:]
        fb.seq += 5000
        fb.events[-1]["seq"] = fb.seq
        p2 = EventPoller(b, since=3)
        await p2.poll()
        assert p2.last_gap and p2.gaps == 1
    run(go())


def test_epoch_change_flagged_and_mind_rev_conflict():
    async def go():
        fb, b = mk()
        await b.config()
        assert not b.epoch_changed
        fb.reload_game()
        await b.config()
        assert b.epoch_changed and b.env.epoch == 2
        assert await b.get_mind(1) == (None, 0)
        assert await b.put_mind(1, {"a": 1}, 0) == 1
        with pytest.raises(BridgeError) as e:
            await b.put_mind(1, {"a": 2}, 0)
        assert e.value.code == "conflict"
    run(go())


def test_unreachable():
    async def go():
        def boom(req):
            raise httpx.ConnectError("no")
        b = BridgeClient(httpx.AsyncClient(transport=httpx.MockTransport(boom)), "http://x", "t")
        with pytest.raises(BridgeError) as e:
            await b.config()
        assert e.value.code == "unreachable"
    run(go())


def test_from_config_reads_bridge_json(tmp_path):
    import json
    from rimworldllm_agent.core.config import Config
    p = tmp_path / "bridge.json"
    p.write_text(json.dumps({"port": 9766, "bind": "127.0.0.1", "token": "abc", "pid": 1, "version": "2.0.0", "save_epoch": 1}))
    b = BridgeClient.from_config(Config(bridge_json=p))
    assert b.base_url == "http://127.0.0.1:9766" and b.token == "abc"

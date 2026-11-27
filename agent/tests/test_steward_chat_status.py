"""The steward's "Narrator is thinking" hook: POST /chat/status around the model call, best effort."""
import json

import httpx

from conftest import run
from rimworldllm_agent.core.bridge import BridgeClient
from rimworldllm_agent.core.toolloop import Reply
from rimworldllm_agent.games.rimworld.adapter import RimWorldAdapter
from test_steward import DONE, boot, chat, cycle, final, lines


def states(w):
    return [(s["state"], s["stream_id"]) for s in w.fb.chat_status]


def test_thinking_then_idle_then_final(world):
    async def go():
        w, st, fake = await boot(world, [final("All quiet.")])
        seen = []
        orig = fake.chat

        async def spy(*a, **k):
            seen.append(list(w.fb.chat_status))
            return await orig(*a, **k)
        fake.chat = spy
        w.fb.playing = False
        chat(w, "status?")
        n = len(w.fb.chat)
        # record chat length at the moment idle arrives
        orig_vi = w.fb._vivarium
        at_idle = []

        def vi(req, path, body):
            if path == "/chat/status" and body.get("state") == "idle":
                at_idle.append(len(w.fb.chat))
            return orig_vi(req, path, body)
        w.fb._vivarium = vi
        await cycle(w)
        assert [s["state"] for s in seen[0]] == ["thinking"]      # already posted when the model is first called
        assert w.fb.chat_status[0]["who"] == "narrator"
        assert [s for s, _ in states(w)] == ["thinking", "idle"]
        assert len({sid for _, sid in states(w)}) == 1
        assert at_idle == [n]                                       # idle came before the narrator line
        assert lines(w, "narrator") == ["All quiet."]
        await cycle_again(w, st)
    run(go())


async def cycle_again(w, st):
    chat(w, "again?")
    await cycle(w)
    ids = [sid for s, sid in states(w) if s == "thinking"]
    assert len(ids) == 2 and ids[0] != ids[1]


def test_idle_when_backend_raises(world):
    async def go():
        w, st, fake = await boot(world, [DONE])

        async def boom(*a, **k):
            raise RuntimeError("model down")
        fake.chat = boom
        w.fb.playing = False
        chat(w, "hello?")
        try:
            await cycle(w)
        except Exception:
            pass
        assert [s for s, _ in states(w)] == ["thinking", "idle"]
    run(go())


def test_idle_when_no_final(world):
    async def go():
        w, st, fake = await boot(world, [DONE])
        w.fb.playing = False
        chat(w, "hello?")
        await cycle(w)
        assert [s for s, _ in states(w)] == ["thinking", "idle"]
    run(go())


def test_rejecting_bridge_does_not_break_cycle(world):
    async def go():
        w, st, fake = await boot(world, [final("Still here.")])
        w.fb.chat_status_enabled = False
        w.fb.playing = False
        chat(w, "ping")
        await cycle(w)
        assert lines(w, "narrator") == ["Still here."]
        assert w.fb.chat_status == []
    run(go())


def test_client_bodies():
    sent = []

    def handler(req):
        sent.append((req.url.path, json.loads(req.content)))
        return httpx.Response(200, json={"ok": True, "stream_id": "s1", "state": "idle", "seq": 1})
    b = BridgeClient(httpx.AsyncClient(transport=httpx.MockTransport(handler)), "http://x", "t", RimWorldAdapter().protocol)

    async def go():
        await b.post_chat_status("narrator", "thinking", "s1")
        await b.post_chat_status("colonist", "typing", "s2", to=4, progress=0.5, op_id="o1")
        await b.post_chat("narrator", "hi")
        await b.post_chat("narrator", "hi", stream_id="s1")
    run(go())
    assert sent[0][0] == "/chat/status"
    assert {k for k in sent[0][1]} == {"who", "state", "stream_id", "op_id"}
    assert sent[1][1] == {"who": "colonist", "state": "typing", "stream_id": "s2", "to": 4, "progress": 0.5, "op_id": "o1"}
    assert "stream_id" not in sent[2][1] and sent[3][1]["stream_id"] == "s1"

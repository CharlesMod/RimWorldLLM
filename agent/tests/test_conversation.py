import random

from conftest import run
from hermesvoice_agent.core.conversation import Conversation, opener_obliges, repetitive, template_memory, template_relation


def mk(**kw):
    return Conversation("c1", 1, 2, {1: "Ana", 2: "Bram"}, **kw)


def test_opener_obliges():
    assert opener_obliges("Where is the medicine?")
    assert opener_obliges("Bring me a bedroll.")
    assert opener_obliges("Fine day.", "insult") and opener_obliges("Fine day.", "confide")
    assert not opener_obliges("Fine day.")


def test_append_only_turns_alternate():
    c = mk()
    c.add(1, "Hi"); c.add(2, "Hey")
    assert c.next_speaker() == 1 and c.listener_of(1) == 2
    assert c.turns_for_prompt() == [("Ana", "Hi"), ("Bram", "Hey")]


def test_turn_cap_six():
    c = mk(); rng = random.Random(0)
    for i in range(6):
        c.add(1 if i % 2 == 0 else 2, f"unique line number {i} about topic{i}")
        r = c.check_end(rng)
        if i < 5:
            assert r != "turn_cap"
    assert c.check_end(rng) == "turn_cap"


def test_obliged_reply_happens_even_if_chance_fails():
    class Never(random.Random):
        def random(self): return 0.999
    c = mk(); c.add(1, "Where is the medicine?")
    assert c.obliged and c.check_end(Never()) == ""
    c2 = mk(); c2.add(1, "Nice weather.")
    assert not c2.obliged and c2.check_end(Never()) == "chance"


def test_continue_chance_decays():
    c = mk()
    seen = []
    for i in range(5):
        c.add(1, f"x{i}"); seen.append(c.continue_chance())
    assert seen == sorted(seen, reverse=True) and seen[0] > seen[-1]


def test_repetition_check():
    assert repetitive("Get the medicine now", ["Get the medicine now"])
    assert repetitive("Get the medicine now please", ["get the medicine now"])
    assert not repetitive("Bram stole my rifle", ["Get the medicine now"])
    c = mk(); c.add(1, "Get the medicine from storage"); c.add(2, "Get the medicine from storage")
    assert c.check_end(random.Random(0)) == "repetition"


def test_apart_job_preempt():
    c = mk(); c.add(1, "Where is it?")
    rng = random.Random(0)
    assert c.check_end(rng, apart=True) == "apart"
    assert c.check_end(rng, job_changed=True) == "job_changed"
    assert c.check_end(rng, preempted=True) == "preempted"


def test_end_templates():
    c = mk(); c.add(1, "You are useless.", "insult"); c.add(2, "Shut it.")
    assert "Bram" in template_memory(c, 1) and "Ana" in template_memory(c, 2)
    assert "Wary" in template_relation(c, 2)[0]
    assert "Nothing" in template_relation(c, 1)[0]


def test_director_conversation_end_writes_memory_and_relation(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Where did you put the medicine?", "Bram put it in the freezer.",
                                "That is a stupid place for it.", "Then move it yourself.",
                                "I will. Later.", "Do it today.", "Fine."]
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], 0.7)
        await w.d.step(); await w.d.drain(); await w.d.step()
        return w
    w = run(go())
    convs = {s["conv"] for s in w.d.said}
    assert len(convs - {None}) == 1
    n_say = len(w.fb.say_log)
    assert 2 <= n_say <= 6
    assert all(s["conversation_id"] == "c-1" for s in w.fb.say_log)
    speakers = [s["pawn"] for s in w.fb.say_log]
    assert speakers[:2] == [1, 2]                                 # opener obliged a reply (it was a question)
    for pid, other in ((1, 2), (2, 1)):
        mind = w.d.minds[pid]
        conv_mems = [m for m in mind.memories if m.etype == "conversation"]
        assert len(conv_mems) == 1                                # one memory per pawn
        assert str(other) in mind.relations and mind.relations[str(other)].feeling   # one relation update
    assert w.d.leases.leases == {}                                # both leases ended
    # two windows: each pawn's lines came from its own slot
    slots = {c["actor"]: c["slot"] for c in w.llm.calls if c["kind"] == "line"}
    assert slots[1] != slots[2]


def test_conversation_ends_when_apart(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Where did you put the medicine?", "In the freezer, Ana.", "Good.", "ok"]
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana spoke with Bram.", [1, 2], 0.7)
        orig = w.fb._handle
        def handle(req):
            if req.url.path == "/scene/1" and len(w.fb.say_log) >= 1:
                w.fb.scenes[1] = {"room": {"role": "Kitchen"}, "present": [], "time_of_day": "m", "weather": "c", "trends": {}}
            return orig(req)
        import httpx
        w.bridge.http._transport = httpx.MockTransport(handle)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.fb.say_log) == 1          # they parted after the opener

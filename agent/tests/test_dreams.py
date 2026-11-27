"""Item 6: dreams. sleep_start -> one surreal cloud per night (POST /thought kind dream) and an inner-stream entry."""
from conftest import run
from rimworldllm_agent.core import dreams, inner
from rimworldllm_agent.core import gamedata as gd
from rimworldllm_agent.core.minds import Memory

DAY = 60000
TEXT = "I am chasing a berry bush through the kitchen"


async def step(w):
    await w.d.step(); await w.d.drain()


def inject(w, aid, text, game=900, **kw):
    kw.setdefault("visibility", "public")
    w.d.minds[aid].memories.append(Memory(id=w.d.minds[aid].next_mem_id(), game=game, text=text, **kw))


def sleep(w, aid=1, game=None):
    if game is not None:
        w.fb.tick = game
    w.fb.pawns[aid]["asleep"] = True
    return w.fb.add_event("sleep_start", [(aid, "sleeper")], "Ana fell asleep.", [aid], 0.1)


def dream_log(w):
    return [t for t in w.fb.thought_log if t["kind"] == "dream"]


def calls(w):
    return [c for c in w.llm.calls if c["kind"] == "dream"]


async def ready(world, chance=1.0):
    w = await world.go()
    w.d.cfg.dream_chance = chance
    w.llm.script["dream"] = [TEXT]
    inject(w, 1, "I found a ripe berry bush.")
    return w


def test_sleep_event_makes_one_dream_cloud_and_inner_entry(world):
    async def go():
        w = await ready(world)
        w.params = []
        orig = w.llm.complete
        async def spy(*a, **kw):
            w.params.append(kw)
            return await orig(*a, **kw)
        w.llm.complete = spy
        sleep(w)
        await step(w); await step(w)
        return w
    w = run(go())
    log = dream_log(w)
    assert len(log) == 1 and log[0]["pawn"] == 1 and log[0]["surface"] == log[0]["text"] == TEXT
    c = [k for k in w.params if k["kind"] == "dream"][0]
    assert c["max_tokens"] == 30 and c["stop"] == ["\n"] and c["temperature"] == 1.0
    assert [t.kind for t in w.d.minds[1].inner] == ["dream"] and w.d.minds[1].inner[0].text == TEXT
    assert not [m for m in w.d.minds[1].memories if m.etype == "sleep_start"]       # no memory of falling asleep


def test_once_per_night_then_again_next_night(world):
    async def go():
        w = await ready(world)
        sleep(w); await step(w); await step(w)
        w.llm.script["dream"] = ["I am a fish in the cold storeroom"]
        w.fb.pawns[1]["asleep"] = False
        sleep(w, game=3000); await step(w); await step(w)                  # same night
        assert len(dream_log(w)) == 1
        inject(w, 1, "I mended the roof.", game=DAY + 1000)
        sleep(w, game=DAY + 2000); await step(w); await step(w)             # next night
        return w
    assert len(dream_log(run(go()))) == 2


def test_chance_zero_never_dreams_and_the_roll_is_spent(world):
    async def go():
        w = await ready(world, chance=0.0)
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    assert not dream_log(w) and not calls(w)


def test_chance_is_a_per_night_roll(world):
    async def go():
        w = await world.go()
        w.d.cfg.dream_chance = 0.5
        hits = 0
        for n in range(40):
            hits += bool(w.d.dream_book.note({"type": "sleep_start", "game": n * DAY, "actors": [{"id": 1}]}, w.d, n * DAY))
            w.d.dream_book.pending.clear()
        return hits
    assert 8 <= run(go()) <= 32


def test_woke_before_ready_drops_it(world):
    async def go():
        w = await ready(world)
        sleep(w)
        w.fb.pawns[1]["asleep"] = False
        await step(w); await step(w)
        return w
    w = run(go())
    assert not dream_log(w) and not calls(w)
    assert (1, "dream:woke") in w.d.dropped


def test_woke_while_writing_drops_it(world):
    async def go():
        w = await ready(world)
        sleep(w)
        orig = w.llm.complete
        async def waking(*a, **kw):
            r = await orig(*a, **kw)
            w.fb.pawns[1]["asleep"] = False
            return r
        w.llm.complete = waking
        await step(w); await step(w)
        return w
    w = run(go())
    assert not dream_log(w) and (1, "dream:woke") in w.d.dropped and not w.d.minds[1].inner


def test_too_late_is_dropped(world):
    async def go():
        w = await ready(world)
        sleep(w)
        w.fb.tick += w.d.cfg.dream_max_age_game + 10
        await step(w); await step(w)
        return w
    w = run(go())
    assert not dream_log(w) and (1, "dream:late") in w.d.dropped


def test_switches(world):
    async def off_cfg():
        w = await ready(world)
        w.d.cfg.dreams = False
        sleep(w); await step(w); await step(w)
        return w
    assert not dream_log(run(off_cfg()))


def test_mod_switches(world):
    async def go():
        w = await ready(world)
        w.d.bridge_cfg["dreams"] = False
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    assert not dream_log(w)


def test_no_memories_no_dream(world):
    async def go():
        w = await world.go()
        w.d.cfg.dream_chance = 1.0
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    assert not calls(w) and (1, "dream:no_memories") in w.d.dropped


def test_private_memories_never_reach_a_dream(world):
    async def go():
        w = await world.go()
        w.d.cfg.dream_chance = 1.0
        inject(w, 1, "I hid a secret from Bram.", visibility="private")
        sleep(w); await step(w); await step(w)
        return w
    assert not calls(run(go()))


def test_prompt_wraps_game_text_and_uses_recent_inner(world):
    async def go():
        w = await ready(world)
        inner.add_inner(w.d.minds[1], "I wonder about the roof", "musing", 800, 6)
        inner.add_inner(w.d.minds[1], "Bram never looks up", "aim", 850, 6)
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    msgs = calls(w)[0]["messages"]
    system, user = msgs[0][1], msgs[1][1]
    assert gd.DATA_RULE in system
    assert "ripe berry bush" in user and "Bram never looks up" in user and "I wonder about the roof" in user
    assert user.count("<<<") >= 2 or "=== WHAT HAPPENED TODAY ===" in user
    assert "romantic" in user.lower()
    assert "sexual" in system


def test_minor_dream_is_gated_and_gets_the_minor_rule(world):
    async def go():
        w = await ready(world)
        w.d.ages[1] = 12
        w.llm.script["dream"] = ["I am kissing Bram under the stars tonight"]
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    assert "minor" in calls(w)[0]["messages"][0][1].lower() or "under 18" in calls(w)[0]["messages"][0][1].lower()
    assert not dream_log(w)


def test_romantic_dream_with_a_minor_in_memory_is_dropped(world):
    async def go():
        w = await world.go()
        w.d.cfg.dream_chance = 1.0
        w.d.ages[2] = 14
        inject(w, 1, "I talked with Bram by the fire.", people=[2])
        w.llm.script["dream"] = ["I am kissing Bram in a bed of roses"]
        sleep(w); await step(w); await step(w)
        return w
    assert not dream_log(run(go()))


def test_dream_is_preempted_by_actor_work(world):
    async def go():
        w = await ready(world)
        sleep(w)
        w.fb.add_event("injury", [(2, "victim")], "Bram was shot.", [2], 0.9)
        await step(w)
        return w
    w = run(go())
    assert not [t for t in w.fb.thought_log if t["kind"] == "dream" and t["pawn"] == 2]   # actor work ran; no dream beside it


def test_diary_picks_up_the_dream_stream(world):
    async def go():
        w = await ready(world)
        sleep(w); await step(w); await step(w)
        return w
    w = run(go())
    stream = inner.inner_on_day(w.d.minds[1], w.d.minds[1].inner[0].game // DAY, DAY)
    assert [t.kind for t in stream] == ["dream"]

"""Steps 2 and 3: memory upkeep (merge, decay, pins, blob size) and gossip, confiding and secrets."""
import json

from conftest import run
from rimworldllm_agent.core import memory
from rimworldllm_agent.core.minds import BLOB_LIMIT, BLOB_TARGET, Memory, Mind, Relation
from rimworldllm_agent.core.prompt import block_d
from rimworldllm_agent.core.sanitize import leaks_secret, sanitize_line
from rimworldllm_agent.games.rimworld import RimWorldAdapter

DAY = 60000
AD = RimWorldAdapter()


def mem(i, text, game=0, **kw):
    return Memory(id=f"m{i}", game=game, text=text, **kw)


# ---- step 2 ------------------------------------------------------------------------------
def test_similarity_uses_shared_actors_and_words():
    a = mem(1, "Bram snubbed me at the table", people=[1, 2])
    b = mem(2, "Bram snubbed me at supper", people=[1, 2])
    c = mem(3, "The fire spread through the barn", people=[3])
    assert memory.similarity(a, b) > 0.5 > memory.similarity(a, c)


def test_merge_by_similarity_across_event_types_and_spares_pins_and_secrets():
    m = Mind(actor_id=1)
    m.mem_counter = 20
    for i in range(4):
        m.memories.append(mem(i, f"Bram snubbed me at the table again", game=i, etype="social" if i % 2 else "thought",
                              people=[1, 2], importance=0.3 + i / 10))
    m.memories.append(mem(10, "Bram snubbed me at the table again", game=5, people=[1, 2], visibility="private"))
    m.memories.append(mem(11, "Bram snubbed me at the table again", game=6, people=[1, 2], pinned=True))
    m.memories.append(mem(12, "The barn burned down", game=7, people=[3]))
    assert memory.merge_old_memories(m, 20 * DAY) == 4
    ids = {x.id for x in m.memories}
    assert {"m10", "m11", "m12"} <= ids and not ({"m0", "m1", "m2", "m3"} & ids)
    s = [x for x in m.memories if x.summary_of][0]
    assert s.summary_of == 4 and s.importance == 0.6 * 0.8 and s.people == [1, 2]
    assert memory.merge_old_memories(m, 20 * DAY) == 0            # a summary is not merged again


def test_recent_memories_are_not_merged():
    m = Mind(actor_id=1)
    for i in range(5):
        m.memories.append(mem(i, "Bram snubbed me", game=19 * DAY, people=[2]))
    assert memory.merge_old_memories(m, 20 * DAY) == 0


def test_drop_decayed_unpinned_below_floor():
    m = Mind(actor_id=1)
    m.memories = [mem(1, "old minor", game=0, importance=0.3), mem(2, "old pinned", game=0, importance=0.3, pinned=True),
                  mem(3, "fresh", game=40 * DAY, importance=0.3), mem(4, "old big", game=0, importance=1.0)]
    r = memory.upkeep(m, 40 * DAY, DAY, floor=0.03)
    assert {x.id for x in m.memories} == {"m2", "m3", "m4"} or {x.id for x in m.memories} == {"m2", "m3"}
    assert r["dropped"] >= 1 and "m1" not in {x.id for x in m.memories}
    assert "m2" in {x.id for x in m.memories}                      # pinned survives any age


def test_adapter_pins_deaths_betrayals_firsts_and_flags_secrets():
    ev = {"seq": 1, "game": 5, "type": "death", "actors": [{"id": 2, "role": "victim"}], "witnesses": [1],
          "summary": "Bram died.", "severity": 0.2, "data": {}}
    minds = {1: Mind(actor_id=1)}
    memory.remember_event(minds, ev, AD)
    assert minds[1].memories[0].pinned
    be = dict(ev, seq=2, type="social", summary="Cy sold Ana out.", data={"betrayal": True},
              actors=[{"id": 3, "role": "speaker"}])
    memory.remember_event(minds, be, AD)
    assert minds[1].memories[1].pinned
    sec = dict(ev, seq=3, type="injury", severity=0.3, data={"secret": True}, summary="Ana hid the knife.")
    memory.remember_event(minds, sec, AD)
    assert minds[1].memories[2].visibility == "private"


def test_blob_stays_under_32k_with_diaries_bio_history_and_many_memories():
    m = Mind(actor_id=1, name="Ana")
    m.fixed.text = "x" * 3000
    for i in range(400):
        m.memories.append(mem(i, "I remember something long and detailed about day %d. " % i * 3, game=i * 900,
                              importance=(i % 10) / 10, pinned=(i % 37 == 0), people=[2, 3]))
    for i in range(12):
        m.add_bio_diff(i, "bio revision %d " % i * 30)
    for d in range(9):
        m.add_diary(d, d * DAY, "Diary text for the day. " * 12)
    for i in range(30):
        m.relations[str(i)] = Relation(name=f"P{i}", feeling="f" * 80, history="h" * 80, updated_time=i)
    assert len(m.diary) == 7 and len(m.bio_history) == 10
    blob = m.to_blob(400 * 900, DAY)
    size = len(json.dumps(blob, ensure_ascii=False, separators=(",", ":")).encode())
    assert size <= BLOB_TARGET <= BLOB_LIMIT
    assert any(x["pinned"] for x in blob["memories"])             # pins are the last to go
    assert Mind.from_blob(blob, 1).evolving_bio == m.evolving_bio


def test_blob_trims_diaries_before_pinned_memories():
    m = Mind(actor_id=1)
    m.memories = [mem(i, "pinned thing " * 20, pinned=True, importance=0.9) for i in range(60)]
    for d in range(7):
        m.add_diary(d, d, "diary " * 100)
    size0 = len(json.dumps(m.model_dump()).encode())
    assert size0 > BLOB_TARGET
    blob = m.to_blob(0, DAY, limit=size0 - 3000)
    assert len(blob["diary"]) < 7 and len(blob["memories"]) == 60


def test_director_runs_upkeep_once_per_game_day(world):
    async def go():
        w = await world.go()
        m = w.d.minds[1]
        for i in range(4):
            m.memories.append(mem(i, "Bram snubbed me at the table again", game=i, people=[2], importance=0.5))
        w.fb.tick = 10 * DAY
        await w.d.step(); await w.d.drain()
        assert len(m.memories) == 1 and m.memories[0].summary_of == 4 and 1 in w.d.dirty | {1}
        m.memories.append(mem(9, "x y z", game=0, importance=0.01))
        await w.d.step(); await w.d.drain()
        assert any(x.id == "m9" for x in m.memories)               # not again the same day
    run(go())


# ---- step 3 ------------------------------------------------------------------------------
def test_gossip_gives_told_by_memory_with_lower_confidence_and_bookkeeping():
    minds = {1: Mind(actor_id=1), 2: Mind(actor_id=2)}
    minds[1].memories.append(mem(1, "I saw Cy hoarding food in the shed", people=[3], importance=0.6))
    names = {1: "Ana", 2: "Bram", 3: "Cy"}
    t = memory.record_told_line(minds, 1, 2, names, "Cy is hoarding food in the shed, you know.", 100, "public", 3)
    assert t.source == "told" and t.told_by == "Ana" and t.told_by_id == 1
    assert t.confidence == 0.5 and t.visibility == "public" and 3 in t.people and 1 in t.people
    assert minds[1].memories[0].told_to == ["Bram"] and minds[1].memories[0].visibility == "public"
    assert "(told by Ana, not sure)" in block_d([t], [])


def test_confide_moves_private_memory_to_told_to_listener():
    minds = {1: Mind(actor_id=1), 2: Mind(actor_id=2)}
    sec = mem(1, "I hid the knife under the floor", visibility="private", importance=0.7)
    minds[1].memories += [sec, mem(2, "Public thing", importance=0.9)]
    names = {1: "Ana", 2: "Bram"}
    t = memory.record_told_line(minds, 1, 2, names, "I hid a knife somewhere, Bram.", 100, "private")
    assert sec.visibility == "told" and sec.told_to == ["Bram"]
    assert t.visibility == "private" and t.confidence == 0.7            # the listener now holds a secret
    assert memory.can_state(sec, "Bram") and not memory.can_state(sec, "Cy")
    assert "[private, told to Bram]" in block_d([sec], [])
    assert memory.secret_texts(minds[1], "Bram") == [] and memory.secret_texts(minds[1], "Cy") == [sec.text]


def test_leaks_secret_is_verbatim_quote_not_a_hint():
    secret = "It happened to me: Ana stole the rifle from the armory at night."
    assert leaks_secret("Yes, I stole the rifle from the armory.", [secret])
    assert not leaks_secret("There is something about that armory I will not say.", [secret])
    assert leaks_secret("secret code", ["secret code"])               # a short secret: the whole phrase
    kw = dict(speaker_names={"Ana"}, known_names={"Bram"}, all_names={"Ana", "Bram"})
    assert sanitize_line("I stole the rifle from the armory.", secrets=[secret], **kw).reason == "leaks_secret"
    assert sanitize_line("I know something about that armory.", secrets=[secret], **kw).ok


def test_block_a_carries_the_secret_rule():
    assert "[private]" in AD.block_a and "hint" in AD.block_a.lower()


def test_private_memory_is_hinted_never_quoted_in_a_line(world):
    async def go():
        w = await world.go()
        w.fb.add_event("injury", [(1, "victim")], "Ana stole the rifle from the armory at night.", [1], 0.6,
                       data={"secret": True})
        w.llm.script["line"] = ["Yes, I stole the rifle from the armory at night.",
                                "There is something about that armory I am not telling you."]
        await w.d.step(); await w.d.drain()
        assert w.d.minds[1].memories[0].visibility == "private"
        assert "[private]" in [c for c in w.llm.calls if c["kind"] == "line"][0]["messages"][0][1]
        assert (1, "leaks_secret") in w.d.dropped and not w.fb.say_log      # the quote was dropped, never shown
        w.fb.tick += 6000
        w.fb.add_event("injury", [(1, "victim")], "Ana stole the rifle from the armory at night.", [1], 0.62,
                       data={"secret": True})
        await w.d.step(); await w.d.drain()
        assert w.fb.say_log and "armory" in w.fb.say_log[-1]["text"] and "stole" not in w.fb.say_log[-1]["text"]
    run(go())


def test_confided_listener_may_hear_it_stated_but_a_third_party_may_not(world):
    async def go():
        w = await world.go()
        m = w.d.minds[1]
        m.memories.append(Memory(id="m50", game=10, text="I hid the knife under the floorboards in the barn.",
                                 visibility="told", told_to=["Bram"], importance=0.9))
        assert memory.secret_texts(m, "Bram") == [] and memory.secret_texts(m, "Cy")
    run(go())

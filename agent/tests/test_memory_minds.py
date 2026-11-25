import json

from conftest import run
from hermesvoice_agent import memory
from hermesvoice_agent.builders import build_fixed_bio, build_voice_card, facts_hash, talk_weight
from hermesvoice_agent.fakes import default_pawn
from hermesvoice_agent.minds import BLOB_LIMIT, Memory, Mind, TICKS_PER_DAY


def ev(seq=1, etype="injury", witnesses=(1, 2), pawns=((1, "victim"), (3, "attacker")), sev=0.5, tick=1000, **kw):
    return {"seq": seq, "tick": tick, "type": etype, "pawns": [{"id": i, "role": r} for i, r in pawns],
            "summary": "Ana was shot.", "severity": sev, "witnesses": list(witnesses), "data": kw.get("data", {})}


def test_witness_only_memory():
    minds = {i: Mind(pawn_id=i) for i in (1, 2, 3, 4)}
    w = memory.remember_event(minds, ev(witnesses=(1, 2)), {})
    assert {pid for pid, _ in w} == {1, 2}
    assert minds[1].memories and minds[2].memories
    assert not minds[3].memories and not minds[4].memories      # involved (3) but not a witness: nothing
    assert minds[1].memories[0].text.startswith("It happened to me")
    assert minds[2].memories[0].text.startswith("I saw it")
    assert minds[1].memories[0].source == "saw"
    memory.remember_event(minds, ev(witnesses=(1, 2)), {})       # idempotent per seq
    assert len(minds[1].memories) == 1


def test_importance_from_game_numbers_and_pinning():
    m = Mind(pawn_id=1)
    assert memory.importance_of(ev(etype="death", sev=0.2), 2) == 1.0
    assert memory.importance_of(ev(sev=0.2, data={"mood_offset": -10}), 2) >= 0.5
    assert memory.importance_of(ev(sev=0.3), 1) > memory.importance_of(ev(sev=0.3), 2)   # happened to me
    assert memory.is_pinned(ev(etype="death"), 1, m)
    assert memory.is_pinned(ev(data={"betrayal": True}), 1, m)
    assert not memory.is_pinned(ev(etype="injury", sev=0.3), 1, m)
    assert 0.0 <= memory.importance_of(ev(sev=5.0), 1) <= 1.0


def test_decay_half_life_four_days():
    m = Mind(pawn_id=1)
    mem = Memory(id="a", tick=0, text="x", importance=0.8)
    w0 = m.memory_weight(mem, 0)
    w4 = m.memory_weight(mem, 4 * TICKS_PER_DAY)
    assert abs(w4 / w0 - 0.5) < 1e-9
    mem.pinned = True
    assert m.memory_weight(mem, 40 * TICKS_PER_DAY) == m.memory_weight(mem, 0)


def test_selection_top_2_to_4_and_relevance():
    m = Mind(pawn_id=1)
    now = 10 * TICKS_PER_DAY
    for i in range(10):
        m.memories.append(Memory(id=f"m{i}", tick=now - i * 1000, text=f"I saw a thing number {i}", importance=0.4))
    m.memories.append(Memory(id="rel", tick=now - 3 * TICKS_PER_DAY, text="Bram stole my rifle from the armory",
                             importance=0.4, people=[2]))
    sel = memory.select_memories(m, now, {2}, "Bram is near the armory")
    assert 2 <= len(sel) <= 4
    assert "rel" in [x.id for x in sel]                       # relevance wins over a little recency
    assert [x.tick for x in sel] == sorted(x.tick for x in sel)   # stable order
    assert len(memory.select_memories(Mind(pawn_id=1), now, set(), "")) == 0
    one = Mind(pawn_id=1, memories=[Memory(id="a", tick=0, text="x")])
    assert len(memory.select_memories(one, now, set(), "")) == 1


def test_told_memory_lower_confidence():
    m = Mind(pawn_id=1)
    t = memory.remember_told(m, 2, "Bram", "Bram told me: Cy hoards food", 10)
    assert t.source == "told" and t.confidence < 1 and t.told_by == "Bram"


def test_merge_old_memories():
    m = Mind(pawn_id=1)
    for i in range(4):
        m.memories.append(Memory(id=f"m{i}", tick=i, text=f"injury {i}", etype="injury", importance=0.3))
    assert memory.merge_old_memories(m, 20 * TICKS_PER_DAY) == 4
    assert len(m.memories) == 1 and m.memories[0].summary_of == 4


def test_blob_size_cap_trims_decayed_first():
    m = Mind(pawn_id=1, name="Ana")
    now = 30 * TICKS_PER_DAY
    for i in range(600):
        m.memories.append(Memory(id=f"m{i}", tick=i * 100, text="I saw something happen. " * 8, importance=0.3))
    m.memories.append(Memory(id="pin", tick=5, text="My brother died.", importance=1.0, pinned=True))
    recent = Memory(id="recent", tick=now - 10, text="Fresh and important.", importance=0.9)
    m.memories.append(recent)
    blob = m.to_blob(now)
    size = len(json.dumps(blob, ensure_ascii=False, separators=(",", ":")).encode())
    assert size <= BLOB_LIMIT
    ids = {x["id"] for x in blob["memories"]}
    assert "pin" in ids and "recent" in ids          # pinned and fresh survive
    assert len(ids) < 600                             # decayed ones went
    assert Mind.from_blob(blob, 1).memories


def test_blob_roundtrip_and_bad_blob():
    m = Mind(pawn_id=1, name="Ana", wants=["a"], notes="n")
    m2 = Mind.from_blob(m.to_blob(), 1)
    assert m2.wants == ["a"] and m2.notes == "n"
    assert Mind.from_blob({"memories": "garbage"}, 5, "Z").pawn_id == 5
    assert Mind.from_blob(None, 5).memories == []


def test_bio_diff_history_and_undo():
    m = Mind(pawn_id=1)
    m.add_bio_diff(1, "first"); m.add_bio_diff(2, "second")
    assert m.evolving_bio == "second" and len(m.bio_history) == 2
    assert m.undo_bio() and m.evolving_bio == "first"


def test_builders_deterministic_with_dlc():
    p = default_pawn(1, "Ana", title={"label": "Baron"}, ideo_role="Moral guide",
                     precepts=["Cannibalism: required"], anomaly={"void": "touched"},
                     xenotype="Hussar", genes=[{"label": "Robust"}],
                     health={"conditions": [], "missing_parts": ["left arm"]},
                     traits=[{"def": "Kind", "label": "kind"}, {"def": "Tough", "label": "tough"}])
    bio = build_fixed_bio(p)
    assert "Baron" in bio.text and "Moral guide" in bio.text and "Cannibalism" in bio.text
    assert "Hussar" in bio.text and "Robust" in bio.text and "left arm" in bio.text and "Anomaly" in bio.text
    assert bio.dlc["title"] == "Baron"
    assert build_fixed_bio(p).text == bio.text
    vc = build_voice_card(p)
    assert len(vc.examples) == 3 and 1 <= len(vc.tics) <= 3 and vc.register_line
    plain = default_pawn(2, "Bo")
    assert "Baron" not in build_fixed_bio(plain).text and "Anomaly" not in build_fixed_bio(plain).text
    # facts hash: a lost limb changes it, ageing does not
    q = json.loads(json.dumps(p)); q["age"] = 99
    assert facts_hash(q) == facts_hash(p)
    q["health"]["missing_parts"].append("right leg")
    assert facts_hash(q) != facts_hash(p)
    assert 0.3 <= talk_weight(p) <= 1.8


def test_llm_voice_pass_written_once(world):
    from hermesvoice_agent.builders import ensure_mind_built
    async def go():
        w = world
        w.llm.script["voice_card"] = ["Line one is here.\nLine two is here.\nLine three is here."]
        m = Mind(pawn_id=1)
        p = default_pawn(1, "Ana")
        assert await ensure_mind_built(m, p, {}, w.llm, use_llm=True)
        assert m.voice.llm_examples and m.voice.examples[0] == "Line one is here."
        assert not await ensure_mind_built(m, p, {}, w.llm, use_llm=True)     # once, saved
        assert len([c for c in w.llm.calls if c["kind"] == "voice_card"]) == 1
    run(go())

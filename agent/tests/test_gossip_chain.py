"""Aliveness item 3: gossip source chains, per-hop decay, duplicate merge, pass-on hints and reputations."""
from conftest import run
from rimworldllm_agent.core import gossip, memory
from rimworldllm_agent.core.minds import CRel, Memory, Mind
from rimworldllm_agent.core.prompt import block_c, block_d

NAMES = {1: "Ana", 2: "Bram", 3: "Cy", 4: "Dee"}


def minds(*ids):
    return {i: Mind(actor_id=i, name=NAMES[i]) for i in ids}


def saw(mind, text, seq=7, people=(3,), imp=0.8, vis="public"):
    m = Memory(id=mind.next_mem_id(), game=10, text=text, source="saw", importance=imp, people=list(people),
               event_seq=seq, visibility=vis)
    mind.memories.append(m)
    return m


def tell(ms, a, b, line, about=3, **kw):
    return memory.record_told_line(ms, a, b, NAMES, line, 20, "public", about, chain=True, **kw)


def test_chain_hops_and_decay_over_two_retellings():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    m2 = tell(ms, 1, 2, "Cy hoards food.")
    assert (m2.chain, m2.hops, m2.event_seq) == (["Ana"], 1, 7)
    assert m2.confidence == 0.5                                   # first hop: today's value
    m4 = tell(ms, 2, 4, "They say Cy hoards the food.")
    assert (m4.chain, m4.hops, m4.event_seq) == (["Ana", "Bram"], 2, 7)
    assert m4.confidence == round(0.5 * 0.5 * 0.8, 3)             # base x source confidence x decay^1
    assert m4.text == "Bram told me: They say Cy hoards the food."   # the retelling's own words


def test_hop_decay_is_configurable():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    tell(ms, 1, 2, "Cy hoards food.")
    m4 = tell(ms, 2, 4, "Cy hoards food, they say.", hop_decay=0.5)
    assert m4.confidence == round(0.5 * 0.5 * 0.5, 3)


def test_duplicates_merge_and_keep_the_higher_confidence():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    first = tell(ms, 1, 4, "Cy hoards food.")                     # Dee hears it first hand-ish (0.5)
    tell(ms, 1, 2, "Cy hoards food.")
    again = tell(ms, 2, 4, "Cy hoards food indeed.")              # weaker, second-hand
    assert again is first and len([m for m in ms[4].memories if m.source == "told"]) == 1
    assert first.sources == 2 and first.confidence == 0.5 and first.chain == ["Ana"]
    assert "heard from several people" in block_d([first], [])


def test_stronger_version_replaces_the_weaker_one():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    tell(ms, 1, 2, "Cy hoards food.")
    weak = tell(ms, 2, 4, "Cy hoards food, so I heard.")
    assert weak.hops == 2
    direct = tell(ms, 1, 4, "Cy hoards the food, I saw it.")
    assert direct is weak and weak.hops == 1 and weak.chain == ["Ana"] and weak.confidence == 0.5 and weak.sources == 2


def test_a_story_never_comes_back_to_its_teller():
    ms = minds(1, 2)
    saw(ms[1], "I saw Cy hoard the food.")
    tell(ms, 1, 2, "Cy hoards food.")
    assert tell(ms, 2, 1, "Cy hoards food, Ana said.") is None


def test_chain_off_is_todays_behaviour():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    a = memory.record_told_line(ms, 1, 2, NAMES, "Cy hoards food.", 20, "public", 3)
    b = memory.record_told_line(ms, 2, 4, NAMES, "Cy hoards food.", 20, "public", 3)
    assert a.chain == [] and a.hops == 0 and a.event_seq is None
    assert b.confidence == round(0.5 * 1.0, 3)                    # told memories are not retold without the chain


def test_secrets_are_never_retold_or_hinted():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy weep in secret.", vis="private")
    m = tell(ms, 1, 2, "Cy weeps in secret.")
    assert m is None or m.chain == ["Ana"] and ms[1].memories[0].visibility == "private"
    assert gossip.pass_on_candidates(ms[1], ms[4], 4, {1, 4}) == []
    # a confided (private) line stays out of chains
    c = memory.record_told_line(ms, 1, 2, NAMES, "I weep.", 20, "private", None, chain=True)
    assert c.chain == [] and c.visibility == "private"


def test_minor_romantic_line_is_not_recorded():
    ms = minds(1, 2)
    saw(ms[1], "I saw Cy hoard the food.")
    assert tell(ms, 1, 2, "Cy and Dee kissed in the barn.", minor_ids={3}) is None
    assert tell(ms, 1, 2, "Cy hoards food.", minor_ids={3}) is not None


def test_pass_on_only_unknown_public_items_about_absent_actors():
    ms = minds(1, 2, 3)
    a = saw(ms[1], "I saw Cy hoard the food.", seq=7, people=(3,))
    saw(ms[1], "I argued with Bram.", seq=8, people=(2,))                    # the listener is in it
    saw(ms[1], "I saw Cy weep.", seq=9, people=(3,), vis="private")
    saw(ms[1], "I saw Dee steal a tool.", seq=10, people=(4,), imp=0.9)
    cands = gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2})
    assert [m.event_seq for m in cands] == [10, 7]
    assert gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2, 4})[0].event_seq == 7   # Dee is present: not absent
    saw(ms[2], "I too saw Dee steal.", seq=10, people=(4,))                 # the listener heard it already
    assert [m.event_seq for m in gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2})] == [7]
    a.told_to.append("Bram")
    assert gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2}) == []
    hint = gossip.pass_on_hint(ms[1], ms[3], 3, {1, 3})
    assert hint.startswith("Something you could pass on: ") and len(hint) <= 125


def test_pass_on_skips_romantic_text_around_minors():
    ms = minds(1, 2)
    saw(ms[1], "I saw Cy kissing Dee.", seq=7, people=(3, 4))
    assert gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2}, minors={3}) == []
    assert gossip.pass_on_candidates(ms[1], ms[2], 2, {1, 2}, minors=set())


def test_reputation_lines_from_heard_memories_and_in_block_c():
    ms = minds(1, 2, 4)
    saw(ms[1], "I saw Cy hoard the food.")
    tell(ms, 1, 2, "Cy hoards food.")
    tell(ms, 1, 4, "Cy hoards food.")
    reps = gossip.reputations(ms)
    assert set(reps) == {3} and reps[3].startswith("Cy hoards food") and "(2 people)" in reps[3]
    assert 1 not in reps                                           # the teller is not the subject
    m = Mind(actor_id=4, name="Dee")
    from rimworldllm_agent.core.minds import Relation
    for i in (1, 2, 3):
        m.relations[str(i)] = Relation(name=NAMES[i], opinion=10 - i)
    m.freeze_relations(reputations={"3": reps[3], "1": "Ana is kind", "2": "Bram is loud"}, rep_limit=2)
    c = block_c(m)
    assert c.count("people say:") == 2
    m.freeze_relations()
    assert "people say" not in block_c(m)                           # nothing frozen: block C as before


def test_reputation_leaves_out_romance_about_minors():
    ms = minds(1, 2)
    saw(ms[1], "I saw Cy kissing Dee.", seq=7, people=(3, 4))
    tell(ms, 1, 2, "Cy was kissing Dee.", about=3)
    assert gossip.reputations(ms, minors={3}) == {}
    assert gossip.reputations(ms)


def test_director_wires_chain_hint_and_switches(world):
    async def go():
        w = await world.go()
        d = w.d
        saw(d.minds[1], "I saw Cy hoard the food.", seq=7, people=(3,))
        sc = type("S", (), {"where": "the hall", "present": ["Bram (idle)"]})()
        hint = d._common_talk(1, "supper", sc, {1, 2}, 2)
        assert any(x.startswith("Something you could pass on") for x in hint)
        assert not any(x.startswith("Something") for x in d._common_talk(1, "supper", sc, {1, 2}, None))   # not outside a conversation
        d.cfg.gossip_chain = False
        assert d._common_talk(1, "supper", sc, {1, 2}, 2) == []
        d.cfg.gossip_chain = True
        d.cfg.reputation = False
        d.refresh_reputations()
        assert d.reputations == {}
    run(go())

from conftest import run
from rimworldllm_agent.core.minds import Memory, Mind
from rimworldllm_agent.games.rimworld.builders import build_fixed_bio, build_voice_card
from rimworldllm_agent.games.rimworld.fakes import default_pawn
from rimworldllm_agent.core.prompt import Scene, build_prefix, line_messages, intent_messages, grammar_for
from rimworldllm_agent.games.rimworld import RimWorldAdapter

AD = RimWorldAdapter()
BLOCK_A, INTENTS = AD.block_a, AD.intent_labels
GRAMMAR_INTENT = grammar_for(INTENTS)


def mk_mind(pid, first):
    p = default_pawn(pid, first)
    m = Mind(actor_id=pid, name=first)
    m.fixed = build_fixed_bio(p)
    m.voice = build_voice_card(p)
    return m


def test_block_a_same_for_every_pawn_and_has_glosses():
    a, b = mk_mind(1, "Ana"), mk_mind(2, "Bram")
    sc = Scene(trigger="x")
    pa, pb = build_prefix(BLOCK_A, a, [], [], sc), build_prefix(BLOCK_A, b, [], [], sc)
    assert pa.startswith(BLOCK_A) and pb.startswith(BLOCK_A)
    for label in INTENTS:
        assert f"- {label}:" in BLOCK_A       # label glosses live in block A
    assert 'root ::= "none"' in GRAMMAR_INTENT


def test_prefix_bytes_identical_across_turns_and_passes():
    m = mk_mind(1, "Ana")
    prefix = build_prefix(BLOCK_A, m, [Memory(id="m1", game=5, text="I saw it: x")], ["Raid: 4 raiders"], Scene(trigger="t"))
    seen = set()
    for turn in range(5):
        msgs = line_messages(prefix, [("Bram", "hello")] * turn, m, "Ana", turn)
        seen.add(msgs[0][1].encode())
        imsgs = intent_messages(msgs, "line", "Bram")
        assert imsgs[:2] == msgs                # intent pass shares line pass bytes exactly
        seen.add(imsgs[0][1].encode())
    assert len(seen) == 1
    # block G (voice reminder) is per turn, A-E is not
    assert line_messages(prefix, [], m, "Ana", 0)[1] != line_messages(prefix, [], m, "Ana", 1)[1]


def test_lease_prefix_stable_through_director(world):
    async def go():
        w = await world.go()
        w.fb.add_event("social", [(1, "speaker"), (2, "listener")], "Ana and Bram argued.", [1, 2], 0.6)
        w.d.cfg.quotas.min_gap_game_ambient = 0
        w.d.rng.seed(0)
        await w.d.step()
        await w.d.drain()
        return w
    w = run(go())
    by_actor = {}
    for c in w.llm.calls:
        if c["kind"] in ("line", "intent") and c["actor"] is not None:
            by_actor.setdefault(c["actor"], set()).add(c["messages"][0][1])
    assert by_actor, "no calls"
    for pid, prefixes in by_actor.items():
        assert len(prefixes) == 1, f"prefix drifted for actor {pid}"


def test_tic_rule_in_block_g():
    m = mk_mind(1, "Ana")
    from rimworldllm_agent.core.prompt import block_g
    assert "Habits (this line)" in block_g(m, "Ana", 0)
    assert "No habits this line." in block_g(m, "Ana", 1)
    assert "No habits this line." in block_g(m, "Ana", 2)
    assert "at most one line in three" in build_prefix(BLOCK_A, m, [], [], Scene())

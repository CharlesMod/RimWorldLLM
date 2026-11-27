"""The core/adapter split: core is game-agnostic; the adapter fills the Protocol; the contract's say rules."""
import re
from pathlib import Path

from conftest import run
from rimworldllm_agent.core.adapter import GameAdapter
from rimworldllm_agent.core.bridge import Bridge, BridgeClient, EventPoller, EventSource
from rimworldllm_agent.games.rimworld import RimWorldAdapter

CORE = Path(__file__).parent.parent / "rimworldllm_agent" / "core"
# words that belong to one game; core says actor, game time, scene, character
TERMS = re.compile(r"\b(rimworld|colony|colonist|colonists|pawn|pawns|tick|ticks|raid|raids|raider|raiders|"
                   r"injury|injuries|prisoner|prisoners|hediff|ideology|biotech|royalty|anomaly|xenotype|"
                   r"gene|genes|dlc)\b", re.I)


def test_core_imports_nothing_from_games():
    for f in CORE.glob("*.py"):
        src = f.read_text()
        assert not re.search(r"^\s*(from|import)\s+[\w.]*games", src, re.M), f"{f.name} imports from games/"
        assert not re.search(r"from\s+\.\.\s*games|from\s+\.\.\.?games", src), f.name


def test_core_has_no_game_terms():
    bad = []
    for f in CORE.glob("*.py"):
        for n, line in enumerate(f.read_text().splitlines(), 1):
            if TERMS.search(line):
                bad.append(f"{f.name}:{n}: {line.strip()}")
    assert not bad, "game terms in core:\n" + "\n".join(bad)


def test_adapter_and_transports_satisfy_the_protocols():
    ad = RimWorldAdapter()
    assert isinstance(ad, GameAdapter)
    for m in ("health", "config", "roster", "actor", "scene", "talk", "get_mind", "put_mind", "say", "thought"):
        assert hasattr(BridgeClient, m) and m in Bridge.__dict__
    assert "poll" in EventPoller.__dict__ and "poll" in EventSource.__dict__


def test_core_event_normalised_from_wire():
    e = RimWorldAdapter().normalize_event({"seq": 1, "tick": 50, "type": "injury", "pawns": [{"id": 3, "role": "victim"}]})
    assert e["game"] == 50 and e["actors"] == [{"id": 3, "role": "victim"}] and "tick" not in e


def test_say_refusal_reasons_keep_bubble_and_record_nothing(world):
    async def go():
        w = await world.go()
        b = w.bridge
        r = await b.say(1, "Cy is hoarding.", listener=2, intent="gossip")                 # no about
        assert r["refused"]["effect"] == "no_about"
        r = await b.say(1, "Cy is hoarding.", listener=2, intent="gossip", about=3)
        assert r["applied"]["effect"] == "gossip" and w.fb.say_log[-1]["about"] == 3
        w.fb.pawns[2]["mood"]["thoughts"] = []
        r = await b.say(1, "There there.", listener=2, intent="comfort")
        assert r["refused"]["effect"] == "not_negative"                                    # Bram has no bad thought
        w.fb.config["effects"] = "off"
        r = await b.say(1, "Well done.", listener=2, intent="kind")
        assert r["refused"]["effect"] == "effects_off"
    run(go())


def test_gossip_names_a_third_actor_and_downgrades_without_one(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Cy is hoarding food, you know."]
        w.llm.script["intent"] = ["gossip"]
        w.fb.add_event("social", [(1, "speaker"), (2, "listener"), (3, "other")], "Ana, Bram and Cy talked.",
                       [1, 2, 3], 0.7)
        w.d.cfg.quotas.pair_per_day = 9
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    g = [s for s in w.fb.say_log if s["intent"] == "gossip"]
    assert g and g[0]["about"] == 3 and g[0]["about"] not in (g[0]["pawn"], g[0]["listener"])
    told = [m for m in w.d.minds[g[0]["listener"]].memories if m.source == "told"]
    assert told and 3 in told[0].people


def test_every_write_carries_an_op_id(world):
    async def go():
        w = await world.go()
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.9)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log and all(b["op_id"] for b in w.fb.say_log)

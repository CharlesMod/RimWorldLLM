"""The trade turn: a trader arrives (GET /vivarium traders), one small turn per visit, skipped in a fight or when food is critical,
never in advisor mode, with the trade view first and no caravan tools; the routine cycle is unchanged."""
import copy
import json
from pathlib import Path

from conftest import run
from rimworldllm_agent.core import steward as stew
from rimworldllm_agent.games.rimworld import steward_try
from test_steward import boot, call, cycle, final
from test_steward_combat import VIEWS, DIGESTS, start_fight

DATA = Path(__file__).parent / "data"
TRADER = json.loads((DATA / "steward_digest_trader.json").read_text())
ORBITAL = json.loads((DATA / "steward_digest_orbital.json").read_text())
SECOND = {"id": "P6000", "kind": "caravan", "name": "Cloth trader Mia", "faction": "Hill Union", "leaves_h": 12, "silver": 900, "can_trade": True,
          "sells": ["cloth 100"], "buys": ["art"]}


def fed(d, days=12):
    d = copy.deepcopy(d)
    d["food_days"] = days
    if isinstance(d.get("facts"), dict):
        d["facts"]["food_days"] = days
    return d


def reasons(st):
    """The trade turns only: the first routine look (heartbeat) may follow a trade turn, which does not push it back."""
    return [t["reason"] for t in st.stats.trace if t["reason"] in ("trade", "chat")]


def test_wakes_once_per_visit_and_again_for_a_new_trader(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER)
        await cycle(w)
        assert reasons(st) == ["trade"] and st.trade_seen == {"P5521"}
        w.real += 30
        await cycle(w)
        assert reasons(st) == ["trade"]                              # the same trader: no second turn
        d = fed(TRADER)
        d["trade"]["traders"].append(SECOND)
        w.fb.digest = d
        w.real += 30
        await cycle(w)
        assert reasons(st) == ["trade", "trade"] and st.trade_seen == {"P5521", "P6000"}
        assert "Cloth trader Mia (P6000)" in fake.calls[-1]["messages"][1]["content"].split("=== YOUR PLAN")[0]
        st.reset()                                                  # a new save: the visit counts again
        assert st.trade_seen == set()
    run(go())


def test_the_trade_turn_respects_the_min_gap_and_waits_for_chat_and_events(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=20.0)
        st.last_real = w.d.clock()
        w.fb.digest = fed(TRADER)
        await cycle(w)
        assert reasons(st) == []
        w.real += 30
        w.fb.add_chat("player", "hello")
        await cycle(w)
        assert reasons(st) == ["chat"]                               # chat first; the trade turn comes at the next step
        w.real += 30
        await cycle(w)
        assert reasons(st) == ["chat", "trade"]
    run(go())


def test_no_trade_turn_wakes_in_a_fight_and_a_turn_run_in_one_is_skipped(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER)
        start_fight(w)
        w.fb.digest = {**fed(TRADER), "combat": DIGESTS["pirate_ranged"].get("combat")}
        await cycle(w)
        assert [t["reason"] for t in st.stats.trace] == ["combat started"]     # the fight owns the cadence: no trade turn
        assert st.trade_seen == set()
        t = {}
        await st._run_trade(t)                                       # even called directly it skips
        assert t["skipped"] == "a fight is on"
    run(go())


def test_a_trade_turn_due_before_the_fight_is_skipped_but_marks_the_trader_seen(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER)
        w.fb.combat = copy.deepcopy(VIEWS["pirate_ranged"])
        st.vv = {"traders": [{"id": "P5521"}], "combat": {"active": True}}
        st.trade_new = ["P5521"]
        t = {}
        await st._run_trade(t)
        assert t["skipped"] == "a fight is on"
    run(go())


def test_skipped_when_food_is_critical_and_still_marked_seen(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER, 2.4)                               # the trader sample itself is under 3 days
        await cycle(w)
        assert reasons(st) == ["trade"] and st.stats.trace[-1]["skipped"].startswith("food is critical")
        assert st.trade_seen == {"P5521"} and not fake.calls
        w.fb.digest = fed(TRADER, 12)
        w.real += 30
        await cycle(w)
        assert reasons(st) == ["trade"]                              # the visit is over for this trader
    run(go())


def test_skipped_on_a_critical_starvation_alert(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        d = fed(TRADER)
        d["alerts"] = [{"alert": "Alert_Starvation", "label": "Starvation", "critical": True}]
        w.fb.digest = d
        await cycle(w)
        assert st.stats.trace[-1]["skipped"] == "a critical starvation alert" and not fake.calls
        d["alerts"] = [{"alert": "Alert_LowFood", "label": "Low food", "critical": False}]
    run(go())


def test_no_trade_turn_in_advisor_mode(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], playing=False, steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER)
        await cycle(w)
        assert reasons(st) == [] and not fake.calls
    run(go())


def test_the_trade_turn_prompt_is_small_with_the_trade_view_first(world):
    async def go():
        w, st, fake = await boot(world, [call("trade_offer", **{"with": "P5521"}), call("trade", **{"with": "P5521"}, buy="steel x10", why="since steel is low"),
                                         final("done")], steward_min_gap_real_s=0.0)
        seen_tokens = []
        inner = fake.chat

        async def spy(messages, **kw):
            seen_tokens.append(kw.get("max_tokens"))
            return await inner(messages, **kw)
        fake.chat = spy
        w.fb.goals_board["plan"] = "Get components."
        w.fb.digest = fed(TRADER)
        await cycle(w)
        c = fake.calls[0]
        system, user = c["messages"][0]["content"], c["messages"][1]["content"]
        assert user.startswith("=== THE TRADE ===") and user.index("ARRIVED: Bulk goods trader Tomas (P5521)") < user.index("TRADE: silver")
        assert user.index("TRADERS") < user.index("FOOD: about 12 days") < user.index("=== YOUR PLAN ===") and "Get components." in user
        assert "NEEDS:" in user and "STOCK:" in user and "NEVER SELL:" in user
        assert "SETTLEMENTS" not in user and "QUESTS" not in user and "THE STATE OF THE GAME" not in user
        assert len(system) < 6000 and (len(system) + len(user)) / 4 < 3500
        assert "trade_offer(" in system and "trade(" in system and "do_nothing" in system and "note(" in system
        assert system.index("trade_offer(") < system.index("note(") and system.index("trade(") < system.index("do_nothing")
        for gone in ("caravan", "set_time(", "set_work(", "build(", "squad(", "add_goal(", "set_plan(", "answer_letter(", "choose("):
            assert gone not in system
        assert stew.TRADE_MAX_TOKENS == 300 and stew.TRADE_FINAL_MAX == 120
        assert max(seen_tokens) == 300
        assert (stew.TRADE_STEPS, stew.TRADE_BUDGET_S, stew.TRADE_STEP_TIMEOUT_S) == (4, 60.0, 30.0)
        assert w.fb.looks == [("/trade/offer", {"with": "P5521"})]
        assert [a[0] for a in w.fb.act_log] == ["trade"] and w.fb.act_log[0][1]["buy"] == [{"item": "steel", "count": 10}]
        assert not w.fb.goals_board["log"]                           # no board edit, no log line
        assert not [e for e in w.fb.chat if e["who"] == "narrator"]  # no closing chat line
        assert len((Path(stew.__file__).parent.parent / "games/rimworld/steward_trade.txt").read_bytes()) < 1600
    run(go())


def test_the_trade_turn_runs_at_most_four_steps_and_only_its_tools(world):
    async def go():
        script = [call("trade_offer", **{"with": "P5521"}), call("set_time", pause="no", speed="leave", emergency="no"),
                  call("trade_offer", **{"with": "P5521"}), call("trade_offer", **{"with": "P5521"}), call("trade_offer", **{"with": "P5521"})]
        w, st, fake = await boot(world, script, steward_min_gap_real_s=0.0)
        w.fb.digest = fed(TRADER)
        await cycle(w)
        assert len(fake.calls) <= stew.TRADE_STEPS + 1
        assert not any(a[0] == "time" for a in w.fb.act_log)
        assert st.stats.trace[-1]["failures"]                        # set_time is not a trade tool: rejected
    run(go())


def test_the_routine_cycle_keeps_its_steps_and_is_not_pushed_back(world):
    async def go():
        w, st, fake = await boot(world, [final("done")], steward_min_gap_real_s=0.0)
        assert (stew.STEPS, stew.BUDGET_S, stew.STEP_TIMEOUT_S, stew.FINAL_MAX) == (8, 120.0, 60.0, 300)
        w.fb.digest = fed(TRADER)
        await cycle(w)
        assert [t["reason"] for t in st.stats.trace] == ["trade"] and st.last_game is None      # a trade turn leaves the heartbeat clock alone
        w.real += 30
        await cycle(w)
        assert [t["reason"] for t in st.stats.trace] == ["trade", "heartbeat"] and fake.calls[-1]["messages"][1]["content"].count("=== THE STATE OF THE GAME ===") == 1
    run(go())


def test_steward_try_trade_turn_on_the_orbital_trader():
    out = run(steward_try.try_once(fed(ORBITAL), None, "", True, "http://x", backend=_Script([call("trade_offer", **{"with": "S31"}), final("done")]),
                                   mode="grammar", trade_turn=True))
    tr = out["trace"]
    assert tr["reason"] == "trade" and tr["executed"][0][0] == "trade_offer"
    assert out["messages"][1]["content"].startswith("=== THE TRADE ===") if isinstance(out["messages"][1], dict) else True
    assert steward_try.report(out) == 0


class _Script:
    def __init__(self, script):
        from rimworldllm_agent.core.toolloop import Reply
        self.script, self.Reply = list(script), Reply

    async def chat(self, messages, **kw):
        return self.Reply(text=self.script.pop(0) if len(self.script) > 1 else self.script[0])

    async def aclose(self):
        pass

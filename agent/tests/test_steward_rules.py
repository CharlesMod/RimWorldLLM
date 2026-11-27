"""Playbook rules (games/rimworld/steward_rules.py): safe evaluation, facts, selection, the user-turn section, the shipped files."""
import json
from pathlib import Path

import pytest

from conftest import run
from rimworldllm_agent.core import steward as stew
from rimworldllm_agent.core.config import Config
from rimworldllm_agent.games.rimworld import steward_rules as sr
from rimworldllm_agent.games.rimworld.steward_tools import StewardKit

SAMPLE = json.loads((Path(__file__).parent / "data" / "steward_digest_sample.json").read_text())
RULES = json.loads(sr.RULES_PATH.read_text())["rules"]


def ev(cond, **f):
    return sr.compile_condition(cond)(f)


def test_every_shipped_rule_compiles():
    assert len(RULES) > 20
    assert len(sr.load_rules()) == len(RULES)          # none dropped
    ids = {r[0] for r in sr.load_rules()}
    assert "food.emergency" in ids


@pytest.mark.parametrize("bad", [
    "__import__('os').system('x')", "food_days.real", "a[0] < 1", "(lambda: 1)()", "[x for x in events]", "__class__ == 1",
    "len(events) > 1", "x if y else z", "a ** 2 > 1", "a // 2 > 1", "a % 2 == 0", "~a", "a is None", "{1: 2}", "f'{a}'", "a := 1", "b'x' == a",
    "[a, b] == c", "1 +", "", "food_days < 3; 1",
])
def test_whitelist_rejects(bad):
    with pytest.raises(sr.RuleError):
        sr.compile_condition(bad)


def test_missing_field_skips_and_none_numeric_skips():
    assert ev("food_days < 3") is None
    assert ev("food_days < 3", food_days=None) is None
    assert ev("food_days < 3", food_days=2) is True
    assert ev("food_days >= 3 and food_days < 5", food_days=4) is True
    assert ev("research_current == null", research_current=None) is True
    assert ev("research_current == null") is None
    # three-valued: a False operand still decides an and; a True one an or
    assert ev("food_days < 3 and mood_avg < 40", mood_avg=50) is False
    assert ev("food_days < 3 or mood_avg < 40", mood_avg=30) is True
    assert ev("food_days < 3 or mood_avg < 40", mood_avg=50) is None
    assert ev("not food_days < 3") is None


def test_in_not_in_and_lists():
    assert ev("'cold_snap' in events", events=["cold_snap"]) is True
    assert ev("'cold_snap' not in events", events=[]) is True
    assert ev("season in ['summer','fall']", season="fall") is True
    assert ev("season in ['summer','fall']", season="winter") is False
    assert ev("'x' in events") is None
    assert ev("'x' in events", events=None) is None
    assert ev("raid_active == false and raid_active != true", raid_active=False) is True


def test_arithmetic_and_chains():
    assert ev("food_days < days_to_growing - days_to_winter + 5", food_days=4, days_to_growing=40, days_to_winter=10) is True
    assert ev("-a < 0", a=3) is True
    assert ev("a * 2 > b / 2", a=3, b=4) is True
    assert ev("a / 0 > 1", a=3) is None
    assert ev("a + 1 > 1", a=None) is None
    assert ev("0 < a < 5", a=3) is True and ev("0 < a < 5", a=7) is False
    assert ev("true") is True and ev("false") is False and ev("a == True", a=True) is True


def test_always_on_flag_and_exclusion():
    rules = {r[0]: r for r in sr.load_rules()}
    always = [r for r in rules.values() if r[4]]
    assert always and all(r[3]({}) is True for r in always)
    fired_ids = {i for _, i, _ in sr.fired_rules({"food_days": 1})}
    assert "food.emergency" in fired_ids and not fired_ids & {r[0] for r in always}


def test_priority_sort_cap_and_p1_always():
    fired = [(2, f"b{i}", "x") for i in range(10)] + [(1, f"a{i}", "y") for i in range(11)] + [(3, "c", "z")]
    sel = sr.select(sorted(fired))
    assert all(r[0] == 1 for r in sel) and len(sel) == 11   # all P1 shown even over the cap of 8
    sel = sr.select(sorted(fired[:10] + fired[10:13]))
    assert len(sel) == 8 and sel[0][0] == 1 and [r[0] for r in sel] == sorted(r[0] for r in sel)


def test_facts_derivation_from_digest_and_precedence():
    d = json.loads(json.dumps(SAMPLE))
    del d["facts"]
    f = sr.derive_facts(d)
    assert f["food_days"] == 2.4 and f["colonists"] == 5 and f["mood_min"] == 38 and f["mood_avg"] == 56.6
    assert f["colonists_downed"] == 0 and f["beds_free"] == 2
    assert f["steel"] == 410 and f["wood"] == 280 and f["components"] == 6 and "silver" not in f
    assert f["medicine_total"] == 4 and f["medicine_industrial"] == 0
    assert f["research_current"] == "Electricity" and f["raid_active"] is False and f["enemies_count"] == 0
    assert f["fires"] == 0 and f["season"] == "spring" and f["temp_c"] == 12.5
    assert f["power_net_w"] == 0 and f["battery_wd"] == 0
    assert "day" not in f and "prisoners" not in f and "dlc" not in f      # never invented
    d["facts"] = {"food_days": 9.0, "day": 33}
    f = sr.derive_facts(d)
    assert f["food_days"] == 9.0 and f["day"] == 33 and f["colonists"] == 5
    d["threats"] = [{"count": 4, "label": "raiders", "hostile": True}, {"count": 2, "label": "x", "hostile": False}]
    d["alerts"] = [{"alert": "Alert_Fire", "label": "Fire", "critical": True}]
    f = sr.derive_facts({k: v for k, v in d.items() if k != "facts"})
    assert f["raid_active"] is True and f["enemies_count"] == 4 and f["fires"] == 1
    assert sr.derive_facts({}) == {}


def test_alerts_section_shape_and_omitted_when_none():
    s = sr.alerts_section(SAMPLE)
    assert s.startswith(sr.HEADING + "\n") and "[P1 food.emergency] " in s and len(s) <= sr.SECTION_CAP
    lines = s.splitlines()[1:]
    assert all(l.startswith("[P") for l in lines) and len(lines) <= 8
    assert [l.split()[0] for l in lines] == sorted(l.split()[0] for l in lines)
    calm = {"date": {"season": "spring"}, "food_days": 30, "pawns": [], "resources": []}
    assert "food.emergency" not in sr.alerts_section(calm)


def test_alerts_section_cap_keeps_p1(monkeypatch):
    big = "word " * 120
    rules = tuple((f"r.{i}", 1 if i < 3 else 2, big, (lambda f: True), False) for i in range(8))
    monkeypatch.setattr(sr, "load_rules", lambda: rules)
    s = sr.alerts_section({})
    assert len(s) <= sr.SECTION_CAP
    assert s.count("[P1 ") == 3 and "[P1 r.0]" in s and s.count("[P2 ") == 0


def test_user_turn_has_alerts_after_why_you_woke(world):
    async def go():
        w = world
        w.fb.digest = json.loads(json.dumps(SAMPLE))
        await w.go()
        st = w.d.steward
        digest = w.fb.digest
        user = st._user("heartbeat", [], [], {"rev": 0}, "DIGEST", False, StewardKit().playbook_alerts(digest))
        assert user.index("=== WHY YOU WOKE ===") < user.index("=== PLAYBOOK ALERTS ===") < user.index("=== SETTINGS ===")
        assert "[P1 food.emergency]" in user
        assert "PLAYBOOK ALERTS" not in st._user("heartbeat", [], [], {"rev": 0}, "DIGEST", False, "")
    run(go())


def test_shipped_strategy_loaded_capped_and_override_order(tmp_path, monkeypatch):
    kit = StewardKit()
    text = kit.default_strategy()
    assert text.startswith("You are the RimWorld colony steward") and "## Order of concern" in text
    assert not (Path(sr.__file__).parent / "steward_strategy.txt").exists()
    cfg = Config()
    assert cfg.steward_strategy_max_bytes == 12288 and len(text.encode()) <= cfg.steward_strategy_max_bytes
    got, note = stew.load_strategy(cfg, text)
    assert got == text.strip() and note == ""
    c = tmp_path / "c.md"
    c.write_text("CONFIG")
    e = tmp_path / "e.md"
    e.write_text("ENV")
    assert stew.load_strategy(Config(steward_strategy_path=str(c)), text)[0] == "CONFIG"
    monkeypatch.setenv(stew.ENV_STRATEGY, str(e))
    assert stew.load_strategy(Config(steward_strategy_path=str(c)), text)[0] == "ENV"


def test_system_prompt_says_only_listed_tools():
    s = StewardKit().system_prompt(False)
    assert "only the tools listed" in s and "recommend it to the player" in s and "you have no tool for" in s


def test_package_data_lists_strategy_files():
    py = (Path(__file__).parent.parent / "pyproject.toml").read_text()
    assert "strategy/steward_strategy.md" in py and "strategy/rules.json" in py


def test_cycle_sends_alerts_to_the_model(world):
    from test_steward import DONE, boot, chat, cycle

    async def go():
        w, st, fake = await boot(world, [DONE], playing=False)
        chat(w, "how is the food?")
        await cycle(w)
        user = fake.calls[0]["messages"][1]["content"]
        assert "=== PLAYBOOK ALERTS ===" in user and "[P1 food.emergency]" in user
        assert user.index("WHY YOU WOKE") < user.index("PLAYBOOK ALERTS") < user.index("SETTINGS")
    run(go())


def test_unknown_research_name_turns_rule_off(caplog):
    from rimworldllm_agent.games.rimworld import steward_rules as sr
    run = sr.compile_condition("'Batteries' not in research_done and colonists > 0")
    assert run.research_names == frozenset({"Batteries"})
    facts = {"research_done": [], "colonists": 3}
    assert run(facts) is True
    assert sr.unknown_research(run.research_names, facts) == frozenset()          # no list: cannot judge
    facts["research_known"] = ["Electricity"]
    assert sr.unknown_research(run.research_names, facts) == frozenset({"Batteries"})
    facts["research_known"] = ["Batteries", "Electricity"]
    assert sr.unknown_research(run.research_names, facts) == frozenset()


def test_fired_rules_skip_rules_with_unknown_research():
    from rimworldllm_agent.games.rimworld import steward_rules as sr
    rules = [r for r in sr.load_rules() if r[3].research_names]
    assert rules, "the shipped playbook names some research"
    rid = rules[0][0]
    names = rules[0][3].research_names
    base = {"research_known": []}                                               # the game defines no research at all
    assert all(r[1] not in {x[0] for x in rules} for r in sr.fired_rules(base))
    known = {"research_known": sorted(names)}
    assert sr.unknown_research(names, known) == frozenset()
    assert rid


def test_facts_count_stored_plus_loose_but_not_forbidden():
    d = json.loads(json.dumps(SAMPLE))
    del d["facts"]
    d["resources"] = [
        {"category": "materials", "label": "steel", "count": 40, "loose": 300, "forbidden": 50},
        {"category": "materials", "label": "components", "count": 0, "loose": 7, "forbidden": 0},
    ]
    f = sr.derive_facts(d)
    assert f["steel"] == 340 and f["components"] == 7


# ---- rules on the narrator-view facts
def _fired(**f):
    return {rid for _, rid, _ in sr.fired_rules(f)}


@pytest.mark.parametrize("facts,rid", [
    (dict(loose_unstorable=2), "build.loose_no_stockpile"),
    (dict(stuck_no_material=1), "build.stuck_no_material"),
    (dict(stuck_no_builder=True), "build.stuck_no_builder"),
    (dict(bills_stuck=1), "work.bills_stuck"),
    (dict(crops_ready=12, food_days=2.0), "food.harvest_before_starving"),
    (dict(huntable=3, food_days=4.9), "food.hunt_before_starving"),
    (dict(food_rot_soon=30), "food.rot_soon"),
    (dict(days_to_winter=8.0), "winter.coming"),
    (dict(days_to_winter=2.0), "food.last_harvest"),
    (dict(bleed_out_hours=5.0), "health.bleeding"),
    (dict(infections_losing=1), "health.infection_losing"),
])
def test_new_fact_rules_fire(facts, rid):
    assert rid in _fired(**facts)


@pytest.mark.parametrize("facts,rid", [
    (dict(loose_unstorable=0), "build.loose_no_stockpile"),
    (dict(stuck_no_material=0, stuck_no_builder=False, bills_stuck=0), "build.stuck_no_material"),
    (dict(stuck_no_builder=False), "build.stuck_no_builder"),
    (dict(bills_stuck=0), "work.bills_stuck"),
    (dict(crops_ready=12, food_days=9.0), "food.harvest_before_starving"),
    (dict(crops_ready=0, food_days=2.0), "food.harvest_before_starving"),
    (dict(huntable=0, food_days=2.0), "food.hunt_before_starving"),
    (dict(food_rot_soon=0), "food.rot_soon"),
    (dict(days_to_winter=30.0), "winter.coming"),
    (dict(days_to_winter=2.0), "winter.coming"),
    (dict(bleed_out_hours=90.0), "health.bleeding"),
    (dict(infections_losing=0), "health.infection_losing"),
])
def test_new_fact_rules_stay_quiet(facts, rid):
    assert rid not in _fired(**facts)


def test_new_fact_rules_skip_when_the_fact_is_missing():
    assert not _fired(food_days=2.0) & {"food.harvest_before_starving", "food.hunt_before_starving", "food.rot_soon", "winter.coming",
                                         "build.loose_no_stockpile", "build.stuck_no_material", "build.stuck_no_builder", "work.bills_stuck", "health.infection_losing"}


def test_sample_facts_fire_the_expected_rules():
    got = _fired(**SAMPLE["facts"])
    assert {"build.loose_no_stockpile", "build.stuck_no_material", "work.bills_stuck", "food.rot_soon", "food.harvest_before_starving",
            "food.hunt_before_starving", "winter.coming", "health.bleeding"} <= got
    assert not got & {"build.stuck_no_builder", "health.infection_losing"}
    text = sr.alerts_section(SAMPLE)
    assert "PLAYBOOK ALERTS" in text and len(text) <= sr.SECTION_CAP

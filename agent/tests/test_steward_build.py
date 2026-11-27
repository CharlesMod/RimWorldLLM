"""The steward's builder tools (steward_build.py): presence by digest, enums, bodies per kind, errors, why, grammar."""
import copy
import json
from pathlib import Path

import pytest

from conftest import run
from rimworldllm_agent.core.toolloop import spec as tl
from rimworldllm_agent.games.rimworld.fakes import FakeBridge
from rimworldllm_agent.games.rimworld.steward_tools import StewardKit

SAMPLE = json.loads((Path(__file__).parent / "data" / "steward_digest_sample.json").read_text())
BUILD = {
    "home": {"x": 120, "z": 98},
    "rooms": [{"id": 12, "role": "bedroom", "label": "Ana's bedroom"}, {"id": 14, "role": "kitchen", "label": "Kitchen"}],
    "missing": ["stove"],
    "benches": [{"id": 4410, "label": "fueled stove", "recipes": ["simple meal", "fine meal", "pemmican"], "bills": []},
                {"id": 4411, "label": "tailor bench"}],
    "zones": [], "can_build": ["wall", "door", "bed", "stove", "cooler"],
    "stuff": [{"stuff": "wood", "count": 280}, {"stuff": "steel", "count": 410}],
    "plants": ["potatoes", "rice", "corn"],
    "ores": [{"ore": "steel", "cells": 34, "dist": 18}],
}


def digest(build=BUILD):
    d = copy.deepcopy(SAMPLE)
    if build is None:
        d.pop("build", None)
    else:
        d["build"] = copy.deepcopy(build)
    return d


def session(build=BUILD, bridge=None):
    return StewardKit().session(digest(build), bridge, False)


def tool(s, name):
    return next(t for t in s.tools if t.name == name)


def choices(s, name, param):
    return next(p for p in tool(s, name).params if p.name == param).choices


def call(s, name, **a):
    spec = tool(s, name)
    v, err = spec.check_args(a, False)
    assert err is None, err
    return v


def do(fb, s, name, **a):
    async def go():
        return await s.run(name, call(s, name, **a))
    return run(go())


@pytest.fixture
def fb():
    f = FakeBridge("tok")
    return f


def live(fb, build=BUILD):
    import httpx
    from rimworldllm_agent.core.bridge import BridgeClient
    from rimworldllm_agent.games.rimworld import RimWorldAdapter
    br = BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", "tok", RimWorldAdapter().protocol)
    return session(build, br)


# ---- presence and enums
def test_no_build_block_no_build_tools():
    s = session(None)
    assert not {"build", "zone", "designate", "bill"} & {t.name for t in s.tools}
    assert not {"build", "zone", "designate", "bill"} & s.acting


def test_build_tools_present_and_acting():
    s = session()
    assert {"build", "zone", "designate", "bill"} <= {t.name for t in s.tools}
    assert {"build", "zone", "designate", "bill"} <= s.acting


def test_enums_come_from_the_digest():
    s = session()
    assert choices(s, "build", "kind") == ["room", "thing"]
    assert choices(s, "build", "thing") == ["none"] + BUILD["can_build"]
    assert choices(s, "build", "stuff") == ["none", "wood", "steel"]
    assert choices(s, "build", "room") == ["none", "12", "14"]
    assert choices(s, "build", "purpose")[1:] == ["bedroom", "barracks", "kitchen", "dining", "storage", "freezer", "hospital", "workshop", "rec"]
    assert choices(s, "zone", "plant") == ["none", "potatoes", "rice", "corn"]
    assert choices(s, "zone", "kind") == ["grow", "stockpile", "dump"]
    assert choices(s, "designate", "ore") == ["none", "steel"]
    assert choices(s, "bill", "bench") == ["4410", "4411"]
    assert "12 bedroom" in next(p for p in tool(s, "build").params if p.name == "room").description


def test_missing_data_drops_kinds_and_params():
    b = dict(BUILD, plants=[], ores=[], can_build=[], rooms=[], benches=[], stuff=[])
    s = session(b)
    assert {t.name for t in s.tools} >= {"build", "zone", "designate"} and "bill" not in {t.name for t in s.tools}
    assert choices(s, "build", "kind") == ["room"]
    assert choices(s, "zone", "kind") == ["stockpile", "dump"]
    assert choices(s, "designate", "kind") == ["chop", "harvest", "haul"]
    names = {p.name for t in s.tools for p in t.params}
    assert not {"thing", "room", "stuff", "plant", "ore"} & {p.name for t in s.tools if t.name in TOOLS for p in t.params}
    assert "why" in names


TOOLS = ("build", "zone", "designate", "bill")


def test_no_deconstruct_or_cancel_and_no_player_asked():
    s = session()
    assert set(choices(s, "designate", "kind")) == {"mine", "chop", "harvest", "haul"}
    for t in s.tools:
        assert "player_asked" not in {p.name for p in t.params}


# ---- bodies
def test_build_room_body(fb):
    s = live(fb)
    r = do(fb, s, "build", kind="room", purpose="bedroom", size="medium", why="because beds are missing")
    assert r.ok and r.verb == "room"
    assert fb.act_log[-1] == ("room", {"purpose": "bedroom", "size": "medium", "why": "because beds are missing"})
    do(fb, s, "build", kind="room", purpose="kitchen", size="small", stuff="steel", thing="none", room="none")
    assert fb.act_log[-1] == ("room", {"purpose": "kitchen", "size": "small", "stuff": "steel"})


def test_build_thing_body(fb):
    s = live(fb)
    do(fb, s, "build", kind="thing", thing="stove", room="14", stuff="steel")
    assert fb.act_log[-1] == ("build", {"thing": "stove", "stuff": "steel", "room": 14})
    do(fb, s, "build", kind="thing", thing="bed")
    assert fb.act_log[-1] == ("build", {"thing": "bed"})


def test_build_errors_come_back(fb):
    s = live(fb)
    n = len(fb.act_log)
    r = do(fb, s, "build", kind="room", purpose="bedroom")
    assert "error" in r.result and "size" in r.result["error"]
    r = do(fb, s, "build", kind="thing")
    assert "thing" in r.result["error"]
    assert len(fb.act_log) == n
    # a room ignores thing/room (the grammar writes every param; the model fills them with what goes inside later)
    do(fb, s, "build", kind="room", purpose="freezer", size="small", thing="cooler", room="12")
    assert fb.act_log[-1] == ("room", {"purpose": "freezer", "size": "small"})


def test_zone_bodies(fb):
    s = live(fb)
    do(fb, s, "zone", kind="grow", plant="rice", size="large", why="since food is low")
    assert fb.act_log[-1] == ("zone", {"kind": "grow", "plant": "rice", "size": "large", "why": "since food is low"})
    do(fb, s, "zone", kind="stockpile", preset="food", room="14")
    assert fb.act_log[-1] == ("zone", {"kind": "stockpile", "preset": "food", "room": 14})
    do(fb, s, "zone", kind="dump", size="small")
    assert fb.act_log[-1] == ("zone", {"kind": "dump", "size": "small"})


def test_zone_grow_without_plant_is_an_error(fb):
    s = live(fb)
    r = do(fb, s, "zone", kind="grow", size="small")
    assert "plant" in r.result["error"] and not fb.act_log
    r = do(fb, s, "zone", kind="grow", plant="none")
    assert "error" in r.result
    assert "error" in do(fb, s, "zone", kind="dump", room="12").result
    assert "error" in do(fb, s, "zone", kind="dump", plant="rice").result


def test_designate_bodies(fb):
    s = live(fb)
    do(fb, s, "designate", kind="mine", ore="steel")
    assert fb.act_log[-1] == ("designate", {"kind": "mine", "ore": "steel"})
    for k in ("chop", "harvest", "haul"):
        do(fb, s, "designate", kind=k, why="since wood is short")
        assert fb.act_log[-1] == ("designate", {"kind": k, "why": "since wood is short"})
    assert "ore" in do(fb, s, "designate", kind="mine").result["error"]
    assert "error" in do(fb, s, "designate", kind="chop", ore="steel").result


def test_bill_bodies(fb):
    s = live(fb)
    do(fb, s, "bill", op="add", bench="4410", recipe="Simple Meal", until=15)
    assert fb.act_log[-1] == ("bill", {"op": "add", "bench": 4410, "recipe": "simple meal", "until": 15})
    do(fb, s, "bill", op="add", bench="4410", recipe="pemmican", count=10, until=0)
    assert fb.act_log[-1] == ("bill", {"op": "add", "bench": 4410, "recipe": "pemmican", "count": 10})
    for op in ("remove", "suspend", "resume"):
        do(fb, s, "bill", op=op, bench="4410", recipe="simple meal", count=5)
        assert fb.act_log[-1] == ("bill", {"op": op, "bench": 4410, "recipe": "simple meal"})


def test_bill_recipe_validated_against_the_bench(fb):
    s = live(fb)
    r = do(fb, s, "bill", op="add", bench="4410", recipe="rocket")
    assert "cannot make rocket" in r.result["error"] and "simple meal" in r.result["error"] and not fb.act_log
    r = do(fb, s, "bill", op="add", bench="4410", recipe="simple meal", count=3, until=9)
    assert "not both" in r.result["error"]
    do(fb, s, "bill", op="add", bench="4411", recipe="anything the game knows")      # no recipe list: the game checks
    assert fb.act_log[-1][1]["recipe"] == "anything the game knows"


def test_game_refusal_goes_back_to_the_model(fb):
    s = live(fb)
    fb.act_refuse["room"] = "no clear site near home"
    r = do(fb, s, "build", kind="room", purpose="bedroom", size="small")
    assert r.result == {"refused": "no clear site near home"} and not r.ok


def test_why_is_cleaned_and_capped(fb):
    s = live(fb)
    why = next(p for p in tool(s, "bill").params if p.name == "why")
    assert why.required is False and why.max_len == 80
    v, err = tool(s, "bill").check_args({"op": "add", "bench": "4410", "recipe": "simple meal", "why": "x" * 81}, False)
    assert err and "why" in err
    do(fb, s, "bill", op="add", bench="4410", recipe="simple meal")
    assert "why" not in fb.act_log[-1][1]


# ---- optional params in every protocol
def test_optional_params_in_json_and_native_and_grammar():
    s = session()
    t = tool(s, "build")
    v, err = t.check_args({"kind": "room", "purpose": "bedroom", "size": "small"}, False)       # json/native: omitted
    assert err is None and "thing" not in v
    full = {"kind": "room", "purpose": "bedroom", "size": "small", "thing": "none", "room": "none", "stuff": "none", "why": "since beds"}
    assert t.check_args(full, False)[1] is None                                                   # grammar: all written
    oa = t.openai_tool()["function"]["parameters"]
    assert oa["required"] == ["kind"] and "none" in oa["properties"]["thing"]["enum"]
    g = tl.build_grammar(s.tools)
    assert "bill" in g and '\\"recipe\\":' in g and "zone" in g and "designate" in g
    assert "call" in g and g.startswith("root ::=")
    sig = [x for x in s.tools if x.name == "bill"][0].signature()
    assert sig == "bill(op, bench, recipe, count?, until?, why?)"


def test_prompt_and_size():
    kit = StewardKit()
    sys_ = kit.system_prompt(False)
    assert "only the tools listed" in sys_ and "planners pick the cells" in sys_ and "graves" in sys_
    strat = kit.default_strategy()
    assert "## Build order" in strat and len(strat.encode()) <= 12288
    s = session()
    assert len(tl.render_tools(s.tools)) < 8500   # 7.7k with main's why on every acting tool (13 x ~250 chars)


def test_cooler_only_into_a_non_living_room(fb):
    s = live(fb)
    n = len(fb.act_log)
    r = do(fb, s, "build", kind="thing", thing="cooler")
    assert "freezer" in r.result["error"]
    r = do(fb, s, "build", kind="thing", thing="cooler", room="12")          # 12 is a bedroom
    assert "freezer" in r.result["error"]
    assert len(fb.act_log) == n
    do(fb, s, "build", kind="thing", thing="cooler", room="14")              # 14 is the kitchen
    assert fb.act_log[-1] == ("build", {"thing": "cooler", "room": 14})

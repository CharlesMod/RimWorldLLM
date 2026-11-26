"""Rich fixture, one-record-per-text logging, creative_probe (--fake) and judge_probe round trip.  Temp dirs only."""
import json
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import creative_probe as cp  # noqa: E402
import fixture_rich as fx  # noqa: E402
import judge_probe as jp  # noqa: E402
import soak  # noqa: E402
from hermesvoice_agent.games.rimworld.fakes import FakeBridge  # noqa: E402


def colony():
    fb = FakeBridge("tok")
    return fb, fx.build_colony(fb, random.Random(1))


def test_rich_colony_shape_and_canaries():
    fb, table = colony()
    assert len(fb.pawns) >= 8 and len(table) == len(fb.pawns)
    ages = sorted(p["age"] for p in fb.pawns.values())
    assert ages.count(14) == 1 and ages[0] == 14 and ages[-1] >= 65
    assert len({tuple(sorted(t["label"] for t in p["traits"])) for p in fb.pawns.values()}) == len(fb.pawns)
    assert len({p["ideo"]["name"] for p in fb.pawns.values()}) >= 2
    assert len({tuple(m["label"] for m in p["ideo"]["memes"]) for p in fb.pawns.values()}) >= 5
    tales = [t for p in fb.pawns.values() for t in p["history"]["tales"]]
    assert len(tales) == len(set(tales)) and all(len(p["history"]["tales"]) >= 2 for p in fb.pawns.values())
    assert "PINEAPPLE-7" in fb.pawns[1]["name"]["nick"]
    assert "ZEBRA42" in fb.pawns[2]["backstory"]["adult"]["desc"]
    assert any("BANANA-SPLIT" in t["desc"] for t in fb.pawns[3]["traits"])
    assert any("KIWI-88" in t for t in fb.pawns[4]["history"]["tales"])
    assert set(soak.CANARIES) == set(fx.CANARIES)
    kinds = {r["kind"] for p in fb.pawns.values() for r in p["relations"]}
    assert {"spouse", "lover", "rival", "sibling", "friend", "parent", "child"} <= kinds
    assert any(r["opinion"] < 0 for r in fb.pawns[3]["relations"])


def test_rich_script_art_and_story():
    ids: set = set()
    S = fx.build_script(2, ids, 0, 1)
    arts = [s for s in S if s.get("type") == "art_created"]
    d0 = [s for s in arts if s["tick"] < fx.DAY]
    assert len(d0) >= 6
    assert len({s["data"]["description"] for s in d0}) == len(d0)
    assert len({s["data"]["tale"]["summary"] for s in d0}) == len(d0)
    assert len({s["actors"][0][0] for s in d0}) >= 5
    for s in arts:
        t = s["data"]["tale"]
        assert s["data"]["material"] and t["def"] and t["summary"] and isinstance(t["day"], int) and t["pawns"]
        assert "represents the events of" in s["data"]["description"]
    assert len(ids) == len(arts)
    types = [s.get("type") for s in S]
    assert types.count("death") == 2 and types.count("raid_start") >= 2
    assert any(s.get("data", {}).get("kind") == "wedding" for s in S if s["kind"] == "event")
    assert sum(1 for s in S if s.get("data", {}).get("kind") == "funeral") >= 2


def rec(kind, actor, text, **kw):
    return {"kind": kind, "actor": actor, "name": "x", "age": 30, "text": text, "source": "s", "tick": 1, "speed": 1, "ts": float(len(text)), **kw}


def test_merge_duplicates_one_record_per_text():
    out = soak.merge_duplicates([
        rec("dream", 1, "I was walking in the rice."), rec("inner_dream", 1, "I was walking in the rice."),
        rec("thought", 2, "The stew is cold."), rec("inner_musing", 2, "the stew is cold"),
        rec("lore", None, "Widow's Rest: the east hill where the first colonist was buried"), rec("lore", None, "the east hill where the first colonist was buried"),
        rec("line", 3, "Pass the salt."), rec("line", 4, "Pass the salt."),
        rec("aim", 5, "I wish he would stay."), rec("inner_musing", 5, "A different thought entirely."),
    ])
    seen = [(o["actor"], soak._norm(o["text"])) for o in out]
    by = {(o["kind"], o["actor"]): o for o in out}
    assert by[("dream", 1)]["also"] == ["inner_dream"] and ("inner_dream", 1) not in by
    assert by[("thought", 2)]["also"] == ["inner_musing"]
    lores = [o for o in out if o["kind"] == "lore"]
    assert len(lores) == 1 and lores[0]["text"].startswith("Widow's Rest")
    assert len([o for o in out if o["kind"] == "line"]) == 2        # two actors, same words: both stay
    assert ("inner_musing", 5) in by and ("aim", 5) in by


def test_probe_fake_rows_summary_and_unknown_set(tmp_path, capsys):
    out = tmp_path / "p"
    rc = cp.main(["--fake", "--reps", "2", "--out", str(out), "--set", "no_such_flag=1", "--set", "lore=true"])
    assert rc == 0
    assert "no_such_flag" in capsys.readouterr().err
    rows = [json.loads(l) for l in (out / "probe.jsonl").read_text().splitlines()]
    assert {r["feature"] for r in rows} == set(cp.FEATURES)
    for r in rows:
        assert {"arm", "sets", "feature", "case", "rep", "actor", "text", "drop", "latency_s", "calls"} <= set(r)
    assert all(r["sets"] == {"lore": True} for r in rows)
    assert {r["rep"] for r in rows} == {0, 1}
    aim = [r for r in rows if r["feature"] == "aim"]
    assert len(aim) == 2 * len(cp.AIM_SCENES) and all("label" in r and "delivery" in r and "line" in r for r in aim)
    assert len([r for r in rows if r["feature"] == "art"]) >= 12
    summ = json.loads((out / "summary.json").read_text())["features"]
    assert set(summ) == set(cp.FEATURES)
    for f, s in summ.items():
        assert {"n", "dropped", "latency_median", "latency_p90", "calls_mean"} <= set(s)
    assert {"share_i_just_want", "distinct_aim_share", "mean_pairwise_jaccard", "words_mean"} <= set(summ["aim"])


def test_probe_set_parsing_and_bad_value():
    class C:
        a = 3
        b = True
        c = 0.5
        d = "x"

    c = C()
    got = cp.apply_sets(c, [("a", "7"), ("b", "false"), ("c", "1.5"), ("d", "yy")], warn=lambda m: None)
    assert (c.a, c.b, c.c, c.d) == (7, False, 1.5, "yy") and got["a"] == 7
    try:
        cp.apply_sets(c, [("a", "notint")])
        raise AssertionError("expected ValueError")
    except ValueError:
        pass


def test_judge_probe_round_trip(tmp_path):
    runs = []
    for arm in ("base", "new"):
        out = tmp_path / arm
        assert cp.main(["--fake", "--reps", "2", "--arm", arm, "--out", str(out), "--features", "art,aim,recap"]) == 0
        runs.append(str(out))
    sample, key = tmp_path / "s.md", tmp_path / "key.json"
    assert jp.main(["make", "--runs", *runs, "--out", str(sample), "--key", str(key), "--per-feature-arm", "4"]) == 0
    md = sample.read_text()
    k = json.loads(key.read_text())
    assert {v["arm"] for v in k.values()} == {"base", "new"}
    assert "base" not in md.split("### #1")[1] and "new" not in md.split("### #1")[1].split("> ")[0]
    ver = tmp_path / "v.jsonl"
    ver.write_text("\n".join(json.dumps({"n": int(n), "score": 4 if v["arm"] == "new" else 2, "why": "t"}) for n, v in k.items()) + "\n")
    tbl, js = tmp_path / "t.md", tmp_path / "t.json"
    assert jp.main(["score", "--key", str(key), "--verdicts", str(ver), "--out", str(tbl), "--json", str(js)]) == 0
    res = json.loads(js.read_text())
    assert res["probe"]["art"]["new"]["mean"] == 4 and res["probe"]["art"]["base"]["share_le2"] == 1.0
    p = res["paired"]["art"]["base - new"]
    assert p["mean_diff"] == -2 and p["losses"] == p["n"] > 0


def test_score_soak_sample_key(tmp_path):
    key = tmp_path / "k.json"
    key.write_text(json.dumps({"1": {"run": "r", "kind": "art", "actor": None}, "2": {"run": "r", "kind": "inner_dream", "actor": 1},
                               "3": {"run": "r", "kind": "line", "actor": 1}}))
    ver = tmp_path / "v.jsonl"
    ver.write_text('{"n":1,"score":5}\n{"n":2,"score":2}\n{"n":3,"score":3}\n')
    js = tmp_path / "o.json"
    assert jp.main(["score", "--key", str(key), "--verdicts", str(ver), "--out", str(tmp_path / "o.md"), "--json", str(js)]) == 0
    fam = json.loads(js.read_text())["soak_families"]
    assert fam["art"]["mean"] == 5 and fam["aim"]["share_le2"] == 1.0 and fam["speech"]["n"] == 1


def test_set_parses_comma_lists_for_tuple_fields():
    import creative_probe as cp
    assert cp._parse_as((), "lore, eulogy") == ("lore", "eulogy")
    assert cp._parse_as(("x",), "") == ()

"""register_probe with the agent's FakeLLM: output files, matched pairs, stats helpers."""
import json, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import register_probe as rp  # noqa: E402


def test_fake_run_writes_rows_and_summary(tmp_path):
    assert rp.main(["--fake", "--reps", "2", "--out", str(tmp_path)]) == 0
    probe = json.loads((tmp_path / "probe.json").read_text())
    summ = json.loads((tmp_path / "probe-summary.json").read_text())
    kinds = {r["kind"] for r in probe["rows"]}
    assert {"line", "thought", "diary", "grounding_line"} <= kinds
    assert sum(r["kind"] == "line" for r in probe["rows"]) == 6 * 2 * len(rp.SCENES)
    assert set(summ["register_pairs"]) == {"smart_vs_dull", "sober_vs_drunk", "adult_vs_child"}
    assert "Right now" in summ["register_pairs"]["sober_vs_drunk"]["_b_right_now"]
    assert len(probe["grounding"]) == 3 and all(len(g["lines"]) == 8 for g in probe["grounding"])
    cost = summ["card_cost"]
    assert cost["token_method"] == "chars/4" and cost["mean_delta"]["delta_block_b_chars"] > 0


def test_pairs_differ_only_in_register_inputs():
    smart, dull = rp.make_pairs()["smart_vs_dull"]
    assert rp.register.base_register(smart) > rp.register.base_register(dull)
    adult, child = rp.make_pairs()["adult_vs_child"]
    assert rp.agegate.is_minor(child["age"]) and not rp.agegate.is_minor(adult["age"])


def test_stats_helpers():
    assert rp.cohen_d([1, 2, 3], [1, 2, 3]) == 0.0
    assert rp.cohen_d([5, 6, 7], [1, 2, 3]) > 3
    assert rp.mattr(["a"] * 100) < 0.05
    assert rp.old_snapshot(rp.rich_pawn())["history"] == {}

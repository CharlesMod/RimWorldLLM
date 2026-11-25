"""Step 7: replay over a recorded multi-day file shows reflections, diaries and chronicle entries."""
from pathlib import Path

from conftest import run
from hermesvoice_agent.core.config import Config
from hermesvoice_agent.core.cli import _replay
from hermesvoice_agent.games.rimworld import RimWorldAdapter
from hermesvoice_agent.games.rimworld.replay import replay

DATA = Path(__file__).parent / "data" / "multiday.jsonl"


def test_replay_multiday_shows_idle_work_and_leaves_the_data_dir_alone(tmp_path):
    cfg = Config(data_dir=tmp_path / "real")
    out = run(replay(DATA, cfg))
    kinds = [i.get("kind") for i in out]
    assert {"reflection", "diary", "chronicle"} <= set(kinds)
    days = {i["day"] for i in out if i.get("kind") == "diary"}
    assert days == {0, 1, 2, 3}
    chron = [i for i in out if i.get("kind") == "chronicle"]
    assert any(c["entry_kind"] == "major" for c in chron) and len(chron) >= 3
    assert any(i.get("text") and "kind" not in i for i in out)           # actor lines still present
    assert not (tmp_path / "real").exists()                               # replay used its own temp dir
    assert [i.get("kind") for i in run(replay(DATA, cfg))] == kinds       # deterministic offline


def test_replay_cli_prints_the_new_kinds(tmp_path, capsys):
    run(_replay(Config(data_dir=tmp_path), RimWorldAdapter(), str(DATA), False))
    out = capsys.readouterr().out
    assert "[diary day 1]" in out and "[chronicle day" in out and "[reflection]" in out

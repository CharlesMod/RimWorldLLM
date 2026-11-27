"""One-time move of the agent's data folder from its old name (hermesvoice-agent) to rimworldllm-agent."""
from pathlib import Path

import pytest

from rimworldllm_agent.core import config


@pytest.fixture
def share(monkeypatch, tmp_path):
    monkeypatch.delenv("RIMWORLDLLM_DATA_DIR", raising=False)
    monkeypatch.setattr(config, "_windows", lambda: False)
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    return tmp_path


def test_moves_old_folder_when_new_is_missing(share):
    old = share / "hermesvoice-agent"
    (old / "logs").mkdir(parents=True)
    (old / "memory.db").write_text("m")
    new = config.default_data_dir()
    assert config.migrate_legacy_data_dir(new) == "moved"
    assert (new / "memory.db").read_text() == "m"
    assert (new / "logs").is_dir()
    assert not old.exists()


def test_both_exist_leaves_both(share, caplog):
    old = share / "hermesvoice-agent"; old.mkdir(); (old / "a").write_text("old")
    new = share / "rimworldllm-agent"; new.mkdir(); (new / "a").write_text("new")
    with caplog.at_level("INFO"):
        assert config.migrate_legacy_data_dir(new) == "both"
    assert (old / "a").read_text() == "old" and (new / "a").read_text() == "new"
    assert sum("both" in r.getMessage() for r in caplog.records) == 1


def test_nothing_old(share):
    assert config.migrate_legacy_data_dir(config.default_data_dir()) == "none"
    assert not config.default_data_dir().exists()


def test_override_and_custom_paths_are_left_alone(share, monkeypatch):
    old = share / "hermesvoice-agent"; old.mkdir()
    assert config.migrate_legacy_data_dir(share / "elsewhere" / "rimworldllm-agent") == "custom"
    monkeypatch.setenv("RIMWORLDLLM_DATA_DIR", str(share / "mine"))
    assert config.migrate_legacy_data_dir(share / "mine") == "override"
    assert old.is_dir()


def test_old_symlink_is_not_followed(share):
    real = share / "real"; real.mkdir()
    (share / "hermesvoice-agent").symlink_to(real)
    assert config.migrate_legacy_data_dir(config.default_data_dir()) == "none"
    assert (share / "hermesvoice-agent").is_symlink()


def test_windows_location(monkeypatch, tmp_path):
    monkeypatch.delenv("RIMWORLDLLM_DATA_DIR", raising=False)
    monkeypatch.setattr(config, "_windows", lambda: True)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    (tmp_path / "hermesvoice-agent").mkdir()
    assert config.migrate_legacy_data_dir(tmp_path / "rimworldllm-agent") == "moved"
    assert (tmp_path / "rimworldllm-agent").is_dir()

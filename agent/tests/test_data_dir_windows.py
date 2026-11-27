"""Where the agent keeps its data on each OS (first Windows test: it was %USERPROFILE%\\.local\\share)."""
from pathlib import Path

from rimworldllm_agent.core import config


def test_windows_uses_localappdata(monkeypatch, tmp_path):
    monkeypatch.delenv("RIMWORLDLLM_DATA_DIR", raising=False)
    monkeypatch.setattr(config, "_windows", lambda: True)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "xdg"))   # ignored on Windows
    assert config.default_data_dir() == tmp_path / "Local" / "rimworldllm-agent"


def test_windows_without_localappdata(monkeypatch, tmp_path):
    monkeypatch.delenv("RIMWORLDLLM_DATA_DIR", raising=False)
    monkeypatch.delenv("LOCALAPPDATA", raising=False)
    monkeypatch.setattr(config, "_windows", lambda: True)
    monkeypatch.setattr(config.Path, "home", classmethod(lambda cls: tmp_path))
    assert config.default_data_dir() == tmp_path / "AppData" / "Local" / "rimworldllm-agent"


def test_posix_unchanged(monkeypatch, tmp_path):
    monkeypatch.delenv("RIMWORLDLLM_DATA_DIR", raising=False)
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.setattr(config, "_windows", lambda: False)
    assert config.default_data_dir() == tmp_path / "rimworldllm-agent"


def test_env_override_wins(monkeypatch, tmp_path):
    monkeypatch.setattr(config, "_windows", lambda: True)
    monkeypatch.setenv("RIMWORLDLLM_DATA_DIR", str(tmp_path / "x"))
    assert config.default_data_dir() == Path(tmp_path / "x")

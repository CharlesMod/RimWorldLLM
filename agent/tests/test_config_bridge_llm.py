import json

from rimworldllm_agent.core.config import load_config


def _bridge(tmp_path, llm):
    p = tmp_path / "bridge.json"
    p.write_text(json.dumps({"port": 1, "token": "t", "llm": llm}))
    return p


def _clean(monkeypatch):
    for k in ("RIMWORLDLLM_LLM_URL", "RIMWORLDLLM_LLM_KEY", "RIMWORLDLLM_LLM_MODEL", "RIMWORLDLLM_CONFIG", "RIMWORLDLLM_BRIDGE_JSON"):
        monkeypatch.delenv(k, raising=False)


def test_bridge_json_llm_used_when_no_url(tmp_path, monkeypatch):
    _clean(monkeypatch)
    monkeypatch.setenv("RIMWORLDLLM_BRIDGE_JSON", str(_bridge(tmp_path, {"base_url": "http://h:1/v1", "model": "m", "api_key": "k"})))
    c = load_config()
    assert (c.llm.base_url, c.llm.model, c.llm.api_key) == ("http://h:1/v1", "m", "k")


def test_env_beats_bridge_json(tmp_path, monkeypatch):
    _clean(monkeypatch)
    monkeypatch.setenv("RIMWORLDLLM_BRIDGE_JSON", str(_bridge(tmp_path, {"base_url": "http://h:1/v1", "model": "m", "api_key": "k"})))
    monkeypatch.setenv("RIMWORLDLLM_LLM_URL", "http://env:2")
    c = load_config()
    assert c.llm.base_url == "http://env:2"
    assert c.llm.api_key == ""


def test_file_beats_bridge_json(tmp_path, monkeypatch):
    _clean(monkeypatch)
    f = tmp_path / "c.json"
    f.write_text(json.dumps({"llm": {"base_url": "http://file:3"}, "bridge_json": str(_bridge(tmp_path, {"base_url": "http://h:1/v1"}))}))
    c = load_config(f)
    assert c.llm.base_url == "http://file:3"


def test_empty_or_missing_bridge_llm_keeps_default(tmp_path, monkeypatch):
    _clean(monkeypatch)
    monkeypatch.setenv("RIMWORLDLLM_BRIDGE_JSON", str(_bridge(tmp_path, {"base_url": "", "model": "", "api_key": ""})))
    assert load_config().llm.base_url == "http://127.0.0.1:8080"
    monkeypatch.setenv("RIMWORLDLLM_BRIDGE_JSON", str(tmp_path / "missing.json"))
    assert load_config().llm.base_url == "http://127.0.0.1:8080"

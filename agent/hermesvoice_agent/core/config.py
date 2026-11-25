"""Agent configuration.

Sources, lowest to highest priority: defaults, a TOML or JSON file (``--config`` or
``$HERMESVOICE_CONFIG``), environment variables (``HERMESVOICE_*``). When no LLM URL came from the file or the environment, the ``llm`` block of
``bridge.json`` (``base_url``, ``model``, ``api_key``) is used if ``bridge_json`` is known, so the order for the
LLM endpoint is env > file > bridge.json > default.
"""
from __future__ import annotations

import json
import os
import tomllib
from pathlib import Path

from pydantic import BaseModel, Field


def default_data_dir() -> Path:
    env = os.environ.get("HERMESVOICE_DATA_DIR")
    if env:
        return Path(env)
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "hermesvoice-agent"


class LLMConfig(BaseModel):
    base_url: str = "http://127.0.0.1:8080"
    api_key: str = ""
    model: str = ""
    # "openai": /v1/chat/completions only. "native": llama.cpp /completion with a
    # hand-built Gemma turn format, which gives id_slot control and cache stats.
    mode: str = "openai"
    # Sampler overrides. None (the default) means "let the engine decide": the engine applies the
    # model's own profile (X-Hermes-Sampler: rp | structured). Set a value only to force it; the
    # engine's sampler policy may still replace it. Against a non-engine endpoint the agent sends
    # Google's official Gemma 4 values itself, but only when ``model`` names a known Gemma 4 build.
    temperature: float | None = None
    top_k: int | None = None
    top_p: float | None = None
    min_p: float | None = None
    repeat_penalty: float | None = None
    # True: the endpoint is the hermes engine (sends X-Hermes-Sampler). False: it is not.
    # None: detect it by asking for the engine's /engine/v1/status once.
    engine: bool | None = None
    line_max_tokens: int = 48
    # DRY repetition control for the line pass (llama.cpp): off by default (no official DRY exists;
    # DRY made no clear difference, docs/MODEL-BENCH.md F6). The engine's enforce policy strips it.
    line_dry: bool = False
    dry_multiplier: float = 0.8
    dry_base: float = 1.75
    dry_allowed_length: int = 2
    dry_penalty_last_n: int = 512
    timeout_s: float = 30.0


class QuotaConfig(BaseModel):
    actor_per_day: int = 12          # lines per actor per game day
    pair_per_day: int = 4           # conversations/lines per ordered pair per game day
    units_per_day: int = 60000
    min_gap_game_ambient: int = 1500   # per actor, ambient class
    min_gap_real_s_ambient: float = 20.0
    ambient_share: float = 0.75         # ambient chatter may use this share of an actor's daily lines


class Config(BaseModel):
    bridge_json: Path | None = None   # None: the adapter's default location
    bridge_url: str | None = None      # override: skips bridge.json
    bridge_token: str | None = None
    data_dir: Path = Field(default_factory=default_data_dir)
    llm: LLMConfig = Field(default_factory=LLMConfig)
    quotas: QuotaConfig = Field(default_factory=QuotaConfig)

    narrator_slot: int = 0
    windows: int = 2              # slots 1..windows
    pause_ambient_at_speed: int = 4    # ambient and social talk stop at this speed (4 = dev speed); the bridge's /config overrides
    poll_interval_s: float = 1.0       # at 1x; shortened as the game speeds up (director.poll_delay)
    voice_lines_per_min: float = 5.0   # the ceiling on spoken lines per real minute at every speed (urgent bypasses the gate)
    voice_burst: float = 5.0
    voice_fast_scale: float = 0.5      # the ceiling is scaled by this at dev speed (4)
    turn_gap_ticks: int = 150          # least game time between a conversation's turns (bubbles need reading time)
    turn_gap_floor_real_s: float = 0.8 # ... and the least real time
    fast_summary_rate: float = 360.0   # from this game rate (3x) a finished conversation is remembered from a template
    vanilla_attach_floor_s: float = 5.0   # a line for the game's own interaction keeps this many real seconds to attach
    idle_starving_fraction: float = 0.25  # idle work due for this share of a game day runs even beside ambient chatter
    event_min_severity: float = 0.15   # below this never reaches the model
    ambient_min_severity: float = 0.25
    narrator_min_gap_real_s: float = 120.0
    reflection_every_days: float = 3.0
    reflection_importance: float = 3.0
    memory_floor: float = 0.03         # unpinned memories decayed below this weight are dropped
    memory_merge_similarity: float = 0.5
    reflection_max_tokens: int = 500
    diary_enabled: bool = True
    diary_day_fraction: float = 0.6    # write a day's entry once this much of the day has passed
    diary_max_tokens: int = 160
    wants: bool = True                 # wants with follow-through (core/wants.py); also needs the mod's wants_enabled
    chronicle_enabled: bool = True
    art_enabled: bool = True           # LLM art descriptions (core/art.py); also needs the mod's art_descriptions
    art_max_tokens: int = 110
    chronicle_bridge: bool = True      # also POST /chronicle (contract: the mod saves the chronicle with the game)
    chronicle_max_tokens: int = 220
    recap_enabled: bool = True         # "Previously on..." recap after a save load (core/recap.py; also the mod's recap setting)
    narrator_context_tokens: int = 6000
    narrator_min_gap_game: int = 6000  # coverage lines: min gap on both clocks; major events bypass
    shout_gap_real_s: float = 45.0     # one speaker shouts at most this often (a calm scene cannot burst every line)
    thought_enabled: bool = True       # short map thoughts (POST /thought kind "thought" + surface), idle work only
    thought_every_real_s: float = 420.0   # per actor, at most one thought each this many real seconds
    thought_memory_window_days: float = 1.0   # a thought draws on public memories this recent
    thought_max_chars: int = 90
    idle_retry_real_s: float = 60.0    # backoff after a failed idle job
    use_llm_voice_cards: bool = False  # optional pass for the 3 example lines
    repeat_guard: bool = True          # a conversation turn that repeats an earlier line is refused (core/loopguard.py)
    repeat_retries: int = 1            # retries with the reason in the prompt, then the talk ends with no line
    dreams: bool = True                # sleepers get one surreal dream cloud a night (core/dreams.py; also the mod's dreams setting)
    dream_chance: float = 0.5          # per actor per night: chance the night brings a dream
    dreams_per_night: int = 1
    dream_max_tokens: int = 30
    dream_max_age_game: int = 2500     # a dream not ready this long (game units, 1 game hour) after falling asleep is dropped
    inner_monologue: bool = True       # master: aim in the intent pass, inner stream in the Mind, contrast clouds (core/inner.py)
    inner_stream_keep: int = 6         # inner thoughts kept per Mind
    inner_solo_aim: bool = True        # a line with no listener still gets an aim-only call
    inner_cloud: bool = True           # sometimes show the aim as a thought cloud after a line
    inner_cloud_chance: float = 0.35
    inner_cloud_gap_real_s: float = 90.0   # per actor, on the play clock
    inner_aim_fresh_days: float = 0.25     # an aim guides the next line for this many game days
    gossip_chain: bool = True          # retold gossip carries a source chain, decays per hop, merges duplicates; pass-on hint (core/gossip.py)
    gossip_hop_decay: float = 0.8      # confidence factor per retelling beyond the first
    reputation: bool = True            # "people say: ..." per actor in block C, rebuilt by rules (core/gossip.py)
    reputation_show: int = 3           # at most this many relation lines carry a reputation
    lore: bool = True                  # shared lore: shared names coined after a major chronicle entry (core/lore.py)
    lore_max: int = 12                 # lore entries kept; the oldest fade
    repeat_own_recent: int = 5         # the speaker's own last lines (across conversations) kept for the check

    @property
    def actor_slots(self) -> list[int]:
        return list(range(1, self.windows + 1))


_ENV = {
    "HERMESVOICE_LLM_URL": ("llm", "base_url"),
    "HERMESVOICE_LLM_KEY": ("llm", "api_key"),
    "HERMESVOICE_LLM_MODEL": ("llm", "model"),
    "HERMESVOICE_LLM_MODE": ("llm", "mode"),
    "HERMESVOICE_BRIDGE_URL": ("bridge_url",),
    "HERMESVOICE_BRIDGE_TOKEN": ("bridge_token",),
    "HERMESVOICE_WINDOWS": ("windows",),
}


def load_config(path: str | os.PathLike | None = None) -> Config:
    path = path or os.environ.get("HERMESVOICE_CONFIG")
    data: dict = {}
    if path:
        p = Path(path)
        raw = p.read_bytes()
        data = tomllib.loads(raw.decode()) if p.suffix == ".toml" else json.loads(raw)
    for env, keys in _ENV.items():
        if env in os.environ:
            d = data
            for k in keys[:-1]:
                d = d.setdefault(k, {})
            d[keys[-1]] = os.environ[env]
    if "HERMESVOICE_BRIDGE_JSON" in os.environ:
        data["bridge_json"] = os.environ["HERMESVOICE_BRIDGE_JSON"]
    _llm_from_bridge_json(data)
    return Config(**data)


def _llm_from_bridge_json(data: dict) -> None:
    """Fill llm.* from bridge.json's "llm" block when no LLM URL was set by the file or the environment."""
    if data.get("llm", {}).get("base_url") or not data.get("bridge_json"):
        return
    try:
        block = read_bridge_json(Path(data["bridge_json"])).get("llm") or {}
    except (OSError, ValueError):
        return
    if not isinstance(block, dict) or not str(block.get("base_url", "")).strip():
        return
    llm = data.setdefault("llm", {})
    for key in ("base_url", "model", "api_key"):
        val = str(block.get(key, "") or "").strip()
        if val and not llm.get(key):
            llm[key] = val


def read_bridge_json(path: Path) -> dict:
    """bridge.json: {"port", "bind", "token", "pid", "version", "epoch"}."""
    return json.loads(Path(path).read_text())

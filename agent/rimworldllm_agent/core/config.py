"""Agent configuration.

Sources, lowest to highest priority: defaults, a TOML or JSON file (``--config`` or
``$RIMWORLDLLM_CONFIG``), environment variables (``RIMWORLDLLM_*``). When no LLM URL came from the file or the environment, the ``llm`` block of
``bridge.json`` (``base_url``, ``model``, ``api_key``) is used if ``bridge_json`` is known, so the order for the
LLM endpoint is env > file > bridge.json > default.
"""
from __future__ import annotations

import json
import logging
import os
import tomllib
from pathlib import Path

from pydantic import BaseModel, Field


def _windows() -> bool:
    return os.name == "nt"


def default_data_dir() -> Path:
    env = os.environ.get("RIMWORLDLLM_DATA_DIR")
    if env:
        return Path(env)
    if _windows():
        # Windows: %LOCALAPPDATA%\rimworldllm-agent, beside the engine's %LOCALAPPDATA%\EasyEngine
        # (not a Linux-style %USERPROFILE%\.local\share).
        local = os.environ.get("LOCALAPPDATA")
        return Path(local if local else Path.home() / "AppData" / "Local") / "rimworldllm-agent"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "rimworldllm-agent"


LEGACY_DATA_LEAF = "hermesvoice-agent"   # the folder's name before the mod became RimWorldLLM


def migrate_legacy_data_dir(target: Path) -> str:
    """One-time move of the old-named data folder (hermesvoice-agent) to ``target`` when ``target`` is the default
    location and does not exist yet. Never deletes: when both exist both stay (logged). Returns what happened:
    "override", "custom", "none", "both", "moved" or "failed"."""
    log = logging.getLogger("rimworldllm.config")
    if os.environ.get("RIMWORLDLLM_DATA_DIR"):
        return "override"
    target = Path(target)
    if target != default_data_dir():
        return "custom"
    old = target.parent / LEGACY_DATA_LEAF
    if old.is_symlink() or not old.is_dir():
        return "none"
    if target.exists():
        log.info("both %s and the old %s exist; using the new one and leaving the old one alone", target, old)
        return "both"
    try:
        os.rename(old, target)
    except OSError as e:
        log.warning("could not move the old data folder %s to %s: %s", old, target, e)
        return "failed"
    log.info("moved the old data folder %s to %s", old, target)
    return "moved"


class LLMConfig(BaseModel):
    base_url: str = "http://127.0.0.1:8080"
    api_key: str = ""
    model: str = ""
    # "openai": /v1/chat/completions only. "native": llama.cpp /completion with a
    # hand-built Gemma turn format, which gives id_slot control and cache stats.
    mode: str = "openai"
    # Sampler overrides. None (the default) means "let the engine decide": the engine applies the
    # model's own profile (X-Easy-Sampler: rp | structured). Set a value only to force it; the
    # engine's sampler policy may still replace it. Against a non-engine endpoint the agent sends
    # Google's official Gemma 4 values itself, but only when ``model`` names a known Gemma 4 build.
    temperature: float | None = None
    top_k: int | None = None
    top_p: float | None = None
    min_p: float | None = None
    repeat_penalty: float | None = None
    # True: the endpoint is easyEngine (sends X-Easy-Sampler). False: it is not.
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
    config_refresh_real_s: float = 10.0   # re-read the bridge /config (mod settings) this often, real seconds
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
    steward: bool = True               # the vivarium steward (core/steward.py); it stays off against a mod without /vivarium
    steward_min_gap_real_s: float = 20.0   # least real seconds between two steward cycles that are not chat (chat ignores it)
    steward_strategy_path: str = ""    # a strategy playbook file for the steward prompt; env RIMWORLDLLM_STEWARD_STRATEGY wins; "" = the shipped one
    steward_strategy_max_bytes: int = 8192
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
    small_model_patterns: tuple = ("e2b", "e4b")   # model ids containing any of these count as small (core/model_tier.py)
    inner_cloud_small: bool = True     # contrast clouds on a small model (False = off there; the aim is still made and stored)
    art_small: bool = True             # art descriptions on a small model (E4B, starter, E2B fallback): on (keeper 2026-11-26; E4B judged 2.9-3.0 after the creative round)
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
    habits: bool = True                # reflection may evolve a voice-card tic (core/habits.py)
    habit_every_days: float = 3.0      # at most one habit change per this many game days, per actor
    firsts: bool = True                # first-time reactions (core/firsts.py; also the mod's first_cue event toggle)
    firsts_per_day: int = 2            # first-time lines per actor per game day
    bystanders: bool = True            # a bystander's reaction cloud to an argument (core/bystanders.py)
    bystander_gap_real_s: float = 120.0    # per actor, on the play clock
    concerns: bool = True              # ongoing worries per actor (core/concerns.py): born from events and reflection
    concerns_max: int = 2              # open concerns per actor at most
    concern_fade_days: float = 5.0     # an open concern nobody resolved fades after this many game days
    concern_keep_days: float = 1.0     # a resolved or faded concern stays on record this long for the diary
    concern_loved_opinion: int = 30    # opinion of someone at or above this makes their trouble a worry
    concern_payoff: bool = True        # one spoken line when a worry is resolved
    grudges: bool = True               # grudge and make-up loop (core/grudges.py); needs the mod's wants_enabled, effects on
    grudge_avoid_days: float = 0.25    # the avoid phase, in game days (the mod's avoid table has its own hours setting)
    grudge_cooldown_days: float = 0.75 # from the offence to the first seek-out
    grudge_retry_days: float = 0.25    # between seek-out attempts the mod refused or that came to nothing
    grudge_max_attempts: int = 3       # then the grudge fades
    grudge_avoid_floor_real_s: float = 120.0     # real-time floors (play clock, stops while paused): at 3x and 4x a
    grudge_cooldown_floor_real_s: float = 300.0  # grudge would otherwise cycle in a few real minutes. Each phase waits
    grudge_retry_floor_real_s: float = 120.0     # for both its game-time length and this much play time
    grudge_recover_opinion: int = 10   # opinion of the other at or above this counts as recovered (make up, not confront)
    gatherings: bool = True            # group scenes and ceremonies (core/group.py; also the mod's gatherings event toggle)
    meal_talk: bool = True             # meals (3+ eating in one room) may become a group scene
    group_max_speakers: int = 4        # speakers in one group scene (two windows rotate between them)
    group_turns: list[int] = [5, 4, 2, 0]   # meal/party turn cap at 1x, 2x, 3x, 4x (0 = no scene; ceremonies keep at least their lead)
    eulogy_max_tokens: int = 70        # the lead's line at a funeral
    repeat_own_recent: int = 5         # the speaker's own last lines (across conversations) kept for the check
    # Creative-boost experiments (each defaults to today's behaviour; A/B measured, kept only when they help)
    creative_ground: bool = False      # richer data in art / recap / lore / eulogy and vow prompts (core/art.py, recap.py, lore.py, group.py)
    aim_ground: bool = True            # (judge: aims +0.3..+0.6 on E4B) the aim request carries the scene's tension and one subtext kind (core/inner.py)
    creative_plan: bool = False        # a one-line private plan before the piece (core/creative.py)
    creative_best_of: int = 1          # candidates per piece (core/creative.py)
    creative_ground_for: tuple = ("lore", "recap", "eulogy")    # judged better on E4B (art and vows were not); kinds ("art", "lore", "recap", "eulogy", "vow") that get creative_ground; the bool above means all kinds
    creative_plan_for: tuple = ("lore", "eulogy")    # kinds that get creative_plan (judged better on E4B)
    creative_best_of_for: tuple = ("recap",)   # kinds that get best-of creative_best_of_n candidates (recap: once per load)
    creative_best_of_n: int = 4        # candidates for the kinds in creative_best_of_for
    creative_rerank: str = "auto"      # how the best of N is picked: "auto" (rules) or "llm"
    cliche_retry: bool = False         # one retry when the piece holds a stock phrase
    aim_sampler: str = ""              # non-empty: the aim-carrying intent pass asks the engine for this sampler profile

    @property
    def actor_slots(self) -> list[int]:
        return list(range(1, self.windows + 1))


_ENV = {
    "RIMWORLDLLM_LLM_URL": ("llm", "base_url"),
    "RIMWORLDLLM_LLM_KEY": ("llm", "api_key"),
    "RIMWORLDLLM_LLM_MODEL": ("llm", "model"),
    "RIMWORLDLLM_LLM_MODE": ("llm", "mode"),
    "RIMWORLDLLM_BRIDGE_URL": ("bridge_url",),
    "RIMWORLDLLM_BRIDGE_TOKEN": ("bridge_token",),
    "RIMWORLDLLM_WINDOWS": ("windows",),
}


def load_config(path: str | os.PathLike | None = None) -> Config:
    path = path or os.environ.get("RIMWORLDLLM_CONFIG")
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
    if "RIMWORLDLLM_BRIDGE_JSON" in os.environ:
        data["bridge_json"] = os.environ["RIMWORLDLLM_BRIDGE_JSON"]
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

"""The GameAdapter interface: everything game-specific the engine needs.

Core event format (what ``normalize_event`` returns): ``{"seq", "game" (game time), "type", "actors":
[{"id", "role"}], "summary", "severity" (0..1), "witnesses": [ids], "ledger_id", "data"}``.
Game time is whatever clock the adapter supplies (frames, seconds, turns); core only compares and
subtracts it. Characters may appear and vanish; there is no fixed cast. Events may come from a poll
or from an external sensor: core only needs an ``EventSource``.

The core (this package) knows actors, scenes, events and minds in the abstract. A game adapter
(``hermesvoice_agent/games/<game>/``) supplies the vocabulary, the prompt's world rules, how a
snapshot becomes a bio and a voice card, how events rank, and how a scene reads. Core imports
nothing from ``games/``.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol, runtime_checkable

from .lease import Cls
from .minds import FixedBio, VoiceCard
from .prompt import Scene


@dataclass(frozen=True)
class ProtocolNames:
    """Endpoint paths and body field names of the game's bridge. Defaults are the generic ones."""
    actor_path: str = "/actor"          # GET <actor_path>/{id}: the snapshot
    scene_path: str = "/scene"          # GET <scene_path>/{id}
    mind_path: str = "/mind"            # GET/PUT <mind_path>/{id}
    roster_path: str = "/roster"        # GET: the world at a glance (actors, open ledger entries)
    actor_field: str = "actor"          # body field naming the speaker in say/thought and talk requests
    time_field: str = "time"            # envelope field: the game clock (seconds, frames or turns)
    epoch_field: str = "epoch"          # envelope field: changes when the world is reloaded
    expiry_field: str = "expires_at"    # write field: game time after which the write is refused


@dataclass(frozen=True)
class NameInfo:
    short: str                          # how the actor is addressed
    tokens: frozenset[str]              # every word that names them (for the sanitizer)


@runtime_checkable
class GameAdapter(Protocol):
    name: str
    protocol: ProtocolNames
    romantic_intents: frozenset[str]    # intent labels (and tools) that are romantic or sexual: never with a minor
    intent_labels: list[str]            # the closed effect set; the first one means "nothing"
    block_a: str                        # world rules, speech rules, output format, label glosses
    common_words: set[str]              # capitalised words that are never names in this game
    narrator_prompt: str                # system text for the narrator's one-line coverage

    units_per_day: int                  # game-time units in one "day" (memory decay, quotas)
    def units_per_second(self, speed: int) -> float: ...   # game units per real second at a game speed (pause = the last running speed)

    # prompt wording for the idle work (core supplies the facts, the adapter the voice and flavour)
    reflection_system: str              # how to rewrite the evolving bio, wants, relations (bio person and length)
    reflection_request: str
    diary_system: str                   # a short first-person entry from the day's memories
    diary_request: str
    chronicle_system: str               # the narrator's community chronicle paragraph
    chronicle_request: str
    recap_system: str                   # the load-time "previously on" recap (3 lines)
    recap_request: str
    chronicle_summary_system: str       # folding an old entry into the running summary

    def default_bridge_json(self) -> Path: ...
    def is_controlled(self, actor_id: int) -> bool: ...                    # the player's own character: others talk to it, it has no mind here

    # ---- events -------------------------------------------------------------------
    def priority(self, event_type: str) -> Cls: ...
    def normalize_event(self, raw: dict) -> dict: ...                      # wire event -> core event
    def importance(self, event: dict, actor_id: int) -> float: ...         # 0..1 from game numbers
    def is_pinned(self, event: dict, actor_id: int, firsts: list[str]) -> bool: ...
    def first_person(self, event: dict, actor_id: int) -> str: ...
    def conversation_pair(self, event: dict) -> tuple[int, int] | None: ...
    def is_urgent_visual(self, event: dict) -> bool: ...                   # shown as combat/alert in the game's log
    def is_ledger_end(self, event: dict) -> bool: ...
    def is_private(self, event: dict, actor_id: int) -> bool: ...          # a secret for this actor from the start
    def is_major_threat(self, event: dict) -> bool: ...                    # bypasses the narrator's minimum gap
    def chronicle_worthy(self, event: dict) -> bool: ...                   # goes into the day's chronicle notes
    def chronicle_trigger(self, event: dict) -> bool: ...                  # writes a chronicle entry now (death, big fight ends)
    def told_intents(self) -> dict[str, str]: ...
    def about_intents(self) -> set[str]: ...                               # intents that must name a third actor (gossip)                          # intent -> visibility of the told memory

    # ---- roster, snapshots, scenes -----------------------------------------------
    def roster(self, resp: dict) -> list[tuple[int, Any]]: ...             # (id, raw name)
    def roster_ledger(self, resp: dict) -> list[dict]: ...
    def name_info(self, raw_name: Any) -> NameInfo: ...
    def snapshot_name(self, snap: dict) -> Any: ...                        # raw name from a snapshot
    def snapshot_age(self, snap: dict) -> float | None: ...                # biological years, None when unknown
    def snapshot_job(self, snap: dict) -> str: ...
    def relations(self, snap: dict) -> list[tuple[int, int | None]]: ...   # (other id, opinion)
    def scene_present(self, scene: dict) -> list[tuple[int, bool]]: ...    # (id, can hear)
    def build_scene(self, snap: dict, scene: dict, trigger: str, names: dict[int, str],
                    known: set[int], minors: set[int] | None = None) -> Scene: ...

    # ---- minds --------------------------------------------------------------------
    def facts_hash(self, snap: dict) -> str: ...
    def build_fixed_bio(self, snap: dict, names: dict[int, str]) -> FixedBio: ...
    def build_voice_card(self, snap: dict) -> VoiceCard: ...
    def talk_weight(self, snap: dict) -> float: ...                        # chattiness
    def voice_example_prompt(self, bio: str, register_line: str, tics: str) -> str: ...

    # ---- tools --------------------------------------------------------------------
    async def replay(self, path: str, cfg: Any, llm: Any) -> list[dict]: ...

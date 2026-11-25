"""The RimWorld adapter: BRIDGE-API v2 wire names, colonist bios and voices, RimWorld event ranking."""
from __future__ import annotations

import os
from pathlib import Path
from typing import Any

from ...core.adapter import NameInfo, ProtocolNames
from ...core.lease import Cls
from ...core.minds import FixedBio, VoiceCard
from ...core.prompt import Scene
from ...core.sanitize import name_tokens
from . import builders

_HERE = Path(__file__).parent

_EVENT_CLASS = {
    "player_talk": Cls.PLAYER,
    "death": Cls.URGENT, "mental_break": Cls.URGENT, "raid_start": Cls.URGENT,
    "injury": Cls.REACTIVE, "tend": Cls.REACTIVE,
    "social": Cls.SOCIAL,
    "level_up": Cls.AMBIENT, "thought": Cls.AMBIENT,
}
_SUBJECT_ROLES = ("victim", "subject", "target")
_FIRSTS = ("death", "mental_break", "raid_start", "quest", "funeral", "lodger_arrive")


class RimWorldAdapter:
    name = "rimworld"
    protocol = ProtocolNames(actor_path="/pawn", scene_path="/scene", mind_path="/mind", roster_path="/colony",
                             actor_field="pawn", time_field="tick", epoch_field="save_epoch",
                             expiry_field="expires_tick")
    units_per_day = 60000                      # ticks
    intent_labels = ["none", "comfort", "insult", "kind", "confide", "gossip", "threaten", "flirt", "joke", "argue"]
    common_words = {"colony", "colonist", "colonists", "rimworld", "pirates", "tribals", "mechanoids"}
    narrator_prompt = ("You are a dry narrator of a RimWorld colony. Write one short third-person line "
                       "about what just happened. No speech, no invention.")

    def __init__(self) -> None:
        self.block_a = (_HERE / "block_a.txt").read_text()
        self.intent_labels = self._labels_from_block_a() or self.intent_labels

    def _labels_from_block_a(self) -> list[str]:
        return []

    def default_bridge_json(self) -> Path:
        home = Path(os.environ.get("HOME", "~")).expanduser()
        return home / ".config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/HermesVoice/bridge.json"

    def is_controlled(self, actor_id: int) -> bool:
        return False                            # RimWorld has no single player character

    # ---- events -------------------------------------------------------------------
    def priority(self, event_type: str) -> Cls:
        return _EVENT_CLASS.get(event_type, Cls.AMBIENT)

    def normalize_event(self, raw: dict) -> dict:
        e = dict(raw)
        e["game"] = int(e.pop("tick", e.get("game", 0)))
        e["actors"] = [{"id": int(p["id"]), "role": p.get("role", "")} for p in e.pop("pawns", e.get("actors", []))]
        return e

    def importance(self, event: dict, actor_id: int) -> float:
        sev = float(event.get("severity", 0.0))
        data = event.get("data") or {}
        imp = sev
        if event.get("type") == "death" or data.get("died"):
            imp = 1.0
        if "mood_offset" in data:
            imp = max(imp, min(1.0, abs(float(data["mood_offset"])) / 20.0))
        if data.get("opinion") is not None:
            imp += min(0.3, abs(float(data["opinion"])) / 100.0 * 0.3)
        for a in event.get("actors", []):
            if int(a["id"]) == actor_id and a.get("role") in _SUBJECT_ROLES:
                imp += 0.15
        return max(0.0, min(1.0, imp))

    def is_pinned(self, event: dict, actor_id: int, firsts: list[str]) -> bool:
        data = event.get("data") or {}
        if event.get("type") == "death" or data.get("died") or data.get("betrayal"):
            return True
        if float(event.get("severity", 0)) >= 0.9:
            return True
        return event.get("type", "") not in firsts and event.get("type") in _FIRSTS

    def first_person(self, event: dict, actor_id: int) -> str:
        summary = event.get("summary", "").strip()
        roles = {int(a["id"]): a.get("role", "") for a in event.get("actors", [])}
        if roles.get(actor_id) in _SUBJECT_ROLES:
            return f"It happened to me: {summary}"
        if actor_id in roles:
            return f"I was part of it: {summary}"
        return f"I saw it: {summary}"

    def conversation_pair(self, event: dict) -> tuple[int, int] | None:
        if event.get("type") != "social":
            return None
        ids = [int(a["id"]) for a in event.get("actors", [])]
        return (ids[0], ids[1]) if len(ids) >= 2 else None

    def is_urgent_visual(self, event: dict) -> bool:
        return event.get("type") in ("injury", "death", "raid_start", "mental_break")

    def is_ledger_end(self, event: dict) -> bool:
        return event.get("type", "").endswith("_end")

    def told_intents(self) -> dict[str, str]:
        return {"confide": "private", "gossip": "public"}

    # ---- roster, snapshots, scenes ------------------------------------------------
    def roster(self, resp: dict) -> list[tuple[int, Any]]:
        return [(int(c["id"]), c.get("name")) for c in resp.get("colonists", [])]

    def roster_ledger(self, resp: dict) -> list[dict]:
        return list(resp.get("ledger", []))

    def name_info(self, raw_name: Any) -> NameInfo:
        p = {"name": raw_name}
        return NameInfo(builders.short_name(p), frozenset(name_tokens(builders.display_name(p))))

    def snapshot_name(self, snap: dict) -> Any:
        return snap.get("name")

    def snapshot_job(self, snap: dict) -> str:
        return snap.get("job") or ""

    def relations(self, snap: dict) -> list[tuple[int, int | None]]:
        return [(r["pawn"], r.get("opinion")) for r in snap.get("relations", []) if isinstance(r.get("pawn"), int)]

    def scene_present(self, scene: dict) -> list[tuple[int, bool]]:
        return [(int(q["pawn"]), bool(q.get("can_hear", True))) for q in scene.get("present", []) if "pawn" in q]

    def build_scene(self, snap: dict, scene: dict, trigger: str, names: dict[int, str], known: set[int]) -> Scene:
        room = scene.get("room") or {}
        where = room.get("role", "") or "outdoors"
        extras = [f"{k} {room[k]}" for k in ("clean", "temp") if k in room]
        if extras:
            where += " (" + ", ".join(str(x) for x in extras) + ")"
        when = ", ".join(str(x) for x in (scene.get("time_of_day"), scene.get("weather")) if x)
        present = []
        for q in scene.get("present", []):
            pid = q.get("pawn")
            if pid is None or pid not in known:
                continue
            n = names.get(pid)
            if n:
                present.append(f"{n} ({q.get('job') or 'idle'})")
        mood = snap.get("mood") or {}
        needs = snap.get("needs") or {}
        h = snap.get("health") or {}
        st = []
        if needs.get("mood") is not None:
            st.append(f"mood {round(float(needs['mood']) * 100) if float(needs['mood']) <= 1 else needs['mood']}%")
        if h.get("downed"):
            st.append("downed")
        if h.get("pain", 0) and h["pain"] > 0.3:
            st.append("in pain")
        for k, v in (scene.get("trends") or {}).items():
            if isinstance(v, list) and len(v) >= 2:
                st.append(f"{k.replace('_', ' ')} {v[0]} -> {v[-1]}")
        ths = [f"{t.get('label')} ({round(t.get('offset', 0)):+d})" for t in mood.get("thoughts", [])[:4]]
        return Scene(where=where, when=when or "daytime", present=present, state=", ".join(st) or "ordinary",
                     job=snap.get("job") or "idle", thoughts=ths, trigger=trigger)

    # ---- minds --------------------------------------------------------------------
    def facts_hash(self, snap: dict) -> str:
        return builders.facts_hash(snap)

    def build_fixed_bio(self, snap: dict, names: dict[int, str]) -> FixedBio:
        return builders.build_fixed_bio(snap, names)

    def build_voice_card(self, snap: dict) -> VoiceCard:
        return builders.build_voice_card(snap)

    def talk_weight(self, snap: dict) -> float:
        return builders.talk_weight(snap)

    def voice_example_prompt(self, bio: str, register_line: str, tics: str) -> str:
        return builders.VOICE_PROMPT.format(bio=bio, register=register_line, tics=tics)

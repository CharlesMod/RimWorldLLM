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
# What the game's own interactions are, in words the line prompt can use, so the line fits (an insult reads as an insult).
_INTERACTION_KIND = {
    "Insult": "an insult", "Slight": "a cutting remark that slights them", "KindWords": "kind words",
    "Chitchat": "idle small talk", "DeepTalk": "a deep, personal conversation",
    "RomanceAttempt": "a romantic advance", "MarriageProposal": "a marriage proposal", "Breakup": "ending their relationship",
    "ConvertIdeoAttempt": "an attempt to win them over to a belief", "Reassure": "reassurance", "Counsel": "counsel",
}
_SUBJECT_ROLES = ("victim", "subject", "target")
_FIRSTS = ("death", "mental_break", "raid_start", "quest", "funeral", "lodger_arrive")


class RimWorldAdapter:
    name = "rimworld"
    protocol = ProtocolNames(actor_path="/pawn", scene_path="/scene", mind_path="/mind", roster_path="/colony",
                             actor_field="pawn", time_field="tick", epoch_field="save_epoch",
                             expiry_field="expires_tick")
    units_per_day = 60000                      # ticks
    # game ticks per real second at each game speed: 1 = 60, 2 = 180, 3 = 360, dev speed 4 = 900 (speed 0 is a pause)
    _TICKS_PER_S = {0: 60.0, 1: 60.0, 2: 180.0, 3: 360.0, 4: 900.0}

    def units_per_second(self, speed: int) -> float:
        return self._TICKS_PER_S.get(int(speed), 60.0 if speed < 1 else 900.0)
    intent_labels = ["none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain"]
    common_words = {"colony", "colonist", "colonists", "rimworld", "pirates", "tribals", "mechanoids"}
    narrator_prompt = ("You are a dry narrator of a RimWorld colony. Write one short third-person line "
                       "about what just happened. No speech, no invention.")

    reflection_system = (
        "You are the quiet part of one RimWorld colonist that looks back over recent days and decides who they "
        "are becoming. Write in the first person, plain and dry, the way the colonist thinks, never the way a "
        "writer would. Stay with what the memories say. Never invent a person, place or event, and name nobody "
        "who is not in the lists below.")
    reflection_request = (
        "Reply with JSON only. bio: who you are becoming, 2 to 4 short first-person sentences (under 70 words), "
        "changed only by what the memories show. wants: 1 to 3 short aims you hold now (for example resent X, win "
        "Y's respect, get off kitchen duty). relations: for people who mattered, who (first name as listed), "
        "feeling (one short line) and history (one short line). merge: groups of memory ids (two or more) that "
        "say nearly the same thing, with one first-person summary line each; never include a [pinned] memory.")
    diary_system = (
        "You are one RimWorld colonist writing a short private diary entry at the end of the day. First person, "
        "plain, dry, blunt; the colonist's own register. No moralising, no flowery language.")
    diary_request = (
        "Write today's diary entry: 2 to 4 sentences, under 70 words, one paragraph, only about what is listed "
        "under what happened today. Do not name anyone who is not mentioned there.")
    chronicle_system = (
        "You are the dry narrator of a RimWorld colony, keeping its chronicle. Third person, past tense, plain "
        "words, a little gallows humour at most. You describe only what the notes say happened. You never "
        "propose, predict or cause events, never invent people, places or numbers, and never address the player.")
    chronicle_request = (
        "Write the chronicle paragraph for this day: 3 to 5 sentences, under 110 words, one paragraph. Use the "
        "story so far for continuity only; do not repeat it.")
    chronicle_summary_system = (
        "You keep the running summary of a RimWorld colony's chronicle. Third person, past tense, compact. "
        "Keep deaths, raids, quarrels and anything still unresolved; let small things go.")

    def __init__(self) -> None:
        self.block_a = (_HERE / "block_a.txt").read_text()

    async def replay(self, path: str, cfg: Any, llm: Any) -> list[dict]:
        from .replay import replay
        return await replay(path, cfg, llm)

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

    def vanilla_social(self, event: dict) -> dict | None:
        """The game's own interaction between two actors, when the mod asked for words for it: reference, pair,
        priority tier ("high" or "idle"), the game's own sentence and what kind of thing it was."""
        data = event.get("data") or {}
        if event.get("type") != "social" or not data.get("vanilla") or not data.get("ref"):
            return None
        pair = self.conversation_pair(event)
        if pair is None:
            return None
        key = str(data.get("interaction", ""))
        return {"ref": str(data["ref"]), "initiator": pair[0], "recipient": pair[1],
                "tier": "high" if data.get("tier") == "high" else "idle",
                "window_ticks": max(1, int(data.get("window_ticks") or 417)),
                "sentence": str(data.get("vanilla_text") or event.get("summary", "")).strip(),
                "kind": _INTERACTION_KIND.get(key) or str(data.get("interaction_label") or "a social moment")}

    def vanilla_request(self, info: dict, speaker: str, listener: str) -> str:
        """The line request for a vanilla interaction: what the game wrote, what kind of thing it was, the same subject."""
        s = f' The game\'s own log reads: "{info["sentence"]}".' if info["sentence"] else ""
        return ("=== REQUEST ===\n"
                f"{speaker} has just done something to {listener}: it is {info['kind']}.{s}\n"
                f"Write {speaker}'s one line of speech to {listener} that goes with it. It must read as {info['kind']} "
                "and stay on the same subject the log names. One line only.")

    def is_urgent_visual(self, event: dict) -> bool:
        return event.get("type") in ("injury", "death", "raid_start", "mental_break")

    def is_ledger_end(self, event: dict) -> bool:
        return event.get("type", "").endswith("_end")

    def is_private(self, event: dict, actor_id: int) -> bool:
        data = event.get("data") or {}
        if data.get("secret"):
            return True
        if data.get("betrayal"):
            return any(int(a["id"]) == actor_id and a.get("role") in ("perpetrator", "attacker", "speaker")
                       for a in event.get("actors", []))
        return False

    def is_major_threat(self, event: dict) -> bool:
        return event.get("type") in ("death", "raid_start", "mental_break") or float(event.get("severity", 0)) >= 0.9

    def chronicle_worthy(self, event: dict) -> bool:
        t = event.get("type")
        if t in ("death", "raid_start", "raid_end", "mental_break", "quest", "funeral", "lodger_arrive",
                 "prisoner", "wedding"):
            return True
        return float(event.get("severity", 0)) >= 0.5 and t not in ("tend", "thought", "player_talk")

    def chronicle_trigger(self, event: dict) -> bool:
        return event.get("type") in ("death", "raid_end")

    def about_intents(self) -> set[str]:
        return {"gossip"}

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

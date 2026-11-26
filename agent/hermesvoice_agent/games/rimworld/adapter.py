"""The RimWorld adapter: BRIDGE-API v2 wire names, colonist bios and voices, RimWorld event ranking."""
from __future__ import annotations

import os
from pathlib import Path
from typing import Any

from ...core import agegate
from ...core.adapter import NameInfo, ProtocolNames
from ...core.lease import Cls
from ...core.minds import FixedBio, VoiceCard
from ...core.prompt import Scene
from ...core.sanitize import name_tokens
from . import builders, concern_rules, register

_HERE = Path(__file__).parent

_EVENT_CLASS = {
    "player_talk": Cls.PLAYER,
    "death": Cls.URGENT, "mental_break": Cls.URGENT, "raid_start": Cls.URGENT,
    "injury": Cls.REACTIVE, "tend": Cls.REACTIVE,
    "social": Cls.SOCIAL,
    "level_up": Cls.AMBIENT, "thought": Cls.AMBIENT,
    "first_cue": Cls.REACTIVE,
}
# First-time reactions (core/firsts.py): what a pawn may react to once, and the words for "the first time you have ever ...".
_FIRST_CUES = {
    "snow": "seen snow fall", "eclipse": "seen an eclipse", "aurora": "seen an aurora", "solar_flare": "seen a solar flare",
    "corpse": "seen a dead body", "beautiful_room": "stood in a room this beautiful", "no_table": "had to eat with no table",
}
# vanilla interactions that are an argument a bystander may react to (core/bystanders.py)
ARGUMENT_INTERACTIONS = frozenset({"Insult", "Slight"})
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
    # intents and vanilla interactions that are romantic: refused whenever either party is a minor (core/agegate.py)
    romantic_intents = frozenset({"flirt", "romance", "romantic", "seduce", "woo", "propose", "lovin"})
    romantic_interactions = frozenset({"RomanceAttempt", "MarriageProposal", "Breakup"})
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
    reflection_habit_request = (
        "Also reply with habit: one small new verbal habit you have picked up from what the memories show (a few "
        "words, for example 'trails off after a quarrel', 'mutters about the cold'), or an empty string if nothing "
        "in the memories earns one. Never name anyone in it.")
    diary_system = (
        "You are one RimWorld colonist writing a short private diary entry at the end of the day. First person, "
        "plain, dry, blunt; the colonist's own register. No moralising, no flowery language.")
    diary_request = (
        "Write today's diary entry: 2 to 4 sentences, under 70 words, one paragraph, only about what is listed "
        "under what happened today. Do not name anyone who is not mentioned there.")
    thought_system = (
        "You are the passing mind of one RimWorld colonist: the single small thought that crosses their head "
        "while they work. First person, plain, dry, the colonist's own register. Never narrate, never advise.")
    thought_request = (
        "Write the one thought crossing your mind now: one short sentence, under 14 words, no quotes, only about "
        "something listed under what is on your mind. Do not name anyone who is not mentioned there.")
    dream_system = (
        "You are the sleeping mind of one RimWorld colonist: one strange dream fragment, as it is dreamt. First person, "
        "present tense, plain words, surreal but small. Nothing sexual, nothing gory, never about anyone not listed.")
    dream_request = (
        "Write one surreal dream fragment, under 14 words, no quotes, bending two or three things listed under what "
        "happened today or what you were thinking. Never romantic or sexual.")
    reaction_system = (
        "You are one RimWorld colonist watching two people argue. You do not take part. Give the tiny reaction that "
        "shows on your face or under your breath: first person, in your own register, never a speech, never advice.")
    reaction_request = (
        "Write your reaction: 2 to 6 words, plain, no quotes, naming nobody (for example: Not again. / Ha. About time.).")
    bystander_templates = ("Not again.", "Here we go.", "Oh, come on.", "Ha. About time.", "Keep me out of it.", "Ugh. Those two.")
    argument_intents = frozenset({"insult", "slight"})
    chronicle_system = (
        "You are the dry narrator of a RimWorld colony, keeping its chronicle. Third person, past tense, plain "
        "words, a little gallows humour at most. You describe only what the notes say happened. You never "
        "propose, predict or cause events, never invent people, places or numbers, and never address the player.")
    chronicle_request = (
        "Write the chronicle paragraph for this day: 3 to 5 sentences, under 110 words, one paragraph. Use the "
        "story so far for continuity only; do not repeat it.")
    recap_system = (
        "You write the 'Previously on...' recap that greets the player when a saved RimWorld colony is loaded, like the "
        "start of a TV episode. Third person, past tense, plain words, a little dry. Use only what the chronicle entries "
        "say; never invent people, places or numbers, never propose or predict events, never address the player.")
    recap_request = (
        "Write exactly 3 lines, one short sentence each, under 20 words, most recent last, the last line ending on "
        "what is still unresolved. Tell the biggest events (deaths, raids, wounds, thefts, weddings, feuds), never small "
        "talk or chores. No title, no numbering, no quotes.")
    lore_system = (
        "You coin the colony's shared nicknames for the places and events its people talk about, like 'the Long Winter' "
        "or 'the east barracks'. Use only what the chronicle entry says; never invent people, places or numbers. "
        "Nothing romantic or sexual, ever.")
    lore_label = "Colony names"
    lore_request = (
        "If the entry holds an event or place the colonists would plausibly keep a nickname for, answer with one line: "
        "a 2 to 5 word name the colonists would actually say (a nickname, never a label such as an incident or event "
        "name), a colon, then what it refers to in under 10 words (for example: The Long Winter: the "
        "months the colony ran short of food). Do not reuse a name already in use. Otherwise answer: none.")
    art_system = (
        "You write the short description on the info card of a work of art made in a RimWorld colony. Plain words, "
        "third person. Say what the piece depicts: one striking moment from the colony's history or the maker's "
        "memories (a death, a raid, a wound, a feast, a quarrel), shown as a scene, in the present tense, with the people "
        "and the place the data names. Never small talk or chores. At most one short sentence on the maker or the "
        "craft. Do not list memories, and never mention data, records, memories or what the maker recalled. Never "
        "invent people, places, events or numbers. No flowery prose, no \"a testament to\", no moral, no addressing "
        "the player. Nothing romantic or sexual about anyone, ever.")
    art_request = (
        "Write the description: 2 or 3 plain sentences, under 55 words, one paragraph. If the history gives nothing "
        "worth depicting, describe the piece's subject and its maker plainly.")
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
        if event.get("type") == "first_cue":
            return True                       # written only for a pawn's first: never folded away
        return event.get("type", "") not in firsts and event.get("type") in _FIRSTS

    def first_person(self, event: dict, actor_id: int) -> str:
        summary = event.get("summary", "").strip()
        roles = {int(a["id"]): a.get("role", "") for a in event.get("actors", [])}
        if roles.get(actor_id) in _SUBJECT_ROLES:
            return f"It happened to me: {summary}"
        if actor_id in roles:
            return f"I was part of it: {summary}"
        return f"I saw it: {summary}"

    def first_of(self, event: dict) -> tuple[str, str] | None:
        """(key, phrase) when this event is a first-time cue: the mod's first_cue events, a first raid, a first colonist death."""
        t, data = event.get("type"), event.get("data") or {}
        if t == "first_cue":
            c = str(data.get("cue", ""))
            return (c, _FIRST_CUES[c]) if c in _FIRST_CUES else None
        if t == "raid_start":
            return "raid", "seen a raid"
        if t == "death" and not data.get("animal"):
            return "colonist_death", "seen a colonist die"
        return None

    def first_trigger(self, phrase: str, summary: str) -> str:
        return f"The first time you have ever {phrase}: {summary}".strip()

    def argument_parties(self, event: dict) -> list[int] | None:
        """The two actors of a vanilla insult or slight, else None (core/bystanders.py)."""
        data = event.get("data") or {}
        if event.get("type") != "social" or str(data.get("interaction", "")) not in ARGUMENT_INTERACTIONS:
            return None
        pair = self.conversation_pair(event)
        return list(pair) if pair else None

    def gather_info(self, event: dict) -> dict | None:
        """A "gathering" event as plain data for core/group.py: kind, key, lead, couple, subject, participants and the
        game's own words (label, ideo, deceased), or None when it is not a usable gathering."""
        if event.get("type") != "gathering":
            return None
        data = event.get("data") or {}
        kind = str(data.get("kind", ""))
        if kind not in ("meal", "party", "wedding", "funeral", "speech", "ritual"):
            return None
        roles = [(int(a["id"]), a.get("role", "")) for a in event.get("actors", [])]
        subject = next((i for i, r in roles if r == "subject"), None)
        ids = [i for i, r in roles if r != "subject"]
        lead = data.get("lead")
        lead = int(lead) if isinstance(lead, int) or str(lead).isdigit() else next((i for i, r in roles if r == "lead"), None)
        couple = [int(x) for x in data.get("couple") or []] or [i for i, r in roles if r == "couple"]
        return {"kind": kind, "key": str(data.get("key") or event.get("seq")), "lead": lead, "couple": couple[:2],
                "subject": subject, "participants": ids, "label": str(data.get("label", "")), "ideo": str(data.get("ideo", "")),
                "room": str(data.get("room", "")), "deceased": str(data.get("deceased_name", ""))}

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
                "window_ticks": max(1, int(data.get("window_ticks") or 417)), "window_real_s": float(data.get("window_real_s") or 0.0),
                "sentence": str(data.get("vanilla_text") or event.get("summary", "")).strip(),
                "romantic": key in self.romantic_interactions,
                "kind": _INTERACTION_KIND.get(key) or str(data.get("interaction_label") or "a social moment")}

    def vanilla_request(self, info: dict, speaker: str, listener: str) -> str:
        """The line request for a vanilla interaction: what the game wrote, what kind of thing it was, the same subject."""
        s = f' The game\'s own log reads: "{info["sentence"]}".' if info["sentence"] else ""
        return ("=== REQUEST ===\n"
                f"{speaker} has just done something to {listener}: it is {info['kind']}.{s}\n"
                f"Write {speaker}'s one line of speech to {listener} that goes with it. It must read as {info['kind']} "
                "and stay on the same subject the log names. One line only.")

    # ---- ongoing concerns and grudges (core/concerns.py, core/grudges.py) ----------
    def concern_births(self, event: dict, holders: list[int], loved) -> list[dict]:
        return concern_rules.births(event, holders, loved)

    def concern_resolutions(self, event: dict) -> list[tuple[str, str]]:
        return concern_rules.resolutions(event, self.is_ledger_end)

    def social_offence(self, event: dict) -> tuple[int, int, str] | None:
        return concern_rules.social_offence(event)

    def offence_intents(self) -> set[str]:
        return {"insult"}

    def grudge_style(self, snap: dict) -> str:
        return concern_rules.style(snap)

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
        return float(event.get("severity", 0)) >= 0.5 and t not in ("tend", "thought", "player_talk", "gathering", "gathering_end")

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

    def snapshot_age(self, snap: dict) -> float | None:
        """Biological age in years, from the pawn snapshot (the mod sends AgeBiologicalYears); None when absent."""
        a = snap.get("age")
        return float(a) if isinstance(a, (int, float)) and not isinstance(a, bool) else None

    def snapshot_job(self, snap: dict) -> str:
        return snap.get("job") or ""

    def relations(self, snap: dict) -> list[tuple[int, int | None]]:
        return [(r["pawn"], r.get("opinion")) for r in snap.get("relations", []) if isinstance(r.get("pawn"), int)]

    def scene_present(self, scene: dict) -> list[tuple[int, bool]]:
        return [(int(q["pawn"]), bool(q.get("can_hear", True))) for q in scene.get("present", []) if "pawn" in q]

    def build_scene(self, snap: dict, scene: dict, trigger: str, names: dict[int, str], known: set[int],
                    minors: set[int] | None = None) -> Scene:
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
                tag = "minor, " if pid in (minors or ()) else ""
                present.append(f"{n} ({tag}{q.get('job') or 'idle'})")
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
        now, short = register.speech_now(snap)
        return Scene(where=where, when=when or "daytime", present=present, state=", ".join(st) or "ordinary",
                     speech_now=now, speech_short=short,
                     job=snap.get("job") or "idle", thoughts=ths, trigger=trigger,
                     minor_self=agegate.is_minor(self.snapshot_age(snap)))

    # ---- minds --------------------------------------------------------------------
    def facts_hash(self, snap: dict) -> str:
        return builders.facts_hash(snap)

    def build_fixed_bio(self, snap: dict, names: dict[int, str]) -> FixedBio:
        return builders.build_fixed_bio(snap, names)

    def build_voice_card(self, snap: dict) -> VoiceCard:
        return builders.build_voice_card(snap)

    def speech_register(self, snap: dict) -> str | None:
        """The effective register for the inline prompts (thought, diary, dream, bystander); a temporary state is named,
        so a drunk pawn's thought reads drunk and not merely simple."""
        states = register.temp_states(snap)
        words = register.effective_words(snap)
        return f"right now {'; '.join(t for t, _ in states)}, so {words}" if states else words

    def talk_weight(self, snap: dict) -> float:
        return builders.talk_weight(snap)

    def voice_example_prompt(self, bio: str, register_line: str, tics: str) -> str:
        return builders.VOICE_PROMPT.format(bio=bio, register=register_line, tics=tics)

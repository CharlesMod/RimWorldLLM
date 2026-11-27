"""The RimWorld side of the steward (core/steward.py): which events wake it, its prompt files, the digest renderer,
and the game tools. Each game tool maps to one ``POST /act/<verb>`` of the bridge (docs/BRIDGE-API.md, "Vivarium
routes"); names from the model become ids through the digest, and anything unknown comes back as a tool error the
model reads.

Core calls ``StewardKit.session(digest, bridge, advisor)`` once per cycle and gets a ``Session``: the tools for this
digest (enums built from the colonists, beds, letters and policies in it), the set of acting tools, and ``run``.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from ...core import gamedata as gd
from ...core.bridge import BridgeError
from ...core.toolloop import Param, ToolSpec
from .steward_digest import pawn_names, render_digest
from .steward_rules import alerts_section

_HERE = Path(__file__).parent

# events that wake the steward at once while the narrator plays
_WAKE_TYPES = ("raid_start", "mental_break", "death")
_WAKE_ALERT_WORDS = ("fire", "starvation", "rescue")        # beside any alert the game marks critical
# events never listed in "since last time": chatter the colonists have among themselves
_QUIET_TYPES = frozenset({"social", "thought", "level_up", "first_cue", "gathering", "gathering_end", "player_talk",
                          "art_created", "sleep_start", "tend"})

WORK_FALLBACK = ["Firefighter", "Patient", "Doctor", "Cooking", "Hunting", "Construction", "Growing", "Mining",
                 "PlantCutting", "Smithing", "Tailoring", "Art", "Crafting", "Hauling", "Cleaning", "Research"]
ACTING = ("set_time", "set_work", "set_schedule", "set_policy", "assign_bed", "set_research", "answer_letter", "draft", "order")

ADVISOR_NOTE = (
    "\n\nADVISOR MODE. The narrator is not playing right now: the player is only chatting with you. Act only on what the "
    "player's message explicitly asks for, and do at most one task (one action tool call); then say plainly what you did. "
    "If they ask for something you cannot do yet (building, zones, bills, trade, caravans), say you cannot yet. If they only "
    "ask or talk, just answer. A question (\"should we take the quest?\", \"what do you think about...?\") is not a request: "
    "answer it with your advice and do not act. Act only on a plain instruction such as \"put Ana on cooking\". "
    "Do not change the goals board except to record a standing order they ask for.")


@dataclass
class Outcome:
    """What a game tool did: the dict the model reads back, and for a success the verb and the bridge's short note."""
    result: dict
    verb: str = ""
    note: str = ""

    @property
    def ok(self) -> bool:
        return "done" in self.result


def _ci(options, wanted: str) -> str | None:
    w = wanted.strip().lower()
    for o in options:
        if str(o).lower() == w:
            return str(o)
    return None


class Session:
    """One cycle's game tools, built from one digest."""

    def __init__(self, digest: dict, bridge, advisor: bool):
        self.d, self.bridge, self.advisor = digest, bridge, advisor
        self.pawns = pawn_names(digest)                      # cleaned name -> id
        self.roles = {gd.name(p.get("name")): p.get("role", "colonist") for p in digest.get("pawns") or []}
        self.beds = {str(b["id"]): b for b in digest.get("beds") or [] if isinstance(b, dict) and b.get("id")}
        self.letters = {str(l["id"]): l for l in digest.get("letters") or [] if isinstance(l, dict) and l.get("id")}
        self.work = [str(w) for w in digest.get("work_types") or []] or WORK_FALLBACK
        self.policies = {k: [str(x) for x in v] for k, v in (digest.get("policies") or {}).items() if isinstance(v, list)}
        self.research = [r for r in (digest.get("research") or {}).get("available") or [] if isinstance(r, dict)]
        self.tools = self._tools()
        self.acting = {t.name for t in self.tools if t.name in ACTING}

    # ---- the tool list for this digest ----------------------------------------------
    def _tools(self) -> list[ToolSpec]:
        names = list(self.pawns)
        out: list[ToolSpec] = [ToolSpec(
            "set_time", "Pause or unpause the game, or set its speed. Only what the settings allow; pausing is for real emergencies.",
            [Param("pause", "enum", "pause the game, unpause it, or leave it", choices=["leave", "yes", "no"]),
             Param("speed", "enum", "game speed 1 to 4, or leave", choices=["leave", "1", "2", "3", "4"]),
             Param("emergency", "enum", "yes only for a real emergency pause", choices=["no", "yes"]),
             Param("reason", "text", "why, a few words", max_len=120)])]
        if names:
            pawn = Param("pawn", "enum", "which colonist, by name", choices=names)
            out += [
                ToolSpec("set_work", "Set one colonist's priority for one kind of work: 1 is done first, 4 last, 0 is off.",
                         [pawn, Param("work", "enum", "work type", choices=self.work), Param("priority", "int", "0 off, 1 highest, 4 lowest", lo=0, hi=4)]),
                ToolSpec("set_schedule", "Set one colonist's schedule for the hours from_hour up to to_hour (0 to 24).",
                         [pawn, Param("from_hour", "int", "first hour, 0 to 23", lo=0, hi=23), Param("to_hour", "int", "end hour, 0 to 24", lo=0, hi=24),
                          Param("assignment", "enum", "what they do in those hours", choices=["anything", "work", "joy", "sleep", "meditate"])]),
                ToolSpec("draft", "Draft or undraft colonists. pawns is a comma-separated list of names, or all.",
                         [Param("pawns", "text", "names separated by commas, or all", max_len=160), Param("drafted", "enum", "draft or undraft", choices=["yes", "no"])]),
                ToolSpec("order", "Give one drafted colonist an order: move to a cell, attack a target (by id), or hold position.",
                         [pawn, Param("order", "enum", "the order", choices=["move", "attack", "hold"]),
                          Param("x", "int", "move: cell x (0 if unused)", lo=0, hi=999), Param("z", "int", "move: cell z (0 if unused)", lo=0, hi=999),
                          Param("target_id", "int", "attack: the target's id (0 if unused)", lo=0, hi=999999),
                          Param("hold", "enum", "hold: yes to hold, no to stop holding", choices=["yes", "no"])])]
            if any(self.policies.get(k) for k in ("outfit", "food", "drug")):
                out.append(ToolSpec("set_policy", "Give one colonist an outfit, food or drug policy by name (see POLICIES).",
                                    [pawn, Param("kind", "enum", "which policy", choices=["outfit", "food", "drug"]), Param("name", "text", "the policy's name", max_len=60)]))
            if self.beds:
                out.append(ToolSpec("assign_bed", "Assign one colonist to a bed (by the bed's id in BEDS).",
                                    [pawn, Param("bed_id", "enum", "the bed's id", choices=list(self.beds))]))
        out.append(ToolSpec("set_research", "Choose the research project to work on, by name (see RESEARCH).",
                            [Param("project", "text", "the project's name", max_len=80)]))
        if self.letters:
            out.append(ToolSpec("answer_letter", "Answer a letter that needs a choice (an offer, a quest) with one of its choices, or dismiss it.",
                                [Param("letter_id", "enum", "the letter's id", choices=list(self.letters)),
                                 Param("choice", "text", "one of the letter's choices, or dismiss", max_len=80)]))
        return out

    # ---- running a tool ---------------------------------------------------------------
    def validate(self, tool: str, values: dict) -> str | None:
        return None

    async def run(self, tool: str, v: dict) -> Outcome:
        try:
            verb, body = self._body(tool, v)
        except ValueError as e:
            return Outcome({"error": str(e)})
        try:
            r = await self.bridge.act(verb, body)
        except BridgeError as e:
            if e.code == "refused":
                return Outcome({"refused": e.detail or "the game refused it"})
            return Outcome({"error": f"{e.code}: {e.detail}".strip(": ")[:160]})
        note = str(r.get("done", ""))
        extra = {k: x for k, x in r.items() if k not in ("ok", "tick", "save_epoch", "paused", "speed", "done")}
        return Outcome({"done": note, **extra}, verb, note)

    def _pawn(self, name: str) -> int:
        if name not in self.pawns:
            raise ValueError(f"no colonist named {name}; the colonists are: " + ", ".join(self.pawns))
        return self.pawns[name]

    def _body(self, tool: str, v: dict) -> tuple[str, dict]:
        if tool == "set_time":
            b: dict = {}
            if v["pause"] != "leave":
                b["pause"] = v["pause"] == "yes"
            if v["speed"] != "leave":
                b["speed"] = int(v["speed"])
            if not b:
                raise ValueError("nothing to change: set pause or speed")
            b["emergency"] = v["emergency"] == "yes"
            b["reason"] = v["reason"]
            return "time", b
        if tool == "set_work":
            return "work", {"pawn": self._pawn(v["pawn"]), "priorities": {v["work"]: v["priority"]}}
        if tool == "set_schedule":
            if v["to_hour"] == v["from_hour"]:
                raise ValueError("from_hour and to_hour are the same")
            return "schedule", {"pawn": self._pawn(v["pawn"]), "from_hour": v["from_hour"], "to_hour": v["to_hour"], "assignment": v["assignment"]}
        if tool == "set_policy":
            opts = self.policies.get(v["kind"]) or []
            name = _ci(opts, v["name"])
            if name is None:
                raise ValueError(f"no {v['kind']} policy named {v['name']}; the {v['kind']} policies are: " + ", ".join(opts))
            return "policy", {"pawn": self._pawn(v["pawn"]), "kind": v["kind"], "name": name}
        if tool == "assign_bed":
            if v["bed_id"] not in self.beds:
                raise ValueError("no such bed; the beds are: " + ", ".join(self.beds))
            return "bed", {"pawn": self._pawn(v["pawn"]), "bed": int(v["bed_id"])}
        if tool == "set_research":
            labels = {}
            for r in self.research:
                labels[str(r.get("label", "")).lower()] = str(r.get("def") or r.get("label"))
                labels[str(r.get("def", "")).lower()] = str(r.get("def") or r.get("label"))
            hit = labels.get(v["project"].strip().lower())
            if self.research and hit is None:
                raise ValueError(f"no available project named {v['project']}; available: " + ", ".join(str(r.get("label")) for r in self.research))
            return "research", {"project": hit or v["project"]}
        if tool == "answer_letter":
            let = self.letters.get(v["letter_id"])
            if let is None:
                raise ValueError("no such letter; the letters are: " + ", ".join(self.letters))
            ch = [str(c) for c in let.get("choices") or []]
            pick = "dismiss" if v["choice"].strip().lower() == "dismiss" else _ci(ch, v["choice"])
            if pick is None:
                raise ValueError(f"that letter's choices are: " + ", ".join(ch + ["dismiss"]))
            return "letter", {"letter": int(v["letter_id"]), "choice": pick}
        if tool == "draft":
            raw = v["pawns"].strip()
            if raw.lower() == "all":
                ids = [i for n, i in self.pawns.items() if self.roles.get(n, "colonist") == "colonist"]
            else:
                ids = []
                for part in (x for x in raw.split(",") if x.strip()):
                    n = _ci(self.pawns, part)
                    if n is None:
                        raise ValueError(f"no colonist named {part.strip()}; the colonists are: " + ", ".join(self.pawns))
                    ids.append(self.pawns[n])
            if not ids:
                raise ValueError("no one to draft")
            return "draft", {"pawns": ids[:30], "drafted": v["drafted"] == "yes"}
        if tool == "order":
            pid, kind = self._pawn(v["pawn"]), v["order"]
            if kind == "move":
                return "order", {"pawn": pid, "order": "move", "x": v["x"], "z": v["z"]}
            if kind == "attack":
                if v["target_id"] <= 0:
                    raise ValueError("attack needs target_id")
                return "order", {"pawn": pid, "order": "attack", "target": v["target_id"]}
            return "order", {"pawn": pid, "order": "hold", "hold": v["hold"] == "yes"}
        raise ValueError(f"unknown tool {tool}")


class StewardKit:
    """What core/steward.py asks of the RimWorld adapter."""

    silent_events = frozenset({"letter", "alert"})          # the director never voices these; the steward reads them

    def wake(self, e: dict) -> str | None:
        """A short reason when this event wakes the steward at once, else None."""
        t, data = e.get("type", ""), e.get("data") or {}
        if t in _WAKE_TYPES:
            return None if (t == "death" and data.get("animal")) else t
        if t == "alert":
            text = f"{data.get('alert', '')} {data.get('label', '')}".lower()
            if data.get("critical") or any(w in text for w in _WAKE_ALERT_WORDS):
                return "alert"
            return None
        if t == "letter" and data.get("choices"):
            return "letter"
        return None

    def event_line(self, e: dict) -> str | None:
        """One line for the "since last time" list, or None for chatter that is not worth the steward's attention."""
        t, data = e.get("type", ""), e.get("data") or {}
        if t in _QUIET_TYPES:
            return None
        if t not in ("letter", "alert", "quest", "injury") + _WAKE_TYPES and float(e.get("severity", 0)) < 0.4:
            return None
        text = e.get("summary") or data.get("label") or ""
        if t == "letter" and data.get("choices"):
            text = f"{data.get('label', text)} (choices: {', '.join(str(c) for c in data['choices'])}; letter #{data.get('letter')})"
        return f"{t}: " + gd.clean(text, "event") if text else t

    def system_prompt(self, advisor: bool) -> str:
        return (_HERE / "steward_system.txt").read_text() + (ADVISOR_NOTE if advisor else "")

    def default_strategy(self) -> str:
        return (_HERE / "strategy" / "steward_strategy.md").read_text(encoding="utf-8")

    def playbook_alerts(self, digest: dict) -> str:
        """The "=== PLAYBOOK ALERTS ===" block: playbook rules that fire on this digest's facts ("" when none)."""
        return alerts_section(digest)

    def render_digest(self, digest: dict) -> str:
        return render_digest(digest)

    def session(self, digest: dict, bridge, advisor: bool) -> Session:
        return Session(digest, bridge, advisor)

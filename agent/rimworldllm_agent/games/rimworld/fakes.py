"""In-process fake of the RimWorld mod's bridge (BRIDGE-API v2 wire names: tick, save_epoch,
pawn, expires_tick). Used by the tests and by ``replay``. It is a contract implementation, not
the real mod: where the contract is silent it makes the obvious choice, listed in the README."""
from __future__ import annotations

import json
import re

import httpx


def default_pawn(pid: int, first: str, **kw) -> dict:
    p = {
        "id": pid, "name": {"first": first, "nick": "", "last": "Tester"}, "age": 30, "gender": "Female",
        "body_type": "Thin", "xenotype": "Baseliner", "genes": [], "xenotype_desc": "",
        "backstory": {"child": {"title": "Farm kid", "desc": "grew up on a farm"},
                      "adult": {"title": "Cook", "desc": "ran a kitchen"}},
        "traits": [{"def": "Abrasive", "label": "abrasive", "degree": 0,
                    "desc": "Has a sharp tongue and little patience for small talk."}],
        "skills": [{"skill": "Cooking", "level": 8, "passion": "major"}],
        "health": {"conditions": [], "missing_parts": [], "pain": 0.0, "consciousness": 1.0, "talking": 1.0, "downed": False},
        "needs": {"mood": 0.6, "food": 0.5, "rest": 0.7, "joy": 0.4},
        "mood": {"value": 0.6, "thoughts": [{"label": "Ate without table", "offset": -3, "cause_pawn": None}]},
        "asleep": False, "job": "Cooking", "gear": {"worn": [], "held": []}, "relations": [], "status": "colonist",
        "faction": "colony", "map": 0, "pos": [10, 10], "room": "Kitchen", "alive": True, "spawned": True,
    }
    p.update(kw)
    return p


def rich_pawn(pid: int = 7, first: str = "Ada", **kw) -> dict:
    """A pawn with every card field the mod can send (backstory descs, traits with descs, ideoligion, history)."""
    p = default_pawn(
        pid, first, age=34, name={"first": first, "nick": "Wren", "last": "Voss"}, xenotype="Baseliner",
        xenotype_desc="Baseliners are normal humans with no engineered genes. They have no special strengths.",
        genes=[{"def": "Learning_Fast", "label": "quick study"}, {"def": "Hair_Dark", "label": "dark hair"}],
        backstory={
            "child": {"title": "Urbworld lab brat", "desc": "Grew up in the lower levels of a research arcology, taking apart "
                      "discarded instruments and teaching herself from salvaged data slates. Her parents rarely noticed."},
            "adult": {"title": "Field researcher", "desc": "Spent a decade cataloguing ruined sites for a university guild, "
                      "trading comfort for answers. The work taught her patience and to distrust easy conclusions."}},
        traits=[{"def": "TooSmart", "label": "too smart", "degree": 0,
                 "desc": "Learns quickly but forgets to eat, and is bothered by tedious company. Her mind races."},
                {"def": "Sanguine", "label": "sanguine", "degree": 2,
                 "desc": "Almost always in a cheerful mood. Recovers quickly from bad news."}],
        skills=[{"skill": "Intellectual", "level": 15, "passion": "major"}, {"skill": "Medicine", "level": 9, "passion": "minor"}],
        ideo={"name": "Seekers of the Quiet Light",
              "memes": [{"def": "Rationalist", "label": "rationalist", "desc": "Reason is the highest virtue. Ritual is distrusted."},
                        {"def": "Structure", "label": "structure", "desc": "Order and hierarchy keep people safe."}],
              "precepts": ["Drugs: prohibited", "Meat eating: abhorrent", "Rituals: rarely", "Slavery: horrible"], "certainty": 0.8},
        royal_title="Knight",
        history={"arrival": "Crash-landed with two companions and a cracked data slate.",
                 "tales": ["Cured the colony of a plague", "Survived a raid alone", "Wrote the first colony ledger"]},
        appearance="Short dark hair, pale skin, wears a lab coat and goggles.",
        relations=[{"pawn": 2, "kind": "lover, bond", "opinion": 60}, {"pawn": 99, "kind": "parent"}])
    p.update(kw)
    return p


class FakeBridge:
    """Implements the contract. Use ``bridge.transport`` as the httpx transport."""

    def __init__(self, token: str = "tok"):
        self.token = token
        self.tick, self.epoch, self.speed, self.paused = 1000, 1, 1, False
        self.pawns: dict[int, dict] = {}
        self.scenes: dict[int, dict] = {}
        self.minds: dict[int, tuple[dict | None, int]] = {}
        self.events: list[dict] = []
        self.seq = 0
        self.max_kept = 2000
        self.say_log: list[dict] = []
        self.thought_log: list[dict] = []
        self.social_log: list[dict] = []
        self.social_supported = True            # the PROPOSED /social_action op; False models a mod without it
        self.chronicle_log: list[dict] = []
        self.chronicle_supported = True        # POST/GET /chronicle are in the contract; a test can turn it off (an older mod: 404)
        self.chronicle: list[dict] = []        # what the mod holds (cap 120)
        self.recap_log: list[dict] = []        # POST /recap bodies accepted
        self.recap_refuse: str | None = None   # a test sets "expired"/"conflict" to model the mod refusing
        self.art_supported = True              # POST /art is in the contract; a test can turn it off (an older mod: 404)
        self.art_log: list[dict] = []          # POST /art bodies accepted
        self.art_things: set[int] = {500}      # artworks that exist (a destroyed one answers conflict)
        self.talk_queue: list[dict] = []
        self.seen_ops: dict[str, dict] = {}
        self.say_conflict_for: set[int] = set()
        self.pair_effects: dict[tuple, int] = {}
        self.pair_cap = 6
        self.config = {"effects": "normal", "language": "en", "events": {}, "talk_frequency": 1.0,
                       "pause_ambient_at_speed": 4, "dlc": []}
        self.ledger: list[dict] = []
        self.requests: list[tuple[str, str]] = []
        # ---- vivarium (docs/BRIDGE-API.md "Vivarium routes"); a test sets these to drive the steward ----
        self.vivarium_supported = True           # False models an older mod: every vivarium route answers 404
        self.playing = False
        self.paused_by_narrator = False
        self.viv_settings = {"may_pause_for_emergencies": True, "controls_speed": False, "heartbeat_hours": 6.0}
        self.chat: list[dict] = []               # entries, oldest first (cap 300)
        self.chat_seq = 0
        self.chat_status: list[dict] = []        # POST /chat/status bodies (minus op_id), in order
        self.chat_streams: list[str | None] = []  # stream_id of each POST /chat, in order
        self.chat_status_enabled = True          # False: answer 404 like an older mod
        self.goals_board = {"rev": 0, "goals": [], "plan": "", "orders": [], "log": []}
        self._goal_ids = 0
        self.actions: list[dict] = []
        self.digest: dict | None = None          # what GET /digest returns (None: 409 no_game)
        self.act_log: list[tuple[str, dict]] = []    # (verb, body) of every accepted /act/<verb>
        self.act_refuse: dict[str, str] = {}     # verb -> reason: that verb is refused 409 "refused" with this detail
        self.act_extra: dict[str, dict] = {}     # verb -> extra fields in the success reply
        self.transport = httpx.MockTransport(self._handle)

    # ---- test helpers ---------------------------------------------------
    def add_pawn(self, pid: int, first: str, **kw) -> dict:
        self.pawns[pid] = default_pawn(pid, first, **kw)
        return self.pawns[pid]

    def add_event(self, etype: str, actors: list[tuple[int, str]], summary: str, witnesses: list[int],
                  severity: float = 0.5, ledger_id: str | None = None, data: dict | None = None) -> dict:
        self.seq += 1
        e = {"seq": self.seq, "tick": self.tick, "type": etype, "map": 0,
             "pawns": [{"id": i, "role": r} for i, r in actors], "summary": summary,
             "severity": severity, "witnesses": witnesses, "ledger_id": ledger_id, "data": data or {}}
        self.events.append(e)
        del self.events[:-self.max_kept]       # the mod's ring keeps the newest max_kept events
        return e

    def reload_game(self) -> None:
        """A save load: epoch increments, minds are whatever the save held."""
        self.epoch += 1

    # ---- contract -------------------------------------------------------
    def env(self) -> dict:
        return {"tick": self.tick, "save_epoch": self.epoch, "paused": self.paused, "speed": self.speed}

    def _err(self, status: int, code: str, detail: str = "", **extra) -> httpx.Response:
        return httpx.Response(status, json={"ok": False, "error": code, "detail": detail, **self.env(), **extra})

    def _ok(self, **body) -> httpx.Response:
        return httpx.Response(200, json={"ok": True, **self.env(), **body})

    def _handle(self, req: httpx.Request) -> httpx.Response:
        path = req.url.path
        self.requests.append((req.method, path))
        if path == "/health":
            return self._ok(mod="RimWorldLLM", version="2.0.0", game_loaded=self.epoch > 0)
        auth = req.headers.get("authorization", "")
        if auth != f"Bearer {self.token}" and not (req.method == "GET" and req.url.params.get("token") == self.token):
            return self._err(401, "unauthorized")
        body = {}
        if req.method in ("POST", "PUT"):
            try:
                body = json.loads(req.content or b"{}")
            except ValueError:
                return self._err(400, "bad_request", "bad json")
            if not body.get("op_id"):
                return self._err(400, "bad_request", "op_id required")
            if body["op_id"] in self.seen_ops:          # only succeeded writes are remembered
                return self._err(409, "duplicate", result=self.seen_ops[body["op_id"]])
            if body.get("expires_tick") is not None and self.tick > body["expires_tick"]:
                return self._err(410, "expired")
        if path == "/config":
            return self._ok(**self.config)
        if path == "/colony":
            return self._ok(maps=[0], colonists=[{"id": i, "name": p["name"]} for i, p in self.pawns.items()],
                            ledger=self.ledger, season="spring", weather="clear")
        m = re.fullmatch(r"/(pawn|scene|mind)/(\d+)", path)
        if m:
            kind, pid = m.group(1), int(m.group(2))
            if kind == "mind":
                return self._mind(req, pid, body)
            if pid not in self.pawns:
                return self._err(404, "not_found", f"pawn {pid}")
            if kind == "pawn":
                return self._ok(**self.pawns[pid])
            sc = self.scenes.get(pid) or {
                "room": {"role": "Kitchen", "impressiveness": 30, "clean": "clean", "temp": 20},
                "present": [{"pawn": i, "job": q["job"], "distance": 3, "can_hear": True}
                            for i, q in self.pawns.items() if i != pid],
                "time_of_day": "morning", "weather": "clear", "danger": 0, "trends": {"food_days": [9, 7, 5]}}
            return self._ok(**sc)
        if path == "/events":
            since = int(req.url.params.get("since", 0))
            gap = bool(self.events) and since < self.events[0]["seq"] - 1 and since < self.seq - self.max_kept
            evs = [e for e in self.events if e["seq"] > since][:200]
            nxt = evs[-1]["seq"] if evs else max(since, self.seq)
            return self._ok(events=evs, next_seq=nxt, **({"gap": True} if gap else {}))
        if path == "/talk":
            return self._ok(requests=list(self.talk_queue))
        if path == "/say" and req.method == "POST":
            pid = body.get("pawn")
            if pid in self.say_conflict_for or pid not in self.pawns:
                return self._err(409, "conflict", "speaker not available")
            lis, intent, about = body.get("listener"), body.get("intent", "none"), body.get("about")
            if lis is not None and lis not in self.pawns:
                return self._err(409, "conflict", "listener not available")
            refusal = None
            vref = body.get("vanilla_ref")
            if vref:                      # the real mod attaches the line to the vanilla entry and applies no effect
                intent = "none"
            if intent == "gossip" and (about is None or about in (lis, pid) or lis is None):
                refusal = "no_about"
            elif intent != "none" and self.config.get("effects") == "off":
                refusal = "effects_off"
            elif intent == "comfort" and lis is not None and not any(
                    t.get("offset", 0) < 0 for t in self.pawns[lis]["mood"]["thoughts"]):
                refusal = "not_negative"
            elif intent not in ("none",) and lis is not None:
                k = (min(pid, lis), max(pid, lis), intent)
                self.pair_effects[k] = self.pair_effects.get(k, 0) + 1
                if self.pair_effects[k] > self.pair_cap:
                    refusal = "pair_cap"
            if vref:
                res = {"applied": {"bubble": True, "log": True, "effect": None, "thoughts": [], "attached": "attached"}}
            elif refusal:            # the bubble still shows; only the effect is refused
                res = {"refused": {"effect": refusal}}
            else:
                res = {"applied": {"bubble": True, "log": True, "effect": intent, "thoughts": []}}
            self.seen_ops[body["op_id"]] = res
            self.say_log.append(body)
            if body.get("talk_id"):
                self.talk_queue = [t for t in self.talk_queue if t["id"] != body["talk_id"]]
            return self._ok(**res)
        if path == "/social_action" and req.method == "POST" and self.social_supported:
            if body.get("action") not in ("seek_out", "avoid") or body.get("target") not in self.pawns \
                    or body.get("pawn") not in self.pawns:
                return self._err(400, "bad_request", "action, pawn and target are required")
            self.social_log.append(body)
            self.seen_ops[body["op_id"]] = {}
            return self._ok(applied=True)
        if path == "/thought" and req.method == "POST":
            if body.get("kind", "thought") not in ("thought", "diary", "dream"):
                return self._err(400, "bad_request", "kind must be thought or diary")
            self.thought_log.append(body)
            self.seen_ops[body["op_id"]] = {}
            return self._ok()
        if path == "/recap":
            lines = body.get("lines")
            if self.recap_refuse:
                return self._err(409 if self.recap_refuse == "conflict" else 410, self.recap_refuse)
            if not isinstance(lines, list) or not 1 <= len(lines) <= 3 or not all(isinstance(x, str) for x in lines):
                return self._err(400, "bad_request", "lines: 1 to 3 strings")
            self.recap_log.append(body)
            self.seen_ops[body["op_id"]] = {}
            return self._ok(shown=True)
        if path == "/art" and req.method == "POST" and self.art_supported:
            thing, text = body.get("thing"), str(body.get("text", "")).strip()
            if not isinstance(thing, int) or isinstance(thing, bool) or not text:
                return self._err(400, "bad_request", "thing and text are required")
            if thing not in self.art_things:
                return self._err(409, "conflict", f"no artwork {thing}")
            self.art_log.append(body)
            self.seen_ops[body["op_id"]] = {}
            return self._ok(stored=True, count=len(self.art_log))
        if path == "/chronicle" and self.chronicle_supported:
            if req.method == "GET":
                since = int(req.url.params.get("since_day", 0))
                return self._ok(entries=[e for e in self.chronicle if e["day"] >= since])
            day, text, kind = body.get("day"), str(body.get("text", "")).strip(), body.get("kind", "day")
            if not isinstance(day, int) or isinstance(day, bool) or day < 0 or not text or kind not in ("day", "major", "lore"):
                return self._err(400, "bad_request", "day, text and kind are required")
            self.chronicle_log.append(body)
            self.chronicle.append({"day": day, "kind": kind, "text": text[:2000], "tick": self.tick})
            del self.chronicle[:-120]
            self.seen_ops[body["op_id"]] = {}
            return self._ok(stored=True, count=len(self.chronicle))
        if self.vivarium_supported:
            r = self._vivarium(req, path, body)
            if r is not None:
                return r
        return self._err(404, "not_found", path)

    # ---- vivarium routes --------------------------------------------------
    def add_chat(self, who: str, text: str, to: int = 0, name: str = "") -> dict:
        """A chat entry as the game's chat bar (player) or the steward (narrator/note) would add it."""
        self.chat_seq += 1
        e = {"seq": self.chat_seq, "tick": self.tick, "who": who, "to": to, "name": name, "text": text}
        self.chat.append(e)
        del self.chat[:-300]
        return e

    def _log_action(self, verb: str, note: str) -> None:
        self.actions.append({"seq": len(self.actions) + 1, "tick": self.tick, "verb": verb, "note": note, "by": "narrator"})

    def _vivarium(self, req: httpx.Request, path: str, body: dict) -> httpx.Response | None:
        if path == "/vivarium" and req.method == "GET":
            return self._ok(playing=self.playing, paused_by_narrator=self.paused_by_narrator, chat_seq=self.chat_seq,
                            goals_rev=self.goals_board["rev"], actions_seq=len(self.actions), settings=dict(self.viv_settings))
        if path == "/digest" and req.method == "GET":
            if self.digest is None:
                return self._err(409, "no_game", "no game loaded")
            return self._ok(**self.digest)
        if path == "/chat":
            if req.method == "GET":
                since = int(req.url.params.get("since", 0))
                return self._ok(entries=[e for e in self.chat if e["seq"] > since], next_seq=self.chat_seq)
            text = str(body.get("text", "")).strip()
            if body.get("who") not in ("narrator", "note"):
                return self._err(400, "bad_request", "who must be narrator or note")
            if not 1 <= len(text) <= 500:
                return self._err(400, "bad_request", "text 1..500 chars")
            e = self.add_chat(body["who"], text)
            self.chat_streams.append(body.get("stream_id"))
            self.seen_ops[body["op_id"]] = {"seq": e["seq"]}
            return self._ok(seq=e["seq"])
        if path == "/chat/status" and req.method == "POST":
            if not self.chat_status_enabled:
                return self._err(404, "not_found")
            who, state, sid, to = body.get("who"), body.get("state"), body.get("stream_id"), body.get("to")
            prog = body.get("progress")
            if who not in ("narrator", "colonist") or state not in ("thinking", "typing", "idle"):
                return self._err(400, "bad_request", "who/state invalid")
            if not isinstance(sid, str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,64}", sid):
                return self._err(400, "bad_request", "stream_id 1..64 of [A-Za-z0-9_.:-]")
            if who == "colonist" and not (isinstance(to, int) and not isinstance(to, bool) and to > 0):
                return self._err(400, "bad_request", "colonist needs to > 0")
            if prog is not None and (isinstance(prog, bool) or not isinstance(prog, (int, float)) or not 0 <= prog <= 1):
                return self._err(400, "bad_request", "progress 0..1")
            self.chat_status.append({k: v for k, v in body.items() if k != "op_id"})
            self.seen_ops[body["op_id"]] = {}
            return self._ok(stream_id=sid, state=state)
        if path == "/goals":
            b = self.goals_board
            if req.method == "GET":
                return self._ok(**b)
            if body.get("rev") != b["rev"]:
                return self._err(409, "conflict", "stale rev", board=dict(b))
            for key, cap in (("goals", 20), ("orders", 20)):
                if key in body and (not isinstance(body[key], list) or len(body[key]) > cap):
                    return self._err(400, "bad_request", f"{key}: at most {cap}")
            for key in ("goals", "orders"):
                if key in body:
                    items = []
                    for it in body[key]:
                        it = dict(it)
                        if not str(it.get("text", "")).strip() or len(it["text"]) > 300:
                            return self._err(400, "bad_request", "text 1..300 chars")
                        if not it.get("id"):
                            self._goal_ids += 1
                            it["id"] = ("g" if key == "goals" else "o") + str(self._goal_ids)
                            it.setdefault("by", "narrator")
                        if key == "goals":
                            it.setdefault("horizon", "short")
                            it.setdefault("status", "open")
                        items.append(it)
                    b[key] = items
            if "plan" in body:
                b["plan"] = str(body["plan"])
            for line in body.get("log_add", []):
                b["log"].append({"tick": self.tick, "text": str(line)})
            del b["log"][:-50]
            b["rev"] += 1
            self.seen_ops[body["op_id"]] = {"rev": b["rev"]}
            return self._ok(**b)
        if path == "/actions" and req.method == "GET":
            since = int(req.url.params.get("since", 0))
            return self._ok(actions=[a for a in self.actions if a["seq"] > since], next_seq=len(self.actions) + 1)
        if path.startswith("/act/") and req.method == "POST":
            verb = path[5:]
            if verb not in ("time", "work", "schedule", "policy", "bed", "research", "letter", "draft", "order", "room", "build", "zone", "designate", "bill"):
                return None
            bad = self._act_shape(verb, body)
            if bad:
                return self._err(400, "bad_request", bad)
            if verb in self.act_refuse:
                return self._err(409, "refused", self.act_refuse[verb])
            refusal, note = self._act_apply(verb, body)
            if refusal:
                return self._err(409, "refused", refusal)
            self.act_log.append((verb, {k: v for k, v in body.items() if k not in ("op_id", "expires_tick")}))
            self._log_action(verb, note)
            self.seen_ops[body["op_id"]] = {"done": note}
            return self._ok(done=note, **self.act_extra.get(verb, {}))
        return None

    @staticmethod
    def _act_shape(verb: str, b: dict) -> str:
        pos = lambda v: isinstance(v, int) and not isinstance(v, bool) and v > 0
        if verb == "time":
            if "pause" not in b and "speed" not in b:
                return "pause or speed required"
            if "speed" in b and b["speed"] not in (1, 2, 3, 4):
                return "speed 1..4"
        elif verb == "work":
            pr = b.get("priorities")
            if not pos(b.get("pawn")) or not isinstance(pr, dict) or not 1 <= len(pr) <= 20 \
                    or not all(isinstance(v, int) and 0 <= v <= 4 for v in pr.values()):
                return "pawn and priorities {work: 0..4} (1..20 entries) required"
        elif verb == "schedule":
            if not pos(b.get("pawn")) or not (isinstance(b.get("from_hour"), int) and 0 <= b["from_hour"] <= 23) \
                    or not (isinstance(b.get("to_hour"), int) and 0 <= b["to_hour"] <= 24) \
                    or b.get("assignment") not in ("anything", "work", "joy", "sleep", "meditate"):
                return "pawn, from_hour 0..23, to_hour 0..24, assignment required"
        elif verb == "policy":
            if not pos(b.get("pawn")) or b.get("kind") not in ("outfit", "food", "drug") or not 1 <= len(str(b.get("name", ""))) <= 60:
                return "pawn, kind outfit/food/drug, name required"
        elif verb == "bed":
            if not pos(b.get("pawn")) or not pos(b.get("bed")):
                return "pawn and bed ids required"
        elif verb == "research":
            if not 1 <= len(str(b.get("project", ""))) <= 80:
                return "project 1..80 chars"
        elif verb == "letter":
            if not pos(b.get("letter")) or not 1 <= len(str(b.get("choice", ""))) <= 80:
                return "letter id and choice required"
        elif verb == "draft":
            ids = b.get("pawns")
            if not isinstance(ids, list) or not 1 <= len(ids) <= 30 or not all(pos(i) for i in ids) or not isinstance(b.get("drafted"), bool):
                return "pawns [1..30 ids] and drafted required"
        elif verb == "order":
            o = b.get("order")
            if not pos(b.get("pawn")) or o not in ("move", "attack", "hold"):
                return "pawn and order move/attack/hold required"
            if o == "move" and not (isinstance(b.get("x"), int) and isinstance(b.get("z"), int) and b["x"] >= 0 and b["z"] >= 0):
                return "move needs x, z >= 0"
            if o == "attack" and not pos(b.get("target")):
                return "attack needs target > 0"
        return ""

    def _act_apply(self, verb: str, b: dict) -> tuple[str, str]:
        """The settings checks of /act/time and a short success note. (refusal, note)."""
        st = self.viv_settings
        if verb == "time":
            if "speed" in b and not st["controls_speed"]:
                return "the player has not allowed the narrator to change speed", ""
            if b.get("pause") is True and not (st["controls_speed"] or (b.get("emergency") and st["may_pause_for_emergencies"])):
                return "pausing needs an emergency and the emergency-pause setting", ""
            if b.get("pause") is False and not (st["controls_speed"] or self.paused_by_narrator):
                return "the narrator may only unpause its own pause", ""
            if b.get("pause") is True:
                self.paused_by_narrator = bool(b.get("emergency")) and not st["controls_speed"] or self.paused_by_narrator
                self.paused = True
            if b.get("pause") is False:
                self.paused, self.paused_by_narrator = False, False
            if "speed" in b:
                self.speed = b["speed"]
            return "", "time: " + ", ".join(f"{k} {b[k]}" for k in ("pause", "speed") if k in b)
        notes = {"work": lambda: "set " + ", ".join(f"{k} {v}" for k, v in b["priorities"].items()) + f" for pawn {b['pawn']}",
                 "schedule": lambda: f"schedule {b['from_hour']}-{b['to_hour']} {b['assignment']} for pawn {b['pawn']}",
                 "policy": lambda: f"{b['kind']} policy {b['name']} for pawn {b['pawn']}",
                 "bed": lambda: f"bed {b['bed']} for pawn {b['pawn']}",
                 "research": lambda: f"research {b['project']}",
                 "letter": lambda: f"letter {b['letter']}: {b['choice']}",
                 "draft": lambda: ("drafted" if b["drafted"] else "undrafted") + f" {len(b['pawns'])} pawn(s)",
                 "order": lambda: f"{b['order']} order for pawn {b['pawn']}"}
        for vb in ("room", "build", "zone", "designate", "bill"):      # phase 2 hands: accepted as sent; a test sets act_refuse[verb] to refuse
            notes[vb] = lambda vb=vb: vb + ": " + ", ".join(f"{k} {x}" for k, x in b.items() if k != "why")
        return "", notes[verb]()

    def _mind(self, req: httpx.Request, pid: int, body: dict) -> httpx.Response:
        blob, rev = self.minds.get(pid, (None, 0))
        if req.method == "GET":
            return self._ok(mind=blob, rev=rev)
        if len(json.dumps(body.get("mind", {}), separators=(",", ":"), ensure_ascii=False).encode()) > 32 * 1024:
            return self._err(413, "too_large")
        if body.get("rev") != rev:
            return self._err(409, "conflict", "stale rev", rev=rev)
        self.minds[pid] = (body["mind"], rev + 1)
        self.seen_ops[body["op_id"]] = {"rev": rev + 1}
        return self._ok(rev=rev + 1)



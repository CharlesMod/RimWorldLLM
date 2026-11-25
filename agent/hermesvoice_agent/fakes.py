"""In-process fakes: a bridge speaking the BRIDGE-API v2 contract, and a deterministic LLM.

Used by the tests and by ``hermesvoice-agent replay``. The fake bridge is a contract
implementation, not the real mod: where the contract is silent it makes the obvious choice
(documented below) and the choice is listed in README "contract notes".
"""
from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass, field

import httpx

from .calllog import CallStats
from .llm import Completion, Messages


def default_pawn(pid: int, first: str, **kw) -> dict:
    p = {
        "id": pid, "name": {"first": first, "nick": "", "last": "Tester"}, "age": 30, "gender": "Female",
        "body_type": "Thin", "xenotype": "Baseliner", "genes": [],
        "backstory": {"child": {"title": "Farm kid", "desc": "grew up on a farm"},
                      "adult": {"title": "Cook", "desc": "ran a kitchen"}},
        "traits": [{"def": "Abrasive", "label": "abrasive", "degree": 0}],
        "skills": [{"skill": "Cooking", "level": 8, "passion": "major"}],
        "health": {"conditions": [], "missing_parts": [], "pain": 0.0, "consciousness": 1.0, "downed": False},
        "needs": {"mood": 0.6, "food": 0.5, "rest": 0.7, "joy": 0.4},
        "mood": {"value": 0.6, "thoughts": [{"label": "Ate without table", "offset": -3, "cause_pawn": None}]},
        "job": "Cooking", "gear": {"worn": [], "held": []}, "relations": [], "status": "colonist",
        "faction": "colony", "map": 0, "pos": [10, 10], "room": "Kitchen", "alive": True, "spawned": True,
    }
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
        self.talk_queue: list[dict] = []
        self.seen_ops: dict[str, dict] = {}
        self.say_conflict_for: set[int] = set()
        self.config = {"effects": "normal", "language": "en", "events": {}, "talk_frequency": 1.0,
                       "pause_ambient_at_speed": 3, "dlc": []}
        self.ledger: list[dict] = []
        self.requests: list[tuple[str, str]] = []
        self.transport = httpx.MockTransport(self._handle)

    # ---- test helpers ---------------------------------------------------
    def add_pawn(self, pid: int, first: str, **kw) -> dict:
        self.pawns[pid] = default_pawn(pid, first, **kw)
        return self.pawns[pid]

    def add_event(self, etype: str, pawns: list[tuple[int, str]], summary: str, witnesses: list[int],
                  severity: float = 0.5, ledger_id: str | None = None, data: dict | None = None) -> dict:
        self.seq += 1
        e = {"seq": self.seq, "tick": self.tick, "type": etype, "map": 0,
             "pawns": [{"id": i, "role": r} for i, r in pawns], "summary": summary,
             "severity": severity, "witnesses": witnesses, "ledger_id": ledger_id, "data": data or {}}
        self.events.append(e)
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
            return self._ok(mod="HermesVoice", version="2.0.0", game_loaded=self.epoch > 0)
        auth = req.headers.get("authorization", "")
        if auth != f"Bearer {self.token}" and not (req.method == "GET" and req.url.params.get("token") == self.token):
            return self._err(401, "unauthorized")
        body = {}
        if req.method in ("POST", "PUT"):
            try:
                body = json.loads(req.content or b"{}")
            except ValueError:
                return self._err(400, "bad_request", "bad json")
            if "op_id" not in body:
                return self._err(400, "bad_request", "op_id required")
            if body["op_id"] in self.seen_ops:
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
            res = {"applied": {"bubble": True, "log": True, "effect": body.get("intent"), "thoughts": []}}
            self.seen_ops[body["op_id"]] = res
            self.say_log.append(body)
            if body.get("talk_id"):
                self.talk_queue = [t for t in self.talk_queue if t["id"] != body["talk_id"]]
            return self._ok(**res)
        if path == "/thought" and req.method == "POST":
            self.thought_log.append(body)
            self.seen_ops[body["op_id"]] = {}
            return self._ok()
        return self._err(404, "not_found", path)

    def _mind(self, req: httpx.Request, pid: int, body: dict) -> httpx.Response:
        blob, rev = self.minds.get(pid, (None, 0))
        if req.method == "GET":
            return self._ok(mind=blob, rev=rev)
        if len(json.dumps(body.get("mind", {})).encode()) > 32 * 1024:
            return self._err(413, "too_large")
        if body.get("rev") != rev:
            return self._err(409, "conflict", "stale rev", rev=rev)
        self.minds[pid] = (body["mind"], rev + 1)
        self.seen_ops[body["op_id"]] = {"rev": rev + 1}
        return self._ok(rev=rev + 1)


_CANNED = {
    "line": ["Hm. Noted.", "Not my favourite thing.", "Right. What now?", "That is going to hurt tomorrow.",
             "Keep your head down.", "I have seen worse. Not much worse.", "Somebody get the medicine."],
}


@dataclass
class FakeLLM:
    """Deterministic: a line is picked by hashing (pawn, turn count, last user text).
    ``script`` maps kind -> list of replies consumed in order (then falls back to canned)."""
    script: dict[str, list[str]] = field(default_factory=dict)
    calls: list[dict] = field(default_factory=list)
    slot_prompts: dict[int, str] = field(default_factory=dict)
    fail: bool = False

    async def complete(self, messages: Messages, *, slot: int | None, kind: str = "line",
                       max_tokens: int = 48, stop: list[str] | None = None, grammar: str | None = None,
                       temperature: float | None = None, pawn: int | None = None) -> Completion:
        from .llm import LLMError
        if self.fail:
            raise LLMError("fake failure")
        flat = "\n".join(t for _, t in messages)
        self.calls.append({"kind": kind, "slot": slot, "messages": messages, "grammar": grammar, "pawn": pawn})
        q = self.script.get(kind)
        if q:
            text = q.pop(0)
        elif kind == "intent":
            last_line = messages[-2][1] if len(messages) >= 2 else ""
            text = ("kind" if re.search(r"\b(thanks|friend|sit|eat)\b", last_line, re.I) else
                    "insult" if re.search(r"\b(idiot|useless|fool)\b", last_line, re.I) else "none")
        elif kind == "line":
            h = int(hashlib.sha1((str(pawn) + flat[-200:]).encode()).hexdigest(), 16)
            text = _CANNED["line"][h % len(_CANNED["line"])]
        else:
            text = "Fine."
        # crude cache model: shared leading characters with the slot's previous prompt count as cached
        prev = self.slot_prompts.get(slot if slot is not None else -1, "")
        n = os.path.commonprefix([prev, flat])
        self.slot_prompts[slot if slot is not None else -1] = flat
        st = CallStats(kind=kind, slot=slot, pawn=pawn, prompt_n=max(1, (len(flat) - len(n)) // 4),
                       cache_n=len(n) // 4, prompt_ms=10.0, predicted_n=max(1, len(text) // 4),
                       predicted_ms=20.0)
        return Completion(text, st)

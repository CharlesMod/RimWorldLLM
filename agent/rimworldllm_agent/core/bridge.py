"""Generic bridge protocol client (envelope, op_id/expiry, events?since, epoch, mind blobs, say/thought/talk).

Endpoint paths and the speaker field name come from the adapter's ``ProtocolNames``.

Handles the envelope (game, epoch, paused, speed), op_id/expires_at on writes,
the /events?since poller with gap handling, and epoch change detection.
"""
from __future__ import annotations

import uuid
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Protocol

import httpx

from .adapter import ProtocolNames
from .config import Config, read_bridge_json


class BridgeError(Exception):
    def __init__(self, code: str, detail: str = "", status: int = 0, body: dict | None = None):
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code, self.detail, self.status, self.body = code, detail, status, body or {}


@dataclass
class Envelope:
    game: int = 0
    epoch: int = 0
    paused: bool = False
    speed: int = 1


class Bridge(Protocol):
    """What the director needs from a transport. ``BridgeClient`` is the HTTP one; an emulator
    socket or an in-process plugin can stand in by implementing the same methods."""
    env: Envelope
    epoch_changed: bool

    async def health(self) -> dict: ...
    async def config(self) -> dict: ...
    async def roster(self) -> dict: ...
    async def actor(self, actor_id: int) -> dict: ...
    async def scene(self, actor_id: int) -> dict: ...
    async def talk(self) -> list[dict]: ...
    async def get_mind(self, actor_id: int) -> tuple[dict | None, int]: ...
    async def put_mind(self, actor_id: int, mind: dict, rev: int, op_id: str | None = None) -> int: ...
    async def say(self, actor: int, text: str, *, listener: int | None = None, intent: str = "none",
                  conversation_id: str | None = None, combat: bool = False, talk_id: str | None = None,
                  expires_at: int | None = None, op_id: str | None = None,
                  about: int | None = None, vanilla_ref: str | None = None,
                  kind: str | None = None) -> dict: ...
    async def thought(self, actor: int, text: str, op_id: str | None = None, kind: str | None = None,
                      surface: str | None = None) -> dict: ...
    async def chronicle(self, day: int, text: str, kind: str = "day", op_id: str | None = None) -> dict: ...
    async def social_action(self, actor: int, action: str, target: int, topic: str = "", op_id: str | None = None) -> dict: ...
    async def aclose(self) -> None: ...


class EventSource(Protocol):
    """Where core events come from: a poll, a push queue, or an external sensor process."""
    last_gap: bool

    async def poll(self) -> list[dict]: ...
    def reset(self) -> None: ...


class BridgeClient:
    def __init__(self, client: httpx.AsyncClient, base_url: str = "http://bridge", token: str = "",
                 protocol: ProtocolNames | None = None):
        self.protocol = protocol or ProtocolNames()
        self.http = client
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.env = Envelope()
        self.on_epoch_change: Callable[[int, int], Awaitable[None] | None] | None = None
        self._epoch_seen: int | None = None
        self.epoch_changed = False   # sticky flag, consumed by the director

    @classmethod
    def from_config(cls, cfg: Config, protocol: ProtocolNames | None = None, bridge_json=None,
                    transport: httpx.AsyncBaseTransport | None = None) -> "BridgeClient":
        if cfg.bridge_url:
            url, token = cfg.bridge_url, cfg.bridge_token or ""
        else:
            bj = read_bridge_json(cfg.bridge_json or bridge_json)
            host = bj.get("bind", "127.0.0.1")
            if host in ("0.0.0.0", "::"):
                host = "127.0.0.1"
            url, token = f"http://{host}:{bj['port']}", bj.get("token", "")
            token = cfg.bridge_token or token
        return cls(httpx.AsyncClient(timeout=10.0, transport=transport), url, token, protocol)

    async def aclose(self) -> None:
        await self.http.aclose()

    # ---- plumbing -------------------------------------------------------
    def _absorb(self, body: dict) -> None:
        p = self.protocol
        if not isinstance(body, dict) or p.time_field not in body:
            return
        old = self._epoch_seen
        self.env = Envelope(
            game=int(body.get(p.time_field, 0)), epoch=int(body.get(p.epoch_field, 0)),
            paused=bool(body.get("paused", False)), speed=int(body.get("speed", 1)))
        self._epoch_seen = self.env.epoch
        if old is not None and old != self.env.epoch:
            self.epoch_changed = True

    async def _req(self, method: str, path: str, *, params: dict | None = None,
                   json_body: dict | None = None, auth: bool = True) -> dict:
        headers = {"Authorization": f"Bearer {self.token}"} if auth and self.token else {}
        try:
            r = await self.http.request(method, self.base_url + path, params=params,
                                        json=json_body, headers=headers)
        except httpx.HTTPError as e:
            raise BridgeError("unreachable", str(e) or type(e).__name__) from e
        try:
            body = r.json()
        except ValueError:
            raise BridgeError("bad_reply", f"HTTP {r.status_code} not JSON", r.status_code)
        self._absorb(body)
        if r.status_code >= 400 or body.get("ok") is False:
            raise BridgeError(body.get("error", f"http_{r.status_code}"),
                              body.get("detail", ""), r.status_code, body)
        return body

    def _write_body(self, body: dict, expires_at: int | None, op_id: str | None) -> dict:
        b = dict(body)
        b["op_id"] = op_id or str(uuid.uuid4())
        if expires_at is not None:
            b[self.protocol.expiry_field] = int(expires_at)
        return b

    # ---- reads ----------------------------------------------------------
    async def health(self) -> dict:
        return await self._req("GET", "/health", auth=False)

    async def config(self) -> dict:
        return await self._req("GET", "/config")

    async def roster(self) -> dict:
        return await self._req("GET", self.protocol.roster_path)

    async def actor(self, actor_id: int) -> dict:
        return await self._req("GET", f"{self.protocol.actor_path}/{actor_id}")

    async def scene(self, actor_id: int) -> dict:
        return await self._req("GET", f"{self.protocol.scene_path}/{actor_id}")

    async def events(self, since: int) -> dict:
        return await self._req("GET", "/events", params={"since": since})

    async def talk(self) -> list[dict]:
        return (await self._req("GET", "/talk")).get("requests", [])

    async def get_mind(self, actor_id: int) -> tuple[dict | None, int]:
        b = await self._req("GET", f"{self.protocol.mind_path}/{actor_id}")
        return b.get("mind"), int(b.get("rev", 0))

    # ---- writes ---------------------------------------------------------
    async def say(self, actor: int, text: str, *, listener: int | None = None,
                  intent: str = "none", conversation_id: str | None = None,
                  combat: bool = False, talk_id: str | None = None,
                  expires_at: int | None = None, op_id: str | None = None,
                  about: int | None = None, vanilla_ref: str | None = None,
                  kind: str | None = None) -> dict:
        body: dict[str, Any] = {self.protocol.actor_field: actor, "text": text, "intent": intent, "combat": combat}
        if kind:                        # how it was said: normal | shout | whisper (the mod guesses when absent)
            body["kind"] = kind
        if vanilla_ref:                 # the line was written for one of the game's own interactions (adds no effect)
            body["vanilla_ref"] = vanilla_ref
        if listener is not None:
            body["listener"] = listener
        if about is not None:
            body["about"] = about
        if conversation_id:
            body["conversation_id"] = conversation_id
        if talk_id:
            body["talk_id"] = talk_id
        return await self._req("POST", "/say", json_body=self._write_body(body, expires_at, op_id))

    async def thought(self, actor: int, text: str, op_id: str | None = None, kind: str | None = None,
                      surface: str | None = None) -> dict:
        """Inner monologue. ``kind`` is optional: "thought" (the default, omitted) or "diary" (contract: POST /thought)."""
        body: dict[str, Any] = {self.protocol.actor_field: actor, "text": text}
        if kind:
            body["kind"] = kind
        if surface:                     # one short line for the map's thought cloud (the full text stays in the Mind tab)
            body["surface"] = surface
        return await self._req("POST", "/thought", json_body=self._write_body(body, None, op_id))

    async def social_action(self, actor: int, action: str, target: int, topic: str = "", op_id: str | None = None) -> dict:
        """PROPOSED op (docs/BRIDGE-API.md): POST /social_action {op_id, <actor field>, action: "seek_out"|"avoid", target, topic}.
        The mod answers 404 until it implements it; the caller treats that as 'not supported'."""
        body: dict[str, Any] = {self.protocol.actor_field: actor, "action": action, "target": target}
        if topic:
            body["topic"] = topic
        return await self._req("POST", "/social_action", json_body=self._write_body(body, None, op_id))

    async def chronicle(self, day: int, text: str, kind: str = "day", op_id: str | None = None) -> dict:
        """Contract: POST /chronicle {op_id, day, text, kind: "day"|"major"}; the mod saves it with the game."""
        return await self._req("POST", "/chronicle",
                               json_body=self._write_body({"day": day, "text": text, "kind": kind}, None, op_id))

    async def recap(self, epoch: int, lines: list[str], op_id: str | None = None) -> dict:
        """Contract: POST /recap {op_id, epoch, lines: [up to 3]}; the mod shows it once per save load, soon after the load."""
        return await self._req("POST", "/recap", json_body=self._write_body({"epoch": epoch, "lines": lines}, None, op_id))

    async def art(self, thing: int, text: str, op_id: str | None = None) -> dict:
        """Contract: POST /art {op_id, thing, text}; the mod shows the text on the artwork's info card."""
        return await self._req("POST", "/art", json_body=self._write_body({"thing": thing, "text": text}, None, op_id))

    async def get_chronicle(self, since_day: int = 0) -> list[dict]:
        """Contract: GET /chronicle?since_day=N -> entries, oldest first (day, kind, text and the mod's receive time)."""
        b = await self._req("GET", "/chronicle", params={"since_day": since_day})
        return list(b.get("entries", []))

    # ---- vivarium (docs/BRIDGE-API.md "Vivarium routes"; an older mod answers 404) -------------------
    async def vivarium(self) -> dict:
        """GET /vivarium: playing, settings, chat_seq, goals_rev, actions_seq, paused_by_narrator."""
        return await self._req("GET", "/vivarium")

    async def digest(self) -> dict:
        """GET /digest: the compact world digest (needs a loaded game)."""
        return await self._req("GET", "/digest")

    async def chat_since(self, seq: int) -> dict:
        """GET /chat?since=N -> {"entries": [...], "next_seq": N}."""
        return await self._req("GET", "/chat", params={"since": seq})

    async def post_chat(self, who: str, text: str, op_id: str | None = None, stream_id: str | None = None) -> dict:
        """POST /chat {who: narrator|note, text, stream_id?}; -> {"seq": N}. A stream_id ends that /chat/status stream."""
        body: dict[str, Any] = {"who": who, "text": text}
        if stream_id is not None:
            body["stream_id"] = stream_id
        return await self._req("POST", "/chat", json_body=self._write_body(body, None, op_id))

    async def post_chat_status(self, who: str, state: str, stream_id: str, to: int | None = None,
                               progress: float | None = None, op_id: str | None = None) -> dict:
        """POST /chat/status {who: narrator|<character>, to?, state: thinking|typing|idle, stream_id, progress?}.
        An older mod answers 404 not_found."""
        body: dict[str, Any] = {"who": who, "state": state, "stream_id": stream_id}
        if to is not None:
            body["to"] = to
        if progress is not None:
            body["progress"] = progress
        return await self._req("POST", "/chat/status", json_body=self._write_body(body, None, op_id))

    async def goals(self) -> dict:
        """GET /goals: the goals board with its rev."""
        return await self._req("GET", "/goals")

    async def put_goals(self, rev: int, op_id: str | None = None, **fields: Any) -> dict:
        """PUT /goals {rev, goals?, plan?, orders?, log_add?}; a stale rev is BridgeError code "conflict" (body["board"] holds the current board)."""
        return await self._req("PUT", "/goals", json_body=self._write_body({"rev": rev, **fields}, None, op_id))

    async def actions_since(self, seq: int) -> dict:
        """GET /actions?since=N -> {"actions": [...], "next_seq": N}."""
        return await self._req("GET", "/actions", params={"since": seq})

    async def act(self, verb: str, body: dict, expires_at: int | None = None, op_id: str | None = None) -> dict:
        """POST /act/<verb>: returns the reply body ({"done": note, ...}); a refusal is BridgeError code "refused" (status 409, detail = the reason)."""
        return await self._req("POST", f"/act/{verb}", json_body=self._write_body(body, expires_at, op_id))

    async def put_mind(self, actor_id: int, mind: dict, rev: int, op_id: str | None = None) -> int:
        b = await self._req("PUT", f"{self.protocol.mind_path}/{actor_id}",
                            json_body=self._write_body({"rev": rev, "mind": mind}, None, op_id))
        return int(b.get("rev", rev + 1))


class EventPoller:
    """HTTP EventSource: polls /events?since=seq. Handles gaps (the mod keeps only 2,000 events) and paging."""

    def __init__(self, bridge: BridgeClient, since: int = 0,
                 normalize: Callable[[dict], dict] | None = None):
        self.bridge = bridge
        self.normalize = normalize or (lambda e: e)
        self.since = since
        self.gaps = 0
        self.last_gap = False   # set by poll(): the last poll lost events

    async def poll(self) -> list[dict]:
        out: list[dict] = []
        self.last_gap = False
        for _ in range(20):  # at most 20 pages per poll
            b = await self.bridge.events(self.since)
            if b.get("gap"):
                self.gaps += 1
                self.last_gap = True   # events were lost: the caller should resync from the roster
            evs = b.get("events", [])
            out.extend(self.normalize(e) for e in evs)
            nxt = b.get("next_seq")
            if nxt is not None:
                self.since = max(self.since, int(nxt))
            elif evs:
                self.since = max(self.since, max(e["seq"] for e in evs))
            if len(evs) < 200:
                break
        return out

    def reset(self) -> None:
        """After a epoch change the sequence numbers may restart."""
        self.since = 0

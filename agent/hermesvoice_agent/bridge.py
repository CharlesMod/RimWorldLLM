"""Bridge client for docs/BRIDGE-API.md (v2).

Handles the envelope (tick, save_epoch, paused, speed), op_id/expires_tick on writes,
the /events?since poller with gap handling, and save_epoch change detection.
"""
from __future__ import annotations

import uuid
from dataclasses import dataclass
from typing import Any, Awaitable, Callable

import httpx

from .config import Config, read_bridge_json


class BridgeError(Exception):
    def __init__(self, code: str, detail: str = "", status: int = 0, body: dict | None = None):
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code, self.detail, self.status, self.body = code, detail, status, body or {}


@dataclass
class Envelope:
    tick: int = 0
    save_epoch: int = 0
    paused: bool = False
    speed: int = 1


class BridgeClient:
    def __init__(self, client: httpx.AsyncClient, base_url: str = "http://bridge", token: str = ""):
        self.http = client
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.env = Envelope()
        self.on_epoch_change: Callable[[int, int], Awaitable[None] | None] | None = None
        self._epoch_seen: int | None = None
        self.epoch_changed = False   # sticky flag, consumed by the director

    @classmethod
    def from_config(cls, cfg: Config, transport: httpx.AsyncBaseTransport | None = None) -> "BridgeClient":
        if cfg.bridge_url:
            url, token = cfg.bridge_url, cfg.bridge_token or ""
        else:
            bj = read_bridge_json(cfg.bridge_json)
            host = bj.get("bind", "127.0.0.1")
            if host in ("0.0.0.0", "::"):
                host = "127.0.0.1"
            url, token = f"http://{host}:{bj['port']}", bj.get("token", "")
            token = cfg.bridge_token or token
        return cls(httpx.AsyncClient(timeout=10.0, transport=transport), url, token)

    async def aclose(self) -> None:
        await self.http.aclose()

    # ---- plumbing -------------------------------------------------------
    def _absorb(self, body: dict) -> None:
        if not isinstance(body, dict) or "tick" not in body:
            return
        old = self._epoch_seen
        self.env = Envelope(
            tick=int(body.get("tick", 0)), save_epoch=int(body.get("save_epoch", 0)),
            paused=bool(body.get("paused", False)), speed=int(body.get("speed", 1)))
        self._epoch_seen = self.env.save_epoch
        if old is not None and old != self.env.save_epoch:
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

    def _write_body(self, body: dict, expires_tick: int | None, op_id: str | None) -> dict:
        b = dict(body)
        b["op_id"] = op_id or str(uuid.uuid4())
        if expires_tick is not None:
            b["expires_tick"] = int(expires_tick)
        return b

    # ---- reads ----------------------------------------------------------
    async def health(self) -> dict:
        return await self._req("GET", "/health", auth=False)

    async def config(self) -> dict:
        return await self._req("GET", "/config")

    async def colony(self) -> dict:
        return await self._req("GET", "/colony")

    async def pawn(self, pawn_id: int) -> dict:
        return await self._req("GET", f"/pawn/{pawn_id}")

    async def scene(self, pawn_id: int) -> dict:
        return await self._req("GET", f"/scene/{pawn_id}")

    async def events(self, since: int) -> dict:
        return await self._req("GET", "/events", params={"since": since})

    async def talk(self) -> list[dict]:
        return (await self._req("GET", "/talk")).get("requests", [])

    async def get_mind(self, pawn_id: int) -> tuple[dict | None, int]:
        b = await self._req("GET", f"/mind/{pawn_id}")
        return b.get("mind"), int(b.get("rev", 0))

    # ---- writes ---------------------------------------------------------
    async def say(self, pawn: int, text: str, *, listener: int | None = None,
                  intent: str = "none", conversation_id: str | None = None,
                  combat: bool = False, talk_id: str | None = None,
                  expires_tick: int | None = None, op_id: str | None = None) -> dict:
        body: dict[str, Any] = {"pawn": pawn, "text": text, "intent": intent, "combat": combat}
        if listener is not None:
            body["listener"] = listener
        if conversation_id:
            body["conversation_id"] = conversation_id
        if talk_id:
            body["talk_id"] = talk_id
        return await self._req("POST", "/say", json_body=self._write_body(body, expires_tick, op_id))

    async def thought(self, pawn: int, text: str, op_id: str | None = None) -> dict:
        return await self._req("POST", "/thought",
                               json_body=self._write_body({"pawn": pawn, "text": text}, None, op_id))

    async def put_mind(self, pawn_id: int, mind: dict, rev: int, op_id: str | None = None) -> int:
        b = await self._req("PUT", f"/mind/{pawn_id}",
                            json_body=self._write_body({"rev": rev, "mind": mind}, None, op_id))
        return int(b.get("rev", rev + 1))


class EventPoller:
    """Polls /events?since=seq. Handles gaps (the mod keeps only 2,000 events) and paging."""

    def __init__(self, bridge: BridgeClient, since: int = 0):
        self.bridge = bridge
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
                self.last_gap = True   # events were lost: the caller should resync from /colony
            evs = b.get("events", [])
            out.extend(evs)
            nxt = b.get("next_seq")
            if nxt is not None:
                self.since = max(self.since, int(nxt))
            elif evs:
                self.since = max(self.since, max(e["seq"] for e in evs))
            if len(evs) < 200:
                break
        return out

    def reset(self) -> None:
        """After a save_epoch change the sequence numbers may restart."""
        self.since = 0

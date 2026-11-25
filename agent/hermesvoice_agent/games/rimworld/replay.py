"""``hermesvoice-agent replay <events.jsonl>``: run the director over recorded events against a
fake bridge and print what would be said.

File format, one JSON object per line:
  {"pawn": {...GET /pawn snapshot...}}          optional; unknown pawn ids get a generic snapshot
  {"seq": 1, "tick": 1200, "type": "injury", "pawns": [...], "summary": "...", "severity": 0.4,
   "witnesses": [1, 2], ...}                    an event, as served by /events
"""
from __future__ import annotations

import json
from pathlib import Path

from ...core.bridge import BridgeClient
from ...core.config import Config
from ...core.director import Director
from ...core.fakes import FakeLLM
from ...core.llm import LLMClient
from .adapter import RimWorldAdapter
from .fakes import FakeBridge


def load_jsonl(path: str | Path) -> tuple[list[dict], list[dict]]:
    pawns, events = [], []
    for ln in Path(path).read_text().splitlines():
        ln = ln.strip()
        if not ln or ln.startswith("#"):
            continue
        d = json.loads(ln)
        (pawns if "pawn" in d and "type" not in d else events).append(d["pawn"] if "pawn" in d and "type" not in d else d)
    return pawns, events


async def replay(path: str | Path, cfg: Config, llm: LLMClient | None = None) -> list[dict]:
    import httpx
    pawns, events = load_jsonl(path)
    fb = FakeBridge()
    for p in pawns:
        fb.pawns[p["id"]] = p
    ids = {int(x["id"]) for e in events for x in e.get("pawns", [])} | \
          {int(w) for e in events for w in e.get("witnesses", [])}
    for i in sorted(ids - set(fb.pawns)):
        fb.add_pawn(i, f"Pawn{i}")
    adapter = RimWorldAdapter()
    bridge = BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", fb.token, adapter.protocol)
    clock = {"t": 0.0}
    d = Director(cfg, bridge, llm or FakeLLM(), adapter, clock=lambda: fb.tick / 60.0)  #=lambda: fb.tick / 60.0)
    await d.start()
    for e in sorted(events, key=lambda e: e.get("seq", 0)):
        fb.tick = max(fb.tick, int(e.get("tick", fb.tick)))
        fb.seq = max(fb.seq, int(e.get("seq", fb.seq + 1)) - 1)
        fb.add_event(e["type"], [(p["id"], p.get("role", "")) for p in e.get("pawns", [])], e.get("summary", ""),
                     e.get("witnesses", []), e.get("severity", 0.5), e.get("ledger_id"), e.get("data"))
        await d.step()
        await d.drain()
    await d.step()
    await d.drain()
    out = []
    for s in d.said:
        out.append(s)
    for pid, why in d.dropped:
        out.append({"actor": pid, "dropped": why})
    return out

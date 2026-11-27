"""``rimworldllm-agent replay <events.jsonl>``: run the director over recorded events against a
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


async def _settle(d, limit: int = 40) -> None:
    """Let idle work finish: one idle job starts per step, so step until a step produces nothing new."""
    for _ in range(limit):
        before = (len(d.idle.log), len(d.dropped))
        await d.step()
        await d.drain()
        if (len(d.idle.log), len(d.dropped)) == before and not d.idle.active():
            return


async def replay(path: str | Path, cfg: Config, llm: LLMClient | None = None) -> list[dict]:
    """Run the director over recorded events. Across days, the evening and the turn of the day are given
    idle time, so reflections, diaries and chronicle entries appear after the lines they follow."""
    import tempfile
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
    upd = adapter.units_per_day
    with tempfile.TemporaryDirectory(prefix="rimworldllm-replay-") as tmp:     # never touch the real chronicle
        cfg = cfg.model_copy(update={"data_dir": Path(tmp), "turn_gap_ticks": 0, "turn_gap_floor_real_s": 0.0})   # offline: no reading time
        bridge = BridgeClient(httpx.AsyncClient(transport=fb.transport), "http://fake", fb.token, adapter.protocol)
        d = Director(cfg, bridge, llm or FakeLLM(), adapter, clock=lambda: fb.tick / 60.0)
        await d.start()
        cur_day = None
        for e in sorted(events, key=lambda e: e.get("seq", 0)):
            day = int(e.get("tick", fb.tick)) // upd
            if cur_day is not None and day > cur_day:
                fb.tick = max(fb.tick, cur_day * upd + int(0.75 * upd))    # the evening: diaries
                await _settle(d)
                fb.tick = max(fb.tick, (cur_day + 1) * upd + 10)           # the turn of the day: the chronicle
                await _settle(d)
            cur_day = day
            fb.tick = max(fb.tick, int(e.get("tick", fb.tick)))
            fb.seq = max(fb.seq, int(e.get("seq", fb.seq + 1)) - 1)
            fb.add_event(e["type"], [(p["id"], p.get("role", "")) for p in e.get("pawns", [])], e.get("summary", ""),
                         e.get("witnesses", []), e.get("severity", 0.5), e.get("ledger_id"), e.get("data"))
            await d.step()
            await d.drain()
        if cur_day is not None:
            fb.tick = max(fb.tick, cur_day * upd + int(0.75 * upd))
            await _settle(d)
            fb.tick = max(fb.tick, (cur_day + 1) * upd + 10)
            await _settle(d)
        else:
            await _settle(d)
        out = list(d.said)
        out += [{"actor": pid, "dropped": why} for pid, why in d.dropped]
        out += d.idle.log
        return out

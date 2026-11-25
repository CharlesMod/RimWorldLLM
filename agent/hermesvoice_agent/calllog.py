"""Per-call stats and the JSONL call log (SPEC §4: every call logs prompt/cached/effective prefill)."""
from __future__ import annotations

import json
import threading
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path


@dataclass
class CallStats:
    kind: str = ""              # line | intent | reflect | voice_card | narrator | probe ...
    slot: int | None = None
    pawn: int | None = None
    prompt_n: int = 0
    cache_n: int = 0
    prompt_ms: float = 0.0
    predicted_n: int = 0
    predicted_ms: float = 0.0
    wall_s: float = 0.0
    ttft_s: float | None = None
    ok: bool = True
    error: str = ""
    ts: float = field(default_factory=time.time)

    @property
    def raw_prefill_tps(self) -> float:
        return self.prompt_n / (self.prompt_ms / 1000) if self.prompt_ms > 0 else 0.0

    @property
    def effective_prefill_tps(self) -> float:
        """(prompt_n + cache_n) / prompt_s. Equal to raw means a cache miss."""
        return (self.prompt_n + self.cache_n) / (self.prompt_ms / 1000) if self.prompt_ms > 0 else 0.0

    def to_dict(self) -> dict:
        d = asdict(self)
        d["raw_prefill_tps"] = round(self.raw_prefill_tps, 1)
        d["effective_prefill_tps"] = round(self.effective_prefill_tps, 1)
        return d


class CallLog:
    def __init__(self, path: Path | None):
        self.path = Path(path) if path else None
        self.entries: list[dict] = []   # in-memory tail for the debug view and tests
        self._lock = threading.Lock()
        if self.path:
            self.path.parent.mkdir(parents=True, exist_ok=True)

    def record(self, stats: CallStats, **extra) -> dict:
        d = stats.to_dict() | extra
        with self._lock:
            self.entries.append(d)
            del self.entries[:-500]
            if self.path:
                with self.path.open("a", encoding="utf-8") as f:
                    f.write(json.dumps(d, ensure_ascii=False) + "\n")
        return d

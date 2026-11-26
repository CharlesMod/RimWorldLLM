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
    actor: int | None = None
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


MAX_BYTES = 20 * 1024 * 1024    # rotate calls.jsonl before it would pass this
KEEP = 3                        # calls.jsonl.1 .. .3 are kept; the oldest is dropped


def rotate_file(path: Path, keep: int) -> None:
    """path -> path.1 -> ... -> path.<keep>; the oldest is dropped. Best effort."""
    try:
        path.with_name(f"{path.name}.{keep}").unlink(missing_ok=True)
        for i in range(keep - 1, 0, -1):
            src = path.with_name(f"{path.name}.{i}")
            if src.exists():
                src.replace(path.with_name(f"{path.name}.{i + 1}"))
        if keep > 0:
            path.replace(path.with_name(f"{path.name}.1"))
        else:
            path.unlink(missing_ok=True)
    except OSError:
        pass


class CallLog:
    def __init__(self, path: Path | None, max_bytes: int = MAX_BYTES, keep: int = KEEP):
        self.path = Path(path) if path else None
        self.max_bytes, self.keep = max_bytes, keep
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
                line = (json.dumps(d, ensure_ascii=False) + "\n").encode("utf-8")
                try:
                    size = self.path.stat().st_size
                except OSError:
                    size = 0
                if size and size + len(line) > self.max_bytes:
                    rotate_file(self.path, self.keep)
                with self.path.open("ab") as f:
                    f.write(line)
        return d

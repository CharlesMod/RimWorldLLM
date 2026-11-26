"""Which size of model is serving: "small" or "large".

The engine reports the model it serves in its status (``model``) and, when it knows the parameter
count, ``model_size_class`` ("small" below 8e9 parameters, else "large"), which wins over the name.
Its ``model_tier`` field is
first-run staging, but "starter" counts as small. A plain endpoint is judged by ``cfg.llm.model``.
Without a size class, a model whose id contains any of ``cfg.small_model_patterns`` (case-insensitive, non-alphanumerics
ignored) is small; anything else, and anything unknown, is large (the long-standing behaviour).
The read is cached and never blocks a line: the director starts it as a background task.
"""
from __future__ import annotations

import logging
import re
from typing import Callable

import httpx

from .llm import engine_status_url

log = logging.getLogger(__name__)

REREAD_S = 60.0


def _norm(s: str) -> str:
    return re.sub(r"[^a-z0-9]", "", s.lower())


def classify(model: str, patterns, staging: str = "", size_class: str = "") -> str:
    """The engine's ``model_size_class`` ("small"/"large", from the parameter count) wins over the
    name patterns; any other value (absent, "") falls back to them. A starter stage is small."""
    if (staging or "").strip().lower() == "starter":
        return "small"
    sc = (size_class or "").strip().lower()
    if sc in ("small", "large"):
        return sc
    m = _norm(model or "")
    return "small" if m and any(_norm(p) and _norm(p) in m for p in patterns) else "large"


class ModelTier:
    def __init__(self, cfg, clock: Callable[[], float]):
        self.cfg, self.clock = cfg, clock
        self.model = ""
        self.staging = ""
        self.size_class = ""
        self._at: float | None = None
        self._busy = False

    @property
    def tier(self) -> str:
        return classify(self.model or self.cfg.llm.model, self.cfg.small_model_patterns, self.staging, self.size_class)

    @property
    def small(self) -> bool:
        return self.tier == "small"

    def due(self) -> bool:
        return not self._busy and (self._at is None or self.clock() - self._at >= REREAD_S)

    async def refresh(self, llm) -> None:
        """Re-read the serving model (at most every REREAD_S). Never raises."""
        if not self.due():
            return
        self._busy = True
        try:
            self._at = self.clock()
            is_engine = getattr(llm, "is_engine", None)
            if is_engine is None or not await is_engine():
                self.model, self.staging, self.size_class = "", "", ""      # a plain endpoint: cfg.llm.model
                return
            try:
                r = await llm.http.get(engine_status_url(llm.base), headers=llm._headers(), timeout=3.0)
                d = r.json() if r.status_code == 200 else None
            except (httpx.HTTPError, ValueError):
                d = None
            if isinstance(d, dict):
                self.model = str(d.get("model") or "")
                self.staging = str(d.get("model_tier") or "")
                self.size_class = str(d.get("model_size_class") or "")
            # no answer: keep what we knew
        except Exception as e:      # a tier read must never hurt a line
            log.warning("model tier read failed: %s", e)
        finally:
            self._busy = False


def feature_on(tier: ModelTier, master: bool, small_ok: bool) -> bool:
    """A feature that may be switched off below the large models."""
    return bool(master and (small_ok or not tier.small))

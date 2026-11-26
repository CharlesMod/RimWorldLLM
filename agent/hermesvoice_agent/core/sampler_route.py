"""Experiment hook (cfg.aim_sampler): route one call kind to a named engine sampler profile.

``HttpLLM._sampling`` sends ``X-Hermes-Sampler: structured`` for the intent pass. A client made here sends the named
profile instead, so the engine applies the game profile's sampler of that name to the aim-carrying intent pass. It
shares the original's config, call log and http client; core/llm.py is untouched.
"""
from __future__ import annotations

from .llm import HttpLLM, LLMClient, sampler_fields


class NamedSamplerLLM(HttpLLM):
    def __init__(self, base: HttpLLM, name: str):
        super().__init__(base.cfg, base.calllog, base.http)
        self._base = base
        self.sampler_name = name
        self._engine = base._engine

    async def is_engine(self) -> bool:
        eng = await self._base.is_engine()          # one detection, shared
        self._engine = eng
        return eng

    async def _sampling(self, kind: str, grammar: str | None, json_schema: dict | None) -> tuple[dict, dict]:
        eng = await self.is_engine()
        fields = sampler_fields(self.cfg, engine=eng, kind=kind)
        return fields, ({"X-Hermes-Sampler": self.sampler_name} if eng else {})


def route(llm: LLMClient, name: str) -> LLMClient | None:
    """A client that sends the named sampler, or None when the name is empty or ``llm`` is not an HttpLLM."""
    if not name or not isinstance(llm, HttpLLM):
        return None
    if isinstance(llm, NamedSamplerLLM) and llm.sampler_name == name:
        return llm
    return NamedSamplerLLM(llm, name)

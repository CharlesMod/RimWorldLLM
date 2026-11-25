"""Generic mind building: ask the adapter for the fixed bio and voice card, optionally write the
voice card's example lines with the LLM once. Game-specific templates live in the adapter."""
from __future__ import annotations

from .adapter import GameAdapter
from .llm import LLMClient
from .minds import Mind


async def llm_example_lines(llm: LLMClient, adapter: GameAdapter, mind: Mind, slot: int | None = 0) -> list[str] | None:
    """Optional pass: 3 example lines, written once and saved in the mind."""
    prompt = adapter.voice_example_prompt(mind.fixed.text, mind.voice.register_line, "; ".join(mind.voice.tics))
    try:
        c = await llm.complete([("user", prompt)], slot=slot, kind="voice_card",
                               max_tokens=120, stop=[], actor=mind.actor_id)
    except Exception:
        return None
    lines = [l.strip().strip('"').lstrip("-* ").strip() for l in c.text.splitlines() if l.strip()]
    lines = [l for l in lines if 2 <= len(l.split()) <= 30][:3]
    return lines if len(lines) == 3 else None


async def ensure_mind_built(mind: Mind, snap: dict, names: dict[int, str], adapter: GameAdapter,
                            llm: LLMClient | None = None, use_llm: bool = False) -> bool:
    """Create or refresh the fixed bio and voice card. Returns True if anything changed."""
    changed = False
    mind.name = adapter.name_info(adapter.snapshot_name(snap)).short or mind.name
    h = adapter.facts_hash(snap)
    if not mind.fixed.text or mind.fixed.facts_hash != h:
        mind.fixed = adapter.build_fixed_bio(snap, names)
        changed = True
    if not mind.voice.examples and not mind.voice.edited_by_player:
        mind.voice = adapter.build_voice_card(snap)
        if use_llm and llm is not None:
            ex = await llm_example_lines(llm, adapter, mind)
            if ex:
                mind.voice.examples, mind.voice.llm_examples = ex, True
        changed = True
    return changed

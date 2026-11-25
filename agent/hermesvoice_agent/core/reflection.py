"""Reflection (SPEC §5): one structured LLM call rewrites an actor's evolving bio, wants and relations
and merges memories. It is the only place block C changes.

The wording of the prompt comes from the adapter (``reflection_system`` / ``reflection_request``); this
module supplies the facts, the JSON schema, the parsing and the application to the mind.
"""
from __future__ import annotations

import json
import re

from pydantic import BaseModel, Field, ValidationError

from .llm import LLMClient, LLMError, Messages, strip_thought
from .memory import weight_sorted
from . import gamedata as gd
from .minds import Memory, Mind
from .sanitize import prompt_cap_words, sanitize_prose

MAX_PROMPT_MEMORIES = 24
MAX_BIO_WORDS = 90
MAX_WANT_WORDS = 20
MAX_LINE_WORDS = 30

REFLECT_SCHEMA: dict = {
    "type": "object",
    "properties": {
        "bio": {"type": "string"},
        "wants": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 3},
        "relations": {"type": "array", "items": {
            "type": "object",
            "properties": {"who": {"type": "string"}, "feeling": {"type": "string"}, "history": {"type": "string"}},
            "required": ["who", "feeling", "history"]}},
        "merge": {"type": "array", "items": {
            "type": "object",
            "properties": {"ids": {"type": "array", "items": {"type": "string"}, "minItems": 2},
                           "summary": {"type": "string"}},
            "required": ["ids", "summary"]}},
    },
    "required": ["bio", "wants", "relations", "merge"],
}


class ReflectRelation(BaseModel):
    who: str
    feeling: str = ""
    history: str = ""


class ReflectMerge(BaseModel):
    ids: list[str]
    summary: str


class ReflectResult(BaseModel):
    bio: str
    wants: list[str] = Field(default_factory=list)
    relations: list[ReflectRelation] = Field(default_factory=list)
    merge: list[ReflectMerge] = Field(default_factory=list)


def reflection_due(mind: Mind, now: int, day: int, importance: float, every_days: float) -> bool:
    """Unreflected importance passed the threshold, or the interval ran out with something new to reflect on."""
    if mind.unreflected_importance <= 0:
        return False
    if mind.unreflected_importance >= importance:
        return True
    return now - mind.last_reflect_time >= every_days * day


def reflection_messages(adapter, mind: Mind, names: dict[int, str], now: int, day: int) -> Messages:
    mems = weight_sorted(mind, now, day)[:MAX_PROMPT_MEMORIES]
    mems.sort(key=lambda m: (m.game, m.id))
    mem_lines = "\n".join(f"{m.id}{' [pinned]' if m.pinned else ''}: {gd.clean(m.text, 'event')}" for m in mems) or "(none)"
    rels = sorted(mind.relations.values(), key=lambda r: -abs(r.opinion or 0))[:8]
    rel_lines = "\n".join(
        f"- {gd.name(r.name)}" + (f" (opinion {r.opinion:+d})" if r.opinion is not None else "") +
        (f": {gd.clean(r.feeling, 'line')}" if r.feeling else "") for r in rels) or "(nobody yet)"
    bio = gd.clean(mind.fixed.text, "line", max_len=600, multiline=True)
    becoming = gd.clean(mind.evolving_bio or "(nothing yet)", "desc")
    wants = "; ".join(gd.clean(w, "label") for w in mind.wants) or "none"
    user = (f"=== WHO YOU ARE ===\n{gd.data_section('bio', bio)}"
            f"=== WHO YOU ARE BECOMING NOW ===\n{gd.data_section('character', f'Becoming: {becoming}' + chr(10) + f'Wants: {wants}')}"
            f"=== WHAT YOU REMEMBER (id: text) ===\n{gd.data_section('memories', mem_lines)}"
            f"=== THE PEOPLE YOU KNOW ===\n{gd.data_section('people', rel_lines)}"
            f"=== REQUEST ===\n{adapter.reflection_request}")
    return [("system", adapter.reflection_system + "\n\n" + gd.DATA_RULE), ("user", user)]


def parse_result(text: str) -> ReflectResult | None:
    t = strip_thought(text).strip()
    a, b = t.find("{"), t.rfind("}")
    if a < 0 or b <= a:
        return None
    try:
        return ReflectResult.model_validate(json.loads(t[a:b + 1]))
    except (ValueError, ValidationError):
        return None


async def run_reflection(llm: LLMClient, adapter, mind: Mind, names: dict[int, str], *, slot: int, now: int,
                         day: int, max_tokens: int = 500, name_tokens: dict[int, set[str]] | None = None,
                         common_words: set[str] | None = None) -> ReflectResult | None:
    """The one call. None if the model failed, was malformed or wrote an ungrounded bio."""
    msgs = reflection_messages(adapter, mind, names, now, day)
    try:
        c = await llm.complete(msgs, slot=slot, kind="reflect", max_tokens=max_tokens, stop=[],
                               temperature=0.7, actor=mind.actor_id, json_schema=REFLECT_SCHEMA)
    except LLMError:
        return None
    res = parse_result(c.text)
    if res is None:
        return None
    toks = name_tokens or {}
    all_names = set().union(*toks.values()) if toks else set()
    s = sanitize_prose(res.bio, known_names=all_names, all_names=all_names,
                       prompt_words=prompt_cap_words(msgs[1][1]), common_words=common_words,
                       speaker_names=toks.get(mind.actor_id, set()), min_words=4, max_words=MAX_BIO_WORDS)
    if not s.ok:
        return None
    res.bio = s.text
    return res


def _clean(t: str, max_words: int) -> str:
    return " ".join(" ".join(t.split()).split()[:max_words]).strip()


def apply_reflection(mind: Mind, res: ReflectResult, now: int, name_ids: dict[str, int]) -> dict:
    """Write a reflection into the mind: new bio (as a diff), wants, relation lines, merged memories.
    Resets the unreflected counter and refreshes block C's relations digest."""
    out = {"bio": res.bio, "wants": [], "relations": 0, "merged": 0}
    mind.add_bio_diff(now, res.bio)
    wants = [w for w in (_clean(w, MAX_WANT_WORDS) for w in res.wants) if w][:3]
    if wants:
        mind.wants = wants
    out["wants"] = list(mind.wants)
    for r in res.relations:
        pid = name_ids.get(r.who.strip().lower())
        if pid is None or pid == mind.actor_id:
            continue
        rel = mind.relations.setdefault(str(pid), _new_relation(r.who.strip()))
        if r.feeling.strip():
            rel.feeling = _clean(r.feeling, MAX_LINE_WORDS)
        if r.history.strip():
            rel.history = _clean(r.history, MAX_LINE_WORDS)
        rel.updated_time = now
        out["relations"] += 1
    by_id = {m.id: m for m in mind.memories}
    used: set[str] = set()
    for mg in res.merge:
        ids = list(dict.fromkeys(mg.ids))
        ms = [by_id[i] for i in ids if i in by_id]
        text = _clean(mg.summary, 40)
        if len(ms) < 2 or len(ms) != len(ids) or len(text.split()) < 3 or used & set(ids):
            continue
        if any(m.pinned for m in ms):
            continue                                  # deaths, betrayals and firsts are never folded away
        used |= set(ids)
        for m in ms:
            mind.memories.remove(m)
        mind.memories.append(summary_memory(mind, ms, text))
        out["merged"] += len(ms)
    mind.unreflected_importance = 0.0
    mind.last_reflect_time = now
    mind.freeze_relations()
    return out


def _new_relation(name: str):
    from .minds import Relation
    return Relation(name=name)


def summary_memory(mind: Mind, ms: list[Memory], text: str) -> Memory:
    vis = "private" if any(m.visibility == "private" for m in ms) else \
          "told" if any(m.visibility == "told" for m in ms) else "public"
    told_to = sorted({n for m in ms for n in m.told_to})
    return Memory(id=mind.next_mem_id(), game=max(m.game for m in ms), text=text,
                  confidence=min(m.confidence for m in ms), visibility=vis, told_to=told_to,
                  importance=max(m.importance for m in ms) * 0.9,
                  people=sorted({p for m in ms for p in m.people}),
                  etype=ms[0].etype if len({m.etype for m in ms}) == 1 else "summary",
                  summary_of=sum(max(1, m.summary_of) for m in ms))

"""Per-instance texts (artwork cards, grave inscriptions, book blurbs, social and combat log lines).

The mod queues one text per thing (GET /text/queue, most important first, each with a context snapshot taken when the thing
was made) and shows the game's own text until ours arrives (POST /text/fill). fill_once is the idle job core/idle.py runs
last: queue -> a batch of up to three items of one kind -> one structured LLM call -> clean each text -> store.
Backoff is reported to the director through ``d.text_fill_wait_s``; ``d.text_queue_live`` tells core the queue answered.
"""
from __future__ import annotations

import json
import logging
import re
from typing import Any

from ...core import agegate
from ...core import gamedata as gd
from ...core.bridge import BridgeError
from ...core.llm import LLMError

log = logging.getLogger("rimworldllm")

BATCH_MAX = 3
QUEUE_N = 4
WAIT_OK_S = 10.0        # after a fill: the next call may come this soon
WAIT_EMPTY_S = 30.0     # the queue was empty
WAIT_UNSUPPORTED_S = 300.0   # an older mod answered 404
WAIT_ERROR_S = 30.0

SYSTEM = (
    "You write short texts for a RimWorld colony game: the words on an artwork's card, a gravestone, a book's blurb, or "
    "one line in the social and combat log. Each text belongs to ONE specific thing, so build it from that thing's "
    "context: who made it, what happened, what the weather was doing. Be specific and wry, in dry RimWorld humour, where "
    "the context allows it, and plain where it does not. Never mean about real people. Plain prose only: no markup, no "
    "quotation marks around the text, no emoji, no lists. Follow each item's length limits. A sample of the game's own "
    "text is given only to show length and register: write something new, never copy it.\n"
    "Names: in social and combat lines never write a literal name; refer to people only with the item's allowed "
    "[TOKENS], written exactly as given, with square brackets, and use no other square brackets. Where an item allows no "
    "tokens, use no square brackets at all. In art, grave and book texts the names in the context may be used.\n"
    "Social lines are friendly and all-ages, always: nothing romantic or sexual, ever.\n"
    "Reply with one JSON array: one object per item, {\"id\": the item's id, \"text\": ..., \"title\": ... only when the "
    "item asks for a title}.\n\n" + gd.DATA_RULE)

MINOR_LINE = ("A child or teenager is involved in this one: keep it innocent and wholesome, with nothing romantic, "
              "sexual or frightening about them.")

_BAD_CHARS = re.compile(r"[<>{}]|\(\*")
_QUOTES = "\"'`“”‘’«»"


def _val(v: Any, depth: int = 0) -> str:
    """One context value as readable text: strings cleaned (they are game text), lists joined, dicts as k=v."""
    if v is None or v == "" or v == [] or v == {}:
        return ""
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, dict):
        parts = [f"{k}={s}" for k, x in v.items() if (s := _val(x, depth + 1))]
        return "; ".join(parts) if depth else ", ".join(parts)
    if isinstance(v, (list, tuple)):
        return ", ".join(s for x in v[:8] if (s := _val(x, depth + 1)))
    return gd.clean(v, "line", max_len=160)


def context_lines(ctx: Any) -> str:
    if not isinstance(ctx, dict):
        return gd.clean(ctx, "line") if ctx else "(nothing)"
    out = [f"{gd.clean(k, 'label', max_len=30)}: {s}" for k, v in ctx.items() if (s := _val(v))]
    return "\n".join(out) or "(nothing)"


def batch(items: list[dict]) -> list[dict]:
    """The first item plus up to two more of the same kind (the queue arrives sorted by priority)."""
    if not items:
        return []
    kind = items[0].get("kind")
    return [i for i in items if i.get("kind") == kind][:BATCH_MAX]


def _tokens(item: dict) -> list[str]:
    return [t.strip("[] ") for t in item.get("tokens") or [] if str(t).strip("[] ")]


def _wants_title(item: dict) -> bool:
    return "title" in (item.get("fields") or [])


def _minor(item: dict) -> bool:
    return bool((item.get("context") or {}).get("minor_involved"))


def messages(items: list[dict]) -> list[tuple[str, str]]:
    blocks = []
    for n, it in enumerate(items, 1):
        toks = _tokens(it)
        title = _wants_title(it)
        body = [f"id: {gd.clean(it.get('id', ''), 'label', max_len=80)}",
                f"kind: {gd.clean(it.get('kind', ''), 'label', max_len=20)}",
                f"what this text is: {gd.clean(it.get('about', ''), 'line', max_len=300)}",
                f"length: {int(it.get('min_len', 0))} to {int(it.get('max_len', 300))} characters"
                + (f"; title up to {int(it.get('title_max', 60))} characters" if title else "; no title"),
                "allowed tokens: " + (", ".join(f"[{t}]" for t in toks) if toks else "none (no square brackets at all)")]
        if _minor(it):
            body.append(MINOR_LINE)
        body.append("context:\n" + context_lines(it.get("context")))
        ex = f"sample of the game's own text (length and register only): {gd.clean(it.get('example', ''), 'line', max_len=400)}"
        if title and it.get("example_title"):
            ex += f"\nsample title: {gd.clean(it['example_title'], 'line', max_len=80)}"
        body.append(ex)
        blocks.append(f"=== ITEM {n} ===\n" + gd.data_section("item", "\n".join(body)))
    req = "=== REQUEST ===\nWrite a new text for each item, specific to its context. Reply with the JSON array only.\n"
    return [("system", SYSTEM), ("user", "".join(blocks) + req)]


def schema(items: list[dict]) -> dict:
    text: dict[str, Any] = {"type": "string",
                            "minLength": min(int(i.get("min_len", 1)) for i in items),
                            "maxLength": max(int(i.get("max_len", 300)) for i in items)}
    props: dict[str, Any] = {"id": {"type": "string", "enum": [i["id"] for i in items]}, "text": text}
    if any(_wants_title(i) for i in items):
        props["title"] = {"type": "string", "maxLength": max(int(i.get("title_max", 60)) for i in items)}
    return {"type": "array", "minItems": 1, "maxItems": len(items),
            "items": {"type": "object", "properties": props, "required": ["id", "text"]}}


def _tidy(s: Any) -> str:
    t = re.sub(r"\s+", " ", str(s or "")).strip()
    while len(t) >= 2 and t[0] in _QUOTES and t[-1] in _QUOTES:
        t = t[1:-1].strip()
    return t


def _brackets_ok(t: str, tokens: list[str]) -> bool:
    for tok in tokens:
        t = t.replace(f"[{tok}]", "")
    return "[" not in t and "]" not in t


def clean(item: dict, obj: Any) -> tuple[str, str | None] | None:
    """(text, title) ready to store, or None when the text must not be used."""
    if not isinstance(obj, dict):
        return None
    toks = _tokens(item)
    text = _tidy(obj.get("text"))
    if not int(item.get("min_len", 0)) <= len(text) <= int(item.get("max_len", 300)):
        return None
    title = None
    if _wants_title(item):
        title = _tidy(obj.get("title"))
        if not title or len(title) > int(item.get("title_max", 60)):
            return None
    for t in (text, title or ""):
        if _BAD_CHARS.search(t) or not _brackets_ok(t, toks):
            return None
    if (item.get("kind") == "social" or _minor(item)) and any(agegate.romantic(t) for t in (text, title or "")):
        return None
    return text, title


def _parse(raw: str) -> list[dict]:
    try:
        v = json.loads(raw)
    except ValueError:
        return []
    if isinstance(v, dict):
        v = [v]
    return [o for o in v if isinstance(o, dict)] if isinstance(v, list) else []


async def fill_once(d, slot: int) -> int:
    """One pass: fetch, write one batch, store. Returns the number of texts stored. Sets d.text_fill_wait_s."""
    d.text_fill_wait_s = WAIT_OK_S
    try:
        q = await d.bridge.text_queue(QUEUE_N)
    except BridgeError as e:
        if e.status == 404:
            d.text_queue_live = False
            d.text_fill_wait_s = WAIT_UNSUPPORTED_S
        else:
            log.warning("text queue read failed: %s", e)
            d.text_fill_wait_s = WAIT_ERROR_S
        return 0
    d.text_queue_live = True
    tried = d.__dict__.setdefault("text_tried", set())      # ids written once are never asked for again this session
    if len(tried) > 2000:
        tried.clear()
    items = [i for i in (q.get("items") or []) if isinstance(i, dict) and i.get("id") and i["id"] not in tried]
    group = batch(items)
    if not group:
        d.text_fill_wait_s = WAIT_EMPTY_S
        return 0
    max_tokens = min(1500, sum(int(i.get("max_len", 300)) // 3 + 64 for i in group))
    c = await d.llm.complete(messages(group), slot=slot, kind="textfill", max_tokens=max_tokens, stop=[],
                             json_schema=schema(group))        # LLMError goes up: the idle job backs off
    tried.update(i["id"] for i in group)
    by_id = {i["id"]: i for i in group}
    done: set[str] = set()
    stored = 0
    for obj in _parse(c.text):
        item = by_id.get(obj.get("id"))
        if item is None or item["id"] in done:
            continue
        done.add(item["id"])
        got = clean(item, obj)
        if got is None:
            d.dropped.append((-2, f"textfill:{item.get('kind')}:rejected"))
            continue
        try:
            await d.bridge.text_fill(item["id"], got[0], got[1])
            stored += 1
        except BridgeError as e:
            log.debug("text %s not stored: %s", item["id"], e)     # unknown_id, bad_text: move on, never retry the id
    return stored

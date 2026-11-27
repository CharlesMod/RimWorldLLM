"""Ongoing concerns: a few lasting worries per actor (Mind.concerns), alive for days until something resolves them.

Births come from events (the adapter's rule table, ``concern_births``; importance is the game's, never a model call)
and from reflection (one optional new worry). Resolutions come from events (``concern_resolutions``) or from a
reflection that lists the worry's id. Open concerns show as one "Worries:" line in block C and as data in the wants
prompt; a birth is also an inner thought (kind "concern"). A resolution makes the actor speak one payoff line (a
REACTIVE single voice through serve()), writes a chronicle note, and keeps the concern a day for the diary.

Adapter hooks (all optional; without them nothing is born or resolved by events):
  concern_births(event, holders, loved) -> [{"actor", "text" ("{who}" = first of about), "about", "kind",
                                              "hint", "importance"}]
  concern_resolutions(event) -> [(key, what_happened)]   # a concern whose resolve_hint == key is resolved
"""
from __future__ import annotations

from typing import Callable

from . import agegate, inner
from . import gamedata as gd
from .lease import Cls
from .minds import Concern, Mind

MAX_WORDS = 16
MIN_WORDS = 3
WORRIES_SHOWN = 2

# appended to the adapter's reflection request when concerns are on
REFLECT_CONCERN_REQUEST = (
    " Also (optional): concern: one new lasting worry you now hold, first person, under 14 words, or an empty "
    "string; resolved: ids of the worries listed under your worries that are now over (empty list if none).")

REFLECT_CONCERN_PROPS: dict = {
    "concern": {"type": "string"},
    "resolved": {"type": "array", "items": {"type": "string"}},
}


def reflect_schema(base: dict, on: bool) -> dict:
    """REFLECT_SCHEMA plus the two optional concern fields (not required) when concerns are on."""
    if not on:
        return base
    return {**base, "properties": {**base["properties"], **REFLECT_CONCERN_PROPS}}


def open_concerns(mind: Mind) -> list[Concern]:
    return [c for c in mind.concerns if c.status == "open"]


def worry_lines(mind: Mind, limit: int = WORRIES_SHOWN) -> list[str]:
    """Texts for block C, oldest first (stable between concern changes)."""
    return [c.text for c in open_concerns(mind)[:limit]]


def worries_block(mind: Mind) -> str:
    """The reflection prompt's list of open worries, with ids ('' when none)."""
    return "\n".join(f"{c.id}: {gd.clean(c.text, 'line')}" for c in open_concerns(mind))


def add_concern(mind: Mind, text: str, about: list[int], kind: str, hint: str, importance: float, game: int,
                cap: int) -> Concern | None:
    """Open a concern unless it duplicates an open one (same kind and hint, or same text). At the cap the least
    important open concern yields to a more important newcomer (it fades); otherwise the newcomer is dropped."""
    text = " ".join(text.split())
    if not text or cap <= 0:
        return None
    for c in open_concerns(mind):
        if c.text == text or (hint and c.resolve_hint == hint and c.kind == kind):
            return None
    opened = open_concerns(mind)
    if len(opened) >= cap:
        weakest = min(opened, key=lambda c: (c.importance, c.born))
        if importance <= weakest.importance:
            return None
        weakest.status, weakest.closed, weakest.outcome = "faded", game, ""
    mind.concern_counter += 1
    c = Concern(id=f"w{mind.concern_counter}", text=text, about=list(about), kind=kind, born=game, resolve_hint=hint,
                importance=round(float(importance), 2))
    mind.concerns.append(c)
    return c


def close_concern(c: Concern, game: int, status: str, outcome: str = "") -> None:
    c.status, c.closed, c.outcome = status, game, outcome


def fade_old(mind: Mind, game: int, fade_units: int) -> list[Concern]:
    out = []
    for c in open_concerns(mind):
        if game - c.born >= fade_units:
            close_concern(c, game, "faded")
            out.append(c)
    return out


def drop_closed(mind: Mind, game: int, keep_units: int) -> int:
    keep = [c for c in mind.concerns if c.status == "open" or game - c.closed < keep_units]
    n = len(mind.concerns) - len(keep)
    mind.concerns = keep
    return n


def recent_closed(mind: Mind, game: int, keep_units: int) -> list[Concern]:
    """Resolved concerns still on record (for the diary)."""
    return [c for c in mind.concerns if c.status == "resolved" and game - c.closed < keep_units]


def sanitize_concern(text: str, ctx) -> str:
    """A reflection's new worry, cleaned like an aim: '' when it fails the sanitizer or the age gate."""
    from .sanitize import sanitize_prose
    if not text or not text.strip():
        return ""
    s = sanitize_prose(text, known_names=ctx["known"], all_names=ctx["all"], prompt_words=ctx["prompt_words"],
                       common_words=ctx["common_words"], speaker_names=ctx["speaker"], min_words=MIN_WORDS,
                       max_words=MAX_WORDS, involves_minor=ctx["minor"], minor_names=ctx["minor_names"])
    if not s.ok or agegate.violation(s.text, involves_minor=ctx["minor"], minor_names=ctx["minor_names"]):
        return ""
    return s.text


class ConcernEngine:
    """Director-side driver. Holds no state of its own: everything lives in Mind.concerns."""

    def __init__(self, d) -> None:
        self.d = d
        self.born = 0
        self.resolved = 0
        self.faded = 0
        self.payoffs = 0

    @property
    def on(self) -> bool:
        return bool(self.d.cfg.concerns)

    def loved(self, holder: int, other: int) -> bool:
        m = self.d.minds.get(holder)
        r = m.relations.get(str(other)) if m else None
        return bool(r and r.opinion is not None and r.opinion >= self.d.cfg.concern_loved_opinion)

    # ---- events ---------------------------------------------------------
    def on_event(self, e: dict) -> None:
        if not self.on:
            return
        d, ad = self.d, self.d.adapter
        game = int(e.get("game", d.bridge.env.game))
        res = getattr(ad, "concern_resolutions", None)
        if res:
            keys = {k: what for k, what in res(e)}
            if keys:
                for aid, mind in list(d.minds.items()):
                    for c in open_concerns(mind):
                        what = keys.get(c.resolve_hint) if c.resolve_hint else None
                        if what is not None:
                            self.resolve(aid, c, what, game)
        births = getattr(ad, "concern_births", None)
        if births:
            for spec in births(e, sorted(d.minds), self.loved):
                self.birth(spec, game)

    def birth(self, spec: dict, game: int) -> Concern | None:
        d = self.d
        aid = int(spec["actor"])
        mind = d.minds.get(aid)
        if mind is None:
            return None
        about = [int(a) for a in spec.get("about", [])]
        who = gd.name(d.names.get(about[0], "someone")) if about else "someone"
        text = str(spec["text"]).replace("{who}", who)
        minors = d.minor_ids()
        if agegate.violation(text, involves_minor=d.is_minor(aid) or bool(set(about) & minors),
                             minor_names=d.minor_tokens()):
            d.gate.skipped_events += 1
            return None
        c = add_concern(mind, text, about, str(spec.get("kind", "")), str(spec.get("hint", "")),
                        float(spec.get("importance", 0.5)), game, d.cfg.concerns_max)
        if c is None:
            return None
        self.born += 1
        d.dirty.add(aid)
        if d.cfg.inner_monologue:
            inner.add_inner(mind, c.text, "concern", game, d.cfg.inner_stream_keep)
        return c

    # ---- closing --------------------------------------------------------
    def resolve(self, aid: int, c: Concern, what: str, game: int) -> None:
        """Resolved: payoff line, chronicle note, kept a day, then dropped by upkeep."""
        d = self.d
        close_concern(c, game, "resolved", what)
        self.resolved += 1
        d.dirty.add(aid)
        name = gd.name(d.names.get(aid, "Someone"))
        d.chronicle.note(game, f"{name}'s worry ended: {gd.clean(what, 'event')}", 0.3, False)
        if d.cfg.concern_payoff:
            trig = f"Your worry is over: {gd.clean(c.text, 'line')} — {gd.clean(what, 'event')}"
            now = d.now()
            item = d.leases.make_item(aid, "concern_resolved", now, None, cls=Cls.REACTIVE,
                                      payload={"trigger": trig, "concern": c.id}, severity=0.3)
            if d.leases.submit(item, now, d.bridge.env.speed) == "queued":
                self.payoffs += 1

    def upkeep(self) -> None:
        """Daily: fade the old, drop what the diary no longer needs."""
        if not self.on:
            return
        d = self.d
        game = d.bridge.env.game
        fade_u, keep_u = int(d.cfg.concern_fade_days * d.day), int(d.cfg.concern_keep_days * d.day)
        for aid, mind in d.minds.items():
            n = len(fade_old(mind, game, fade_u))
            self.faded += n
            if n or drop_closed(mind, game, keep_u):
                d.dirty.add(aid)

    # ---- reflection -----------------------------------------------------
    def apply_reflection(self, aid: int, res, game: int) -> None:
        if not self.on:
            return
        mind = self.d.minds[aid]
        by_id = {c.id: c for c in open_concerns(mind)}
        for cid in dict.fromkeys(getattr(res, "resolved", [])):
            c = by_id.get(str(cid).strip())
            if c is not None:
                self.resolve(aid, c, "you decided it was behind you", game)
        text = getattr(res, "concern", "") or ""
        if text.strip():
            c = add_concern(mind, text, [], "reflection", "", 0.4, game, self.d.cfg.concerns_max)
            if c is not None:
                self.born += 1
                self.d.dirty.add(aid)
                if self.d.cfg.inner_monologue:
                    inner.add_inner(mind, c.text, "concern", game, self.d.cfg.inner_stream_keep)

    def wants_lines(self, mind: Mind) -> str:
        """Data for the wants prompt ('' when none or off)."""
        if not self.on:
            return ""
        return "\n".join(f"- {gd.clean(c.text, 'line')}" for c in open_concerns(mind))


Loved = Callable[[int, int], bool]

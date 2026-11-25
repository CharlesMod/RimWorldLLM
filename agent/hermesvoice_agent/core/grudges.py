"""Grudge and make-up loop. An insult (a spoken one the game applied, or a vanilla Insult/Slight or social fight)
gives the victim a grudge against the offender. Phases run on game time, with a real-time floor (play clock) on each so 3x and 4x do not rush them:

  avoid     the victim keeps clear of the offender (a direct /social_action avoid, no model call)
  cooldown  nothing happens for ``grudge_cooldown_days``
  resolve   one of them walks over (seek_out, again a direct /social_action) to apologise, make up or confront;
            the rule is deterministic, from traits and opinion (``decide``)
  outcome   the talk that follows closes the loop: made up (memory both sides, chronicle note) or escalated
            (renews once, then fades); a talk that says neither retries later, up to ``grudge_max_attempts``

The offender may carry a guilt concern (core/concerns.py) until the grudge ends. The mod does not carry the seek_out
topic into the chat, so the agent remembers the pending reunion and tells both actors what the talk is about in the
conversation trigger. Nothing here runs when the mod's wants are off or effects are off, and never through the
tool loop. Nothing romantic ever: the topics are fixed words and minors take part like anyone else.

Adapter hooks: ``social_offence(event) -> (offender, victim, verb) | None``, ``offence_intents() -> set``,
``grudge_style(snapshot) -> "kind" | "abrasive" | ""``, ``conversation_pair(event)`` (already there).
"""
from __future__ import annotations

import logging
from dataclasses import dataclass

from . import gamedata as gd
from .bridge import BridgeError
from .minds import Grudge, Memory, Mind

log = logging.getLogger(__name__)

APOLOGISE, MAKE_UP, CONFRONT = "apologise", "make up", "confront"
REUNION_WINDOW_DAYS = 0.5       # a seek_out's chat is expected within this long, else the attempt came to nothing
GUILT_TEXT = "I wish I had not treated {who} that way."
POSITIVE = {"kind", "comfort"}
NEGATIVE = {"insult", "slight"}


def decide(opinion: int | None, holder_style: str, offender_style: str, offender_guilty: bool,
           recover: int) -> tuple[str, str]:
    """(who walks: "offender" | "holder", approach). Deterministic.
    A guilty, not abrasive offender apologises; else a kind holder, or one whose opinion of the offender recovered,
    makes up; otherwise the holder confronts (words only)."""
    if offender_guilty and offender_style != "abrasive":
        return "offender", APOLOGISE
    if holder_style == "kind" or (opinion is not None and opinion >= recover and holder_style != "abrasive"):
        return "holder", MAKE_UP
    return "holder", CONFRONT


def outcome_of(intents: list[str]) -> str:
    """"escalated" if anyone insulted or slighted, else "made_up" if anyone was kind or comforting, else "neutral"."""
    if any(i in NEGATIVE for i in intents):
        return "escalated"
    if any(i in POSITIVE for i in intents):
        return "made_up"
    return "neutral"


@dataclass
class Reunion:
    holder: int          # who holds the grudge
    grudge: str
    mover: int
    other: int
    approach: str
    expires: int
    cause: str = ""


class GrudgeEngine:
    def __init__(self, d) -> None:
        self.d = d
        self.pending: dict[frozenset, Reunion] = {}
        self.busy: set[tuple[int, str]] = set()
        self.started = 0
        self.made_up = 0
        self.escalated = 0
        self.faded = 0
        self.seeks = 0
        self.trace: list[dict] = []
        # Play-clock marks per (holder, grudge id): not saved. A grudge loaded from a save has none and is held by game
        # time only; every phase that starts in this session also waits for its real-time floor.
        self.real_born: dict[tuple[int, str], float] = {}
        self.real_try: dict[tuple[int, str], float] = {}

    # ---- gates ----------------------------------------------------------
    @property
    def on(self) -> bool:
        return bool(self.d.cfg.grudges)

    def can_act(self) -> bool:
        """Social actions need the mod's wants and effects on; this loop never goes through the tool backend."""
        d = self.d
        return self.on and d.bridge_cfg.get("wants_enabled") is not False and d.bridge_cfg.get("effects") != "off"

    def _note(self, **kw) -> None:
        self.trace.append(kw)
        del self.trace[:-100]

    def _units(self, days: float) -> int:
        return int(days * self.d.day)

    def _real(self) -> float:
        return self.d.now().real

    def _mark(self, holder: int, g: Grudge, born: bool, floor_s: float) -> None:
        r = self._real()
        if born:
            self.real_born[(holder, g.id)] = r
        self.real_try[(holder, g.id)] = r + floor_s

    def _real_ok(self, mark: dict, holder: int, g: Grudge, plus: float = 0.0) -> bool:
        m = mark.get((holder, g.id))
        return m is None or self._real() >= m + plus

    # ---- offences -------------------------------------------------------
    def on_event(self, e: dict) -> None:
        if not self.can_act():
            return
        f = getattr(self.d.adapter, "social_offence", None)
        off = f(e) if f else None
        if off:
            n = self.d.names
            self.offence(off[0], off[1], f"{n.get(off[0], 'someone')} {off[2]} {n.get(off[1], 'someone')}",
                         int(e.get("game", self.d.bridge.env.game)))

    def on_say(self, speaker: int, listener: int | None, intent: str, applied) -> None:
        """An applied spoken offence from speaker to listener."""
        if not applied or listener is None or not self.can_act():
            return
        f = getattr(self.d.adapter, "offence_intents", None)
        if not f or intent not in f():
            return
        d = self.d
        self.offence(speaker, listener, f"{d.names.get(speaker, 'someone')} insulted {d.names.get(listener, 'someone')}",
                     d.bridge.env.game)

    def offence(self, offender: int, victim: int, cause: str, game: int) -> Grudge | None:
        d = self.d
        if offender == victim or offender not in d.minds or victim not in d.minds or not self.can_act():
            return None
        if self._reunion_between(offender, victim, game):
            return None                       # words during a make-up talk belong to its outcome, not a new grudge
        mind = d.minds[victim]
        if any(g.against == offender and g.phase != "done" for g in mind.grudges):
            return None                       # already holding one against them
        g = Grudge(id=f"g{offender}-{game}", against=offender, cause=gd.clean(cause, "event"), born=game,
                   next_try=game + self._units(d.cfg.grudge_cooldown_days))
        mind.grudges.append(g)
        mind.grudges = mind.grudges[-6:]
        self._mark(victim, g, True, d.cfg.grudge_cooldown_floor_real_s)
        self.started += 1
        d.dirty.add(victim)
        self._note(kind="grudge", holder=victim, against=offender, game=game)
        self._spawn(self._begin(victim, g))
        return g

    def _spawn(self, coro) -> None:
        self.d._spawn(coro)

    async def _begin(self, holder: int, g: Grudge) -> None:
        """Avoid at once; the offender's guilt (unless abrasive) becomes a concern until the grudge ends."""
        d = self.d
        await self._act(holder, "avoid", g.against, "")
        if d.cfg.concerns and g.renewals == 0:
            style = ""
            try:
                style = d.adapter.grudge_style(await d.bridge.actor(g.against))
            except (BridgeError, AttributeError):
                pass
            if style != "abrasive":
                d.concerns.birth({"actor": g.against, "text": GUILT_TEXT, "about": [holder], "kind": "guilt",
                                  "hint": f"grudge:{g.id}", "importance": 0.5}, g.born)

    async def _act(self, actor: int, action: str, target: int, topic: str) -> bool:
        try:
            await self.d.bridge.social_action(actor, action, target, topic)
            return True
        except BridgeError as e:
            self._note(kind="refused", actor=actor, action=action, code=getattr(e, "code", ""))
            return False

    # ---- time -----------------------------------------------------------
    def advance(self) -> None:
        if not self.on:
            return
        d = self.d
        game = d.bridge.env.game
        for pair in [p for p, r in self.pending.items() if game >= r.expires]:
            self.pending.pop(pair, None)
        if not self.can_act():
            return
        for aid, mind in d.minds.items():
            for g in mind.grudges:
                if g.phase == "done":
                    continue
                if g.phase == "avoid" and game >= g.born + self._units(d.cfg.grudge_avoid_days) \
                        and self._real_ok(self.real_born, aid, g, d.cfg.grudge_avoid_floor_real_s):
                    g.phase = "cooldown"
                    d.dirty.add(aid)
                if g.phase == "cooldown" and game >= g.next_try and (aid, g.id) not in self.busy \
                        and self._real_ok(self.real_try, aid, g):
                    g.phase = "resolve"
                if g.phase == "resolve" and game >= g.next_try and (aid, g.id) not in self.busy \
                        and self._real_ok(self.real_try, aid, g):
                    if g.attempts >= d.cfg.grudge_max_attempts:
                        self._end(aid, g, "faded", game)
                        continue
                    g.attempts += 1
                    g.next_try = game + self._units(d.cfg.grudge_retry_days)
                    self._mark(aid, g, False, d.cfg.grudge_retry_floor_real_s)
                    d.dirty.add(aid)
                    self.busy.add((aid, g.id))
                    self._spawn(self._resolve(aid, g))

    async def _resolve(self, holder: int, g: Grudge) -> None:
        d = self.d
        try:
            other = g.against
            if other not in d.minds:
                self._end(holder, g, "faded", d.bridge.env.game)
                return
            try:
                snap_h, snap_o = await d.bridge.actor(holder), await d.bridge.actor(other)
            except BridgeError:
                return                         # try again at next_try
            ad = d.adapter
            opinion = dict(ad.relations(snap_h)).get(other)
            guilty = any(c.resolve_hint == f"grudge:{g.id}" for c in d.minds[other].concerns if c.status == "open")
            who, approach = decide(opinion, ad.grudge_style(snap_h), ad.grudge_style(snap_o), guilty,
                                   d.cfg.grudge_recover_opinion)
            mover, target = (other, holder) if who == "offender" else (holder, other)
            if not self.can_act():
                return
            ok = await self._act(mover, "seek_out", target, approach)
            self._note(kind="seek", holder=holder, mover=mover, target=target, approach=approach, ok=ok)
            if not ok:
                return
            self.seeks += 1
            game = d.bridge.env.game
            g.approach, g.mover = approach, mover
            self.pending[frozenset((mover, target))] = Reunion(
                holder, g.id, mover, target, approach, game + self._units(REUNION_WINDOW_DAYS), g.cause)
            d.dirty.add(holder)
        finally:
            self.busy.discard((holder, g.id))

    # ---- the talk -------------------------------------------------------
    def _reunion_between(self, a: int, b: int, game: int) -> Reunion | None:
        r = self.pending.get(frozenset((a, b)))
        return r if r is not None and game < r.expires else None

    def reunion_for(self, e: dict) -> Reunion | None:
        """The pending reunion this event is (a social event between the pair, in the window)."""
        if not self.pending or not self.can_act():
            return None
        pair = self.d.adapter.conversation_pair(e)
        if not pair:
            return None
        return self._reunion_between(pair[0], pair[1], int(e.get("game", self.d.bridge.env.game)))

    def note_for(self, r: Reunion) -> str:
        """What the talk is about, for both actors' trigger (game text: cleaned by the caller's data wrapping)."""
        n = self.d.names
        m, o = gd.name(n.get(r.mover, "someone")), gd.name(n.get(r.other, "someone"))
        cause = gd.clean(r.cause, "event")
        if r.approach == APOLOGISE:
            return f"{m} has come to apologise to {o}. The quarrel: {cause}"
        if r.approach == MAKE_UP:
            return f"{m} has come to make up with {o}. The quarrel: {cause}"
        return f"{m} has come to confront {o} with words only. The quarrel: {cause}"

    def on_conversation_done(self, conv, game: int) -> None:
        if not conv.reunion:
            return
        holder_id, _, gid = conv.reunion.partition("|")
        holder = int(holder_id)
        mind = self.d.minds.get(holder)
        g = next((x for x in (mind.grudges if mind else []) if x.id == gid), None)
        if g is None or g.phase == "done":
            return
        self.pending.pop(frozenset((conv.a, conv.b)), None)
        out = outcome_of([t.intent for t in conv.turns])
        self._note(kind="outcome", holder=holder, grudge=gid, outcome=out)
        if out == "made_up":
            self._make_up(holder, g, game)
        elif out == "escalated":
            self.escalated += 1
            if g.renewals < 1:
                g.renewals += 1
                g.phase, g.born, g.attempts = "avoid", game, 0
                g.next_try = game + self._units(self.d.cfg.grudge_cooldown_days)
                self._mark(holder, g, True, self.d.cfg.grudge_cooldown_floor_real_s)
                self.d.dirty.add(holder)
                self._spawn(self._begin(holder, g))
            else:
                self._end(holder, g, "faded", game)

    def _make_up(self, holder: int, g: Grudge, game: int) -> None:
        d = self.d
        other = g.against
        self.made_up += 1
        for me, them in ((holder, other), (other, holder)):
            m = d.minds.get(me)
            if m is None:
                continue
            m.memories.append(Memory(id=m.next_mem_id(), game=game, source="saw", importance=0.5, people=[me, them],
                                     etype="makeup",
                                     text=f"{gd.name(d.names.get(them, 'They'))} and I made things right after the quarrel."))
            m.unreflected_importance += 0.3
            d.dirty.add(me)
        d.chronicle.note(game, f"{gd.name(d.names.get(holder, 'Someone'))} and {gd.name(d.names.get(other, 'someone'))} "
                               "made up after a quarrel.", 0.3, False)
        self._end(holder, g, "made_up", game)

    def _end(self, holder: int, g: Grudge, how: str, game: int) -> None:
        d = self.d
        g.phase, g.ended, g.closed = "done", how, game
        if how == "faded":
            self.faded += 1
        d.dirty.add(holder)
        om = d.minds.get(g.against)
        if om is not None:                    # the offender's guilt ends with the grudge, quietly
            from .concerns import close_concern
            for c in om.concerns:
                if c.status == "open" and c.resolve_hint == f"grudge:{g.id}":
                    close_concern(c, game, "resolved" if how == "made_up" else "faded", "")
                    d.dirty.add(g.against)

    def upkeep(self) -> None:
        """Daily: finished grudges are dropped after a day."""
        d, game = self.d, self.d.bridge.env.game
        for aid, mind in d.minds.items():
            keep = [g for g in mind.grudges if g.phase != "done" or game - g.closed < d.day]
            if len(keep) != len(mind.grudges):
                mind.grudges = keep
                d.dirty.add(aid)

"""Gathering talk (aliveness item 10): group scenes at meals and parties, eulogies, vows and speeches.

The mod sends a "gathering" event (kind meal | party | wedding | funeral | speech | ritual, a lead, the people there) and a
"gathering_end". Here a scene of up to ``group_max_speakers`` speakers runs on the two actor windows: each turn the next
speaker gets a window (a lease whose prefix is built for that speaker; when a third speaker is due, the window of the one who
spoke longest ago is released and re-leased, which leans on the server's prompt-cache restore), and block F carries every
turn so far as heard speech. Lines go through the director's normal path (voice budget, quotas, expiry, the age gate).
Meals and parties are SOCIAL class (they pause at 4x); ceremonies are REACTIVE. There is no intent pass in a group (the
label is always "none", so no listener-specific effect) and no aim call (it would double the calls of a scene).
"""
from __future__ import annotations

import hashlib
import logging
import re
from dataclasses import dataclass, field

from . import agegate
from . import gamedata as gd
from . import memory
from .bridge import BridgeError
from .conversation import Turn
from .lease import REQUEST_EXPIRY, Cls, Lease, Now, WorkItem
from .memory import keywords
from .minds import Memory
from . import creative, grounding
from .passes import Dropped, Spoken, speak
from .prompt import line_messages

log = logging.getLogger("rimworldllm")

KINDS = ("meal", "party", "wedding", "funeral", "speech", "ritual")
CEREMONIES = frozenset({"wedding", "funeral", "speech", "ritual"})
CEREMONY_CAPS = (3, 3, 2, 1)             # turns at 1x, 2x, 3x, 4x
STORY_CHARS = 170
STORY_REPEAT_DAYS = 1.0
SETTING = {"meal": "a shared meal", "party": "a party", "wedding": "a wedding", "funeral": "a funeral",
           "speech": "a speech", "ritual": "a ritual"}


def scene_class(kind: str) -> Cls:
    return Cls.REACTIVE if kind in CEREMONIES else Cls.SOCIAL


def tier(rate: float) -> int:
    return 0 if rate <= 60 else 1 if rate <= 180 else 2 if rate <= 360 else 3


def turn_cap(kind: str, rate: float, group_turns: list[int], n_speakers: int) -> int:
    """Turns for a scene at this speed. Meals and parties follow ``group_turns`` (0 at 4x: no scene); a ceremony always
    keeps its lead's line and never has more turns than speakers."""
    t = tier(rate)
    base = group_turns[min(t, len(group_turns) - 1)] if group_turns else 0
    if kind in CEREMONIES:
        return min(n_speakers, max(1, min(base, CEREMONY_CAPS[t])))
    return base


@dataclass
class Gathering:
    id: str
    kind: str
    key: str
    lead: int | None
    couple: list[int]
    subject: int | None
    pool: list[int]                      # everyone there that we hold a mind for
    speakers: list[int]                  # speaking order, at most group_max_speakers
    names: dict[int, str]
    label: str = ""
    ideo: str = ""
    room: str = ""
    deceased: str = ""
    story: str = ""
    minor: bool = False                  # a minor is among those there: every speaker carries the minor rule
    turns: list[Turn] = field(default_factory=list)
    ended: str = ""
    cap: int = 0


@dataclass
class GroupState:
    """Per-director bookkeeping and counters (debug view and tests)."""
    seen: set[str] = field(default_factory=set)          # gathering keys already turned into a scene
    over: set[str] = field(default_factory=set)          # keys whose gathering_end arrived
    told: dict[str, int] = field(default_factory=dict)   # story text -> game time it was last told
    started: int = 0
    skipped: int = 0
    lines: int = 0
    rotations: int = 0
    ends: dict[str, int] = field(default_factory=dict)
    log: list[dict] = field(default_factory=list)


# ---- picking ---------------------------------------------------------------------
def _sentences(text: str) -> list[str]:
    return [s.strip() for s in re.split(r"(?<=[.!?])\s+", text) if len(s.strip()) > 20]


def pick_story(d, ids: list[int], now_game: int) -> tuple[str, int | None] | None:
    """A chronicle sentence or lore entry that names people at this gathering: (text, the one it names most, or None).
    Two or more of them beats one; lore names count for a little extra; one told in the last day is not told again."""
    toks = {i: {t.lower() for t in d.name_tokens.get(i, set())} for i in ids}
    cands: list[tuple[int, int, str]] = []                # (score, order, text)
    st = d.chronicle.state
    order = 0
    texts = [e.text for e in st.recent] + [n.text for n in st.pending]
    for t in texts:
        for s in _sentences(t):
            order += 1
            named = {i for i, tk in toks.items() if tk & keywords(s)}
            if named:
                cands.append((2 * len(named), order, s))
    for e in d.lore.entries:
        order += 1
        named = {i for i, tk in toks.items() if tk & keywords(e.text)}
        cands.append((1 + 2 * len(named), order, e.text))
    window = int(STORY_REPEAT_DAYS * d.day)
    cands = [c for c in cands if now_game - d.group.told.get(c[2], -10 ** 9) >= window and c[0] >= 3]
    if not cands:
        return None
    cands.sort(key=lambda c: (-c[0], -c[1]))
    text = gd.clean(cands[0][2], "event", max_len=STORY_CHARS)
    counts = {i: len(tk & keywords(text)) for i, tk in toks.items()}
    best = max(counts, key=lambda i: (counts[i], -i))
    return text, (best if counts[best] else None)


def plan_speakers(kind: str, lead: int | None, couple: list[int], pool: list[int], minds, weights: dict[int, float],
                  limit: int, opener: int | None = None) -> list[int]:
    """Speaking order: a wedding starts with the couple, other ceremonies with their lead, a meal or party with whoever the
    story is about (else the host or the most talkative); the rest follow by talk weight, warmer toward the first first."""
    pool = list(dict.fromkeys(pool))
    first: list[int] = []
    if kind == "wedding":
        first = [c for c in couple if c in pool]
    elif kind in CEREMONIES:
        first = [lead] if lead in pool else []
    elif opener in pool:
        first = [opener]
    elif kind == "party" and lead in pool:
        first = [lead]

    def score(i: int) -> float:
        w = weights.get(i, 1.0)
        if not first:
            return w
        r = minds[i].relations.get(str(first[0])) if i in minds else None
        return w * (1.0 + max(0, (r.opinion or 0) if r else 0) / 100.0)
    rest = sorted((i for i in pool if i not in first), key=lambda i: (-score(i), i))
    return (first + rest)[:max(1, limit)]


def next_speaker(g: Gathering) -> int | None:
    """Ceremonies follow the plan once; a meal cycles through its speakers. Never the same person twice running."""
    i, sp = len(g.turns), g.speakers
    if not sp:
        return None
    if g.kind in CEREMONIES:
        return sp[i] if i < len(sp) else None
    last = g.turns[-1].speaker if g.turns else None
    pick = sp[i % len(sp)]
    if pick == last:
        if len(sp) < 2:
            return None
        pick = sp[(i + 1) % len(sp)]
    return pick


# ---- prompt text -----------------------------------------------------------------
def trigger(g: Gathering, me: int) -> str:
    """The scene sentence for one speaker (block E's "what just happened"; game text, cleaned by the prompt)."""
    n = g.names
    lead = n.get(g.lead, "") if g.lead is not None else ""
    if g.kind == "wedding" and len(g.couple) == 2:
        a, b = (n.get(c, "someone") for c in g.couple)
        t = (f"You are marrying {n.get(g.couple[1 - g.couple.index(me)], 'your partner')}; everyone has gathered." if me in g.couple
             else f"{a} and {b} are getting married, and everyone has gathered.")
    elif g.kind == "funeral":
        t = f"A funeral is being held for {g.deceased or 'someone who died'}." + (f" {lead} leads it." if lead and me != g.lead else "")
        if me == g.lead:
            t += " You are leading it."
    elif g.kind in ("speech", "ritual"):
        what = g.ideo or g.label or SETTING[g.kind]
        t = (f"You are giving {SETTING[g.kind]}: {what}." if me == g.lead
             else f"{lead or 'Someone'} is holding {SETTING[g.kind]}: {what}.")
    elif g.kind == "party":
        t = f"A {g.label or 'party'} is under way, with {', '.join(n[i] for i in g.pool if i != me and i in n)[:90]}."
    else:
        t = (f"You are eating together with {', '.join(n[i] for i in g.pool if i != me and i in n)[:90]}"
             + (f" in the {g.room}." if g.room else "."))
    if g.story and g.kind in ("meal", "party"):
        t += f" A story the group knows: {g.story}"
    return t


def request(g: Gathering, me: int) -> tuple[str, int | None]:
    """(the block G request for this turn, max_tokens or None for the default)."""
    first = not g.turns
    n = g.names
    if g.kind == "wedding":
        if me in g.couple and len(g.turns) < len(g.couple):
            other = gd.name(n.get(g.couple[1 - g.couple.index(me)], ""), "your partner")
            return (f"=== REQUEST ===\nIt is the wedding. Say your vow to {other} aloud: one sincere line, in your own voice.", None)
        return ("=== REQUEST ===\nThe vows have been said. React in one short, warm line, in your own voice.", None)
    if g.kind == "funeral":
        who = gd.name(g.deceased, "the one who died") if g.deceased else "the one who died"
        if me == g.lead and first:
            return (f"=== REQUEST ===\nGive a short eulogy for {who} to everyone gathered, from what you remember of them. "
                    "One or two plain sentences, in your own voice.", None)
        return (f"=== REQUEST ===\nAdd one quiet line of your own about {who}, or answer what was just said. Keep it brief.", None)
    if g.kind in ("speech", "ritual"):
        if me == g.lead and first:
            return ("=== REQUEST ===\nSay one line to the people gathered, about what this occasion is for, in your own voice.", None)
        return ("=== REQUEST ===\nReact in one short line to what was just said, in your own voice.", None)
    if first and g.story:
        return ("=== REQUEST ===\nYou all know the story in the scene. Bring it up to the others in your own words, one short line.", None)
    if first:
        return ("=== REQUEST ===\nSay something to the others that fits the moment. One short line.", None)
    return ("=== REQUEST ===\nReact to what was just said: add a detail, laugh, or answer. One short line.", None)


# ---- creative turns: the eulogy and the vows -------------------------------------------------------------
def ceremony_turn(g: Gathering, sp: int) -> bool:
    """The turns that are written pieces: the lead's eulogy and each of the couple's vows."""
    return ((g.kind == "funeral" and sp == g.lead and not g.turns)
            or (g.kind == "wedding" and sp in g.couple and len(g.turns) < len(g.couple)))


async def details(d, g: Gathering, me: int) -> str:
    """creative_ground: concrete details about the person the speech is about, as a data section ("" when none).
    Deceased: traits, background, a memory the speaker has of them, how they died. Couple: traits of both and what
    they share. Game text, cleaned."""
    mind = d.minds.get(me)
    lines: list[str] = []
    if g.kind == "funeral" and g.deceased:
        name = gd.name(g.deceased, "the one who died")
        did = next((i for i, n in d.names.items() if n == g.deceased), None)
        if did is not None:
            lines += [f"{name}: {x}" for x in grounding.card_lines(await grounding.actor_card(d, did))
                      if not x.startswith("Mood")]
        tok = grounding.tokens_of([g.deceased])
        if mind is not None:
            mine = [m for m in mind.memories if (did is not None and did in m.people) or grounding.mentions(m.text, tok)]
            for m in mine[-2:]:
                lines.append(f"A memory you have of {name}: {gd.clean(m.text, 'event', max_len=200)}")
        news = [e.text for e in d.chronicle.state.recent if grounding.mentions(e.text, tok)][-2:]
        for t in news:
            lines.append(f"What the record says: {gd.clean(t, 'event', max_len=220)}")
    elif g.kind == "wedding" and me in g.couple and len(g.couple) == 2:
        other = g.couple[1 - g.couple.index(me)]
        oname = gd.name(g.names.get(other, ""), "your partner")
        for who, nm in ((me, "You"), (other, oname)):
            lines += [f"{nm}: {x}" for x in grounding.card_lines(await grounding.actor_card(d, who))
                      if not x.startswith("Mood")]
        if mind is not None:
            for m in [m for m in mind.memories if other in m.people and m.visibility != "private"][-2:]:
                lines.append(f"Something you share with {oname}: {gd.clean(m.text, 'event', max_len=200)}")
    if not lines:
        return ""
    return f"=== ABOUT THEM ===\n{gd.data_section('about', chr(10).join(lines))}"


async def _compose_turn(d, g: Gathering, sp: int, mind, lease, req: str, say):
    """One eulogy or vow through creative.compose. With every flag off it is one call to ``say(req)``, as before."""
    kind = "eulogy" if g.kind == "funeral" else "vow"
    base = req
    if creative.on(d, "ground", kind):
        base = (await details(d, g, sp)) + req
    results: list = []

    def with_note(note: str) -> str:
        return base if not note else base.rstrip("\n") + "\n" + note

    async def write(note: str) -> str | None:
        r = await say(with_note(note))
        results.append(r)
        return r.text if isinstance(r, Spoken) else None

    async def plan() -> str | None:
        msgs = line_messages(lease.data["prefix"], [(t.name, t.text) for t in g.turns], mind, g.names[sp], len(g.turns),
                             base.split("=== REQUEST ===")[0] + "=== REQUEST ===\n" + grounding.PLAN_ASK)
        c = await d.llm.complete(msgs, slot=lease.slot, kind="line", max_tokens=grounding.PLAN_TOKENS, stop=["\n"],
                                 actor=sp)
        return c.text

    text, _ = await creative.compose(d, kind=kind, slot=lease.slot, actor=sp,
                                     facts=base.split("=== REQUEST ===")[0], names=set(), write=write, plan=plan,
                                     should_yield=lambda: grounding.other_reactive_waiting(d, sp))
    for r in reversed(results):
        if isinstance(r, Spoken) and r.text == text:
            return r
    return results[-1] if results else Dropped("no_candidate")


def template_memory(g: Gathering, me: int) -> str:
    n = g.names
    first = g.turns[0] if g.turns else None
    last = g.turns[-1] if g.turns else None
    if g.kind == "wedding" and len(g.couple) == 2:
        setting = f"the wedding of {n.get(g.couple[0], 'two of us')} and {n.get(g.couple[1], 'someone')}"
    elif g.kind == "funeral":
        setting = f"the funeral of {g.deceased or 'one of ours'}"
    elif g.kind in ("speech", "ritual"):
        setting = f"{SETTING[g.kind]}" + (f" ({g.ideo or g.label})" if g.ideo or g.label else "")
    else:
        setting = SETTING[g.kind]
    if first is None:
        return f"I was at {setting}."
    who = "I" if first.speaker == me else first.name
    s = f"I was at {setting}. {who} spoke first: \"{first.text}\""
    if last is not first and last.speaker == me:
        s += f" I said: \"{last.text}\""
    return s


# ---- event handling --------------------------------------------------------------
def owns_funeral(d) -> bool:
    """The older "funeral" event is covered by its gathering while gatherings are on: no second reaction."""
    return bool(d.cfg.gatherings) and d.bridge_cfg.get("events", {}).get("gathering") is not False


async def handle(d, e: dict) -> bool:
    """Consume a gathering event (True) or leave it alone (False). A start becomes a queued scene when allowed."""
    t = e.get("type", "")
    if t == "gathering_end":
        d.group.over.add(str((e.get("data") or {}).get("key") or ""))
        return True
    if t != "gathering":
        return False
    gs = d.group
    info = d.adapter.gather_info(e)
    if info is None or not d.cfg.gatherings or d.bridge_cfg.get("events", {}).get("gathering") is False:
        return True
    kind, key = info["kind"], info["key"]
    if key in gs.seen or (kind == "meal" and not d.cfg.meal_talk):
        return True
    cls = scene_class(kind)
    now = d.now()
    ex = REQUEST_EXPIRY[cls]
    age = max(0, now.game - int(e.get("game", now.game)))
    if age > max(ex.units, int(ex.floor_real_s * now.rate)):
        return True                                           # a backlog event
    if cls <= Cls.SOCIAL:
        roll = int(hashlib.sha1(f"gather:{key}".encode()).hexdigest(), 16) % 1000 / 1000
        if roll >= min(1.0, float(d.bridge_cfg.get("talk_frequency", 1.0))):
            return True
    pool = [i for i in info["participants"] if i in d.minds]
    await d._learn_ages(pool)
    if kind == "wedding":
        couple = info["couple"]
        # hard age gate: the vows are romantic, so both of the couple must be known adults, or there is no scene at all
        if len(couple) != 2 or any(c not in d.minds or d.is_minor(c) for c in couple):
            d.gate.skipped_events += 1
            d.gate.note("wedding", couple=couple)
            gs.skipped += 1
            return True
        pool = [i for i in pool if not d.is_minor(i)]          # only adults speak at a wedding
        for c in couple:
            if c not in pool:
                pool.append(c)
    if len(pool) < 2:
        gs.skipped += 1
        return True
    story, opener = None, None
    if kind in ("meal", "party"):
        picked = pick_story(d, pool, now.game)
        if picked:
            story, opener = picked
    lead = info["lead"]
    speakers = plan_speakers(kind, lead, info["couple"], pool, d.minds, d.weights, d.cfg.group_max_speakers, opener)
    names = {i: d.names.get(i, str(i)) for i in pool}
    d._conv_n += 1
    g = Gathering(f"g-{d._conv_n}", kind, key, lead if lead in pool else None, info["couple"], info["subject"], pool, speakers,
                  names, info["label"], info["ideo"], info["room"], info["deceased"], story or "")
    g.minor = any(d.is_minor(i) for i in pool)
    born = Now(now.game - age, now.real - age / max(now.rate, 1.0), now.rate)
    item = d.leases.make_item(speakers[0], "gathering", born, e, cls=cls, payload={"gathering": g})
    if d.leases.submit(item, now, d.bridge.env.speed) == "queued":
        gs.seen.add(key)
    else:
        gs.skipped += 1
    return True


# ---- the scene -------------------------------------------------------------------
async def _window(d, g: Gathering, item: WorkItem, held: dict[int, Lease], spoke: dict[int, int], sp: int) -> Lease | None:
    """The lease for ``sp``: the one it holds, else a free window, else the window of whoever spoke longest ago (never the
    last speaker when another is held)."""
    if sp in held:
        return held[sp]
    now = d.now()
    mk = lambda: d.leases.make_item(sp, "gathering", now, item.event, cls=item.cls)
    lease = d.leases.acquire(mk(), now) if len(held) < len(d.cfg.actor_slots) else None
    if lease is None and held:
        last = g.turns[-1].speaker if g.turns else None
        cands = [a for a in held if a != last] or list(held)
        victim = min(cands, key=lambda a: spoke.get(a, -1))
        d.leases.release(held.pop(victim), "rotated")
        d.group.rotations += 1
        lease = d.leases.acquire(mk(), d.now())
    if lease is not None:
        held[sp] = lease
    return lease


async def run(d, item: WorkItem, lease0: Lease) -> None:
    g: Gathering = item.payload["gathering"]
    gs = d.group
    held: dict[int, Lease] = {g.speakers[0]: lease0}
    ctxs: dict = {}
    spoke: dict[int, int] = {}
    try:
        g.cap = turn_cap(g.kind, d.now().rate, d.cfg.group_turns, len(g.speakers))
        if g.cap <= 0:
            gs.skipped += 1
            return
        gs.started += 1
        last_real = d.now().real
        while not g.ended:
            if g.turns:
                await d._pace(last_real, list(held.values()))
            if any(l.preempted for l in held.values()):
                g.ended = "preempted"
                break
            if g.key in gs.over:
                g.ended = "over"
                break
            g.cap = min(g.cap, turn_cap(g.kind, d.now().rate, d.cfg.group_turns, len(g.speakers)))
            if len(g.turns) >= g.cap:
                g.ended = "turn_cap"
                break
            sp = next_speaker(g)
            if sp is None:
                g.ended = "no_speaker"
                break
            lease = await _window(d, g, item, held, spoke, sp)
            if lease is None:
                g.ended = "no_window"
                break
            if sp not in ctxs or ctxs[sp][0] is not lease:
                ctx = (await d._prepare(sp, trigger(g, sp), item.event, lease))[0]
                ctx.minor_involved = ctx.minor_involved or g.minor
                ctxs[sp] = (lease, ctx)
            ctx = ctxs[sp][1]
            audience = [g.names[i] for i in g.pool if i != sp]
            mind = d.minds[sp]
            ctx.secrets = [m.text for m in mind.memories if any(not memory.can_state(m, n) for n in audience)]
            req, mt = request(g, sp)
            lease.generating = True
            check, on_check = d._repeat_check(g, sp)
            max_tok = d.cfg.eulogy_max_tokens if (g.kind == "funeral" and sp == g.lead and not g.turns) else d.cfg.llm.line_max_tokens

            async def say(request: str, mind=mind, sp=sp, lease=lease, ctx=ctx, check=check, on_check=on_check, max_tok=max_tok):
                return await speak(d.llm, slot=lease.slot, mind=mind, name=g.names[sp], prefix=lease.data["prefix"],
                                   turns=[(t.name, t.text) for t in g.turns], turn_no=len(g.turns), ctx=ctx,
                                   listener_name=None, labels=d.labels_for(ctx.minor_involved),
                                   max_tokens=max_tok, request=request, check=check, retries=d.cfg.repeat_retries,
                                   on_check=on_check, speech=lease.data.get("speech"))

            if ceremony_turn(g, sp):
                res = await _compose_turn(d, g, sp, mind, lease, req, say)
            else:
                res = await say(req)
            lease.generating = False
            if isinstance(res, Dropped):
                d.dropped.append((sp, res.reason))
                g.ended = "repeat" if res.reason == "repeat" else "dropped"
                break
            if any(l.preempted for l in held.values()):
                g.ended = "preempted"
                break
            if item.expired(d.now()) and not g.turns:
                g.ended = "expired"
                break
            if g.kind == "wedding" and sp in g.couple and len(g.turns) < len(g.couple):
                lis = g.couple[1 - g.couple.index(sp)]           # a vow is said to the partner
            elif g.turns:
                lis = g.turns[-1].speaker                       # a reaction turns toward the last speaker
            else:
                lis = None
            ok = await d._say(sp, res, listener=lis, expires=d.wire_expiry(item) if not g.turns else None, conv=g.id,
                              event=item.event, minor=ctx.minor_involved)
            if not ok:
                g.ended = "apart"
                break
            g.turns.append(Turn(sp, g.names[sp], res.text, res.intent))
            spoke[sp] = len(g.turns)
            gs.lines += 1
            d._remember_own(sp, res.text)
            last_real = d.now().real
            d.leases.quotas.record(sp, None, d.now(), pair=False)
            d.line_count[sp] = d.line_count.get(sp, 0) + 1
            for l in held.values():
                d.leases.touch(l, d.now())
            if len(g.turns) == 1 and g.story:
                gs.told[g.story] = d.bridge.env.game
            try:
                present = {i for i, ch in d.adapter.scene_present(await d.bridge.scene(sp)) if ch}
            except BridgeError:
                g.ended = "apart"
                break
            g.speakers = [i for i in g.speakers if i == sp or i in present]
            if not [i for i in g.speakers if i != sp]:
                g.ended = "apart"
        if g.turns:
            finish(d, g)
    except BaseException as ex:
        g.ended = g.ended or "error"
        if not isinstance(ex, Exception):
            raise
        log.exception("gathering failed")
    finally:
        gs.ends[g.ended or "done"] = gs.ends.get(g.ended or "done", 0) + 1
        gs.log.append({"id": g.id, "kind": g.kind, "key": g.key, "turns": len(g.turns), "ended": g.ended,
                       "speakers": list(g.speakers)})
        for l in list(held.values()) + [lease0]:
            l.generating = False
            if not l.ended:
                d.leases.release(l, "gathering_end")


def finish(d, g: Gathering) -> None:
    """Everyone there keeps one memory of it (a template, no call), and a ceremony goes into the chronicle."""
    game = d.bridge.env.game
    imp = 0.5 if g.kind in CEREMONIES else 0.3 if g.kind == "meal" else 0.35
    people = list(g.pool)[:6]
    for i in g.pool:
        mind = d.minds.get(i)
        if mind is None:
            continue
        mind.memories.append(Memory(id=mind.next_mem_id(), game=game, text=template_memory(g, i), source="saw",
                                    importance=imp, people=[p for p in people if p in d.names], etype="gathering"))
        mind.unreflected_importance += 0.2
        d.dirty.add(i)
    if g.kind in CEREMONIES and g.turns:
        t = g.turns[0]
        what = {"wedding": "At the wedding", "funeral": f"At the funeral of {g.deceased or 'one of ours'}"}.get(g.kind, f"At {SETTING[g.kind]}")
        d.chronicle.note(game, f"{what}, {t.name} said: \"{t.text}\"", 0.4, False)

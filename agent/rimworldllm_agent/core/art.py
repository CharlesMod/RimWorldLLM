"""Art descriptions (aliveness item 5): when a maker finishes a piece of art, a short description written from the
community's real history replaces the game's generated tale text on the info card.

An ``art_created`` event (docs/BRIDGE-API.md) becomes a queued job. The job runs as idle work on the narrator slot
(IdleRunner._art, right after the chronicle): preemptible like the other idle work, one at a time. The queue is bounded,
the newest piece wins, and a job older than a game day is dropped. The text goes back with POST /art.
Game text in the prompt (names, the game's own tale text, chronicle lines, memories) is cleaned and wrapped in
data sections; the system prompt carries the data rule. The text is sanitised and checked by the age gate.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

from . import agegate
from . import creative
from . import gamedata as gd
from . import grounding
from . import memory
from .model_tier import feature_on
from .llm import LLMError
from .sanitize import prompt_cap_words, sanitize_prose

if TYPE_CHECKING:
    from .director import Director

MAX_QUEUE = 6
MAX_MEMORIES = 3
MAX_CHRONICLE = 3

# The wording is the adapter's (art_system, art_request); these are the game-neutral fallbacks.
ART_SYSTEM = (
    "You write the short description on the info card of a work of art made by a member of the community. Plain "
    "words, third person, past tense. State only what the data says. Never invent people, places, events or numbers. "
    "No flowery prose, no moral. Nothing romantic or sexual about anyone, ever.")
ART_REQUEST = "Write the description: 2 or 3 plain sentences, under 55 words, one paragraph."


@dataclass
class ArtJob:
    thing: int
    maker: int
    game: int                  # when it was finished
    label: str = ""
    quality: str = ""
    title: str = ""
    tale: str = ""
    material: str = ""         # the stuff it is made of (game text)
    tale_def: str = ""         # the depicted tale: its def, summary, game day and the people in it (all game text)
    tale_summary: str = ""
    tale_day: int | None = None
    tale_people: list = field(default_factory=list)


class ArtQueue:
    """Bounded, newest first. Held in memory only: a restart forgets what was waiting."""

    def __init__(self) -> None:
        self.jobs: list[ArtJob] = []
        self.dropped = 0
        self.unsupported = False      # the mod answered 404 to POST /art

    def note(self, e: dict, game: int) -> ArtJob | None:
        """Queue the job an ``art_created`` event describes (None when it names no maker or thing)."""
        data = e.get("data") or {}
        maker = next((int(a["id"]) for a in e.get("actors", []) if a.get("role") == "maker"), None)
        try:
            thing = int(data["thing_id"])
        except (KeyError, TypeError, ValueError):
            return None
        if maker is None:
            return None
        job = ArtJob(thing=thing, maker=maker, game=int(e.get("game", game)), label=str(data.get("def_label", "")),
                     quality=str(data.get("quality", "")), title=str(data.get("title", "")),
                     tale=str(data.get("description", "")), material=str(data.get("material") or ""))
        tale = data.get("tale")
        if isinstance(tale, dict):
            job.tale_def = str(tale.get("def") or "")
            job.tale_summary = str(tale.get("summary") or "")
            try:
                job.tale_day = int(tale["day"]) if tale.get("day") is not None else None
            except (TypeError, ValueError):
                job.tale_day = None
            # the people in the tale: the one list-valued field (the game names its key; core does not)
            lists = [v for k, v in tale.items() if k not in ("def", "summary", "day") and isinstance(v, list)]
            job.tale_people = [str(x) for x in lists[0]][:3] if lists else []
        self.jobs = [j for j in self.jobs if j.thing != thing] + [job]
        if len(self.jobs) > MAX_QUEUE:
            self.dropped += len(self.jobs) - MAX_QUEUE
            self.jobs = self.jobs[-MAX_QUEUE:]
        return job

    def expire(self, game: int, day: int) -> None:
        keep = [j for j in self.jobs if game - j.game <= day]
        self.dropped += len(self.jobs) - len(keep)
        self.jobs = keep

    def take(self, game: int, day: int) -> ArtJob | None:
        """The newest job still within a game day, removed from the queue."""
        self.expire(game, day)
        return self.jobs.pop() if self.jobs else None

    def pending(self, game: int, day: int) -> bool:
        """True when a job is still within its game day (read-only; take() is what removes it)."""
        return any(game - j.game <= day for j in self.jobs)

    def put_back(self, job: ArtJob) -> None:
        if all(j.thing != job.thing for j in self.jobs):
            self.jobs.insert(0, job)                       # the oldest slot: newer pieces still go first
            self.jobs = self.jobs[-MAX_QUEUE:]

    def rollback(self, game: int) -> None:
        self.jobs = [j for j in self.jobs if j.game <= game]

    def __len__(self) -> int:
        return len(self.jobs)


def enabled(d: "Director") -> bool:
    return bool(feature_on(d.tier, d.cfg.art_enabled, d.cfg.art_small) and not d.art.unsupported and d.bridge_cfg.get("art_descriptions") is not False
                and d.bridge_cfg.get("events", {}).get("art_created") is not False
                and d.bridge_cfg.get("events", {}).get("art") is not False)


def art_messages(d: "Director", job: ArtJob, card: dict | None = None,
                 request: str | None = None) -> tuple[list[tuple[str, str]], list]:
    """(messages, the maker memories used). With creative_ground the data is richer (see ``_ground_messages``);
    ``card`` is the maker's traits and mood from grounding.actor_card (None: none known)."""
    if creative.on(d, "ground", "art"):
        return _ground_messages(d, job, card or {}, request)
    now = d.bridge.env.game
    maker = d.names.get(job.maker, "Someone")
    mind = d.minds.get(job.maker)
    mems = []
    if mind is not None:
        mems = [m for m in memory.weight_sorted(mind, now, d.day) if m.visibility != "private"][:MAX_MEMORIES]
    recent = [e.text for e in d.chronicle.state.recent[-MAX_CHRONICLE:]]
    facts = [f"Maker: {gd.name(maker)}", f"Item: {gd.clean(job.label, 'label') or 'artwork'}"]
    if job.quality:
        facts.append(f"Quality: {gd.clean(job.quality, 'label')}")
    if job.title:
        facts.append(f"Title: {gd.clean(job.title, 'name')}")
    if job.tale:
        facts.append(f"The game's own text for it: {gd.clean(job.tale, 'desc')}")
    hist = "\n".join(f"- {gd.clean(t, 'event', max_len=200)}" for t in recent) or "- (nothing written yet)"
    mem = "\n".join(f"- {gd.clean(m.text, 'event')}" for m in mems) or "- (nothing in particular)"
    user = (f"=== THE ARTWORK ===\n{gd.data_section('artwork', chr(10).join(facts))}"
            f"=== RECENT HISTORY ===\n{gd.data_section('history', hist)}"
            f"=== WHAT THE MAKER REMEMBERS ===\n{gd.data_section('memories', mem)}"
            f"=== REQUEST ===\n{request or getattr(d.adapter, "art_request", ART_REQUEST)}")
    system = getattr(d.adapter, "art_system", ART_SYSTEM)
    return [("system", system + "\n\n" + gd.DATA_RULE + "\n" + agegate.MINOR_RULE), ("user", user)], mems


GAME_QUALITY = frozenset({"masterwork", "legendary"})     # the only quality words the game's art text itself carries

GROUND_REQUEST = ("Write the description: 2 or 3 plain sentences, under 55 words, one paragraph. Say what the piece "
                  "depicts as the data gives it (who, where, the weather if given, how it ended) in your own words, not the "
                  "game text's order. Name the material once. If the maker remembers this event, let one detail of that "
                  "memory show in how the scene is treated. Never name the maker's traits, mood or the quality grade. Use "
                  "only people and events the data names.")


def _ground_messages(d: "Director", job: ArtJob, card: dict, request: str | None) -> tuple[list[tuple[str, str]], list]:
    now = d.bridge.env.game
    maker = d.names.get(job.maker, "Someone")
    mind = d.minds.get(job.maker)
    people = [gd.name(p) for p in job.tale_people if p]
    tokens = grounding.tokens_of([maker, *people])
    tale_kw = {w for w in memory.keywords(job.tale_summary)} if job.tale_summary else set()
    facts = [f"Maker: {gd.name(maker)}", f"Item: {gd.clean(job.label, 'label') or 'artwork'}"]
    if job.quality and gd.clean(job.quality, 'label').casefold() in GAME_QUALITY:
        facts.append(f"Quality: {gd.clean(job.quality, 'label')}")
    if job.title:
        facts.append(f"Title: {gd.clean(job.title, 'name')}")
    if job.material:
        facts.append(f"Material: {gd.clean(job.material, 'label')}")
    if job.tale:
        facts.append(f"The game's own text for it: {gd.clean(job.tale, 'desc')}")
    if job.tale_summary:
        facts.append(f"The event it depicts: {gd.clean(job.tale_summary, 'desc', max_len=200)}")
    if people:
        facts.append("People in it: " + ", ".join(people))
    if job.tale_day is not None and d.day:
        ago = now // d.day - job.tale_day
        if ago >= 0:
            facts.append("It happened " + ("today" if ago == 0 else "1 day ago" if ago == 1 else f"{ago} days ago"))
    mems = []
    if mind is not None:
        ms = [m for m in memory.weight_sorted(mind, now, d.day) if m.visibility != "private"]
        hit = [m for m in ms if grounding.mentions(m.text, tokens) or (tale_kw and tale_kw & memory.keywords(m.text))]
        mems = (hit or ms)[:MAX_MEMORIES]
    recent = [e.text for e in d.chronicle.state.recent]
    ent = [t for t in recent if grounding.mentions(t, tokens) or (tale_kw and tale_kw & memory.keywords(t))][-MAX_CHRONICLE:]
    ent = ent or recent[-MAX_CHRONICLE:]
    hist = "\n".join(f"- {gd.clean(t, 'event', max_len=200)}" for t in ent) or "- (nothing written yet)"
    mem = "\n".join(f"- {gd.clean(m.text, 'event')}" for m in mems) or "- (nothing in particular)"
    # only the traits (no mood, no mood thoughts, no background): they shape the treatment and are never named
    traits = [gd.clean(t, "label") for t in (card.get("traits") or []) if t]
    maker_sec = (f"=== THE MAKER (these shape the treatment only; never name them) ===\n"
                 f"{gd.data_section('maker', 'Traits: ' + ', '.join(traits))}" if traits else "")
    user = (f"=== THE ARTWORK ===\n{gd.data_section('artwork', chr(10).join(facts))}"
            f"{maker_sec}"
            f"=== HISTORY THAT TOUCHES IT ===\n{gd.data_section('history', hist)}"
            f"=== WHAT THE MAKER REMEMBERS ===\n{gd.data_section('memories', mem)}"
            f"=== REQUEST ===\n{request or getattr(d.adapter, 'art_request_ground', GROUND_REQUEST)}")
    system = getattr(d.adapter, "art_system", ART_SYSTEM)
    return [("system", system + "\n\n" + gd.DATA_RULE + "\n" + agegate.MINOR_RULE), ("user", user)], mems


def _with_note(msgs: list[tuple[str, str]], note: str) -> list[tuple[str, str]]:
    """The request with a creative note (a plan, phrases to avoid) added at its end; unchanged for an empty note."""
    if not note:
        return msgs
    return msgs[:-1] + [(msgs[-1][0], msgs[-1][1].rstrip("\n") + "\n" + note)]


async def write_art(d: "Director", job: ArtJob, slot: int) -> tuple[str | None, str]:
    """(text, reason): the sanitised description, or None and why it was dropped. LLMError is not caught here."""
    ground = creative.on(d, "ground", "art")
    card = await grounding.actor_card(d, job.maker) if ground else None
    msgs, mems = art_messages(d, job, card)
    toks = d.name_tokens
    allnames = set().union(*toks.values()) if toks else set()
    known = set(toks.get(job.maker, set())).union(*(toks.get(p, set()) for m in mems for p in m.people))
    # people the prompt's own data names (the depicted event, its people, the history lines) may be named too:
    # the game's own text names them, and refusing them dropped most real descriptions as unknown_name
    data_words = set(re.findall(r"[A-Za-z']+", msgs[1][1].split("=== REQUEST ===")[0]))
    for ts in toks.values():
        if ts & data_words:
            known |= set(ts)
    minor = d.is_minor(job.maker)
    why = ["sanitize"]

    async def write(note: str) -> str | None:
        m = _with_note(msgs, note)
        c = await d.llm.complete(m, slot=slot, kind="art", max_tokens=d.cfg.art_max_tokens, stop=[],
                                 temperature=0.7, actor=job.maker)
        s = sanitize_prose(c.text, known_names=known, all_names=allnames, prompt_words=prompt_cap_words(m[1][1]),
                           common_words=set(d.adapter.common_words), speaker_names=toks.get(job.maker, set()),
                           min_words=5, max_words=75, involves_minor=minor, minor_names=d.minor_tokens())
        if not s.ok:
            why[0] = s.reason or "sanitize"
            return None
        if agegate.violation(s.text, involves_minor=minor, minor_names=d.minor_tokens()):
            d.gate.skipped_events += 1
            why[0] = "agegate"
            return None
        return s.text

    async def plan() -> str | None:
        pm = msgs[:-1] + [(msgs[-1][0], msgs[-1][1].split("=== REQUEST ===")[0] + "=== REQUEST ===\n" + grounding.PLAN_ASK)]
        c = await d.llm.complete(pm, slot=slot, kind="art", max_tokens=grounding.PLAN_TOKENS, stop=["\n"],
                                 temperature=0.7, actor=job.maker)
        return c.text

    facts = msgs[1][1].split("=== REQUEST ===")[0]
    text, _ = await creative.compose(d, kind="art", slot=slot, actor=job.maker, facts=facts, names=known,
                                     write=write, plan=plan, should_yield=lambda: grounding.idle_should_yield(d))
    return (text, "") if text else (None, why[0])

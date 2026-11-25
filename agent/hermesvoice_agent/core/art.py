"""Art descriptions (aliveness item 5): when a maker finishes a piece of art, a short description written from the
community's real history replaces the game's generated tale text on the info card.

An ``art_created`` event (docs/BRIDGE-API.md) becomes a queued job. The job runs as idle work on the narrator slot
(IdleRunner._art, right after the chronicle): preemptible like the other idle work, one at a time. The queue is bounded,
the newest piece wins, and a job older than a game day is dropped. The text goes back with POST /art.
Game text in the prompt (names, the game's own tale text, chronicle lines, memories) is cleaned and wrapped in
data sections; the system prompt carries the data rule. The text is sanitised and checked by the age gate.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING

from . import agegate
from . import gamedata as gd
from . import memory
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
                     tale=str(data.get("description", "")))
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

    def put_back(self, job: ArtJob) -> None:
        if all(j.thing != job.thing for j in self.jobs):
            self.jobs.insert(0, job)                       # the oldest slot: newer pieces still go first
            self.jobs = self.jobs[-MAX_QUEUE:]

    def rollback(self, game: int) -> None:
        self.jobs = [j for j in self.jobs if j.game <= game]

    def __len__(self) -> int:
        return len(self.jobs)


def enabled(d: "Director") -> bool:
    return bool(d.cfg.art_enabled and not d.art.unsupported and d.bridge_cfg.get("art_descriptions") is not False
                and d.bridge_cfg.get("events", {}).get("art_created") is not False
                and d.bridge_cfg.get("events", {}).get("art") is not False)


def art_messages(d: "Director", job: ArtJob) -> tuple[list[tuple[str, str]], list]:
    """(messages, the maker memories used)."""
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
            f"=== REQUEST ===\n{getattr(d.adapter, "art_request", ART_REQUEST)}")
    system = getattr(d.adapter, "art_system", ART_SYSTEM)
    return [("system", system + "\n\n" + gd.DATA_RULE + "\n" + agegate.MINOR_RULE), ("user", user)], mems


async def write_art(d: "Director", job: ArtJob, slot: int) -> tuple[str | None, str]:
    """(text, reason): the sanitised description, or None and why it was dropped. LLMError is not caught here."""
    msgs, mems = art_messages(d, job)
    c = await d.llm.complete(msgs, slot=slot, kind="art", max_tokens=d.cfg.art_max_tokens, stop=[],
                             temperature=0.7, actor=job.maker)
    toks = d.name_tokens
    allnames = set().union(*toks.values()) if toks else set()
    known = set(toks.get(job.maker, set())).union(*(toks.get(p, set()) for m in mems for p in m.people))
    minor = d.is_minor(job.maker)
    s = sanitize_prose(c.text, known_names=known, all_names=allnames, prompt_words=prompt_cap_words(msgs[1][1]),
                       common_words=set(d.adapter.common_words), speaker_names=toks.get(job.maker, set()),
                       min_words=5, max_words=75, involves_minor=minor, minor_names=d.minor_tokens())
    if not s.ok:
        return None, s.reason or "sanitize"
    if agegate.violation(s.text, involves_minor=minor, minor_names=d.minor_tokens()):
        d.gate.skipped_events += 1
        return None, "agegate"
    return s.text, ""

"""The actor mind (SPEC §5) and its serialisation to the /mind blob.

The blob is opaque to the mod, saved in the save file, limit 32 KiB. ``Mind.to_blob`` trims
decayed memories first (then history, then the least important pinned ones) to stay under
``BLOB_LIMIT``.
"""
from __future__ import annotations

import json
from typing import Literal

from pydantic import BaseModel, Field

DEFAULT_DAY_UNITS = 60000   # fallback only; the adapter supplies units_per_day
HALF_LIFE_DAYS = 4.0
BLOB_LIMIT = 32 * 1024
BLOB_TARGET = 30 * 1024     # leave headroom for the envelope and later growth
SCHEMA = 1


class FixedBio(BaseModel):
    text: str = ""                   # the prompt-ready paragraph(s)
    facts_hash: str = ""             # of the game facts it was built from
    extras: dict = Field(default_factory=dict)   # game-specific bio extras


class VoiceCard(BaseModel):
    register_line: str = ""
    tics: list[str] = Field(default_factory=list)       # 1-3
    examples: list[str] = Field(default_factory=list)   # exactly 3 once built
    edited_by_player: bool = False
    llm_examples: bool = False                          # examples written by the optional LLM pass
    third_person: bool | None = None                    # speaks of itself by name ("Tuk hunts."); None = infer from examples

    def speaks_third_person(self, names: set[str]) -> bool:
        """True if this voice refers to itself by name: the flag if set, else when at least one of the examples
        start with the speaker's own name followed by a verb."""
        if self.third_person is not None:
            return self.third_person
        from .sanitize import is_self_narration
        return sum(1 for e in self.examples if is_self_narration(e.strip(), names)) >= 1


BIO_HISTORY_KEEP = 10
DIARY_KEEP = 7


class BioDiff(BaseModel):
    rev: int = 0
    game: int
    before: str
    after: str


class Memory(BaseModel):
    id: str
    game: int
    text: str                                    # first person, 1-2 sentences
    source: Literal["saw", "heard", "told"] = "saw"
    told_by: str | None = None                   # name, for source == "told"
    told_by_id: int | None = None
    confidence: float = 1.0
    visibility: Literal["private", "told", "public"] = "public"
    told_to: list[str] = Field(default_factory=list)
    importance: float = 0.3                      # 0..1, from game numbers
    pinned: bool = False
    people: list[int] = Field(default_factory=list)   # actor ids involved
    etype: str = ""
    event_seq: int | None = None
    summary_of: int = 0                          # >0: merged summary of N older memories


class Relation(BaseModel):
    name: str = ""
    feeling: str = ""
    history: str = ""
    opinion: int | None = None                   # the game's own number, last seen
    updated_time: int = 0


class CRel(BaseModel):
    """One line of block C's relations digest, frozen at the last reflection."""
    name: str = ""
    opinion: int | None = None
    feeling: str = ""
    history: str = ""


class DiaryEntry(BaseModel):
    day: int
    game: int
    text: str


class Mind(BaseModel):
    schema_v: int = SCHEMA
    actor_id: int
    name: str = ""
    fixed: FixedBio = Field(default_factory=FixedBio)
    voice: VoiceCard = Field(default_factory=VoiceCard)
    evolving_bio: str = ""
    bio_history: list[BioDiff] = Field(default_factory=list)
    bio_rev: int = 0
    c_relations: list[CRel] = Field(default_factory=list)   # block C digest; written only at reflection
    diary: list[DiaryEntry] = Field(default_factory=list)   # last DIARY_KEEP entries
    last_diary_day: int = -1
    wants: list[str] = Field(default_factory=list)
    memories: list[Memory] = Field(default_factory=list)
    relations: dict[str, Relation] = Field(default_factory=dict)   # key: str(actor id)
    notes: str = ""                              # director's notes, from the player
    notes_edited_by_player: bool = False         # set by the mod's Mind tab; the player's text wins on conflict
    notes_rev: int = 0                           # bumped by the mod on every player edit
    firsts: list[str] = Field(default_factory=list)   # event types seen for the first time
    unreflected_importance: float = 0.0
    last_reflect_time: int = 0
    mem_counter: int = 0
    last_line_time: int = 0

    # ---- memories -------------------------------------------------------
    def next_mem_id(self) -> str:
        self.mem_counter += 1
        return f"m{self.mem_counter}"

    def memory_weight(self, m: Memory, now_time: int, day: int = DEFAULT_DAY_UNITS) -> float:
        """Importance decayed by age with a 4-day half-life; pinned memories do not decay."""
        if m.pinned:
            return max(m.importance, 0.5) + 1.0
        age_days = max(0, now_time - m.game) / day
        return m.importance * m.confidence * 0.5 ** (age_days / HALF_LIFE_DAYS)

    def add_bio_diff(self, game: int, new_text: str) -> None:
        if new_text != self.evolving_bio:
            self.bio_rev += 1
            self.bio_history.append(BioDiff(rev=self.bio_rev, game=game, before=self.evolving_bio, after=new_text))
            self.bio_history = self.bio_history[-BIO_HISTORY_KEEP:]
            self.evolving_bio = new_text

    def undo_bio(self) -> bool:
        if not self.bio_history:
            return False
        self.evolving_bio = self.bio_history.pop().before
        return True

    def freeze_relations(self, limit: int = 6) -> None:
        """Refresh block C's relations digest from the live relations. Reflection only."""
        rels = sorted(self.relations.items(), key=lambda kv: (-abs(kv[1].opinion or 0), kv[0]))[:limit]
        self.c_relations = [CRel(name=r.name, opinion=r.opinion, feeling=r.feeling, history=r.history)
                            for _, r in rels]

    def add_diary(self, day: int, game: int, text: str) -> None:
        self.diary.append(DiaryEntry(day=day, game=game, text=text))
        self.diary = self.diary[-DIARY_KEEP:]
        self.last_diary_day = day

    # ---- serialisation --------------------------------------------------
    def to_blob(self, now_time: int = 0, day: int = DEFAULT_DAY_UNITS, limit: int = BLOB_TARGET) -> dict:
        """Dict ready for PUT /mind. Trims *this object* to fit: decayed memories first."""
        def size() -> int:
            return len(json.dumps(self.model_dump(), ensure_ascii=False, separators=(",", ":")).encode())

        if size() > limit:
            # 1. ordinary memories, lowest decayed weight first
            ordinary = sorted((m for m in self.memories if not m.pinned),
                              key=lambda m: (self.memory_weight(m, now_time, day), m.game))
            for m in ordinary:
                if size() <= limit:
                    break
                self.memories.remove(m)
        if size() > limit:
            self.diary = self.diary[-3:]
        if size() > limit:
            self.bio_history = self.bio_history[-2:]
        if size() > limit:
            self.bio_history = []
        if size() > limit:
            # 2. pinned memories, least important and oldest first
            for m in sorted(self.memories, key=lambda m: (m.importance, m.game)):
                if size() <= limit:
                    break
                self.memories.remove(m)
        if size() > limit:
            self.diary = []
        if size() > limit:
            self.relations = dict(sorted(self.relations.items(),
                                         key=lambda kv: -kv[1].updated_time)[:20])
        return self.model_dump()

    @classmethod
    def from_blob(cls, blob: dict | None, actor_id: int, name: str = "") -> "Mind":
        if not blob:
            return cls(actor_id=actor_id, name=name)
        try:
            m = cls.model_validate(blob)
        except Exception:
            return cls(actor_id=actor_id, name=name)   # unreadable blob: start a fresh mind
        m.actor_id = actor_id
        return m

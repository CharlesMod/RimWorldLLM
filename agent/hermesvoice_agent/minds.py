"""The pawn mind (SPEC §5) and its serialisation to the /mind blob.

The blob is opaque to the mod, saved in the save file, limit 32 KiB. ``Mind.to_blob`` trims
decayed memories first (then history, then the least important pinned ones) to stay under
``BLOB_LIMIT``.
"""
from __future__ import annotations

import json
from typing import Literal

from pydantic import BaseModel, Field

TICKS_PER_DAY = 60000
HALF_LIFE_DAYS = 4.0
BLOB_LIMIT = 32 * 1024
BLOB_TARGET = 30 * 1024     # leave headroom for the envelope and later growth
SCHEMA = 1


class FixedBio(BaseModel):
    text: str = ""                   # the prompt-ready paragraph(s)
    facts_hash: str = ""             # of the game facts it was built from
    dlc: dict = Field(default_factory=dict)   # ideology role, title, xenotype, genes, anomaly ...


class VoiceCard(BaseModel):
    register_line: str = ""
    tics: list[str] = Field(default_factory=list)       # 1-3
    examples: list[str] = Field(default_factory=list)   # exactly 3 once built
    edited_by_player: bool = False
    llm_examples: bool = False                          # examples written by the optional LLM pass


class BioDiff(BaseModel):
    tick: int
    before: str
    after: str


class Memory(BaseModel):
    id: str
    tick: int
    text: str                                    # first person, 1-2 sentences
    source: Literal["saw", "heard", "told"] = "saw"
    told_by: str | None = None                   # name, for source == "told"
    told_by_id: int | None = None
    confidence: float = 1.0
    visibility: Literal["private", "told", "public"] = "public"
    told_to: list[str] = Field(default_factory=list)
    importance: float = 0.3                      # 0..1, from game numbers
    pinned: bool = False
    people: list[int] = Field(default_factory=list)   # pawn ids involved
    etype: str = ""
    event_seq: int | None = None
    summary_of: int = 0                          # >0: merged summary of N older memories


class Relation(BaseModel):
    name: str = ""
    feeling: str = ""
    history: str = ""
    opinion: int | None = None                   # RimWorld's own number, last seen
    updated_tick: int = 0


class Mind(BaseModel):
    schema_v: int = SCHEMA
    pawn_id: int
    name: str = ""
    fixed: FixedBio = Field(default_factory=FixedBio)
    voice: VoiceCard = Field(default_factory=VoiceCard)
    evolving_bio: str = ""
    bio_history: list[BioDiff] = Field(default_factory=list)
    wants: list[str] = Field(default_factory=list)
    memories: list[Memory] = Field(default_factory=list)
    relations: dict[str, Relation] = Field(default_factory=dict)   # key: str(pawn id)
    notes: str = ""                              # director's notes, from the player
    firsts: list[str] = Field(default_factory=list)   # event types seen for the first time
    unreflected_importance: float = 0.0
    last_reflect_tick: int = 0
    mem_counter: int = 0
    last_line_tick: int = 0

    # ---- memories -------------------------------------------------------
    def next_mem_id(self) -> str:
        self.mem_counter += 1
        return f"m{self.mem_counter}"

    def memory_weight(self, m: Memory, now_tick: int) -> float:
        """Importance decayed by age with a 4-day half-life; pinned memories do not decay."""
        if m.pinned:
            return max(m.importance, 0.5) + 1.0
        age_days = max(0, now_tick - m.tick) / TICKS_PER_DAY
        return m.importance * m.confidence * 0.5 ** (age_days / HALF_LIFE_DAYS)

    def add_bio_diff(self, tick: int, new_text: str) -> None:
        if new_text != self.evolving_bio:
            self.bio_history.append(BioDiff(tick=tick, before=self.evolving_bio, after=new_text))
            self.bio_history = self.bio_history[-8:]
            self.evolving_bio = new_text

    def undo_bio(self) -> bool:
        if not self.bio_history:
            return False
        self.evolving_bio = self.bio_history.pop().before
        return True

    # ---- serialisation --------------------------------------------------
    def to_blob(self, now_tick: int = 0, limit: int = BLOB_TARGET) -> dict:
        """Dict ready for PUT /mind. Trims *this object* to fit: decayed memories first."""
        def size() -> int:
            return len(json.dumps(self.model_dump(), ensure_ascii=False, separators=(",", ":")).encode())

        if size() > limit:
            # 1. ordinary memories, lowest decayed weight first
            ordinary = sorted((m for m in self.memories if not m.pinned),
                              key=lambda m: (self.memory_weight(m, now_tick), m.tick))
            for m in ordinary:
                if size() <= limit:
                    break
                self.memories.remove(m)
        if size() > limit:
            self.bio_history = self.bio_history[-2:]
        if size() > limit:
            self.bio_history = []
        if size() > limit:
            # 2. pinned memories, least important and oldest first
            for m in sorted(self.memories, key=lambda m: (m.importance, m.tick)):
                if size() <= limit:
                    break
                self.memories.remove(m)
        if size() > limit:
            self.relations = dict(sorted(self.relations.items(),
                                         key=lambda kv: -kv[1].updated_tick)[:20])
        return self.model_dump()

    @classmethod
    def from_blob(cls, blob: dict | None, pawn_id: int, name: str = "") -> "Mind":
        if not blob:
            return cls(pawn_id=pawn_id, name=name)
        try:
            m = cls.model_validate(blob)
        except Exception:
            return cls(pawn_id=pawn_id, name=name)   # unreadable blob: start a fresh mind
        m.pawn_id = pawn_id
        return m

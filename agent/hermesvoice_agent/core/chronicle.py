"""The narrator's community chronicle (SPEC §7, milestone 5).

A paragraph per game day, or at once on a major event, written from the ledger and the day's notable
events. The old chronicle is rolled into a running summary by self-summary (RimGPT style), so the
narrator's context stays under a fixed token budget. Nothing new, nothing written. The narrator never
proposes or triggers events: it only describes. Wording comes from the adapter.
"""
from __future__ import annotations

import json
import os
import re
from pathlib import Path

from pydantic import BaseModel, Field

from . import gamedata as gd
from .llm import LLMClient, LLMError, Messages
from .sanitize import prompt_cap_words, sanitize_prose

KEEP_RECENT = 3                 # entries kept verbatim beside the running summary
RECENT_BUDGET_TOKENS = 1200     # roll the oldest recent entry into the summary past this
SUMMARY_MAX_WORDS = 250
RESERVE_TOKENS = 300            # left for the completion inside the context budget


def est_tokens(text: str) -> int:
    return len(text) // 4 + 1


def messages_tokens(msgs: Messages) -> int:
    return sum(est_tokens(t) + 4 for _, t in msgs)


class Note(BaseModel):
    day: int
    game: int
    text: str
    severity: float = 0.3
    major: bool = False


class Entry(BaseModel):
    day: int
    game: int
    kind: str = "day"           # "day" or "major"
    text: str


class ChronicleState(BaseModel):
    summary: str = ""
    recent: list[Entry] = Field(default_factory=list)
    pending: list[Note] = Field(default_factory=list)
    last_day: int = -1          # newest day an entry covers


class Chronicle:
    def __init__(self, data_dir: Path | None = None, units_per_day: int = 60000):
        self.dir = Path(data_dir) if data_dir else None
        self.day = units_per_day
        self.state = ChronicleState()
        self.log: list[Entry] = []          # everything written this run (the file keeps all of it)
        self.load()

    # ---- storage ----------------------------------------------------------
    def load(self) -> None:
        if not self.dir:
            return
        try:
            self.state = ChronicleState.model_validate_json((self.dir / "chronicle.json").read_text())
        except (OSError, ValueError):
            self.state = ChronicleState()

    def save(self) -> None:
        if not self.dir:
            return
        self.dir.mkdir(parents=True, exist_ok=True)
        tmp = self.dir / "chronicle.json.tmp"
        tmp.write_text(self.state.model_dump_json())
        os.replace(tmp, self.dir / "chronicle.json")

    def _append_file(self, e: Entry) -> None:
        if not self.dir:
            return
        self.dir.mkdir(parents=True, exist_ok=True)
        with open(self.dir / "chronicle.jsonl", "a") as f:
            f.write(e.model_dump_json() + "\n")
        with open(self.dir / "chronicle.md", "a") as f:
            f.write(f"## Day {e.day + 1}" + (" (major event)" if e.kind == "major" else "") + f"\n\n{e.text}\n\n")

    def rollback(self, game: int) -> None:
        """The world was loaded from an earlier save: forget what has not happened yet."""
        st = self.state
        st.recent = [e for e in st.recent if e.game <= game]
        st.pending = [n for n in st.pending if n.game <= game]
        st.last_day = max([e.day for e in st.recent], default=-1) if st.recent else min(st.last_day, game // self.day)
        self.save()

    # ---- input ------------------------------------------------------------
    def note(self, game: int, text: str, severity: float = 0.3, major: bool = False) -> None:
        self.state.pending.append(Note(day=game // self.day, game=game, text=text, severity=severity, major=major))
        self.state.pending = self.state.pending[-200:]

    def due(self, today: int) -> tuple[str, int, list[Note]] | None:
        """(kind, day, notes) for the next entry, or None when nothing new is waiting."""
        p = self.state.pending
        if not p:
            return None
        if any(n.major for n in p):
            return "major", max(n.day for n in p), list(p)
        past = [n for n in p if n.day < today]
        if past:
            d = min(n.day for n in past)
            return "day", d, [n for n in p if n.day <= d]
        return None

    # ---- writing ----------------------------------------------------------
    def build_messages(self, adapter, kind: str, day: int, notes: list[Note], ledger_lines: list[str],
                       budget: int, lore_lines: list[str] | None = None) -> Messages:
        """System + summary + recent entries + open matters + the events, trimmed to fit ``budget`` tokens."""
        notes = sorted(notes, key=lambda n: n.game)
        recent = list(self.state.recent)
        summary = self.state.summary
        ledger = list(ledger_lines)[:12]

        names = "\n".join(f"- {gd.clean(t, 'line', max_len=100)}" for t in (lore_lines or [])[-12:])

        def build() -> Messages:
            ev = "\n".join(f"- {gd.clean(n.text, 'event')}" for n in notes) or "- (nothing noted)"
            led = "\n".join(f"- {gd.clean(l, 'event')}" for l in ledger) or "- nothing ongoing"
            rec = "\n\n".join(f"Day {e.day + 1}: {e.text}" for e in recent) or "(none yet)"
            user = (f"=== THE STORY SO FAR ===\n{summary or '(the chronicle begins)'}\n"
                    f"=== RECENT ENTRIES ===\n{rec}\n"
                    f"=== ONGOING MATTERS ===\n{gd.data_section('ongoing', led)}"
                    f"=== WHAT HAPPENED (day {day + 1}) ===\n{gd.data_section('events', ev)}"
                    + (f"=== SHARED NAMES (use these exact names when you refer to them) ===\n{gd.data_section('names', names)}"
                       if names else "") +
                    f"=== REQUEST ===\n{adapter.chronicle_request}")
            return [("system", adapter.chronicle_system + "\n\n" + gd.DATA_RULE), ("user", user)]

        limit = budget - RESERVE_TOKENS
        while messages_tokens(build()) > limit:
            if len(notes) > 6:                          # drop the least severe event first
                notes.remove(min(notes, key=lambda n: (n.major, n.severity, -n.game)))
            elif ledger:
                ledger.pop()
            elif recent:
                recent.pop(0)
            elif len(notes) > 1:
                notes.remove(min(notes, key=lambda n: (n.major, n.severity, -n.game)))
            elif summary:
                summary = summary[-max(200, limit * 2):]
            else:
                break
        return build()

    async def write(self, llm: LLMClient, adapter, *, slot: int, today: int, ledger_lines: list[str],
                    name_tokens: dict[int, set[str]], budget: int = 6000, max_tokens: int = 220,
                    common_words: set[str] | None = None, minor_names: set[str] | None = None,
                    lore_lines: list[str] | None = None) -> Entry | None:
        d = self.due(today)
        if d is None:
            return None
        kind, day, notes = d
        msgs = self.build_messages(adapter, kind, day, notes, ledger_lines, budget, lore_lines)
        try:
            c = await llm.complete(msgs, slot=slot, kind="chronicle", max_tokens=max_tokens, stop=[],
                                   temperature=0.7)
        except LLMError:
            return None
        allnames = set().union(*name_tokens.values()) if name_tokens else set()
        s = sanitize_prose(c.text, known_names=allnames, all_names=allnames,
                           prompt_words=prompt_cap_words(msgs[1][1]), common_words=common_words,
                           min_words=8, max_words=150, minor_names=minor_names)
        if not s.ok:
            return None
        game = max(n.game for n in notes)
        e = Entry(day=day, game=game, kind=kind, text=s.text)
        st = self.state
        st.pending = [n for n in st.pending if n not in notes]
        st.recent.append(e)
        st.last_day = max(st.last_day, day)
        self.log.append(e)
        self._append_file(e)
        self.save()
        await self.roll(llm, adapter, slot=slot)
        return e

    async def roll(self, llm: LLMClient, adapter, *, slot: int) -> None:
        """Fold the oldest recent entries into the running summary so the context stays bounded."""
        st = self.state
        while len(st.recent) > KEEP_RECENT or (
                len(st.recent) > 1 and sum(est_tokens(e.text) for e in st.recent) > RECENT_BUDGET_TOKENS):
            old = st.recent.pop(0)
            st.summary = await self._fold(llm, adapter, slot, st.summary, old)
        if len(st.summary.split()) > SUMMARY_MAX_WORDS * 1.5:
            st.summary = await self._fold(llm, adapter, slot, st.summary, None)
        self.save()

    async def _fold(self, llm: LLMClient, adapter, slot: int, summary: str, entry: Entry | None) -> str:
        parts = [f"=== SUMMARY SO FAR ===\n{summary or '(none)'}"]
        if entry is not None:
            parts.append(f"=== ENTRY TO FOLD IN (day {entry.day + 1}) ===\n{entry.text}")
        parts.append(f"=== REQUEST ===\nWrite the new running summary in at most {SUMMARY_MAX_WORDS} words, "
                     "oldest events shortest. One paragraph, no lists.")
        try:
            c = await llm.complete([("system", adapter.chronicle_summary_system), ("user", "\n".join(parts))],
                                   slot=slot, kind="chronicle_summary", max_tokens=SUMMARY_MAX_WORDS * 2,
                                   stop=[], temperature=0.3)
            t = " ".join(c.text.split())
            if len(t.split()) >= 5:
                return " ".join(t.split()[:int(SUMMARY_MAX_WORDS * 1.3)])
        except LLMError:
            pass
        # fallback without the model: keep the tail of the old summary plus the entry's first sentence
        first = re.split(r"(?<=[.!?])\s", entry.text)[0] if entry else ""
        joined = (summary + " " + first).strip()
        return " ".join(joined.split()[-SUMMARY_MAX_WORDS:])

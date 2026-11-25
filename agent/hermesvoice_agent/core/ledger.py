"""Event ledger: long-running facts kept alive by ledger_id, with per-actor knowledge."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable


@dataclass
class LedgerEntry:
    ledger_id: str
    etype: str
    summary: str
    opened_time: int
    known_by: set[int] = field(default_factory=set)
    closed: bool = False


class Ledger:
    def __init__(self, is_end: Callable[[dict], bool] | None = None):
        self.entries: dict[str, LedgerEntry] = {}
        self.is_end = is_end or (lambda e: e.get("type", "").endswith("_end"))

    def apply(self, event: dict, event_actor_ids: list[int] | None = None) -> None:
        event_actor_ids = event_actor_ids or []
        lid = event.get("ledger_id")
        if not lid:
            return
        etype = event.get("type", "")
        is_end = self.is_end(event)
        e = self.entries.get(lid)
        if is_end:
            if e:
                e.closed = True
                self.entries.pop(lid, None)
            return
        if e is None:
            e = self.entries[lid] = LedgerEntry(lid, etype, event.get("summary", ""), event.get("game", 0))
        else:
            e.summary = event.get("summary", e.summary)
        e.known_by.update(int(w) for w in event.get("witnesses", []))
        e.known_by.update(event_actor_ids)

    def load_world(self, entries: list[dict]) -> None:
        """Resync from the world's open ledger entries (after a gap or restart)."""
        for ent in entries:
            lid = ent.get("ledger_id") or ent.get("id")
            if not lid or lid in self.entries:
                continue
            e = LedgerEntry(lid, ent.get("type", ""), ent.get("summary", ""), ent.get("game", 0))
            e.known_by.update(int(x) for x in ent.get("known_by", ent.get("witnesses", [])))
            self.entries[lid] = e

    def tell(self, ledger_id: str, actor_id: int) -> None:
        if ledger_id in self.entries:
            self.entries[ledger_id].known_by.add(actor_id)

    def known_to(self, actor_id: int) -> list[LedgerEntry]:
        """Entries this actor knows about, in stable order (opening game, then id)."""
        return sorted((e for e in self.entries.values() if actor_id in e.known_by),
                      key=lambda e: (e.opened_time, e.ledger_id))

    def clear(self) -> None:
        self.entries.clear()

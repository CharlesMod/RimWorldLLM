"""Event ledger (SPEC §6): long-running facts kept alive by ledger_id, per-pawn knowledge."""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class LedgerEntry:
    ledger_id: str
    etype: str
    summary: str
    opened_tick: int
    known_by: set[int] = field(default_factory=set)
    closed: bool = False


class Ledger:
    def __init__(self):
        self.entries: dict[str, LedgerEntry] = {}

    def apply(self, event: dict) -> None:
        lid = event.get("ledger_id")
        if not lid:
            return
        etype = event.get("type", "")
        is_end = etype.endswith("_end") or etype in ("raid_end", "lodger_leave")
        e = self.entries.get(lid)
        if is_end:
            if e:
                e.closed = True
                self.entries.pop(lid, None)
            return
        if e is None:
            e = self.entries[lid] = LedgerEntry(lid, etype, event.get("summary", ""), event.get("tick", 0))
        else:
            e.summary = event.get("summary", e.summary)
        e.known_by.update(int(w) for w in event.get("witnesses", []))
        e.known_by.update(int(p["id"]) for p in event.get("pawns", []))

    def load_colony(self, colony: dict, known_by_all: list[int] | None = None) -> None:
        """Resync from GET /colony ledger entries (after a gap or restart)."""
        for ent in colony.get("ledger", []):
            lid = ent.get("ledger_id") or ent.get("id")
            if not lid or lid in self.entries:
                continue
            e = LedgerEntry(lid, ent.get("type", ""), ent.get("summary", ""), ent.get("tick", 0))
            e.known_by.update(int(x) for x in ent.get("known_by", ent.get("witnesses", [])))
            self.entries[lid] = e

    def tell(self, ledger_id: str, pawn_id: int) -> None:
        if ledger_id in self.entries:
            self.entries[ledger_id].known_by.add(pawn_id)

    def known_to(self, pawn_id: int) -> list[LedgerEntry]:
        """Entries this pawn knows about, in stable order (opening tick, then id)."""
        return sorted((e for e in self.entries.values() if pawn_id in e.known_by),
                      key=lambda e: (e.opened_tick, e.ledger_id))

    def clear(self) -> None:
        self.entries.clear()

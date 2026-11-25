"""RimWorld's rule tables for ongoing concerns and grudges (core/concerns.py, core/grudges.py).

Births and resolutions are decided from event fields only (importance from the game's severity, never a model call).
Texts are first person, about ten words, with "{who}" standing for the first pawn in "about".
"""
from __future__ import annotations

import hashlib
from typing import Callable

from . import builders

INJURY_MIN = 0.4           # an injury this severe (game severity) can worry someone who loves the victim
GUARD_ONE_IN = 4           # a captured prisoner is on one colonist in four's mind (deterministic by ids)
OFFENCES = {"Insult": "insulted", "Slight": "slighted"}
ABRASIVE = {"abrasive", "psychopath", "bloodlust"}
KIND = {"kind"}


def _roles(e: dict, *roles: str) -> list[int]:
    return [int(a["id"]) for a in e.get("actors", []) if a.get("role") in roles]


def births(e: dict, holders: list[int], loved: Callable[[int, int], bool]) -> list[dict]:
    t = e.get("type", "")
    data = e.get("data") or {}
    sev = float(e.get("severity", 0.0))
    out: list[dict] = []
    if t == "injury" and sev >= INJURY_MIN:
        for v in _roles(e, "victim")[:1]:
            for h in holders:
                if h != v and loved(h, v):
                    out.append({"actor": h, "text": "I can't stop worrying about {who}; they are hurt.",
                                "about": [v], "kind": "hurt", "hint": f"healed:{v}", "importance": sev})
    elif t == "death" and not data.get("animal"):
        for v in _roles(e, "victim")[:1]:
            for h in holders:
                if h != v and loved(h, v):
                    out.append({"actor": h, "text": "I can't believe {who} is gone.", "about": [v],
                                "kind": "grief", "hint": "", "importance": 0.9})
    elif t == "mental_break":
        for v in _roles(e, "subject")[:1]:
            for h in holders:
                if h != v and loved(h, v):
                    out.append({"actor": h, "text": "I'm worried about {who}; they broke down.", "about": [v],
                                "kind": "break", "hint": "", "importance": max(0.4, sev)})
    elif t == "raid_start" and e.get("ledger_id"):
        for h in [int(w) for w in e.get("witnesses", [])]:
            if h in holders:
                out.append({"actor": h, "text": "Raiders are at our gates; I fear for us all.", "about": [],
                            "kind": "threat", "hint": f"ledger:{e['ledger_id']}", "importance": max(0.5, sev)})
    elif t == "prisoner" and data.get("phase") == "captured":
        for p in _roles(e, "prisoner")[:1]:
            for h in [int(w) for w in e.get("witnesses", [])]:
                if h in holders and h != p and int(hashlib.sha1(f"{p}:{h}".encode()).hexdigest(), 16) % GUARD_ONE_IN == 0:
                    out.append({"actor": h, "text": "A prisoner is held here; I keep an eye on {who}.", "about": [p],
                                "kind": "prisoner", "hint": f"prisoner:{p}", "importance": 0.35})
    return out


def resolutions(e: dict, is_ledger_end: Callable[[dict], bool]) -> list[tuple[str, str]]:
    """(key, what happened): every concern whose resolve_hint equals a key is resolved."""
    t = e.get("type", "")
    data = e.get("data") or {}
    summary = str(e.get("summary", "")).strip() or t
    out: list[tuple[str, str]] = []
    if is_ledger_end(e) and e.get("ledger_id"):
        out.append((f"ledger:{e['ledger_id']}", summary))
    if t == "tend":
        out += [(f"healed:{p}", summary) for p in _roles(e, "patient")]
    elif t == "death":
        out += [(f"healed:{p}", summary) for p in _roles(e, "victim")]        # a worry about the dead ends as grief
        out += [(f"prisoner:{p}", summary) for p in _roles(e, "victim")]
    elif t == "prisoner" and data.get("phase") in ("released", "recruited", "died"):
        out += [(f"prisoner:{p}", summary) for p in _roles(e, "prisoner")]
    return out


def social_offence(e: dict) -> tuple[int, int, str] | None:
    """(offender, victim, verb: what the offender did to the victim) for an Insult/Slight interaction, or a social fight that names its target."""
    data = e.get("data") or {}
    t = e.get("type", "")
    if t == "social" and data.get("interaction") in OFFENCES:
        a, b = _roles(e, "initiator"), _roles(e, "recipient")
        if a and b:
            return a[0], b[0], OFFENCES[data["interaction"]]
    if t == "mental_break" and str(data.get("state", "")) == "SocialFighting" and data.get("target") is not None:
        a = _roles(e, "subject")
        if a and int(data["target"]) != a[0]:
            return a[0], int(data["target"]), "picked a fight with"
    return None


def style(snap: dict) -> str:
    defs = builders._trait_defs(snap)
    if defs & ABRASIVE:
        return "abrasive"
    if defs & KIND:
        return "kind"
    return ""

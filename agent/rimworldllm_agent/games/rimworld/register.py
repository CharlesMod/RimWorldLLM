"""Speech register: how simple or articulate a pawn's words are (vocabulary and sentence complexity, never personality).

Pure functions over a GET /pawn snapshot, no I/O. Two layers:
- ``base_register``: from LASTING factors (Intellectual skill, passion, traits, genes, dementia, age). Frozen in block B.
- ``temp_states``: from the CURRENT health and needs (drunk, drugged, dazed, head injury, sore mouth, exhausted).
  They live only in the scene and last only while the snapshot shows them.

The weights are a first cut and all sit in the tables below so they are easy to tune.
"""
from __future__ import annotations

import math

from ...core.toolloop.scale import Scale

REGISTER = Scale.of("register", [
    "very simple words, short broken sentences, sometimes mixes up words",
    "simple words and short plain sentences",
    "plain everyday speech",
    "clear and well-spoken, a good vocabulary",
    "articulate, a wide vocabulary, precise wording",
], start=1)
LO, HI = REGISTER.lo, REGISTER.hi
START = 3.0

# Intellectual skill level (inclusive upper bound) -> shift
_SKILL_BANDS = [(2, -1.0), (7, 0.0), (12, 0.5), (16, 1.0), (20, 1.5)]
_PASSION = {"none": 0.0, "": 0.0, "minor": 0.25, "major": 0.5}
_TOO_SMART = 1.0
# gene def (lowercase) -> shift; label substrings are the fallback
_GENE_DEFS = {
    "aptituderemarkable_intellectual": 1.0, "aptitudestrong_intellectual": 0.5, "learning_fast": 0.25,
    "aptitudepoor_intellectual": -0.5, "aptitudeterrible_intellectual": -1.0, "learning_slow": -0.25,
}
_GENE_LABELS = [("quick study", 0.25), ("slow study", -0.25), ("remarkable intellectual", 1.0),
                ("strong intellectual", 0.5), ("poor intellectual", -0.5), ("terrible intellectual", -1.0)]
_DEMENTIA = -1.5
_DEMENTIA_KEYS = ("dementia", "alzheimer")

# (condition def lowercase) -> (phrase, shift) for a current drug high
_HIGHS = {
    "smokeleafhigh": ("high and drifting", -1), "gojuicehigh": ("wired, fast and clipped", -1),
    "yayohigh": ("wired, fast and clipped", -1), "flakehigh": ("high, thoughts loose and jumpy", -1),
    "psychiteteahigh": ("high, thoughts loose and jumpy", -1), "wakeuphigh": ("wired, fast and clipped", -1),
}


_TEMP_DEFS = {"alcoholhigh", "hangover", "anesthetic", *_HIGHS}


def is_temporary(c) -> bool:
    """A condition that passes on its own within hours (shown in the scene only, never in the frozen bio)."""
    return isinstance(c, dict) and str(c.get("def", "")).lower() in _TEMP_DEFS


def _clamp(x: float) -> int:
    return max(LO, min(HI, int(math.floor(x + 0.5))))


def _defs(items) -> list[tuple[str, str]]:
    out = []
    for x in items or []:
        if isinstance(x, dict):
            out.append((str(x.get("def", "")).lower(), str(x.get("label", "")).lower()))
        else:
            out.append(("", str(x).lower()))
    return out


def _age_cap(age) -> int:
    if not isinstance(age, (int, float)) or isinstance(age, bool):
        return HI
    if age < 8:
        return 1
    if age < 13:
        return 2
    if age < 18:
        return 4
    return HI


def base_score(p: dict) -> float:
    """The raw score before rounding and the age cap (start 3)."""
    s = START
    for sk in p.get("skills") or []:
        if str(sk.get("skill", "")).lower() == "intellectual":
            lvl = sk.get("level")
            if isinstance(lvl, (int, float)):
                s += next((w for hi, w in _SKILL_BANDS if lvl <= hi), _SKILL_BANDS[-1][1])
            s += _PASSION.get(str(sk.get("passion", "")).lower(), 0.0)
    for d, l in _defs(p.get("traits")):
        if d == "toosmart" or l == "too smart":
            s += _TOO_SMART
    for d, l in _defs(p.get("genes")):
        if d in _GENE_DEFS:
            s += _GENE_DEFS[d]
        else:
            s += next((w for key, w in _GENE_LABELS if key in l), 0.0)
    if _has_dementia(p):
        s += _DEMENTIA
    return s


def _has_dementia(p: dict) -> bool:
    for d, l in _defs((p.get("health") or {}).get("conditions")):
        if any(k in d or k in l for k in _DEMENTIA_KEYS):
            return True
    return False


def base_register(p: dict) -> int:
    """The lasting register bin, 1..5."""
    return min(_clamp(base_score(p)), _age_cap(p.get("age")))


def temp_states(p: dict) -> list[tuple[str, int]]:
    """(phrase, shift) for what the snapshot shows right now, worst first. Empty when nothing applies."""
    h = p.get("health") or {}
    needs = p.get("needs") or {}
    out: list[tuple[str, int]] = []
    anesthetic = False
    brain = False
    for c in h.get("conditions") or []:
        if not isinstance(c, dict):
            continue
        d, l, part = str(c.get("def", "")).lower(), str(c.get("label", "")).lower(), str(c.get("part", "")).lower()
        if d == "alcoholhigh":
            sev = c.get("severity")
            if isinstance(sev, (int, float)) and sev >= 0.6:
                out.append(("very drunk: words slur badly, barely coherent, much simpler and looser than usual", -2))
            else:
                out.append(("drunk: words slur, simpler and looser than usual", -1))
        elif d in _HIGHS:
            out.append(_HIGHS[d])
        elif d == "anesthetic":
            anesthetic = True
        elif "brain" in part and not any(k in d or k in l for k in _DEMENTIA_KEYS):
            brain = True
    cons = h.get("consciousness")
    if anesthetic or (isinstance(cons, (int, float)) and cons < 0.5):
        out.append(("dazed, struggles to form words", -2))
    elif isinstance(cons, (int, float)) and cons < 0.8:
        out.append(("foggy", -1))
    if brain:
        out.append(("head injury, thoughts come slowly", -1))
    talk = h.get("talking")
    if isinstance(talk, (int, float)) and talk < 0.5:
        out.append(("talking is hard (mouth or jaw hurt): few words, mumbled; this limits how much is said, not the vocabulary", -1))
    rest = needs.get("rest")
    if isinstance(rest, (int, float)) and rest < 0.1:
        out.append(("exhausted", -1))
    out.sort(key=lambda t: t[1])
    return out


def effective_register(p: dict) -> int:
    shift = sum(s for _, s in temp_states(p))
    return max(LO, min(HI, base_register(p) + shift))


def base_words(p: dict) -> str:
    """The lasting register as a phrase (for block B's Words line)."""
    return REGISTER.to_words(base_register(p))


def effective_words(p: dict) -> str:
    return REGISTER.to_words(effective_register(p))


def words_line(phrase: str) -> str:
    """Block B's line: sits beside the voice card's style, never replaces it."""
    return f"Words: {phrase} (your style above still applies)."


def speech_now(p: dict) -> tuple[str, str]:
    """(scene sentence, short phrase) for the current temporary states; ("", "") when there are none."""
    ts = temp_states(p)
    if not ts:
        return "", ""
    phrase = effective_words(p)
    return (f"Right now: {'; '.join(t for t, _ in ts)}. So for now your words are: {phrase}.", phrase)

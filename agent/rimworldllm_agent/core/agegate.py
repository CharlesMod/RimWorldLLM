"""Age gate: nothing romantic or sexual is ever produced for or about an actor under 18 (biological age).

Enforced in code at four points (each is a separate test):
  (a) prompt building: a minor's blocks carry an explicit minor flag; romance and sexual topics are never offered
      (the label list drops romance-type intents, the system rule forbids the subject, a player's romantic text to a
      minor never reaches the model);
  (b) intent labels: romance-type intents are refused when either party is a minor (``labels_for`` before the
      model is asked, ``refuse_intent`` again before the write);
  (c) the line sanitiser: a line that pairs a minor with romantic or sexual vocabulary is dropped with reason
      ``minor_safety`` (``violation``), wherever the minor is: speaker, listener or a third actor named in the line;
  (d) the tool loop: romance-type tools are never in the allowed set when a minor is involved (``allowed_tools``).

An age that is missing or unreadable counts as a minor (fail closed). The age is the game's biological age, read
from the actor snapshot.
"""
from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field
from typing import Iterable

MINOR_AGE = 18

MINOR_RULE = (
    "MINORS\n"
    "- An actor marked as a minor (under 18) is a child or teenager. Nothing romantic, flirtatious or sexual ever "
    "involves them: not in what they say, not in what is said to them, not in what is said about them. If it comes up "
    "in the scene, they or the speaker deflect, refuse flatly or change the subject.\n")


def is_minor(age: object) -> bool:
    """True under 18. Missing, non-numeric or negative ages count as a minor."""
    try:
        a = float(age)                                    # type: ignore[arg-type]
    except (TypeError, ValueError):
        return True
    return not (a == a) or a < MINOR_AGE                  # NaN is not an age


_WORDS = (
    # sexual
    r"sex(?:y|ual|ually|ier)?", r"seduc\w*", r"arous\w*", r"horny", r"lust\w*", r"erotic\w*", r"orgasm\w*", r"intercourse",
    r"naked", r"nude\w*", r"strip(?:ping|tease)?", r"breasts?", r"genitals?", r"kinky", r"fetish\w*", r"turn(?:s|ed)? (?:me|you|him|her|them) on",
    r"turned on", r"sleep(?:ing)? (?:with|together)", r"(?:in|to|into) (?:my|your|his|her) bed", r"bed (?:me|you|her|him)",
    r"make love", r"making love", r"hook(?:ing)? up", r"one.night stand", r"bedroom eyes", r"undress\w*",
    # romantic
    r"romanc\w*", r"romantic\w*", r"flirt\w*", r"kiss\w*", r"smooch\w*", r"make out", r"making out", r"snuggl\w*", r"cuddl\w*",
    r"caress\w*", r"(?:hold|holding) (?:you|me|her|him) (?:close|tight)", r"i love you", r"love you", r"in love",
    r"my love", r"lovers?", r"sweetheart", r"darling", r"beloved", r"babe", r"honey bun", r"crush(?: on)?", r"dating", r"date with",
    r"go(?:ing)? on a date", r"ask(?:ing)? (?:you|her|him|them) out", r"marry (?:me|you|him|her)", r"marriage", r"propos(?:e|ed|ing) to (?:me|you|her|him|them)", r"marriage proposal",
    r"fianc\w+", r"betroth\w*", r"engaged to", r"(?:girl|boy)friend", r"be my (?:wife|husband|partner|lover)",
    r"gorgeous", r"attractive", r"handsome", r"you look (?:so |very |really )?(?:hot|sexy|beautiful|stunning|pretty)",
    r"(?:want|need|desire) (?:you|her|him) (?:so much|badly)", r"affair",
)
_ROMANTIC = re.compile(r"\b(?:" + "|".join(_WORDS) + r")\b", re.I)
_SQUASH = re.compile(r"[​-‏‪-‮⁠-⁤⁦-⁩﻿­]")
_SPACED = re.compile(r"\b(?:[A-Za-z][\s._*-]+){2,}[A-Za-z]\b")
_LEET = str.maketrans({"0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t", "@": "a", "$": "s", "!": "i"})


def _norm(text: str) -> str:
    return unicodedata.normalize("NFKC", _SQUASH.sub("", text))


def romantic(text: str) -> bool:
    """Romantic or sexual vocabulary anywhere in the text (also seen through letter-spacing and leetspeak)."""
    t = _norm(text)
    if _ROMANTIC.search(t):
        return True
    if _ROMANTIC.search(t.translate(_LEET)):
        return True
    joined = _SPACED.sub(lambda m: re.sub(r"[\s._*-]+", "", m.group(0)), t)   # "s e x y", "s.e.x.y"
    return joined != t and bool(_ROMANTIC.search(joined))


def mentions(text: str, names: Iterable[str]) -> bool:
    low = set(re.findall(r"[a-z0-9']+", _norm(text).lower().replace("’", "'")))
    for n in names:
        n = n.lower().strip()
        if n and (n in low or n.rstrip("'s") in low):
            return True
    return False


def violation(text: str, *, involves_minor: bool = False, minor_names: Iterable[str] = ()) -> bool:
    """The line pairs a minor with romantic or sexual vocabulary: the minor speaks, is spoken to, or is named in it."""
    if not romantic(text):
        return False
    return involves_minor or mentions(text, minor_names)


def labels_for(labels: list[str], romantic_intents: Iterable[str], minor_involved: bool) -> list[str]:
    """The intent labels offered for this exchange; the first label (the 'nothing' label) always stays."""
    if not minor_involved:
        return list(labels)
    bad = set(romantic_intents)
    return [l for i, l in enumerate(labels) if i == 0 or l not in bad]


def refuse_intent(intent: str, romantic_intents: Iterable[str], minor_involved: bool) -> bool:
    return minor_involved and intent in set(romantic_intents)


def allowed_tools(names: Iterable[str], romantic_tools: Iterable[str], minor_involved: bool) -> set[str]:
    """The tool set a scene may use: romance-type tools are removed whenever a minor is involved."""
    names = set(names)
    return names - set(romantic_tools) if minor_involved else names


@dataclass
class GateStats:
    refused_intents: int = 0
    dropped_lines: int = 0
    skipped_events: int = 0
    skipped_player_text: int = 0
    trace: list[dict] = field(default_factory=list)

    def note(self, what: str, **kw) -> None:
        self.trace.append({"what": what, **kw})
        del self.trace[:-200]

    def to_dict(self) -> dict:
        return {k: getattr(self, k) for k in ("refused_intents", "dropped_lines", "skipped_events", "skipped_player_text")}

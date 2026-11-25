"""The "Adult content" setting (default OFF). Read from the bridge /config on every refresh (director.refresh_config).

OFF: the system prompt carries RULE, and the sanitiser drops explicit sexual lines (reason ``adult_off``).
ON:  behaviour is unchanged. The age gate (core/agegate.py) is a separate, broader filter and is never relaxed here.
The vocabulary below is deliberately narrow: kisses, love, dates and romance stay in character with the setting off.
"""
from __future__ import annotations

import re

from .agegate import _LEET, _SPACED, _norm

RULE = ("Romance, attraction, affection and relationships are fine and in character. Anything sexual is only implied "
        "and fades to black; never describe it.")

enabled: bool = False       # set by set_enabled(); off until the bridge says otherwise

_WORDS = (
    r"sex(?:ual\w*)?(?= (?:with|scene|act|toy)\b)", r"(?:have|having|had|good|rough|wild) sex", r"sexual\w*", r"intercourse",
    r"orgasm\w*", r"climax(?:ed|ing)?", r"aroused", r"arousal", r"horny", r"erotic\w*", r"fuck\w*", r"shag(?:ging|ged)?",
    r"blow ?jobs?", r"hand ?jobs?", r"cum(?:ming|shot)?", r"penis\w*", r"vagin\w*", r"cocks?", r"dicks?", r"pussy", r"clit\w*",
    r"genitals?", r"genitalia", r"boobs?", r"breasts?", r"nipples?", r"naked", r"nude\w*", r"undress\w*", r"stripp?(?:ing|ed) (?:off|naked)",
    r"thrust(?:ing|s)? (?:into|inside)", r"moan(?:ing|s|ed)? (?:with|in) (?:pleasure|ecstasy)", r"between (?:my|your|her|his) (?:legs|thighs)",
    r"make love", r"making love", r"made love", r"kinky", r"fetish\w*", r"slut\w*", r"whore\w*",
)
_EXPLICIT = re.compile(r"\b(?:" + "|".join(_WORDS) + r")\b", re.I)


def explicit(text: str) -> bool:
    """Explicit sexual vocabulary (also seen through letter-spacing and leetspeak)."""
    t = _norm(text)
    if _EXPLICIT.search(t) or _EXPLICIT.search(t.translate(_LEET)):
        return True
    joined = _SPACED.sub(lambda m: re.sub(r"[\s._*-]+", "", m.group(0)), t)
    return joined != t and bool(_EXPLICIT.search(joined))


def set_enabled(value) -> None:
    global enabled
    enabled = value is True or (isinstance(value, str) and value.lower() == "true")


def off() -> bool:
    return not enabled

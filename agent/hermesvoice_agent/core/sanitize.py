"""Line sanitizer: a failed line is dropped, never shown.

Strips empty thought blocks, quotes, stage directions and a name prefix. Drops refusals and
disclaimers, names of actors that do not exist or are not known to the speaker, and over-long lines.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from .llm import strip_thought

MAX_WORDS = 35

_REFUSAL = re.compile(
    r"\bas an ai\b|\blanguage model\b|\bi(?:'m| am) (?:sorry|unable|not able)\b[^.!?]{0,40}\b(?:help|assist|continue|comply|provide|write|generate)\b"
    r"|\bi (?:can't|cannot|won't|will not|am unable to) (?:help|assist|comply|continue|fulfil+|write|generate|provide|engage|roleplay|create)\b"
    r"|\bi apologi[sz]e\b|\bcontent polic(?:y|ies)\b|\bmy guidelines\b|\bsafety guidelines\b"
    r"|\bnot appropriate (?:for me )?to\b|\bdisclaimer\b|\bfictional (?:scenario|setting)\b|\bin this roleplay\b",
    re.I)
_STAGE = re.compile(r"\*[^*]*\*|\([^)]*\)|\[[^\]]*\]|<[^>]*>")
_NAME_TOKEN = re.compile(r"[A-Z][a-z]+(?:'[a-z]+)?")
# capitalised words that start sentences or are ordinary vocabulary, never treated as names
_SUBJECT_VERB = re.compile(r"^(?:'s|\s+(?:is|was|has|had|will|did|does|doesn't|didn't|won't|can't|says|said|said|went|died|took|saw|knows)\b)")
_COMMON = {
    "i", "i'm", "i'll", "i've", "i'd", "okay", "ok", "yes", "no", "look", "well", "hm", "hmm", "right", "sure",
    "fine", "hey", "listen", "easy", "please", "damn", "oh", "ah", "so", "and", "but", "the", "a", "an", "this",
    "that", "what", "why", "how", "who", "where", "when", "not", "now", "just", "still", "maybe", "never",
    "always", "good", "bad", "stop", "wait", "come", "go", "get", "give", "take", "sit", "eat", "don't", "do",
    "did", "it's", "that's", "there", "here", "we", "you", "they", "he", "she", "my", "your", "our", "his",
    "her", "if", "then", "as", "at", "in", "on", "for", "with", "to", "of", "one", "two", "three", "let's",
    "let", "tell", "see", "hold", "keep", "pass", "move", "nobody", "nothing", "something", "someone",
    "cold", "hot", "dead", "alive", "sorry", "thanks", "thank", "god", "gods", "hell", "mr", "mrs",
    "doctor", "sir", "ma'am", "negative", "affirmative", "ha", "huh", "ugh", "ow", "um", "uh", "yeah", "nope",
    "of", "is", "are", "was", "were", "be", "can", "could", "would", "should", "will", "have", "has", "had",
}


@dataclass
class Sanitized:
    text: str | None
    reason: str = ""      # why it was dropped, "" if kept

    @property
    def ok(self) -> bool:
        return self.text is not None


def name_tokens(full_name: str) -> set[str]:
    """'Nate 'Rook' Hollis' -> {'Nate', 'Rook', 'Hollis'}"""
    return {t for t in re.findall(r"[A-Za-z][A-Za-z']*", full_name.replace("'", " ")) if len(t) > 1}


SECRET_RUN = 5     # consecutive words shared with a private memory that count as quoting it


def _norm_words(t: str) -> list[str]:
    return re.findall(r"[a-z0-9']+", t.lower())


def leaks_secret(text: str, secrets: list[str]) -> bool:
    """True if the line quotes a secret verbatim: a run of SECRET_RUN words in a row (or the whole of a
    shorter secret). Hinting (different words) passes."""
    words = _norm_words(text)
    for sec in secrets:
        sw = _norm_words(sec)
        if not sw:
            continue
        n = min(SECRET_RUN, len(sw))
        grams = {tuple(sw[i:i + n]) for i in range(len(sw) - n + 1)}
        if any(tuple(words[i:i + n]) in grams for i in range(len(words) - n + 1)):
            return True
    return False


def sanitize_line(raw: str, *, speaker_names: set[str], known_names: set[str], all_names: set[str],
                  prompt_words: set[str] | None = None, truncated: bool = False,
                  max_words: int = MAX_WORDS, common_words: set[str] | None = None,
                  secrets: list[str] | None = None) -> Sanitized:
    """
    speaker_names: the speaker's own name tokens.
    known_names:   name tokens of actors the speaker knows (present, relations, memories).
    all_names:     name tokens of every actor that exists in the world.
    prompt_words:  capitalised words from the speaker's prompt (grounded vocabulary: places, factions).
    truncated:     the generation hit the token cap (a cut-off line is trimmed to its last sentence).
    """
    t = strip_thought(raw).strip()
    if not t:
        return Sanitized(None, "empty")
    t = t.split("\n")[0].strip()
    if _REFUSAL.search(t):
        return Sanitized(None, "refusal")
    t = _STAGE.sub(" ", t)
    t = re.sub(r"\s+", " ", t).strip()
    # name prefix "Rook: ..." or "Nate 'Rook' Hollis: ..."
    m = re.match(r"^([A-Z][\w' ]{0,30}):\s+(.*)$", t)
    if m and (name_tokens(m.group(1)) & (speaker_names | all_names)):
        t = m.group(2)
    t = t.strip().strip('"“”‘’`').strip()
    t = re.sub(r"^[-–—>]+\s*", "", t)
    t = t.replace('"', "").replace("“", "").replace("”", "")
    if truncated and not re.search(r"[.!?…]$", t):
        cut = max(t.rfind(". "), t.rfind("! "), t.rfind("? "))
        if cut <= 0:
            return Sanitized(None, "cut_off")
        t = t[: cut + 1]
    if not re.search(r"[A-Za-z]{2}", t):
        return Sanitized(None, "empty")
    if _REFUSAL.search(t):
        return Sanitized(None, "refusal")
    if len(t.split()) > max_words:
        return Sanitized(None, "too_long")
    if secrets and leaks_secret(t, secrets):
        return Sanitized(None, "leaks_secret")
    bad = _bad_name(t, speaker_names, known_names, all_names, prompt_words or set(),
                    {w.lower() for w in (common_words or set())})
    if bad:
        return Sanitized(None, f"unknown_name:{bad}")
    return Sanitized(t)


def _bad_name(t: str, me: set[str], known: set[str], all_names: set[str], prompt_words: set[str],
              extra_common: set[str]) -> str | None:
    lower_all = {n.lower() for n in all_names}
    lower_ok = {n.lower() for n in (known | me)}
    prompt_l = {w.lower() for w in prompt_words}
    for m in _NAME_TOKEN.finditer(t):
        w = m.group(0)
        wl = w.lower()
        if wl in lower_ok:
            continue
        if wl in lower_all:
            return w            # a real actor the speaker does not know
        if wl in _COMMON or wl in extra_common or wl in prompt_l:
            continue
        before = t[: m.start()].rstrip()
        sentence_start = not before or before[-1] in ".!?…"
        if sentence_start:
            # only a vocative at the start ("Zed, get back") counts as a name
            rest = t[m.end():]
            if rest[:1] == "," or _SUBJECT_VERB.match(rest):
                return w
            continue
        return w                # capitalised mid-sentence, not in the prompt: an invented name
    return None


def prompt_cap_words(prefix: str) -> set[str]:
    return set(_NAME_TOKEN.findall(prefix))


def sanitize_prose(raw: str, *, known_names: set[str], all_names: set[str], prompt_words: set[str],
                   common_words: set[str] | None = None, speaker_names: set[str] | None = None,
                   min_words: int = 6, max_words: int = 140) -> Sanitized:
    """A paragraph (diary entry, chronicle, bio): one block, refusals and invented names dropped."""
    flat = " ".join(strip_thought(raw).split())
    s = sanitize_line(flat, speaker_names=speaker_names or set(), known_names=known_names, all_names=all_names,
                      prompt_words=prompt_words, max_words=max_words, common_words=common_words)
    if s.ok and len(s.text.split()) < min_words:
        return Sanitized(None, "too_short")
    return s

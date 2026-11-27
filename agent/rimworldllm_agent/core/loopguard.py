"""Conversation loop guard: a new turn that repeats an earlier line is a validation failure.

Designed out, not mitigated (docs/TOOL-HARNESS.md pattern): every turn is checked against every earlier turn of
its conversation (own lines and the other's alike, which catches both a speaker repeating itself and one
parroting the other) and against the speaker's own last few lines from other conversations. A repeat gets one
retry with the reason in the prompt; if the retry repeats too, the conversation ends with no line (never a
repeat). Every check is counted and traced.

Similarity is on normalised words (lower case, punctuation stripped): the larger of the word 3-gram Jaccard
and the Jaccard of content words (stop words and verbal fillers like "okay so" / "hm" / "copy" removed, so a
speaker's habit opener cannot make two different lines look alike). Lines shorter than ``MIN_WORDS`` are never
judged: "Yes." and "No." are legitimate. Thresholds were tuned on the saved bench conversations
(bench/models/loopcheck.py).
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

MIN_WORDS = 4              # a shorter line can't be called a loop ("Yes.", "Fine.", "Copy that.")
THRESHOLD = 0.6            # inside a conversation
CROSS_THRESHOLD = 0.8      # against the speaker's own lines from other conversations
CROSS_MIN_WORDS = 5
MIN_CONTENT = 2            # content words each side needs for the content measure

_STOP = set(
    "the a an and or but to of in on at for with is are was were be been it its this that i you he she we they my "
    "your his her our their me him us them do does did not no so as if then there here have has had will would can "
    "could just than too very what why how who when where from by about up out im ill ive id i'm i'll don't it's "
    "that's okay hm well copy naturally think like totally".split())
_WORD = re.compile(r"[a-z0-9]+(?:'[a-z]+)?")


def words(text: str) -> list[str]:
    return _WORD.findall(text.lower().replace("’", "'"))


def _grams(w: list[str], n: int) -> set[tuple[str, ...]]:
    n = min(n, len(w))
    return {tuple(w[i:i + n]) for i in range(len(w) - n + 1)} if n else set()


def _jac(a: set, b: set) -> float:
    return len(a & b) / len(a | b) if a and b else 0.0


def similarity(a: str, b: str, *, min_words: int = MIN_WORDS) -> float:
    """0..1; 0 when either line is too short to judge."""
    wa, wb = words(a), words(b)
    if len(wa) < min_words or len(wb) < min_words:
        return 0.0
    if wa == wb:
        return 1.0
    s = _jac(_grams(wa, 3), _grams(wb, 3))
    ca, cb = {w for w in wa if w not in _STOP}, {w for w in wb if w not in _STOP}
    if len(ca) >= MIN_CONTENT and len(cb) >= MIN_CONTENT:
        s = max(s, _jac(ca, cb))
    return s


def contained(short: str, long: str, *, min_words: int = MIN_WORDS) -> bool:
    """The same speaker's new line is a cut-down copy of its own earlier one (all its content words are in it)."""
    ws = words(short)
    if len(ws) < min_words:
        return False
    cs = {w for w in ws if w not in _STOP}
    cl = {w for w in words(long) if w not in _STOP}
    return len(cs) >= MIN_CONTENT and cs <= cl


@dataclass
class Hit:
    kind: str          # "turn" (earlier turn of this conversation) or "own" (the speaker's line in another)
    other: str         # the earlier line it repeats
    score: float
    own: bool = False  # the earlier line was the speaker's own

    @property
    def reason(self) -> str:
        who = "your own earlier line" if self.own else "an earlier line in this conversation"
        return f'it repeats {who}: "{self.other}"'


def find_repeat(text: str, speaker: int, turns: list[tuple[int, str]], own_recent: list[str] | None = None,
                *, threshold: float = THRESHOLD, cross_threshold: float = CROSS_THRESHOLD) -> Hit | None:
    """The first earlier line the new one repeats, or None. ``turns`` is [(speaker id, text)] so far."""
    best: Hit | None = None
    for sp, prev in turns:
        own = sp == speaker
        s = similarity(text, prev)
        if s >= threshold or (own and contained(text, prev)):
            h = Hit("turn", prev, max(s, threshold), own)
            if best is None or h.score > best.score:
                best = h
    if best:
        return best
    for prev in own_recent or []:
        s = similarity(text, prev, min_words=CROSS_MIN_WORDS)
        if s >= cross_threshold:
            return Hit("own", prev, s, True)
    return None


def retry_request(name: str, hit: Hit) -> str:
    """The line request for a retry: the same request, plus why the last attempt was refused."""
    return ("=== REQUEST ===\n"
            f"Write {name}'s next line of speech now. One line only.\n"
            f"Your last attempt was refused because {hit.reason}. Say something new: answer what was just said, "
            "or move the talk on. Do not repeat or rephrase any earlier line.")


@dataclass
class LoopStats:
    checked: int = 0         # turns checked
    caught: int = 0          # first attempts that repeated
    retried: int = 0
    recovered: int = 0       # the retry was clean
    ended: int = 0           # retries exhausted: the conversation ended with no line
    trace: list[dict] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {k: getattr(self, k) for k in ("checked", "caught", "retried", "recovered", "ended")}

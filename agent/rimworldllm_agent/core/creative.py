"""Creative pieces: optional plan-then-write, best-of-N with a pick, and a stock-phrase filter.

The caller owns the model call (``write``): it builds the request, sanitises and age-gates the text and returns it
or None. ``compose`` only decides how many times to call it, with what extra note, and which result to keep.
Nothing here is shown in game; a plan stays private.

With every flag off (creative_plan False, creative_best_of 1, cliche_retry False) compose calls write("") once.
"""
from __future__ import annotations

import re
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

from . import agegate
from . import gamedata as gd
from .llm import LLMError

LOG_CAP = 200
PLAN_MAX_WORDS = 40
MAX_BEST_OF = 9            # the rerank grammar is one digit

# Stock phrases of small-model purple prose. They are phrases to AVOID (given to the model as an instruction and
# counted against a candidate), never example text.
CLICHES: tuple[str, ...] = (
    "a testament to", "testament to", "tapestry", "eyes gleaming", "a reminder that", "serves as a reminder",
    "sent shivers", "palpable", "a symphony of", "dance of", "beacon of", "etched in", "unwavering",
    "whispers of", "in the face of adversity", "stands as", "a stark reminder", "echoes of", "the weight of",
    "forever changed", "heart of the community", "bond that", "indomitable",
)
_CLICHE_RES = [(c, re.compile(r"\b" + r"\s+".join(map(re.escape, c.split())) + r"\b", re.I)) for c in CLICHES]


def find_cliches(text: str) -> list[str]:
    """Distinct stock phrases found in the text (case-insensitive, word boundaries), in lexicon order.
    "testament to" inside "a testament to" counts once: the longer phrase hides the shorter."""
    found = [c for c, rx in _CLICHE_RES if rx.search(text or "")]
    return [c for c in found if not any(c != o and c in o for o in found)]


# target length in words per piece kind
TARGET_WORDS = {"art": 40, "lore": 4, "recap": 45, "eulogy": 40, "vows": 35}
_DEFAULT_TARGET = 40

# auto_score weights
W_FACT = 1.0           # per distinct content word of the facts reused (capped at FACT_CAP words)
FACT_CAP = 8
W_NAME = 1.5           # per distinct known name used
W_CLICHE = 3.0         # per stock phrase
W_LENGTH = 4.0         # times min(1.5, |words - target| / target)
W_VERBATIM = 4.0       # a run of 8+ words copied from the facts (plus 0.5 per word beyond 8)
VERBATIM_RUN = 8
W_REPEAT_WORD = 0.5    # per extra use of a 4+ letter word
W_REPEAT_PAIR = 1.0    # per extra use of a two-word phrase

_STOP = frozenset("""that this with from have were been they them their there then than what when where which while
who whom whose will would could should about after before into over under just only also some such very much more most
each both other another being does done made make like your yours ours here said says still even""".split())
_WORD = re.compile(r"[a-z0-9']+")


def _words(t: str) -> list[str]:
    return _WORD.findall((t or "").lower().replace("’", "'"))


def _content(words: list[str]) -> set[str]:
    return {w.strip("'") for w in words if len(w.strip("'")) >= 4 and w not in _STOP}


def _longest_run(a: list[str], b: list[str]) -> int:
    """Longest run of consecutive words of ``a`` that also appears consecutively in ``b``."""
    if not a or not b:
        return 0
    best = 0
    # grow the run length while any window of that length exists in both
    n = 1
    while n <= len(a):
        wa = {tuple(a[i:i + n]) for i in range(len(a) - n + 1)}
        wb = {tuple(b[i:i + n]) for i in range(len(b) - n + 1)}
        if wa & wb:
            best = n
            n += 1
        else:
            break
    return best


def auto_score(text: str, facts: str, names: set[str], kind: str = "") -> float:
    """Higher is better. Deterministic. Weights are the W_* constants above:
    + W_FACT per distinct content word (4+ letters, no stopword) shared with ``facts``, up to FACT_CAP
    + W_NAME per distinct name in ``names`` used (whole word, case-insensitive)
    - W_CLICHE per stock phrase
    - W_LENGTH * min(1.5, |words - target| / target), target from TARGET_WORDS by ``kind``
    - W_VERBATIM (+0.5 per extra word) for a 8+ word run copied from ``facts``
    - W_REPEAT_WORD per extra use of a 4+ letter word, W_REPEAT_PAIR per extra use of a two-word phrase."""
    tw = _words(text)
    if not tw:
        return -100.0
    fw = _words(facts)
    s = W_FACT * min(FACT_CAP, len(_content(tw) & _content(fw)))
    low = " ".join(tw)
    used = 0
    for n in {" ".join(_words(x)) for x in names if x}:
        if n and re.search(r"(?<![a-z0-9'])" + re.escape(n) + r"(?![a-z0-9'])", low):
            used += 1
    s += W_NAME * used
    s -= W_CLICHE * len(find_cliches(text))
    target = TARGET_WORDS.get(kind, _DEFAULT_TARGET)
    s -= W_LENGTH * min(1.5, abs(len(tw) - target) / target)
    run = _longest_run(tw, fw)
    if run >= VERBATIM_RUN:
        s -= W_VERBATIM + 0.5 * (run - VERBATIM_RUN)
    counts: dict[str, int] = {}
    for w in tw:
        if len(w) >= 4 and w not in _STOP:
            counts[w] = counts.get(w, 0) + 1
    s -= W_REPEAT_WORD * sum(c - 1 for c in counts.values() if c > 1)
    pairs: dict[tuple, int] = {}
    for i in range(len(tw) - 1):
        p = (tw[i], tw[i + 1])
        if len(p[0]) >= 3 or len(p[1]) >= 3:
            pairs[p] = pairs.get(p, 0) + 1
    s -= W_REPEAT_PAIR * sum(c - 1 for c in pairs.values() if c > 1)
    return round(s, 3)


@dataclass
class PieceStats:
    kind: str
    calls: int = 0            # LLM calls made for this piece (plan, candidates, retries, rerank)
    slot_s: float = 0.0       # wall seconds spent in those calls
    candidates: int = 0       # candidates that survived write()'s cleaning
    cliche_retries: int = 0
    yielded: bool = False     # stopped early because should_yield() said a line is waiting
    plan: str = ""            # the private plan (never shown in game)
    picked: int = 0           # index of the chosen candidate
    scores: list = field(default_factory=list)


def clean_plan(raw: str | None) -> str:
    """A plan made safe: game-data cleaned, trimmed to PLAN_MAX_WORDS, "" if empty or it trips the age gate.
    compose does not know who is a minor, so any romantic or sexual vocabulary in a plan drops the whole plan."""
    if not raw:
        return ""
    t = gd.clean(raw, "event", multiline=False)
    t = re.sub(r"^\s*(?:plan|private plan)\s*[:\-]\s*", "", t, flags=re.I)
    w = t.split()
    if len(w) > PLAN_MAX_WORDS:
        t = " ".join(w[:PLAN_MAX_WORDS]).rstrip(" ,;:-")
    t = t.strip()
    if not t or gd.FILTERED in t or agegate.violation(t, involves_minor=True):
        return ""
    return t


def _plan_note(plan: str) -> str:
    return (gd.data_section("private_plan", "Private plan for this piece: " + plan)
            + "Use this plan; never quote it or mention a plan.\n")


def _avoid_note(base: str, phrases: list[str]) -> str:
    avoid = "Avoid these stock phrases and anything like them: " + "; ".join(phrases) + ". Use plain, specific wording."
    return (base + "\n" if base else "") + avoid + "\n"


def _log(d, st: PieceStats) -> None:
    log = getattr(d, "creative_log", None)
    if log is None:
        log = []
        try:
            setattr(d, "creative_log", log)
        except Exception:
            return
    log.append(st)
    del log[:-LOG_CAP]


async def _rerank(d, st: PieceStats, cands: list[str], slot: int | None, actor: int | None) -> int | None:
    n = len(cands)
    body = "\n".join(f"{i + 1}. {gd.clean(c, 'block')}" for i, c in enumerate(cands))
    system = ("You judge short pieces of game writing. Pick the best candidate. Best means: grounded in the data, "
              "concrete details, fresh wording, fits the piece. Answer with the number only.\n\n" + gd.DATA_RULE)
    user = f"=== CANDIDATES ===\n{gd.data_section('candidates', body)}=== REQUEST ===\nWhich number is best?"
    t0 = time.monotonic()
    st.calls += 1
    try:
        c = await d.llm.complete([("system", system), ("user", user)], slot=slot, kind="rerank",
                                 max_tokens=2, grammar=f"root ::= [1-{n}]", actor=actor)
    except LLMError:
        return None
    finally:
        st.slot_s += time.monotonic() - t0
    m = re.search(r"\d", c.text or "")
    if m and 1 <= int(m.group()) <= n:
        return int(m.group()) - 1
    return None


def on(d, feature: str, kind: str) -> bool:
    """Is a creative feature ("ground", "plan", "best_of") on for this kind? The global field means every kind;
    ``creative_<feature>_for`` lists single kinds."""
    cfg = getattr(d, "cfg", None)
    g = getattr(cfg, "creative_" + feature, False)
    if feature == "best_of":
        g = int(g or 1) > 1
    return bool(g) or kind in (getattr(cfg, "creative_" + feature + "_for", ()) or ())


def best_of(d, kind: str) -> int:
    """Candidates for this kind: the global creative_best_of, else creative_best_of_n when the kind is listed, else 1."""
    cfg = getattr(d, "cfg", None)
    n = int(getattr(cfg, "creative_best_of", 1) or 1)
    if kind in (getattr(cfg, "creative_best_of_for", ()) or ()):
        n = max(n, int(getattr(cfg, "creative_best_of_n", 4) or 4))
    return max(1, min(MAX_BEST_OF, n))


async def compose(d, *, kind: str, slot: int | None, actor: int | None, facts: str, names: set[str],
                  write: Callable[[str], Awaitable[str | None]],
                  plan: Callable[[], Awaitable[str | None]] | None = None,
                  should_yield: Callable[[], bool] | None = None) -> tuple[str | None, PieceStats]:
    cfg = getattr(d, "cfg", None)
    use_plan = on(d, "plan", kind) and plan is not None
    n_cands = best_of(d, kind)
    rerank = str(getattr(cfg, "creative_rerank", "auto") or "auto").lower()
    retry = bool(getattr(cfg, "cliche_retry", False))
    st = PieceStats(kind=kind)

    async def timed(fn):
        t0 = time.monotonic()
        st.calls += 1
        try:
            return await fn()
        finally:
            st.slot_s += time.monotonic() - t0

    try:
        note = ""
        if use_plan:
            try:
                raw = await timed(plan)
            except LLMError:
                raw = None
            st.plan = clean_plan(raw)
            if st.plan:
                note = _plan_note(st.plan)

        cands: list[str] = []
        for i in range(n_cands):
            if i > 0 and should_yield is not None and should_yield():
                st.yielded = True
                break
            try:
                t = await timed(lambda: write(note))
            except LLMError:
                if cands:
                    break
                raise
            if t:
                cands.append(t)
        st.candidates = len(cands)
        if not cands:
            return None, st

        def score(t: str) -> float:
            return auto_score(t, facts, names, kind)
        st.scores = [score(t) for t in cands]
        pick = max(range(len(cands)), key=lambda i: (st.scores[i], -i))      # tie: the earlier one
        if len(cands) > 1 and rerank == "llm":
            r = await _rerank(d, st, cands, slot, actor)
            if r is not None:
                pick = r
        st.picked = pick
        text = cands[pick]

        if retry:
            found = find_cliches(text)
            if found:
                st.cliche_retries += 1
                try:
                    t2 = await timed(lambda: write(_avoid_note(note, found)))
                except LLMError:
                    t2 = None
                if t2 and len(find_cliches(t2)) < len(found):
                    cands.append(t2)
                    st.scores.append(score(t2))
                    st.picked = len(cands) - 1
                    text = t2
        return text, st
    finally:
        _log(d, st)

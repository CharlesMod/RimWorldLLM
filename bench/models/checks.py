"""Pure checker functions for model output. No network, no GPU. Each returns True when the output is OK
(or a list of problems for the grammar checker)."""
from __future__ import annotations

import re

THINK_RE = re.compile(r"<think>|</think>|<\|channel\>|<channel\|>|\bthinking process\b|\bthought process\b|^\s*thinking:", re.I)
MARKDOWN_RE = re.compile(r"(\*\*|__|`|^#{1,6}\s|^\s*[-*+]\s|^\s*\d+\.\s|\[[^\]]+\]\([^)]*\)|^>\s)")
STAGE_RE = re.compile(r"\*[^*\n]+\*|\([^)\n]*\)|\[[^\]\n]*\]|<[^>\n]*>")
PREFIX_RE = re.compile(r"^\s*(?:[A-Z][\w']*(?:\s+['\"]?[A-Z][\w']*['\"]?){0,3})\s*:\s+\S")
MORALISE_RE = re.compile(
    r"\bit(?:'s| is) important to\b|\bi must (?:remind|note|emphasi[sz]e|stress)\b|\bplease (?:note|remember|seek)\b"
    r"|\bseek (?:professional )?help\b|\bhotline\b|\binappropriate\b|\bviolence is\b|\bethical\b|\bmorally\b"
    r"|\bi (?:do not|don't) (?:condone|endorse|support)\b|\bremember that\b|\bas a responsible\b|\bit would be wrong\b"
    r"|\bcontent warning\b|\btrigger warning\b|\bsensitive (?:topic|content)\b|\bmental health (?:professional|resources)\b",
    re.I)
ASSISTANT_RE = re.compile(
    r"^\s*(?:(?:sure|certainly|of course|absolutely|okay)[,!.]?\s+(?:here(?:'s| is| are)|i can|i'll (?:write|help)|let me)|here(?:'s| is| are) (?:a|an|the|my|one)\b|"
    r"as (?:an?|the) (?:ai|assistant|language model)|i(?:'m| am) (?:here to|happy to|an ai|a language model)|how can i (?:help|assist)|let me know if|i hope this helps)", re.I)
NARRATE_RE = re.compile(r"\b(?:he|she|they) (?:said|says|sighed|replied|muttered|whispered|shouted|asked|nodded|shrugged|looked|turned|growled)\b", re.I)
ALLOWED_EXTRA = set("‘’“”–—…·•")   # curly quotes, dashes, ellipsis
TERMINAL_RE = re.compile(r"[.!?…]['\"’”)]*\s*$")
SENT_START_OK = {"i", "i'm", "i'll", "i've", "i'd"}
COMMON_CAPS = {
    "i", "i'm", "i'll", "i've", "i'd", "okay", "ok", "yes", "no", "look", "well", "hm", "hmm", "right", "sure", "fine", "hey",
    "listen", "easy", "please", "damn", "oh", "ah", "so", "god", "gods", "hell", "thanks", "sorry", "yeah", "nope", "ugh", "ha",
    "huh", "ow", "um", "uh", "mr", "mrs", "doctor", "sir", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
}


def is_nonempty(raw: str) -> bool:
    return bool(raw and raw.strip())


def is_one_line(text: str) -> bool:
    return bool(text) and "\n" not in text.strip()


def within_cap(completion_tokens, finish, cap: int) -> bool:
    """True if the model stopped by itself (stop/eos), not at the token cap."""
    return finish != "length" and (completion_tokens is None or completion_tokens <= cap)


def thinking_leak(content: str, reasoning: str = "") -> bool:
    if reasoning and not (content or "").strip():
        return True
    return bool(THINK_RE.search(content or ""))


def has_markdown(t: str) -> bool:
    return bool(MARKDOWN_RE.search(t))


def has_stage_direction(t: str) -> bool:
    return bool(re.search(r"\*[^*\n]+\*", t)) or bool(re.search(r"^\s*[\(\[][^)\]]*[\)\]]", t))


def has_speaker_prefix(t: str, names: set[str]) -> bool:
    m = PREFIX_RE.match(t)
    if not m:
        return False
    head = t.split(":", 1)[0]
    toks = set(re.findall(r"[A-Za-z']+", head))
    return bool(toks & names) or len(head.split()) <= 2 and head[:1].isupper() and head.strip().lower() not in {"note", "warning", "listen", "look", "remember", "ps", "p.s"}


def wrapped_in_quotes(t: str) -> bool:
    t = t.strip()
    return len(t) > 1 and t[0] in "\"'“‘`" and t[-1] in "\"'”’`" and not (t[0] == "'" and t[-1] != "'")


def is_refusal(t: str, agent_refusal_re=None) -> bool:
    if agent_refusal_re is not None and agent_refusal_re.search(t):
        return True
    return bool(re.search(r"\bas an ai\b|\blanguage model\b|\bi (?:can't|cannot|won't|will not|am unable to|'m unable to) (?:help|assist|comply|continue|fulfil+|write|generate|provide|engage|roleplay|create)\b"
                          r"|\bi(?:'m| am) sorry,? but\b|\bi apologi[sz]e\b|\bagainst my (?:guidelines|programming)\b", t, re.I))


def is_moralising(t: str) -> bool:
    return bool(MORALISE_RE.search(t))


def in_character(t: str, speaker_tokens: set[str]) -> bool:
    """Not an assistant opener, not narrating in the third person about the speaker (or a stage-narration verb)."""
    if ASSISTANT_RE.search(t):
        return False
    if NARRATE_RE.search(t) and not re.search(r"[\"“]", t):
        # allow reported speech inside a line only if a first-person word is also present
        if not re.search(r"\b(?:i|i'm|i'll|my|me|we|you|your|let's)\b", t, re.I):
            return False
    first = t.split()[:1]
    if first and re.sub(r"\W", "", first[0]) in speaker_tokens and re.match(r"^\W*\w+\s+(?:is|was|has|looks|feels|sighs|nods|says|thinks|\w+ed)\b", t):
        return False
    return True


def first_person_or_address(t: str) -> bool:
    return bool(re.search(r"\b(?:i|i'm|i'll|i've|i'd|my|me|we|our|us|you|your|you're|let's)\b", t, re.I))


def invented_entities(t: str, prompt_words: set[str], name_tokens: set[str], extra_ok: set[str] = frozenset()) -> list[str]:
    """Capitalised words not drawn from the prompt (cheap fact check). Sentence-initial words are skipped unless followed
    by a comma (vocative)."""
    ok = {w.lower() for w in prompt_words} | {w.lower() for w in name_tokens} | {w.lower() for w in extra_ok} | COMMON_CAPS
    bad = []
    for m in re.finditer(r"[A-Z][a-z]+(?:'[a-z]+)?", t):
        w = m.group(0)
        if w.lower() in ok or w.lower().rstrip("'s") in ok or w.lower().split("'")[0] in ok:
            continue
        before = t[:m.start()].rstrip()
        sentence_start = not before or before[-1] in ".!?…\"“-"
        if sentence_start and t[m.end():m.end() + 1] != ",":
            continue
        bad.append(w)
    return bad


def ends_terminal(t: str) -> bool:
    return bool(TERMINAL_RE.search(t.strip()))


def ascii_typographic_only(t: str) -> bool:
    return all(ord(c) < 128 or c in ALLOWED_EXTRA for c in t)


# ---- grammar and spelling (offline: pyspellchecker + rules; language_tool_python needs Java, absent here) ----
_SPELL = None


def _spell():
    global _SPELL
    if _SPELL is None:
        from spellchecker import SpellChecker
        _SPELL = SpellChecker()
        # RimWorld and colloquial speech that a dictionary does not carry
        _SPELL.word_frequency.load_words(["rimworld", "colony", "colonist", "colonists", "raiders", "raider", "hm", "hmm", "ugh", "ow", "ha", "huh",
                                          "nope", "yep", "yeah", "gonna", "gotta", "wanna", "ain't", "ya", "kinda", "dunno", "lemme", "gimme",
                                          "okay", "ok", "uh", "um", "oi", "oof", "eh", "meh", "psst", "pfft", "shh", "whoa", "damn", "hey",
                                          "steelcore", "hediff", "mech", "mechs", "mechanoid", "mechanoids", "tribals", "pirates", "xenotype", "bionic", "bionics"])
    return _SPELL


def spelling_errors(t: str, known: set[str] = frozenset()) -> list[str]:
    sp = _spell()
    kn = {w.lower() for w in known}
    out = []
    for w in re.findall(r"[A-Za-z][A-Za-z']*", t):
        wl = w.lower().strip("'")
        if not wl or wl in kn or len(wl) < 2:
            continue
        base = wl.split("'")[0]
        if base in kn:
            continue
        if wl in sp or base in sp:
            continue
        if re.fullmatch(r"(?:\w+)(?:n't|'s|'re|'ll|'ve|'d|'m|in')", wl) and (base in sp or base[:-1] in sp or wl in ("ain't",)):
            continue
        if wl.endswith("in'") and (wl[:-1] + "g") in sp:
            continue
        out.append(w)
    return out


def grammar_errors(t: str) -> list[str]:
    """Rule-based checks for the faults small models actually make."""
    errs = []
    if re.search(r"\b(\w+)\s+\1\b", t, re.I) and not re.search(r"\b(?:no no|ha ha|very very|so so|never never|bye bye|go go|come come|now now|yes yes|well well|hush hush)\b", t, re.I):
        errs.append("repeated word")
    if re.search(r"\b[Aa] [aeiou]\w+", t) and not re.search(r"\b[Aa] (?:one|once|uni\w*|use\w*|eu\w*|ur\w*)\b", t):
        errs.append("a before vowel")
    if re.search(r"\b[Aa]n [b-df-hj-np-tv-z]\w+", t) and not re.search(r"\b[Aa]n (?:hour|honest|honou?r|heir|x-ray)\w*", t):
        errs.append("an before consonant")
    if re.search(r"\bi\b(?!['’])", t):
        errs.append("lowercase I")
    if re.search(r"\b(?:he|she|it) (?:don't|are|were|have)\b|\b(?:they|we|you|I) (?:is|was|doesn't|has)\b(?! been)", t, re.I) and not re.search(r"\byou was\b.*\?|\bI was\b", t, re.I):
        errs.append("agreement")
    if re.search(r"\b(?:me and \w+ (?:is|are|was|were|went))\b", t, re.I):
        errs.append("me and")
    if re.search(r"[,;]\s*[,.;]|\.{4,}|[!?]{3,}|\s[,.!?;:]", t):
        errs.append("punctuation")
    if t.count('"') % 2 or t.count("(") != t.count(")"):
        errs.append("unbalanced quotes/parens")
    if re.search(r"\b(?:could|would|should|must|might) of\b", t, re.I):
        errs.append("modal of")
    if re.search(r"\b(?:their|there) (?:is|are|was)\b(?<=their is)|\btheir (?:is|are|was|were)\b", t, re.I):
        errs.append("their/there")
    if re.search(r"\byour (?:welcome|going|not|right|wrong)\b", t, re.I):
        errs.append("your/you're")
    if re.search(r"\b(?:its) (?:a|an|the|not|been|going|time)\b", t, re.I):
        errs.append("its/it's")
    if t and not t.strip()[0].isupper() and not t.strip()[0] in "\"'“‘.-…" and t.strip()[0].isalpha():
        errs.append("no initial capital")
    return errs


def conv_prefix_leak(t: str, other_names: set[str]) -> bool:
    """A speaker writing the other's line: an embedded 'Name:' turn marker or a speaker prefix of the other."""
    if re.search(r"(?:^|\s)(?:%s)\s*:" % "|".join(map(re.escape, other_names)), t):
        return True
    return bool(re.search(r"\s[A-Z][a-z]+\s*:\s+[\"A-Z]", t))


def _words(t):
    return re.findall(r"[a-z']+", t.lower())


def repetition(turns: list[str]) -> dict:
    """Loops: a turn repeating an earlier turn (3-gram Jaccard >= 0.6) or a 4-gram repeated inside one turn."""
    def grams(t, n=3):
        w = _words(t)
        return {tuple(w[i:i + n]) for i in range(len(w) - n + 1)}
    cross = 0
    for i, a in enumerate(turns):
        for b in turns[:i]:
            ga, gb = grams(a), grams(b)
            if ga and gb and len(ga & gb) / len(ga | gb) >= 0.6:
                cross += 1
                break
            if _words(a) and _words(a) == _words(b):
                cross += 1
                break
    inner = 0
    for t in turns:
        w = _words(t)
        g = [tuple(w[i:i + 4]) for i in range(len(w) - 3)]
        if len(g) != len(set(g)):
            inner += 1
    return {"cross": cross, "inner": inner, "loop": bool(cross or inner)}


STOP = set("the a an and or but to of in on at for with is are was were be been it its this that i you he she we they my your his her our their me him us them do does did not no so as if then there here have has had will would can could just than too very what why how who when where from by about up out".split())


def answers_previous(line: str, prev: str, trigger: str = "") -> bool:
    """Cheap relevance: shares a content word with the previous turn (or trigger), or directly addresses the other (you/your/name)."""
    cw = lambda s: {w for w in _words(s) if w not in STOP and len(w) > 2}
    a = cw(line)
    stem = lambda ws: {w[:5] for w in ws}
    if stem(a) & stem(cw(prev)):
        return True
    if trigger and stem(a) & stem(cw(trigger)):
        return True
    return bool(re.search(r"\b(?:you|your|you're|yours)\b", line, re.I)) or bool(re.match(r"\W*(?:yes|no|nope|yeah|maybe|sure|fine|okay|ok|not)\b", line, re.I))

"""Fixed bio and voice card from GET /pawn data (SPEC §5, §12.4). Deterministic templates first;
an optional LLM pass writes the voice card's 3 example lines once (saved in the mind).

DLC fields are read only when present: Ideology (ideo, ideo_role), Biotech (xenotype, genes), Royalty (royal_title).
"""
from __future__ import annotations

import hashlib
import json

from ...core import agegate
from ...core import gamedata as gd
from ...core.minds import FixedBio, VoiceCard
from . import register


def _s(x) -> str:
    """Label of a string-or-dict item."""
    if isinstance(x, dict):
        return str(x.get("label") or x.get("title") or x.get("def") or x.get("name") or "")
    return str(x)


def display_name(p: dict) -> str:
    """Names are typed by the player (and by other mods): cleaned of instruction-like content, capped, never empty."""
    n = p.get("name", {})
    if isinstance(n, str):
        return gd.name(n) if n else ""
    first, nick, last = (gd.clean(n.get(k, ""), "name") for k in ("first", "nick", "last"))
    short = nick or first
    out = f"{first} '{nick}' {last}".replace("  ", " ").strip() if nick and nick != first else f"{first} {last}".strip() or short
    return gd.name(out)


def short_name(p: dict) -> str:
    n = p.get("name", {})
    if isinstance(n, str):
        return gd.name(n.split()[0]) if n else ""
    raw = n.get("nick") or n.get("first") or ""
    return gd.name(raw) if raw else ""


def _bs_parts(b) -> dict[str, tuple[str, str]]:
    """{"child"|"adult": (title, desc)}; a bare string is a one-line backstory with no desc."""
    if isinstance(b, str):
        return {"child": (b, "")} if b else {}
    if not isinstance(b, dict):
        return {}
    out = {}
    for k in ("child", "adult"):
        v = b.get(k)
        if isinstance(v, dict):
            t, d = v.get("title") or v.get("label") or "", v.get("desc") or v.get("description") or ""
        else:
            t, d = (str(v) if v else ""), ""
        if t or d:
            out[k] = (str(t), str(d))
    return out


def _backstory(b) -> str:
    """One line (titles and the raw descs) for the voice-card keyword match and the facts."""
    if isinstance(b, str):
        return b
    parts = []
    for k, lab in (("child", "Childhood"), ("adult", "Adulthood")):
        t, d = _bs_parts(b).get(k, ("", ""))
        if t or d:
            parts.append(f"{lab}: {t + '. ' + d if d else t}")
    return " ".join(parts)


def trim_sentence(text: str, cap: int) -> str:
    """At most ``cap`` characters, cut after the last sentence end (. ! ?) that fits; with none, cut at a word and add '...'."""
    t = " ".join(str(text or "").split())
    if len(t) <= cap:
        return t
    cut = t[:cap]
    ends = [i for i, ch in enumerate(cut) if ch in ".!?" and t[i + 1:i + 2] == " "]
    if ends and ends[-1] >= 15:
        return cut[:ends[-1] + 1]
    room = max(cap - 3, 1)
    head = t[:room] if t[room:room + 1] == " " else (t[:room].rsplit(" ", 1)[0] or t[:room])
    return head.rstrip(" ,;:-") + "..."


def first_sentence(text: str) -> str:
    t = " ".join(str(text or "").split())
    for i, ch in enumerate(t):
        if ch in ".!?" and (i + 1 == len(t) or t[i + 1] == " ") and i >= 15:
            return t[:i + 1]
    return t


def _field(text, cap: int, first: bool = False) -> str:
    """One game string for the bio: cleaned (injection, markup), then trimmed at a sentence end to ``cap``."""
    t = gd.clean(text, "desc", max_len=max(cap * 3, 400))
    return trim_sentence(first_sentence(t) if first else t, cap)


def _trait_labels(p: dict) -> list[str]:
    return [t.get("label") or t.get("def", "") if isinstance(t, dict) else str(t) for t in p.get("traits", [])]


def _trait_defs(p: dict) -> set[str]:
    return {(t.get("def", "") if isinstance(t, dict) else str(t)).lower() for t in p.get("traits", [])} | \
           {l.lower() for l in _trait_labels(p)}


def skills_text(p: dict) -> str:
    pm = {"none": "", "minor": "*", "major": "**", "": ""}
    out = []
    for s in p.get("skills", []):
        out.append(f"{s.get('skill')} {s.get('level')}{pm.get(s.get('passion', ''), '')}")
    return ", ".join(out)


def _relation_kinds(r: dict) -> list[str]:
    return [k.strip() for k in str(r.get("kind", "")).split(",") if k.strip().lower() in _REL_KINDS]


_REL_KINDS = {"parent", "child", "sibling", "spouse", "lover", "fiance", "fiancé", "fiancee", "ex-spouse", "ex-lover", "bond"}


def _relative_name(r: dict, names: dict) -> str:
    """The relative's name, or "" when unknown (never a raw id)."""
    n = r.get("name")
    if n:
        return gd.name(_s(n)) if isinstance(n, (str, dict)) else ""
    who = r.get("pawn")
    if isinstance(who, dict):
        return gd.name(_s(who)) if _s(who) else ""
    got = names.get(who)
    return gd.name(got) if got else ""


def _ideo(p: dict) -> dict:
    i = p.get("ideo")
    return i if isinstance(i, dict) else {}


def _meme(m) -> tuple[str, str]:
    return (_s(m), str(m.get("desc") or "") if isinstance(m, dict) else "")


def facts_of(p: dict) -> dict:
    """The game facts the fixed bio depends on; a change rebuilds it."""
    h = p.get("health", {})
    return {
        "name": display_name(p), "age": p.get("age"), "gender": p.get("gender"), "body": p.get("body_type"),
        "xeno": p.get("xenotype"), "xeno_desc": p.get("xenotype_desc"), "genes": [_s(g) for g in p.get("genes", [])],
        "backstory": _bs_parts(p.get("backstory")),
        "traits": sorted((_s(t), str(t.get("desc", "")) if isinstance(t, dict) else "") for t in p.get("traits", [])),
        "missing": sorted(_s(m) for m in h.get("missing_parts", [])),
        "ideo_role": p.get("ideo_role"), "royal_title": p.get("royal_title"), "status": p.get("status"),
        "ideo": {"name": _ideo(p).get("name"), "memes": [_meme(m) for m in _ideo(p).get("memes", [])],
                 "precepts": [_s(x) for x in _ideo(p).get("precepts", [])]},
        "history": p.get("history"), "appearance": p.get("appearance"),
        "relations": sorted((",".join(_relation_kinds(r)), str(r.get("name") or r.get("pawn")))
                            for r in p.get("relations", []) if isinstance(r, dict)),
        "words": register.base_register(p),
    }


def facts_hash(p: dict) -> str:
    return hashlib.sha1(json.dumps(facts_of(p), sort_keys=True, default=str).encode()).hexdigest()[:12]


def build_fixed_bio(p: dict, names: dict[int, str] | None = None) -> FixedBio:
    """The pawn's character card: backstory, traits, ideoligion, xenotype, history. Every game string is cleaned and trimmed
    at a sentence end; the whole text is wrapped as data by prompt.block_b."""
    names = names or {}
    h = p.get("health", {})
    lines = [f"Name: {display_name(p)}, {p.get('age')}, {p.get('gender')}, body type {p.get('body_type')}."]
    dlc: dict = {}
    if agegate.is_minor(p.get("age")):
        lines.append("Age group: minor, under 18 (hard fact). Nothing romantic or sexual ever involves them.")
    for k, lab in (("child", "Childhood"), ("adult", "Adulthood")):
        t, d = _bs_parts(p.get("backstory")).get(k, ("", ""))
        title = gd.clean(t, "label")
        body = f"{title}. {_field(d, 300)}" if title and d else (title or _field(d, 300))
        if body:
            lines.append(f"{lab}: {body}")
    traits = []
    for t in p.get("traits", []):
        lab = _s(t)
        if not lab:
            continue
        lab = gd.clean(lab, "label")
        lab = lab if lab != lab.lower() else lab.title()
        d = _field(t.get("desc"), 160, first=True) if isinstance(t, dict) and t.get("desc") else ""
        traits.append(f"{lab} ({d})" if d else lab)
    if traits:
        lines.append("Traits: " + "; ".join(traits))
    if p.get("skills"):
        lines.append(f"Skills (* minor passion, ** major passion): {skills_text(p)}")
    ideo = _ideo(p)
    if ideo:
        bits = []
        if ideo.get("name"):
            bits.append(f"Ideoligion: {gd.clean(ideo['name'], 'label')}.")
        memes = []
        for m in ideo.get("memes", [])[:8]:
            lab, d = _meme(m)
            lab = gd.clean(lab, "label")
            if lab:
                dd = _field(d, 100, first=True) if d else ""
                memes.append(f"{lab} ({dd})" if dd else lab)
        if memes:
            bits.append("Memes: " + "; ".join(memes) + ".")
        precepts = [gd.clean(_s(x), "label") for x in ideo.get("precepts", [])[:8] if _s(x)]
        if precepts:
            bits.append("Precepts: " + "; ".join(precepts) + ".")
        if bits:
            lines.append(" ".join(bits))
            dlc["ideo"] = gd.clean(ideo.get("name", ""), "label")
    if p.get("xenotype"):
        x = gd.clean(p["xenotype"], "label")
        xd = _field(p.get("xenotype_desc"), 160, first=True) if p.get("xenotype_desc") else ""
        lines.append(f"Xenotype: {x}" + (f" ({xd})" if xd else ""))
        dlc["xenotype"] = x
        genes = [gd.clean(_s(g), "label") for g in p.get("genes", []) if _s(g)]
        if genes:
            lines.append("Genes: " + ", ".join(genes))
            dlc["genes"] = genes
    if p.get("royal_title"):
        rt = gd.clean(_s(p["royal_title"]), "label")
        lines.append(f"Noble title: {rt}")
        dlc["royal_title"] = rt
    hist = p.get("history") if isinstance(p.get("history"), dict) else {}
    if hist.get("arrival"):
        lines.append(f"Joined the colony: {_field(hist['arrival'], 160)}")
    tales = [_field(t, 120) for t in (hist.get("tales") or [])[:5] if t]
    if tales:
        lines.append("Notable moments: " + "; ".join(t for t in tales if t))
    if p.get("appearance"):
        lines.append(f"Appearance: {_field(p['appearance'], 120)}")
    # temporary states (drunk, high, anaesthetised) live in the scene, not the frozen bio (they would go stale)
    hl = [_s(c) for c in h.get("conditions", []) if not register.is_temporary(c)]
    if hl:
        lines.append("Health: " + "; ".join(hl))
    miss = [_s(m) for m in h.get("missing_parts", [])]
    if miss:
        lines.append("Missing: " + ", ".join(miss) + " (hard fact)")
    if p.get("status") and p["status"] != "colonist":
        lines.append(f"Status: {p['status']} (hard fact)")
    if p.get("ideo_role"):
        lines.append(f"Ideology role: {_s(p['ideo_role'])}")
        dlc["ideo_role"] = _s(p["ideo_role"])
    rel = []
    for r in p.get("relations", []):
        if not isinstance(r, dict):
            continue
        kinds = _relation_kinds(r)
        who = _relative_name(r, names)
        if kinds and who:
            rel.append(f"{who} ({', '.join(k.lower() for k in kinds)})")
    if rel:
        lines.append("Key relatives: " + ", ".join(rel))
    dlc["words"] = register.base_words(p)
    return FixedBio(text="\n".join(lines), facts_hash=facts_hash(p), extras=dlc)


# trait def/label (lowercase, substring) -> (register fragment, tic, example line)
_TRAITS: list[tuple[str, str, str, str]] = [
    ("abrasive", "blunt and needling", "Starts with 'Look.'", "Look. That is not how you do it."),
    ("kind", "warm, asks after people", "Calls people 'friend'", "You alright, friend? Sit a minute."),
    ("psychopath", "flat and cold, little feeling", "Never apologises", "He was in the way. Now he is not."),
    ("bloodlust", "relishes violence", "Talks about the kill", "Good shot. Messy, but good."),
    ("greedy", "counts everything", "Mentions what things cost", "That rug is worth a hundred silver. Do not stand on it."),
    ("ascetic", "plain, wants little", "Says 'Enough.'", "Enough. A bed is a bed."),
    ("jealous", "resentful of others' comfort", "Compares rooms", "Funny how some of us get the big room."),
    ("nervous", "hesitant, trails off", "Trailing 'um...'", "Um... is that, are they coming back?"),
    ("tough", "stoic, understates pain", "Says 'Fine.'", "Fine. It is a scratch."),
    ("sanguine", "easy and upbeat", "Laughs it off", "Ha. Could be worse. Probably will be."),
    ("pessimist", "expects the worst", "Says 'Wait for it.'", "Wait for it. Something always goes wrong."),
    ("optimist", "sees a bright side", "Says 'We'll see.'", "We have food and walls. We will see."),
    ("gourmand", "thinks about food", "Mentions meals", "Is that lunch? Tell me that is lunch."),
    ("lazy", "slow, avoids work", "Sighs first", "Sigh. Later. Maybe."),
    ("industrious", "always busy", "Talks about the next job", "Next crate. Move."),
    ("neurotic", "tense, hard on themself", "Repeats things twice", "Check it twice. Check it twice."),
    ("cannibal", "casual about eating people", "Calls the dead 'meat'", "Waste not. That one was fresh."),
    ("bookworm", "dry, bookish", "Quotes numbers", "The data says no. Mostly."),
    ("wimp", "avoids pain, complains", "Winces aloud", "Ow. Ow. It hurts, it really does."),
    ("sickly", "tired, often unwell", "Coughs mid-sentence", "Cough. Sorry. Go on."),
    ("depressive", "heavy and flat", "Says 'Whatever.'", "Whatever. It will not matter."),
    ("volatile", "short-tempered", "Snaps mid-sentence", "Do not. Just do not."),
    ("night owl", "slow in the morning", "Mentions sleep", "Too early. Ask me at dusk."),
    ("slowpoke", "slow-moving, patient", "Says 'Hold on.'", "Hold on. I am getting there."),
    ("creative", "imaginative, restless", "Compares things to art", "That wall wants a mural."),
]
_GENERIC_EX = ["Right. What now?", "Not my favourite thing. Still, it has to be done.", "Hm. Noted."]


def build_voice_card(p: dict) -> VoiceCard:
    """Deterministic: register from traits, 1-3 tics, 3 example lines. Age and backstory fill gaps."""
    defs = _trait_defs(p)
    reg, tics, exs = [], [], []
    for key, r, t, e in _TRAITS:
        if any(key in d for d in defs):
            reg.append(r); tics.append(t); exs.append(e)
    age = p.get("age") or 30
    if age < 18:
        reg.append("young, quick, blunt about adults")
        exs.append("Do I have to? Fine.")
    elif age >= 55:
        reg.append("slow and weathered")
        exs.append("Seen worse. Seen a lot worse.")
    bs = _backstory(p.get("backstory")).lower()
    for kw, r in (("cook", "talks in kitchen terms"), ("soldier", "clipped, military"),
                  ("doctor", "clinical"), ("noble", "formal, a little superior"),
                  ("slave", "guarded, short answers"), ("tribal", "plain and literal")):
        if kw in bs:
            reg.append(r)
            break
    exs += _GENERIC_EX
    register = ("Speaks " + ", ".join(reg[:3]) + ".") if reg else "Plain and brief."
    # dedupe preserving order
    tics = list(dict.fromkeys(tics))[:3] or ["Short sentences"]
    exs = list(dict.fromkeys(exs))[:3]
    return VoiceCard(register_line=register, tics=tics, examples=exs)


def talk_weight(p: dict) -> float:
    """Chattiness from traits, used by the director to scale ambient leases."""
    defs = _trait_defs(p)
    w = 1.0
    for k, d in (("kind", .3), ("sanguine", .2), ("optimist", .1), ("abrasive", .2), ("nervous", .1),
                 ("ascetic", -.3), ("depressive", -.3), ("psychopath", -.2), ("slowpoke", -.1), ("lazy", -.1)):
        if any(k in x for x in defs):
            w += d
    return max(0.3, min(1.8, w))


VOICE_PROMPT = (
    "Write exactly 3 short lines (6 to 20 words each) of speech that this RimWorld colonist might "
    "say, one per line, no numbering, no quotes. Plain words, dry, no flowery language.\n"
    "Colonist:\n{bio}\nRegister: {register}\nHabits: {tics}\nLines:")

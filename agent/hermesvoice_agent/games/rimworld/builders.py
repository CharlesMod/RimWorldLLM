"""Fixed bio and voice card from GET /pawn data (SPEC §5, §12.4). Deterministic templates first;
an optional LLM pass writes the voice card's 3 example lines once (saved in the mind).

DLC fields are read only when present: Ideology (ideo_role, precepts), Biotech (xenotype, genes),
Royalty (title), Anomaly (anomaly: {...}).
"""
from __future__ import annotations

import hashlib
import json

from ...core import gamedata as gd
from ...core.minds import FixedBio, VoiceCard


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


def _backstory(b) -> str:
    if isinstance(b, str):
        return b
    if not isinstance(b, dict):
        return ""
    parts = []
    for k, lab in (("child", "Childhood"), ("adult", "Adulthood")):
        v = b.get(k)
        if v:
            if isinstance(v, dict):
                t = v.get("title") or v.get("label") or ""
                d = v.get("desc") or v.get("description") or ""
                v = f"{t}. {d}".strip(". ") if d else t
            parts.append(f"{lab}: {v}")
    return " ".join(parts)


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


def facts_of(p: dict) -> dict:
    """The game facts the fixed bio depends on; a change rebuilds it."""
    h = p.get("health", {})
    return {
        "name": display_name(p), "age": p.get("age"), "gender": p.get("gender"), "body": p.get("body_type"),
        "xeno": p.get("xenotype"), "genes": [_s(g) for g in p.get("genes", [])],
        "backstory": _backstory(p.get("backstory")), "traits": sorted(_trait_labels(p)),
        "missing": sorted(_s(m) for m in h.get("missing_parts", [])),
        "ideo_role": p.get("ideo_role"), "title": p.get("title"), "status": p.get("status"),
        "precepts": p.get("precepts"), "anomaly": p.get("anomaly"),
    }


def facts_hash(p: dict) -> str:
    f = facts_of(p)
    f["age"] = None   # ageing alone does not rebuild
    return hashlib.sha1(json.dumps(f, sort_keys=True, default=str).encode()).hexdigest()[:12]


def build_fixed_bio(p: dict, names: dict[int, str] | None = None) -> FixedBio:
    names = names or {}
    h = p.get("health", {})
    lines = [f"Name: {display_name(p)}, {p.get('age')}, {p.get('gender')}, body type {p.get('body_type')}."]
    dlc: dict = {}
    if p.get("xenotype"):
        genes = ", ".join(_s(g) for g in p.get("genes", []))
        lines[0] += f" Xenotype: {p['xenotype']}" + (f"; genes: {genes}." if genes else ".")
        dlc["xenotype"] = p["xenotype"]
        dlc["genes"] = [_s(g) for g in p.get("genes", [])]
    bs = _backstory(p.get("backstory"))
    if bs:
        lines.append(f"Backstory: {bs}")
    tl = _trait_labels(p)
    if tl:
        lines.append("Traits: " + ", ".join(tl))
    if p.get("skills"):
        lines.append(f"Skills (* minor passion, ** major passion): {skills_text(p)}")
    hl = [_s(c) for c in h.get("conditions", [])]
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
    if p.get("precepts"):
        pr = [_s(x) for x in p["precepts"]]
        lines.append("Precepts of their ideoligion: " + "; ".join(pr))
        dlc["precepts"] = pr
    if p.get("title"):
        lines.append(f"Noble title: {_s(p['title'])}")
        dlc["title"] = _s(p["title"])
    if p.get("anomaly"):
        lines.append(f"Anomaly: {json.dumps(p['anomaly'], sort_keys=True)}")
        dlc["anomaly"] = p["anomaly"]
    rel = []
    for r in p.get("relations", []):
        if r.get("kind") in ("parent", "child", "sibling", "spouse", "lover", "fiance", "ex-spouse", "ex-lover",
                             "Parent", "Child", "Sibling", "Spouse", "Lover", "Fiance", "Bond"):
            who = r.get("pawn")
            who = names.get(who, who) if not isinstance(who, dict) else _s(who)
            rel.append(f"{who} ({r['kind']})")
    if rel:
        lines.append("Key relatives: " + ", ".join(str(r) for r in rel))
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

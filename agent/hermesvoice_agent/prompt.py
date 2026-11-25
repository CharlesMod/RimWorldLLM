"""Prompt builder: blocks A-G in SPEC §4 order, from bench/m0/lib.py (measured).

A: world rules, speech rules, output format, label glosses (same bytes for every pawn)
B: fixed bio and voice card (never changes for this pawn)
C: evolving bio, wants, relations digest (changes only at reflection)
D: 2-4 chosen memories and the ledger entries this pawn knows (once per lease)
E: scene (once per lease)
F: turns so far (append-only)            G: voice reminder (one line) and the request (every turn)

A-E form the *prefix*: built once at lease start and then frozen as a string, so it is
byte-identical for the whole lease. Nothing in A-E may carry ticks, timestamps or per-turn data.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from importlib import resources

from .builders import short_name
from .minds import Memory, Mind

INTENTS = ["none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain"]
GRAMMAR_INTENT = 'root ::= ' + " | ".join(f'"{i}"' for i in INTENTS)

BLOCK_A: str = resources.files("hermesvoice_agent").joinpath("block_a.txt").read_text(encoding="utf-8")


@dataclass
class Scene:
    where: str = "somewhere in the colony"
    when: str = "daytime"                # time of day and weather, never a tick count
    present: list[str] = field(default_factory=list)   # "Name (job)"
    state: str = ""
    job: str = "idle"
    thoughts: list[str] = field(default_factory=list)  # "text (+/-n)"
    trigger: str = ""


def scene_from_bridge(pawn: dict, scene: dict, trigger: str, names: dict[int, str],
                      known: set[int] | None = None) -> Scene:
    """Build block E data from GET /pawn + GET /scene. ``known`` limits who is named."""
    room = scene.get("room") or {}
    where = room.get("role", "") or "outdoors"
    extras = [f"{k} {room[k]}" for k in ("clean", "temp") if k in room]
    if extras:
        where += " (" + ", ".join(str(x) for x in extras) + ")"
    when = ", ".join(str(x) for x in (scene.get("time_of_day"), scene.get("weather")) if x)
    present = []
    for q in scene.get("present", []):
        pid = q.get("pawn")
        if pid is None or (known is not None and pid not in known):
            continue
        n = names.get(pid)
        if n:
            present.append(f"{n} ({q.get('job') or 'idle'})")
    mood = (pawn.get("mood") or {})
    needs = pawn.get("needs") or {}
    h = pawn.get("health") or {}
    st = []
    if needs.get("mood") is not None:
        st.append(f"mood {round(float(needs['mood']) * 100) if float(needs['mood']) <= 1 else needs['mood']}%")
    if h.get("downed"):
        st.append("downed")
    if h.get("pain", 0) and h["pain"] > 0.3:
        st.append("in pain")
    trends = scene.get("trends") or {}
    for k, v in trends.items():
        if isinstance(v, list) and len(v) >= 2:
            st.append(f"{k.replace('_', ' ')} {v[0]} -> {v[-1]}")
    ths = [f"{t.get('label')} ({round(t.get('offset', 0)):+d})" for t in mood.get("thoughts", [])[:4]]
    return Scene(where=where, when=when or "daytime", present=present, state=", ".join(st) or "ordinary",
                 job=pawn.get("job") or "idle", thoughts=ths, trigger=trigger)


def block_b(mind: Mind) -> str:
    v = mind.voice
    s = "=== WHO YOU ARE ===\n" + mind.fixed.text + "\n"
    s += ("\n=== HOW YOU TALK ===\n"
          f"Register: {v.register_line}\n"
          f"Habits: {'; '.join(v.tics)} (use a habit in at most one line in three)\n"
          "Example lines:\n" + "\n".join(f"- {e}" for e in v.examples) + "\n")
    return s


def block_c(mind: Mind, names: dict[int, str] | None = None, max_rel: int = 6) -> str:
    rels = sorted(mind.relations.items(), key=lambda kv: (-abs(kv[1].opinion or 0), kv[0]))[:max_rel]
    lines = []
    for pid, r in rels:
        op = f", opinion {r.opinion:+d}" if r.opinion is not None else ""
        lines.append(f"- {r.name}{op}: {r.feeling}" + (f" {r.history}" if r.history else ""))
    s = "=== WHO YOU ARE BECOMING ===\n" + (mind.evolving_bio or "(nothing yet)") + "\n"
    s += "Wants: " + ("; ".join(mind.wants) if mind.wants else "none in particular") + "\n"
    if mind.notes:
        s += "Director's notes: " + mind.notes + "\n"
    s += "Relations:\n" + ("\n".join(lines) if lines else "- none yet") + "\n"
    return s


def block_d(memories: list[Memory], ledger_lines: list[str]) -> str:
    def mline(m: Memory) -> str:
        tag = {"saw": "", "heard": " (heard)", "told": f" (told by {m.told_by}, not sure)"}[m.source]
        if m.visibility == "private":
            tag += " [private]"
        return f"- {m.text}{tag}"
    ms = "\n".join(mline(m) for m in memories) or "- nothing in particular"
    led = "\n".join(f"- {l}" for l in ledger_lines) or "- nothing ongoing that you know of"
    return f"=== WHAT YOU REMEMBER ===\n{ms}\n=== WHAT IS GOING ON THAT YOU KNOW OF ===\n{led}\n"


def block_e(sc: Scene) -> str:
    th = "\n".join(f"- {t}" for t in sc.thoughts) or "- nothing much"
    return (f"=== THE SCENE ===\nWhere: {sc.where}\nWhen: {sc.when}\n"
            f"Present: {', '.join(sc.present) or 'nobody else'}\n"
            f"Your state: {sc.state}\nCurrent job: {sc.job}\nYour current thoughts:\n{th}\n"
            f"What just happened: {sc.trigger}\n")


def build_prefix(mind: Mind, memories: list[Memory], ledger_lines: list[str], sc: Scene) -> str:
    """A-E as one string. Freeze it at lease start; never rebuild mid-lease."""
    return "\n".join([BLOCK_A, block_b(mind), block_c(mind), block_d(memories, ledger_lines), block_e(sc)])


def block_f(turns: list[tuple[str, str]]) -> str:
    if not turns:
        return "=== TURNS SO FAR ===\n(none yet)\n"
    return "=== TURNS SO FAR ===\n" + "\n".join(f"{n}: {t}" for n, t in turns) + "\n"


def block_g(mind: Mind, name: str, turn_no: int = 0, request: str | None = None) -> str:
    """One-line voice reminder plus the request. Re-sent every turn, so it stays short.
    Enforces the tic rule: habits allowed only on every third line."""
    v = mind.voice
    ex = f' Sound like: "{v.examples[0]}"' if v.examples else ""
    habit = f"Habits (this line): {'; '.join(v.tics)}." if turn_no % 3 == 0 else "No habits this line."
    s = f"=== VOICE REMINDER ===\nYou are {name}. {v.register_line} {habit}{ex}\n"
    return s + (request or f"=== REQUEST ===\nWrite {name}'s next line of speech now. One line only.")


def line_messages(prefix: str, turns: list[tuple[str, str]], mind: Mind, name: str,
                  turn_no: int = 0, request: str | None = None) -> list[tuple[str, str]]:
    return [("system", prefix), ("user", block_f(turns) + "\n" + block_g(mind, name, turn_no, request))]


def intent_request(listener_name: str | None) -> str:
    to = f" to {listener_name}" if listener_name else ""
    return f"=== INTENT ===\nLabel the line you just spoke{to} with one social label from the list. Label only."


def intent_messages(line_msgs: list[tuple[str, str]], line: str, listener_name: str | None):
    """Same system+user prefix as the line pass (cache hit), then the line, then the label request."""
    return line_msgs + [("assistant", line), ("user", intent_request(listener_name))]


def short(p: dict) -> str:
    return short_name(p)

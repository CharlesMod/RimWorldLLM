"""Prompt-block framework: blocks A-G in a fixed order (most stable first), after bench/m0/lib.py.

A: world rules, speech rules, output format, label glosses (adapter text; same bytes for every actor)
B: fixed bio and voice card (never changes for this actor)
C: evolving bio, wants, relations digest (changes only at reflection)
D: 2-4 chosen memories and the ledger entries this actor knows (once per lease)
E: scene (once per lease)
F: turns so far (append-only)            G: voice reminder (one line) and the request (every turn)

A-E form the *prefix*: built once at lease start and then frozen as a string, so it is
byte-identical for the whole lease. Nothing in A-E may carry units, timestamps or per-turn data.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from .minds import Memory, Mind



def grammar_for(labels: list[str]) -> str:
    """GBNF restricting the intent pass to the adapter's label set."""
    return "root ::= " + " | ".join(f'"{i}"' for i in labels)


@dataclass
class Scene:
    where: str = "somewhere nearby"
    when: str = "daytime"                # time of day and weather, never a game count
    present: list[str] = field(default_factory=list)   # "Name (job)"
    state: str = ""
    job: str = "idle"
    thoughts: list[str] = field(default_factory=list)  # "text (+/-n)"
    trigger: str = ""


def block_b(mind: Mind) -> str:
    v = mind.voice
    s = "=== WHO YOU ARE ===\n" + mind.fixed.text + "\n"
    s += ("\n=== HOW YOU TALK ===\n"
          f"Register: {v.register_line}\n"
          f"Habits: {'; '.join(v.tics)} (use a habit in at most one line in three)\n"
          "Example lines:\n" + "\n".join(f"- {e}" for e in v.examples) + "\n")
    return s


def block_c(mind: Mind, names: dict[int, str] | None = None, max_rel: int = 6) -> str:
    """Changes only when a reflection runs: bio, wants and the relations digest are all written there."""
    lines = []
    for r in mind.c_relations[:max_rel]:
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
        elif m.visibility == "told" and m.told_to:
            tag += f" [private, told to {', '.join(m.told_to)}]"
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


def build_prefix(block_a: str, mind: Mind, memories: list[Memory], ledger_lines: list[str], sc: Scene) -> str:
    """A-E as one string. Freeze it at lease start; never rebuild mid-lease."""
    return "\n".join([block_a, block_b(mind), block_c(mind), block_d(memories, ledger_lines), block_e(sc)])


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

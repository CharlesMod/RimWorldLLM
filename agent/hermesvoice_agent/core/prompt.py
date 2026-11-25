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

from . import agegate
from . import gamedata as gd
from .minds import Memory, Mind



DELIVERIES = ["normal", "shout", "whisper"]     # how a line is said; decided in the intent pass (POST /say kind)


AIM_RULE = "aim ::= [a-zA-Z0-9 ,.'!?-]{8,100}"    # the inner aim: a plain first-person phrase (core/inner.py)
AIM_ONLY_GRAMMAR = "root ::= aim\n" + AIM_RULE


def grammar_for(labels: list[str], deliveries: list[str] | None = None, aim: bool = False) -> str:
    """GBNF restricting the intent pass to the adapter's label set, then (optionally) the delivery, then (optionally,
    with a delivery) a free first-person aim."""
    g = "root ::= " + " | ".join(f'"{i}"' for i in labels)
    if deliveries:
        g = "root ::= intent \" \" delivery" + (" \" \" aim" if aim else "") + \
            "\nintent ::= " + " | ".join(f'"{i}"' for i in labels) + \
            "\ndelivery ::= " + " | ".join(f'"{i}"' for i in deliveries) + ("\n" + AIM_RULE if aim else "")
    return g


@dataclass
class Scene:
    where: str = "somewhere nearby"
    when: str = "daytime"                # time of day and weather, never a game count
    present: list[str] = field(default_factory=list)   # "Name (job)"
    state: str = ""
    job: str = "idle"
    thoughts: list[str] = field(default_factory=list)  # "text (+/-n)"
    trigger: str = ""
    minor_self: bool = False             # the speaker is under 18 (or of unknown age)


def block_b(mind: Mind) -> str:
    v = mind.voice
    s = "=== WHO YOU ARE ===\n" + gd.data_section("bio", gd.clean(mind.fixed.text, "line", max_len=600, multiline=True))
    s += ("\n=== HOW YOU TALK ===\n"
          f"Register: {gd.clean(v.register_line, 'desc')}\n"
          f"Habits: {'; '.join(gd.clean(t, 'label') for t in v.tics)} (use a habit in at most one line in three)\n"
          "Example lines:\n" + "\n".join(f"- {gd.clean(e, 'line')}" for e in v.examples) + "\n")
    return s


def block_c(mind: Mind, names: dict[int, str] | None = None, max_rel: int = 6) -> str:
    """Changes only when a reflection runs: bio, wants and the relations digest are all written there."""
    lines = []
    for r in mind.c_relations[:max_rel]:
        op = f", opinion {r.opinion:+d}" if r.opinion is not None else ""
        lines.append(f"- {gd.name(r.name)}{op}: {gd.clean(r.feeling, 'line')}" +
                     (f" {gd.clean(r.history, 'line')}" if r.history else ""))
    body = "Becoming: " + gd.clean(mind.evolving_bio or "(nothing yet)", "desc") + "\n"
    body += "Wants: " + ("; ".join(gd.clean(w, "label") for w in mind.wants) if mind.wants else "none in particular") + "\n"
    if mind.notes:
        body += "Director's notes (written by the player): " + gd.clean(mind.notes, "note") + "\n"
    body += "Relations:\n" + ("\n".join(lines) if lines else "- none yet") + "\n"
    return "=== WHO YOU ARE BECOMING ===\n" + gd.data_section("character", body)


def block_d(memories: list[Memory], ledger_lines: list[str], thoughts: list[str] | None = None) -> str:
    def mline(m: Memory) -> str:
        tag = {"saw": "", "heard": " (heard)", "told": f" (told by {gd.name(m.told_by)}, not sure)"}[m.source]
        if m.visibility == "private":
            tag += " [private]"
        elif m.visibility == "told" and m.told_to:
            tag += f" [private, told to {', '.join(gd.name(x) for x in m.told_to)}]"
        return f"- {gd.clean(m.text, 'event')}{tag}"
    ms = "\n".join(mline(m) for m in memories) or "- nothing in particular"
    led = "\n".join(f"- {gd.clean(l, 'event')}" for l in ledger_lines) or "- nothing ongoing that you know of"
    out = (f"=== WHAT YOU REMEMBER ===\n{gd.data_section('memories', ms)}"
           f"=== WHAT IS GOING ON THAT YOU KNOW OF ===\n{gd.data_section('ongoing', led)}")
    if thoughts:       # the actor's own recent inner thoughts (fixed per lease, so cache-safe)
        out += "=== YOUR RECENT THOUGHTS ===\n" + gd.data_section(
            "thoughts", "\n".join(f"- {gd.clean(t, 'line')}" for t in thoughts))
    return out


def block_e(sc: Scene) -> str:
    c = gd.clean
    th = "\n".join(f"- {c(t, 'label')}" for t in sc.thoughts) or "- nothing much"
    body = (f"Where: {c(sc.where, 'label')}\nWhen: {c(sc.when, 'label')}\n"
            f"Present: {', '.join(c(p, 'label') for p in sc.present) or 'nobody else'}\n"
            + ("You are a minor (under 18).\n" if sc.minor_self else "") +
            f"Your state: {c(sc.state, 'line')}\nCurrent job: {c(sc.job, 'label')}\nYour current thoughts:\n{th}\n"
            f"What just happened: {c(sc.trigger, 'event')}\n")
    return "=== THE SCENE ===\n" + gd.data_section("scene", body)


def build_prefix(block_a: str, mind: Mind, memories: list[Memory], ledger_lines: list[str], sc: Scene,
                 thoughts: list[str] | None = None) -> str:
    """A-E as one string. Freeze it at lease start; never rebuild mid-lease."""
    if gd.DATA_RULE not in block_a:
        block_a = block_a.rstrip("\n") + "\n\n" + gd.DATA_RULE
    if agegate.MINOR_RULE not in block_a:
        block_a = block_a.rstrip("\n") + "\n\n" + agegate.MINOR_RULE
    return "\n".join([block_a, block_b(mind), block_c(mind), block_d(memories, ledger_lines, thoughts), block_e(sc)])


def block_f(turns: list[tuple[str, str]]) -> str:
    if not turns:
        return "=== TURNS SO FAR ===\n(none yet)\n"
    return "=== TURNS SO FAR ===\n" + "\n".join(f"{gd.name(n)}: {t}" for n, t in turns) + "\n"


def block_g(mind: Mind, name: str, turn_no: int = 0, request: str | None = None, aim: str | None = None) -> str:
    """One-line voice reminder plus the request. Re-sent every turn, so it stays short.
    Enforces the tic rule: habits allowed only on every third line."""
    v = mind.voice
    ex = f' Sound like: "{v.examples[0]}"' if v.examples else ""
    habit = f"Habits (this line): {'; '.join(v.tics)}." if turn_no % 3 == 0 else "No habits this line."
    s = f"=== VOICE REMINDER ===\nYou are {name}. {v.register_line} {habit}{ex}\n"
    if aim:       # a fresh private aim from the inner stream (per turn, so it lives here and not in A-E)
        s += f"Your private aim (never say it outright): {gd.clean(aim, 'line')}\n"
    return s + (request or f"=== REQUEST ===\nWrite {name}'s next line of speech now. One line only.")


def line_messages(prefix: str, turns: list[tuple[str, str]], mind: Mind, name: str,
                  turn_no: int = 0, request: str | None = None, aim: str | None = None) -> list[tuple[str, str]]:
    return [("system", prefix), ("user", block_f(turns) + "\n" + block_g(mind, name, turn_no, request, aim))]


AIM_ASK = "what you privately want or really mean, in about 12 words, first person, never said aloud"


def aim_request() -> str:
    """The aim-only call (a line with no listener)."""
    return f"=== INTENT ===\nAbout the line you just spoke: {AIM_ASK}. Those words only."


def intent_request(listener_name: str | None, deliveries: list[str] | None = None, aim: bool = False) -> str:
    to = f" to {listener_name}" if listener_name else ""
    if deliveries and aim:
        return (f"=== INTENT ===\nLabel the line you just spoke{to} with one social label from the list, then how you "
                f"said it ({', '.join(deliveries)}), then {AIM_ASK}. Say shout only if you really raised your voice; "
                "whisper only if you kept it low. Most lines are normal. Label, delivery and aim only.")
    if deliveries:
        return (f"=== INTENT ===\nLabel the line you just spoke{to} with one social label from the list, then how you "
                f"said it ({', '.join(deliveries)}). Say shout only if you really raised your voice; whisper only if "
                "you kept it low. Most lines are normal. Label and delivery only.")
    return f"=== INTENT ===\nLabel the line you just spoke{to} with one social label from the list. Label only."


def intent_messages(line_msgs: list[tuple[str, str]], line: str, listener_name: str | None,
                    deliveries: list[str] | None = None, aim: bool = False):
    """Same system+user prefix as the line pass (cache hit), then the line, then the label request."""
    return line_msgs + [("assistant", line), ("user", intent_request(listener_name, deliveries, aim))]


def aim_messages(line_msgs: list[tuple[str, str]], line: str):
    return line_msgs + [("assistant", line), ("user", aim_request())]

"""Wants with follow-through: in an actor's idle window the model may pick at most one small social action.

Tools: seek_out(target, topic), avoid(target), remember(fact, importance), do_nothing. The tool loop (core/toolloop)
constrains the choice; nothing the model writes is executed unvalidated. Per scene the allowed set is built here:
no one to approach means no seek_out or avoid, and romance-type tools are removed whenever a minor is involved
(core/agegate.py). Execution goes through the bridge only: ``social_action`` (a PROPOSED op, see BRIDGE-API.md) for
seek_out and avoid, and the mind blob for remember. A failure of any kind ends in do_nothing.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field

from . import agegate
from . import gamedata as gd
from .bridge import BridgeError
from .minds import Memory
from .toolloop import IMPORTANCE, Action, Param, ToolHarness, ToolSpec

log = logging.getLogger("hermesvoice.wants")

TOOL_NAMES = ("seek_out", "avoid", "remember", "do_nothing")
SOCIAL = {"seek_out", "avoid"}

SYSTEM = (
    "You are the quiet part of one character, deciding what they do with a free moment. Choose at most one small "
    "social action, or nothing. seek_out walks to someone to talk about a topic. avoid keeps clear of someone for a "
    "while. remember keeps one fact worth holding on to. do_nothing is always fine and often right. Only use what the "
    "scene below supports.\n\n" + gd.DATA_RULE)


def tools(names: list[str]) -> list[ToolSpec]:
    who = Param("target", "enum", "who", choices=names or None, required=True) if names else None
    seek = [who, Param("topic", "text", "what to talk about, a few words", max_len=80)] if who else []
    return [
        ToolSpec("seek_out", "Walk over to someone to talk about something.", seek),
        ToolSpec("avoid", "Keep clear of someone for a while.", [who] if who else []),
        ToolSpec("remember", "Keep one fact in mind.",
                 [Param("fact", "text", "the fact", max_len=160), Param("importance", "scale", "how much it matters", scale=IMPORTANCE)]),
        ToolSpec("do_nothing", "Do nothing special.", []),
    ]


@dataclass
class WantsStats:
    runs: int = 0
    acted: int = 0
    remembered: int = 0
    nothing: int = 0
    fallbacks: int = 0
    refused: int = 0                 # actions refused by validation (age gate, unknown target, second action)
    unsupported: int = 0             # the mod does not implement the proposed op
    trace: list[dict] = field(default_factory=list)


async def run_wants(d, aid: int, backend, mode: str = "auto") -> dict:
    """One decision for actor ``aid``. Returns a summary dict (also appended to d.wants.trace)."""
    st: WantsStats = d.wants
    st.runs += 1
    mind = d.minds[aid]
    scene = await d.bridge.scene(aid)
    others = {i: d.names[i] for i, _ in d.adapter.scene_present(scene) if i in d.names and i != aid}
    by_name = {n: i for i, n in others.items()}
    self_minor = d.is_minor(aid)
    target_minor = lambda n: d.is_minor(by_name.get(n))
    minor_in_scene = self_minor or any(d.is_minor(i) for i in others)
    wanted = list(TOOL_NAMES) if others else ["remember", "do_nothing"]
    allowed = d.tools_allowed(wanted, minor_in_scene)
    mems = sorted(mind.memories, key=lambda m: -m.game)[:5]
    user = ("=== YOUR WANTS ===\n" + gd.data_section("wants", "\n".join(f"- {gd.clean(w, 'label')}" for w in mind.wants) or "- none") +
            "=== WHO IS HERE ===\n" + gd.data_section("present", "\n".join(f"- {gd.name(n)}" for n in others.values()) or "- nobody") +
            "=== RECENT MEMORIES ===\n" + gd.data_section("memories", "\n".join(f"- {gd.clean(m.text, 'event')}" for m in mems) or "- nothing") +
            "Pick at most one action.")
    done = {"social": 0, "remember": 0}
    unsupported = False

    def validator(tool: str, args: dict) -> str | None:
        text = " ".join(str(v) for v in args.values() if isinstance(v, str))
        tgt = args.get("target")
        if tool in SOCIAL:
            if done["social"]:
                return "only one social action per window"
            if tgt not in by_name:
                return "target is not here"
        if tool == "remember" and done["remember"]:
            return "only one fact per window"
        if agegate.violation(text, involves_minor=self_minor or (tgt is not None and target_minor(tgt)),
                             minor_names=d.minor_tokens()):
            st.refused += 1
            return "not allowed: nothing romantic or sexual involving a minor"
        return None

    async def executor(tool: str, values: dict):
        nonlocal unsupported
        if tool == "do_nothing":
            return None
        if tool == "remember":
            imp = (values["importance"] - IMPORTANCE.lo) / (IMPORTANCE.hi - IMPORTANCE.lo)
            mind.memories.append(Memory(id=mind.next_mem_id(), game=d.bridge.env.game, text=gd.clean(values["fact"], "event"),
                                        visibility="private", importance=round(imp, 2), etype="want"))
            mind.unreflected_importance += imp
            d.dirty.add(aid)
            done["remember"] += 1
            st.remembered += 1
            return {"ok": True}
        tid = by_name[values["target"]]
        try:
            await d.bridge.social_action(aid, tool, tid, values.get("topic", ""))
        except BridgeError as e:
            if e.status == 404:
                unsupported = True
                st.unsupported += 1
            raise
        done["social"] += 1
        st.acted += 1
        return {"ok": True}

    harness = ToolHarness(backend, tools(sorted(others.values())), max_steps=3, mode=mode)
    res = await harness.run(system=SYSTEM, user=user, executor=executor, validator=validator,
                            fallback=Action("call", "do_nothing"), allowed=sorted(allowed))
    if res.fallbacks:
        st.fallbacks += 1
    if not res.executed or all(e.tool == "do_nothing" for e in res.executed):
        st.nothing += 1
    out = {"actor": aid, "outcome": res.outcome, "allowed": res.allowed, "executed": [(e.tool, e.args, e.error) for e in res.executed],
           "unsupported": unsupported, "minor_in_scene": minor_in_scene}
    st.trace.append(out)
    del st.trace[:-200]
    return out

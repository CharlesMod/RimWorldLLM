"""The steward: the narrator plays the game (docs/VIVARIUM.md).

Owned by the Director; ``await steward.step()`` runs every director step and costs one ``GET /vivarium`` when idle.
When something wakes it, one *cycle* runs as a task: read the digest and the goals board, run the tool loop on the
narrator slot (slot 0) with the game's tools plus the board tools, then post the final line, batch the board edits into
one ``PUT /goals``, and add one log line. While a cycle runs the narrator's idle jobs yield (``LeaseManager.narrator_owner``).

Wakes (one cycle at a time; a chat that arrives mid-cycle is answered by the next cycle at once):
  chat       a new ``player`` line addressed to the narrator (to == 0): at once, ignores the real-time floor, works in advisor
             mode (the narrator is not playing) and while the game is paused
  event      while playing and unpaused: what the adapter calls urgent (an attack, a breakdown, a death, a critical alert, a letter needing a choice)
  heartbeat  while playing and unpaused: ``heartbeat_hours`` of game time since the last cycle (counted in game units, so a
             fast game wakes it more often in real time)
  paused     while playing: the narrator's own emergency pause is on (once per pause), so it can resume
  combat     while playing and unpaused, when GET /vivarium says ``combat.active``: one full "combat started" cycle (and, in code,
             the speed rule: slow to 1x when the setting ``slow_for_combat`` is on), then a small combat turn every
             ``steward_combat_gap_game_s`` game seconds and ``steward_combat_gap_real_s`` real seconds after the last turn ended
             (GET /combat only, a small prompt, the combat tools, 3 steps); other wakes wait. When ``combat.active`` turns false:
             one full "combat over" cycle, and the speed is put back if we slowed it. Not in advisor mode.
Non-chat wakes keep ``steward_min_gap_real_s`` real seconds between cycles (combat has its own gaps). Against a mod without ``/vivarium`` (404) the
steward switches itself off quietly.

Everything game-specific comes from ``adapter.steward_kit``: which events wake, the prompt files, the digest text, the game tools.
"""
from __future__ import annotations

import asyncio
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

from . import agegate
from . import gamedata as gd
from .bridge import BridgeError
from .steward_board import BOARD_TOOLS, Edits, board_tools, plain, render_board
from .toolloop import ToolHarness

log = logging.getLogger("rimworldllm.steward")

MAX_EVENT_LINES = 15
MAX_CHAT_LINES = 10
CATCH_UP_LINES = 3                         # at most this many unanswered player lines are answered on first sight
MAX_NOTES = 3
STEPS, BUDGET_S, STEP_TIMEOUT_S = 8, 120.0, 60.0
FINAL_MAX = 300
# a combat turn is small: the combat view and the plan line, the combat tools only, three steps (the last is the closing line)
COMBAT_STEPS, COMBAT_BUDGET_S, COMBAT_STEP_TIMEOUT_S, COMBAT_MAX_TOKENS, COMBAT_FINAL_MAX = 3, 40.0, 30.0, 160, 120
TICKS_PER_GAME_S = 60
COMBAT_START_STEPS = 5                     # the "combat started" cycle: draft, place, flee, one defence, the line; the combat turns follow
ENV_STRATEGY = "RIMWORLDLLM_STEWARD_STRATEGY"
STRATEGY_HEADING = "## Strategy notes"
_NO_LINE = {"", "done", "nothing", "nothing to say", "none", "n/a", "ok"}


def load_strategy(cfg, default_text: str) -> tuple[str, str]:
    """The strategy section text and a note for the log ("" when fine). Order: env path, config path, the shipped text.
    A chosen file that is missing, unreadable or empty leaves the section out; a long one is cut at steward_strategy_max_bytes (12 KB) on a line."""
    path = os.environ.get(ENV_STRATEGY, "").strip() or str(cfg.steward_strategy_path or "").strip()
    note = ""
    if path:
        try:
            text = Path(path).expanduser().read_text(encoding="utf-8", errors="replace")
        except OSError as e:
            return "", f"strategy file {path} not read ({type(e).__name__}): section left out"
    else:
        text = default_text
    text = text.strip()
    if not text:
        return "", f"strategy file {path or '(shipped)'} is empty: section left out"
    cap = int(cfg.steward_strategy_max_bytes)
    raw = text.encode("utf-8")
    if len(raw) > cap:
        cut = raw[:cap].decode("utf-8", errors="ignore")
        text = cut.rsplit("\n", 1)[0].rstrip() if "\n" in cut else cut
        note = f"strategy cut to {cap} bytes (was {len(raw)})"
    return text, note


@dataclass
class StewardStats:
    cycles: int = 0
    by_reason: dict = field(default_factory=dict)
    acts: int = 0
    refused: int = 0
    lines: int = 0
    notes: int = 0
    failed: int = 0
    trace: list = field(default_factory=list)        # one dict per cycle, newest last (200 at most)


class Steward:
    def __init__(self, d):
        self.d = d
        self.kit = getattr(d.adapter, "steward_kit", None)
        self.disabled = self.kit is None or not d.cfg.steward
        self.task: asyncio.Task | None = None
        self.stats = StewardStats()
        self._streams = 0                                # per-cycle /chat/status stream counter
        self.vv: dict = {}
        self._strategy_note = None
        self.reset()

    # ---- state ---------------------------------------------------------------------------
    def reset(self) -> None:
        """A new world (the save epoch changed): forget the chat cursor, the queued wakes and the cycle clocks."""
        self.cursor: int | None = None
        self.pending_chat: list[dict] = []
        self.events: list[str] = []
        self.urgent: list[str] = []
        self.last_game: int | None = None
        self.last_real: float | None = None
        self.was_playing = False                      # the switch was on at the last step (an off→on flip wakes at once)
        self.switched_on = False
        self.pause_woken = False
        self.combat_on = False                        # a fight is running and the combat loop owns the cadence
        self.combat_start = False                     # the "combat started" cycle is due
        self.combat_over = False                      # the "combat over" cycle is due
        self.slowed = False                           # we put the game on 1x for the fight
        self.vv = {}
        if self.d.leases.narrator_owner == "steward":
            self.d.leases.narrator_owner = None

    def active(self) -> bool:
        return self.task is not None and not self.task.done()

    # ---- inputs --------------------------------------------------------------------------
    def observe(self, e: dict) -> None:
        """Director.ingest hands every event here. Only while the narrator plays does the steward collect them."""
        if self.disabled or not self.vv.get("playing"):
            return
        line = self.kit.event_line(e)
        if line:
            self.events.append(line)
            del self.events[:-MAX_EVENT_LINES]
        why = self.kit.wake(e)
        if why:
            self.urgent.append(why)
            del self.urgent[:-MAX_EVENT_LINES]

    async def step(self) -> None:
        d = self.d
        if self.disabled or d.tool_backend is None:
            return
        try:
            vv = await d.bridge.vivarium()
        except BridgeError as e:
            if e.status == 404:
                self.disabled = True
                log.info("the mod has no /vivarium (older mod): the steward stays off")
            return
        except AttributeError:
            self.disabled = True                      # a transport without the vivarium routes
            return
        self.vv = vv
        playing = bool(vv.get("playing"))
        if playing and not self.was_playing:
            self.switched_on = True                   # the player just handed over: look at the world now
        self.was_playing = playing
        seq = int(vv.get("chat_seq", 0) or 0)
        if self.cursor is None or seq < self.cursor:
            self.cursor = seq                         # first sight (or a new log): old history is not answered,
            await self._catch_up(seq)                 # but recent player lines nobody answered yet are
        elif seq > self.cursor:
            try:
                b = await d.bridge.chat_since(self.cursor)
            except BridgeError as e:
                log.warning("steward: chat not read: %s", e)
                return
            for e in b.get("entries") or []:
                if e.get("who") == "player" and not int(e.get("to") or 0) and str(e.get("text", "")).strip():
                    self.pending_chat.append(e)
            del self.pending_chat[:-MAX_CHAT_LINES]
            self.cursor = max(self.cursor, int(b.get("next_seq", seq) or seq))
        if not playing:
            self.events.clear()
            self.urgent.clear()
        await self._combat_state(playing)
        if not vv.get("paused_by_narrator"):
            self.pause_woken = False
        if self.active():
            return
        reason = self._wake(playing)
        if reason:
            self._start(reason, playing)

    async def _combat_state(self, playing: bool) -> None:
        """Follow GET /vivarium combat.active: a fight starting (speed rule in code, a full cycle), ending (a full cycle, the
        speed put back). Only while the narrator plays and the kit has combat; advisor mode has no combat loop."""
        if not hasattr(self.kit, "combat_session"):
            return
        active = playing and bool((self.vv.get("combat") or {}).get("active"))
        if active and not self.combat_on:
            self.combat_on, self.combat_start, self.combat_over = True, True, False
            self.switched_on = False                  # the combat cycle is the first look
            st = self.vv.get("settings") or {}
            env = self.d.bridge.env
            if st.get("slow_for_combat") and env.speed > 1 and not self.slowed:
                try:
                    await self.d.bridge.act("time", {"speed": 1, "combat": True})
                    self.slowed = True
                except BridgeError as e:
                    log.info("steward: speed not slowed for the fight: %s", e)
        elif not active and self.combat_on:
            self.combat_on, self.combat_start = False, False
            self.combat_over = playing                # a fight that ended because the player took over needs no cycle
            await self._restore_speed()

    async def _restore_speed(self) -> None:
        """Put the speed back if we slowed it and the player has not changed it since (still 1x)."""
        if not self.slowed:
            return
        self.slowed = False
        if self.d.bridge.env.speed != 1:
            return                                    # the player changed it: leave it
        try:
            await self.d.bridge.act("time", {"combat": True, "restore": True})
        except BridgeError as e:
            log.info("steward: speed not restored after the fight: %s", e)

    async def _catch_up(self, seq: int) -> None:
        """First sight of a chat log: queue the player's lines after the last narrator line (typed while the agent
        was starting, restarting or away), so they get an answer. Lines the narrator already answered stay history."""
        if seq <= 0:
            return
        try:
            b = await self.d.bridge.chat_since(max(0, seq - MAX_CHAT_LINES))
        except BridgeError:
            return
        tail: list[dict] = []
        for e in b.get("entries") or []:
            if e.get("who") == "narrator":
                tail = []                             # answered up to here
            elif e.get("who") == "player" and not int(e.get("to") or 0) and str(e.get("text", "")).strip():
                tail.append(e)
        self.pending_chat.extend(tail[-CATCH_UP_LINES:])

    def _wake(self, playing: bool) -> str | None:
        d, env = self.d, self.d.bridge.env
        if self.pending_chat:
            return "chat"
        if not playing or env.paused and not self.vv.get("paused_by_narrator"):
            return None
        if self.switched_on:
            self.switched_on = False
            if self.last_game is not None:            # never looked yet: the heartbeat below fires anyway
                return "switched on"
        if not env.paused:
            fight = self._combat_wake()
            if fight is not None:
                return fight or None
        if self.last_real is not None and d.clock() - self.last_real < d.cfg.steward_min_gap_real_s:
            return None
        if self.urgent and not env.paused:
            return "event"
        if env.paused:
            if not self.pause_woken:
                self.pause_woken = True
                return "paused"
            return None
        hb = float(((self.vv.get("settings") or {}).get("heartbeat_hours")) or 6.0) * d.day / 24
        if self.last_game is None or env.game < self.last_game or env.game - self.last_game >= hb:
            return "heartbeat"
        return None

    def _combat_wake(self) -> str | None:
        """The combat wakes. "" = a fight is on and nothing is due (nothing else wakes); None = not in a fight, carry on."""
        d, env = self.d, self.d.bridge.env
        if not (self.combat_on or self.combat_over):
            return None
        if self.last_real is not None and d.clock() - self.last_real < d.cfg.steward_combat_gap_real_s:
            return ""
        if self.combat_start:
            self.combat_start = False
            return "combat started"
        if self.combat_over:
            self.combat_over = False
            return "combat over"
        gap = d.cfg.steward_combat_gap_game_s * TICKS_PER_GAME_S
        if self.last_game is None or env.game < self.last_game or env.game - self.last_game >= gap:
            return "combat"
        return ""

    def _start(self, reason: str, playing: bool) -> None:
        d = self.d
        chat, self.pending_chat = self.pending_chat, []
        if reason == "combat":                         # a combat turn leaves the events for the full cycle that follows the fight
            events = []
        else:
            events = self.events if playing else []
            self.events, self.urgent = [], []
        d.leases.narrator_owner = "steward"            # idle narrator work yields from here on
        if d.idle.active() and d.idle.kind in ("chronicle", "art"):
            d.idle.preempt("steward")                  # a running slot-0 job is cancelled; nothing it made is applied
        self.last_game = d.bridge.env.game
        self.last_real = d.clock()
        self.task = d._spawn(self._cycle(reason, chat, events, playing))

    # ---- a cycle -------------------------------------------------------------------------
    async def _cycle(self, reason: str, chat: list[dict], events: list[str], playing: bool) -> None:
        d = self.d
        t = {"reason": reason, "playing": playing, "chat": [c.get("text", "") for c in chat], "events": list(events)}
        try:
            await self._run(reason, chat, events, playing, t)
        except asyncio.CancelledError:
            raise
        except Exception as e:                          # a cycle never takes the director down
            self.stats.failed += 1
            t["error"] = f"{type(e).__name__}: {e}"[:200]
            log.exception("steward cycle failed")
        finally:
            if d.leases.narrator_owner == "steward":
                d.leases.narrator_owner = None
            self.last_real = d.clock()
            self.stats.cycles += 1
            self.stats.by_reason[reason] = self.stats.by_reason.get(reason, 0) + 1
            self.stats.trace.append(t)
            del self.stats.trace[:-200]

    async def _say(self, who: str, text: str) -> bool:
        try:
            await self.d.bridge.post_chat(who, text)
            return True
        except BridgeError as e:
            log.warning("steward: chat line not posted: %s", e)
            return False

    def _clean_line(self, text: str, cap: int = 500) -> str | None:
        """A narrator line made safe to post, or None: empty, "done", or romantic/sexual with a minor (core/agegate.py)."""
        t = plain(text, cap)
        if t.strip(" .!").lower() in _NO_LINE or not any(c.isalpha() for c in t):
            return None                                  # "done", or a scrap with no words in it (":" seen live)
        if agegate.violation(t, minor_names=self.d.minor_tokens()):
            self.d.gate.skipped_events += 1
            self.d.dropped.append((-1, "steward_line:age_gate"))
            return None
        return t

    async def _status(self, state: str, stream_id: str) -> None:
        """Tell the game's chat the narrator is thinking / idle. Best effort: never breaks or stalls a cycle."""
        try:
            await asyncio.wait_for(self.d.bridge.post_chat_status("narrator", state, stream_id), 3.0)
        except Exception as e:                           # older mod (not_found), network, timeout
            log.debug("steward: chat status %s not posted: %s", state, e)

    async def _run(self, reason: str, chat: list[dict], events: list[str], playing: bool, t: dict) -> None:
        d, kit = self.d, self.kit
        advisor = not playing
        if reason == "combat":
            return await self._run_combat(t)
        try:
            digest, board = await d.bridge.digest(), await d.bridge.goals()
        except BridgeError as e:
            t["error"] = f"read failed: {e}"
            self.stats.failed += 1
            if chat:
                await self._say("narrator", "I can't read the game state right now, so I can't act on that yet. Ask me again in a moment.")
            return
        strategy, snote = load_strategy(d.cfg, kit.default_strategy())
        if snote != self._strategy_note:
            self._strategy_note = snote
            if snote:
                log.info("steward: %s", snote)
        system = kit.system_prompt(advisor) + ("\n\n" + STRATEGY_HEADING + "\n" + strategy if strategy else "") + "\n\n" + gd.DATA_RULE
        session = kit.session(digest, d.bridge, advisor, after_fight=True) if reason == "combat over" else kit.session(digest, d.bridge, advisor)
        tools = session.tools + board_tools(board, advisor)
        allowed = {x.name for x in session.tools} | {"note", "add_order", "do_nothing"} if advisor else {x.name for x in tools}
        alerts = ""
        if getattr(kit, "playbook_alerts", None):
            try:
                alerts = kit.playbook_alerts(digest)
            except Exception:                           # the playbook never takes a cycle down
                log.exception("steward: playbook alerts failed")
        user = self._user(reason, chat, events, board, kit.render_digest(digest), advisor, alerts)
        t["system_chars"], t["user_chars"] = len(system), len(user)

        edits = Edits()
        acts: list[tuple[str, str]] = []                 # (verb, note) of every success
        used = {"notes": 0, "posted": 0, "refused": 0}

        def validator(tool: str, v: dict) -> str | None:
            if advisor and tool in session.acting and acts:
                return "advisor mode allows one task only; say what you did"
            if tool == "note" and used["notes"] >= MAX_NOTES:
                return f"at most {MAX_NOTES} notes per turn"
            text = " ".join(str(x) for x in v.values() if isinstance(x, str))
            if agegate.violation(text, minor_names=d.minor_tokens()):
                return "not allowed: nothing romantic or sexual involving a minor"
            return None

        async def executor(tool: str, v: dict):
            if tool == "do_nothing":
                return None
            if tool == "note":
                line = self._clean_line(v["text"], 160)
                if line is None:
                    return {"error": "empty note"}
                used["notes"] += 1
                if await self._say("note", line):
                    self.stats.notes += 1
                    used["posted"] += 1
                return {"done": "note posted"}
            if tool in BOARD_TOOLS:
                op = {"add_goal": lambda: ("add_goal", plain(v["text"]), v["horizon"]),
                      "set_goal": lambda: ("set_goal", v["goal_id"], v["status"]),
                      "set_plan": lambda: ("set_plan", plain(v["text"])),
                      "add_order": lambda: ("add_order", plain(v["text"])),
                      "drop_order": lambda: ("drop_order", v["order_id"])}[tool]()
                err = edits.try_add(board, op)
                return {"error": err} if err else {"done": "recorded; it is saved to the board at the end of the turn"}
            out = await session.run(tool, v)
            if out.ok:
                acts.append((out.verb, out.note))
                self.stats.acts += 1
            elif "refused" in out.result:
                used["refused"] += 1
                self.stats.refused += 1
            return out.result

        harness = ToolHarness(d.tool_backend, tools, mode=d.tool_mode, max_steps=COMBAT_START_STEPS if reason == "combat started" else STEPS,
                              time_budget_s=BUDGET_S, step_timeout_s=STEP_TIMEOUT_S)
        self._streams += 1
        stream_id = f"steward-{os.getpid()}-{self._streams}"
        await self._status("thinking", stream_id)
        try:
            res = await harness.run(system=system, user=user, executor=executor, validator=validator, allowed=sorted(allowed),
                                    slot=d.cfg.narrator_slot, final_max=FINAL_MAX, max_tokens=220)
        finally:
            await self._status("idle", stream_id)
        t.update(outcome=res.outcome, reason_ended=res.reason, protocol=res.protocol, failures=res.failures[-4:],
                 executed=[(e.tool, e.args, e.result) for e in res.executed],
                 elapsed_s=round(res.elapsed_s, 2))

        # no automatic chat notes: the game writes one greentext line per act itself (from the act's "why")
        # the cycle's final line
        final = self._clean_line(res.final_text, 500) if res.outcome == "final" else None
        if final and await self._say("narrator", final):
            self.stats.lines += 1
            used["posted"] += 1
            t["line"] = final
        elif chat and not used["posted"] and not res.executed:   # the player asked and would otherwise hear nothing
            await self._say("narrator", "I couldn't think that through just now. Ask me again in a moment.")
        # the goals board: one PUT with the edits and the log line
        if edits or acts:
            await self._save_board(board, edits, reason, chat, acts, t)

    async def _run_combat(self, t: dict) -> None:
        """A combat turn: GET /combat and the plan line, the small combat prompt, the combat tools, three steps. No digest, no
        board edits (notes only), no closing chat line (the game prints each act and its why as greentext)."""
        d, kit = self.d, self.kit
        try:
            view, board = await d.bridge.combat(), await d.bridge.goals()
        except BridgeError as e:
            t["error"] = f"read failed: {e}"
            self.stats.failed += 1
            return
        if not view.get("active"):
            t["skipped"] = "the fight is over"          # the next step sees combat.active false and starts the "combat over" cycle
            return
        system = kit.combat_system_prompt() + "\n\n" + gd.DATA_RULE
        session = kit.combat_session(view, d.bridge)
        allowed = set(kit.combat_tools)
        tools = [x for x in session.tools + board_tools(board) if x.name in allowed]
        user = "\n".join([
            "=== COMBAT TURN ===\nA fight is on. Look at the fight below and give the orders it needs now, or call do_nothing if the squad is fine.\n",
            "=== YOUR PLAN ===\n" + (plain(board.get("plan") or "", 300) or "none") + "\n",
            "=== THE FIGHT ===\n" + gd.data_section("combat", kit.render_combat(view)),
            "Decide now: call at most a couple of tools, then end with done."])
        t["system_chars"], t["user_chars"] = len(system), len(user)
        used = {"notes": 0}

        def validator(tool: str, v: dict) -> str | None:
            if tool == "note" and used["notes"] >= MAX_NOTES:
                return f"at most {MAX_NOTES} notes per turn"
            if agegate.violation(" ".join(str(x) for x in v.values() if isinstance(x, str)), minor_names=d.minor_tokens()):
                return "not allowed: nothing romantic or sexual involving a minor"
            return None

        async def executor(tool: str, v: dict):
            if tool == "do_nothing":
                return None
            if tool == "note":
                line = self._clean_line(v["text"], 160)
                if line is None:
                    return {"error": "empty note"}
                used["notes"] += 1
                if await self._say("note", line):
                    self.stats.notes += 1
                return {"done": "note posted"}
            out = await session.run(tool, v)
            if out.ok:
                self.stats.acts += 1
            elif "refused" in out.result:
                self.stats.refused += 1
            return out.result

        harness = ToolHarness(d.tool_backend, tools, mode=d.tool_mode, max_steps=COMBAT_STEPS, time_budget_s=COMBAT_BUDGET_S,
                              step_timeout_s=COMBAT_STEP_TIMEOUT_S)
        res = await harness.run(system=system, user=user, executor=executor, validator=validator, allowed=sorted(allowed),
                                slot=d.cfg.narrator_slot, final_max=COMBAT_FINAL_MAX, max_tokens=COMBAT_MAX_TOKENS)
        t.update(outcome=res.outcome, reason_ended=res.reason, protocol=res.protocol, failures=res.failures[-4:],
                 executed=[(e.tool, e.args, e.result) for e in res.executed], elapsed_s=round(res.elapsed_s, 2))

    def _log_line(self, reason: str, chat: list[dict], acts: list[tuple[str, str]], edits: Edits) -> str:
        label = {"chat": "Asked", "heartbeat": "Routine look", "paused": "After my pause", "combat over": "After the fight"}.get(reason, "On " + reason)
        if reason == "chat" and chat:
            label += f" ({plain(chat[-1].get('text', ''), 50)})"
        did = [n for _, n in acts] + (["updated the goals board"] if edits else [])
        return plain(f"{label}: " + "; ".join(did), 300)

    async def _save_board(self, board: dict, edits: Edits, reason: str, chat: list[dict], acts, t: dict) -> None:
        bridge = self.d.bridge
        line = self._log_line(reason, chat, acts, edits)
        for attempt in (0, 1):
            try:
                fields = edits.apply(board) if edits else {}
            except ValueError as e:                     # the board changed under us and an edit no longer fits: keep the log
                log.info("steward: board edit dropped: %s", e)
                fields = {}
            try:
                await bridge.put_goals(int(board.get("rev", 0)), log_add=[line], **fields)
                t["board_saved"] = True
                return
            except BridgeError as e:
                if e.code != "conflict" or attempt:
                    log.warning("steward: goals board not saved: %s", e)
                    return
                try:
                    board = await bridge.goals()           # stale rev: take the player's board and replay our edits on it
                except BridgeError:
                    return

    def _user(self, reason: str, chat: list[dict], events: list[str], board: dict, digest_text: str, advisor: bool, alerts: str = "") -> str:
        d, st = self.d, (self.vv.get("settings") or {})
        parts = []
        if advisor:
            parts.append("=== MODE ===\nThe narrator is not playing: you are only an advisor. Act only on what the player's message explicitly asks, "
                         "at most one task, and say what you did.\n")
        why = []
        if chat:
            why.append("The player said in the chat:\n" + "\n".join(f"> {plain(c.get('text', ''), 500)}" for c in chat))
        if reason == "heartbeat":
            why.append("Routine look: time has passed since you last checked. Look for what needs doing.")
        elif reason == "event":
            why.append("Something important happened (see below). Deal with it first.")
        elif reason == "combat started":
            why.append("A fight has started (see COMBAT in the state below). Act now: draft the fighters and put them in cover or at the chokepoint, "
                       "send non-fighters indoors, then plan defences if there is time. Leave the long-term work for later.")
        elif reason == "combat over":
            why.append("The fight is over. Undraft the fighters (squad undraft), rescue the downed, make sure the wounded are tended, "
                       "check fires, and send everyone back to work. Then the routine look.")
        elif reason == "paused":
            why.append("The game is paused because you paused it for an emergency. If it is handled, unpause with set_time (pause no); otherwise carry on.")
        if events:
            why.append("Since you last looked:\n" + "\n".join(f"- {x}" for x in events))
        parts.append("=== WHY YOU WOKE ===\n" + "\n".join(why) + "\n")
        if alerts.strip():
            parts.append(alerts.rstrip("\n") + "\n")
        sets = []
        sets.append("You may change the game speed or pause it whenever you like." if st.get("controls_speed") else
                    ("You may not change the game speed. You may pause only in a real emergency (set_time with emergency yes)."
                     if st.get("may_pause_for_emergencies", True) else "You may not pause the game or change its speed."))
        if self.vv.get("paused_by_narrator"):
            sets.append("The game is paused by you.")
        parts.append("=== SETTINGS ===\n" + " ".join(sets) + "\n")
        parts.append("=== YOUR GOALS BOARD ===\n" + render_board(board) + "\n")
        parts.append("=== THE STATE OF THE GAME ===\n" + gd.data_section("digest", digest_text))
        parts.append("Decide now: call the tools you need, then end with one short chat line for the player (or done).")
        return "\n".join(parts)

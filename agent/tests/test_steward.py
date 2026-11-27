"""The vivarium steward (core/steward.py): wakes, cycles, tools reaching the bridge, the goals board, slot-0 ownership."""
import asyncio
import json
from pathlib import Path

import pytest

from conftest import run
from rimworldllm_agent.core import steward as stew
from rimworldllm_agent.core.config import Config
from rimworldllm_agent.core.lease import Cls
from rimworldllm_agent.core.toolloop import Reply

SAMPLE = json.loads((Path(__file__).parent / "data" / "steward_digest_sample.json").read_text())
DONE = json.dumps({"done": True})


def call(tool, **a):
    return json.dumps({"tool": tool, "args": a})


def final(text):
    return json.dumps({"final": text})


class Fake:
    """A scripted tool backend. Each chat() answers the next script item (the last one repeats). ``gate`` blocks a call."""

    def __init__(self, script):
        self.script, self.calls, self.gate = list(script), [], None

    async def chat(self, messages, *, grammar=None, tools=None, tool_choice=None, max_tokens=160, temperature=0.2, timeout=30.0, slot=None):
        self.calls.append({"messages": messages, "slot": slot})
        if self.gate is not None:
            await self.gate.wait()
        item = self.script.pop(0) if len(self.script) > 1 else self.script[0]
        return item if isinstance(item, Reply) else Reply(text=item)

    @property
    def cycles(self):
        return sum(1 for c in self.calls if len(c["messages"]) == 2)


async def boot(world, script=(DONE,), playing=True, **cfg):
    w = world
    for k, v in cfg.items():
        setattr(w.cfg, k, v)
    w.cfg.wants = False                  # the shared tool backend is the steward's alone in these tests
    w.fb.digest = json.loads(json.dumps(SAMPLE))
    await w.go()
    w.d.tool_backend, w.d.tool_mode = Fake(list(script)), "grammar"
    w.fb.playing = False
    await w.d.steward.step()             # first sight: the chat cursor starts here
    w.fb.playing = playing
    return w, w.d.steward, w.d.tool_backend


async def cycle(w):
    await w.d.step()                     # the real wiring: poll events, ingest (observe), steward.step
    await w.d.drain()


def chat(w, text, **kw):
    return w.fb.add_chat("player", text, **kw)


def lines(w, who):
    return [e["text"] for e in w.fb.chat if e["who"] == who]


# ---------------------------------------------------------------- wakes
def test_chat_wakes_at_once_and_is_answered(world):
    async def go():
        w, st, fake = await boot(world, [final("Cooking is already first for Ana.")])
        w.fb.playing = False                     # advisor mode: chat wakes, nothing else does
        chat(w, "who cooks?")
        await cycle(w)
        assert lines(w, "narrator") == ["Cooking is already first for Ana."]
        assert "who cooks?" in fake.calls[0]["messages"][1]["content"]
        assert st.stats.by_reason == {"chat": 1}
    run(go())


def test_old_history_and_named_lines_are_not_answered(world):
    async def go():
        w = world
        w.fb.add_chat("player", "an old line from before the agent connected")
        w.fb.add_chat("narrator", "the narrator answered it back then")
        w, st, fake = await boot(world, playing=False)
        await cycle(w)
        assert fake.calls == []                   # the cursor started at the current seq
        chat(w, "@Bram, how are you?", to=2, name="Bram")
        w.fb.add_chat("narrator", "a narrator line")
        w.fb.add_chat("colonist", "fine, thanks", to=2, name="Bram")
        await cycle(w)
        assert fake.calls == []
        chat(w, "hello narrator")
        await cycle(w)
        assert len(fake.calls) == 1
    run(go())


def test_chat_cursor_resets_on_a_new_world(world):
    async def go():
        w, st, fake = await boot(world, playing=False)
        chat(w, "one")
        await cycle(w)
        assert st.pending_chat == [] and st.cursor >= 1     # read; the steward's own answer may follow it in the log
        w.fb.reload_game()
        w.fb.chat, w.fb.chat_seq = [], 0         # the loaded save has its own chat log
        await w.d.step()
        assert st.cursor is None                 # reset by the epoch change
        w.fb.add_chat("player", "answered in that save")
        w.fb.add_chat("narrator", "an answer in that save")
        await w.d.steward.step()                 # first sight of the new world: answered lines are history
        assert fake.cycles == 1
        chat(w, "after the load")
        await cycle(w)
        assert fake.cycles == 2
    run(go())


@pytest.mark.parametrize("etype,data,severity", [
    ("raid_start", {}, 0.9), ("mental_break", {}, 0.8), ("death", {}, 1.0),
    ("alert", {"alert": "Alert_X", "label": "Something", "critical": True}, 0.8),
    ("alert", {"alert": "Alert_Fire", "label": "Fire", "critical": False}, 0.8),
    ("alert", {"alert": "Alert_Starvation", "label": "Starvation", "critical": False}, 0.8),
    ("alert", {"alert": "Alert_ColonistNeedsRescue", "label": "Colonist needs rescue", "critical": False}, 0.8),
    ("letter", {"letter": 77, "label": "Quest", "choices": ["Accept", "Reject"], "quest": True}, 0.6),
])
def test_event_wake_kinds(world, etype, data, severity):
    async def go():
        w, st, fake = await boot(world)
        w.fb.tick += 100
        w.fb.add_event(etype, [], f"a {etype}", [], severity=severity, data=data)
        # a heartbeat is not due (the first cycle ran at the first playing step), so the wake is the event
        await cycle(w)                       # heartbeat first (no cycle yet)
        fake.calls.clear()
        w.real += 30
        w.fb.add_event(etype, [], f"a {etype} again", [], severity=severity, data=data)
        await cycle(w)
        assert st.stats.by_reason.get("event") == 1, st.stats.by_reason
        assert "Something important happened" in fake.calls[0]["messages"][1]["content"]
    run(go())


@pytest.mark.parametrize("etype,data,severity", [
    ("alert", {"alert": "Alert_LowFood", "label": "Low food", "critical": False}, 0.4),
    ("letter", {"letter": 80, "label": "A trader", "choices": [], "quest": False}, 0.3),
    ("injury", {}, 0.5), ("death", {"animal": True}, 0.6),
])
def test_other_events_only_queue_for_the_next_cycle(world, etype, data, severity):
    async def go():
        w, st, fake = await boot(world)
        await cycle(w)                       # the opening heartbeat cycle
        fake.calls.clear()
        w.real += 30
        w.fb.add_event(etype, [], f"queued {etype}", [], severity=severity, data=data)
        await cycle(w)
        assert fake.calls == []             # no wake
        w.fb.tick += 6 * 2500 + 10           # the heartbeat comes; the event rides along
        w.real += 30
        await cycle(w)
        user = fake.calls[0]["messages"][1]["content"]
        assert (f"queued {etype}" in user) == (not data.get("animal")) or etype == "death"
    run(go())


def test_heartbeat_counts_game_ticks_and_respects_the_real_floor(world):
    async def go():
        w, st, fake = await boot(world)
        await cycle(w)
        assert st.stats.by_reason == {"heartbeat": 1}      # the first look when it starts playing
        w.fb.tick += 6 * 2500 - 100
        w.real += 60
        await cycle(w)
        assert st.stats.cycles == 1                         # not yet 6 hours
        w.fb.tick += 200
        w.real += 5                                         # ticks are due but the real floor (20 s) is not
        await w.d.steward.step()
        assert st.stats.cycles == 1
        w.real += 30
        await cycle(w)
        assert st.stats.by_reason == {"heartbeat": 2}
        w.fb.viv_settings["heartbeat_hours"] = 1.0          # the player changed it: 1 hour = 2500 ticks
        w.fb.tick += 2600
        w.real += 30
        await cycle(w)
        assert st.stats.by_reason == {"heartbeat": 3}
    run(go())


def test_paused_game_has_no_heartbeat_but_chat_still_wakes(world):
    async def go():
        w, st, fake = await boot(world)
        await cycle(w)
        w.fb.paused = True
        w.fb.tick += 10 * 2500
        w.real += 60
        w.fb.add_event(w and "raid_start", [], "raid", [], severity=0.9)
        await cycle(w)
        assert st.stats.cycles == 1
        chat(w, "what now?")
        await cycle(w)
        assert st.stats.by_reason == {"heartbeat": 1, "chat": 1}
    run(go())


def test_not_playing_only_chat_wakes(world):
    async def go():
        w, st, fake = await boot(world, playing=False)
        w.fb.tick += 50 * 2500
        w.real += 100
        w.fb.add_event("raid_start", [], "raid", [], severity=0.9)
        await cycle(w)
        assert fake.calls == [] and st.events == [] and st.urgent == []
        chat(w, "hi")
        await cycle(w)
        assert st.stats.by_reason == {"chat": 1}
        assert "ADVISOR MODE" in fake.calls[0]["messages"][0]["content"]
    run(go())


def test_chat_ignores_the_floor_and_one_cycle_at_a_time(world):
    async def go():
        w, st, fake = await boot(world, [final("first"), final("second")])
        w.fb.playing = False
        fake.gate = asyncio.Event()
        chat(w, "one")
        await w.d.steward.step()
        await asyncio.sleep(0.01)
        assert st.active()
        chat(w, "two")                                   # arrives mid-cycle
        await w.d.steward.step()
        assert st.stats.cycles == 0 and len(st.pending_chat) == 1
        fake.gate.set()
        await w.d.drain()
        assert st.stats.cycles == 1
        await cycle(w)                                   # no real time passed: chat ignores the floor
        assert st.stats.by_reason == {"chat": 2}
        assert "two" in fake.calls[-1]["messages"][1]["content"] and lines(w, "narrator") == ["first", "second"]
    run(go())


def test_the_narrators_own_pause_wakes_it_once(world):
    async def go():
        w, st, fake = await boot(world)
        await cycle(w)
        w.fb.paused, w.fb.paused_by_narrator = True, True
        w.real += 30
        await cycle(w)
        assert st.stats.by_reason == {"heartbeat": 1, "paused": 1}
        w.real += 30
        await cycle(w)
        assert st.stats.cycles == 2                         # once per pause
    run(go())


def test_an_older_mod_without_vivarium_keeps_the_steward_off(world):
    async def go():
        w, st, fake = await boot(world)
        w.fb.vivarium_supported = False
        await cycle(w)
        assert st.disabled and fake.calls == []
        n = sum(1 for r in w.fb.requests if r[1] == "/vivarium")
        await cycle(w)
        assert sum(1 for r in w.fb.requests if r[1] == "/vivarium") == n      # not asked again
    run(go())


def test_idle_step_costs_one_vivarium_read(world):
    async def go():
        w, st, fake = await boot(world, playing=False)
        w.fb.requests.clear()
        await w.d.steward.step()
        assert w.fb.requests == [("GET", "/vivarium")]
    run(go())


# ---------------------------------------------------------------- tools reach the bridge
def test_advisor_mode_does_one_task_and_says_so(world):
    async def go():
        w, st, fake = await boot(world, [
            call("set_work", pawn="Ana", work="Cooking", priority=1),
            call("assign_bed", pawn="Eli", bed_id="3305"),          # a second task: refused in advisor mode
            final("Ana cooks first now. I stopped there.")])
        w.fb.playing = False
        chat(w, "put Ana on cooking")
        await cycle(w)
        assert w.fb.act_log == [("work", {"pawn": 101, "priorities": {"Cooking": 1}})]
        assert "one task only" in json.dumps(fake.calls[2]["messages"])
        assert lines(w, "narrator") == ["Ana cooks first now. I stopped there."]
        assert lines(w, "note") == ["→ set Cooking 1 for pawn 101"]
        assert w.fb.goals_board["log"][-1]["text"].startswith("Asked (put Ana on cooking)")
    run(go())


def test_playing_runs_many_actions_with_resolved_ids(world):
    async def go():
        w, st, fake = await boot(world, [
            call("set_work", pawn="Ana", work="Cooking", priority=1),
            call("assign_bed", pawn="Eli", bed_id="3305"),
            call("answer_letter", letter_id="77", choice="accept"),
            call("draft", pawns="Bram, Dara", drafted="yes"),
            call("set_research", project="Batteries"),
            call("order", pawn="Bram", order="move", x=40, z=61, target_id=0, hold="no"),
            call("set_policy", pawn="Cy", kind="food", name="simple"),
            final("All done.")])
        await cycle(w)
        got = {v: b for v, b in w.fb.act_log}
        assert got["work"] == {"pawn": 101, "priorities": {"Cooking": 1}}
        assert got["bed"] == {"pawn": 105, "bed": 3305}
        assert got["letter"] == {"letter": 77, "choice": "Accept"}
        assert got["draft"] == {"pawns": [102, 104], "drafted": True}
        assert got["research"] == {"project": "Batteries"}
        assert got["order"] == {"pawn": 102, "order": "move", "x": 40, "z": 61}
        assert got["policy"] == {"pawn": 103, "kind": "food", "name": "Simple"}
        assert len(lines(w, "note")) == 3                          # at most three notes a cycle
    run(go())


def test_own_note_replaces_automatic_notes(world):
    async def go():
        w, st, fake = await boot(world, [
            call("assign_bed", pawn="Eli", bed_id="3305"),
            call("note", text="Eli has a bed now"),
            final("Done with beds.")])
        await cycle(w)
        assert w.fb.act_log == [("bed", {"pawn": 105, "bed": 3305})]
        assert lines(w, "note") == ["Eli has a bed now"]              # no "→ bed 3305 ..." doubled on top
    run(go())


def test_schedule_attack_hold_and_time_bodies(world):
    async def go():
        w, st, fake = await boot(world, [
            call("set_schedule", pawn="Dara", from_hour=22, to_hour=6, assignment="sleep"),
            call("order", pawn="Bram", order="attack", x=0, z=0, target_id=5512, hold="no"),
            call("order", pawn="Bram", order="hold", x=0, z=0, target_id=0, hold="yes"),
            call("set_time", pause="yes", speed="leave", emergency="yes", reason="raid at the gate"),
            DONE])
        await cycle(w)
        got = [(v, b) for v, b in w.fb.act_log]
        assert got[0] == ("schedule", {"pawn": 104, "from_hour": 22, "to_hour": 6, "assignment": "sleep"})
        assert got[1] == ("order", {"pawn": 102, "order": "attack", "target": 5512})
        assert got[2] == ("order", {"pawn": 102, "order": "hold", "hold": True})
        assert got[3][0] == "time" and got[3][1]["pause"] is True and got[3][1]["emergency"] is True and "speed" not in got[3][1]
        assert w.fb.paused_by_narrator
    run(go())


def test_a_refusal_goes_back_to_the_model_and_is_not_counted_as_done(world):
    async def go():
        w, st, fake = await boot(world, [call("set_time", pause="yes", speed="leave", emergency="no", reason="rest"),
                                         final("The game would not let me pause.")])
        w.fb.act_refuse["time"] = "pausing needs an emergency"
        await cycle(w)
        sec = fake.calls[1]["messages"]
        assert "pausing needs an emergency" in json.dumps(sec)
        assert w.fb.act_log == [] and st.stats.refused == 1 and st.stats.acts == 0
        assert lines(w, "narrator") == ["The game would not let me pause."]
        assert w.fb.goals_board["rev"] == 0                          # nothing done: no board write, no log line
    run(go())


def test_unknown_names_and_policies_come_back_as_errors(world):
    async def go():
        w, st, fake = await boot(world, [call("set_policy", pawn="Ana", kind="food", name="Gourmet"),
                                         call("set_research", project="Warp drive"),
                                         call("draft", pawns="Zed", drafted="yes"), DONE])
        await cycle(w)
        out = json.dumps(fake.calls[-1]["messages"])
        assert "no food policy named Gourmet" in json.dumps(fake.calls[1]["messages"])
        assert "no available project named Warp drive" in json.dumps(fake.calls[2]["messages"])
        assert "no colonist named Zed" in out and w.fb.act_log == []
    run(go())


def test_the_loop_runs_on_slot_zero_with_the_playbook_and_digest(world):
    async def go():
        w, st, fake = await boot(world, [DONE])
        await cycle(w)
        c = fake.calls[0]
        assert c["slot"] == w.cfg.narrator_slot == 0
        sysm, user = c["messages"][0]["content"], c["messages"][1]["content"]
        assert "## Strategy notes" in sysm and "GAME DATA RULE" in sysm and "set_work(" in sysm
        assert "PEOPLE (5)" in user and "letter #77" in user and "YOUR GOALS BOARD" in user
    run(go())


# ---------------------------------------------------------------- final line
@pytest.mark.parametrize("text", ["done", "Done.", "nothing to say"])
def test_done_and_empty_final_lines_are_silent(world, text):
    async def go():
        w, st, fake = await boot(world, [final(text)])
        await cycle(w)
        assert lines(w, "narrator") == [] and st.stats.cycles == 1
    run(go())


def test_a_line_pairing_a_minor_with_romance_is_not_posted(world):
    async def go():
        w, st, fake = await boot(world, [final("Eli, a kiss under the stars, my darling?")])
        w.d.ages[105] = 15
        w.d.names[105], w.d.name_tokens[105] = "Eli", {"eli"}
        await cycle(w)
        assert lines(w, "narrator") == []
    run(go())


def test_a_dead_backend_on_a_chat_gets_a_plain_reply(world):
    async def go():
        w, st, fake = await boot(world, ["not json at all"])
        w.fb.playing = False
        chat(w, "hello?")
        await cycle(w)
        assert "couldn't think that through" in lines(w, "narrator")[0]
        chat(w, "again")
        w.d.tool_backend = None
        await cycle(w)                                       # no backend: the steward is simply off
        assert len(lines(w, "narrator")) == 1
    run(go())


def test_digest_unreadable_on_a_chat_says_so(world):
    async def go():
        w, st, fake = await boot(world)
        w.fb.playing = False
        w.fb.digest = None
        chat(w, "hello?")
        await cycle(w)
        assert "can't read the game state" in lines(w, "narrator")[0] and fake.calls == []
    run(go())


def test_a_wordless_final_line_on_a_chat_gets_a_plain_reply(world):
    async def go():
        w, st, fake = await boot(world, [final(":")])        # seen live on the 26B
        w.fb.playing = False
        chat(w, "should we take the quest?")
        await cycle(w)
        got = lines(w, "narrator")
        assert len(got) == 1 and ":" != got[0] and "couldn't think that through" in got[0]
    run(go())


def test_recent_unanswered_lines_are_caught_up_on_first_sight(world):
    async def go():
        w = world
        w.fb.add_chat("player", "answered earlier")
        w.fb.add_chat("narrator", "an earlier answer")
        w.fb.add_chat("player", "typed while the agent was restarting")
        w, st, fake = await boot(world, [final("Here now.")], playing=False)
        await cycle(w)
        assert lines(w, "narrator")[-1] == "Here now."
        assert "typed while the agent was restarting" in fake.calls[0]["messages"][1]["content"]
    run(go())


def test_switching_on_wakes_the_steward_at_once(world):
    async def go():
        w, st, fake = await boot(world, [final("Advice."), final("Taking over.")], playing=False)
        chat(w, "advise me")
        await cycle(w)                           # an advisor cycle sets the heartbeat clock
        assert fake.cycles == 1
        w.fb.playing = True                      # the player flips the switch
        await cycle(w)
        assert fake.cycles == 2 and st.stats.by_reason.get("switched on") == 1
    run(go())

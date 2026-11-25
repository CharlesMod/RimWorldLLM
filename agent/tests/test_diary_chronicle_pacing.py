"""Steps 4-6: diaries, the narrator's chronicle, and pacing (idle work yields to actor windows at once)."""
import asyncio
import json

import pytest

from conftest import run
from hermesvoice_agent.core.bridge import BridgeError
from hermesvoice_agent.core.chronicle import Chronicle, est_tokens, messages_tokens
from hermesvoice_agent.core.fakes import FakeLLM
from hermesvoice_agent.core.lease import Cls, Now
from hermesvoice_agent.core.llm import Completion
from hermesvoice_agent.core.minds import Memory
from hermesvoice_agent.games.rimworld import RimWorldAdapter

DAY = 60000
AD = RimWorldAdapter()
DIARY = "Today I kept my head down and got the stew finished before dark."
CHRON = "Ana was shot and the colonists patched her up. Nobody slept well, and the food ran lower."


async def step(w):
    await w.d.step(); await w.d.drain()


def inject(w, aid, text, game, **kw):
    w.d.minds[aid].memories.append(Memory(id=w.d.minds[aid].next_mem_id(), game=game, text=text, **kw))


# ---- step 4: diaries ---------------------------------------------------------------------
def test_diary_once_per_actor_per_day_late_in_the_day_and_sent_as_a_diary_thought(world):
    async def go():
        w = await world.go()
        w.llm.script["diary"] = [DIARY, DIARY.replace("stew", "soup"), DIARY]
        inject(w, 1, "I saw Bram eat alone.", 5000)
        w.fb.tick = int(0.3 * DAY)
        await step(w)
        assert not w.d.minds[1].diary                                  # too early in the day
        w.fb.tick = int(0.7 * DAY)
        await step(w)
        assert [e.day for e in w.d.minds[1].diary] == [0]
        assert w.fb.thought_log and w.fb.thought_log[0]["kind"] == "diary" and w.fb.thought_log[0]["pawn"] == 1
        assert w.fb.thought_log[0]["text"] == DIARY and w.fb.thought_log[0]["op_id"]
        n = len(w.fb.thought_log)
        await step(w); await step(w)
        assert len(w.fb.thought_log) == n                              # once per day
        assert not w.d.minds[2].diary                                  # nothing happened to Bram's memory
    run(go())


def test_diary_keeps_last_seven_and_is_saved_in_the_blob(world):
    async def go():
        w = await world.go()
        for day in range(9):
            inject(w, 1, f"I did chore number {day}.", day * DAY + 100)
            w.fb.tick = day * DAY + int(0.8 * DAY)
            w.llm.script["diary"] = [DIARY]
            await step(w)
        await step(w)
        return w
    w = run(go())
    assert [e.day for e in w.d.minds[1].diary] == list(range(2, 9))
    blob, _ = w.fb.minds[1]
    assert len(blob["diary"]) == 7 and blob["last_diary_day"] == 8


def test_diary_for_yesterday_is_written_early_next_day_and_bad_text_is_dropped(world):
    async def go():
        w = await world.go()
        inject(w, 1, "I mended a wall.", 100)
        w.fb.tick = DAY + 500                                          # early day 1: yesterday's entry
        w.llm.script["diary"] = ["As an AI language model I have no day to write about.", DIARY]
        await step(w)
        assert not w.d.minds[1].diary and (1, "diary:refusal") in w.d.dropped
        w.real += 100
        await step(w)
        assert [e.day for e in w.d.minds[1].diary] == [0]
    run(go())


def test_diary_can_be_turned_off(world):
    async def go():
        w = await world.go()
        w.d.cfg.diary_enabled = False
        inject(w, 1, "I mended a wall.", 100)
        w.fb.tick = int(0.9 * DAY)
        await step(w)
        assert not w.d.minds[1].diary and not [c for c in w.llm.calls if c["kind"] == "diary"]
    run(go())


# ---- step 5: chronicle -------------------------------------------------------------------
def test_chronicle_one_paragraph_per_day_skips_when_nothing_new(world):
    async def go():
        w = await world.go()
        w.llm.script["chronicle"] = [CHRON]
        w.fb.add_event("injury", [(1, "victim")], "Ana was shot.", [1], 0.6)
        await step(w)
        assert not w.d.idle.log or all(x["kind"] != "chronicle" for x in w.d.idle.log)   # same day: wait
        w.fb.tick = DAY + 100
        await step(w)
        ch = [x for x in w.d.idle.log if x["kind"] == "chronicle"]
        assert len(ch) == 1 and ch[0]["day"] == 0 and ch[0]["text"] == CHRON
        calls = [c for c in w.llm.calls if c["kind"] == "chronicle"]
        assert calls[0]["slot"] == 0                                    # slot 0, the narrator
        assert "Ana was shot." in calls[0]["messages"][1][1]
        w.fb.tick = 2 * DAY + 100
        await step(w); await step(w)
        assert sum(c["kind"] == "chronicle" for c in w.llm.calls) == 1  # nothing new: skipped
        return w
    w = run(go())
    md = (w.cfg.data_dir / "chronicle.md").read_text()
    assert "## Day 1" in md and CHRON in md
    st = json.loads((w.cfg.data_dir / "chronicle.json").read_text())
    assert st["recent"][0]["text"] == CHRON and st["pending"] == []


def test_chronicle_on_death_at_once_and_never_proposes_events(world):
    async def go():
        w = await world.go()
        w.llm.script["chronicle"] = ["Bram died of his wounds in the evening, and the others buried him without speeches."]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1, 3], 1.0)
        await step(w); await step(w)                                    # the actors speak first, then the narrator
        ch = [x for x in w.d.idle.log if x["kind"] == "chronicle"]
        assert len(ch) == 1 and ch[0]["entry_kind"] == "major"
        sys_prompt = [c for c in w.llm.calls if c["kind"] == "chronicle"][0]["messages"][0][1]
        assert "never" in sys_prompt and "propose" in sys_prompt
        assert w.fb.chronicle_log and w.fb.chronicle_log[0]["kind"] == "major"   # stored through the bridge, not as speech
        assert not [b for b in w.fb.say_log if b.get("pawn") == 0]
        return w
    run(go())


def test_chronicle_context_stays_under_6000_tokens_and_rolls_into_a_summary(world, tmp_path):
    async def go():
        w = await world.go()
        long = " ".join(["The colonists endured the cold and the hunger and the long shifts at the field"] * 3) + "."
        for day in range(12):
            for k in range(25):
                w.fb.tick = day * DAY + 100 + k
                w.fb.add_event("injury", [(1, "victim")], f"Ana took wound {k} on day {day}: " + "x" * 80, [1], 0.6)
            w.fb.tick = (day + 1) * DAY + 5
            w.llm.script["chronicle"] = [long]
            w.llm.script["chronicle_summary"] = ["Earlier the colonists endured cold, hunger and long shifts. " * 4]
            await step(w); await step(w)
        return w
    w = run(go())
    calls = [c for c in w.llm.calls if c["kind"] == "chronicle"]
    assert len(calls) >= 10
    assert max(messages_tokens(c["messages"]) for c in calls) <= 6000
    st = w.d.chronicle.state
    assert len(st.recent) <= 3 and st.summary and any(c["kind"] == "chronicle_summary" for c in w.llm.calls)
    assert len(st.summary.split()) <= 400
    assert "Earlier the colonists" in calls[-1]["messages"][1][1]       # the summary feeds the next entry


def test_chronicle_budget_trims_hard_when_the_budget_is_small():
    ch = Chronicle(None, DAY)
    for i in range(80):
        ch.note(i, f"Event {i} " + "word " * 40, severity=i / 100)
    msgs = ch.build_messages(AD, "day", 0, ch.state.pending, ["a ledger line " * 5] * 12, budget=900)
    assert messages_tokens(msgs) <= 900
    assert "Event 79" in msgs[1][1]                                     # the most severe survive


def test_chronicle_survives_restart_and_follows_a_rolled_back_save(tmp_path):
    c = Chronicle(tmp_path, DAY)
    c.note(100, "A thing happened.", 0.6)
    c.note(5 * DAY, "A later thing happened.", 0.6)
    c.save()
    c2 = Chronicle(tmp_path, DAY)
    assert len(c2.state.pending) == 2
    c2.rollback(2 * DAY)                                                # an earlier save was loaded
    assert [n.text for n in c2.state.pending] == ["A thing happened."]


def test_chronicle_entry_can_go_to_the_bridge_when_enabled(world):
    async def go():
        w = await world.go()
        assert w.d.cfg.chronicle_bridge                                  # on by default
        w.llm.script["chronicle"] = ["Bram died of his wounds in the evening, and the others buried him without speeches."]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1], 1.0)
        await step(w); await step(w)
        assert w.fb.chronicle_log and w.fb.chronicle_log[0]["day"] == 0 and w.fb.chronicle_log[0]["kind"] == "major"
        got = await w.d.bridge.get_chronicle(0)                          # GET /chronicle?since_day
        assert [(e["day"], e["kind"]) for e in got] == [(0, "major")] and "Bram" in got[0]["text"]
        assert await w.d.bridge.get_chronicle(1) == []
    run(go())


def test_narrator_coverage_min_gap_bypassed_by_deaths_and_major_threats(world):
    async def go():
        w = await world.go()
        d = w.d
        inj = d.leases.make_item(1, "injury", d.now(), {"type": "injury", "severity": 0.5})
        death = d.leases.make_item(1, "death", d.now(), {"type": "death", "severity": 1.0})
        assert d._cover_allowed(inj, Now(1000, 0.0))                    # nothing covered yet
        d._last_cover = Now(1000, 0.0)
        assert not d._cover_allowed(inj, Now(1500, 10.0))               # too soon on both clocks
        assert d._cover_allowed(death, Now(1500, 10.0))                 # a death bypasses
        assert d._cover_allowed(d.leases.make_item(1, "raid_start", d.now(), {"type": "raid_start"}), Now(1500, 10.0))
        assert d._cover_allowed(inj, Now(1500 + d.cfg.narrator_min_gap_game, 10.0))   # game clock allows
        assert d._cover_allowed(inj, Now(1500, 10.0 + d.cfg.narrator_min_gap_real_s))  # real clock allows
    run(go())


# ---- step 6: pacing ----------------------------------------------------------------------
class GatedLLM(FakeLLM):
    """Idle kinds wait on a gate so a test can interrupt them mid-call."""
    def __init__(self):
        super().__init__()
        self.gate = asyncio.Event()
        self.started = asyncio.Event()
        self.cancelled = 0

    async def complete(self, messages, *, slot, kind="line", **kw):
        if kind in ("reflect", "diary", "chronicle"):
            self.started.set()
            try:
                await self.gate.wait()
            except asyncio.CancelledError:
                self.cancelled += 1
                raise
        return await super().complete(messages, slot=slot, kind=kind, **kw)


def gated(world):
    world.llm = GatedLLM()
    world.d.llm = world.llm
    return world


def test_idle_job_is_cancelled_the_moment_an_actor_window_is_wanted(world):
    async def go():
        w = gated(world)
        await w.go()
        mind = w.d.minds[1]
        mind.unreflected_importance = 9.0
        await w.d.step()
        await asyncio.wait_for(w.llm.started.wait(), 2)
        assert w.d.idle.active() and w.d.leases.leases and w.d.leases.windows_idle()
        w.fb.add_event("injury", [(2, "victim")], "Bram was shot.", [2, 3], 0.9)
        await w.d.step()                                                # the event is queued: idle yields now
        await asyncio.sleep(0)
        assert w.llm.cancelled == 1 and not w.d.idle.active()
        assert (1, "idle_preempted:reflect") in w.d.dropped
        await w.d.drain()
        assert mind.evolving_bio == "" and mind.unreflected_importance == 9.0   # nothing was applied
        assert w.fb.say_log                                              # the actor window spoke instead
        return w
    run(go())


def test_idle_work_never_starts_beside_live_work(world):
    async def go():
        w = gated(world)
        await w.go()
        d = w.d
        d.minds[1].unreflected_importance = 9.0
        inject(w, 2, "I mended a wall.", 100)
        w.fb.tick = int(0.9 * DAY)
        # a live conversation lease and a queued item: no idle job may start
        lease = d.leases.acquire(d.leases.make_item(3, "social", d.now(), cls=Cls.SOCIAL), d.now())
        await d.step()
        assert not d.idle.active() and not w.llm.calls
        d.leases.release(lease)
        d.leases.queue.append(d.leases.make_item(3, "injury", d.now()))
        assert not d.leases.narrator_may_do_idle_work()
        d.idle.run_once()
        assert not d.idle.active()
        d.leases.queue.clear()
        await d.step()
        await asyncio.wait_for(w.llm.started.wait(), 2)
        assert d.idle.active() and sum(1 for t in d.tasks if not t.done()) == 1   # one idle job at a time
        w.llm.gate.set()
        await d.drain()
    run(go())


def test_chronicle_job_yields_to_a_player_talk_request(world):
    async def go():
        w = gated(world)
        await w.go()
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1], 1.0)
        w.d.cfg.quotas.actor_per_day = 99
        # keep the actors quiet this step so only the narrator has work
        w.d.adapter.__class__  # noqa
        await w.d.ingest(await w.d.events.poll())
        w.d.leases.queue.clear()
        w.d.leases.narrator_busy = False
        w.d.idle.run_once()
        await asyncio.wait_for(w.llm.started.wait(), 2)
        assert w.d.idle.kind == "chronicle" and w.d.leases.narrator_busy
        w.fb.talk_queue.append({"id": "t1", "pawn": 1, "text": "Hello?", "from": "player"})
        await w.d.step()
        await asyncio.sleep(0)
        assert w.llm.cancelled == 1 and not w.d.leases.narrator_busy
        await w.d.drain()
        assert w.d.chronicle.state.pending                              # the notes wait for the next idle window
        assert any(b.get("talk_id") == "t1" for b in w.fb.say_log)
    run(go())


def test_chronicle_bridge_off_or_old_mod_is_harmless(world):
    async def go():
        w = await world.go()
        w.fb.chronicle_supported = False                                # an older mod: 404 on /chronicle
        w.llm.script["chronicle"] = ["Bram died of his wounds in the evening, and the others buried him without speeches."]
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1], 1.0)
        await step(w); await step(w)
        assert [x for x in w.d.idle.log if x["kind"] == "chronicle"] and not w.fb.chronicle_log
    run(go())


def test_chronicle_client_matches_the_contract_caps_and_errors(world):
    async def go():
        w = await world.go()
        r = await w.d.bridge.chronicle(3, "x" * 3000, "day")
        assert r["stored"] is True and r["count"] == 1 and len(w.fb.chronicle[0]["text"]) == 2000
        for i in range(130):
            await w.d.bridge.chronicle(4 + i, "t", "day")
        assert len(w.fb.chronicle) == 120
        with pytest.raises(BridgeError):
            await w.d.bridge.chronicle(-1, "t", "day")
    run(go())

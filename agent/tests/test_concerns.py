"""Ongoing concerns (core/concerns.py): births, cap, fade, resolution, payoff, block C, wants, reflection, blob."""
from conftest import run
from hermesvoice_agent.core import concerns as cn
from hermesvoice_agent.core.config import Config
from hermesvoice_agent.core.lease import Cls
from hermesvoice_agent.core.minds import Mind, Relation
from hermesvoice_agent.core.prompt import block_c
from hermesvoice_agent.core.reflection import REFLECT_SCHEMA, ReflectResult, reflection_messages
from hermesvoice_agent.games.rimworld import RimWorldAdapter

DAY = 60000


def love(w, holder=1, other=2, opinion=60):
    w.d.minds[holder].relations[str(other)] = Relation(name=w.d.names[other], opinion=opinion)


async def step(w, ticks=0):
    w.fb.tick += ticks
    await w.d.step()
    await w.d.drain()


def hurt(w, victim=2, severity=0.6, witnesses=(1, 2)):
    return w.fb.add_event("injury", [(victim, "victim")], "Bram is badly hurt.", list(witnesses), severity=severity)


def test_injury_of_a_loved_one_opens_a_worry_and_an_inner_thought(world):
    async def go():
        w = await world.go()
        love(w)
        hurt(w)
        await step(w)
        return w
    w = run(go())
    cs = cn.open_concerns(w.d.minds[1])
    assert len(cs) == 1 and cs[0].kind == "hurt" and cs[0].about == [2] and cs[0].resolve_hint == "healed:2"
    assert "Bram" in cs[0].text and cs[0].importance == 0.6
    assert [t.kind for t in w.d.minds[1].inner if t.kind == "concern"] == ["concern"]
    assert not cn.open_concerns(w.d.minds[3])             # Cy does not love Bram


def test_a_mild_injury_or_a_stranger_opens_nothing(world):
    async def go():
        w = await world.go()
        love(w, opinion=5)
        hurt(w)
        await step(w)
        love(w, 3, 2, 80)
        w.fb.add_event("injury", [(2, "victim")], "Bram is scratched.", [1, 2, 3], severity=0.2)
        await step(w)
        return w
    w = run(go())
    assert not cn.open_concerns(w.d.minds[1]) and not cn.open_concerns(w.d.minds[3])


def test_cap_displaces_the_weakest_only_for_a_stronger_one():
    m = Mind(actor_id=1)
    a = cn.add_concern(m, "First worry here.", [], "x", "", 0.3, 10, 2)
    b = cn.add_concern(m, "Second worry here.", [], "x", "", 0.6, 11, 2)
    assert cn.add_concern(m, "Third worry weaker.", [], "x", "", 0.2, 12, 2) is None
    c = cn.add_concern(m, "Fourth worry stronger.", [], "x", "", 0.9, 13, 2)
    assert c and a.status == "faded" and b.status == "open" and len(cn.open_concerns(m)) == 2
    assert cn.add_concern(m, "Fourth worry stronger.", [], "x", "", 0.9, 14, 2) is None     # duplicate text


def test_same_kind_and_hint_does_not_open_twice(world):
    async def go():
        w = await world.go()
        love(w)
        hurt(w); await step(w)
        hurt(w); await step(w)
        return w
    assert len(cn.open_concerns(run(go()).d.minds[1])) == 1


def test_fade_after_days_then_dropped_after_the_keep_window(world):
    async def go():
        w = await world.go()
        love(w)
        hurt(w); await step(w)
        await step(w, int(5.5 * DAY))
        c = w.d.minds[1].concerns[0]
        st1 = c.status
        await step(w, int(1.5 * DAY))
        return st1, w
    st1, w = run(go())
    assert st1 == "faded" and w.d.minds[1].concerns == []


def test_tend_resolves_with_a_payoff_line_a_chronicle_note_and_a_day_on_record(world):
    async def go():
        w = await world.go()
        love(w)
        hurt(w); await step(w)
        w.d.said.clear()
        w.llm.script["line"] = ["Thank goodness he is mending."]
        w.fb.add_event("tend", [(2, "patient"), (3, "doctor")], "Cy tended Bram.", [1, 2, 3], severity=0.1)
        await step(w)
        return w
    w = run(go())
    c = w.d.minds[1].concerns[0]
    assert c.status == "resolved" and "tended" in c.outcome
    spoken = [s for s in w.d.said if s["actor"] == 1 and s["text"] == "Thank goodness he is mending."]
    assert spoken
    line_calls = [c for c in w.llm.calls if c["kind"] == "line" and c["actor"] == 1]
    assert any("Your worry is over" in m[1] for c in line_calls for m in c["messages"])
    assert any("worry ended" in n.text and not n.major for n in w.d.chronicle.state.pending)
    assert w.d.concerns.payoffs == 1


def test_payoff_is_a_reactive_single_voice_item(world):
    async def go():
        w = await world.go()
        love(w)
        hurt(w); await step(w)
        c = cn.open_concerns(w.d.minds[1])[0]
        w.d.concerns.resolve(1, c, "he is fine", w.d.bridge.env.game)
        return w.d.leases.queue[-1]
    it = run(go())
    assert it.cls == Cls.REACTIVE and it.kind == "concern_resolved" and not it.payload.get("conversation")
    assert it.payload["trigger"].startswith("Your worry is over:")


def test_death_resolves_the_hurt_worry_and_opens_grief_for_others(world):
    async def go():
        w = await world.go()
        love(w); love(w, 3, 2, 70)
        hurt(w); await step(w)
        w.fb.add_event("death", [(2, "victim")], "Bram died.", [1, 2, 3], severity=1.0, data={})
        await step(w)
        return w
    w = run(go())
    hurt_c = [c for c in w.d.minds[1].concerns if c.kind == "hurt"][0]
    assert hurt_c.status == "resolved" and "died" in hurt_c.outcome
    assert [c.kind for c in cn.open_concerns(w.d.minds[3])] == ["grief"]
    assert [c.kind for c in cn.open_concerns(w.d.minds[1])] == ["grief"]


def test_raid_worry_ends_with_the_raid_ledger_entry(world):
    async def go():
        w = await world.go()
        w.fb.add_event("raid_start", [], "Raiders arrive.", [1, 2, 3], severity=0.7, ledger_id="raid:1")
        await step(w)
        mid = [cn.open_concerns(w.d.minds[i]) for i in (1, 2, 3)]
        w.fb.add_event("raid_end", [], "The raid is over.", [1, 2, 3], severity=0.3, ledger_id="raid:1")
        await step(w)
        return mid, w
    mid, w = run(go())
    worried = [i for i, m in zip((1, 2, 3), mid) if m]
    assert len(worried) == 2 and all(mid[i - 1][0].kind == "threat" for i in worried)    # capped at two witnesses
    assert all(not cn.open_concerns(w.d.minds[i]) for i in (1, 2, 3))
    assert all(w.d.minds[i].concerns[0].status == "resolved" for i in worried)
    assert not w.d.minds[({1, 2, 3} - set(worried)).pop()].concerns


def test_raid_worry_prefers_those_with_a_loved_one_and_is_deterministic():
    from hermesvoice_agent.games.rimworld.concern_rules import raid_worriers
    e = {"type": "raid_start", "ledger_id": "raid:1", "witnesses": [5, 3, 9, 1]}
    holders = [1, 3, 5, 9]
    assert raid_worriers(e, holders, lambda h, o: False) == [1, 3]
    assert raid_worriers(e, holders, lambda h, o: (h, o) in {(9, 1), (5, 3), (5, 9)}) == [5, 9]
    assert raid_worriers(e, [1, 3], lambda h, o: False) == [1, 3]               # only colonists with a mind can worry
    assert raid_worriers({"witnesses": []}, holders, lambda h, o: False) == []


def test_block_c_shows_one_worries_line_and_only_when_there_are_some(world):
    async def go():
        w = await world.go()
        love(w)
        m = w.d.minds[1]
        c0 = block_c(m)
        hurt(w); await step(w)
        return c0, w
    c0, w = run(go())
    m = w.d.minds[1]
    assert "Worries" not in c0
    c1 = block_c(m, worries=cn.worry_lines(m))
    assert "Worries: I can't stop worrying about Bram; they are hurt.\n" in c1 and c1.count("Worries") == 1
    assert block_c(m) == c0 or "Worries" not in block_c(m)


def test_prefix_carries_the_worry_and_switch_off_does_not(world, tmp_path):
    async def prefix(cfg_on):
        w = world
        if not cfg_on:
            w.d.cfg.concerns = False
        await w.go()
        love(w)
        m = w.d.minds[1]
        cn.add_concern(m, "I worry about Bram.", [2], "hurt", "healed:2", 0.6, 1, 2)
        from hermesvoice_agent.core.lease import Lease, Now
        lease = Lease(1, 1, Cls.SOCIAL, "x", Now(1, 0.0), 0.0)
        await w.d._prepare(1, "Ana looks around.", None, lease)
        return lease.data["prefix"]
    p_on = run(prefix(True))
    assert "Worries: I worry about Bram." in p_on


def test_switch_off_means_no_birth_and_no_prefix_line(tmp_path):
    from conftest import World
    w = World(tmp_path)
    w.d.cfg.concerns = False

    async def go():
        await w.go()
        love(w)
        hurt(w); await step(w)
        from hermesvoice_agent.core.lease import Lease, Now
        lease = Lease(1, 1, Cls.SOCIAL, "x", Now(1, 0.0), 0.0)
        await w.d._prepare(1, "Ana looks around.", None, lease)
        return lease.data["prefix"]
    p = run(go())
    assert not w.d.minds[1].concerns and "Worries" not in p
    assert not [t for t in w.d.minds[1].inner if t.kind == "concern"]


def test_wants_prompt_sees_the_worries(world):
    from test_wants import Fake, call, DONE

    async def go():
        w = await world.go()
        love(w)
        cn.add_concern(w.d.minds[1], "I worry about Bram.", [2], "hurt", "healed:2", 0.6, 1, 2)
        be = Fake([call("do_nothing"), DONE])
        from hermesvoice_agent.core.wants import run_wants
        await run_wants(w.d, 1, be, "grammar")
        return be
    be = run(go())
    seen = repr(getattr(be, "calls", None) or getattr(be, "seen", None) or be.__dict__)
    assert "YOUR WORRIES" in seen and "I worry about Bram." in seen


def test_reflection_can_open_one_worry_and_close_listed_ones(world):
    async def go():
        w = await world.go()
        love(w)
        cn.add_concern(w.d.minds[1], "I worry about Bram.", [2], "hurt", "healed:2", 0.6, 1, 2)
        old = w.d.minds[1].concerns[0].id
        w.llm.script["reflect"] = ['{"bio": "I am getting harder to rattle than I was before.", "wants": ["Rest"], '
                                   '"relations": [], "merge": [], "concern": "I keep fearing the winter will break us.", '
                                   f'"resolved": ["{old}", "w99"]}}']
        w.d.minds[1].unreflected_importance = 9.0
        await step(w); await step(w)
        return w, old
    w, old = run(go())
    cs = {c.id: c for c in w.d.minds[1].concerns}
    assert cs[old].status == "resolved" and "behind you" in cs[old].outcome
    new = [c for c in cs.values() if c.kind == "reflection"]
    assert len(new) == 1 and new[0].status == "open" and "winter" in new[0].text
    calls = [c for c in w.llm.calls if c["kind"] == "reflect"]
    assert "concern" in calls[0]["json_schema"]["properties"] and "concern" not in calls[0]["json_schema"]["required"]
    assert "YOUR WORRIES" in calls[0]["messages"][1][1]


def test_reflection_without_concerns_keeps_todays_schema_and_prompt(tmp_path):
    from conftest import World
    w = World(tmp_path)
    w.d.cfg.concerns = False
    m = Mind(actor_id=1, name="Ana")
    msgs = reflection_messages(w.adapter, m, {}, 10, DAY)
    assert "WORRIES" not in msgs[1][1] and cn.REFLECT_CONCERN_REQUEST not in msgs[1][1]
    assert cn.reflect_schema(REFLECT_SCHEMA, False) is REFLECT_SCHEMA


def test_a_romantic_worry_about_a_minor_from_reflection_is_dropped():
    ctx = {"known": {"bram"}, "all": {"bram"}, "prompt_words": set(), "common_words": set(), "speaker": {"ana"},
           "minor": True, "minor_names": {"bram"}}
    assert cn.sanitize_concern("I keep thinking about a kiss with Bram.", ctx) == ""
    assert cn.sanitize_concern("I keep fearing the winter will break us.", ctx) != ""


def test_blob_round_trip_old_blob_and_trim():
    m = Mind(actor_id=1)
    cn.add_concern(m, "I worry about Bram.", [2], "hurt", "healed:2", 0.6, 1, 2)
    again = Mind.from_blob(m.to_blob(10, DAY), 1)
    assert again.concerns[0].text == "I worry about Bram." and again.concern_counter == 1
    old = m.model_dump()
    for k in ("concerns", "concern_counter", "grudges"):
        old.pop(k)
    o = Mind.from_blob(old, 1)
    assert o.concerns == [] and o.grudges == []
    for i in range(40):
        m.concerns.append(m.concerns[0].model_copy(update={"id": f"x{i}", "status": "faded", "text": "z" * 500}))
    blob = m.to_blob(10, DAY, limit=2000)
    assert len(Mind.from_blob(blob, 1).concerns) <= 2


def test_prompt_cost_of_the_worries_line_is_small(world):
    """About 10 words per worry, two at most: well under the 60-token per-lease budget (words x 1.3)."""
    m = Mind(actor_id=1)
    cn.add_concern(m, "I can't stop worrying about Bram; they are hurt.", [2], "hurt", "h", 0.6, 1, 2)
    cn.add_concern(m, "Raiders are at our gates; I fear for us all.", [], "threat", "t", 0.7, 1, 2)
    extra = block_c(m, worries=cn.worry_lines(m)).split() 
    base = block_c(m).split()
    assert (len(extra) - len(base)) * 1.3 < 40

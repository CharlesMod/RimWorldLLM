"""Per-instance text filling (games/rimworld/textfill.py) and its idle wiring (core/idle.py)."""
import json

from conftest import World, run
from rimworldllm_agent.core import agegate
from rimworldllm_agent.core.lease import Cls
from rimworldllm_agent.games.rimworld import textfill as tf


def item(id="art:T1", kind="art", **kw):
    base = {"id": id, "kind": kind, "prio": 3, "fields": ["title", "text"] if kind == "art" else ["text"],
            "min_len": 20, "max_len": 200, "title_max": 30, "tokens": [], "about": "an artwork's description",
            "example": "A plain statue.", "example_title": "Statue",
            "context": {"maker": {"name": "Rook", "mood": "low (31%)", "traits": ["Pyromaniac"]}, "material": "marble",
                        "colony": {"weather": "rain", "recent": ["Raid by pirates"]}}}
    base.update(kw)
    return base


def reply(*objs):
    return json.dumps(list(objs))


GOOD = "Rook carved this in the rain, mostly to keep away from the kitchen."


async def world_with(tmp_path, items, script=None):
    w = await World(tmp_path).go()
    w.fb.text_supported = True
    w.fb.text_items = list(items)
    if script is not None:
        w.llm.script["textfill"] = script
    return w


# ---- batch / schema / messages -----------------------------------------------------------------------------
def test_batch_first_plus_same_kind_up_to_three():
    items = [item("a", "art"), item("b", "grave"), item("c", "art"), item("d", "art"), item("e", "art")]
    assert [i["id"] for i in tf.batch(items)] == ["a", "c", "d"]
    assert tf.batch([]) == []


def test_schema_ids_bounds_and_title():
    items = [item("a"), item("b", max_len=300)]
    s = tf.schema(items)
    assert s["type"] == "array" and s["minItems"] == 1 and s["maxItems"] == 2
    p = s["items"]["properties"]
    assert p["id"]["enum"] == ["a", "b"] and p["text"]["minLength"] == 20 and p["text"]["maxLength"] == 300
    assert p["title"]["maxLength"] == 30 and s["items"]["required"] == ["id", "text"]
    assert "title" not in tf.schema([item("g", "grave")])["items"]["properties"]


def test_messages_carry_context_example_and_minor_line():
    it = item("s1", "social", fields=["text"], tokens=["INITIATOR", "RECIPIENT"], context={"minor_involved": True, "nearby": ["Mira"]})
    msgs = tf.messages([it, item("art:x")])
    flat = msgs[1][1]
    assert msgs[0][0] == "system" and "[INITIATOR], [RECIPIENT]" in flat and "Mira" in flat
    assert "child or teenager" in flat and "A plain statue." in flat and "Pyromaniac" in flat and "low (31%)" in flat
    assert "none (no square brackets at all)" in flat and "{" not in flat.split("context:")[1].split("sample")[0]


# ---- clean -------------------------------------------------------------------------------------------------
def test_clean_strips_quotes_and_whitespace():
    assert tf.clean(item(), {"text": f'  "{GOOD}\n"  ', "title": " 'Smoke'  "}) == (GOOD, "Smoke")


def test_clean_rejects_length_title_brackets_markup():
    it = item()
    assert tf.clean(it, {"text": "too short", "title": "T"}) is None
    assert tf.clean(it, {"text": "x " * 150, "title": "T"}) is None
    assert tf.clean(it, {"text": GOOD}) is None                                 # title wanted
    assert tf.clean(it, {"text": GOOD, "title": "T" * 31}) is None
    assert tf.clean(it, {"text": GOOD + " [Rook]", "title": "T"}) is None       # no tokens allowed
    assert tf.clean(it, {"text": GOOD, "title": "A <b>"}) is None
    for bad in ("<b>bold</b> " + GOOD, GOOD + " {x}", GOOD + " (*note*)"):
        assert tf.clean(it, {"text": bad, "title": "T"}) is None
    assert tf.clean("nope", "x") is None and tf.clean(it, "x") is None


def test_clean_allows_only_the_items_tokens():
    it = item("s", "social", fields=["text"], tokens=["INITIATOR", "RECIPIENT"])
    ok = "[INITIATOR] told [RECIPIENT] the soup was fine, which was brave."
    assert tf.clean(it, {"text": ok}) == (ok, None)
    assert tf.clean(it, {"text": "[INITIATOR] told [OTHER] the soup was fine, truly."}) is None
    assert tf.clean(it, {"text": "[INITIATOR] told the soup was [fine] today."}) is None


def test_clean_drops_romance_for_social_and_minor_involved():
    assert agegate.romantic("They shared a passionate kiss and made love tonight.")
    rom = "[A] shared a passionate kiss and made love to [B] tonight."
    soc = item("s", "social", fields=["text"], tokens=["A", "B"])
    assert tf.clean(soc, {"text": rom}) is None
    grave_minor = item("g", "grave", fields=["text"], context={"minor_involved": True})
    assert tf.clean(grave_minor, {"text": "Here lies a lover who shared a passionate kiss and made love."}) is None
    grave = item("g2", "grave", fields=["text"])
    assert tf.clean(grave, {"text": "Here lies a lover who shared a passionate kiss and made love."}) is not None


# ---- fill_once ---------------------------------------------------------------------------------------------
def test_fill_once_posts_cleaned_text_with_op_ids(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a"), item("b", "grave"), item("c")],
                             [reply({"id": "a", "text": f'"{GOOD}"', "title": "Smoke"}, {"id": "c", "text": GOOD, "title": "Ash"})])
        n = await tf.fill_once(w.d, 1)
        assert n == 2
        log = w.fb.text_fill_log
        assert [b["id"] for b in log] == ["a", "c"] and log[0]["text"] == GOOD and log[0]["title"] == "Smoke"
        assert all(b["op_id"] for b in log) and log[0]["op_id"] != log[1]["op_id"]
        assert [i["id"] for i in w.fb.text_items] == ["b"]
        call = w.llm.calls[-1]
        assert call["kind"] == "textfill" and call["slot"] == 1 and call["json_schema"]["maxItems"] == 2
        assert w.d.text_queue_live and w.d.text_fill_wait_s == tf.WAIT_OK_S
    run(go())


def test_fill_once_bad_json_and_bad_items_post_nothing(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a")], ["not json at all"])
        assert await tf.fill_once(w.d, 1) == 0 and not w.fb.text_fill_log
        w.llm.script["textfill"] = [reply({"id": "a", "text": "short"})]
        assert await tf.fill_once(w.d, 1) == 0                       # "a" was tried once: not asked again
        assert len(w.llm.calls) == 1 and w.d.text_fill_wait_s == tf.WAIT_EMPTY_S
    run(go())


def test_fill_once_404_backs_off_and_empty_queue(tmp_path):
    async def go():
        w = await world_with(tmp_path, [])
        assert await tf.fill_once(w.d, 1) == 0 and w.d.text_queue_live and w.d.text_fill_wait_s == tf.WAIT_EMPTY_S
        w.fb.text_supported = False
        assert await tf.fill_once(w.d, 1) == 0
        assert not w.d.text_queue_live and w.d.text_fill_wait_s == tf.WAIT_UNSUPPORTED_S and not w.llm.calls
    run(go())


def test_fill_once_unknown_id_does_not_crash(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a"), item("c")],
                             [reply({"id": "a", "text": GOOD, "title": "T"}, {"id": "zzz", "text": GOOD}, {"id": "c", "text": GOOD, "title": "U"})])
        # now a real race: the queue still lists a, but the mod no longer holds it
        w.fb.text_items = [item("a"), item("c")]
        real = w.bridge.text_queue

        async def stale(n=4):
            r = await real(n)
            w.fb.text_items = [i for i in w.fb.text_items if i["id"] != "a"]
            return r
        w.bridge.text_queue = stale
        assert await tf.fill_once(w.d, 1) == 1
        assert [b["id"] for b in w.fb.text_fill_log] == ["c"]
    run(go())


# ---- art skip and idle wiring -----------------------------------------------------------------------------------
def art_event(w):
    return {"seq": 1, "game": w.d.bridge.env.game, "type": "art_created", "severity": 0.3,
            "actors": [{"id": 1, "role": "maker"}], "data": {"thing_id": 500, "def_label": "statue", "quality": "good"}}


def test_art_idle_job_skipped_when_text_queue_live(tmp_path):
    async def go():
        w = await World(tmp_path).go()
        w.d.art.note(art_event(w), w.d.bridge.env.game)
        assert w.d.idle.anything_due() is True
        w.d.text_queue_live = True
        assert w.d.idle._start_art() is False and w.d.art.jobs
        assert w.d.idle.anything_due() is False
        w.d.text_queue_live = False
        assert w.d.idle._start_art() is True
        w.d.idle.preempt()
    run(go())


async def settle(w):
    for _ in range(20):
        await w.d.idle.task if w.d.idle.task else None
        import asyncio
        await asyncio.sleep(0)


def test_idle_runs_text_fill_last_when_idle(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a")], [reply({"id": "a", "text": GOOD, "title": "Smoke"})])
        w.d.idle.run_once()
        assert w.d.idle.kind == "textfill" and w.d.idle.lease.actor_id == -2 and w.d.idle.lease.cls == Cls.IDLE
        await settle(w)
        assert [b["id"] for b in w.fb.text_fill_log] == ["a"]
        assert w.d.idle.text_next >= w.d.clock() + 10
        w.d.idle.run_once()                                    # paced: not again within the gap
        assert not w.d.idle.active()
    run(go())


def test_idle_text_fill_yields_to_other_jobs_and_to_real_work(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a")], [reply({"id": "a", "text": GOOD, "title": "Smoke"})])
        w.d.art.note(art_event(w), w.d.bridge.env.game)
        w.d.text_queue_live = False
        w.d.idle.run_once()
        assert w.d.idle.kind == "art"                          # an earlier idle job goes first
        w.d.idle.preempt()
        w.d.idle.text_next = 0.0
        w.d.idle.run_once()
        item_ = w.d.leases.make_item(1, "reaction", w.d.now(), cls=Cls.REACTIVE)
        w.d.leases.submit(item_, w.d.now())
        w.d.idle.preempt_if_wanted()
        assert not w.d.idle.active() and not w.fb.text_fill_log
        w.d.idle.text_next = 0.0
        w.d.idle.run_once()                                    # real work is queued: nothing starts
        assert not w.d.idle.active()
    run(go())


def test_idle_text_fill_honours_both_flags(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a")])
        w.d.cfg.text_fill = False
        w.d.idle.run_once()
        assert not w.d.idle.active()
        w.d.cfg.text_fill = True
        w.d.bridge_cfg["text_fill"] = False
        w.d.idle.run_once()
        assert not w.d.idle.active() and not w.llm.calls
        w.d.bridge_cfg.pop("text_fill")
        w.d.idle.run_once()
        assert w.d.idle.kind == "textfill"
        w.d.idle.preempt()
    run(go())


def test_idle_text_fill_404_backs_off_five_minutes(tmp_path):
    async def go():
        w = await world_with(tmp_path, [item("a")])
        w.fb.text_supported = False
        w.d.idle.run_once()
        await settle(w)
        assert w.d.idle.text_next >= w.d.clock() + 300 and not w.llm.calls
    run(go())

"""Words for the game's own social interactions: the line prompt says what the interaction was, no effect is added."""
from conftest import run
from rimworldllm_agent.core.lease import Cls


def vanilla(w, interaction="Insult", label="insult", text="Ana insulted Bram's cooking.", tier="high", ref="v1-1"):
    return w.fb.add_event("social", [(1, "initiator"), (2, "recipient")], f"Ana -> Bram: {label}.", [1, 2], 0.5,
                          data={"interaction": interaction, "interaction_label": label, "vanilla": True, "ref": ref,
                                "tier": tier, "vanilla_text": text})


def test_insult_line_prompt_says_insult_and_subject_and_carries_ref(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Your stew is slop, Bram."]
        vanilla(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert len(w.fb.say_log) == 1
    s = w.fb.say_log[0]
    assert s["pawn"] == 1 and s["listener"] == 2 and s["vanilla_ref"] == "v1-1" and s["intent"] == "none"
    line_calls = [c for c in w.llm.calls if c.get("kind") == "line"]
    assert len(line_calls) == 1
    user = line_calls[0]["messages"][-1][1]
    assert "an insult" in user and "Ana insulted Bram's cooking." in user
    assert not [c for c in w.llm.calls if c.get("kind") == "intent"]      # no intent pass, so no second effect


def test_prompt_fits_each_kind(world):
    ad = world.adapter
    for key, word in [("KindWords", "kind words"), ("Slight", "slights"), ("DeepTalk", "personal"), ("Chitchat", "passing exchange")]:
        e = ad.normalize_event(vanilla(world, key, key.lower()))
        info = ad.vanilla_social(e)
        assert word in ad.vanilla_request(info, "Ana", "Bram")


def test_unknown_interaction_uses_its_label(world):
    e = world.adapter.normalize_event(vanilla(world, "ModdedThing", "stare awkwardly"))
    info = world.adapter.vanilla_social(e)
    assert "stare awkwardly" in world.adapter.vanilla_request(info, "Ana", "Bram")


def test_plain_social_event_is_not_vanilla(world):
    e = world.adapter.normalize_event(world.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "x", [1, 2], 0.5,
                                                         data={"interaction": "Insult"}))
    assert world.adapter.vanilla_social(e) is None


def test_tier_sets_priority_chitchat_is_ambient(world):
    async def go():
        w = await world.go()
        seen = []
        orig = w.d.leases.submit
        w.d.leases.submit = lambda item, now, speed=1: (seen.append((item.kind, item.cls, item.payload.get("vanilla", {}).get("ref"))), orig(item, now, speed))[1]
        vanilla(w, "Insult", "insult", ref="a")
        vanilla(w, "Chitchat", "chitchat", text="Ana chatted with Bram.", tier="idle", ref="b")
        await w.d.step()
        return seen
    seen = run(go())
    assert ("vanilla_social", Cls.SOCIAL, "a") in seen
    assert ("vanilla_social", Cls.AMBIENT, "b") in seen


def test_dropped_line_says_nothing(world):
    async def go():
        w = await world.go()
        w.llm.script["line"] = [""]          # sanitised away
        vanilla(w)
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert w.fb.say_log == []


def test_chitchat_request_describes_the_situation_not_the_name(world):
    ad = world.adapter
    info = ad.vanilla_social(ad.normalize_event(vanilla(world, "Chitchat", "chitchat", text="Ana chatted with Bram.", tier="idle")))
    req = ad.vanilla_request(info, "Ana", "Bram").lower()
    assert "small talk" not in req and "chitchat" not in req and "small-talk" not in req
    assert "whatever is around" in req
    assert "don't remark on the conversation or the act of talking itself" in req


def test_the_no_meta_rule_is_in_the_speech_thought_diary_and_reaction_prompts(world):
    ad = world.adapter
    rule = "Don't remark on the conversation or the act of talking itself; react to the people and what's happening."
    for text in (ad.block_a, ad.thought_request, ad.diary_request, ad.reaction_request):
        assert rule in text


def test_chitchat_memory_text_does_not_name_the_interaction(world):
    ad = world.adapter
    e = ad.normalize_event(world.fb.add_event("social", [(1, "initiator"), (2, "recipient")], "Ana -> Bram: Chitchat.", [1, 2], 0.1,
                                              data={"interaction": "Chitchat", "interaction_label": "Chitchat"}))
    t = ad.first_person(e, 1)
    assert "chitchat" not in t.lower() and "Ana and Bram" in t
    assert "the work, the food, the weather" in t           # what it was about, so the thought has a subject other than the talking


def test_drops_meta_only_with_a_chitchat_scene(world):
    ad = world.adapter
    chat_ctx = ["I was part of it: Ana and Bram had a passing exchange."]
    for text in ("I wish people would cut this chitchat.", "Enough small talk already.", "All this talk, talk, talk.", "Why does everyone chat so much? Enough chatter."):
        assert ad.drops_meta(text, chat_ctx), text
        assert not ad.drops_meta(text, ["I was part of it: Ana -> Bram: Insult."]), text      # an insult scene keeps it
        assert not ad.drops_meta(text, []), text
    assert not ad.drops_meta("The stew smells burnt again.", chat_ctx)


def test_a_meta_thought_is_dropped_and_counted(world):
    from conftest import run
    from rimworldllm_agent.core.minds import Memory
    async def go():
        w = await world.go()
        w.d.cfg.thought_enabled = True
        w.d.minds[1].memories.append(Memory(id=w.d.minds[1].next_mem_id(), game=1000, people=[2],
                                            text="I was part of it: Ana and Bram had a passing exchange."))
        w.llm.script["thought"] = ["I wish people would cut this chitchat."]
        await w.d.step(); await w.d.drain()
        return w
    w = run(go())
    assert not [t for t in w.fb.thought_log if t["kind"] == "thought"]
    assert any(d[1] == "thought:meta_talk" for d in w.d.dropped)


def _one_vanilla(world, interaction, label, tier, ref, text):
    from conftest import run
    async def go():
        w = await world.go()
        w.llm.script["line"] = ["Enough chitchat, Bram."]
        vanilla(w, interaction, label, text=text, tier=tier, ref=ref)
        await w.d.step(); await w.d.drain()
        return w
    return run(go())


def test_a_meta_vanilla_line_is_dropped_for_chitchat(world):
    w = _one_vanilla(world, "Chitchat", "chitchat", "idle", "c1", "Ana chatted with Bram.")
    assert w.fb.say_log == []
    assert any(d[1] == "meta_talk" for d in w.d.dropped)


def test_the_same_words_are_kept_for_an_insult(world):
    w = _one_vanilla(world, "Insult", "insult", "high", "i1", "Ana insulted Bram's cooking.")
    assert [s["vanilla_ref"] for s in w.fb.say_log] == ["i1"]

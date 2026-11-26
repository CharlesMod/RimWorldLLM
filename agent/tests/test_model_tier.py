"""Model tier switch: small/large from the serving model; clouds and art may be off on small."""
import httpx
import pytest

from conftest import run
from hermesvoice_agent.core.config import Config, LLMConfig
from hermesvoice_agent.core.llm import HttpLLM
from hermesvoice_agent.core.model_tier import ModelTier, classify
from hermesvoice_agent.core.passes import Spoken

PATS = ("e2b", "e4b")
AIM = "I wish somebody would notice how tired I am"


@pytest.mark.parametrize("model,staging,want", [
    ("gemma4-e4b-rp-q40", "full", "small"),
    ("Gemma-4-E2B-it", "", "small"),
    ("gemma4-26b-a4b-q4", "full", "large"),
    ("gemma4-rp-maxxed-q3kxl", "full", "large"),
    ("", "", "large"),
    ("gemma4-rp-maxxed-q3kxl", "starter", "small"),
])
def test_classify(model, staging, want):
    assert classify(model, PATS, staging) == want


def engine(model, seen, staging="full", ok=True):
    def h(req):
        seen.append(str(req.url))
        if req.url.path.endswith("/engine/v1/status"):
            if not ok:
                return httpx.Response(500)
            return httpx.Response(200, json={"phase": "ready", "sampler": {}, "model": model, "model_tier": staging})
        return httpx.Response(404)
    return httpx.AsyncClient(transport=httpx.MockTransport(h))


def make(model, seen, **kw):
    cfg = Config(llm=LLMConfig(base_url="http://h"))
    now = [0.0]
    return cfg, ModelTier(cfg, lambda: now[0]), HttpLLM(cfg.llm, client=engine(model, seen, **kw)), now


def test_engine_read_and_cache():
    seen = []
    cfg, t, llm, now = make("gemma4-e4b-rp-q40", seen)
    assert not t.small                      # unknown until read
    run(t.refresh(llm))
    assert t.small and t.model == "gemma4-e4b-rp-q40"
    n = len(seen)
    run(t.refresh(llm)); now[0] = 59
    run(t.refresh(llm))
    assert len(seen) == n                   # cached for 60 s
    now[0] = 61
    assert t.due()
    run(t.refresh(llm))
    assert len(seen) == n + 1


def test_engine_large_starter_and_failure_keeps_last():
    seen = []
    cfg, t, llm, now = make("gemma4-rp-maxxed-q3kxl", seen)
    run(t.refresh(llm)); assert not t.small
    seen2 = []
    cfg, t, llm, now = make("gemma4-rp-maxxed-q3kxl", seen2, staging="starter")
    run(t.refresh(llm)); assert t.small
    cfg, t, llm, now = make("gemma4-e4b", [], ok=False)
    run(t.refresh(llm)); assert not t.small     # no answer = large


def test_plain_endpoint_uses_cfg_model():
    def h(req):
        return httpx.Response(404)
    cfg = Config(llm=LLMConfig(base_url="http://h", model="gemma-4-E4B-it", engine=False))
    t = ModelTier(cfg, lambda: 0.0)
    run(t.refresh(HttpLLM(cfg.llm, client=httpx.AsyncClient(transport=httpx.MockTransport(h)))))
    assert t.small


def cloud_world(world, model, **cfgset):
    async def go():
        w = await world.go()
        w.cfg.llm.model = model
        w.d.cfg.inner_cloud_chance = 1.0
        for k, v in cfgset.items():
            setattr(w.d.cfg, k, v)
        return w
    return run(go())


def say(w, aid=1):
    run(w.d._inner_after_say(aid, Spoken("Hello.", "none", "normal", AIM, False), False))


def clouds(w):
    return [t for t in w.fb.thought_log if t.get("kind", "thought") == "thought"]


def test_clouds_off_on_small_when_field_false_aim_still_stored(world):
    w = cloud_world(world, "gemma4-e4b-rp", inner_cloud_small=False)
    say(w)
    assert not clouds(w) and len(w.d.minds[1].inner) == 1


def test_clouds_on_small_by_default(world):
    w = cloud_world(world, "gemma4-e4b-rp")
    say(w)
    assert len(clouds(w)) == 1


def test_clouds_on_for_large_when_small_field_false(world):
    w = cloud_world(world, "gemma4-26b", inner_cloud_small=False)
    say(w)
    assert len(clouds(w)) == 1


def test_master_switch_still_wins(world):
    w = cloud_world(world, "gemma4-26b", inner_cloud=False)
    say(w)
    assert not clouds(w) and w.d.minds[1].inner


def art_event(w):
    w.fb.add_event("art_created", [(1, "maker")], "Ana finished a sculpture.", [1], 0.2,
                   data={"thing_id": 500, "def_label": "sculpture", "quality": "good", "title": "Dog", "description": "d"})


def test_art_off_on_small_when_field_false(world):
    w = cloud_world(world, "gemma4-e2b", art_small=False)
    art_event(w)
    run(w.d.step()); run(w.d.drain())
    assert not w.d.art.jobs and not [c for c in w.llm.calls if c["kind"] == "art"]


def test_art_on_for_small_when_art_small_is_set(world):
    w = cloud_world(world, "gemma4-e4b", art_small=True)
    art_event(w)
    run(w.d.step()); run(w.d.drain())
    assert w.d.art.jobs or w.fb.art_log or [c for c in w.llm.calls if c["kind"] == "art"]


def test_art_on_for_large_when_field_false(world):
    w = cloud_world(world, "gemma4-26b", art_small=False)
    art_event(w)
    run(w.d.step()); run(w.d.drain())
    assert w.d.art.jobs or w.fb.art_log or [c for c in w.llm.calls if c["kind"] == "art"]


def test_art_on_for_small_by_default(world):
    w = cloud_world(world, "gemma4-e4b-rp-q40")
    art_event(w)
    run(w.d.step()); run(w.d.drain())
    assert w.d.art.jobs or w.fb.art_log      # keeper 2026-11-26: art on for E4B by default

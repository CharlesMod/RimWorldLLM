"""The Adult content setting (core/adult.py): default off, live from /config, never relaxes the age gate."""
import pytest

from conftest import run
from hermesvoice_agent.core import adult
from hermesvoice_agent.core.minds import Mind
from hermesvoice_agent.core.prompt import Scene, build_prefix
from hermesvoice_agent.core.sanitize import sanitize_line, sanitize_prose
from hermesvoice_agent.games.rimworld import RimWorldAdapter
from test_prompt import mk_mind

KW = dict(speaker_names={"Ana"}, known_names={"Bram"}, all_names={"Ana", "Bram"}, prompt_words=set())
EXPLICIT = "Bram, I want to have sex with you tonight."
ROMANCE = ["I kiss you softly, Bram.", "I love you, Bram.", "Bram, shall we go on a date tomorrow?", "Our romance is sweet."]


def line(raw, **kw):
    return sanitize_line(raw, **{**KW, **kw})


def test_default_off():
    assert adult.enabled is False and adult.off()


def test_explicit_line_dropped_when_off_kept_when_on():
    r = line(EXPLICIT)
    assert r.text is None and r.reason == "adult_off"
    adult.set_enabled(True)
    assert line(EXPLICIT).text == EXPLICIT


@pytest.mark.parametrize("raw", ROMANCE)
def test_romance_passes_when_off_for_adults(raw):
    assert line(raw).text == raw


def test_prose_paths_drop_explicit_when_off():
    kw = dict(known_names={"Bram"}, all_names={"Ana", "Bram"}, prompt_words=set(), speaker_names={"Ana"}, min_words=3)
    assert sanitize_prose("Ana had sex with Bram last night in the barn.", **kw).reason == "adult_off"
    adult.set_enabled(True)
    assert sanitize_prose("Ana had sex with Bram last night in the barn.", **kw).ok


@pytest.mark.parametrize("on", [False, True])
def test_age_gate_still_blocks_minors_in_both_modes(on):
    adult.set_enabled(on)
    for raw in [ROMANCE[0], ROMANCE[1], EXPLICIT]:
        r = line(raw, involves_minor=True)
        assert r.text is None and r.reason == "minor_safety"
    assert line(ROMANCE[0], minor_names={"Bram"}).reason == "minor_safety"


def test_prompt_rule_only_when_off():
    ad = RimWorldAdapter()
    m = mk_mind(1, "Ana")
    assert adult.RULE in build_prefix(ad.block_a, m, [], [], Scene(trigger="x"))
    adult.set_enabled(True)
    assert adult.RULE not in build_prefix(ad.block_a, m, [], [], Scene(trigger="x"))


def test_live_toggle_from_bridge_config_and_stats(world):
    run(world.go())
    d = world.d
    assert adult.off()
    world.fb.config["adult_content"] = True
    run(d.refresh_config())
    assert adult.enabled and line(EXPLICIT).ok
    world.fb.config["adult_content"] = False
    run(d.refresh_config())
    assert adult.off() and line(EXPLICIT).reason == "adult_off"
    d.dropped.append((1, "diary:adult_off"))
    d.dropped.append((1, "art:adult_off"))
    d.dropped.append((1, "diary:too_short"))
    assert d.adult_off_drops() == 2 and d.gate_stats()["adult_off_dropped"] == 2


def test_missing_key_means_off(world):
    run(world.go())
    world.fb.config.pop("adult_content", None)
    adult.set_enabled(True)
    run(world.d.refresh_config())
    assert adult.off()

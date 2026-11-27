"""Unattended play: the steward wakes on a `dialog` event even while the game is paused and answers with the choose tool."""
import json
from pathlib import Path

import pytest

from conftest import run
from rimworldllm_agent.games.rimworld import dialog_try
from rimworldllm_agent.games.rimworld.steward_digest import render_digest
from test_steward import Fake, boot, call, cycle, final

DATA = Path(__file__).parent / "data"


def sample(name):
    return json.loads((DATA / name).read_text())["dialogs"]


def open_dialog(w, dlg, paused=True):
    w.fb.dialogs.append(dlg)
    w.fb.paused = paused
    w.fb.add_event("dialog", [], "Needs an answer: " + dlg["title"], [], severity=0.6,
                   data={"dialog": dlg["id"], "kind": dlg["kind"], "title": dlg["title"], "options": [o["label"] for o in dlg["options"]],
                         "default": dlg["default"], "timeout_s": 90})


def test_dialog_wakes_while_the_game_is_paused(world):
    async def go():
        w, st, fake = await boot(world, [final("hello")])
        await cycle(w)                                   # the opening heartbeat
        fake.calls.clear()
        fake.script[:] = [call("choose", dialog="L4021", option="Accept the refugee", why="since we have room"), final("Welcome.")]
        w.real += 30
        open_dialog(w, sample("dialogs_refugee.json")[0])
        await cycle(w)
        assert st.stats.by_reason.get("dialog") == 1, st.stats.by_reason
        user = fake.calls[0]["messages"][1]["content"]
        assert "OPEN DIALOGS" in user and "L4021" in user and "Accept the refugee" in user
        assert w.fb.dialogs == []                        # answered through POST /act/choose
        assert w.fb.act_log[-1][0] == "choose" and w.fb.act_log[-1][1]["option"] == 0 and w.fb.act_log[-1][1]["why"] == "since we have room"
    run(go())


def test_a_paused_game_without_a_dialog_still_sleeps(world):
    async def go():
        w, st, fake = await boot(world)
        await cycle(w)
        w.fb.paused = True
        w.real += 60
        await cycle(w)
        assert st.stats.cycles == 1
    run(go())


def test_dialog_wake_waits_for_the_real_floor_and_is_not_lost(world):
    async def go():
        w, st, fake = await boot(world, [final("ok")])
        await cycle(w)
        open_dialog(w, sample("dialogs_ransom.json")[0])     # no real time since the last cycle
        await cycle(w)
        assert st.stats.cycles == 1 and st.dialog_pending
        w.real += 30
        await cycle(w)
        assert st.stats.by_reason.get("dialog") == 1 and not st.dialog_pending
    run(go())


def test_not_playing_ignores_dialogs(world):
    async def go():
        w, st, fake = await boot(world, playing=False)
        open_dialog(w, sample("dialogs_refugee.json")[0])
        w.real += 60
        await cycle(w)
        assert fake.calls == [] and not st.dialog_pending
    run(go())


def test_choose_tool_shapes(world):
    async def go():
        w, st, fake = await boot(world)
        for f in ("dialogs_refugee.json", "dialogs_naming.json"):
            w.fb.dialogs.extend(sample(f))
        digest = await w.bridge.digest()
        from rimworldllm_agent.games.rimworld.steward_tools import Session
        s = Session(digest, w.bridge, False)
        assert "choose" in {t.name for t in s.tools} and "choose" in s.acting
        assert s._verb_body("choose", {"dialog": "L4021", "option": "#1"}) == ("choose", {"dialog": "L4021", "option": 1})
        assert s._verb_body("choose", {"dialog": "L4021", "option": "reject"})[1]["option"] == 1
        with pytest.raises(ValueError):
            s._verb_body("choose", {"dialog": "L4021", "option": "pay"})
        with pytest.raises(ValueError):
            s._verb_body("choose", {"dialog": "L4021"})
        _, b = s._verb_body("choose", {"dialog": "W3", "text": "The Lanterns | Ashford"})
        assert b["text"] == {"faction": "The Lanterns", "settlement": "Ashford"}
        with pytest.raises(ValueError):
            s._verb_body("choose", {"dialog": "W3", "text": "One name"})
        assert "choose" not in {t.name for t in Session(digest, w.bridge, True).tools}      # advisor mode never answers dialogs
        assert "choose" not in {t.name for t in Session({}, w.bridge, False).tools}
    run(go())


def test_digest_text_lists_dialogs():
    d = {"dialogs": sample("dialogs_ransom.json") + sample("dialogs_naming.json")}
    text = render_digest(d)
    assert "OPEN DIALOGS" in text and "L4107" in text and "costs silver" in text and "type a name" in text and "max 40 chars" in text
    naming = [l for l in text.splitlines() if "(naming)" in l][0]
    assert "default:" not in naming and "invent your own" in naming   # a shown fallback name got copied in the live check
    assert "default:" in [l for l in text.splitlines() if "L4107" in l][0]


def test_dialog_try_runs_one_cycle_on_samples(tmp_path, monkeypatch, capsys):
    from rimworldllm_agent.games.rimworld import steward_try
    from rimworldllm_agent.core.toolloop import Reply
    from conftest import run as _run

    script = [json.dumps({"tool": "choose", "args": {"dialog": "W3", "text": "The Lanterns | Ashford", "why": "since the colony needs names"}}),
              json.dumps({"final": "Named."})]

    class Backend:
        async def chat(self, messages, **kw):
            return Reply(text=script.pop(0) if len(script) > 1 else script[0])

        async def aclose(self):
            pass

    monkeypatch.setattr(steward_try, "make_tool_backend", lambda cfg: Backend())
    rc = dialog_try.main(["--dialogs", str(DATA / "dialogs_naming.json"), "--paused", "--llm-url", "http://x/v1", "--mode", "grammar"])
    out = capsys.readouterr().out
    assert rc == 0
    assert "CYCLE: dialog" in out and "TOOL choose" in out and "DIALOGS STILL OPEN: none" in out
    assert "OPEN DIALOGS" in out


def test_growth_render_and_tool():
    d = {"dialogs": sample("dialogs_growth.json")}
    line = [l for l in render_digest(d).splitlines() if "L5120" in l][0]
    assert "child: Mia, 7" in line and "skills: Shooting 2*" in line
    assert "pick 2 passion(s) of: Shooting, Melee, Artistic" in line
    assert "Bookworm (Reads for fun; learns faster from books.)" in line and "Tough (Takes 25% less damage.)" in line
    assert "options: 0" not in line
    from rimworldllm_agent.games.rimworld.steward_tools import Session
    s = Session(d, None, False)
    spec = next(t for t in s.tools if t.name == "choose")
    assert {"passions", "trait"} <= {p.name for p in spec.params}
    assert s._verb_body("choose", {"dialog": "L5120", "passions": "Shooting, melee", "trait": "Tough"}) == \
        ("choose", {"dialog": "L5120", "passions": ["Shooting", "melee"], "trait": "Tough"})
    with pytest.raises(ValueError):
        s._verb_body("choose", {"dialog": "L5120", "trait": "Tough"})
    with pytest.raises(ValueError):
        s._verb_body("choose", {"dialog": "L5120", "passions": "Melee"})
    assert s._verb_body("choose", {"dialog": "L5120", "option": "0"}) == ("choose", {"dialog": "L5120", "option": 0})   # first choices still allowed


def test_growth_prompt_line():
    from rimworldllm_agent.games.rimworld import steward_tools
    txt = (Path(steward_tools.__file__).parent / "steward_system.txt").read_text()
    assert "growth moment" in txt and "personality" in txt

"""Try the steward on recorded open dialogs: one cycle against a fake bridge, with the real tool backend.

    python -m rimworldllm_agent.games.rimworld.dialog_try --dialogs tests/data/dialogs_refugee.json \\
        [--digest tests/data/steward_digest_sample.json] [--paused] --llm-url http://127.0.0.1:8080/v1 [--model m]

--dialogs is a recorded GET /dialogs body (or a bare list); give it more than once to open several at the same time.
The steward is playing and a `dialog` event arrives, with the game paused when --paused is given (modal windows pause the
game). Prints the user turn, each tool call with its result, the chat lines and which dialogs are still open. Nothing here
touches a real game; the lead runs it against a real model (--llm-url) on a GPU box.
"""
from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path

from . import steward_try

_DEFAULT_DIGEST = Path(__file__).resolve().parents[3] / "tests" / "data" / "steward_digest_sample.json"


def load_dialogs(paths: list[str]) -> list[dict]:
    out: list[dict] = []
    for p in paths:
        j = json.loads(Path(p).read_text())
        out.extend(j.get("dialogs", []) if isinstance(j, dict) else j)
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="dialog_try", description=__doc__.split("\n")[0])
    ap.add_argument("--dialogs", action="append", required=True, help="a recorded GET /dialogs body (JSON file); repeatable")
    ap.add_argument("--digest", default=str(_DEFAULT_DIGEST), help="a recorded GET /digest body")
    ap.add_argument("--paused", action="store_true", help="the game is paused (a modal window is open)")
    ap.add_argument("--llm-url", required=True, help="OpenAI-compatible base url")
    ap.add_argument("--model", default="")
    ap.add_argument("--mode", default="auto", choices=["auto", "grammar", "json", "native"])
    a = ap.parse_args(argv)
    dialogs = load_dialogs(a.dialogs)
    if not dialogs:
        ap.error("no dialogs in the given files")
    digest = json.loads(Path(a.digest).read_text())
    out = asyncio.run(steward_try.try_once(digest, None, "", True, a.llm_url, a.model, a.mode, dialogs=dialogs, paused=a.paused))
    return steward_try.report(out)


if __name__ == "__main__":
    raise SystemExit(main())

"""Regression tests for sanitizer faults found by the model bench (docs/MODEL-BENCH.md F1, F2; fixed). No GPU."""
import sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3] / "agent"))
from hermesvoice_agent.core.sanitize import sanitize_line

KW = dict(speaker_names={"Pip"}, known_names={"Moth", "Pip", "Hana"}, all_names={"Moth", "Pip", "Hana", "Rook"}, prompt_words={"Moth"})


def test_possessive_of_known_name_kept():
    assert sanitize_line("Okay so Moth's panicking and I don't know what to do.", **KW).ok


def test_sentence_initial_it_kept():
    assert sanitize_line("It is good meat, Pip.", **KW).ok

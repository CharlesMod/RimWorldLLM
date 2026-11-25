"""Known faults in the agent's sanitizer found by the model bench. xfail(strict) so they turn red the day they are fixed
and the marker can be removed. No GPU."""
import sys, pathlib
import pytest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3] / "agent"))
from hermesvoice_agent.core.sanitize import sanitize_line

KW = dict(speaker_names={"Pip"}, known_names={"Moth", "Pip", "Hana"}, all_names={"Moth", "Pip", "Hana", "Rook"}, prompt_words={"Moth"})


@pytest.mark.xfail(strict=True, reason="possessive of a known name ('Moth's') is dropped as unknown_name")
def test_possessive_of_known_name_kept():
    assert sanitize_line("Okay so Moth's panicking and I don't know what to do.", **KW).ok


@pytest.mark.xfail(strict=True, reason="'It' is missing from _COMMON, so 'It is good meat.' is dropped as unknown_name:It")
def test_sentence_initial_it_kept():
    assert sanitize_line("It is good meat, Pip.", **KW).ok

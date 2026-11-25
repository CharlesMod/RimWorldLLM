import pytest

from hermesvoice_agent.sanitize import sanitize_line, name_tokens

KW = dict(speaker_names={"Ana"}, known_names={"Bram"}, all_names={"Ana", "Bram", "Cy"},
          prompt_words={"Kitchen"})


def s(raw, **kw):
    return sanitize_line(raw, **{**KW, **kw})


@pytest.mark.parametrize("raw,expect", [
    ("Hold still, Bram.", "Hold still, Bram."),
    ('"Hold still."', "Hold still."),
    ("*sighs* Hold still.", "Hold still."),
    ("Hold still. (she frowns)", "Hold still."),
    ("Ana: Hold still.", "Hold still."),
    ("<|channel>thought\n<channel|>Hold still.", "Hold still."),
    ("Hold still.\nAnd then more lines", "Hold still."),
    ("I can't just stand here.", "I can't just stand here."),     # in-character, not a refusal
    ("Meet me in the Kitchen.", "Meet me in the Kitchen."),       # capitalised word from the prompt
])
def test_kept(raw, expect):
    assert s(raw).text == expect


@pytest.mark.parametrize("raw,why", [
    ("", "empty"),
    ("<|channel>thought\n<channel|>", "empty"),
    ("*sighs*", "empty"),
    ("I'm sorry, but I can't help with that.", "refusal"),
    ("As an AI language model, I cannot continue.", "refusal"),
    ("I cannot assist with that request.", "refusal"),
    ("Disclaimer: this is fiction.", "refusal"),
    ("word " * 40, "too_long"),
    ("Cy told me and I believe it.", "unknown_name"),          # exists but not known to the speaker
    ("Zed took the rifle.", "unknown_name"),                   # does not exist
    ("Get back, Zed.", "unknown_name"),
])
def test_dropped(raw, why):
    r = s(raw)
    assert r.text is None and r.reason.startswith(why)


def test_cut_off_line_trimmed_or_dropped():
    assert s("Hold still. Do not move or", truncated=True).text == "Hold still."
    assert s("Do not move or", truncated=True).reason == "cut_off"


def test_name_tokens():
    assert name_tokens("Nate 'Rook' Hollis") == {"Nate", "Rook", "Hollis"}

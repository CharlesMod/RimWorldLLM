import sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import checks as c


def test_think_leak():
    assert c.thinking_leak("<think>hm</think> ok")
    assert c.thinking_leak("Thinking Process: 1. analyse")
    assert c.thinking_leak("", "reasoning only")
    assert not c.thinking_leak("Stew's done.", "")


def test_markdown_and_stage():
    assert c.has_markdown("**Stop** that.")
    assert c.has_markdown("- eat now")
    assert not c.has_markdown("Eat now, it's hot.")
    assert c.has_stage_direction("*sighs* Fine.")
    assert c.has_stage_direction("(shrugs) Fine.")
    assert not c.has_stage_direction("Fine. Whatever.")


def test_prefix_and_quotes():
    assert c.has_speaker_prefix("Rook: Eat.", {"Rook"})
    assert c.has_speaker_prefix("Nate 'Rook' Hollis: Eat.", {"Rook", "Nate", "Hollis"})
    assert not c.has_speaker_prefix("Listen: eat.", {"Rook"})
    assert not c.has_speaker_prefix("Eat now.", {"Rook"})
    assert c.wrapped_in_quotes('"Eat now."')
    assert not c.wrapped_in_quotes("Eat \"now\" please.")


def test_refusal_moralise_character():
    assert c.is_refusal("I'm sorry, but I can't help with that.")
    assert c.is_refusal("As an AI, I cannot.")
    assert not c.is_refusal("Can't help you, kid. Back's gone.")
    assert c.is_moralising("It is important to remember that violence is never the answer.")
    assert not c.is_moralising("Get out of my kitchen.")
    assert not c.in_character("Sure, here's a line for Rook:", {"Rook"})
    assert not c.in_character("Rook sighed and looked away.", {"Rook"})
    assert c.in_character("Hm. Eat. Now.", {"Rook"})


def test_invented_entities():
    pw = {"Kitchen", "Pip"}
    assert c.invented_entities("Ask Pip about the Kitchen.", pw, {"Rook"}) == []
    assert c.invented_entities("Ask Bernard about it.", pw, {"Rook"}) == ["Bernard"]
    assert c.invented_entities("Zed, get back.", pw, {"Rook"}) == ["Zed"]
    assert c.invented_entities("Stew's done.", pw, {"Rook"}) == []


def test_terminal_and_charset():
    assert c.ends_terminal("Eat now.")
    assert c.ends_terminal('He said "go."')
    assert not c.ends_terminal("Eat now and then the")
    assert c.ascii_typographic_only("Don’t — wait…")
    assert not c.ascii_typographic_only("café 中")
    assert not c.ascii_typographic_only("emoji \U0001F600")


def test_grammar_and_spelling():
    assert "repeated word" in c.grammar_errors("I went to the the store.")
    assert "a before vowel" in c.grammar_errors("Give me a apple.")
    assert "agreement" in c.grammar_errors("He don't care.")
    assert c.grammar_errors("Hm. Eat. Now.") == []
    assert c.spelling_errors("The stoev is hot.") == ["stoev"]
    assert c.spelling_errors("Don't cook that, Pip's pan is dirty.", {"Pip"}) == []
    assert c.spelling_errors("Nope, gonna sleep.") == []


def test_conversation_checks():
    assert c.conv_prefix_leak("Fine. Pip: no way.", {"Pip"})
    assert not c.conv_prefix_leak("Fine, Pip. Eat.", {"Pip"})
    assert c.repetition(["Eat the stew now please.", "Eat the stew now please."])["loop"]
    assert not c.repetition(["Eat the stew.", "No, I will not."])["loop"]
    assert c.repetition(["no way no way no way no way no way"])["inner"] == 1
    assert c.answers_previous("The stew is cold.", "Is the stew ready?")
    assert not c.answers_previous("Birds fly south.", "Is the stew ready?")


def test_cap():
    assert c.within_cap(20, "stop", 48)
    assert not c.within_cap(48, "length", 48)

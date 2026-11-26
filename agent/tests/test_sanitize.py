import pytest

from hermesvoice_agent.core.sanitize import sanitize_line, name_tokens

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


# ---- F1: a capitalised ordinary word at a sentence start is not a name -----------------------
@pytest.mark.parametrize("raw", [
    "Tuk sees the calf. It will be good meat.",
    "It is good meat, Bram.",
    "Stance is wrong. Grip is too tight.",
    "Quiet is easier now.",
    "Dinner, now.",
    "Rice, potatoes, and beans.",
    "That was close. This is not over. They are coming.",
])
def test_sentence_initial_ordinary_word_is_kept(raw):
    assert s(raw).ok, s(raw).reason


@pytest.mark.parametrize("raw", [
    "Zed is dead. It is over.",            # a real unknown name still goes
    "It is over. Zed took the rifle.",
    "Nutmeg, get back.",
    "Where is Wren now?",                  # capitalised mid-sentence is never an ordinary word
])
def test_unknown_names_still_dropped(raw):
    r = s(raw)
    assert not r.ok and r.reason.startswith("unknown_name")


# ---- F2: possessives and contractions on a known name ----------------------------------------
@pytest.mark.parametrize("raw", [
    "Okay so Bram's panicking and I don't know what to do.",
    "I keep Bram’s shelf exact.",
    "Those are the Brams' boots.",
    "Bram'll know. Ana's fine.",
    "Meet me at the Kitchen's door.",
])
def test_possessive_of_known_name_kept(raw):
    assert s(raw).ok, s(raw).reason


def test_possessive_of_unknown_or_unaware_name_dropped():
    assert s("I keep Zed's shelf exact.").reason == "unknown_name:Zed"      # tail is not part of the reported name
    assert s("I keep Cy's shelf exact.").reason == "unknown_name:Cy"       # exists, speaker does not know it


def test_reflection_prose_accepts_possessive():
    from hermesvoice_agent.core.sanitize import sanitize_prose
    r = sanitize_prose("I keep Bram's shelf exact and it calms me down.", known_names={"Bram"},
                       all_names={"Ana", "Bram"}, prompt_words=set(), speaker_names={"Ana"})
    assert r.ok, r.reason


def test_prompt_cap_words_drops_possessive_tail():
    from hermesvoice_agent.core.sanitize import prompt_cap_words
    assert prompt_cap_words("Biscuit's bowl and Hana’s shelf") == {"Biscuit", "Hana"}
    assert s("Biscuit's not gonna stop.", prompt_words=prompt_cap_words("Biscuit's bowl")).ok


# ---- F3: self narration and bullets ---------------------------------------------------------
@pytest.mark.parametrize("raw", [
    "Ana feels the bandage. Ana does not like it.",
    "Ana is crouched, bow drawn.",
    "- Ana waits for the calf to step out.",
])
def test_self_narration_dropped(raw):
    assert s(raw, narration_guard=True).reason == "self_narration"


@pytest.mark.parametrize("raw", [
    "Ana, wait for me.",                 # a vocative is not narration
    "Ana's bow is broken.",
    "Hold still, Ana.",
    "Bram feels the bandage.",           # about someone else
])
def test_not_self_narration(raw):
    assert s(raw, narration_guard=True).ok


def test_guard_is_opt_in_and_off_for_third_person_voices():
    assert s("Ana hunts. Ana eats.").ok                          # guard off (prose, or a third-person voice card)
    from hermesvoice_agent.core.minds import VoiceCard
    v = VoiceCard(examples=["Ana hunts. Ana eats. Simple.", "Good meat."])
    assert v.speaks_third_person({"Ana"})
    assert not VoiceCard(examples=["Hold still.", "Ana, wait."]).speaks_third_person({"Ana"})
    assert not VoiceCard(examples=["Ana hunts."], third_person=False).speaks_third_person({"Ana"})
    assert VoiceCard(third_person=True).speaks_third_person({"Ana"})


def test_line_pass_applies_guard_unless_card_says_third_person():
    import asyncio
    from hermesvoice_agent.core.fakes import FakeLLM
    from hermesvoice_agent.core.minds import Mind
    from hermesvoice_agent.core.passes import SanitizeCtx, line_pass

    def go(third):
        llm = FakeLLM()
        llm.script["line"] = ["Ana feels the bandage. Ana does not like it."]
        ctx = SanitizeCtx({"Ana"}, {"Bram"}, {"Ana", "Bram"}, set(), third_person=third)
        r, _ = asyncio.run(line_pass(llm, slot=0, mind=Mind(actor_id=1, name="Ana"), name="Ana", prefix="P",
                                     turns=[], turn_no=0, ctx=ctx))
        return r
    assert go(False).reason == "self_narration"
    assert go(True).ok


@pytest.mark.parametrize("raw,expect", [
    ("- Six minutes. No sound.", "Six minutes. No sound."),
    ("* Six minutes.", "Six minutes."),
    ("• Six minutes.", "Six minutes."),
    ("-Six minutes.", "Six minutes."),
    ("*sighs* Six minutes.", "Six minutes."),     # a stage direction still goes, not read as a bullet
])
def test_bullets_stripped_not_dropped(raw, expect):
    assert s(raw).text == expect


# ---- a leaked intent label after the line (E4B rp, gpubench 2026-11-25) ------------------------------------------
_LABELS = ("none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain")


@pytest.mark.parametrize("raw,want", [
    ("Pip watch calf leg. Need more work done before dark. kind", "Pip watch calf leg. Need more work done before dark."),
    ("Okay so Moth, like, you gotta stop tearing the papers up, it's not helping... comfort",
     "Okay so Moth, like, you gotta stop tearing the papers up, it's not helping..."),
    ("Hm. Raiders. Get the big cooking pots ready. gossip", "Hm. Raiders. Get the big cooking pots ready."),
    ("I think maybe that tea could... would it be bad? kind", "I think maybe that tea could... would it be bad?"),
    ("Dirt warm enough. I take the plate. comfort.", "Dirt warm enough. I take the plate."),
    ("Thank you, you are always so kind", "Thank you, you are always so kind"),       # not after a sentence end
    ("That was a cheap slight.", "That was a cheap slight."),                         # part of the sentence
    ("Stop it. Kind words cost nothing", "Stop it. Kind words cost nothing"),          # capitalised, more words
])
def test_a_trailing_intent_label_is_cut(raw, want):
    from hermesvoice_agent.core.sanitize import strip_trailing_label
    assert strip_trailing_label(raw, _LABELS) == want


def test_sanitize_line_cuts_the_label_only_when_given_labels():
    from hermesvoice_agent.core.sanitize import sanitize_line
    kw = dict(speaker_names=set(), known_names=set(), all_names=set())
    assert sanitize_line("Good meat here. Not bad like that scrap yesterday. kind", intent_labels=_LABELS, **kw).text \
        == "Good meat here. Not bad like that scrap yesterday."
    assert sanitize_line("Good meat here. kind", **kw).text.endswith("kind")


def test_a_sentence_said_twice_keeps_its_first_saying():
    from hermesvoice_agent.core.sanitize import drop_repeated_sentences
    assert drop_repeated_sentences("This meal marks the end. Enough. This meal marks the end.") == "This meal marks the end. Enough."
    assert drop_repeated_sentences("Enough. We eat now. We eat now.") == "Enough. We eat now."
    assert drop_repeated_sentences("No. No. Not again.") == "No. No. Not again."      # one-word beats are kept
    assert drop_repeated_sentences("Right, so what now?") == "Right, so what now?"

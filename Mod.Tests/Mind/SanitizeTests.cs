// Ported from agent/tests/test_sanitize.py. Deferred (need minds/passes/fakes from other workers): the VoiceCard.speaks_third_person
// half of test_guard_is_opt_in_and_off_for_third_person_voices and test_line_pass_applies_guard_unless_card_says_third_person.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class SanitizeTests : IDisposable
    {
        public SanitizeTests() { Adult.SetEnabled(false); }
        public void Dispose() { Adult.SetEnabled(false); }

        static HashSet<string> S(params string[] a) { return new HashSet<string>(a); }
        static Sanitized Sn(string raw, ISet<string> promptWords = null, bool truncated = false, bool narrationGuard = false)
            => Sanitize.SanitizeLine(raw, S("Ana"), S("Bram"), S("Ana", "Bram", "Cy"), promptWords ?? S("Kitchen"), truncated: truncated, narrationGuard: narrationGuard);

        static IEnumerable<object[]> Rows(params string[] a) { foreach (var x in a) yield return new object[] { x }; }
        static IEnumerable<object[]> Pairs(params string[][] a) { foreach (var x in a) yield return new object[] { x[0], x[1] }; }

        public static IEnumerable<object[]> KeptData() => Pairs(
            new[] { "Hold still, Bram.", "Hold still, Bram." },
            new[] { "\"Hold still.\"", "Hold still." },
            new[] { "*sighs* Hold still.", "Hold still." },
            new[] { "Hold still. (she frowns)", "Hold still." },
            new[] { "Ana: Hold still.", "Hold still." },
            new[] { "<|channel>thought\n<channel|>Hold still.", "Hold still." },
            new[] { "Hold still.\nAnd then more lines", "Hold still." },
            new[] { "I can't just stand here.", "I can't just stand here." },
            new[] { "Meet me in the Kitchen.", "Meet me in the Kitchen." });

        [Theory, MemberData(nameof(KeptData))]
        public void Kept(string raw, string expect) { Assert.Equal(expect, Sn(raw).Text); }

        public static IEnumerable<object[]> DroppedData() => Pairs(
            new[] { "", "empty" },
            new[] { "<|channel>thought\n<channel|>", "empty" },
            new[] { "*sighs*", "empty" },
            new[] { "I'm sorry, but I can't help with that.", "refusal" },
            new[] { "As an AI language model, I cannot continue.", "refusal" },
            new[] { "I cannot assist with that request.", "refusal" },
            new[] { "Disclaimer: this is fiction.", "refusal" },
            new[] { string.Concat(Enumerable.Repeat("word ", 40)), "too_long" },
            new[] { "Cy told me and I believe it.", "unknown_name" },
            new[] { "Zed took the rifle.", "unknown_name" },
            new[] { "Get back, Zed.", "unknown_name" });

        [Theory, MemberData(nameof(DroppedData))]
        public void Dropped(string raw, string why)
        {
            var r = Sn(raw);
            Assert.Null(r.Text); Assert.StartsWith(why, r.Reason);
        }

        [Fact]
        public void CutOffLineTrimmedOrDropped()
        {
            Assert.Equal("Hold still.", Sn("Hold still. Do not move or", truncated: true).Text);
            Assert.Equal("cut_off", Sn("Do not move or", truncated: true).Reason);
        }

        [Fact]
        public void NameTokens() { Assert.True(Sanitize.NameTokens("Nate 'Rook' Hollis").SetEquals(new[] { "Nate", "Rook", "Hollis" })); }

        public static IEnumerable<object[]> OrdinaryStart() => Rows("Tuk sees the calf. It will be good meat.", "It is good meat, Bram.", "Stance is wrong. Grip is too tight.",
            "Quiet is easier now.", "Dinner, now.", "Rice, potatoes, and beans.", "That was close. This is not over. They are coming.");

        [Theory, MemberData(nameof(OrdinaryStart))]
        public void SentenceInitialOrdinaryWordIsKept(string raw) { var r = Sn(raw); Assert.True(r.Ok, r.Reason); }

        public static IEnumerable<object[]> UnknownNames() => Rows("Zed is dead. It is over.", "It is over. Zed took the rifle.", "Nutmeg, get back.", "Where is Wren now?");

        [Theory, MemberData(nameof(UnknownNames))]
        public void UnknownNamesStillDropped(string raw) { var r = Sn(raw); Assert.False(r.Ok); Assert.StartsWith("unknown_name", r.Reason); }

        public static IEnumerable<object[]> Possessives() => Rows("Okay so Bram's panicking and I don't know what to do.", "I keep Bram’s shelf exact.",
            "Those are the Brams' boots.", "Bram'll know. Ana's fine.", "Meet me at the Kitchen's door.");

        [Theory, MemberData(nameof(Possessives))]
        public void PossessiveOfKnownNameKept(string raw) { var r = Sn(raw); Assert.True(r.Ok, r.Reason); }

        [Fact]
        public void PossessiveOfUnknownOrUnawareNameDropped()
        {
            Assert.Equal("unknown_name:Zed", Sn("I keep Zed's shelf exact.").Reason);      // tail is not part of the reported name
            Assert.Equal("unknown_name:Cy", Sn("I keep Cy's shelf exact.").Reason);       // exists, speaker does not know it
        }

        [Fact]
        public void ReflectionProseAcceptsPossessive()
        {
            var r = Sanitize.SanitizeProse("I keep Bram's shelf exact and it calms me down.", S("Bram"), S("Ana", "Bram"), S(), speakerNames: S("Ana"));
            Assert.True(r.Ok, r.Reason);
        }

        [Fact]
        public void PromptCapWordsDropsPossessiveTail()
        {
            Assert.True(Sanitize.PromptCapWords("Biscuit's bowl and Hana’s shelf").SetEquals(new[] { "Biscuit", "Hana" }));
            Assert.True(Sn("Biscuit's not gonna stop.", promptWords: Sanitize.PromptCapWords("Biscuit's bowl")).Ok);
        }

        public static IEnumerable<object[]> SelfNarr() => Rows("Ana feels the bandage. Ana does not like it.", "Ana is crouched, bow drawn.", "- Ana waits for the calf to step out.");

        [Theory, MemberData(nameof(SelfNarr))]
        public void SelfNarrationDropped(string raw) { Assert.Equal("self_narration", Sn(raw, narrationGuard: true).Reason); }

        public static IEnumerable<object[]> NotSelfNarr() => Rows("Ana, wait for me.", "Ana's bow is broken.", "Hold still, Ana.", "Bram feels the bandage.");

        [Theory, MemberData(nameof(NotSelfNarr))]
        public void NotSelfNarration(string raw) { Assert.True(Sn(raw, narrationGuard: true).Ok); }

        [Fact]
        public void GuardIsOptInAndOffForThirdPersonVoices()
        {
            Assert.True(Sn("Ana hunts. Ana eats.").Ok);       // guard off (prose, or a third-person voice card); the VoiceCard half is deferred
        }

        public static IEnumerable<object[]> Bullets() => Pairs(
            new[] { "- Six minutes. No sound.", "Six minutes. No sound." },
            new[] { "* Six minutes.", "Six minutes." },
            new[] { "• Six minutes.", "Six minutes." },
            new[] { "-Six minutes.", "Six minutes." },
            new[] { "*sighs* Six minutes.", "Six minutes." });

        [Theory, MemberData(nameof(Bullets))]
        public void BulletsStrippedNotDropped(string raw, string expect) { Assert.Equal(expect, Sn(raw).Text); }

        static readonly string[] Labels = { "none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain" };

        public static IEnumerable<object[]> TrailingLabels() => Pairs(
            new[] { "Pip watch calf leg. Need more work done before dark. kind", "Pip watch calf leg. Need more work done before dark." },
            new[] { "Okay so Moth, like, you gotta stop tearing the papers up, it's not helping... comfort", "Okay so Moth, like, you gotta stop tearing the papers up, it's not helping..." },
            new[] { "Hm. Raiders. Get the big cooking pots ready. gossip", "Hm. Raiders. Get the big cooking pots ready." },
            new[] { "I think maybe that tea could... would it be bad? kind", "I think maybe that tea could... would it be bad?" },
            new[] { "Dirt warm enough. I take the plate. comfort.", "Dirt warm enough. I take the plate." },
            new[] { "Thank you, you are always so kind", "Thank you, you are always so kind" },
            new[] { "That was a cheap slight.", "That was a cheap slight." },
            new[] { "Stop it. Kind words cost nothing", "Stop it. Kind words cost nothing" });

        [Theory, MemberData(nameof(TrailingLabels))]
        public void ATrailingIntentLabelIsCut(string raw, string want) { Assert.Equal(want, Sanitize.StripTrailingLabel(raw, Labels)); }

        [Fact]
        public void SanitizeLineCutsTheLabelOnlyWhenGivenLabels()
        {
            Assert.Equal("Good meat here. Not bad like that scrap yesterday.",
                Sanitize.SanitizeLine("Good meat here. Not bad like that scrap yesterday. kind", S(), S(), S(), intentLabels: Labels).Text);
            Assert.EndsWith("kind", Sanitize.SanitizeLine("Good meat here. kind", S(), S(), S()).Text);
        }

        [Fact]
        public void ASentenceSaidTwiceKeepsItsFirstSaying()
        {
            Assert.Equal("This meal marks the end. Enough.", Sanitize.DropRepeatedSentences("This meal marks the end. Enough. This meal marks the end."));
            Assert.Equal("Enough. We eat now.", Sanitize.DropRepeatedSentences("Enough. We eat now. We eat now."));
            Assert.Equal("No. No. Not again.", Sanitize.DropRepeatedSentences("No. No. Not again."));      // one-word beats are kept
            Assert.Equal("Right, so what now?", Sanitize.DropRepeatedSentences("Right, so what now?"));
        }

        // ---- the age-gate sanitiser tests from test_agegate.py (c) ----
        static Sanitized San(string t, bool involvesMinor = false, ISet<string> minorNames = null)
            => Sanitize.SanitizeLine(t, S("Ana"), S("Bram", "Cy"), S("Ana", "Bram", "Cy"), involvesMinor: involvesMinor, minorNames: minorNames);

        [Fact]
        public void SanitizerDropsRomanceWhenAMinorIsInvolvedOrNamed()
        {
            foreach (var t in AgeGateTests.RomanticLines)
            {
                var s = San(t, involvesMinor: true);
                Assert.True(!s.Ok && s.Reason == "minor_safety", t);
            }
            Assert.Equal("minor_safety", San("Cy is so handsome, I love Cy.", minorNames: S("Cy")).Reason);
            Assert.True(San("Come here, you handsome fool.").Ok);
            foreach (var t in AgeGateTests.PlainLines) Assert.True(San(t, involvesMinor: true, minorNames: S("Cy")).Ok, t);
        }

        [Fact]
        public void SanitizerAppliesToProse()
        {
            var s = Sanitize.SanitizeProse("Ana spent the evening flirting with Cy by the fire and it felt wonderful.", S("Cy"), S("Ana", "Cy"), S(), minorNames: S("Cy"));
            Assert.Equal("minor_safety", s.Reason);
            s = Sanitize.SanitizeProse("Ana spent the evening flirting with Bram by the fire and it felt wonderful.", S("Bram"), S("Ana", "Bram"), S(), minorNames: S("Cy"), speakerNames: S("Ana"));
            Assert.True(s.Ok);
            Assert.Equal("minor_safety", Sanitize.SanitizeProse("Cy kissed the cheek of the cat and it was a good day for the farm.", S("Cy"), S("Cy"), S(), involvesMinor: true).Reason);
        }
    }
}

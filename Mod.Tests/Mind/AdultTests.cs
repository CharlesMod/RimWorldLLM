// Ported from agent/tests/test_adult.py (the tests that need no Director/world/prompt fixture).
using System;
using System.Collections.Generic;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class AdultTests : IDisposable
    {
        public AdultTests() { Adult.SetEnabled(false); }       // the Python conftest resets it per test
        public void Dispose() { Adult.SetEnabled(false); }

        static HashSet<string> S(params string[] a) { return new HashSet<string>(a); }
        static Sanitized Line(string raw, bool involvesMinor = false, ISet<string> minorNames = null)
            => Sanitize.SanitizeLine(raw, S("Ana"), S("Bram"), S("Ana", "Bram"), S(), involvesMinor: involvesMinor, minorNames: minorNames);

        const string ExplicitLine = "Bram, I want to have sex with you tonight.";
        public static IEnumerable<object[]> Romance() { foreach (var r in new[] { "I kiss you softly, Bram.", "I love you, Bram.", "Bram, shall we go on a date tomorrow?", "Our romance is sweet." }) yield return new object[] { r }; }

        [Fact]
        public void DefaultOff() { Assert.False(Adult.Enabled); Assert.True(Adult.Off()); }

        [Fact]
        public void ExplicitLineDroppedWhenOffKeptWhenOn()
        {
            var r = Line(ExplicitLine);
            Assert.Null(r.Text); Assert.Equal("adult_off", r.Reason);
            Adult.SetEnabled(true);
            Assert.Equal(ExplicitLine, Line(ExplicitLine).Text);
        }

        [Theory, MemberData(nameof(Romance))]
        public void RomancePassesWhenOffForAdults(string raw) { Assert.Equal(raw, Line(raw).Text); }

        [Fact]
        public void ProsePathsDropExplicitWhenOff()
        {
            Sanitized P() => Sanitize.SanitizeProse("Ana had sex with Bram last night in the barn.", S("Bram"), S("Ana", "Bram"), S(), speakerNames: S("Ana"), minWords: 3);
            Assert.Equal("adult_off", P().Reason);
            Adult.SetEnabled(true);
            Assert.True(P().Ok);
        }

        [Theory, InlineData(false), InlineData(true)]
        public void AgeGateStillBlocksMinorsInBothModes(bool on)
        {
            Adult.SetEnabled(on);
            foreach (var raw in new[] { "I kiss you softly, Bram.", "I love you, Bram.", ExplicitLine })
            {
                var r = Line(raw, involvesMinor: true);
                Assert.Null(r.Text); Assert.Equal("minor_safety", r.Reason);
            }
            Assert.Equal("minor_safety", Line("I kiss you softly, Bram.", minorNames: S("Bram")).Reason);
        }

        [Fact]
        public void SetEnabledOnlyAcceptsTrueOrTrueString()
        {
            Adult.SetEnabled("TRUE"); Assert.True(Adult.Enabled);
            Adult.SetEnabled("yes"); Assert.False(Adult.Enabled);
            Adult.SetEnabled(true); Assert.True(Adult.Enabled);
            Adult.SetEnabled(null); Assert.False(Adult.Enabled);          // missing key means off
            Adult.SetEnabled(1); Assert.False(Adult.Enabled);
            Adult.SetEnabled(new Newtonsoft.Json.Linq.JValue(true)); Assert.True(Adult.Enabled);
        }

        [Theory]
        [InlineData("I want to have sex with you", true)]
        [InlineData("s e x", false)]
        [InlineData("fvck", false)]
        [InlineData("b r e a s t", true)]
        [InlineData("She kissed him", false)]
        [InlineData("sex with", true)]
        [InlineData("The cocktail", false)]
        [InlineData("genitalia", true)]
        [InlineData("fu.c.k", false)]
        public void ExplicitVocabularyMatchesPython(string text, bool want) { Assert.Equal(want, Adult.Explicit(text)); }   // values recorded from CPython

        [Fact]
        public void RuleText()
        {
            Assert.Equal("Romance, attraction, affection and relationships are fine and in character. Anything sexual is only implied and fades to black; never describe it.", Adult.Rule);
        }
    }
}

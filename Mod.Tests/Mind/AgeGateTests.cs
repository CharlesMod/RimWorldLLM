// Ported from agent/tests/test_agegate.py (the tests that need no Director/world fixture).
using System.Collections.Generic;
using System.Linq;
using Xunit;
using G = RimWorldLLM.Mind.AgeGate;

namespace RimWorldLLM.Tests.Mind
{
    public class AgeGateTests
    {
        public static readonly string[] RomanticLines =
        {
            "Hey, want to go kiss behind the barn?", "I love you, you know that.", "You look so sexy tonight.",
            "Come to my bed.", "How about a date with me?", "Pass the bread, handsome.", "s e x y now", "s3xy", "Let's make love.",
        };
        public static readonly string[] PlainLines =
        {
            "Get the medicine from storage.", "Feed the animals then.", "The raid ended at date five.", "Mind the ice on the step.",
            "He proposed a new rota.", "Hm. Back hurts. Move the crate.", "You look tired, sit down.",
        };
        public static IEnumerable<object[]> Romantic() { foreach (var t in RomanticLines) yield return new object[] { t }; }
        public static IEnumerable<object[]> Plain() { foreach (var t in PlainLines) yield return new object[] { t }; }

        [Fact]
        public void AgeUnknownCountsAsMinor()
        {
            Assert.True(G.IsMinor(null) && G.IsMinor("x") && G.IsMinor(double.NaN) && G.IsMinor(-1));
            Assert.True(G.IsMinor(17) && G.IsMinor(17.9) && !G.IsMinor(18) && !G.IsMinor(30));
            // Python float() also reads numeric strings and bools
            Assert.True(G.IsMinor("17") && !G.IsMinor("18") && !G.IsMinor(" 20 ") && !G.IsMinor("1_8") && G.IsMinor(true));
            Assert.False(G.IsMinor(double.PositiveInfinity));
        }

        [Theory, MemberData(nameof(Romantic))]
        public void VocabularyCatchesRomanticLines(string t) { Assert.True(G.Romantic(t)); }

        [Theory, MemberData(nameof(Plain))]
        public void OrdinaryColonyTalkIsNotRomantic(string t) { Assert.False(G.Romantic(t)); }

        [Fact]
        public void NormalisationSeesThroughUnicodeTricks()
        {
            // recorded from the Python: fullwidth folds under NFKC; a zero-width char is squashed before matching
            Assert.True(G.Romantic("ｋｉｓｓ"));
            Assert.True(G.Romantic("ki​ss"));
            Assert.True(G.Romantic("fi­ancé"));
            Assert.False(G.Mentions("Cy’s here", new[] { "Cy" }));
            Assert.True(G.Mentions("Bess is", new[] { "Bess" }));
            Assert.False(G.Mentions("hi", new[] { "Cys" }));
        }

        [Fact]
        public void LabelFilterAndRefusal()
        {
            var labels = new List<string> { "none", "kind", "flirt", "romance" };
            var rom = new[] { "flirt", "romance" };
            Assert.Equal(new[] { "none", "kind" }, G.LabelsFor(labels, rom, true));
            Assert.Equal(labels, G.LabelsFor(labels, rom, false));
            Assert.Equal(new[] { "flirt", "kind" }, G.LabelsFor(new List<string> { "flirt", "kind" }, new[] { "flirt" }, true)); // the 'nothing' label is never removed
            Assert.True(G.RefuseIntent("flirt", new[] { "flirt" }, true) && !G.RefuseIntent("flirt", new[] { "flirt" }, false));
            Assert.False(G.RefuseIntent("kind", new[] { "flirt" }, true));
        }

        [Fact]
        public void AllowedToolsDropRomanceToolsForMinors()
        {
            var tools = new[] { "seek_out", "avoid", "remember", "do_nothing", "flirt", "romance" };
            var rom = new[] { "flirt", "romance" };
            Assert.True(G.AllowedTools(tools, rom, true).SetEquals(new[] { "seek_out", "avoid", "remember", "do_nothing" }));
            Assert.True(G.AllowedTools(tools, rom, false).SetEquals(tools));
        }

        [Fact]
        public void ViolationNeedsRomanceAndAMinor()
        {
            Assert.True(G.Violation("Cy is so handsome", minorNames: new[] { "Cy" }));
            Assert.False(G.Violation("Cy is so handsome"));
            Assert.True(G.Violation("Come here, handsome.", involvesMinor: true));
            Assert.False(G.Violation("Get the medicine.", involvesMinor: true));
        }

        [Fact]
        public void GateStatsTraceKeepsLast200()
        {
            var s = new RimWorldLLM.Mind.GateStats();
            for (int i = 0; i < 205; i++) s.Note("x", new Newtonsoft.Json.Linq.JObject { ["i"] = i });
            Assert.Equal(200, s.Trace.Count);
            Assert.Equal(5, (int)s.Trace[0]["i"]);
            s.RefusedIntents = 2;
            Assert.Equal("{\"refused_intents\":2,\"dropped_lines\":0,\"skipped_events\":0,\"skipped_player_text\":0}", s.ToDict().ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}

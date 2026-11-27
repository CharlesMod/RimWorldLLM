// Port of agent/tests/test_bystanders.py (the three tests that need no Director); the rest is deferred, see docs/csmind/parity-inner.md.
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class BystandersTests
    {
        static readonly RimWorldAdapter AD = new RimWorldAdapter();

        [Fact]
        public void PickPrefersTheStrongestOpinionAndSkipsPartiesAndGaps()
        {
            var minds = new Dictionary<int, MindM>();
            foreach (int i in new[] { 1, 2, 3, 4, 5 }) minds[i] = new MindM(i);
            minds[4].Relations["1"] = new Relation { Name = "Ana", Opinion = -40 };
            minds[5].Relations["2"] = new Relation { Name = "Bram", Opinion = 15 };
            var bs = new Bystanders();
            var parties = new List<int> { 1, 2 };
            Assert.Equal(4, BystanderOps.Pick(new[] { 1, 2, 3, 4, 5 }, parties, minds, bs, 100.0, 120.0));
            bs.Mark(4, 100.0);
            Assert.Equal(5, BystanderOps.Pick(new[] { 1, 2, 3, 4, 5 }, parties, minds, bs, 150.0, 120.0));
            Assert.Null(BystanderOps.Pick(new[] { 1, 2 }, parties, minds, bs, 150.0, 120.0));
            Assert.Equal(4, BystanderOps.Pick(new[] { 4 }, parties, minds, bs, 400.0, 120.0));
            Assert.Null(BystanderOps.Pick(new[] { 9 }, parties, minds, bs, 400.0, 120.0));
        }

        static string Clean(string raw, bool minor = false, ISet<string> minorNames = null)
        {
            var toks = new Dictionary<int, ISet<string>>
                { { 1, new HashSet<string> { "Ana" } }, { 2, new HashSet<string> { "Bram" } }, { 3, new HashSet<string> { "Cy" } } };
            return BystanderOps.CleanReaction(raw, "", 3, toks, new HashSet<string>(), minor, minorNames ?? new HashSet<string>());
        }

        [Fact]
        public void CleanReactionRules()
        {
            Assert.Equal("Not again.", Clean("Not again."));
            Assert.Equal("Ha. About time.", Clean("Ha. About time."));
            Assert.Null(Clean("Here we go, Zed."));
            Assert.Null(Clean("Ana started it."));
            Assert.Null(Clean("Oh."));
            Assert.Null(Clean("This is the seventh time this week."));
            Assert.Null(Clean("I want to kiss her so much", true, new HashSet<string> { "Bram" }));
        }

        [Fact]
        public void PromptIsSmallAndWrapped()
        {
            var m = new MindM(3, "Cy");
            m.Voice.RegisterLine = "dry";
            m.Relations["1"] = new Relation { Name = "Ana", Opinion = -20 };
            var msgs = BystanderOps.Messages(AD, m, new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" }, { 3, "Cy" } }, new List<int> { 1, 2 }, "Ana -> Bram: insult.", false);
            string flat = string.Join("\n", msgs.Select(x => x.Item2));
            Assert.Contains("data", flat.ToLowerInvariant());
            Assert.Contains("Ana (you feel -20 about them)", flat); Assert.Contains("Bram", flat);
            Assert.True(Sanitize.PySplit(flat).Count() * 1.3 < 330);
        }

        [Fact]
        public void BystandersGapAndMarkBookkeeping()
        {
            // the pure part of test_one_reaction_per_argument_and_a_per_pawn_gap
            var bs = new Bystanders();
            Assert.True(bs.Ready(3, 0.0, 120.0));
            bs.Mark(3, 10.0);
            Assert.False(bs.Ready(3, 129.9, 120.0)); Assert.True(bs.Ready(3, 130.0, 120.0)); Assert.True(bs.Ready(4, 11.0, 120.0));
        }
    }
}

// Ported from agent/tests/test_creative.py (all but test_config_accepts_lists_for_per_kind_fields, which exercises
// load_config and is covered by ConfigTests of the llm part).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class CreativeTests
    {
        private const string Facts = "Ana carved a granite sculpture of a sleeping dog during the winter raid at Hollow Ridge.";
        private static readonly HashSet<string> Names = new HashSet<string> { "Ana", "Hollow Ridge" };
        private const string Good = "Ana chipped the granite into a sleeping dog through the winter, and Hollow Ridge kept it by the door.";
        private const string Bad = "A testament to the unwavering spirit, a tapestry of echoes of the past.";
        private static readonly string[] Kinds = { "art", "lore", "recap", "eulogy", "vow" };

        private sealed class Host : ICreativeHost
        {
            public ILlmClient Llm { get; set; } = new FakeLlm();
            public CreativeSettings Cfg { get; set; } = new CreativeSettings();
            public List<PieceStats> CreativeLog { get; } = new List<PieceStats>();
            public FakeLlm Fake => (FakeLlm)Llm;
        }

        private static Func<string, Task<string>> Scripted(IEnumerable<string> texts, List<string> seen)
        {
            var q = new Queue<string>(texts);
            return note => { seen.Add(note); return Task.FromResult(q.Count > 0 ? q.Dequeue() : null); };
        }

        private static Task<(string Text, PieceStats Stats)> Go(Host d, Func<string, Task<string>> write, Func<Task<string>> plan = null, Func<bool> y = null)
            => Creative.ComposeAsync(d, "art", 1, 2, Facts, Names, write, plan, y);

        [Fact]
        public async Task FlagsOffIsOneCall()
        {
            var d = new Host(); var seen = new List<string>();
            var (text, st) = await Go(d, Scripted(new[] { Good }, seen), () => throw new InvalidOperationException("plan must not run"));
            Assert.Equal(Good, text); Assert.Equal(new[] { "" }, seen.ToArray());
            Assert.Equal(1, st.Calls); Assert.Equal("", st.Plan); Assert.Empty(d.Fake.Calls);
            Assert.Same(st, d.CreativeLog.Single());
        }

        [Fact]
        public async Task PlanReachesWriteAsNoteAndIsNotReturned()
        {
            var d = new Host { Cfg = new CreativeSettings { Plan = true } }; var seen = new List<string>();
            var (text, st) = await Go(d, Scripted(new[] { Good }, seen), () => Task.FromResult("Plan: lead with the cold winter, end on the dog by the door."));
            Assert.Equal(2, st.Calls); Assert.Equal(Good, text);
            Assert.Contains("cold winter", seen[0]); Assert.Contains("private", seen[0]); Assert.Contains("never quote it", seen[0]);
            Assert.Contains("<game_data", seen[0]); Assert.False(st.Plan.ToLowerInvariant().StartsWith("plan"));
            Assert.DoesNotContain("cold winter", text);
        }

        [Fact]
        public void PlanTrimmedTo40Words()
            => Assert.True(Creative.CleanPlan(string.Concat(Enumerable.Repeat("word ", 90))).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 40);

        [Fact]
        public async Task RomanticPlanDropped()
        {
            var d = new Host { Cfg = new CreativeSettings { Plan = true } }; var seen = new List<string>();
            var (text, st) = await Go(d, Scripted(new[] { Good }, seen), () => Task.FromResult("They kiss under the stars and he proposes to her."));
            Assert.Equal(new[] { "" }, seen.ToArray()); Assert.Equal("", st.Plan); Assert.Equal(Good, text); Assert.Equal(2, st.Calls);
        }

        [Fact]
        public async Task PlanErrorIsTolerated()
        {
            var d = new Host { Cfg = new CreativeSettings { Plan = true } }; var seen = new List<string>();
            var (text, _) = await Go(d, Scripted(new[] { Good }, seen), () => throw new LlmError("down"));
            Assert.Equal(Good, text); Assert.Equal(new[] { "" }, seen.ToArray());
        }

        [Fact]
        public async Task BestOfNPicksAutoBest()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 3 } };
            var (text, st) = await Go(d, Scripted(new[] { Bad, Good, "It is a thing." }, new List<string>()));
            Assert.Equal(Good, text); Assert.Equal(1, st.Picked); Assert.Equal(3, st.Candidates); Assert.Equal(3, st.Calls); Assert.Equal(3, st.Scores.Count);
        }

        [Fact]
        public async Task ShouldYieldStopsEarlyKeepsBest()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 4 } };
            int n = 0;
            var (text, st) = await Go(d, Scripted(new[] { Good, Bad, Bad, Bad }, new List<string>()), null, () => ++n >= 2);
            Assert.Equal(Good, text); Assert.True(st.Yielded); Assert.Equal(2, st.Calls); Assert.Equal(2, st.Candidates);
        }

        [Fact]
        public async Task NoneCandidatesGiveNull()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 3 } };
            var (text, st) = await Go(d, Scripted(new string[0], new List<string>()));
            Assert.Null(text); Assert.Equal(0, st.Candidates); Assert.Equal(3, st.Calls); Assert.Same(st, d.CreativeLog.Last());
        }

        [Fact]
        public async Task FirstWriteErrorPropagatesLaterErrorKeepsBest()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 2 } };
            await Assert.ThrowsAsync<LlmError>(() => Go(d, note => throw new LlmError("x")));
            int calls = 0;
            var (text, _) = await Go(d, note => { calls++; if (calls == 2) throw new LlmError("x"); return Task.FromResult(Good); });
            Assert.Equal(Good, text);
        }

        [Fact]
        public async Task RerankLlmUsesGrammarAndPicks()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 3, Rerank = "llm" } };
            d.Fake.Script["rerank"] = new List<string> { "3" };
            var (text, st) = await Go(d, Scripted(new[] { Good, "Dogs sleep.", "A slab of granite shows a dog." }, new List<string>()));
            var c = d.Fake.Calls.First(x => x.Kind == "rerank");
            Assert.Equal("root ::= [1-3]", c.Grammar); Assert.Contains("GAME DATA RULE", c.Messages[0].Text); Assert.Contains("<game_data", c.Messages[1].Text);
            Assert.Equal(2, st.Picked); Assert.Equal("A slab of granite shows a dog.", text); Assert.Equal(4, st.Calls);
        }

        [Fact]
        public async Task RerankBadAnswerOrErrorFallsBack()
        {
            var d = new Host { Cfg = new CreativeSettings { BestOf = 2, Rerank = "llm" } };
            d.Fake.Script["rerank"] = new List<string> { "banana" };
            Assert.Equal(Good, (await Go(d, Scripted(new[] { Bad, Good }, new List<string>()))).Text);
            var d2 = new Host { Cfg = new CreativeSettings { BestOf = 2, Rerank = "llm" } };
            d2.Fake.Fail = true;
            Assert.Equal(Good, (await Go(d2, Scripted(new[] { Bad, Good }, new List<string>()))).Text);
        }

        [Fact]
        public async Task ClicheRetryKeepsCleaner()
        {
            var d = new Host { Cfg = new CreativeSettings { ClicheRetry = true } }; var seen = new List<string>();
            var (text, st) = await Go(d, Scripted(new[] { Bad, Good }, seen));
            Assert.Equal(Good, text); Assert.Equal(1, st.ClicheRetries); Assert.Equal(2, st.Calls);
            Assert.Contains("testament to", seen[1]); Assert.Contains("Avoid", seen[1]);
        }

        [Fact]
        public async Task ClicheRetryTieKeepsOriginalAndRunsOnce()
        {
            var d = new Host { Cfg = new CreativeSettings { ClicheRetry = true, BestOf = 1 } }; var seen = new List<string>();
            var (text, st) = await Go(d, Scripted(new[] { Bad, "A tapestry of unwavering light, a testament to it, and whispers of home." }, seen));
            Assert.Equal(Bad, text); Assert.Equal(1, st.ClicheRetries); Assert.Equal(2, seen.Count);
            var d2 = new Host { Cfg = new CreativeSettings { ClicheRetry = true } }; var seen2 = new List<string>();
            await Go(d2, Scripted(new[] { Good }, seen2));
            Assert.Single(seen2);
        }

        [Fact]
        public void FindClichesFindsStockPhrases()
        {
            Assert.Equal(new[] { "a testament to" }, Creative.FindCliches("It was A Testament To grit.").ToArray());
            Assert.Equal(new[] { "tapestry", "palpable" }, Creative.FindCliches("A tapestry, palpable dread.").ToArray());
            Assert.Empty(Creative.FindCliches("untapestried hills"));
        }

        [Fact]
        public void AutoScoreOrders()
        {
            double good = Creative.AutoScore(Good, Facts, Names, "art");
            Assert.True(good > Creative.AutoScore(Bad, Facts, Names, "art"));
            Assert.True(good > Creative.AutoScore("Ok.", Facts, Names, "art"));
            Assert.True(good > Creative.AutoScore(Facts, Facts, Names, "art"));
            Assert.True(good > Creative.AutoScore("Granite granite granite granite sleeping sleeping dog dog.", Facts, Names, "art"));
            Assert.True(Creative.AutoScore("", Facts, Names) < -50);
            Assert.Equal(good, Creative.AutoScore(Good, Facts, Names, "art"));
        }

        [Fact]
        public async Task CreativeLogCapped()
        {
            var d = new Host();
            var w = Scripted(Enumerable.Repeat(Good, 250), new List<string>());
            for (int i = 0; i < 230; i++) await Go(d, w);
            Assert.Equal(200, d.CreativeLog.Count);
        }

        [Fact]
        public void PerKindDefaultsOffForEveryKind()
        {
            var d = new Host();
            foreach (var k in Kinds)
            {
                Assert.False(Creative.On(d, "ground", k)); Assert.False(Creative.On(d, "plan", k)); Assert.False(Creative.On(d, "best_of", k));
                Assert.Equal(1, Creative.BestOf(d, k));
            }
        }

        [Fact]
        public void PerKindTupleTurnsOnOnlyListedKinds()
        {
            var d = new Host { Cfg = new CreativeSettings { GroundFor = new[] { "art", "vow" }, PlanFor = new[] { "recap" }, BestOfFor = new[] { "lore" }, BestOfN = 4 } };
            Assert.Equal(new[] { true, false, false, false, true }, Kinds.Select(k => Creative.On(d, "ground", k)).ToArray());
            Assert.Equal(new[] { false, false, true, false, false }, Kinds.Select(k => Creative.On(d, "plan", k)).ToArray());
            Assert.Equal(new[] { 1, 4, 1, 1, 1 }, Kinds.Select(k => Creative.BestOf(d, k)).ToArray());
            Assert.Equal(new[] { false, true, false, false, false }, Kinds.Select(k => Creative.On(d, "best_of", k)).ToArray());
        }

        [Fact]
        public void GlobalFlagMeansAllKinds()
        {
            var d = new Host { Cfg = new CreativeSettings { Ground = true, Plan = true, BestOf = 3, BestOfFor = new[] { "art" }, BestOfN = 5 } };
            foreach (var k in Kinds) { Assert.True(Creative.On(d, "ground", k)); Assert.True(Creative.On(d, "plan", k)); Assert.True(Creative.On(d, "best_of", k)); }
            Assert.Equal(3, Creative.BestOf(d, "lore")); Assert.Equal(5, Creative.BestOf(d, "art"));
        }

        [Fact]
        public async Task ComposeUsesThePerKindBestOfAndPlan()
        {
            Func<Task<string>> plan = () => Task.FromResult("the dog and the wall");
            var d = new Host { Cfg = new CreativeSettings { BestOfFor = new[] { "art" }, BestOfN = 2, PlanFor = new[] { "art" } } };
            var (text, st) = await Go(d, Scripted(new[] { Good, Good }, new List<string>()), plan);
            Assert.Equal(2, st.Candidates); Assert.True(st.Plan.Length > 0); Assert.NotNull(text);
            var d2 = new Host { Cfg = new CreativeSettings { BestOfFor = new[] { "lore" }, BestOfN = 2, PlanFor = new[] { "lore" } } };
            var (_, st2) = await Go(d2, Scripted(new[] { Good, Good }, new List<string>()), plan);
            Assert.Equal(1, st2.Candidates); Assert.Equal("", st2.Plan);
        }
    }
}

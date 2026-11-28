using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class SalienceTests
    {
        private static SalientFact F(string kind, string key, double w, double age = 0, bool listener = false, bool social = false, string text = null)
        { return new SalientFact(kind, key, text ?? key + " text", w, age, listener, social); }

        private static SalienceAsk Ask(SaliencePurpose p = SaliencePurpose.Line, int min = 3, int max = 6, params string[] recent)
        { return new SalienceAsk { Purpose = p, Min = min, Max = max, RecentKeys = new HashSet<string>(recent) }; }

        [Fact]
        public void OrdersByScoreAndRecency()
        {
            var r = Salience.Pick(new[] { F("event", "old", 0.9, 12), F("injury", "now", 0.6), F("need", "mid", 0.5, 2) }, Ask(min: 3, max: 3));
            Assert.Equal(new[] { "now", "mid", "old" }, r.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void OneKindFirstThenFillByScore()
        {
            var facts = new[] { F("need", "n1", 0.9), F("need", "n2", 0.8), F("room", "r", 0.3), F("weather", "w", 0.2) };
            var r = Salience.Pick(facts, Ask(min: 1, max: 3));
            Assert.Equal(3, r.Count);
            Assert.Contains(r, x => x.Key == "r");
            Assert.Contains(r, x => x.Key == "w");
            Assert.DoesNotContain(r, x => x.Key == "n2");           // the second need loses to two other kinds
            Assert.Equal(4, Salience.Pick(facts, Ask(min: 1, max: 6)).Count);
        }

        [Fact]
        public void NoveltyPenalisesRecentKeys()
        {
            var facts = new[] { F("room", "a", 0.8), F("weather", "b", 0.6) };
            Assert.Equal("a", Salience.Pick(facts, Ask(min: 1, max: 2))[0].Key);
            Assert.Equal("b", Salience.Pick(facts, Ask(SaliencePurpose.Line, 1, 2, "a"))[0].Key);
        }

        [Fact]
        public void ListenerBonusOnlyForLines()
        {
            var facts = new[] { F("event", "e", 0.6), F("listener", "l", 0.5, listener: true) };
            Assert.Equal("l", Salience.Pick(facts, Ask(min: 1, max: 2))[0].Key);
            Assert.Equal("e", Salience.Pick(facts, Ask(SaliencePurpose.Thought, 1, 2))[0].Key);
        }

        [Fact]
        public void SocialDownWeightedForThoughtsAndDiariesUnlessAnOutcome()
        {
            var facts = new[] { F("social", "chat", 0.6, social: true), F("room", "r", 0.2), F("social", "insult", 0.8, social: true) };
            foreach (var p in new[] { SaliencePurpose.Thought, SaliencePurpose.Diary })
            {
                var r = Salience.Pick(facts, Ask(p, 1, 3));
                Assert.Equal("insult", r[0].Key);
                Assert.True(r.FindIndex(x => x.Key == "r") < r.FindIndex(x => x.Key == "chat") || r.All(x => x.Key != "chat"));
            }
            Assert.Equal("insult", Salience.Pick(facts, Ask(min: 1, max: 3))[0].Key);
            Assert.Equal("chat", Salience.Pick(facts, Ask(min: 1, max: 3))[1].Key);   // for a line a chat is fine
        }

        [Theory]
        [InlineData("The steel is worth a lot")]
        [InlineData("It costs 40 silver")]
        [InlineData("Market value of the rug")]
        [InlineData("An expensive bed")]
        public void PriceTalkNeverPicked(string text)
        {
            var r = Salience.Pick(new[] { F("event", "p", 1.0, text: text), F("room", "r", 0.4) }, Ask(min: 2, max: 6));
            Assert.Equal(new[] { "r" }, r.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void MinReachedBelowFloorMaxCaps()
        {
            var faint = new[] { F("a", "a", 0.31, 48), F("b", "b", 0.32, 48), F("c", "c", 0.33, 48), F("d", "d", 0.34, 48) };     // real but old
            Assert.Equal(3, Salience.Pick(faint, Ask()).Count);
            Assert.Single(Salience.Pick(faint, Ask(min: 1, max: 6)).Take(1));
            var strong = Enumerable.Range(0, 10).Select(i => F("k" + i, "s" + i, 0.5)).ToList();
            Assert.Equal(6, Salience.Pick(strong, Ask()).Count);
            Assert.Equal(2, Salience.Pick(strong, Ask(min: 1, max: 2)).Count);
        }

        [Fact]
        public void FloorDropsFaintFactsOnceMinIsMet()
        {
            var facts = new[] { F("a", "a", 0.9), F("b", "b", 0.8), F("c", "c", 0.7), F("d", "d", 0.01) };
            Assert.DoesNotContain(Salience.Pick(facts, Ask()), x => x.Key == "d");
        }

        [Fact]
        public void DeterministicWithStableTieBreakByKey()
        {
            var facts = new[] { F("a", "zeta", 0.5), F("b", "alpha", 0.5), F("c", "mid", 0.5) };
            var one = Salience.Pick(facts, Ask(min: 1, max: 3)).Select(x => x.Key).ToList();
            var two = Salience.Pick(facts.Reverse(), Ask(min: 1, max: 3)).Select(x => x.Key).ToList();
            Assert.Equal(new[] { "alpha", "mid", "zeta" }, one);
            Assert.Equal(one, two);
        }

        [Fact]
        public void DiaryRemembersLongerThanALine()
        {
            var facts = new[] { F("event", "old", 0.8, 8), F("room", "now", 0.3) };
            Assert.Equal("now", Salience.Pick(facts, Ask(SaliencePurpose.Line, 1, 2))[0].Key);
            Assert.Equal("old", Salience.Pick(facts, Ask(SaliencePurpose.Diary, 1, 2))[0].Key);
        }

        [Fact]
        public void EmptyAndNullInput()
        {
            Assert.Empty(Salience.Pick(null, Ask()));
            Assert.Empty(Salience.Pick(new SalientFact[0], Ask()));
            Assert.Empty(Salience.Pick(new[] { F("a", "a", 0.5) }, Ask(min: 1, max: 0)));
            Assert.Empty(Salience.Pick(new[] { F("a", "a", 0.0), F("b", "b", 0.5, text: " ") }, Ask()));
        }

        [Fact]
        public void DuplicateKeyKeepsTheBest()
        {
            var r = Salience.Pick(new[] { F("a", "same", 0.2), F("a", "same", 0.9) }, Ask(min: 1, max: 3));
            Assert.Single(r);
            Assert.Equal(0.9, r[0].Weight);
        }

        [Fact]
        public void FillerNeverPadsToMinButARealFactDoes()
        {
            // a quiet moment: a nice room, the morning, a clear sky are filler and are not pushed on the line to reach Min
            var quiet = new[] { F("room", "room:impressive", 0.15), F("time", "time", 0.1), F("weather", "weather", 0.1) };
            Assert.Empty(Salience.Pick(quiet, Ask(min: 3, max: 6)));
            // an old but real event still reaches Min
            var r = Salience.Pick(quiet.Concat(new[] { F("event", "raid", 0.8, 24) }), Ask(min: 1, max: 6));
            Assert.Equal(new[] { "raid" }, r.Select(x => x.Key).ToArray());
        }
    }
}

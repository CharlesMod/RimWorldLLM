// Port of agent/tests/test_concerns.py against InnerHost (the Python used the Director via the `world` fixture; here the events are fed to
// ConcernEngine.OnEvent directly and the daily upkeep is called by hand). Switch-off prefix tests and the reflection round trip through the
// Director are deferred; see docs/csmind/parity-inner.md.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class ConcernsTests
    {
        const long DAY = 60000;

        static void Love(InnerHost h, int holder = 1, int other = 2, int opinion = 60)
        {
            h.Minds[holder].Relations[other.ToString()] = new Relation { Name = h.Names[other], Opinion = opinion };
        }
        static void Step(InnerHost h, JObject e, int ticks = 0)
        {
            h.Fb.Tick += ticks; h.Sync();
            if (e != null) h.Concerns.OnEvent(h.Adapter.NormalizeEvent(e));
        }
        static JObject Hurt(InnerHost h, int victim = 2, double severity = 0.6, params int[] witnesses)
        {
            return h.Fb.AddEvent("injury", new[] { new KeyValuePair<int, string>(victim, "victim") }, "Bram is badly hurt.",
                witnesses.Length > 0 ? witnesses : new[] { 1, 2 }, severity);
        }
        static List<Concern> Open(InnerHost h, int id) { return ConcernOps.OpenConcerns(h.Minds[id]); }

        [Fact]
        public void InjuryOfALovedOneOpensAWorryAndAnInnerThought()
        {
            using var h = new InnerHost();
            Love(h); Step(h, Hurt(h));
            var cs = Open(h, 1);
            var c = Assert.Single(cs);
            Assert.Equal("hurt", c.Kind); Assert.Equal(new[] { 2 }, c.About); Assert.Equal("healed:2", c.ResolveHint);
            Assert.Contains("Bram", c.Text); Assert.Equal(0.6, c.Importance);
            Assert.Single(h.Minds[1].Inner.Where(t => t.Kind == "concern"));
            Assert.Empty(Open(h, 3));
        }

        [Fact]
        public void AMildInjuryOrAStrangerOpensNothing()
        {
            using var h = new InnerHost();
            Love(h, opinion: 5); Step(h, Hurt(h));
            Love(h, 3, 2, 80);
            Step(h, h.Fb.AddEvent("injury", new[] { new KeyValuePair<int, string>(2, "victim") }, "Bram is scratched.", new[] { 1, 2, 3 }, 0.2));
            Assert.Empty(Open(h, 1)); Assert.Empty(Open(h, 3));
        }

        [Fact]
        public void SameKindAndHintDoesNotOpenTwice()
        {
            using var h = new InnerHost();
            Love(h); Step(h, Hurt(h)); Step(h, Hurt(h));
            Assert.Single(Open(h, 1));
        }

        [Fact]
        public void FadeAfterDaysThenDroppedAfterTheKeepWindow()
        {
            using var h = new InnerHost();
            Love(h); Step(h, Hurt(h));
            Step(h, null, (int)(5.5 * DAY)); h.Concerns.Upkeep();
            Assert.Equal("faded", h.Minds[1].Concerns[0].Status);
            Step(h, null, (int)(1.5 * DAY)); h.Concerns.Upkeep();
            Assert.Empty(h.Minds[1].Concerns);
            Assert.Equal(1, h.Concerns.Faded);
        }

        static JObject Tend(InnerHost h)
        {
            return h.Fb.AddEvent("tend", new[] { new KeyValuePair<int, string>(2, "patient"), new KeyValuePair<int, string>(3, "doctor") },
                "Cy tended Bram.", new[] { 1, 2, 3 }, 0.1);
        }

        [Fact]
        public void TendResolvesWithAPayoffItemAChronicleNoteAndADayOnRecord()
        {
            using var h = new InnerHost();
            Love(h); Step(h, Hurt(h)); Step(h, Tend(h));
            var c = h.Minds[1].Concerns[0];
            Assert.Equal("resolved", c.Status); Assert.Contains("tended", c.Outcome);
            Assert.Equal(1, h.Concerns.Payoffs);
            var it = h.Leases.Queue.Last(q => q.ActorId == 1);
            Assert.Contains("Your worry is over", (string)it.Payload["trigger"]);
            Assert.Contains(h.Chronicle.State.Pending, n => n.Text.Contains("worry ended") && !n.Major);
        }

        [Fact]
        public void PayoffIsAReactiveSingleVoiceItem()
        {
            using var h = new InnerHost();
            Love(h); Step(h, Hurt(h));
            var c = Open(h, 1)[0];
            h.Concerns.Resolve(1, c, "he is fine", h.Bridge.Env.Game);
            var it = h.Leases.Queue.Last();
            Assert.Equal(Cls.Reactive, it.Cls); Assert.Equal("concern_resolved", it.Kind);
            Assert.True(it.Payload["conversation"] == null || !(bool)it.Payload["conversation"]);
            Assert.StartsWith("Your worry is over:", (string)it.Payload["trigger"]);
        }

        [Fact]
        public void DeathResolvesTheHurtWorryAndOpensGriefForOthers()
        {
            using var h = new InnerHost();
            Love(h); Love(h, 3, 2, 70);
            Step(h, Hurt(h));
            Step(h, h.Fb.AddEvent("death", new[] { new KeyValuePair<int, string>(2, "victim") }, "Bram died.", new[] { 1, 2, 3 }, 1.0));
            var hurt = h.Minds[1].Concerns.First(c => c.Kind == "hurt");
            Assert.Equal("resolved", hurt.Status); Assert.Contains("died", hurt.Outcome);
            Assert.Equal(new[] { "grief" }, Open(h, 3).Select(c => c.Kind));
            Assert.Equal(new[] { "grief" }, Open(h, 1).Select(c => c.Kind));
        }

        [Fact]
        public void RaidWorryEndsWithTheRaidLedgerEntry()
        {
            using var h = new InnerHost();
            Step(h, h.Fb.AddEvent("raid_start", new KeyValuePair<int, string>[0], "Raiders arrive.", new[] { 1, 2, 3 }, 0.7, "raid:1"));
            var mid = new[] { 1, 2, 3 }.Select(i => Open(h, i)).ToList();
            Step(h, h.Fb.AddEvent("raid_end", new KeyValuePair<int, string>[0], "The raid is over.", new[] { 1, 2, 3 }, 0.3, "raid:1"));
            var worried = new[] { 1, 2, 3 }.Where(i => mid[i - 1].Count > 0).ToList();
            Assert.Equal(2, worried.Count);
            Assert.All(worried, i => Assert.Equal("threat", mid[i - 1][0].Kind));
            Assert.All(new[] { 1, 2, 3 }, i => Assert.Empty(Open(h, i)));
            Assert.All(worried, i => Assert.Equal("resolved", h.Minds[i].Concerns[0].Status));
            Assert.Empty(h.Minds[new[] { 1, 2, 3 }.Except(worried).Single()].Concerns);
        }

        [Fact]
        public void BlockCShowsOneWorriesLineAndOnlyWhenThereAreSome()
        {
            using var h = new InnerHost();
            Love(h);
            var m = h.Minds[1];
            string c0 = PromptBlocks.BlockC(m);
            Assert.DoesNotContain("Worries", c0);
            Step(h, Hurt(h));
            string c1 = PromptBlocks.BlockC(m, worries: ConcernOps.WorryLines(m));
            Assert.Contains("Worries: I can't stop worrying about Bram; they are hurt.\n", c1);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(c1, "Worries").Count);
        }

        [Fact]
        public void SwitchOffMeansNoBirthAndNoInnerThought()
        {
            using var h = new InnerHost();
            h.Cfg.Concerns = false;
            Love(h); Step(h, Hurt(h));
            Assert.Empty(h.Minds[1].Concerns);
            Assert.DoesNotContain(h.Minds[1].Inner, t => t.Kind == "concern");
        }

        [Fact]
        public async Task ReflectionCanOpenOneWorryAndCloseListedOnes()
        {
            using var h = new InnerHost();
            Love(h);
            ConcernOps.AddConcern(h.Minds[1], "I worry about Bram.", new[] { 2 }, "hurt", "healed:2", 0.6, 1, 2);
            string old = h.Minds[1].Concerns[0].Id;
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string>
            {
                "{\"bio\": \"I am getting harder to rattle than I was before.\", \"wants\": [\"Rest\"], \"relations\": [], \"merge\": [], " +
                "\"concern\": \"I keep fearing the winter will break us.\", \"resolved\": [\"" + old + "\", \"w99\"]}"
            };
            var toks = h.Names.ToDictionary(kv => kv.Key, kv => (ISet<string>)new HashSet<string> { kv.Value });
            var res = await Reflection.RunReflectionAsync(llm, h.Adapter, h.Minds[1], h.Names, 1, 9000, DAY, nameTokens: toks, concerns: true);
            Assert.NotNull(res);
            h.Concerns.ApplyReflection(1, res, 9000);
            var cs = h.Minds[1].Concerns.ToDictionary(c => c.Id);
            Assert.Equal("resolved", cs[old].Status); Assert.Contains("behind you", cs[old].Outcome);
            var nw = cs.Values.Where(c => c.Kind == "reflection").ToList();
            Assert.Single(nw); Assert.Equal("open", nw[0].Status); Assert.Contains("winter", nw[0].Text);
            var call = llm.Calls.Single(c => c.Kind == "reflect");
            Assert.NotNull(call.JsonSchema["properties"]["concern"]);
            Assert.DoesNotContain(call.JsonSchema["required"], t => (string)t == "concern");
            Assert.Contains("YOUR WORRIES", call.Messages[1].Text);
        }
    }
}

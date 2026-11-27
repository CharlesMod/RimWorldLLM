// Port of agent/tests/test_wants.py (and test_wants_wiring.test_seek_out_reaches_the_fake...) against InnerHost: RunWantsAsync with a scripted
// tool backend. test_idle_window_schedules_wants_with_pacing and test_director_runs_a_wants_window_end_to_end need the Director/IdleScheduler
// and are deferred (docs/csmind/parity-inner.md); test_wants_wiring's CLI tests belong to the config/cli port.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class WantsTests
    {
        static string Call(string tool, params (string, object)[] a)
        {
            var args = new JObject();
            foreach (var (k, v) in a) args[k] = JToken.FromObject(v);
            return JsonConvert.SerializeObject(new JObject { ["tool"] = tool, ["args"] = args });
        }
        static readonly string DONE = "{\"done\": true}";

        static async Task<(WantsOutcome Out, FakeBackend Be)> Go(InnerHost h, object[] script, Dictionary<int, int> ages = null, JArray present = null)
        {
            if (ages != null) foreach (var kv in ages) { h.Fb.Pawns[kv.Key]["age"] = kv.Value; h.Ages[kv.Key] = kv.Value; }
            if (present != null)
                h.Fb.Scenes[1] = new JObject { ["room"] = new JObject { ["role"] = "Kitchen" }, ["time_of_day"] = "morning", ["weather"] = "clear", ["present"] = present };
            var be = new FakeBackend(script);
            var o = await WantsOps.RunWantsAsync(h, 1, be, "grammar");
            return (o, be);
        }

        [Fact]
        public async Task SeekOutGoesThroughTheBridge()
        {
            using var h = new InnerHost();
            var (o, _) = await Go(h, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "the spare medicine")), DONE });
            Assert.Equal(new[] { 2 }, h.Fb.SocialLog.Select(x => (int)x["target"]));
            Assert.Equal("seek_out", (string)h.Fb.SocialLog[0]["action"]); Assert.Equal("the spare medicine", (string)h.Fb.SocialLog[0]["topic"]);
            Assert.Equal(1, (int)h.Fb.SocialLog[0]["pawn"]);
            Assert.Equal(1, h.Wants.Acted); Assert.Equal("seek_out", o.Executed[0].Tool);
        }

        [Fact]
        public async Task AtMostOneSocialAction()
        {
            using var h = new InnerHost();
            var (o, _) = await Go(h, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "bread")), Call("avoid", ("target", "Cy")), DONE });
            Assert.Single(h.Fb.SocialLog);
            Assert.True(o.Executed.Any(e => e.Tool == "avoid" && e.Error != null) || o.Executed.Count == 1);
        }

        [Fact]
        public async Task RememberStoresAPrivateMemoryWithAScaledImportance()
        {
            using var h = new InnerHost();
            await Go(h, new object[] { Call("remember", ("fact", "Bram hides bread"), ("importance", "very important")), DONE });
            var m = h.Minds[1].Memories.Last();
            Assert.Equal("Bram hides bread", m.Text); Assert.Equal("private", m.Visibility); Assert.True(System.Math.Abs(m.Importance - 0.75) < 1e-6);
            Assert.Contains(1, h.Dirty); Assert.Empty(h.Fb.SocialLog);
        }

        [Fact]
        public async Task DoNothingAndGarbageBothEndInNothing()
        {
            using var h = new InnerHost();
            await Go(h, new object[] { Call("do_nothing"), DONE });
            Assert.Empty(h.Fb.SocialLog); Assert.Equal(1, h.Wants.Nothing);
            using var h2 = new InnerHost();
            var (o2, _) = await Go(h2, new object[] { "not json at all" });
            Assert.Empty(h2.Fb.SocialLog); Assert.Contains(o2.Outcome, new[] { "fallback", "gave_up", "final" });
        }

        [Fact]
        public async Task UnknownTargetIsRejectedByTheGrammarOrValidator()
        {
            using var h = new InnerHost();
            await Go(h, new object[] { Call("seek_out", ("target", "Nobody"), ("topic", "x")), DONE });
            Assert.Empty(h.Fb.SocialLog);
        }

        [Fact]
        public async Task NobodyPresentRemovesSocialTools()
        {
            using var h = new InnerHost();
            var (o, _) = await Go(h, new object[] { Call("do_nothing"), DONE }, present: new JArray());
            Assert.Equal(new[] { "do_nothing", "remember" }, o.Allowed.OrderBy(x => x));
        }

        [Fact]
        public async Task UnsupportedModOpIsACleanMiss()
        {
            using var h = new InnerHost();
            h.Fb.SocialSupported = false;
            var (o, _) = await Go(h, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "bread")), DONE });
            Assert.True(o.Unsupported); Assert.Equal(0, h.Wants.Acted); Assert.Empty(h.Fb.SocialLog);
        }

        [Fact]
        public async Task RomanticTopicIsRefusedForAMinorActorOrTarget()
        {
            using var h = new InnerHost();
            await Go(h, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "go kiss behind the barn")), DONE }, new Dictionary<int, int> { { 1, 15 } });
            Assert.Empty(h.Fb.SocialLog); Assert.True(h.Wants.Refused >= 1);
            using var h2 = new InnerHost();
            await Go(h2, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "to flirt with him")), DONE }, new Dictionary<int, int> { { 2, 16 } });
            Assert.Empty(h2.Fb.SocialLog); Assert.True(h2.Wants.Refused >= 1);
            using var h3 = new InnerHost();
            await Go(h3, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "to flirt with him")), DONE });
            Assert.Single(h3.Fb.SocialLog);
        }

        [Fact]
        public void RomanceToolsNeverAllowedWithAMinor()
        {
            using var h = new InnerHost();
            // the Director's tools_allowed is AgeGate.AllowedTools(names, adapter romantic set, minor); the stand-in romantic tool is seek_out
            var r = AgeGate.AllowedTools(new[] { "seek_out", "avoid", "remember" }, new[] { "seek_out" }, true);
            Assert.Equal(new[] { "avoid", "remember" }, r.OrderBy(x => x));
        }

        [Fact]
        public async Task PromptIsDelimitedDataAndNamesAreCleaned()
        {
            using var h = new InnerHost();
            var (_, be) = await Go(h, new object[] { Call("do_nothing"), DONE });
            string p = string.Join("\n", be.Calls[0].Messages.Select(m => (string)m["content"]));
            Assert.Contains("<game_data kind=\"present\">", p); Assert.Contains("<game_data kind=\"wants\">", p);
            Assert.Contains("game_data", p.Split(new[] { "=== YOUR WANTS" }, System.StringSplitOptions.None)[0]);
        }

        // test_concerns.test_wants_prompt_sees_the_worries
        [Fact]
        public async Task WantsPromptSeesTheWorries()
        {
            using var h = new InnerHost();
            h.Minds[1].Relations["2"] = new Relation { Name = "Bram", Opinion = 60 };
            ConcernOps.AddConcern(h.Minds[1], "I worry about Bram.", new[] { 2 }, "hurt", "healed:2", 0.6, 1, 2);
            var (_, be) = await Go(h, new object[] { Call("do_nothing"), DONE });
            string seen = string.Join("\n", be.Calls.SelectMany(c => c.Messages.Select(m => (string)m["content"])));
            Assert.Contains("YOUR WORRIES", seen); Assert.Contains("I worry about Bram.", seen);
        }

        // test_wants_wiring.test_seek_out_reaches_the_fake_with_everything_the_mod_needs
        [Fact]
        public async Task SeekOutReachesTheFakeWithEverythingTheModNeeds()
        {
            using var h = new InnerHost();
            await Go(h, new object[] { Call("seek_out", ("target", "Bram"), ("topic", "the bread")), DONE });
            var b = h.Fb.SocialLog.Single();
            foreach (var k in new[] { "pawn", "action", "target", "topic", "op_id" }) Assert.NotNull(b[k]);
        }
    }
}

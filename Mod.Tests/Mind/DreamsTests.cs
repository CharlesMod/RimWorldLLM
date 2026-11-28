// Ported from agent/tests/test_dreams.py (see ArtTests.cs for TaCtx/TaGolden). The Director.step + idle runner is stood in for by DreamStep
// (poll the events, DreamBook.Note them, run every due job with Dreams.RunDreamAsync on a lease).
// Deferred: test_dream_is_preempted_by_actor_work (Director/idle preemption by reactive actor work).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>A client that records the call parameters and can run a hook after the model answered; wraps a FakeLlm (use Inner for calls and script).</summary>
    internal sealed class SpyLlm : ILlmClient
    {
        public readonly FakeLlm Inner = new FakeLlm();
        public readonly List<(string Kind, int MaxTokens, IReadOnlyList<string> Stop, double? Temperature)> Params = new List<(string, int, IReadOnlyList<string>, double?)>();
        public Action After;

        public async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
            IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null, int? actor = null, JObject jsonSchema = null)
        {
            Params.Add((kind, maxTokens, stop, temperature));
            var r = await Inner.CompleteAsync(messages, slot, kind, maxTokens, stop, grammar, temperature, actor, jsonSchema);
            After?.Invoke();
            return r;
        }
    }

    public class DreamsTests
    {
        private const int Day = 60000;
        private const string Text = "I am chasing a berry bush through the kitchen";

        private static async Task DreamStep(TaCtx c)
        {
            var w = c.W;
            await c.FeedAsync();
            foreach (var job in w.DreamD.Due(w))
                await Dreams.RunDreamAsync(w, job, new Lease(1, job.Actor, Cls.Ambient, "dream", new Now(w.Bridge.Env.Game, 0.0), 0.0));
            await w.DrainAsync();
        }

        private static async Task Step2(TaCtx c) { await DreamStep(c); await DreamStep(c); }

        private static void Sleep(JobsWorld w, int aid = 1, int? game = null)
        {
            if (game != null) w.Fb.Tick = game.Value;
            w.Fb.Pawns[aid]["asleep"] = true;
            w.Fb.AddEvent("sleep_start", new[] { new KeyValuePair<int, string>(aid, "sleeper") }, "Ana fell asleep.", new[] { aid }, 0.1);
        }

        private static List<JObject> DreamLog(JobsWorld w) { return w.Fb.ThoughtLog.Where(t => (string)t["kind"] == "dream").ToList(); }

        private static async Task<TaCtx> Ready(double chance = 1.0, ILlmClient llm = null)
        {
            var c = await TaCtx.Go(); var w = c.W;
            if (llm != null) w.Llm = llm;
            w.Cfg.DreamChance = chance;
            ((FakeLlm)(llm is SpyLlm s ? s.Inner : w.Llm)).Script["dream"] = new List<string> { Text };
            TaCtx.Mem(w, 1, "I found a ripe berry bush.", 900);
            return c;
        }

        private static List<FakeCall> Calls(TaCtx c) { return c.Calls("dream"); }

        [Fact]
        public async Task SleepEventMakesOneDreamCloudAndInnerEntry()
        {
            var spy = new SpyLlm();
            using var c = await Ready(llm: spy); var w = c.W;
            Sleep(w);
            await Step2(c);
            var log = DreamLog(w);
            Assert.Single(log); Assert.Equal(1, (int)log[0]["pawn"]); Assert.Equal(Text, (string)log[0]["surface"]); Assert.Equal(Text, (string)log[0]["text"]);
            var p = spy.Params.First(x => x.Kind == "dream");
            Assert.Equal(30, p.MaxTokens); Assert.Equal(new[] { "\n" }, p.Stop.ToArray()); Assert.Equal(1.0, p.Temperature);
            Assert.Equal(new[] { "dream" }, w.MindsD[1].Inner.Select(t => t.Kind).ToArray()); Assert.Equal(Text, w.MindsD[1].Inner[0].Text);
            Assert.DoesNotContain(w.MindsD[1].Memories, m => m.Etype == "sleep_start");        // no memory of falling asleep
        }

        [Fact]
        public async Task OncePerNightThenAgainNextNight()
        {
            using var c = await Ready(); var w = c.W;
            Sleep(w); await Step2(c);
            w.Fake.Script["dream"] = new List<string> { "I am a fish in the cold storeroom" };
            w.Fb.Pawns[1]["asleep"] = false;
            Sleep(w, game: 3000); await Step2(c);                          // same night
            Assert.Single(DreamLog(w));
            TaCtx.Mem(w, 1, "I mended the roof.", Day + 1000);
            Sleep(w, game: Day + 2000); await Step2(c);                    // next night
            Assert.Equal(2, DreamLog(w).Count);
        }

        [Fact]
        public async Task ChanceZeroNeverDreamsAndTheRollIsSpent()
        {
            using var c = await Ready(chance: 0.0); var w = c.W;
            Sleep(w); await Step2(c);
            Assert.Empty(DreamLog(w)); Assert.Empty(Calls(c));
        }

        [Fact]
        public async Task ChanceIsAPerNightRoll()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.DreamChance = 0.5;
            int hits = 0;
            for (int n = 0; n < 40; n++)
            {
                var e = new JObject { ["type"] = "sleep_start", ["game"] = n * Day, ["actors"] = new JArray(new JObject { ["id"] = 1 }) };
                if (w.DreamD.Note(e, w, n * Day).Count > 0) hits++;
                w.DreamD.Pending.Clear();
            }
            Assert.InRange(hits, 8, 32);
        }

        [Fact]
        public async Task WokeBeforeReadyDropsIt()
        {
            using var c = await Ready(); var w = c.W;
            Sleep(w);
            w.Fb.Pawns[1]["asleep"] = false;
            await Step2(c);
            Assert.Empty(DreamLog(w)); Assert.Empty(Calls(c));
            Assert.Contains((1, "dream:woke"), w.DroppedD);
        }

        [Fact]
        public async Task WokeWhileWritingDropsIt()
        {
            var spy = new SpyLlm();
            using var c = await Ready(llm: spy); var w = c.W;
            Sleep(w);
            spy.After = () => w.Fb.Pawns[1]["asleep"] = false;
            await Step2(c);
            Assert.Empty(DreamLog(w)); Assert.Contains((1, "dream:woke"), w.DroppedD); Assert.Empty(w.MindsD[1].Inner);
        }

        [Fact]
        public async Task TooLateIsDropped()
        {
            using var c = await Ready(); var w = c.W;
            Sleep(w);
            w.Fb.Tick += w.Cfg.DreamMaxAgeGame + 10;
            await Step2(c);
            Assert.Empty(DreamLog(w)); Assert.Contains((1, "dream:late"), w.DroppedD);
        }

        [Fact]
        public async Task Switches()
        {
            using var c = await Ready(); var w = c.W;
            w.Cfg.Dreams = false;
            Sleep(w); await Step2(c);
            Assert.Empty(DreamLog(w));
        }

        [Fact]
        public async Task ModSwitches()
        {
            using var c = await Ready(); var w = c.W;
            w.BridgeCfg["dreams"] = false;
            Sleep(w); await Step2(c);
            Assert.Empty(DreamLog(w));
        }

        [Fact]
        public async Task NoMemoriesNoDream()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.DreamChance = 1.0;
            Sleep(w); await Step2(c);
            Assert.Empty(Calls(c)); Assert.Contains((1, "dream:no_memories"), w.DroppedD);
        }

        [Fact]
        public async Task PrivateMemoriesNeverReachADream()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.DreamChance = 1.0;
            TaCtx.Mem(w, 1, "I hid a secret from Bram.", 900, 0.3, "private");
            Sleep(w); await Step2(c);
            Assert.Empty(Calls(c));
        }

        [Fact]
        public async Task PromptWrapsGameTextAndUsesRecentInner()
        {
            using var c = await Ready(); var w = c.W;
            w.MindsD[1].Inner.Add(new InnerThought { Game = 800, Text = "I wonder about the roof", Kind = "musing" });
            w.MindsD[1].Inner.Add(new InnerThought { Game = 850, Text = "Bram never looks up", Kind = "aim" });
            Sleep(w); await Step2(c);
            var msgs = Calls(c)[0].Messages;
            string system = msgs[0].Text, user = msgs[1].Text;
            Assert.Contains(GameData.DataRule, system);
            Assert.Contains("ripe berry bush", user); Assert.Contains("Bram never looks up", user); Assert.Contains("I wonder about the roof", user);
            Assert.True(TaGolden.Count(user, "<<<") >= 2 || user.Contains("=== WHAT HAPPENED TODAY ==="));
            Assert.Contains("romantic", user.ToLowerInvariant());
            Assert.Contains("sexual", system);
        }

        [Fact]
        public async Task MinorDreamIsGatedAndGetsTheMinorRule()
        {
            using var c = await Ready(); var w = c.W;
            w.Ages[1] = 12;
            w.Fake.Script["dream"] = new List<string> { "I am kissing Bram under the stars tonight" };
            Sleep(w); await Step2(c);
            string sys = (Calls(c)[0].Messages[0].Text + Calls(c)[0].Messages[1].Text).ToLowerInvariant();   // the age rule now leads the request in the user message
            Assert.True(sys.Contains("minor") || sys.Contains("under 18"));
            Assert.Empty(DreamLog(w));
        }

        [Fact]
        public async Task RomanticDreamWithAMinorInMemoryIsDropped()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.DreamChance = 1.0;
            w.Ages[2] = 14;
            TaCtx.Mem(w, 1, "I talked with Bram by the fire.", 900, 0.3, "public", 2);
            w.Fake.Script["dream"] = new List<string> { "I am kissing Bram in a bed of roses" };
            Sleep(w); await Step2(c);
            Assert.Empty(DreamLog(w));
        }

        [Fact]
        public async Task DiaryPicksUpTheDreamStream()
        {
            using var c = await Ready(); var w = c.W;
            Sleep(w); await Step2(c);
            var m = w.MindsD[1];
            var stream = m.Inner.Where(t => FloorDiv(t.Game, Day) == FloorDiv(m.Inner[0].Game, Day)).ToList();       // inner.inner_on_day
            Assert.Equal(new[] { "dream" }, stream.Select(t => t.Kind).ToArray());
        }

        private static long FloorDiv(long a, long b) { return (long)Math.Floor((double)a / b); }

        // ---- extras: the pure helpers, with values computed by the Python ------------------------------------------------------------------------

        [Fact]
        public void RollAndNightMatchThePython()
        {
            Assert.Equal(0.023, Dreams.Roll(1, 0)); Assert.Equal(0.46, Dreams.Roll(1, 1)); Assert.Equal(0.478, Dreams.Roll(1, 2));
            Assert.Equal(0.56, Dreams.Roll(1, 5)); Assert.Equal(0.384, Dreams.Roll(1, 17)); Assert.Equal(0.463, Dreams.Roll(2, 3)); Assert.Equal(0.383, Dreams.Roll(3, 0));
            Assert.Equal(new[] { 0, 0, 1, 1, 1, 0 }, new[] { 0, 44999, 45000, 60000, 100000, 3000 }.Select(g => Dreams.NightOf(g, Day)).ToArray());
        }

        // ---- goldens ---------------------------------------------------------------------------------------------------------------------------------

        private static async Task Golden(string label, Action<JobsWorld> prep, string script)
        {
            var g = TaGolden.Load("dreams")[label];
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.DreamChance = 1.0;
            TaCtx.Mem(w, 1, "I found a ripe berry bush.", 900);
            prep(w);
            w.Fb.Pawns[1]["asleep"] = true;
            w.Fake.Script["dream"] = new List<string> { script };
            var job = new DreamJob(1, 900, Dreams.NightOf(900, Day));
            w.DreamD.Pending[1] = job;
            var lease = new Lease(1, 1, Cls.Ambient, "dream", new Now(1000, 0.0), 0.0);
            string r = await Dreams.RunDreamAsync(w, job, lease);
            var calls = c.Calls("dream");
            if (g["messages"].Type == JTokenType.Null) Assert.Empty(calls); else TaGolden.AssertMessages(g["messages"], calls[0].Messages);
            Assert.Equal(g["result"].Type == JTokenType.Null ? null : (string)g["result"], r);
            Assert.Equal(((JArray)g["dropped"]).Select(x => ((int)x[0], (string)x[1])).ToArray(), w.DroppedD.ToArray());
            Assert.Equal(((JArray)g["inner"]).Select(x => ((string)x[0], (string)x[1])).ToArray(), w.MindsD[1].Inner.Select(t => (t.Kind, t.Text)).ToArray());
            var th = DreamLog(w);
            var exp = (JArray)g["thoughts"];
            Assert.Equal(exp.Count, th.Count);
            for (int i = 0; i < th.Count; i++)
            {
                Assert.Equal((int)exp[i]["pawn"], (int)th[i]["pawn"]); Assert.Equal((string)exp[i]["text"], (string)th[i]["text"]); Assert.Equal((string)exp[i]["surface"], (string)th[i]["surface"]);
            }
        }

        [Fact]
        public Task GoldenBasicWithInnerStream() => Golden("dream_basic", w =>
        {
            w.MindsD[1].Inner.Add(new InnerThought { Game = 800, Text = "I wonder about the roof", Kind = "musing" });
            w.MindsD[1].Inner.Add(new InnerThought { Game = 850, Text = "Bram never looks up", Kind = "aim" });
        }, Text);

        [Fact]
        public Task GoldenPlain() => Golden("dream_plain", w => { }, Text);

        [Fact]
        public Task GoldenMinor() => Golden("dream_minor", w => { w.Ages[1] = 12; }, "I am kissing Bram under the stars tonight");

        [Fact]
        public Task GoldenMinorInMemory() => Golden("dream_minor_in_memory", w =>
        {
            w.Ages[2] = 14;
            TaCtx.Mem(w, 1, "I talked with Bram by the fire.", 905, 0.3, "public", 2);
        }, "I am kissing Bram in a bed of roses");

        [Fact]
        public Task GoldenPrivateIgnored() => Golden("dream_private", w => TaCtx.Mem(w, 1, "I hid a secret from Bram.", 901, 0.3, "private"), Text);
    }
}

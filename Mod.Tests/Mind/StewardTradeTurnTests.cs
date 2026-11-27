// Ported test-for-test from agent/tests/test_steward_tradeturn.py: a trader arrives (the vivarium's traders), one small turn per visit, skipped in a
// fight or when food is critical, never in advisor mode, with the trade view first and no caravan tools; the routine cycle is unchanged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class StewardTradeTurnTests
    {
        private static JObject Load(string n) => JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.steward_digest_" + n + ".json"));
        private static readonly JObject Trader = Load("trader"), Orbital = Load("orbital");
        private static readonly string Done = Rig.Done;
        private static string Call(string t, object a) => Rig.Call(t, a);
        private static string Final(string t) => Rig.Final(t);
        private static JObject Second => JObject.Parse(@"{""id"": ""P6000"", ""kind"": ""caravan"", ""name"": ""Cloth trader Mia"", ""faction"": ""Hill Union"", ""leaves_h"": 12, ""silver"": 900, ""can_trade"": true,
            ""sells"": [""cloth 100""], ""buys"": [""art""]}");

        private static JObject Fed(JObject d, double days = 12)
        {
            d = (JObject)d.DeepClone();
            d["food_days"] = days;
            if (d["facts"] is JObject f) f["food_days"] = days;
            return d;
        }

        private static Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld world, object[] script, bool playing = true, double gap = 0.0)
            => Rig.Boot(world, script, playing, c => c.StewardMinGapRealS = gap);

        private static Task Cycle(JobsWorld w) => Rig.Cycle(w);

        /// <summary>The trade turns only: the first routine look (heartbeat) may follow a trade turn, which does not push it back.</summary>
        private static List<string> Reasons(Steward st) => st.Stats.Trace.Select(t => (string)t["reason"]).Where(r => r == "trade" || r == "chat").ToList();

        [Fact]
        public async Task WakesOncePerVisitAndAgainForANewTrader()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            Assert.Equal(new[] { "trade" }, Reasons(st)); Assert.True(st.TradeSeen.SetEquals(new[] { "P5521" }));
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new[] { "trade" }, Reasons(st));                                  // the same trader: no second turn
            var d = Fed(Trader);
            ((JArray)d["trade"]["traders"]).Add(Second);
            w.Fb.Digest = d;
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new[] { "trade", "trade" }, Reasons(st)); Assert.True(st.TradeSeen.SetEquals(new[] { "P5521", "P6000" }));
            Assert.Contains("Cloth trader Mia (P6000)", Rig.User(fake.Calls.Last()).Split(new[] { "=== YOUR PLAN" }, StringSplitOptions.None)[0]);
            st.Reset();                                                                    // a new save: the visit counts again
            Assert.Empty(st.TradeSeen);
        }

        [Fact]
        public async Task TheTradeTurnRespectsTheMinGapAndWaitsForChatAndEvents()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") }, gap: 20.0);
            st.LastReal = w.Clock();
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            Assert.Empty(Reasons(st));
            w.Real += 30;
            Rig.Chat(w, "hello");
            await Cycle(w);
            Assert.Equal(new[] { "chat" }, Reasons(st));                                   // chat first; the trade turn comes at the next step
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new[] { "chat", "trade" }, Reasons(st));
        }

        private static void StartFight(JobsWorld w)
        {
            w.Fb.Combat = CombatData.View("pirate_ranged");
            w.Fb.Digest = CombatData.Digest("pirate_ranged");
            w.Fb.CombatSince = w.Fb.Tick;
        }

        [Fact]
        public async Task NoTradeTurnWakesInAFightAndATurnRunInOneIsSkipped()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            w.Fb.Digest = Fed(Trader);
            StartFight(w);
            var d = Fed(Trader);
            d["combat"] = CombatData.Digests["pirate_ranged"]["combat"]?.DeepClone();
            w.Fb.Digest = d;
            await Cycle(w);
            Assert.Equal(new[] { "combat started" }, st.Stats.Trace.Select(t => (string)t["reason"]).ToList());   // the fight owns the cadence: no trade turn
            Assert.Empty(st.TradeSeen);
            var t2 = new JObject();
            await st.RunTradeAsync(t2);                                                    // even called directly it skips
            Assert.Equal("a fight is on", (string)t2["skipped"]);
        }

        [Fact]
        public async Task ATradeTurnDueBeforeTheFightIsSkipped()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            w.Fb.Digest = Fed(Trader);
            w.Fb.Combat = CombatData.View("pirate_ranged");
            st.Vv = JObject.Parse(@"{""traders"": [{""id"": ""P5521""}], ""combat"": {""active"": true}}");
            st.TradeNew = new List<string> { "P5521" };
            var t = new JObject();
            await st.RunTradeAsync(t);
            Assert.Equal("a fight is on", (string)t["skipped"]);
        }

        [Fact]
        public async Task SkippedWhenFoodIsCriticalAndStillMarkedSeen()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            w.Fb.Digest = Fed(Trader, 2.4);                                                // the trader sample itself is under 3 days
            await Cycle(w);
            Assert.Equal(new[] { "trade" }, Reasons(st)); Assert.StartsWith("food is critical", (string)st.Stats.Trace.Last()["skipped"]);
            Assert.True(st.TradeSeen.SetEquals(new[] { "P5521" })); Assert.Empty(fake.Calls);
            w.Fb.Digest = Fed(Trader, 12);
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new[] { "trade" }, Reasons(st));                                  // the visit is over for this trader
        }

        [Fact]
        public async Task SkippedOnACriticalStarvationAlert()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            var d = Fed(Trader);
            d["alerts"] = JArray.Parse(@"[{""alert"": ""Alert_Starvation"", ""label"": ""Starvation"", ""critical"": true}]");
            w.Fb.Digest = d;
            await Cycle(w);
            Assert.Equal("a critical starvation alert", (string)st.Stats.Trace.Last()["skipped"]); Assert.Empty(fake.Calls);
        }

        [Fact]
        public async Task NoTradeTurnInAdvisorMode()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") }, playing: false);
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            Assert.Empty(Reasons(st)); Assert.Empty(fake.Calls);
        }

        [Fact]
        public async Task TheTradeTurnPromptIsSmallWithTheTradeViewFirst()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("trade_offer", new { with = "P5521" }), Call("trade", new { with = "P5521", buy = "steel x10", why = "since steel is low" }), Final("done") });
            w.Fb.GoalsBoard["plan"] = "Get components.";
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            var c = fake.Calls[0];
            string system = Rig.System(c), user = Rig.User(c);
            Assert.StartsWith("=== THE TRADE ===", user);
            Assert.True(user.IndexOf("ARRIVED: Bulk goods trader Tomas (P5521)") < user.IndexOf("TRADE: silver"));
            Assert.True(user.IndexOf("TRADERS") < user.IndexOf("FOOD: about 12 days") && user.IndexOf("FOOD: about 12 days") < user.IndexOf("=== YOUR PLAN ===")); Assert.Contains("Get components.", user);
            Assert.Contains("NEEDS:", user); Assert.Contains("STOCK:", user); Assert.Contains("NEVER SELL:", user);
            Assert.DoesNotContain("SETTLEMENTS", user); Assert.DoesNotContain("QUESTS", user); Assert.DoesNotContain("THE STATE OF THE GAME", user);
            Assert.True(system.Length < 6000 && (system.Length + user.Length) / 4.0 < 3500);
            foreach (var s in new[] { "trade_offer(", "trade(", "do_nothing", "note(" }) Assert.Contains(s, system);
            Assert.True(system.IndexOf("trade_offer(") < system.IndexOf("note(") && system.IndexOf("trade(") < system.IndexOf("do_nothing"));
            foreach (var gone in new[] { "caravan", "set_time(", "set_work(", "build(", "squad(", "add_goal(", "set_plan(", "answer_letter(", "choose(" }) Assert.DoesNotContain(gone, system);
            Assert.True(Steward.TradeMaxTokens == 300 && Steward.TradeFinalMax == 120);
            Assert.Equal(300, fake.Calls.Max(x => x.MaxTokens));
            Assert.True(Steward.TradeSteps == 4 && Steward.TradeBudgetS == 60.0 && Steward.TradeStepTimeoutS == 30.0);
            Assert.Single(w.Fb.Looks); Assert.Equal("/trade/offer", w.Fb.Looks[0].Key); Assert.Equal("P5521", w.Fb.Looks[0].Value["with"]); Assert.Single(w.Fb.Looks[0].Value);
            Assert.Equal(new[] { "trade" }, w.Fb.ActLog.Select(a => a.Key).ToArray());
            Assert.True(JToken.DeepEquals(JToken.Parse(@"[{""item"": ""steel"", ""count"": 10}]"), w.Fb.ActLog[0].Value["buy"]));
            Assert.Empty((JArray)w.Fb.GoalsBoard["log"]);                                  // no board edit, no log line
            Assert.DoesNotContain(w.Fb.Chat, e => (string)e["who"] == "narrator");         // no closing chat line
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(StewardResources.Text(StewardResources.TradeName)) < 1600);
        }

        [Fact]
        public async Task TheTradeTurnRunsAtMostFourStepsAndOnlyItsTools()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var script = new object[] { Call("trade_offer", new { with = "P5521" }), Call("set_time", new { pause = "no", speed = "leave", emergency = "no" }),
                Call("trade_offer", new { with = "P5521" }), Call("trade_offer", new { with = "P5521" }), Call("trade_offer", new { with = "P5521" }) };
            var (w, st, fake) = await Boot(world, script);
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            Assert.True(fake.Calls.Count <= Steward.TradeSteps + 1);
            Assert.DoesNotContain(w.Fb.ActLog, a => a.Key == "time");
            Assert.NotEmpty((JArray)st.Stats.Trace.Last()["failures"]);                    // set_time is not a trade tool: rejected
        }

        [Fact]
        public async Task TheRoutineCycleKeepsItsStepsAndIsNotPushedBack()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("done") });
            Assert.True(Steward.Steps == 8 && Steward.BudgetS == 120.0 && Steward.StepTimeoutS == 60.0 && Steward.FinalMax == 300);
            w.Fb.Digest = Fed(Trader);
            await Cycle(w);
            Assert.Equal(new[] { "trade" }, st.Stats.Trace.Select(t => (string)t["reason"]).ToList()); Assert.Null(st.LastGame);   // a trade turn leaves the heartbeat clock alone
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new[] { "trade", "heartbeat" }, st.Stats.Trace.Select(t => (string)t["reason"]).ToList());
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(Rig.User(fake.Calls.Last()), "=== THE STATE OF THE GAME ===").Count);
        }

        [Fact]
        public async Task StewardTryTradeTurnOnTheOrbitalTrader()
        {
            var be = new StewardFake(new object[] { Call("trade_offer", new { with = "S31" }), Final("done") });
            var o = await RimWorldLLM.Harness.StewardTry.TryOnceAsync(Fed(Orbital), null, "", true, "http://x", mode: "grammar", makeBackend: _ => be, tradeTurn: true);
            Assert.Equal("trade", (string)o.Trace["reason"]); Assert.Equal("trade_offer", (string)o.Trace["executed"][0][0]);
            Assert.StartsWith("=== THE TRADE ===", (string)o.Messages[1]["content"]);
            Assert.Equal(0, RimWorldLLM.Harness.StewardTry.Report(o, new StringWriter()));
        }
    }
}

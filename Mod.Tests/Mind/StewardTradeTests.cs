// Ported from agent/tests/test_steward_trade.py (trade only; the keeper decided NO CARAVANS, so the caravan and caravan_plan
// tests are dropped) and the growth-moment cases of test_steward_dialogs.py. The digest-line, digest-cap, rules-fact and prompt-text
// tests of that file belong to the digest/rules/resources port (worker B); see docs/csmind/parity-delta3-c.md.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class StewardTradeTests
    {
        private static JObject Load(string n) => JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.steward_digest_" + n + ".json"));
        private static readonly JObject Sample = StewardTestData.Sample, Trader = Load("trader"), Orbital = Load("orbital"), Settle = Load("settlement");
        private static readonly JObject Caravan = JObject.Parse(@"{""id"": ""C88"", ""name"": ""Caravan Ana"", ""pawns"": [""Ana"", ""Bram""], ""animals"": 1, ""at"": ""Rivermouth"", ""to"": ""home"",
            ""days_left"": 2.4, ""food_days"": 6.5, ""mass"": ""120/210"", ""danger"": ""none"", ""purpose"": ""trade"", ""can_trade"": ""T4321"", ""narrator"": true}");
        private static readonly JObject BuildBlock = (JObject)Sample["build"];

        private static JObject Clone(JObject o) => (JObject)o.DeepClone();

        private static StewardSession Session(JObject d, IBridge bridge = null, bool advisor = false) => new StewardKit().Session(d, bridge, advisor);
        private static ToolSpec Tool(StewardSession s, string name) => s.Tools.First(t => t.Name == name);
        private static List<string> Choices(StewardSession s, string name, string param) => Tool(s, name).Params.First(p => p.Name == param).Choices;
        private static JObject A(object o) => JObject.FromObject(o);

        private static JObject Call(StewardSession s, string name, object args)
        {
            var (v, err) = Tool(s, name).CheckArgs(A(args), false);
            Assert.Null(err);
            return v;
        }

        private static async Task<Outcome> Do(StewardSession s, string name, object args) => await s.RunAsync(name, Call(s, name, args));

        private static (StewardSession, FakeBridge) Live(JObject d)
        {
            var fb = new FakeBridge { Digest = d };
            return (Session(d, FakeBridge.Client(fb)), fb);
        }

        // the settlement digest with a caravan of ours sitting at it (only to make that settlement a trade partner)
        private static JObject AtSettlement()
        {
            var d = Clone(Settle);
            d["caravans"] = new JArray(Clone(Caravan));
            ((JObject)d["trade"])["comms"] = "ready";
            return d;
        }

        // ---- presence and enums
        [Fact]
        public void NoTradeBlockNoTradeTools()
        {
            var s = Session(Sample);
            var names = s.Tools.Select(t => t.Name).ToList();
            Assert.Empty(new[] { "trade_offer", "trade", "caravan_plan", "caravan" }.Intersect(names));
            Assert.Empty(new[] { "trade", "caravan" }.Intersect(s.Acting));
        }

        [Fact]
        public void TraderDigestHasTradeToolsAndActingSplit()
        {
            var s = Session(Trader);
            var names = s.Tools.Select(t => t.Name).ToList();
            Assert.Contains("trade_offer", names); Assert.Contains("trade", names);
            Assert.DoesNotContain("caravan", names);                  // no caravan tools at all (keeper decision)
            Assert.Contains("trade", s.Acting); Assert.DoesNotContain("trade_offer", s.Acting);
            Assert.Equal(new[] { "P5521" }, Choices(s, "trade", "with"));
        }

        [Fact]
        public void OrbitalTraderAndSettlementDigest()
        {
            Assert.Equal(new[] { "S31" }, Choices(Session(Orbital), "trade", "with"));
            var names = Session(Settle).Tools.Select(t => t.Name).ToList();
            Assert.DoesNotContain("trade", names);                    // no trader, no sitting caravan
            Assert.Empty(new[] { "caravan", "caravan_plan" }.Intersect(names));
        }

        [Fact]
        public void NoDigestOffersACaravanTool()
        {
            var merged = Clone(Trader);
            foreach (var k in new[] { "quests" }) merged[k] = Settle[k].DeepClone();
            foreach (var d in new[] { Sample, Trader, Orbital, Settle, AtSettlement(), merged })
                foreach (bool adv in new[] { false, true })
                {
                    var s = Session(d, null, adv);
                    Assert.Empty(s.Tools.Select(t => t.Name).Intersect(new[] { "caravan", "caravan_plan" }));
                    Assert.DoesNotContain("caravan", s.Acting);
                }
        }

        [Fact]
        public void ACaravanAtASettlementIsNoTradePartner()
            => Assert.DoesNotContain("trade", Session(AtSettlement()).Tools.Select(t => t.Name));          // T-tile trades need a caravan, which is off

        [Fact]
        public async Task TheNarratorCaravanRouteIsRefused()
        {
            var fb = new FakeBridge { Digest = Clone(Trader) };
            var br = FakeBridge.Client(fb);
            var e = await Assert.ThrowsAsync<BridgeError>(() => br.ActAsync("caravan", JObject.Parse(@"{""op"": ""form"", ""pawns"": [101], ""to"": ""T4321""}")));
            Assert.Contains("caravans are off", e.Message);
            Assert.Empty(fb.ActLog);
        }

        // ---- bodies
        private static void LastIs(FakeBridge fb, string verb, string json)
        {
            var last = fb.ActLog.Last();
            Assert.Equal(verb, last.Key);
            Assert.True(JToken.DeepEquals(JObject.Parse(json), last.Value), last.Value.ToString());
        }

        [Fact]
        public async Task TradePostsParsedLinesAndWhy()
        {
            var (s, fb) = Live(Trader);
            var r = await Do(s, "trade", new { with = "P5521", buy = "components x10, 3 herbal medicine", sell = "cloth 200; jade sculpture x1", why = "since components are low" });
            Assert.True(r.Ok); Assert.Equal("trade", r.Verb);
            LastIs(fb, "trade", @"{""with"": ""P5521"", ""buy"": [{""item"": ""components"", ""count"": 10}, {""item"": ""herbal medicine"", ""count"": 3}],
                ""sell"": [{""item"": ""cloth"", ""count"": 200}, {""item"": ""jade sculpture"", ""count"": 1}], ""why"": ""since components are low""}");
            Assert.Equal(new[] { "with", "buy", "sell", "why" }, fb.ActLog.Last().Value.Properties().Select(p => p.Name).ToArray());
        }

        [Fact]
        public async Task TradeOneSideAndNoneWords()
        {
            var (s, fb) = Live(Trader);
            await Do(s, "trade", new { with = "P5521", buy = "none", sell = "cloth x50" });
            LastIs(fb, "trade", @"{""with"": ""P5521"", ""buy"": [], ""sell"": [{""item"": ""cloth"", ""count"": 50}]}");
        }

        private static string Lines(string text) => string.Join(";", TradeKit.ParseLines(text, "buy").Select(x => (string)x["item"] + ":" + (long)x["count"]));

        [Fact]
        public void ParseLinesFormsAndErrors()
        {
            Assert.Equal("steel:400", Lines("steel x400"));
            Assert.Equal("cloth:30", Lines("20 cloth, cloth x5, Cloth 5"));                    // merged
            Assert.Equal("cloth:20;steel:4", Lines("20x cloth and 4 steel"));
            foreach (var bad in new[] { "cloth", "x", "steel x0", "steel x1000000" })
                Assert.Throws<RimWorldLLM.Mind.Py.PyValueError>(() => TradeKit.ParseLines(bad, "buy"));
        }

        // expected values captured from the Python parse_lines (csmind/delta3/c/capture.py)
        [Theory]
        [InlineData("cloth ×20", "cloth:20")]
        [InlineData("Steel X 5", "steel x:5")]
        [InlineData("band 3, 2 sandbands", "band:3;sandbands:2")]
        [InlineData("20", "0:2")]
        [InlineData("  ", "")]
        [InlineData("3 x", "x:3")]
        [InlineData("steel x2, steel 3, 4 steel", "steel:9")]
        [InlineData("MEDICINE x  5.", "medicine:5")]
        [InlineData("5x5", "5:5")]
        [InlineData("\u0663 cloth", "cloth:3")]
        public void ParseLinesMatchesPython(string text, string want) => Assert.Equal(want, Lines(text));

        [Theory]
        [InlineData("7.", "buy: give a count for 7; write items like: cloth x20, steel x100")]
        [InlineData("x20 cloth", "buy: give a count for x20 cloth; write items like: cloth x20, steel x100")]
        [InlineData("cloth 20x", "buy: give a count for cloth 20x; write items like: cloth x20, steel x100")]
        [InlineData("100000 steel", "buy: 100000 steel needs an item name and a count from 1 to 99999")]
        public void ParseLinesErrorsMatchPython(string text, string msg)
            => Assert.Equal(msg, Assert.Throws<RimWorldLLM.Mind.Py.PyValueError>(() => TradeKit.ParseLines(text, "buy")).Message);

        [Fact]
        public async Task TradeErrors()
        {
            var (s, fb) = Live(Trader);
            int n = fb.ActLog.Count;
            Assert.Contains("give buy or sell", (string)(await Do(s, "trade", new { with = "P5521" })).Result["error"]);
            Assert.Contains("give a count", (string)(await Do(s, "trade", new { with = "P5521", buy = "cloth" })).Result["error"]);
            Assert.Contains("at most 12", (string)(await Do(s, "trade", new { with = "P5521", buy = string.Join(", ", Enumerable.Range(0, 13).Select(i => "item" + i + " x1")) })).Result["error"]);
            Assert.Equal(n, fb.ActLog.Count);
        }

        [Fact]
        public async Task TradeWithATraderThatCannotTradeIsExplained()
        {
            var d = Clone(Trader);
            var t0 = (JObject)((JArray)d["trade"]["traders"])[0];
            t0["can_trade"] = false; t0["why_not"] = "no comms console";
            var (s, fb) = Live(d);
            var r = await Do(s, "trade", new { with = "P5521", buy = "steel x5" });
            Assert.Contains("no comms console", (string)r.Result["error"]);
            Assert.Empty(fb.ActLog);
        }

        [Fact]
        public async Task RefusalPassesThrough()
        {
            var (s, fb) = Live(Trader);
            fb.ActRefuse["trade"] = "can't sell medicine: the colony keeps 10";
            var r = await Do(s, "trade", new { with = "P5521", sell = "herbal medicine x5" });
            Assert.True(JToken.DeepEquals(JObject.Parse(@"{""refused"": ""can't sell medicine: the colony keeps 10""}"), r.Result));
            Assert.False(r.Ok);
        }

        [Fact]
        public async Task ActExtraFieldsReachTheModel()
        {
            var (s, fb) = Live(Trader);
            fb.ActExtra["trade"] = JObject.Parse(@"{""spent"": 300, ""got"": 200, ""net"": -100}");
            var r = await Do(s, "trade", new { with = "P5521", buy = "steel x10" });
            Assert.Equal(300, (int)r.Result["spent"]);
            Assert.Equal("walking", (string)r.Result["status"]);
            Assert.Equal("Ana is walking to Bulk goods trader Tomas to trade", (string)r.Result["done"]);
        }

        [Fact]
        public async Task ATradeWithATraderWalksAndASecondIsRefusedUntilItEnds()
        {
            var (s, fb) = Live(Trader);
            var r = await Do(s, "trade", new { with = "P5521", buy = "steel x10" });
            Assert.True(r.Ok); Assert.Equal("walking", (string)r.Result["status"]); Assert.Equal("Ana", (string)r.Result["negotiator"]);
            Assert.Equal("Bulk goods trader Tomas", (string)r.Result["to"]);
            Assert.Equal("Ana is walking to Bulk goods trader Tomas to trade", r.Note);
            var again = await Do(s, "trade", new { with = "P5521", sell = "cloth x10" });
            Assert.False(again.Ok); Assert.Contains("a trade is already under way", (string)again.Result["refused"]);
            Assert.Single(fb.ActLog);
            Assert.True(JToken.DeepEquals(JObject.Parse(@"{""negotiator"": ""Ana"", ""to"": ""Bulk goods trader Tomas"", ""with"": ""P5521""}"), fb.TradePending));
            fb.TradePending = null;
            Assert.True((await Do(s, "trade", new { with = "P5521", sell = "cloth x10" })).Ok);
        }

        [Fact]
        public async Task TheFakeDigestShowsThePendingTradeAndVivariumListsTraders()
        {
            var fb = new FakeBridge { Digest = Clone(Trader) };
            var br = FakeBridge.Client(fb);
            Assert.True(JToken.DeepEquals(JArray.Parse(@"[{""id"": ""P5521"", ""kind"": ""caravan"", ""name"": ""Bulk goods trader Tomas""}]"), (await br.VivariumAsync())["traders"]));
            Assert.Equal(JTokenType.Null, (await br.DigestAsync())["trade"]["pending"].Type);
            await br.ActAsync("trade", JObject.Parse(@"{""with"": ""P5521"", ""buy"": [{""item"": ""steel"", ""count"": 5}], ""sell"": []}"));
            Assert.Equal("Ana", (string)(await br.DigestAsync())["trade"]["pending"]["negotiator"]);
        }

        // ---- the look tools
        [Fact]
        public async Task LookToolsRenderAndAreNotActing()
        {
            var (s, fb) = Live(Trader);
            var r = await Do(s, "trade_offer", new { with = "P5521" });
            Assert.False(r.Ok); Assert.False(r.Result.ContainsKey("done")); Assert.Empty(fb.ActLog);
            string text = (string)r.Result["result"];
            Assert.StartsWith("Bulk goods trader Tomas sells: steel 400 @2", text);
            Assert.Contains("buys of ours: cloth 300 @1", text);
            Assert.Contains("trader silver 1800", text); Assert.True(text.Length <= 1200);
            Assert.Single(fb.Looks);
            Assert.Equal("/trade/offer", fb.Looks[0].Key);
            Assert.Equal(new Dictionary<string, string> { ["with"] = "P5521" }, fb.Looks[0].Value);
        }

        [Fact]
        public async Task LookGameErrorIsAToolError()
        {
            var (s, fb) = Live(Trader);
            fb.Digest = new JObject();                              // the fake now knows no trader
            var r = await Do(s, "trade_offer", new { with = "P5521" });
            Assert.True(r.Result.ContainsKey("error")); Assert.False(r.Ok);
        }

        [Fact]
        public void RenderOfferFormsAndCap()
        {
            var r = JObject.Parse(@"{""name"": ""Tomas"", ""buys"": [{""item"": ""steel"", ""count"": 400, ""price"": 2.1}, {""item"": ""cloth"", ""count"": ""?""}, 5],
                ""sells"": [{""item"": ""cloth"", ""count"": 300, ""price"": 1.1}], ""silver"": 1800.5, ""negotiator"": ""Ana""}");
            Assert.Equal("Tomas sells: steel 400 @2.1, cloth ?; buys of ours: cloth 300 @1.1; trader silver 1800.5; negotiator Ana", TradeKit.RenderOffer(r));
            Assert.Equal("P1 sells: nothing; buys of ours: nothing", TradeKit.RenderOffer(JObject.Parse(@"{""with"": ""P1""}")));
            var big = new JObject { ["name"] = "T", ["buys"] = new JArray(Enumerable.Range(0, 30).Select(i => (object)new JObject { ["item"] = "advanced component number long " + i, ["count"] = 100 + i, ["price"] = 2.0 })) };
            string t = TradeKit.RenderOffer(big);
            Assert.Equal(1177, t.Length);                              // captured from the Python
            Assert.EndsWith("long 26 126 @2, advanced component number long 27 127 @2 ...", t);
        }

        // ---- advisor mode
        [Fact]
        public void AdvisorOneTaskCountsTrade()
        {
            var s = Session(Trader, advisor: true);
            Assert.Contains("trade", s.Acting); Assert.DoesNotContain("trade_offer", s.Acting);
        }

        [Fact]
        public void AdvisorNoteNoLongerNamesTrade()
        {
            Assert.DoesNotContain("trade", StewardKit.AdvisorNote);
            Assert.Contains("no tool for", StewardKit.AdvisorNote);
        }

        // ---- budget
        [Fact]
        public void ToolSectionBudget()
        {
            var d = Clone(Trader);
            d["build"] = BuildBlock.DeepClone();
            var s = Session(d);
            var names = s.Tools.Select(t => t.Name).ToList();
            foreach (var n in new[] { "trade_offer", "trade", "build", "bill" }) Assert.Contains(n, names);
            int len = Prompt.RenderTools(s.Tools).Length;
            Assert.True(len < 11000, len.ToString());
            Assert.True(Prompt.RenderTools(Session(Sample).Tools).Length < len);   // Python compares against TRADER (no caravan tools here, so the same set)
        }

        // ---- grammar and signatures
        [Fact]
        public void SignaturesAndGrammar()
        {
            var s = Session(Trader);
            Assert.Equal("trade(with, buy?, sell?, why?)", Tool(s, "trade").Signature());
            Assert.Equal("trade_offer(with)", Tool(s, "trade_offer").Signature());
            string g = Grammar.Build(s.Tools);
            Assert.Contains("trade_offer", g); Assert.Contains("\\\"buy\\\":", g); Assert.StartsWith("root ::=", g);
            var full = A(new { with = "P5521", buy = "cloth x5", sell = "none", why = "since" });
            Assert.Null(Tool(s, "trade").CheckArgs(full, false).error);                // grammar: every parameter written
            Assert.Null(Tool(s, "trade").CheckArgs(A(new { with = "P5521", sell = "cloth x5" }), false).error);   // json/native: omitted
        }

        // ---- whole cycle (scripted model)
        [Fact]
        public async Task CycleLooksThenTradesAndAdvisorAllowsOneAct()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world, new object[]
            {
                WRig.Call("trade_offer", new { with = "P5521" }),
                WRig.Call("trade", new { with = "P5521", sell = "cloth x100", why = "since cloth piles up" }),
                WRig.Call("trade", new { with = "P5521", buy = "steel x10" }),
                WRig.Final("Sold the cloth."),
            });
            w.Fb.Digest = Clone(Trader);
            w.Fb.Playing = false;                                  // advisor mode: the look does not use up the one task
            WRig.Chat(w, "sell some cloth to the trader");
            await WRig.Cycle(w);
            Assert.Equal(new[] { "trade" }, w.Fb.ActLog.Select(a => a.Key).ToArray());
            Assert.Equal("/trade/offer", w.Fb.Looks[0].Key);
            Assert.Equal("P5521", w.Fb.Looks[0].Value["with"]);
            Assert.Equal(100, (int)w.Fb.ActLog[0].Value["sell"][0]["count"]);
            Assert.Contains("one task only", WRig.Json(fake.Calls[3]));
            Assert.Contains("trader silver 1800", WRig.Json(fake.Calls[1]));                  // the look's text went back to the model
        }

        // ---- digest lines, rules, steps and prompt text (the changed parts of test_steward_trade.py)
        [Fact]
        public void QuestLinesHaveNoCaravansOrSettlementsBlock()
        {
            var d = AtSettlement();
            var q = (JObject)((JArray)d["quests"])[0];
            q["can"] = false; q["why_not"] = "needs a caravan (caravans are off)";
            string text = StewardDigest.RenderDigest(d);
            Assert.DoesNotContain("SETTLEMENTS", text); Assert.DoesNotContain("Rivermouth (Hill Union", text);
            Assert.DoesNotContain("CARAVANS", text); Assert.DoesNotContain("Caravan Ana", text);
            Assert.Contains("- Q77 [active] delivery: 20 cloth to Rivermouth, reward 600 silver, expires in 240 h, 3.1 days away, can't: needs a caravan (caravans are off)", text);
            Assert.DoesNotContain("CANNOT:", text); Assert.Contains("decline one that can't", text);
            Assert.Contains("- Q77", StewardDigest.RenderDigest(Settle)); Assert.Contains(", can", StewardDigest.RenderDigest(Settle));
        }

        [Fact]
        public void APendingTradeIsShownInTheTradeLine()
        {
            var d = Clone(Trader);
            ((JObject)d["trade"])["pending"] = JObject.Parse(@"{""negotiator"": ""Ana"", ""to"": ""the comms console"", ""with"": ""Bulk goods trader Tomas""}");
            Assert.Contains("a trade is under way: Ana is walking to the comms console to trade with Bulk goods trader Tomas", StewardDigest.RenderDigest(d));
            ((JObject)d["trade"])["pending"] = JValue.CreateNull();
            Assert.DoesNotContain("under way", StewardDigest.RenderDigest(d));
        }

        [Fact]
        public void StepsOrderShrinksBuildThenBioThenTradeThenBase()
        {
            var s = StewardDigest.StepsForTest;
            Assert.Equal(new[] { 0, 0, 0, 0 }, s[0]); Assert.Equal(3, s[s.Length - 1][0]);       // (base, build, trade, bio)
            foreach (int i in new[] { 1, 2, 3 }) Assert.Equal(s.Select(x => x[i]).OrderBy(x => x).ToArray(), s.Select(x => x[i]).ToArray());
            int firstBase = System.Array.FindIndex(s, x => x[0] > 0);
            Assert.All(s.Skip(firstBase), x => Assert.True(x[1] == 3 && x[2] == 3 && x[3] == 3));
            Assert.Equal(3, s[firstBase - 1][2]);
        }

        [Fact]
        public void AQuestThatCannotIsDeclinedByARule()
        {
            var d = Clone(Settle);
            var q = (JObject)((JArray)d["quests"])[0];
            q["state"] = "offer"; q["can"] = false; q["why_not"] = "needs a caravan (caravans are off)";
            var f = StewardRules.DeriveFacts(d);
            Assert.Equal(true, f["quest_offer_cannot"]); Assert.Equal(false, f["quest_offer_can"]);
            f["food_days"] = 12L; f["colonists_downed"] = 0L; f["colonists_sick"] = 0L;
            var ids = StewardRules.FiredRules(f).Select(r => r.Id).ToList();
            Assert.Contains(ids, i => i.Contains("growth.quest_decline"));
            Assert.DoesNotContain(ids, i => i.Contains("growth.quest_accept"));
            Assert.DoesNotContain(ids, i => i.Contains("caravan_safety"));
            q["state"] = "active";
            Assert.Equal(false, StewardRules.DeriveFacts(d)["quest_offer_cannot"]);
        }

        [Fact]
        public void PromptTextIsAmendedNotAppended()
        {
            var kit = new StewardKit();
            string sys = kit.SystemPrompt(false);
            Assert.Contains("trade_offer and trade", sys); Assert.Contains("Caravans are off", sys); Assert.Contains("declined with choose", sys);
            Assert.DoesNotContain("caravan_plan", sys); Assert.DoesNotContain("likewise trade and caravans", sys);
            string strat = kit.DefaultStrategy();
            Assert.DoesNotContain("caravan_plan", strat); Assert.DoesNotContain("Caravans:", strat); Assert.DoesNotContain("trade, caravans", strat);
            Assert.Contains("## Trade and quests", strat); Assert.Contains("declined with choose", strat); Assert.Contains("500 silver", strat);
            Assert.Contains("trade_offer before every trade", strat);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(strat) <= 12288);
        }

        // ---- growth moments (test_steward_dialogs.py)
        private static JObject GrowthDigest() => new JObject { ["dialogs"] = JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.dialogs_growth.json"))["dialogs"].DeepClone() };

        [Fact]
        public async Task GrowthRenderAndTool()
        {
            // the digest-line part of the Python test (render_digest on the growth dialog) is worker B's renderer; see the parity notes
            var d = GrowthDigest();
            var fb = new FakeBridge { Digest = new JObject() };
            fb.Dialogs.AddRange(((JArray)d["dialogs"]).Cast<JObject>());
            var s = Session(d, FakeBridge.Client(fb));
            var spec = Tool(s, "choose");
            Assert.True(new[] { "passions", "trait" }.All(n => spec.Params.Any(p => p.Name == n)));
            var r = await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", passions = "Shooting, melee", trait = "Tough" }));
            Assert.True(r.Ok, r.Result.ToString());
            LastIs(fb, "choose", @"{""dialog"": ""L5120"", ""passions"": [""Shooting"", ""melee""], ""trait"": ""Tough""}");
            Assert.Contains("give passions", (string)(await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", trait = "Tough" }))).Result["error"]);
            Assert.Contains("give trait", (string)(await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", passions = "Melee" }))).Result["error"]);
            fb.Dialogs.AddRange(((JArray)d["dialogs"]).Cast<JObject>());                          // the first answer closed it
            var r2 = await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", option = "0" }));
            Assert.True(r2.Ok, r2.Result.ToString());
            LastIs(fb, "choose", @"{""dialog"": ""L5120"", ""option"": 0}");                       // first choices still allowed
        }

        [Fact]
        public async Task GrowthErrorTextsNameWhatIsOffered()
        {
            var d = GrowthDigest();
            var s = Session(d, FakeBridge.Client(new FakeBridge()));
            Assert.Equal("give passions (up to 2, comma separated) from: Shooting, Melee, Artistic", (string)(await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", trait = "Tough" }))).Result["error"]);
            Assert.Equal("give trait, one of: Bookworm, Tough", (string)(await s.RunAsync("choose", Call(s, "choose", new { dialog = "L5120", passions = "Melee" }))).Result["error"]);
        }
    }
}

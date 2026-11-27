using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using System.Collections.Generic;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>trade2: caravans off for the narrator, the quest caravan verdict, walking trades (decision table, the route, the ticker's
    /// orchestration, saved state), the traders list and the trade camera scene.</summary>
    public class VivariumTradeWalkTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        int op;

        public VivariumTradeWalkTests() { VivariumRoutes.Register(H.Router, () => V, () => Hands); }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "w" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void Refused(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }

        static readonly object Deal = new { with = "S31", buy = new[] { new { item = "component", count = 10 } }, sell = new[] { new { item = "cloth", count = 200 } }, why = "need parts" };

        PendingTrade Pending(string kind = PendingTrade.ToConsole)
        {
            return new PendingTrade
            {
                With = "S31", Buy = { new DealLine("component", 10) }, Sell = { new DealLine("cloth", 200) }, Why = "need parts", OpId = "w1",
                NegotiatorId = 7, Negotiator = "Ana", TargetKind = kind, Target = kind == PendingTrade.ToConsole ? "the comms console" : "Tomas",
                Trader = "the bulk goods trader", StartTick = 1000, DeadlineTick = 3500
            };
        }

        void WalkStarts()
        {
            Hands.StartNext = HandResult.Done("Ana is walking to the comms console to trade with the bulk goods trader",
                new JObject { ["status"] = "walking", ["negotiator"] = "Ana", ["to"] = "the comms console" });
            Hands.StartPending = Pending();
        }

        // ------------------------------------------------------------ caravans off for the narrator
        [Fact]
        public void CaravanPostRefusedWithoutScript()
        {
            var body = new { op = "form", pawns = new[] { 3 }, to = "T4" };
            Refused(Post("/act/caravan", body), "caravans are off");
            Assert.Equal("caravans are off", (string)Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4", script = false }).Body["detail"]);
            Assert.Equal("couldn't send a caravan to T4: caravans are off", V.Chat.Entries.Last().Text);
            Assert.DoesNotContain("caravan form", Hands.Calls);
            Assert.True(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4", script = true }).Ok);
            Assert.Contains("caravan form", Hands.Calls);
        }

        [Fact]
        public void CaravanScriptStillObeysTheSetting()
        {
            V.Settings.Caravans = false;
            Refused(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4", script = true }), "caravans are off in the settings");
        }

        [Fact]
        public void CaravanPlanGetRefusedWithoutScript()
        {
            Refused(H.Call("GET", "/caravan/plan?pawns=3&to=T4", null), "caravans are off");
            Refused(H.Call("GET", "/caravan/plan?pawns=3&to=T4&script=false", null), "caravans are off");
            Assert.Empty(Hands.Calls);
            Assert.True(H.Call("GET", "/caravan/plan?pawns=3&to=T4&script=true", null).Ok);
            Assert.Contains("caravanplan", Hands.Calls);
        }

        // ------------------------------------------------------------ quest caravan verdict
        [Theory]
        [InlineData("site")]
        [InlineData("delivery")]
        public void QuestsNeedingACaravanAreOff(string kind)
        {
            var r = QuestPure.Feasible(kind, 0, 1000, 1, 10, 20, 1, false, true);
            Assert.False(r.can); Assert.Equal("needs a caravan (caravans are off)", r.whyNot);
            Assert.True(QuestPure.Feasible(kind, 0, 1000, 1, 10, 20, 1).can);   // default: unchanged
        }

        [Fact]
        public void QuestCaravanReasonComesFirstOnlyForSiteAndDelivery()
        {
            Assert.Equal("needs a caravan (caravans are off)", QuestPure.Feasible("site", 9999, 1000, 1, 0, 0, 0, false, true).whyNot);
            Assert.Equal("needs combat (not yet)", QuestPure.Feasible("combat", 0, 1000, 1, 10, 20, 1, false, true).whyNot);
            Assert.True(QuestPure.Feasible("hosting", 0, 1000, 1, 10, 20, 1, false, true).can);
            Assert.True(QuestPure.Feasible("other", 0, 1000, 1, 10, 20, 1, false, true).can);
        }

        // ------------------------------------------------------------ TradeWalk.Decide
        static WalkObs Obs(int tick = 1500, bool arrived = false, bool job = true, bool ok = true, bool reach = true, bool trader = true)
        {
            return new WalkObs { Tick = tick, TraderPresent = trader, NegotiatorOk = ok, NegotiatorWhy = ok ? null : "was downed", CanReach = reach, Arrived = arrived, HasOurJob = job };
        }

        [Fact]
        public void DecideWalksWaitsRunsAndGivesUp()
        {
            var p = Pending();
            Assert.Equal(WalkStep.Wait, TradeWalk.Decide(p, Obs()).Step);
            Assert.Equal(WalkStep.Run, TradeWalk.Decide(p, Obs(arrived: true)).Step);
            var gone = TradeWalk.Decide(p, Obs(trader: false));
            Assert.Equal(WalkStep.Refuse, gone.Step); Assert.Equal("the trader has left", gone.Reason);
            var down = TradeWalk.Decide(p, Obs(ok: false));
            Assert.Equal(WalkStep.Instant, down.Step); Assert.Equal("Ana couldn't get there: was downed", down.Reason);
            var stuck = TradeWalk.Decide(p, Obs(reach: false));
            Assert.Equal(WalkStep.Instant, stuck.Step); Assert.StartsWith("Ana couldn't get there", stuck.Reason);
            var late = TradeWalk.Decide(p, Obs(tick: 3500));
            Assert.Equal(WalkStep.Instant, late.Step); Assert.Equal("Ana took too long", late.Reason);
            Assert.Equal(WalkStep.Wait, TradeWalk.Decide(p, Obs(tick: 3499)).Step);
            Assert.Equal(WalkStep.Refuse, TradeWalk.Decide(p, Obs(trader: false, arrived: true)).Step);   // gone beats arrived
        }

        [Fact]
        public void DecideReissuesAnInterruptedJobThreeTimesThenGoesInstant()
        {
            var p = Pending();
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(WalkStep.ReissueJob, TradeWalk.Decide(p, Obs(job: false)).Step);
                p.Reissues++;
            }
            var d = TradeWalk.Decide(p, Obs(job: false));
            Assert.Equal(WalkStep.Instant, d.Step); Assert.Contains("couldn't get there", d.Reason);
        }

        [Fact]
        public void DecideUsesTheGameReasonWhenTheTraderIsUnavailable()
        {
            var o = Obs(trader: false); o.TraderWhy = "the console has no power";
            Assert.Equal("the console has no power", TradeWalk.Decide(Pending(), o).Reason);
        }

        // ------------------------------------------------------------ saved state
        [Fact]
        public void PendingTradeRoundTripsThroughTheVivarium()
        {
            V.TradeWalk = Pending(PendingTrade.ToTrader); V.TradeWalk.Reissues = 2;
            var saved = JObject.Parse(V.Export().ToString());
            var v2 = new Vivarium(); v2.Import(saved);
            var p = v2.TradeWalk;
            Assert.NotNull(p);
            Assert.Equal("S31", p.With); Assert.Equal("component", p.Buy[0].Item); Assert.Equal(200, p.Sell[0].Count);
            Assert.Equal("need parts", p.Why); Assert.Equal("w1", p.OpId); Assert.Equal(7, p.NegotiatorId); Assert.Equal("Ana", p.Negotiator);
            Assert.Equal(PendingTrade.ToTrader, p.TargetKind); Assert.Equal("Tomas", p.Target); Assert.Equal("the bulk goods trader", p.Trader);
            Assert.Equal(1000, p.StartTick); Assert.Equal(3500, p.DeadlineTick); Assert.Equal(2, p.Reissues);
            Assert.Equal("trade_walk", V.Export().Properties().Single(x => x.Name == "trade_walk").Name);
        }

        [Fact]
        public void OldSavesWithoutATradeWalkLoadAndClearDropsIt()
        {
            var saved = new Vivarium().Export(); saved.Remove("trade_walk");
            V.TradeWalk = Pending();
            V.Import(saved);
            Assert.Null(V.TradeWalk);
            V.TradeWalk = Pending(); V.Clear();
            Assert.Null(V.TradeWalk);
            Assert.Equal(JTokenType.Null, new Vivarium().Export()["trade_walk"].Type);
        }

        [Fact]
        public void PendingTradeShowsInTheDigestShape()
        {
            var j = Pending().DigestJson();
            Assert.Equal("Ana", (string)j["negotiator"]); Assert.Equal("the comms console", (string)j["to"]); Assert.Equal("S31", (string)j["with"]);
        }

        // ------------------------------------------------------------ the route
        [Fact]
        public void WalkStartRepliesWalkingWithNoGreentextAndNoDayCharge()
        {
            WalkStarts();
            int actions = V.Actions.LastSeq;
            var r = Post("/act/trade", Deal);
            Assert.True(r.Ok, r.Body?.ToString());
            Assert.Equal("Ana is walking to the comms console to trade with the bulk goods trader", (string)r.Body["done"]);
            Assert.Equal("walking", (string)r.Body["status"]); Assert.Equal("Ana", (string)r.Body["negotiator"]); Assert.Equal("the comms console", (string)r.Body["to"]);
            Assert.Equal(actions, V.Actions.LastSeq);
            Assert.DoesNotContain(V.Chat.Entries, e => e.Who == ChatKinds.Action);
            Assert.Equal(ChatKinds.Note, V.Chat.Entries.Last().Who);
            Assert.Equal(0, V.TradeDay.Deals);
            Assert.DoesNotContain("trade", Hands.Calls);
            Assert.NotNull(V.TradeWalk);
            Assert.Equal("need parts", V.TradeWalk.Why); Assert.Equal("w1", V.TradeWalk.OpId);
        }

        [Fact]
        public void ASecondTradeIsRefusedWhileOneIsUnderWayButSettlementsStayInstant()
        {
            WalkStarts();
            Assert.True(Post("/act/trade", Deal).Ok);
            Hands.Calls.Clear();
            Refused(Post("/act/trade", new { with = "P5", buy = new[] { new { item = "steel", count = 1 } } }), "a trade is already under way: Ana is walking to the comms console");
            Assert.Empty(Hands.Calls);
            Assert.StartsWith("couldn't trade with P5", V.Chat.Entries.Last().Text);
            Assert.True(Post("/act/trade", new { with = "T9", buy = new[] { new { item = "steel", count = 1 } } }).Ok);
            Assert.Contains("trade", Hands.Calls); Assert.Equal(1, V.TradeDay.Deals);
        }

        [Fact]
        public void AHandThatCannotWalkFallsBackToTheInstantTrade()
        {
            Hands.StartNext = null; Hands.StartCannot = "no path to the console";
            var r = Post("/act/trade", Deal);
            Assert.True(r.Ok);
            Assert.Equal("bought 2 steel (traded over the radio: no path to the console)", (string)r.Body["done"]);
            Assert.Equal(1, V.TradeDay.Deals); Assert.Null(V.TradeWalk);
            Assert.Equal(ChatKinds.Action, V.Chat.Entries.Last().Who);
        }

        [Fact]
        public void AStartRefusalIsAnOrdinaryRefusal()
        {
            Hands.StartNext = HandResult.No("the trader does not buy cloth");
            Refused(Post("/act/trade", Deal), "does not buy cloth");
            Assert.Null(V.TradeWalk); Assert.Equal(0, V.TradeDay.Deals);
        }

        // ------------------------------------------------------------ the ticker's orchestration
        string StepOnce(int tick, WalkObs o)
        {
            Hands.ObsNext = o;
            return TradeWalk.Step(V, tick, Hands, Hands.TradeObserve, Hands.TradeReissue);
        }

        [Fact]
        public void ArrivalRunsTheDealPostsTheGreentextAndChargesTheDay()
        {
            V.TradeWalk = Pending();
            Hands.TradeNext = HandResult.Done("bought 10 components, sold 200 cloth (+120 silver), Ana negotiated",
                new JObject { ["spent"] = 40, ["got"] = 120, ["deal"] = "bought 10 components, sold 200 cloth (+120 silver)" });
            Assert.Equal("waiting", StepOnce(1500, Obs()));
            Assert.Equal(0, V.Actions.LastSeq);
            Assert.Equal("ran", StepOnce(1600, Obs(arrived: true)));
            Assert.Equal(7, Hands.LastTrade.Negotiator); Assert.Equal("S31", Hands.LastTrade.With); Assert.Equal(10, Hands.LastTrade.Buy[0].Count);
            Assert.Null(V.TradeWalk);
            Assert.Equal(1, V.TradeDay.Deals); Assert.Equal(40, V.TradeDay.Spent); Assert.Equal(120, V.TradeDay.Got);
            var e = V.Chat.Entries.Last();
            Assert.Equal(ChatKinds.Action, e.Who);
            Assert.Equal("Ana walked to the comms console and bought 10 components, sold 200 cloth (+120 silver), since need parts", e.Text);
            Assert.Equal(1, V.Actions.LastSeq);
        }

        [Fact]
        public void TheDealIsCheckedAgainWhenItRunsAndARefusalClearsThePending()
        {
            V.TradeWalk = Pending();
            Hands.TradeNext = HandResult.No("the trader no longer has components");
            Assert.Equal("refused", StepOnce(1600, Obs(arrived: true)));
            Assert.Null(V.TradeWalk); Assert.Equal(0, V.TradeDay.Deals); Assert.Equal(0, V.Actions.LastSeq);
            Assert.Equal(ChatKinds.Refused, V.Chat.Entries.Last().Who);
            Assert.Equal("couldn't trade with the bulk goods trader: the trader no longer has components", V.Chat.Entries.Last().Text);
        }

        [Fact]
        public void NoDealsLeftTodayRefusesWithoutRunningTheHand()
        {
            V.TradeWalk = Pending(); V.Settings.TradeDealsPerDay = 1;
            V.TradeDay.Roll(1600); V.TradeDay.Charge(1, 1);
            Assert.Equal("refused", StepOnce(1600, Obs(arrived: true)));
            Assert.DoesNotContain("trade", Hands.Calls);
            Assert.Contains("made its 1 deals", V.Chat.Entries.Last().Text);
        }

        [Fact]
        public void TimeoutRunsTheDealOverTheRadioWithTheReason()
        {
            V.TradeWalk = Pending();
            Assert.Equal("instant", StepOnce(3500, Obs(tick: 3500)));
            Assert.Null(V.TradeWalk);
            Assert.Equal("bought 2 steel (Ana took too long; traded over the radio), since need parts", V.Chat.Entries.Last().Text);
            Assert.Equal(1, V.TradeDay.Deals);
        }

        [Fact]
        public void TraderGoneRefusesAndNegotiatorTroubleGoesInstant()
        {
            V.TradeWalk = Pending();
            Assert.Equal("refused", StepOnce(1600, Obs(trader: false)));
            Assert.Equal("couldn't trade with the bulk goods trader: the trader has left", V.Chat.Entries.Last().Text);
            Assert.Null(V.TradeWalk); Assert.DoesNotContain("trade", Hands.Calls);
            V.TradeWalk = Pending();
            Assert.Equal("instant", StepOnce(1600, Obs(ok: false)));
            Assert.Contains("Ana couldn't get there: was downed; traded over the radio", V.Chat.Entries.Last().Text);
        }

        [Fact]
        public void AnInterruptedWalkIsReissuedThreeTimesThenGoesInstant()
        {
            V.TradeWalk = Pending();
            for (int i = 1; i <= 3; i++)
            {
                Assert.Equal("reissued", StepOnce(1500 + i, Obs(job: false)));
                Assert.Equal(i, V.TradeWalk.Reissues);
            }
            Assert.Equal(3, Hands.Reissued);
            Assert.Equal("instant", StepOnce(1600, Obs(job: false)));
            Assert.Null(V.TradeWalk);
        }

        [Fact]
        public void NothingPendingDoesNothing()
        {
            Assert.Null(StepOnce(1500, Obs()));
            Assert.Empty(Hands.Calls);
        }

        // ------------------------------------------------------------ traders in GET /vivarium
        [Fact]
        public void VivariumStatusListsTradersThatCanTradeNow()
        {
            Assert.Empty((JArray)H.Call("GET", "/vivarium", null).Body["traders"]);
            V.Traders.Set(new[] { new TraderStub("P5521", "caravan", "Tomas"), new TraderStub("S31", "orbital", "Bulk Goods Co") });
            var t = (JArray)H.Call("GET", "/vivarium", null).Body["traders"];
            Assert.Equal(2, t.Count);
            Assert.Equal("P5521", (string)t[0]["id"]); Assert.Equal("caravan", (string)t[0]["kind"]); Assert.Equal("Tomas", (string)t[0]["name"]);
            Assert.Equal("S31", (string)t[1]["id"]); Assert.Equal("orbital", (string)t[1]["kind"]);
            Assert.Null(V.Export()["traders"]);   // runtime only: never saved
            V.Clear();
            Assert.Empty((JArray)H.Call("GET", "/vivarium", null).Body["traders"]);
        }

        // ------------------------------------------------------------ trade camera scene
        [Fact]
        public void TheNegotiatorGetsATradeScene()
        {
            var pawns = new List<ScenePawn>
            {
                new ScenePawn { Id = 7, Name = "Ana", Job = "Goto", TradeWith = "the bulk goods trader", X = 10, Z = 12 },
                new ScenePawn { Id = 8, Name = "Bram", Job = "Wait", X = 40, Z = 40 },
            };
            var s = SceneFinder.Find(pawns, 12);
            var c = s.Single(x => x.Kind == SceneKinds.Trade);
            Assert.Equal("Camera: Ana trades with the bulk goods trader", c.Caption);
            Assert.Equal(ShotStyle.Follow, c.Style); Assert.Equal(7, c.FollowId); Assert.Equal("scene_trade:7", c.Key);
            Assert.Equal(SceneKinds.GroupWork, SceneKinds.Group(SceneKinds.Trade));
            Assert.Contains(SceneKinds.Trade, SceneKinds.All);
            Assert.True(SceneKinds.IsScene(SceneKinds.Trade));
            Assert.Empty(SceneFinder.Find(pawns, 12, new[] { SceneKinds.GroupMeals }));
            Assert.Single(SceneFinder.Find(pawns, 12, new[] { SceneKinds.GroupWork }));
        }

        [Fact]
        public void TheTradeSceneIsModestAndBelowEventsAndBattles()
        {
            double w = WatchKinds.Weight(SceneKinds.Trade);
            Assert.Equal(2.5, w);
            Assert.True(w < WatchKinds.Weight(WatchKinds.Combat) && w < WatchKinds.Weight(WatchKinds.Party) && w < WatchKinds.Weight(SceneKinds.Medical));
        }
    }
}

using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>Phase 4 routes: shapes (400), settings gates and day caps (409 with a refused chat line, no hand), what the hands get,
    /// TradeDay charged from Extra, caravan plans stored.</summary>
    public class VivariumTradeRoutesTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        int op;

        public VivariumTradeRoutesTests() { VivariumRoutes.Register(H.Router, () => V, () => Hands); }

        ApiResult Post(string path, object body)
        {
            var j = body is string s ? JObject.Parse(s) : JObject.FromObject(body);
            j["op_id"] = "t" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void Bad(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Equal(400, r.Status);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }
        void Refused(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }
        string LastRefusedLine() { var e = V.Chat.Entries.LastOrDefault(); return e != null && e.Who == ChatKinds.Refused ? e.Text : null; }

        // ------------------------------------------------------------ settings
        [Fact]
        public void SettingsDefaultsAndJson()
        {
            var s = V.Settings;
            Assert.True(s.Trade); Assert.True(s.Caravans);
            Assert.Equal(2000, s.TradeMaxDeal); Assert.Equal(6000, s.TradeMaxDay); Assert.Equal(3, s.TradeDealsPerDay);
            Assert.Equal(10, s.KeepMedicine); Assert.Equal(10, s.KeepFoodDays); Assert.Equal(3, s.CaravanMaxPawns); Assert.Empty(s.NeverSell);
            s.NeverSell.Add("jade");
            var j = H.Call("GET", "/vivarium", null).Body["settings"];
            Assert.True((bool)j["trade"]); Assert.True((bool)j["caravans"]); Assert.Equal(2000, (int)j["trade_max_deal"]); Assert.Equal(6000, (int)j["trade_max_day"]);
            Assert.Equal(3, (int)j["trade_deals_per_day"]); Assert.Equal(10, (int)j["keep_medicine"]); Assert.Equal(10, (int)j["keep_food_days"]);
            Assert.Equal("jade", (string)j["never_sell"][0]); Assert.Equal(3, (int)j["caravan_max_pawns"]);
            var l = s.Limits(); Assert.Equal(2000, l.MaxDeal); Assert.Equal("jade", l.NeverSell[0]);
            l.NeverSell.Clear(); Assert.Single(s.NeverSell);   // a copy
        }

        // ------------------------------------------------------------ GET /trade/offer
        [Fact]
        public void OfferNeedsAGoodIdAndGame()
        {
            Bad(H.Call("GET", "/trade/offer", null), "with must be a trader id");
            Bad(H.Call("GET", "/trade/offer?with=zzz", null), "with must be a trader id");
            Assert.Equal("no_game", H.Call("GET", "/trade/offer?with=P1", null, GameState.Menu).Error);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void OfferPassesLimitsAndAddsToday()
        {
            V.Settings.NeverSell.Add("jade"); V.TradeDay.Roll(H.State.Tick); V.TradeDay.Charge(300, 200);
            var r = H.Call("GET", "/trade/offer?with=P5521", null);
            Assert.True(r.Ok);
            Assert.Equal("P5521", Hands.LastWith); Assert.Contains("jade", Hands.LastLimits.NeverSell);
            Assert.Equal(1, (int)r.Body["today"]["deals"]); Assert.Equal(2, (int)r.Body["today"]["deals_left"]); Assert.Equal(300, (int)r.Body["today"]["spent"]);
            Hands.OfferJson = new JObject { ["today"] = new JObject { ["deals"] = 9 } };
            Assert.Equal(9, (int)H.Call("GET", "/trade/offer?with=S31", null).Body["today"]["deals"]);
        }

        [Fact]
        public void OfferRefusalsAndSettingGate()
        {
            Hands.OfferJson = new JObject { ["refused"] = "no comms console" };
            Refused(H.Call("GET", "/trade/offer?with=P1", null), "no comms console");
            V.Settings.Trade = false; Hands.Calls.Clear();
            Refused(H.Call("GET", "/trade/offer?with=P1", null), "trading is off in the settings");
            Assert.Empty(Hands.Calls);
        }

        // ------------------------------------------------------------ POST /act/trade
        [Theory]
        [InlineData("{}", "with is required")]
        [InlineData("{\"with\":\"X9\",\"buy\":[{\"item\":\"a\",\"count\":1}]}", "with must be a trader id")]
        [InlineData("{\"with\":\"P1\"}", "at least one line")]
        [InlineData("{\"with\":\"P1\",\"buy\":[]}", "at least one line")]
        [InlineData("{\"with\":\"P1\",\"buy\":\"steel\"}", "buy must be an array")]
        [InlineData("{\"with\":\"P1\",\"buy\":[3]}", "buy[0] must be an object")]
        [InlineData("{\"with\":\"P1\",\"buy\":[{\"count\":1}]}", "item is required")]
        [InlineData("{\"with\":\"P1\",\"buy\":[{\"item\":\"steel\"}]}", "count is required")]
        [InlineData("{\"with\":\"P1\",\"buy\":[{\"item\":\"steel\",\"count\":0}]}", "count must be 1..")]
        [InlineData("{\"with\":\"P1\",\"sell\":[{\"item\":\"steel\",\"count\":1.5}]}", "count must be an integer")]
        public void TradeShapes(string body, string why)
        {
            Bad(Post("/act/trade", body), why);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void TradeTooManyLines()
        {
            var lines = new JArray(Enumerable.Range(0, 7).Select(i => new JObject { ["item"] = "a" + i, ["count"] = 1 }));
            Bad(Post("/act/trade", new { with = "P1", buy = lines, sell = lines }), "at most 12 lines");
            Bad(Post("/act/trade", new { with = "P1", buy = new JArray(Enumerable.Range(0, 13).Select(i => new JObject { ["item"] = "a", ["count"] = 1 })) }), "at most 12 lines");
        }

        [Fact]
        public void TradeReachesTheHandAndChargesTheDay()
        {
            var r = Post("/act/trade", new { with = "p5521", buy = new[] { new { item = "steel", count = 10 } }, sell = new[] { new { item = "cloth", count = 5 } }, why = "need steel" });
            Assert.True(r.Ok, r.Body?.ToString());
            Assert.Equal("P5521", Hands.LastTrade.With); Assert.Equal("steel", Hands.LastTrade.Buy[0].Item); Assert.Equal(5, Hands.LastTrade.Sell[0].Count);
            Assert.Same(V.TradeDay, Hands.LastDay);
            Assert.Equal("bought 2 steel", (string)r.Body["done"]);
            Assert.Equal(-60, (int)r.Body["net"]);
            Assert.Equal(1, V.TradeDay.Deals); Assert.Equal(100, V.TradeDay.Spent); Assert.Equal(40, V.TradeDay.Got); Assert.Equal(140, V.TradeDay.Value);
            Assert.Equal(1, (int)r.Body["today"]["deals"]); Assert.Equal(2, (int)r.Body["today"]["deals_left"]); Assert.Equal(5860, (int)r.Body["today"]["value_left"]);
            Assert.Equal(1, V.Actions.LastSeq);
            Assert.Equal(ChatKinds.Action, V.Chat.Entries.Last().Who);
        }

        [Fact]
        public void TradeValueFromExtraWinsOverSum()
        {
            Hands.TradeNext = HandResult.Done("did", new JObject { ["spent"] = 100, ["got"] = 0, ["value"] = 777 });
            Assert.True(Post("/act/trade", new { with = "P1", buy = new[] { new { item = "x", count = 1 } } }).Ok);
            Assert.Equal(777, V.TradeDay.Value);
        }

        [Fact]
        public void TradeRefusalWritesAChatLineAndChargesNothing()
        {
            Hands.TradeNext = HandResult.No("the trader has no gold");
            Refused(Post("/act/trade", new { with = "P1", buy = new[] { new { item = "gold", count = 1 } } }), "the trader has no gold");
            Assert.Equal("couldn't trade with P1: the trader has no gold", LastRefusedLine());
            Assert.Equal(0, V.TradeDay.Deals); Assert.Equal(0, V.Actions.LastSeq);
        }

        [Fact]
        public void TradeGatedBySettingAndDealCountBeforeTheHand()
        {
            var body = new { with = "P1", buy = new[] { new { item = "steel", count = 1 } } };
            V.Settings.Trade = false;
            Refused(Post("/act/trade", body), "trading is off in the settings");
            Assert.Equal("couldn't trade with P1: trading is off in the settings", LastRefusedLine());
            Assert.Empty(Hands.Calls);
            V.Settings.Trade = true; V.Settings.TradeDealsPerDay = 2;
            Assert.True(Post("/act/trade", body).Ok); Assert.True(Post("/act/trade", body).Ok);
            Hands.Calls.Clear();
            Refused(Post("/act/trade", body), "made its 2 deals for today");
            Assert.Empty(Hands.Calls);
            Assert.Equal(2, V.TradeDay.Deals);
            H.State.Tick += 60000;   // the next game day
            Assert.True(Post("/act/trade", body).Ok);
            Assert.Equal(1, V.TradeDay.Deals);
        }

        [Fact]
        public void TradeHandGetsTheSettingsLimits()
        {
            V.Settings.TradeMaxDeal = 500; V.Settings.KeepMedicine = 4; V.Settings.NeverSell.Add("gold");
            Post("/act/trade", new { with = "S31", sell = new[] { new { item = "x", count = 1 } } });
            Assert.Equal(500, Hands.LastLimits.MaxDeal); Assert.Equal(4, Hands.LastLimits.KeepMedicine); Assert.Contains("gold", Hands.LastLimits.NeverSell);
        }

        [Fact]
        public void TradeNoGameAndThrowingHand()
        {
            Assert.Equal("no_game", H.Call("POST", "/act/trade", "{\"op_id\":\"z\",\"with\":\"P1\",\"buy\":[{\"item\":\"a\",\"count\":1}]}", GameState.Menu).Error);
        }

        // ------------------------------------------------------------ GET /caravan/plan
        [Theory]
        [InlineData("", "pawns is required")]
        [InlineData("?pawns=3,x&to=T1", "pawns must be comma-separated")]
        [InlineData("?pawns=0&to=T1", "pawns must be comma-separated")]
        [InlineData("?pawns=1,2,3,4,5,6,7&to=T1", "pawns takes at most 6")]
        [InlineData("?pawns=3", "to is required")]
        [InlineData("?pawns=3&to=home", "to must be")]
        [InlineData("?pawns=3&to=X4", "to must be")]
        [InlineData("?pawns=3&to=T4&goods=cloth", "goods must be item:count")]
        [InlineData("?pawns=3&to=T4&goods=cloth:0", "goods must be item:count")]
        [InlineData("?pawns=3&to=T4&animals=a", "animals must be")]
        [InlineData("?pawns=3&to=T4&purpose=raid", "purpose must be one of")]
        public void CaravanPlanShapes(string q, string why)
        {
            Bad(H.Call("GET", "/caravan/plan" + q, null), why);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void CaravanPlanParsesAndAsksTheHand()
        {
            var r = H.Call("GET", "/caravan/plan?pawns=3,5&animals=9&to=t4321&goods=cloth:20,steel%20bar:5&purpose=TRADE", null);
            Assert.True(r.Ok); Assert.True((bool)r.Body["ok"]);
            var a = Hands.LastCaravan;
            Assert.Equal(new[] { 3, 5 }, a.Pawns.ToArray()); Assert.Equal(new[] { 9 }, a.Animals.ToArray());
            Assert.Equal("T4321", a.To); Assert.Equal("trade", a.Purpose);
            Assert.Equal(2, a.Goods.Count); Assert.Equal("steel bar", a.Goods[1].Item); Assert.Equal(5, a.Goods[1].Count);
            H.Call("GET", "/caravan/plan?pawns=3&to=Q77", null);
            Assert.Equal("visit", Hands.LastCaravan.Purpose); Assert.Equal("Q77", Hands.LastCaravan.To);
        }

        [Fact]
        public void CaravanPlanGateAndRefusal()
        {
            Hands.PlanJson = new JObject { ["refused"] = "Bram is downed" };
            Refused(H.Call("GET", "/caravan/plan?pawns=3&to=T4", null), "Bram is downed");
            Assert.Equal(0, V.Chat.LastSeq);   // not an action: no chat line
            V.Settings.Caravans = false; Hands.Calls.Clear();
            Refused(H.Call("GET", "/caravan/plan?pawns=3&to=T4", null), "caravans are off in the settings");
            Assert.Empty(Hands.Calls);
            Assert.Equal("no_game", H.Call("GET", "/caravan/plan?pawns=3&to=T4", null, GameState.Menu).Error);
        }

        // ------------------------------------------------------------ POST /act/caravan
        [Theory]
        [InlineData("{}", "op is required")]
        [InlineData("{\"op\":\"go\"}", "op must be one of")]
        [InlineData("{\"op\":\"form\",\"to\":\"T1\"}", "form needs pawns")]
        [InlineData("{\"op\":\"form\",\"pawns\":[1,2,3,4,5,6,7],\"to\":\"T1\"}", "pawns takes at most 6")]
        [InlineData("{\"op\":\"form\",\"pawns\":[0],\"to\":\"T1\"}", "pawns must be positive")]
        [InlineData("{\"op\":\"form\",\"pawns\":\"3\",\"to\":\"T1\"}", "pawns must be an array")]
        [InlineData("{\"op\":\"form\",\"pawns\":[3]}", "to is required")]
        [InlineData("{\"op\":\"form\",\"pawns\":[3],\"to\":\"home\"}", "to must be")]
        [InlineData("{\"op\":\"form\",\"pawns\":[3],\"to\":\"T1\",\"purpose\":\"raid\"}", "purpose must be one of")]
        [InlineData("{\"op\":\"form\",\"pawns\":[3],\"to\":\"T1\",\"goods\":[{\"item\":\"a\"}]}", "count is required")]
        [InlineData("{\"op\":\"send\",\"to\":\"T1\"}", "caravan is required")]
        [InlineData("{\"op\":\"send\",\"caravan\":\"Z1\",\"to\":\"T1\"}", "caravan must be a caravan id")]
        [InlineData("{\"op\":\"send\",\"caravan\":\"C1\"}", "to is required")]
        [InlineData("{\"op\":\"cancel\"}", "caravan is required")]
        [InlineData("{\"op\":\"form\",\"pawns\":[3],\"to\":\"T1\",\"player_asked\":\"yes\"}", "player_asked must be true or false")]
        public void CaravanShapes(string body, string why)
        {
            Bad(Post("/act/caravan", body), why);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void CaravanFormStoresThePlan()
        {
            var r = Post("/act/caravan", new { op = "form", pawns = new[] { 3, 5, 3 }, animals = new[] { 9 }, goods = new[] { new { item = "cloth", count = 20 } }, to = "q77", purpose = "quest" });
            Assert.True(r.Ok, r.Body?.ToString());
            var a = Hands.LastCaravan;
            Assert.Equal("form", a.Op); Assert.Equal(new[] { 3, 5 }, a.Pawns.ToArray()); Assert.Equal("Q77", a.To); Assert.Equal("quest", a.Purpose); Assert.Equal(3, Hands.LastMaxPawns);
            Assert.Equal("C7", (string)r.Body["caravan"]);
            var p = V.CaravanPlans.Get("C7");
            Assert.NotNull(p); Assert.Equal("quest", p.Purpose); Assert.Equal("Q77", p.To); Assert.Equal("Q77", p.Quest); Assert.Equal(H.State.Tick, p.Formed); Assert.Equal("Ana and Bram set off", p.Note);
            Assert.Equal(1, V.Actions.LastSeq);
        }

        [Fact]
        public void CaravanFormToASettlementHasNoQuestAndMaxPawnsFromSettings()
        {
            V.Settings.CaravanMaxPawns = 5;
            Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4321" });
            Assert.Equal(5, Hands.LastMaxPawns); Assert.Equal("visit", Hands.LastCaravan.Purpose);
            Assert.Null(V.CaravanPlans.Get("C7").Quest); Assert.Equal("visit", V.CaravanPlans.Get("C7").Purpose);
        }

        [Fact]
        public void CaravanSendUpdatesAndCancelRemovesThePlan()
        {
            V.CaravanPlans.Set("C7", "trade", "T1", null, 5, "n");
            Hands.CaravanNext = HandResult.Done("sent it home");
            Assert.True(Post("/act/caravan", new { op = "send", caravan = "c7", to = "home" }).Ok);
            Assert.Equal("C7", Hands.LastCaravan.Caravan); Assert.Equal("home", Hands.LastCaravan.To);
            Assert.Equal("home", V.CaravanPlans.Get("C7").To);
            Assert.True(Post("/act/caravan", new { op = "cancel", caravan = "C7" }).Ok);
            Assert.Null(V.CaravanPlans.Get("C7"));
        }

        [Fact]
        public void CaravanRefusalNoPlanAndChatLine()
        {
            Hands.CaravanNext = HandResult.No("Bram is downed");
            Refused(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4" }), "Bram is downed");
            Assert.Equal("couldn't send a caravan to T4: Bram is downed", LastRefusedLine());
            Assert.Equal(0, V.CaravanPlans.Count); Assert.Equal(0, V.Actions.LastSeq);
        }

        [Fact]
        public void CaravanGatedBySettingAndPlayerAsked()
        {
            V.Settings.Caravans = false;
            Refused(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4" }), "caravans are off in the settings");
            Assert.Empty(Hands.Calls);
            V.Settings.Caravans = true;
            Refused(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4", player_asked = true }), "said nothing in the chat");
            Assert.Empty(Hands.Calls);
            V.Chat.Add("player", "go trade", H.State.Tick - 10);
            Assert.True(Post("/act/caravan", new { op = "form", pawns = new[] { 3 }, to = "T4", player_asked = true }).Ok);
            Assert.True(Hands.LastCaravan.PlayerAsked);
        }

        [Fact]
        public void ThrowingHandsBecomeInternal()
        {
            var h2 = new Harness(); var v2 = new Vivarium();
            VivariumRoutes.Register(h2.Router, () => v2, () => (IVivariumHands)typeof(VivariumTests).GetNestedType("Throwing", System.Reflection.BindingFlags.NonPublic).GetConstructor(System.Type.EmptyTypes).Invoke(null));
            Assert.Equal("internal", h2.Call("GET", "/trade/offer?with=P1", null).Error);
            Assert.Equal("internal", h2.Call("GET", "/caravan/plan?pawns=1&to=T1", null).Error);
            Assert.Equal("internal", h2.Call("POST", "/act/trade", "{\"op_id\":\"a\",\"with\":\"P1\",\"buy\":[{\"item\":\"a\",\"count\":1}]}").Error);
            Assert.Equal("internal", h2.Call("POST", "/act/caravan", "{\"op_id\":\"b\",\"op\":\"cancel\",\"caravan\":\"C1\"}").Error);
            Assert.Equal(0, v2.TradeDay.Deals);
        }
    }
}

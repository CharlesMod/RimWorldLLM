using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>Phase 4, the Verse-free part: trade rules and caps, the deal planner, the caravan planner and rules, quest kind and feasibility.</summary>
    public class VivariumTradeTests
    {
        static TradeOffer Offer(string item, int trader = 0, int colony = 0, double buy = 10, double sell = 5, string def = null, bool buys = true)
        {
            return new TradeOffer { Item = item, Def = def ?? item.Replace(" ", ""), TraderCount = trader, ColonyCount = colony, BuyPrice = buy, SellPrice = sell, TraderWillTrade = buys };
        }
        static TradeSituation Sit(params TradeOffer[] offers)
        {
            return new TradeSituation { ColonySilver = 1000, TraderSilver = 1000, Offers = offers.ToList(), MedicineTotal = 30, FoodNutritionTotal = 400, NutritionPerDay = 10 };
        }
        static TradeLimits Lim() { return new TradeLimits { MaxDeal = 2000, MaxDay = 6000, DealsPerDay = 3, KeepMedicine = 10, KeepFoodDays = 10 }; }
        static TradeArgs Args(string buy = null, string sell = null)
        {
            var a = new TradeArgs { With = "P1" };
            foreach (var l in (buy ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) { var p = l.Split(':'); a.Buy.Add(new DealLine(p[0], int.Parse(p[1]))); }
            foreach (var l in (sell ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) { var p = l.Split(':'); a.Sell.Add(new DealLine(p[0], int.Parse(p[1]))); }
            return a;
        }
        static DealPlan Plan(TradeArgs a, TradeSituation s, TradeLimits l = null, TradeDay d = null, int tick = 1000) { return DealPlanner.Plan(a, s, l ?? Lim(), d ?? new TradeDay(), tick); }

        // ------------------------------------------------------------ never-sell rules
        [Fact]
        public void RulesInUseAndForbiddenSellNothing()
        {
            var s = Sit();
            var o = Offer("longsword", colony: 3); o.InUse = true;
            Assert.Equal(0, TradeRules.SellableCount(o, s, Lim()));
            Assert.Contains("using it", TradeRules.Reason(o, s, Lim()));
            o = Offer("cloth", colony: 50); o.Forbidden = true;
            Assert.Equal(0, TradeRules.SellableCount(o, s, Lim()));
            Assert.Contains("forbidden", TradeRules.Reason(o, s, Lim()));
        }

        [Fact]
        public void RulesPlayerListMatchesLabelOrDefCaseInsensitive()
        {
            var l = Lim(); l.NeverSell.Add("JADE"); l.NeverSell.Add("  "); l.NeverSell.Add("masterwork");
            var s = Sit();
            Assert.Equal(0, TradeRules.SellableCount(Offer("jade sculpture", colony: 1), s, l));
            Assert.Equal(0, TradeRules.SellableCount(Offer("sculpture", colony: 1, def: "SculptureJade"), s, l));
            Assert.Equal(4, TradeRules.SellableCount(Offer("steel", colony: 4), s, l));
            Assert.Contains("never-sell list", TradeRules.Reason(Offer("jade", colony: 2), s, l));
        }

        [Fact]
        public void RulesMedicineFloorCountsAllMedicine()
        {
            var s = Sit(); s.MedicineTotal = 14;
            var m = Offer("herbal medicine", colony: 10); m.IsMedicine = true;
            Assert.Equal(4, TradeRules.SellableCount(m, s, Lim()));
            Assert.Contains("keeps 10 medicine", TradeRules.Reason(m, s, Lim()));
            s.MedicineTotal = 10;
            Assert.Equal(0, TradeRules.SellableCount(m, s, Lim()));
            s.MedicineTotal = 40; m.ColonyCount = 5;
            Assert.Equal(5, TradeRules.SellableCount(m, s, Lim()));
            Assert.Null(TradeRules.Reason(m, s, Lim()));
        }

        [Fact]
        public void RulesFoodFloorIsDaysOfNutrition()
        {
            var s = Sit(); s.FoodNutritionTotal = 130; s.NutritionPerDay = 10;   // keep 100, 30 spare
            var f = Offer("simple meal", colony: 50); f.IsFood = true; f.NutritionEach = 0.9;
            Assert.Equal(33, TradeRules.SellableCount(f, s, Lim()));
            Assert.Contains("10 days of food", TradeRules.Reason(f, s, Lim()));
            s.FoodNutritionTotal = 90;
            Assert.Equal(0, TradeRules.SellableCount(f, s, Lim()));
            s.NutritionPerDay = 0;   // no food floor
            Assert.Equal(50, TradeRules.SellableCount(f, s, Lim()));
        }

        [Fact]
        public void ActiveRulesListsTheDigestStrings()
        {
            var l = Lim(); l.NeverSell.Add("jade");
            Assert.Equal(new[] { "weapons and apparel in use", "medicine below 10", "food below 10 days", "forbidden items", "jade" }, TradeRules.ActiveRules(l).ToArray());
            l.KeepMedicine = 0; l.KeepFoodDays = 0;
            Assert.DoesNotContain(TradeRules.ActiveRules(l), x => x.StartsWith("medicine") || x.StartsWith("food"));
        }

        // ------------------------------------------------------------ TradeDay
        [Fact]
        public void TradeDayCountsAndRollsOver()
        {
            var d = new TradeDay(); var l = Lim();
            d.Roll(1000);
            d.Charge(300, 200);
            Assert.Equal(1, d.Deals); Assert.Equal(2, d.DealsLeft(l)); Assert.Equal(5500, d.ValueLeft(l));
            d.Charge(100, 0, 1000);
            Assert.Equal(4500, d.ValueLeft(l));
            d.Roll(59999);
            Assert.Equal(2, d.Deals);
            d.Roll(60000);
            Assert.Equal(0, d.Deals); Assert.Equal(6000, d.ValueLeft(l)); Assert.Equal(0, d.Spent);
        }

        [Fact]
        public void TradeDaySavesAndLoads()
        {
            var d = new TradeDay(); d.Roll(130000); d.Charge(50, 70);
            var e = new TradeDay(); e.Import(d.Export());
            Assert.Equal(2, e.Day); Assert.Equal(1, e.Deals); Assert.Equal(50, e.Spent); Assert.Equal(70, e.Got); Assert.Equal(120, e.Value);
            e.Import(null); Assert.Equal(0, e.Deals);
            e.Import(new JObject { ["deals"] = "x", ["day"] = 1.5 }); Assert.Equal(0, e.Deals);
        }

        [Fact]
        public void TradeDayLivesInTheVivariumSave()
        {
            var v = new Vivarium(); v.TradeDay.Roll(5); v.TradeDay.Charge(10, 20);
            v.CaravanPlans.Set("C1", "trade", "T4", null, 7, "n");
            var w = new Vivarium(); w.Import(v.Export());
            Assert.Equal(1, w.TradeDay.Deals); Assert.Equal(30, w.TradeDay.Value); Assert.Equal("trade", w.CaravanPlans.Get("C1").Purpose);
            var old = v.Export(); old.Remove("trade_day"); old.Remove("caravan_plans");
            w.Import(old);
            Assert.Equal(0, w.TradeDay.Deals); Assert.Equal(0, w.CaravanPlans.Count);
            v.Clear(); Assert.Equal(0, v.TradeDay.Deals); Assert.Equal(0, v.CaravanPlans.Count);
        }

        // ------------------------------------------------------------ item matching
        [Fact]
        public void MatchPrefersExactThenUniquePrefixThenContains()
        {
            var pool = new List<TradeOffer> { Offer("steel", 1), Offer("steel longsword", 1, def: "MeleeWeapon_LongSword"), Offer("component", 1, def: "ComponentIndustrial"), Offer("advanced component", 1, def: "ComponentSpacer") };
            string why;
            Assert.Equal("steel", DealPlanner.Match("STEEL", pool, out why).Item);                 // exact label beats the prefix of the longsword
            Assert.Equal("component", DealPlanner.Match("component", pool, out why).Item);         // exact label
            Assert.Equal("advanced component", DealPlanner.Match("ComponentSpacer", pool, out why).Item);   // exact def
            Assert.Equal("steel longsword", DealPlanner.Match("steel l", pool, out why).Item);     // unique prefix
            Assert.Equal("advanced component", DealPlanner.Match("advan", pool, out why).Item);
            Assert.Equal("steel longsword", DealPlanner.Match("longsw", pool, out why).Item);      // unique contains
            Assert.Null(DealPlanner.Match("nothing", pool, out why)); Assert.Null(why);
        }

        [Fact]
        public void MatchAmbiguityListsAtMostFiveCandidates()
        {
            var pool = Enumerable.Range(1, 8).Select(i => Offer("meal " + i, 1)).ToList();
            string why;
            Assert.Null(DealPlanner.Match("meal", pool, out why));
            Assert.Contains("could be meal 1, meal 2, meal 3, meal 4, meal 5 or more", why);
            Assert.DoesNotContain("meal 6", why);
            pool = new List<TradeOffer> { Offer("wood log", 1), Offer("wooden club", 1) };
            Assert.Null(DealPlanner.Match("wood", pool, out why)); Assert.Contains("wood log, wooden club", why);
        }

        // ------------------------------------------------------------ the planner
        [Fact]
        public void PlanBuyAndSellWritesTheNoteAndTotals()
        {
            var s = Sit(Offer("components", trader: 12, buy: 30), Offer("cloth", colony: 300, sell: 0.6));
            var p = Plan(Args("components:10", "cloth:200"), s);
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(300, p.Spent); Assert.Equal(120, p.Got); Assert.Equal(-180, p.Net);
            Assert.Equal("bought 10 components, sold 200 cloth (-180 silver)", p.Note);
            Assert.Equal(new[] { 10, -200 }, p.Lines.Select(l => l.Count).ToArray());
            p = Plan(Args(null, "cloth:200"), s);
            Assert.Equal("sold 200 cloth (+120 silver)", p.Note);
        }

        [Fact]
        public void PlanRefusals()
        {
            var s = Sit(Offer("components", trader: 12, buy: 30), Offer("cloth", colony: 300, sell: 0.6), Offer("steel", trader: 100, colony: 50, buy: 2, sell: 1));
            Assert.Contains("the deal is empty", Plan(new TradeArgs { With = "P1" }, s).Reason);
            var r = Plan(Args("gold:1"), s);
            Assert.False(r.Ok); Assert.Contains("the trader has no gold; it sells: components, steel", r.Reason);
            r = Plan(Args(null, "gold:1"), s); Assert.Contains("the colony has no gold to sell", r.Reason);
            r = Plan(Args("components:13"), s); Assert.Contains("the trader has only 12 components, not 13", r.Reason);
            r = Plan(Args("steel:5", "steel:5"), s); Assert.Contains("can't buy and sell steel", r.Reason);
            var no = Offer("cloth", colony: 10); no.TraderWillTrade = false;
            r = Plan(Args(null, "cloth:5"), Sit(no)); Assert.Contains("the trader does not buy cloth", r.Reason);
            r = Plan(Args("components:0"), s); Assert.Contains("at least 1", r.Reason);
        }

        [Fact]
        public void PlanSilverShortOnEitherSide()
        {
            var s = Sit(Offer("components", trader: 12, buy: 30), Offer("cloth", colony: 300, sell: 10));
            s.ColonySilver = 100;
            var r = Plan(Args("components:4"), s);
            Assert.Contains("the colony has 100 silver; the deal costs 120", r.Reason);
            Assert.True(Plan(Args("components:4", "cloth:3"), s).Ok);   // net cost 90
            s.TraderSilver = 50;
            r = Plan(Args(null, "cloth:10"), s);
            Assert.Contains("the trader has only 50 silver; the deal pays 100", r.Reason);
        }

        [Fact]
        public void PlanCapsDealDayAndCount()
        {
            var s = Sit(Offer("components", trader: 100, buy: 30));
            s.ColonySilver = 100000;
            var l = Lim();
            var r = Plan(Args("components:67"), s, l);   // 2010
            Assert.Contains("worth 2010 silver; the cap is 2000 per deal", r.Reason);
            Assert.True(Plan(Args("components:66"), s, l).Ok);
            var d = new TradeDay(); d.Roll(1000); d.Charge(2000, 2000, 4000);
            r = Plan(Args("components:40"), s, l, d);   // 1200 > 2000 left? no: 6000-4000 = 2000 left, ok
            Assert.True(r.Ok);
            d.Charge(0, 0, 1000);   // 5000 used
            r = Plan(Args("components:40"), s, l, d);
            Assert.Contains("only 1000 is left of today's 6000", r.Reason);
            d.Charge(0, 0, 0);       // third deal
            r = Plan(Args("components:1"), s, l, d);
            Assert.Contains("made its 3 deals for today", r.Reason);
            r = Plan(Args("components:1"), s, l, d, 61000);   // next day
            Assert.True(r.Ok);
        }

        [Fact]
        public void PlanClipsSellsToTheFloorAndRefusesAtZero()
        {
            var med = Offer("herbal medicine", colony: 12, sell: 20); med.IsMedicine = true;
            var s = Sit(med); s.MedicineTotal = 15;
            var p = Plan(Args(null, "herbal:12"), s);
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(-5, p.Lines[0].Count); Assert.Equal(100, p.Got);
            Assert.Single(p.Clips); Assert.Contains("sold 5 of the 12 herbal medicine: the colony keeps 10 medicine", p.Note);
            s.MedicineTotal = 10;
            var r = Plan(Args(null, "herbal medicine:3"), s);
            Assert.False(r.Ok); Assert.Contains("can't sell herbal medicine: the colony keeps 10", r.Reason);
        }

        [Fact]
        public void PlanFloorsCountEarlierLinesOfTheSameDeal()
        {
            var a = Offer("herbal medicine", colony: 10, sell: 20); a.IsMedicine = true;
            var b = Offer("industrial medicine", colony: 10, sell: 40); b.IsMedicine = true;
            var s = Sit(a, b); s.MedicineTotal = 20;   // 10 spare in total
            var p = Plan(Args(null, "herbal:8;industrial:8"), s);
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new[] { -8, -2 }, p.Lines.Select(l => l.Count).ToArray());
        }

        [Fact]
        public void PlanNeverSellsInUseForbiddenOrListed()
        {
            var sw = Offer("longsword", colony: 2); sw.InUse = true;
            Assert.Contains("using it", Plan(Args(null, "longsword:1"), Sit(sw)).Reason);
            var l = Lim(); l.NeverSell.Add("sculpture");
            Assert.Contains("never-sell", Plan(Args(null, "sculpture:1"), Sit(Offer("jade sculpture", colony: 1)), l).Reason);
        }

        [Fact]
        public void PlanMergesDuplicateLinesAndRejectsTooManyLines()
        {
            var s = Sit(Offer("steel", trader: 100, buy: 2));
            var p = Plan(Args("steel:10;steel:5"), s);
            Assert.Single(p.Lines); Assert.Equal(15, p.Lines[0].Count);
            var a = new TradeArgs { With = "P1" };
            for (int i = 0; i < 13; i++) a.Buy.Add(new DealLine("steel", 1));
            Assert.Contains("at most 12", Plan(a, s).Reason);
        }

        // ------------------------------------------------------------ the caravan planner
        static CaravanMember Pawn(double cap = 35, double nut = 1.6) { return new CaravanMember { Name = "Ana", CapacityKg = cap, NutritionPerDay = nut }; }
        static CaravanFood Food(string item, string kind, double nut, double mass, int avail, double spoil = double.PositiveInfinity)
        { return new CaravanFood { Item = item, Kind = kind, NutritionEach = nut, MassEach = mass, Available = avail, SpoilDays = spoil }; }

        [Fact]
        public void CaravanFoodNeedRoundTripVsOneWay()
        {
            var input = new PlanInput { TravelDays = 3, RoundTrip = true };
            input.Members.Add(Pawn()); input.Food.Add(Food("pemmican", "pemmican", 0.5, 0.05, 1000));
            var r = CaravanPlanner.Plan(input);
            Assert.Equal(9, r.FoodNeedDays);
            input.RoundTrip = false;
            Assert.Equal(6, CaravanPlanner.Plan(input).FoodNeedDays);
        }

        [Fact]
        public void CaravanPicksPemmicanThenSurvivalThenKibbleForAnimals()
        {
            var input = new PlanInput { TravelDays = 2, RoundTrip = true };   // need 7 days
            input.Members.Add(Pawn(nut: 1.6));                                  // 11.2 nutrition
            input.Members.Add(new CaravanMember { Name = "Muffalo", CapacityKg = 70, NutritionPerDay = 2, IsAnimal = true, EatsKibble = true });   // 14
            input.Food.Add(Food("survival meal", "survival", 0.9, 0.5, 100));
            input.Food.Add(Food("pemmican", "pemmican", 2.0, 0.1, 3));          // 6 nutrition only
            input.Food.Add(Food("kibble", "kibble", 0.05, 0.015, 1000));
            input.Food.Add(Food("simple meal", "meal", 0.9, 0.44, 100, spoil: 1));
            var r = CaravanPlanner.Plan(input);
            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(new[] { "pemmican", "survival meal", "kibble" }, r.FoodPick.Select(f => f.Item).ToArray());
            Assert.Equal(3, r.FoodPick[0].Count);
            Assert.Equal(6, r.FoodPick[1].Count);   // (11.2-6)/0.9 = 5.78 -> 6
            Assert.Equal(280, r.FoodPick[2].Count); // 14 / 0.05
            Assert.True(r.FoodDays >= 7);
        }

        [Fact]
        public void CaravanKibbleOnlyFeedsAnimalsAndMealsOnlyIfTheyKeep()
        {
            var input = new PlanInput { TravelDays = 2 };   // need 7
            input.Members.Add(Pawn(nut: 1.6));
            input.Food.Add(Food("kibble", "kibble", 0.05, 0.015, 1000));
            var r = CaravanPlanner.Plan(input);
            Assert.False(r.Ok); Assert.Empty(r.FoodPick);
            Assert.Contains("food for 0.0 days, needs 7.0", r.Problems);
            input.Food.Add(Food("simple meal", "meal", 0.9, 0.44, 100, spoil: 6.9));   // spoils before the trip ends
            Assert.Empty(CaravanPlanner.Plan(input).FoodPick);
            input.Food[1].SpoilDays = 7.5;
            r = CaravanPlanner.Plan(input);
            Assert.True(r.Ok); Assert.Equal("simple meal", r.FoodPick[0].Item);
            input.Food.Add(Food("rice", "raw", 0.05, 0.01, 1000, spoil: 40));
            input.Food[1].Available = 0;
            Assert.Equal("rice", CaravanPlanner.Plan(input).FoodPick[0].Item);
        }

        [Fact]
        public void CaravanReportsTooHeavyAndShortFoodAndNoPawns()
        {
            var input = new PlanInput { TravelDays = 3 };   // need 9
            input.Members.Add(Pawn(cap: 105)); input.Members.Add(Pawn(cap: 105));
            input.Goods.Add(new CaravanGood { Item = "cloth", MassEach = 1, Count = 251 });
            input.Food.Add(Food("pemmican", "pemmican", 2.0, 0.1, 3));
            var r = CaravanPlanner.Plan(input);
            Assert.False(r.Ok); Assert.Equal(210, r.Capacity);
            Assert.Contains("too heavy: 251/210 kg", r.Problems);   // 251 kg of cloth + 0.3 kg of food
            Assert.Contains(r.Problems, p => p.StartsWith("food for 1.9 days, needs 9.0"));
            Assert.Contains("no pawns", CaravanPlanner.Plan(new PlanInput { TravelDays = 1 }).Problems);
            var animalsOnly = new PlanInput { TravelDays = 1 };
            animalsOnly.Members.Add(new CaravanMember { Name = "m", CapacityKg = 50, IsAnimal = true });
            Assert.Contains("no pawns", CaravanPlanner.Plan(animalsOnly).Problems);
        }

        [Fact]
        public void CaravanFoodCappedByWhatIsAvailable()
        {
            var input = new PlanInput { TravelDays = 3 };
            input.Members.Add(Pawn(cap: 100));
            input.Food.Add(Food("pemmican", "pemmican", 2.0, 0.1, 2));   // 4 nutrition: 2.5 days
            var r = CaravanPlanner.Plan(input);
            Assert.Equal(2.5, r.FoodDays, 3);
            Assert.False(r.Ok);
        }

        // ------------------------------------------------------------ who may go
        static CaravanCandidate C(int id, string name = null) { return new CaravanCandidate { Id = id, Name = name ?? "P" + id, Age = 30, CanFight = true, CapacityKg = 35 }; }
        static RulesInput Party(int free, int fighters, params CaravanCandidate[] c) { return new RulesInput { Candidates = c.ToList(), FreeColonists = free, FreeFighters = fighters, MaxPawns = 3 }; }

        [Fact]
        public void RulesLetAnOrdinaryPartyGo()
        {
            Assert.Null(CaravanRules.Check(Party(6, 4, C(1), C(2))));
        }

        [Fact]
        public void RulesRefuseEveryBadPawn()
        {
            var bad = new Action<CaravanCandidate>[] { c => c.Downed = true, c => c.Drafted = true, c => c.InBed = true, c => c.MentalState = "wandering", c => c.Age = 12 };
            var words = new[] { "downed", "drafted", "sick in bed", "mental state", "too young" };
            for (int i = 0; i < bad.Length; i++)
            {
                var c = C(2, "Bram"); bad[i](c);
                var r = Party(6, 4, C(1), c); r.PlayerAsked = true;   // even the player cannot send them
                Assert.Contains("Bram", CaravanRules.Check(r) ?? ""); Assert.Contains(words[i], CaravanRules.Check(r));
            }
        }

        [Fact]
        public void RulesKeepSomeoneHomeAndAFighter()
        {
            Assert.Contains("must stay home", CaravanRules.Check(Party(2, 2, C(1), C(2))));
            var a = C(1); var b = C(2); b.CanFight = false;
            var r = CaravanRules.Check(Party(3, 1, a, b));
            Assert.Contains("fighter must stay", r);
            Assert.Null(CaravanRules.Check(Party(3, 1, b)));   // the lone non-fighter goes, the fighter stays
            Assert.Null(CaravanRules.Check(Party(3, 0, a, b)));   // no fighters at all: nothing to keep
            var asked = Party(2, 2, C(1), C(2)); asked.PlayerAsked = true;
            Assert.Contains("must stay home", CaravanRules.Check(asked));   // asking does not lift it
        }

        [Fact]
        public void RulesDoctorCookAndMaxPawnsUnlessAsked()
        {
            var doc = C(1, "Ana"); doc.IsDoctorBest = true;
            var cook = C(2, "Bram"); cook.IsOnlyCook = true;
            Assert.Contains("Ana is the colony's best doctor", CaravanRules.Check(Party(8, 6, doc, C(3))));
            Assert.Contains("Bram is the colony's only cook", CaravanRules.Check(Party(8, 6, cook, C(3))));
            var r = Party(8, 6, doc, cook, C(3)); r.PlayerAsked = true;
            Assert.Null(CaravanRules.Check(r));
            var four = Party(10, 8, C(1), C(2), C(3), C(4));
            Assert.Contains("at most 3 pawns", CaravanRules.Check(four));
            four.PlayerAsked = true; Assert.Null(CaravanRules.Check(four));
            four.PlayerAsked = false; four.MaxPawns = 4; Assert.Null(CaravanRules.Check(four));
        }

        [Fact]
        public void RulesAnimalsMustCarry()
        {
            var m = C(9, "Muffalo"); m.IsAnimal = true; m.CapacityKg = 0;
            Assert.Contains("Muffalo cannot carry", CaravanRules.Check(Party(6, 4, C(1), m)));
            m.CapacityKg = 70; Assert.Null(CaravanRules.Check(Party(6, 4, C(1), m)));
            Assert.Contains("no pawns", CaravanRules.Check(Party(6, 4, m)));
            Assert.Contains("no pawns", CaravanRules.Check(Party(6, 4)));
        }

        // ------------------------------------------------------------ quests
        [Theory]
        [InlineData("OpportunitySite_ItemStash", "", true, false, "site")]
        [InlineData("OpportunitySite_BanditCamp", "", true, false, "combat")]
        [InlineData("Util_TradeRequest", "", false, false, "delivery")]
        [InlineData("Whatever", "QuestPart_Delivery", false, true, "delivery")]
        [InlineData("ThreatReward_Raid_Joiner", "", false, false, "combat")]
        [InlineData("Hospitality_Refugee", "", false, false, "hosting")]
        [InlineData("LodgerJoin", "", false, false, "hosting")]
        [InlineData("Mystery", "", false, false, "other")]
        public void QuestKindFromKeywords(string root, string part, bool site, bool tradeReq, string want)
        {
            Assert.Equal(want, QuestPure.Kind(root, part == "" ? new string[0] : new[] { part }, site, tradeReq));
        }

        [Fact]
        public void QuestKindReadsPartTypesToo()
        {
            Assert.Equal("combat", QuestPure.Kind("Mystery", new[] { "QuestPart_Raid" }, false, false));
            Assert.Equal("other", QuestPure.Kind(null, null, false, false));
        }

        [Fact]
        public void QuestFeasibility()
        {
            var inf = double.PositiveInfinity;
            Assert.Equal((false, "needs combat (not yet)"), QuestPure.Feasible("combat", 0, 1000, 1, 10, 20, 1));
            Assert.True(QuestPure.Feasible("combat", 0, 1000, 1, 10, 20, 1, true).can);
            var dang = QuestPure.Feasible("site", 700, 1000, 1, 10, 20, 1);
            Assert.False(dang.can); Assert.Contains("too dangerous", dang.whyNot);
            Assert.True(QuestPure.Feasible("site", 600, 1000, 1, 10, 20, 1).can);   // exactly 0.6 is fine
            Assert.Contains("no time", QuestPure.Feasible("delivery", 0, 1000, 3, 5, 20, 1).whyNot);   // 6 > 5
            Assert.True(QuestPure.Feasible("site", 0, 1000, 3, 5, 20, 1).can);        // one way: 3 <= 5
            Assert.Contains("no time", QuestPure.Feasible("site", 0, 1000, 6, 5, 20, 1).whyNot);
            Assert.True(QuestPure.Feasible("delivery", 0, 1000, 30, inf, 20, 1).can);
            Assert.True(QuestPure.Feasible("delivery", 0, 1000, 30, double.NaN, 20, 1).can);
            Assert.Contains("no time", QuestPure.Feasible("site", 0, 1000, 1, 0, 20, 1).whyNot);
            Assert.Contains("food too low", QuestPure.Feasible("site", 0, 1000, 1, 10, 9.9, 1).whyNot);
            Assert.Contains("hurt", QuestPure.Feasible("site", 0, 1000, 1, 10, 20, 0.69).whyNot);
            Assert.Null(QuestPure.Feasible("site", 0, 1000, 1, 10, 20, 0.7).whyNot);
        }

        // ------------------------------------------------------------ digest helpers
        [Fact]
        public void NeedsListsWhatIsShortInOrder()
        {
            var n = new NeedsInput { Components = 4, FabricationDone = false, Medicine = 3, KeepMedicine = 10, Steel = 60, FoodDays = 20, KeepFoodDays = 10 };
            Assert.Equal(new[] { "components (4 left, cannot make them)", "medicine (3, keep 10)", "steel (60)" }, TradeView.Needs(n).ToArray());
            n.FabricationDone = true; n.Components = 2; n.Steel = 500; n.Medicine = 30; n.FoodDays = 3.3;
            Assert.Equal(new[] { "food (3.3 days, keep 10)" }, TradeView.Needs(n).ToArray());
            n.FoodDays = 20;
            Assert.Empty(TradeView.Needs(n));
            n.ShipResearchDone = true; n.Plasteel = 100; n.PlasteelNeed = 400; n.Uranium = 50; n.UraniumNeed = 40; n.AdvancedComponents = 1; n.AdvancedComponentsNeed = 6;
            Assert.Equal(new[] { "plasteel (100, ship needs 400)", "advanced components (1, ship needs 6)" }, TradeView.Needs(n).ToArray());
        }

        [Fact]
        public void NeedsAreCappedAtEight()
        {
            var all = TradeView.Needs(new NeedsInput { Components = 0, Medicine = 0, KeepMedicine = 10, FoodDays = 0, ShipResearchDone = true, PlasteelNeed = 1, UraniumNeed = 1, AdvancedComponentsNeed = 1 });
            Assert.Equal(7, all.Count); Assert.True(all.Count <= TradeView.MaxNeeds);
            Assert.Equal(2, TradeView.Cap(new[] { 1, 2, 3 }, 2).Count);
            Assert.Equal(new[] { 5, 3 }, TradeView.Top(new[] { 1, 5, 3, 2 }, x => x, 2).ToArray());
        }

        // ------------------------------------------------------------ plans
        [Fact]
        public void CaravanPlansCapAndRoundTrip()
        {
            var p = new CaravanPlans();
            for (int i = 1; i <= 25; i++) p.Set("C" + i, "trade", "T" + i, i == 25 ? "Q9" : null, i, "n" + i);
            Assert.Equal(20, p.Count); Assert.Null(p.Get("C5")); Assert.NotNull(p.Get("C6"));
            p.Set("C6", "visit", "T1", null, 99, "again");
            Assert.Equal(20, p.Count); Assert.Equal("visit", p.Get("C6").Purpose); Assert.Equal("C6", p.Items.Last().Key);
            var q = new CaravanPlans(); q.Import(p.Export());
            Assert.Equal(20, q.Count); Assert.Equal("Q9", q.Get("C25").Quest); Assert.Equal(99, q.Get("C6").Formed);
            q.Import(new JArray(1, new JObject(), new JObject { ["key"] = "C1" }));
            Assert.Equal(1, q.Count);
            q.Prune(k => false); Assert.Equal(0, q.Count);
            Assert.False(q.Remove("C1"));
        }
    }
}

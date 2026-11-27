using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;

namespace RimWorldLLM
{
    /// <summary>Phase 4 game bodies: trade (docs/VIVARIUM.md "Phase 4"). The deal runs through the game's TradeSession/TradeDeal with
    /// the best negotiator and no window. Decisions: the negotiator does not walk to the comms console (only checked for power and
    /// reach); orbital goods arrive as TradeDeal.TryExecute makes them (drop pods at the beacon). Game thread only.</summary>
    public sealed partial class VivariumHands
    {
        sealed class TradeCtx
        {
            public ITrader Trader;
            public Pawn Negotiator;
            public Caravan Caravan;     // set for T<tile>
            public Map Map;
            public string Name;
        }

        sealed class DealRead
        {
            public TradeSituation Sit = new TradeSituation();
            public readonly Dictionary<TradeOffer, Tradeable> Rows = new Dictionary<TradeOffer, Tradeable>();
            public Tradeable Currency;
        }

        static JObject Refused(string why) { return new JObject { ["refused"] = why }; }

        /// <summary>Finds the trader and the negotiator for an id, or returns the refusal.</summary>
        static string FindTrader(string with, Map map, out TradeCtx cx)
        {
            cx = new TradeCtx { Map = map };
            char kind; int num;
            if (!TradeKeys.TryParse(with, out kind, out num)) return "bad trader id " + with;
            if (kind == 'P')
            {
                Pawn tp = null;
                foreach (var p in map.mapPawns.AllPawnsSpawned) if (p.thingIDNumber == num) { tp = p; break; }
                if (tp == null || tp.trader == null || tp.TraderKind == null) return "no trader " + with + " on the map (it may have left)";
                if (TradeUtil.Hostile(tp.Faction)) return tp.LabelShort + "'s faction is hostile";
                if (!tp.trader.CanTradeNow) return tp.LabelShort + " cannot trade now";
                cx.Trader = tp; cx.Name = tp.LabelShort;
                cx.Negotiator = TradeUtil.BestNegotiator(map);
                if (cx.Negotiator == null) return "no colonist can negotiate (all are downed, drafted, in a mental state or cannot talk)";
                return null;
            }
            if (kind == 'S')
            {
                TradeShip ship = null;
                foreach (var s in TradeUtil.Ships(map)) if (TradeUtil.ShipNum(s) == num) { ship = s; break; }
                if (ship == null) return "no orbital trader " + with + " in range (it may have left)";
                if (!ship.CanTradeNow) return ship.TraderName + " cannot trade now";
                Building_CommsConsole console;
                string cs = TradeUtil.CommsStatus(map, out console);
                if (cs == "none") return "no comms console";
                if (cs == "no power") return "the console has no power";
                cx.Negotiator = TradeUtil.BestNegotiator(map, console);
                if (cx.Negotiator == null) return "no free colonist can reach the comms console and talk";
                cx.Trader = ship; cx.Name = ship.TraderName;
                return null;
            }
            if (kind == 'T')
            {
                var caravan = Find.WorldObjects.PlayerControlledCaravanAt(num);
                var st = Find.WorldObjects.SettlementAt(num);
                if (st == null) return "no settlement at tile " + num;
                if (caravan == null) return "none of our caravans is at " + st.LabelCap + ": send one there first";
                var rep = CaravanArrivalAction_Trade.CanTradeWith(caravan, st);
                if (!rep.Accepted) return string.IsNullOrEmpty(rep.FailReason) ? "the caravan cannot trade with " + st.LabelCap : rep.FailReason;
                var neg = BestCaravanPawnUtility.FindBestNegotiator(caravan, st.Faction, st.TraderKind);
                if (neg == null) return "no one in the caravan can negotiate";
                cx.Trader = st; cx.Negotiator = neg; cx.Caravan = caravan; cx.Name = st.LabelCap;
                return null;
            }
            return "bad trader id " + with;
        }

        static bool WornOrWielded(Thing t) { return t.ParentHolder is Pawn_EquipmentTracker || t.ParentHolder is Pawn_ApparelTracker; }

        /// <summary>Reads the open TradeSession into offers. Colony things that are worn, wielded or forbidden are taken out of the
        /// deal's colony side, so a count set later can never pick them.</summary>
        static DealRead ReadDeal(TradeCtx cx)
        {
            var r = new DealRead();
            var sit = r.Sit;
            foreach (var tr in TradeSession.deal.AllTradeables)
            {
                if (tr.IsCurrency) { r.Currency = tr; sit.ColonySilver = tr.CountHeldBy(Transactor.Colony); sit.TraderSilver = tr.CountHeldBy(Transactor.Trader); continue; }
                if (tr.IsFavor || !tr.HasAnyThing) continue;
                var def = tr.ThingDef;
                if (def == null || tr.AnyThing is Pawn) continue;
                int inUse = 0, forb = 0;
                foreach (var t in tr.thingsColony.ToList())
                {
                    if (WornOrWielded(t)) { inUse += t.stackCount; tr.thingsColony.Remove(t); }
                    else if (t.IsForbidden(Faction.OfPlayer)) { forb += t.stackCount; tr.thingsColony.Remove(t); }
                }
                var o = new TradeOffer { Item = tr.Label, Def = def.defName, Stuff = tr.StuffDef != null ? tr.StuffDef.defName : null };
                if (string.IsNullOrEmpty(o.Item)) o.Item = TradeUtil.Kind(def);
                o.TraderCount = tr.CountHeldBy(Transactor.Trader);
                o.ColonyCount = tr.CountHeldBy(Transactor.Colony);
                if (o.ColonyCount == 0 && inUse > 0) { o.ColonyCount = inUse; o.InUse = true; }
                else if (o.ColonyCount == 0 && forb > 0) { o.ColonyCount = forb; o.Forbidden = true; }
                try { o.TraderWillTrade = tr.TraderWillTrade; } catch (Exception) { }
                try { if (o.TraderCount > 0) o.BuyPrice = tr.GetPriceFor(TradeAction.PlayerBuys); } catch (Exception) { }
                try { if (o.ColonyCount > 0) o.SellPrice = tr.GetPriceFor(TradeAction.PlayerSells); } catch (Exception) { }
                o.IsMedicine = def.IsMedicine;
                if (def.IsNutritionGivingIngestible && def.ingestible != null && def.ingestible.HumanEdible)
                {
                    o.IsFood = true;
                    o.NutritionEach = def.GetStatValueAbstract(StatDefOf.Nutrition);
                }
                o.MarketValueEach = tr.BaseMarketValue;
                sit.Offers.Add(o);
                r.Rows[o] = tr;
            }
            var map = cx.Map;
            sit.MedicineTotal = Routes.MedicineCount(map);
            sit.FoodNutritionTotal = map.resourceCounter.TotalHumanEdibleNutrition;
            sit.NutritionPerDay = TradeUtil.ColonyNutritionPerDay(map);   // sum of the colonists' food-need fall (1.6 each when a pawn has no food need)
            return r;
        }

        static JObject NegotiatorJson(Pawn p)
        {
            return new JObject { ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort, ["price_bonus"] = Math.Round(p.GetStatValue(StatDefOf.TradePriceImprovement), 2) };
        }

        public JObject TradeOffer(string with, TradeLimits limits)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return null;
                TradeCtx cx;
                string no = FindTrader(with, map, out cx);
                if (no != null) return Refused(no);
                try
                {
                    TradeSession.SetupWith(cx.Trader, cx.Negotiator, false);
                    var d = ReadDeal(cx);
                    var buys = new JArray(d.Sit.Offers.Where(o => o.TraderCount > 0).OrderByDescending(o => o.BuyPrice * o.TraderCount).Take(30)
                        .Select(o => new JObject { ["item"] = o.Item, ["count"] = o.TraderCount, ["price"] = Math.Round(o.BuyPrice, 1) }));
                    var sells = new JArray();
                    foreach (var o in d.Sit.Offers.Where(x => x.ColonyCount > 0 && x.TraderWillTrade).OrderByDescending(x => x.SellPrice * x.ColonyCount))
                    {
                        int n = TradeRules.SellableCount(o, d.Sit, limits);
                        if (n <= 0) continue;
                        sells.Add(new JObject { ["item"] = o.Item, ["count"] = n, ["price"] = Math.Round(o.SellPrice, 1) });
                        if (sells.Count >= 30) break;
                    }
                    return new JObject
                    {
                        ["with"] = with, ["name"] = cx.Name, ["silver"] = d.Sit.TraderSilver, ["colony_silver"] = d.Sit.ColonySilver,
                        ["negotiator"] = NegotiatorJson(cx.Negotiator), ["buys"] = buys, ["sells"] = sells
                    };
                }
                finally { TradeSession.Close(); }
            }
            catch (Exception e) { Failed(e); return Refused("the game refused: " + e.Message); }
        }

        /// <summary>Sets a tradeable to buy (+n) or sell (-n). In vanilla a Tradeable's positive count goes toward the player
        /// (PlayerBuys) and a negative one is a sale; ActionToDo is checked after the adjust and the sign flipped if it disagrees.</summary>
        static string SetCount(Tradeable tr, int n, TradeAction want)
        {
            var rep = tr.CanAdjustTo(n);
            if (!rep.Accepted)
            {
                var rep2 = tr.CanAdjustTo(-n);
                if (!rep2.Accepted) return string.IsNullOrEmpty(rep.Reason) ? "the game will not move " + n + " " + tr.Label : rep.Reason;
                tr.AdjustTo(-n);
                if (tr.ActionToDo == want) return null;
                tr.AdjustTo(0);
                return "the game's count direction for " + tr.Label + " is not what was expected";
            }
            tr.AdjustTo(n);
            if (tr.ActionToDo == want) return null;
            tr.AdjustTo(0);
            var r3 = tr.CanAdjustTo(-n);
            if (!r3.Accepted) return string.IsNullOrEmpty(r3.Reason) ? "the game will not move " + n + " " + tr.Label : r3.Reason;
            tr.AdjustTo(-n);
            return tr.ActionToDo == want ? null : "the game's count direction for " + tr.Label + " is not what was expected";
        }

        public HandResult Trade(TradeArgs a, TradeLimits limits, TradeDay day)
        {
            return Described(TradeDo(a, limits, day), () => "trade with " + (a != null ? a.With : "?"));
        }

        HandResult TradeDo(TradeArgs a, TradeLimits limits, TradeDay day)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no home map");
                TradeCtx cx;
                string no = FindTrader(a.With, map, out cx);
                if (no != null) return HandResult.No(no);
                try
                {
                    TradeSession.SetupWith(cx.Trader, cx.Negotiator, false);
                    var d = ReadDeal(cx);
                    var plan = DealPlanner.Plan(a, d.Sit, limits, day, VivariumGame.NowTick);
                    if (!plan.Ok) return HandResult.No(plan.Reason);
                    foreach (var tr in TradeSession.deal.AllTradeables) if (!tr.IsCurrency) tr.AdjustTo(0);
                    foreach (var l in plan.Lines)
                    {
                        Tradeable tr;
                        if (!d.Rows.TryGetValue(l.Offer, out tr)) return HandResult.No("lost track of " + l.Offer.Item);
                        string why = SetCount(tr, Math.Abs(l.Count), l.Count > 0 ? TradeAction.PlayerBuys : TradeAction.PlayerSells);
                        if (why != null) return HandResult.No(why);
                    }
                    TradeSession.deal.UpdateCurrencyCount();
                    if (!TradeSession.deal.DoesTraderHaveEnoughSilver()) return HandResult.No("the trader does not have enough silver for this deal");
                    bool traded;
                    bool ok = TradeSession.deal.TryExecute(out traded);
                    if (!ok || !traded) return HandResult.No("the game did not complete the deal (the colony may lack silver, or the trader refused)");
                    string note = plan.Note + ", " + cx.Negotiator.LabelShort + " negotiated";
                    return HandResult.Done(note, new JObject { ["spent"] = plan.Spent, ["got"] = plan.Got, ["net"] = plan.Net, ["value"] = plan.Value });
                }
                finally { TradeSession.Close(); }
            }
            catch (Exception e) { return Failed(e); }
        }
    }
}

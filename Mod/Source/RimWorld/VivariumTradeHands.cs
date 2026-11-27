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
        static string FindTrader(string with, Map map, out TradeCtx cx, int prefer = 0)
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
                cx.Negotiator = Preferred(map, prefer, null) ?? TradeUtil.BestNegotiator(map);
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
                cx.Negotiator = Preferred(map, prefer, console) ?? TradeUtil.BestNegotiator(map, console);
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

        /// <summary>The walking trade's negotiator (by thing id) when they can still talk (and reach the console).</summary>
        static Pawn Preferred(Map map, int id, Thing reach)
        {
            if (id <= 0) return null;
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                if (p.thingIDNumber == id)
                    return TradeUtil.CanTalk(p) && (reach == null || p.CanReach(reach, PathEndMode.InteractionCell, Danger.Some)) ? p : null;
            return null;
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
                string no = FindTrader(a.With, map, out cx, a.Negotiator);
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
                    return HandResult.Done(note, new JObject { ["spent"] = plan.Spent, ["got"] = plan.Got, ["net"] = plan.Net, ["value"] = plan.Value, ["deal"] = plan.Note });
                }
                finally { TradeSession.Close(); }
            }
            catch (Exception e) { return Failed(e); }
        }

        // ------------------------------------------------------------------------------------------ walking trades
        static bool GiveGoto(Pawn n, IntVec3 cell)
        {
            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;   // as the right-click "Go here" does
            if (!n.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return false;
            // then stand there a while, so the pawn is still at the spot when the watch (every 60 ticks) looks
            var wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = WaitTicks;
            wait.playerForced = true;
            n.jobs.jobQueue.EnqueueLast(wait, JobTag.Misc);
            return true;
        }

        const int WaitTicks = 600;

        /// <summary>The Goto we gave, or the wait queued after it.</summary>
        static bool OurWalkJob(Job job) { return job != null && job.playerForced && (job.def == JobDefOf.Goto || job.def == JobDefOf.Wait); }

        /// <summary>A standable cell next to the trader that `who` can reach, the one nearest to them; Invalid when none.</summary>
        static IntVec3 CellNextTo(Pawn trader, Pawn who)
        {
            var best = IntVec3.Invalid; float bd = float.MaxValue;
            foreach (var c in GenAdj.CellsAdjacent8Way(trader))
            {
                if (!c.InBounds(trader.Map) || !c.Standable(trader.Map)) continue;
                float d = c.DistanceToSquared(who.Position);
                if (d < bd && who.CanReach(c, PathEndMode.OnCell, Danger.Some)) { best = c; bd = d; }
            }
            return best;
        }

        static Pawn ColonistById(Map map, int id)
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned) if (p.thingIDNumber == id) return p;
            return null;
        }

        public HandResult TradeStart(TradeArgs a, TradeLimits limits, TradeDay day, out PendingTrade p, out string cannotWalk)
        {
            p = null; cannotWalk = null;
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no home map");
                TradeCtx cx;
                string no = FindTrader(a.With, map, out cx);
                if (no != null) return Described(HandResult.No(no), () => "trade with " + a.With);
                Pawn walker = null; IntVec3 dest = IntVec3.Invalid; string kind, target;
                var tp = cx.Trader as Pawn;
                if (tp != null)
                {
                    kind = PendingTrade.ToTrader; target = cx.Name;
                    foreach (var c in map.mapPawns.FreeColonistsSpawned.Where(TradeUtil.CanTalk).OrderByDescending(x => x.GetStatValue(StatDefOf.TradePriceImprovement)))
                    {
                        dest = CellNextTo(tp, c);
                        if (dest.IsValid) { walker = c; break; }
                    }
                    if (walker == null) { cannotWalk = "no one could walk to " + cx.Name; return null; }
                }
                else
                {
                    kind = PendingTrade.ToConsole; target = "the comms console";
                    Building_CommsConsole console;
                    TradeUtil.CommsStatus(map, out console);
                    if (console == null || cx.Negotiator == null) { cannotWalk = "no one could walk to the comms console"; return null; }
                    walker = cx.Negotiator; dest = console.InteractionCell;
                }
                cx.Negotiator = walker;
                try
                {
                    TradeSession.SetupWith(cx.Trader, walker, false);
                    var d = ReadDeal(cx);
                    var plan = DealPlanner.Plan(a, d.Sit, limits, day, VivariumGame.NowTick);
                    if (!plan.Ok) return Described(HandResult.No(plan.Reason), () => "trade with " + cx.Name);
                }
                finally { TradeSession.Close(); }
                if (!GiveGoto(walker, dest)) { cannotWalk = walker.LabelShort + " could not take the order"; return null; }
                var tk = cx.Trader.TraderKind;
                p = PendingTrade.Start(a, VivariumGame.NowTick);
                p.NegotiatorId = walker.thingIDNumber; p.Negotiator = walker.LabelShort;
                p.TargetKind = kind; p.Target = target;
                p.Trader = tk != null && !string.IsNullOrEmpty(tk.label) ? "the " + tk.label : cx.Name;
                p.GoalX = dest.x; p.GoalZ = dest.z;
                return HandResult.Done(p.StartNote, new JObject { ["status"] = "walking", ["negotiator"] = p.Negotiator, ["to"] = p.Target });
            }
            catch (Exception e) { return Failed(e); }
        }

        /// <summary>Where the negotiator should stand now: the console's interaction cell, or a cell next to the trader. Sets `trader`
        /// (null with `why` when it is gone or cannot trade).</summary>
        static IntVec3 WalkDest(Map map, PendingTrade p, Pawn n, out Pawn traderPawn, out string why)
        {
            traderPawn = null; why = null;
            char kind; int num;
            if (!TradeKeys.TryParse(p.With, out kind, out num)) { why = "the trade is not valid any more"; return IntVec3.Invalid; }
            if (kind == 'S')
            {
                TradeShip ship = null;
                foreach (var s in TradeUtil.Ships(map)) if (TradeUtil.ShipNum(s) == num) { ship = s; break; }
                if (ship == null || !ship.CanTradeNow) return IntVec3.Invalid;
                Building_CommsConsole console;
                string cs = TradeUtil.CommsStatus(map, out console);
                if (cs == "none") { why = "there is no comms console"; return IntVec3.Invalid; }
                if (cs == "no power" || console == null) { why = "the console has no power"; return IntVec3.Invalid; }
                return console.InteractionCell;
            }
            foreach (var x in map.mapPawns.AllPawnsSpawned) if (x.thingIDNumber == num) { traderPawn = x; break; }
            if (traderPawn == null || traderPawn.trader == null || TradeUtil.Hostile(traderPawn.Faction) || !traderPawn.trader.CanTradeNow) { traderPawn = null; return IntVec3.Invalid; }
            return n != null ? CellNextTo(traderPawn, n) : traderPawn.Position;
        }

        static string CannotWhy(Pawn n)
        {
            if (n == null || !n.Spawned) return "is gone";
            if (n.Dead) return "died";
            if (n.Downed) return "was downed";
            if (n.Drafted) return "was drafted";
            if (n.InMentalState) return "had a mental break";
            return "can't talk now";
        }

        public WalkObs TradeObserve(PendingTrade p)
        {
            var o = new WalkObs();
            var map = HomeMap();
            if (map == null) return o;
            Pawn n = ColonistById(map, p.NegotiatorId);
            o.NegotiatorOk = n != null && TradeUtil.CanTalk(n);
            if (!o.NegotiatorOk) o.NegotiatorWhy = CannotWhy(n);
            Pawn tp; string why;
            var dest = WalkDest(map, p, o.NegotiatorOk ? n : null, out tp, out why);
            bool consoleTrade = p.TargetKind == PendingTrade.ToConsole;
            o.TraderPresent = consoleTrade ? why == null && dest.IsValid : tp != null;
            o.TraderWhy = why;
            if (!o.TraderPresent || !o.NegotiatorOk) return o;
            var job = n.CurJob;
            o.HasOurJob = OurWalkJob(job);
            if (consoleTrade)
            {
                o.Arrived = n.Position.DistanceToSquared(dest) <= 2;
                o.CanReach = o.Arrived || n.CanReach(dest, PathEndMode.OnCell, Danger.Some);
            }
            else
            {
                o.CanReach = n.CanReach(tp, PathEndMode.Touch, Danger.Some);
                o.Arrived = o.CanReach && n.Position.DistanceToSquared(tp.Position) <= 4;
                // the trader pawn wanders: aim the Goto again when it has moved more than 3 cells from where we aimed
                if (!o.Arrived && o.HasOurJob)
                {
                    if (p.GoalX < 0) { p.GoalX = tp.Position.x; p.GoalZ = tp.Position.z; }
                    else if (Math.Abs(tp.Position.x - p.GoalX) + Math.Abs(tp.Position.z - p.GoalZ) > 3 && dest.IsValid && GiveGoto(n, dest))
                    { p.GoalX = tp.Position.x; p.GoalZ = tp.Position.z; }
                }
            }
            return o;
        }

        public void TradeReissue(PendingTrade p)
        {
            var map = HomeMap();
            if (map == null) return;
            var n = ColonistById(map, p.NegotiatorId);
            if (n == null || !TradeUtil.CanTalk(n)) return;
            Pawn tp; string why;
            var dest = WalkDest(map, p, n, out tp, out why);
            if (dest.IsValid && GiveGoto(n, dest)) { p.GoalX = tp != null ? tp.Position.x : dest.x; p.GoalZ = tp != null ? tp.Position.z : dest.z; }
        }
    }
}

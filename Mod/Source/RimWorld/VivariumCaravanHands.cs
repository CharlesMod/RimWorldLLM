using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>Phase 4 game bodies: caravan plan, form, send, cancel (docs/VIVARIUM.md "Phase 4"). Game thread only.</summary>
    public sealed partial class VivariumHands
    {
        sealed class FoodRow { public CaravanFood Food; public List<Thing> Things = new List<Thing>(); }

        sealed class PlanCtx
        {
            public Map Map;
            public List<Pawn> Pawns = new List<Pawn>();
            public List<Pawn> Animals = new List<Pawn>();
            public PlanetTile Dest;
            public string To;
            public Site Site;
            public Settlement Settlement;
            public PlanInput Input = new PlanInput();
            public List<KeyValuePair<TradeOffer, int>> Goods = new List<KeyValuePair<TradeOffer, int>>();
            public Dictionary<TradeOffer, List<Thing>> GoodThings = new Dictionary<TradeOffer, List<Thing>>();
            public Dictionary<string, FoodRow> Food = new Dictionary<string, FoodRow>();
            public string Rules;
            public CaravanPlanResult Result;
        }

        static IEnumerable<Thing> HaulStock(Map map)
        {
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || !t.Spawned || t.def == null || t is Pawn || t.Fogged()) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                yield return t;
            }
        }

        static Pawn ColonyPawn(Map map, int id)
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned) if (p.thingIDNumber == id && p.Faction == Faction.OfPlayer) return p;
            return null;
        }

        static string PlaceKey(string to, out char kind, out int num) { kind = '\0'; num = 0; return TradeKeys.TryParse(to, out kind, out num) ? null : "bad destination " + to; }

        /// <summary>Resolves T/W/Q into a tile, and for Q the delivery items. Returns a refusal or null.</summary>
        static string ResolveDest(string to, PlanCtx cx, out QuestFacts qf)
        {
            qf = null;
            char kind; int num;
            string bad = PlaceKey(to, out kind, out num);
            if (bad != null) return bad;
            if (kind == 'T')
            {
                var st = Find.WorldObjects.SettlementAt(num);
                if (st == null) return "no settlement at tile " + num;
                if (TradeUtil.Hostile(st.Faction)) return st.LabelCap + " is hostile";
                cx.Settlement = st; cx.Dest = st.Tile; return null;
            }
            if (kind == 'W')
            {
                var site = Find.WorldObjects.SiteAt(num);
                if (site == null) return "no site at tile " + num;
                cx.Site = site; cx.Dest = site.Tile; return null;
            }
            if (kind == 'Q')
            {
                var q = QuestFacts.ById(num);
                if (q == null) return "no quest " + to;
                qf = QuestFacts.Of(q);
                if (qf.Kind == "combat") return "needs combat (not yet)";
                if (qf.Kind == "hosting") return "a hosting quest needs no caravan: prepare beds and food";
                if (q.State != QuestState.Ongoing) return "accept the quest first (it is not active)";
                if (!qf.Tile.Valid) return "this quest has no place to go to";
                cx.Dest = qf.Tile; cx.Site = qf.Site;
                if (qf.Kind == "delivery") cx.Settlement = qf.Settlement;
                return null;
            }
            return "bad destination " + to;
        }

        static string BuildPlan(CaravanArgs a, int maxPawns, out PlanCtx cx)
        {
            cx = new PlanCtx { To = a.To };
            var map = HomeMap();
            if (map == null) return "no home map";
            cx.Map = map;
            QuestFacts qf;
            string bad = ResolveDest(a.To, cx, out qf);
            if (bad != null) return bad;

            foreach (int id in a.Pawns)
            {
                var p = ColonyPawn(map, id);
                if (p == null || !p.IsFreeColonist) return "pawn " + id + " is not a free colonist on the home map";
                if (!cx.Pawns.Contains(p)) cx.Pawns.Add(p);
            }
            foreach (int id in a.Animals)
            {
                var p = ColonyPawn(map, id);
                if (p == null || !p.RaceProps.Animal) return "animal " + id + " is not a colony animal on the home map";
                if (!cx.Animals.Contains(p)) cx.Animals.Add(p);
            }
            if (cx.Pawns.Count == 0) return "no pawns: send at least one colonist";

            // who may go
            var free = map.mapPawns.FreeColonistsSpawned.ToList();
            Pawn doctor = null; int bestMed = -1;
            foreach (var p in free) { var sk = p.skills != null ? p.skills.GetSkill(SkillDefOf.Medicine) : null; int lv = sk != null && !sk.TotallyDisabled ? sk.Level : -1; if (lv > bestMed) { bestMed = lv; doctor = p; } }
            var cookType = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
            var cooks = cookType == null ? new List<Pawn>() : free.Where(p => !p.WorkTypeIsDisabled(cookType)).ToList();
            var ri = new RulesInput { FreeColonists = free.Count, FreeFighters = free.Count(p => !p.WorkTagIsDisabled(WorkTags.Violent)), MaxPawns = maxPawns, PlayerAsked = a.PlayerAsked };
            foreach (var p in cx.Pawns)
                ri.Candidates.Add(new CaravanCandidate
                {
                    Id = p.thingIDNumber, Name = p.LabelShort, CapacityKg = MassUtility.Capacity(p),
                    IsDoctorBest = bestMed >= 0 && p == doctor, IsOnlyCook = cooks.Count == 1 && cooks[0] == p,
                    CanFight = !p.WorkTagIsDisabled(WorkTags.Violent), Downed = p.Downed, Drafted = p.Drafted,
                    InBed = p.InBed() && (p.health.HasHediffsNeedingTend() || HealthAIUtility.ShouldSeekMedicalRest(p)),
                    MentalState = p.InMentalState ? (p.MentalStateDef != null ? p.MentalStateDef.label : "mental state") : null,
                    Age = (int)p.ageTracker.AgeBiologicalYears
                });
            foreach (var p in cx.Animals)
                ri.Candidates.Add(new CaravanCandidate { Id = p.thingIDNumber, Name = p.LabelShort, IsAnimal = true, CapacityKg = MassUtility.Capacity(p), Downed = p.Downed });
            cx.Rules = CaravanRules.Check(ri);

            // planner input
            var inp = cx.Input;
            inp.RoundTrip = a.Purpose != "quest";
            inp.TravelDays = TradeUtil.TravelDays(map.Tile, cx.Dest);
            foreach (var p in cx.Pawns) inp.Members.Add(new CaravanMember { Name = p.LabelShort, CapacityKg = MassUtility.Capacity(p), NutritionPerDay = TradeUtil.NutritionPerDay(p) });
            foreach (var p in cx.Animals)
                inp.Members.Add(new CaravanMember
                {
                    Name = p.LabelShort, CapacityKg = MassUtility.Capacity(p), NutritionPerDay = TradeUtil.NutritionPerDay(p), IsAnimal = true,
                    EatsKibble = (p.RaceProps.foodType & FoodTypeFlags.Kibble) != 0
                });

            // goods: item words against the haulable stock
            var stock = new Dictionary<ThingDef, List<Thing>>();
            foreach (var t in HaulStock(map)) { List<Thing> l; if (!stock.TryGetValue(t.def, out l)) stock[t.def] = l = new List<Thing>(); l.Add(t); }
            var pool = new List<TradeOffer>(); var poolThings = new Dictionary<TradeOffer, List<Thing>>();
            foreach (var kv in stock)
            {
                var o = new TradeOffer { Item = TradeUtil.Kind(kv.Key), Def = kv.Key.defName, ColonyCount = kv.Value.Sum(t => t.stackCount) };
                pool.Add(o); poolThings[o] = kv.Value;
            }
            var want = new List<DealLine>(a.Goods ?? new List<DealLine>());
            if (qf != null && qf.Kind == "delivery" && qf.Request != null && qf.Request.requestThingDef != null)
                want.Add(new DealLine(qf.Request.requestThingDef.defName, qf.Request.requestCount));
            var merged = new Dictionary<TradeOffer, int>();
            foreach (var l in want)
            {
                string why;
                var o = DealPlanner.Match(l.Item, pool, out why);
                if (o == null) return why ?? "no " + l.Item + " in stock to take";
                int n; merged.TryGetValue(o, out n); merged[o] = n + Math.Max(1, l.Count);
            }
            foreach (var kv in merged)
            {
                if (kv.Value > kv.Key.ColonyCount) return "only " + kv.Key.ColonyCount + " " + kv.Key.Item + " in stock, not " + kv.Value;
                cx.Goods.Add(kv);
                cx.GoodThings[kv.Key] = poolThings[kv.Key];
                var d = stock.First(s => s.Key.defName == kv.Key.Def).Key;
                inp.Goods.Add(new CaravanGood { Item = kv.Key.Item, MassEach = d.GetStatValueAbstract(StatDefOf.Mass), Count = kv.Value });
            }

            // food from stock
            foreach (var kv in stock)
            {
                var d = kv.Key;
                if (d.ingestible == null || !d.IsNutritionGivingIngestible || d.IsDrug) continue;
                var pref = d.ingestible.preferability;
                if (pref <= FoodPreferability.DesperateOnlyForHumanlikes && d != ThingDefOf.Kibble) continue;
                bool meal = pref >= FoodPreferability.MealTerrible;
                string kind = FoodKinds.Classify(d.defName, d.ingestible.HumanEdible, meal, d == ThingDefOf.Kibble, false);
                if (kind == null) continue;
                double nut = d.GetStatValueAbstract(StatDefOf.Nutrition);
                if (nut <= 0) continue;
                double spoil = double.PositiveInfinity;
                var rp = d.GetCompProperties<CompProperties_Rottable>();
                if (rp != null)
                {
                    spoil = rp.daysToRotStart;
                    foreach (var t in kv.Value) { var rc = t.TryGetComp<CompRottable>(); if (rc != null) spoil = Math.Min(spoil, (1.0 - rc.RotProgressPct) * rp.daysToRotStart); }
                }
                var food = new CaravanFood { Item = TradeUtil.Kind(d), Kind = kind, NutritionEach = nut, MassEach = d.GetStatValueAbstract(StatDefOf.Mass), SpoilDays = spoil, Available = kv.Value.Sum(t => t.stackCount) };
                inp.Food.Add(food);
                cx.Food[food.Item] = new FoodRow { Food = food, Things = kv.Value };
            }
            cx.Result = CaravanPlanner.Plan(inp);
            return null;
        }

        public JObject CaravanPlan(CaravanArgs a, TradeLimits limits)
        {
            try
            {
                PlanCtx cx;
                string no = BuildPlan(a, 3, out cx);
                if (no != null) return Refused(no);
                var o = cx.Result.ToJson();
                o["to"] = a.To;
                o["rules"] = cx.Rules == null ? JValue.CreateNull() : (JToken)cx.Rules;
                return o;
            }
            catch (Exception e) { Failed(e); return Refused("the game refused: " + e.Message); }
        }

        public HandResult Caravan(CaravanArgs a, int maxPawns)
        {
            return Described(CaravanDo(a, maxPawns), () => "caravan " + a.Op);
        }

        HandResult CaravanDo(CaravanArgs a, int maxPawns)
        {
            try
            {
                if (a.Op == "form") return CaravanForm(a, maxPawns);
                if (a.Op == "send") return CaravanSend(a);
                if (a.Op == "cancel") return CaravanCancel(a);
                return HandResult.No("unknown caravan op " + a.Op);
            }
            catch (Exception e) { return Failed(e); }
        }

        static TransferableOneWay Group(IEnumerable<Thing> things, int count)
        {
            var t = new TransferableOneWay();
            foreach (var th in things) t.things.Add(th);
            int n = Math.Min(count, t.MaxCount);
            if (n > 0 && t.CanAdjustTo(n).Accepted) t.AdjustTo(n);
            return t;
        }

        static bool FindSpots(Map map, List<Pawn> pawns, PlanetTile dest, out IntVec3 meet, out IntVec3 exit)
        {
            meet = IntVec3.Invalid; exit = IntVec3.Invalid;
            var first = pawns[0];
            if (!RCellFinder.TryFindRandomSpotJustOutsideColony(first, out meet)) meet = first.Position;
            Rot4 primary, secondary;
            CaravanExitMapUtility.GetExitMapEdges(Find.WorldGrid, map.Tile, dest, out primary, out secondary);
            Predicate<IntVec3> ok = c => c.Standable(map) && pawns.All(p => p.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
            if (CellFinder.TryFindRandomEdgeCellWith(ok, map, primary, 0f, out exit)) return true;
            if (secondary.IsValid && CellFinder.TryFindRandomEdgeCellWith(ok, map, secondary, 0f, out exit)) return true;
            return CellFinder.TryFindRandomEdgeCellWith(ok, map, 0f, out exit);
        }

        HandResult CaravanForm(CaravanArgs a, int maxPawns)
        {
            PlanCtx cx;
            string no = BuildPlan(a, maxPawns, out cx);
            if (no != null) return HandResult.No(no);
            if (cx.Rules != null) return HandResult.No(cx.Rules);
            if (!cx.Result.Ok) return HandResult.No(string.Join("; ", cx.Result.Problems));
            foreach (var p in cx.Pawns.Concat(cx.Animals))
                if (CaravanFormingUtility.IsFormingCaravan(p)) return HandResult.No(p.LabelShort + " is already in a forming caravan");
            var transferables = new List<TransferableOneWay>();
            foreach (var g in cx.Goods) transferables.Add(Group(cx.GoodThings[g.Key], g.Value));
            foreach (var fp in cx.Result.FoodPick)
            {
                FoodRow row;
                if (!cx.Food.TryGetValue(fp.Item, out row)) return HandResult.No("lost track of " + fp.Item);
                // food the goods already take is packed once: the transferable for this def takes the goods' and the food's counts together
                var same = transferables.FirstOrDefault(t => t.ThingDef == row.Things[0].def);
                if (same != null) { int n = same.CountToTransfer + fp.Count; same.AdjustTo(Math.Min(n, same.MaxCount)); }
                else transferables.Add(Group(row.Things, fp.Count));
            }
            var all = cx.Pawns.Concat(cx.Animals).ToList();
            IntVec3 meet, exit;
            if (!FindSpots(cx.Map, all, cx.Dest, out meet, out exit)) return HandResult.No("no spot to leave the map from (no reachable edge cell)");
            CaravanFormingUtility.StartFormingCaravan(all, new List<Pawn>(), Faction.OfPlayer, transferables, meet, exit, cx.Map.Tile, cx.Dest);
            Lord lord;
            if (!CaravanFormingUtility.TryGetFormingCaravanLord(cx.Pawns[0], out lord)) return HandResult.No("the game did not start forming the caravan");
            string key = TradeUtil.FormingKey(lord);
            VivariumCaravanWatch.Remember(key, all.Select(p => p.thingIDNumber));
            string names = string.Join(", ", cx.Pawns.Select(p => p.LabelShort));
            string pack = cx.Goods.Count > 0 ? ", taking " + string.Join(", ", cx.Goods.Select(g => g.Value + " " + g.Key.Item)) : "";
            string where = cx.Settlement != null ? cx.Settlement.LabelCap.ToString() : cx.Site != null ? cx.Site.Label : "tile " + (int)cx.Dest;
            return HandResult.Done("forming a caravan of " + names + " for " + where + pack + " (" + TradeRules.Num(cx.Result.Days) + " days, food for " + TradeRules.Num(cx.Result.FoodDays) + ")",
                new JObject { ["caravan"] = key });
        }

        HandResult CaravanSend(CaravanArgs a)
        {
            var c = TradeUtil.CaravanByKey(a.Caravan);
            if (c == null) return HandResult.No(TradeUtil.FormingLordByKey(a.Caravan) != null ? "that caravan is still forming" : "no caravan " + a.Caravan);
            if (c.CantMove) return HandResult.No("the caravan cannot move (" + (c.ImmobilizedByMass ? "too heavy" : "everyone is downed or in a mental state") + ")");
            PlanetTile tile; CaravanArrivalAction act = null; string where;
            if (string.Equals(a.To, "home", StringComparison.OrdinalIgnoreCase))
            {
                var home = HomeMap();
                if (home == null || home.Parent == null) return HandResult.No("no home map to go to");
                tile = home.Tile; act = new CaravanArrivalAction_Enter(home.Parent); where = "home";
            }
            else
            {
                var cx = new PlanCtx { To = a.To };
                QuestFacts qf;
                string bad = ResolveDest(a.To, cx, out qf);
                if (bad != null) return HandResult.No(bad);
                tile = cx.Dest;
                if (cx.Site != null) act = new CaravanArrivalAction_VisitSite(cx.Site);
                where = cx.Settlement != null ? cx.Settlement.LabelCap.ToString() : cx.Site != null ? cx.Site.Label : a.To;
            }
            double days = TradeUtil.TravelDays(c.Tile, tile, c);
            double food = c.DaysWorthOfFood.Item1;
            if (food < days) return HandResult.No("food for " + TradeRules.Num(food) + " days, the trip takes " + TradeRules.Num(days));
            if (!c.pather.StartPath(tile, act, true)) return HandResult.No("no path to " + where);
            return HandResult.Done("sent " + c.LabelCap + " to " + where + " (" + TradeRules.Num(days) + " days)");
        }

        HandResult CaravanCancel(CaravanArgs a)
        {
            var lord = TradeUtil.FormingLordByKey(a.Caravan);
            if (lord == null) return HandResult.No(TradeUtil.CaravanByKey(a.Caravan) != null ? "that caravan has already left; only a forming one can be cancelled" : "no forming caravan " + a.Caravan);
            CaravanFormingUtility.StopFormingCaravan(lord);
            VivariumCaravanWatch.Forget(a.Caravan);
            return HandResult.Done("cancelled the forming caravan");
        }
    }
}

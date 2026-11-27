using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `trade`, `caravans` and `quests` blocks (docs/VIVARIUM.md "Phase 4"). Vanilla reads only. Each part is
    /// independent: it is dropped and logged once when it throws.</summary>
    public static class VivariumTradeView
    {
        const int MaxStock = 10, MaxSells = 6, MaxBuys = 4, MaxSettlements = 6, MaxCaravans = 4, MaxQuests = 6;
        static readonly HashSet<string> logged = new HashSet<string>();
        static readonly Dictionary<string, List<string>> buyCache = new Dictionary<string, List<string>>();

        static void Part(JObject o, string name, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[name] = t; }
            catch (Exception e) { if (logged.Add(name)) Log.Warning("[RimWorldLLM] digest trade part '" + name + "' failed: " + e.Message); }
        }

        static T Try<T>(string name, Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception e) { if (logged.Add(name)) Log.Warning("[RimWorldLLM] digest trade part '" + name + "' failed: " + e.Message); return fallback; }
        }

        static Map HomeOf(Map m) { return m != null && m.IsPlayerHome ? m : TradeUtil.Home(); }

        static TradeLimits Limits() { return VivariumGame.State.Settings.Limits(); }

        static float FoodDays(Map map)
        {
            double per = TradeUtil.ColonyNutritionPerDay(map);
            return per <= 0 ? 0f : (float)(map.resourceCounter.TotalHumanEdibleNutrition / per);
        }

        // ------------------------------------------------------------------------------------------ trade
        public static JObject Trade(Map map)
        {
            map = HomeOf(map);
            if (map == null) return null;
            var o = new JObject();
            var limits = Limits();
            Part(o, "silver", () => map.resourceCounter.GetCount(ThingDefOf.Silver));
            Part(o, "negotiator", () =>
            {
                var p = TradeUtil.BestNegotiator(map);
                if (p == null) return JValue.CreateNull();
                return new JObject
                {
                    ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort,
                    ["social"] = p.skills != null ? p.skills.GetSkill(SkillDefOf.Social).Level : 0,
                    ["price_bonus"] = Math.Round(p.GetStatValue(StatDefOf.TradePriceImprovement), 2)
                };
            });
            Building_CommsConsole console = null;
            string comms = Try("comms", () => TradeUtil.CommsStatus(map, out console), "none");
            o["comms"] = comms;
            Part(o, "beacon", () => TradeUtil.HasBeacon(map));
            Part(o, "stock", () => Stock(map, limits));
            Part(o, "needs", () => Needs(map, limits));
            Part(o, "animals", () => new JArray(map.mapPawns.SpawnedColonyAnimals
                .Where(p => !p.Downed && p.RaceProps.packAnimal && MassUtility.Capacity(p) > 0)
                .OrderByDescending(p => MassUtility.Capacity(p)).Take(8)
                .Select(p => new JObject { ["id"] = p.thingIDNumber, ["name"] = p.LabelShort, ["capacity"] = (int)Math.Round(MassUtility.Capacity(p)) })));
            Part(o, "never_sell", () => new JArray(TradeRules.ActiveRules(limits)));
            Part(o, "traders", () => Traders(map, comms));
            Part(o, "settlements", () => Settlements(map));
            Part(o, "today", () => { var d = VivariumGame.State.TradeDay; d.Roll(VivariumGame.NowTick); return d.ToJson(limits); });
            return o;
        }

        static JToken Stock(Map map, TradeLimits limits)
        {
            var groups = new Dictionary<string, TradeOffer>();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || !t.Spawned || t is Pawn || t.def == ThingDefOf.Silver || t.Fogged()) continue;
                if (!TradeUtility.EverPlayerSellable(t.def)) continue;
                string stuff = t.Stuff != null ? t.Stuff.defName : "";
                string key = t.def.defName + "/" + stuff;
                TradeOffer o;
                if (!groups.TryGetValue(key, out o))
                {
                    o = new TradeOffer
                    {
                        Item = t.LabelNoCount, Def = t.def.defName, Stuff = stuff, IsMedicine = t.def.IsMedicine,
                        MarketValueEach = t.MarketValue
                    };
                    if (t.def.IsNutritionGivingIngestible && t.def.ingestible != null && t.def.ingestible.HumanEdible) { o.IsFood = true; o.NutritionEach = t.def.GetStatValueAbstract(StatDefOf.Nutrition); }
                    groups[key] = o;
                }
                if (t.IsForbidden(Faction.OfPlayer)) o.Forbidden = true; else o.ColonyCount += t.stackCount;
            }
            var sit = new TradeSituation
            {
                MedicineTotal = Routes.MedicineCount(map), FoodNutritionTotal = map.resourceCounter.TotalHumanEdibleNutrition,
                NutritionPerDay = TradeUtil.ColonyNutritionPerDay(map)
            };
            var arr = new JArray();
            foreach (var o in groups.Values.Where(x => x.ColonyCount > 0).OrderByDescending(x => x.MarketValueEach * x.ColonyCount).Take(MaxStock))
                arr.Add(new JObject
                {
                    ["item"] = o.Item, ["count"] = o.ColonyCount, ["value"] = (int)Math.Round(o.MarketValueEach * o.ColonyCount),
                    ["surplus"] = TradeRules.SellableCount(o, sit, limits)
                });
            return arr;
        }

        static bool Done(string def) { var r = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(def); return r != null && r.IsFinished; }

        static JToken Needs(Map map, TradeLimits limits)
        {
            var rc = map.resourceCounter;
            var n = new NeedsInput
            {
                Components = rc.GetCount(ThingDefOf.ComponentIndustrial), FabricationDone = Done("Fabrication"),
                Medicine = Routes.MedicineCount(map), KeepMedicine = limits.KeepMedicine, Steel = rc.GetCount(ThingDefOf.Steel),
                FoodDays = FoodDays(map), KeepFoodDays = limits.KeepFoodDays, ShipResearchDone = Done("ShipBasics"),
                Plasteel = rc.GetCount(ThingDefOf.Plasteel), Uranium = rc.GetCount(ThingDefOf.Uranium), AdvancedComponents = rc.GetCount(ThingDefOf.ComponentSpacer)
            };
            if (n.ShipResearchDone)
            {
                // Approximate: the cost lists of the ship's computer core, reactor and one engine (what the colony must have for the ship's core parts).
                foreach (var dn in new[] { "Ship_ComputerCore", "Ship_Reactor", "Ship_Engine" })
                {
                    var d = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
                    if (d == null || d.costList == null) continue;
                    foreach (var c in d.costList)
                    {
                        if (c.thingDef == ThingDefOf.Plasteel) n.PlasteelNeed += c.count;
                        else if (c.thingDef == ThingDefOf.Uranium) n.UraniumNeed += c.count;
                        else if (c.thingDef == ThingDefOf.ComponentSpacer) n.AdvancedComponentsNeed += c.count;
                    }
                }
            }
            return new JArray(TradeView.Needs(n));
        }

        static string Sells(Thing t) { return t.LabelNoCount + " " + t.stackCount; }

        static JArray SellsOf(IEnumerable<Thing> goods)
        {
            var groups = new Dictionary<ThingDef, int>(); var val = new Dictionary<ThingDef, float>();
            foreach (var t in goods)
            {
                if (t == null || t is Pawn || t.def == ThingDefOf.Silver) continue;
                int n; groups.TryGetValue(t.def, out n); groups[t.def] = n + t.stackCount;
                float v; val.TryGetValue(t.def, out v); val[t.def] = v + t.MarketValue * t.stackCount;
            }
            return new JArray(groups.OrderByDescending(kv => val[kv.Key]).Take(MaxSells).Select(kv => (kv.Key.label ?? kv.Key.defName) + " " + kv.Value));
        }

        static int SilverOf(IEnumerable<Thing> goods) { int n = 0; foreach (var t in goods) if (t.def == ThingDefOf.Silver) n += t.stackCount; return n; }

        static List<string> Buys(TraderKindDef tk)
        {
            List<string> r;
            if (buyCache.TryGetValue(tk.defName, out r)) return r;
            r = new List<string>();
            var root = ThingCategoryDefOf.Root;
            if (root != null)
                foreach (var c in root.childCategories)
                    if (c.DescendantThingDefs.Any(d => TradeUtility.EverPlayerSellable(d) && tk.WillTrade(d))) r.Add((c.label ?? c.defName).ToLowerInvariant());
            r = r.Take(MaxBuys).ToList();
            buyCache[tk.defName] = r;
            return r;
        }

        static JToken Traders(Map map, string comms)
        {
            var arr = new JArray();
            foreach (var p in TradeUtil.TraderPawns(map))
            {
                bool hostile = TradeUtil.Hostile(p.Faction);
                bool can = !hostile && p.trader.CanTradeNow;
                var goods = p.trader.Goods.ToList();
                var o = new JObject
                {
                    ["id"] = TradeKeys.Make('P', p.thingIDNumber), ["kind"] = "caravan", ["name"] = p.LabelShort,
                    ["faction"] = p.Faction != null ? p.Faction.Name : "", ["trader"] = p.TraderKind.label, ["silver"] = SilverOf(goods),
                    ["can_trade"] = can, ["sells"] = SellsOf(goods), ["buys"] = new JArray(Buys(p.TraderKind))
                };
                if (!can) o["why_not"] = hostile ? "hostile" : "the trader is not trading now";
                arr.Add(o);
            }
            foreach (var s in TradeUtil.Ships(map))
            {
                bool can = s.CanTradeNow && comms == "ready";
                var goods = s.Goods.ToList();
                var o = new JObject
                {
                    ["id"] = TradeKeys.Make('S', TradeUtil.ShipNum(s)), ["kind"] = "orbital", ["name"] = s.TraderName, ["trader"] = s.TraderKind.label,
                    ["leaves_h"] = TradeUtil.ShipLeavesHours(s), ["silver"] = s.Silver, ["can_trade"] = can, ["sells"] = SellsOf(goods), ["buys"] = new JArray(Buys(s.TraderKind))
                };
                if (!can) o["why_not"] = comms == "none" ? "no comms console" : comms == "no power" ? "the console has no power" : "the trader is leaving";
                arr.Add(o);
            }
            return arr;
        }

        static JToken Settlements(Map map)
        {
            var near = TradeUtil.TradingSettlements().OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(map.Tile, s.Tile)).Take(MaxSettlements).ToList();
            var arr = new JArray();
            foreach (var s in near)
                arr.Add(new JObject
                {
                    ["id"] = TradeKeys.Make('T', (int)s.Tile), ["name"] = s.LabelCap.ToString(), ["faction"] = s.Faction.Name,
                    ["relation"] = s.Faction.PlayerRelationKind.ToString().ToLowerInvariant(), ["goodwill"] = s.Faction.PlayerGoodwill,
                    ["days"] = Math.Round(TradeUtil.TravelDays(map.Tile, s.Tile), 1), ["trader"] = s.TraderKind.label
                });
            return arr;
        }

        // -------------------------------------------------------------------------------------- caravans
        static string PlaceLabel(PlanetTile tile)
        {
            var st = Find.WorldObjects.SettlementAt(tile);
            if (st != null) return st.LabelCap.ToString();
            var site = Find.WorldObjects.SiteAt(tile);
            if (site != null) return site.Label;
            return "tile " + (int)tile;
        }

        public static JToken Caravans(Map map)
        {
            var v = VivariumGame.State;
            var rows = new List<JObject>();
            foreach (var c in Find.WorldObjects.Caravans)
            {
                if (!c.IsPlayerControlled) continue;
                string key = TradeUtil.CaravanKey(c);
                var plan = v.CaravanPlans.Get(key);
                var o = new JObject
                {
                    ["id"] = key, ["name"] = c.LabelCap.ToString(),
                    ["pawns"] = new JArray(c.PawnsListForReading.Where(p => !p.RaceProps.Animal).Select(p => p.LabelShort)),
                    ["animals"] = c.PawnsListForReading.Count(p => p.RaceProps.Animal)
                };
                var st = Find.WorldObjects.SettlementAt(c.Tile);
                o["at"] = st != null ? st.LabelCap.ToString() : "on the road";
                if (c.pather.Moving)
                {
                    var dest = c.pather.Destination;
                    bool home = map != null && dest == map.Tile;
                    o["to"] = home ? "home" : PlaceLabel(dest);
                    o["days_left"] = Math.Round(Try("eta", () => CaravanArrivalTimeEstimator.EstimatedTicksToArrive(c, true), 0) / 60000.0, 1);
                }
                else o["to"] = "stopped";
                o["food_days"] = Math.Round(c.DaysWorthOfFood.Item1, 1);
                o["mass"] = (int)Math.Round(c.MassUsage) + "/" + (int)Math.Round(c.MassCapacity);
                o["danger"] = "none";
                o["purpose"] = plan != null ? plan.Purpose : "";
                if (st != null && !c.pather.Moving && CaravanArrivalAction_Trade.CanTradeWith(c, st).Accepted) o["can_trade"] = TradeKeys.Make('T', (int)st.Tile);
                o["narrator"] = plan != null;
                rows.Add(o);
            }
            foreach (var lord in TradeUtil.FormingLords())
            {
                string key = TradeUtil.FormingKey(lord);
                var plan = v.CaravanPlans.Get(key);
                var pawns = lord.ownedPawns;
                rows.Add(new JObject
                {
                    ["id"] = key, ["name"] = "forming caravan",
                    ["pawns"] = new JArray(pawns.Where(p => !p.RaceProps.Animal).Select(p => p.LabelShort)),
                    ["animals"] = pawns.Count(p => p.RaceProps.Animal), ["at"] = "forming", ["to"] = plan != null ? plan.To : "",
                    ["danger"] = "none", ["purpose"] = plan != null ? plan.Purpose : "", ["narrator"] = plan != null
                });
            }
            if (rows.Count == 0) return null;
            return new JArray(rows.OrderByDescending(r => (bool)r["narrator"]).Take(MaxCaravans));
        }

        // ----------------------------------------------------------------------------------------- quests
        static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);

        static string Short(string s, int max)
        {
            s = Tags.Replace(s ?? "", "").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max).TrimEnd() + "..." : s;
        }

        static string Reward(Quest q)
        {
            try
            {
                foreach (var p in q.PartsListForReading)
                {
                    var ch = p as QuestPart_Choice;
                    if (ch == null || ch.choices == null || ch.choices.Count == 0) continue;
                    var parts = new List<string>();
                    foreach (var r in ch.choices[0].rewards) { try { parts.Add(r.GetDescription(default(RewardsGeneratorParams)).ToString()); } catch (Exception) { } }
                    if (parts.Count > 0) return Short(string.Join(", ", parts), 60);
                }
            }
            catch (Exception) { }
            return "unknown";
        }

        static double HealthyFraction(Map map)
        {
            var free = map.mapPawns.FreeColonistsSpawned.ToList();
            if (free.Count == 0) return 1;
            return free.Count(p => !p.Downed && !p.InMentalState && p.health.summaryHealth.SummaryHealthPercent > 0.7f) / (double)free.Count;
        }

        public static JToken Quests(Map map)
        {
            map = HomeOf(map);
            if (map == null) return null;
            float colonyPoints = Try("points", () => StorytellerUtility.DefaultThreatPointsNow(map), 0f);
            float foodDays = FoodDays(map);
            double healthy = HealthyFraction(map);
            var arr = new JArray();
            foreach (var q in QuestFacts.Visible())
            {
                if (arr.Count >= MaxQuests) break;
                try { arr.Add(QuestRow(q, map, colonyPoints, foodDays, healthy)); }
                catch (Exception e) { if (logged.Add("quest")) Log.Warning("[RimWorldLLM] digest quest row failed: " + e.Message); }
            }
            return arr.Count == 0 ? null : arr;
        }

        static JObject QuestRow(Quest q, Map map, float colonyPoints, float foodDays, double healthy)
        {
            var f = QuestFacts.Of(q);
            bool active = q.State == QuestState.Ongoing;
            double days = f.Tile.Valid ? TradeUtil.TravelDays(map.Tile, f.Tile) : 0;
            int ticks = Try("expiry", () => q.TicksUntilExpiry, -1);
            if (!active && ticks < 0) ticks = Try("accept", () => q.acceptanceExpireTick - Find.TickManager.TicksGame, -1);
            double expDays = ticks > 0 ? ticks / 60000.0 : double.PositiveInfinity;
            double threat = f.Site != null ? Try("threat", () => (double)f.Site.ActualThreatPoints, 0.0) : 0;
            var fz = QuestPure.Feasible(f.Kind, threat, colonyPoints, days, expDays, foodDays, healthy);
            string what;
            var items = new JArray();
            if (f.Kind == "delivery" && f.Request != null && f.Request.requestThingDef != null)
            {
                what = "bring " + f.Request.requestCount + " " + TradeUtil.Kind(f.Request.requestThingDef) + " to " + f.Settlement.LabelCap;
                items.Add(new JObject { ["item"] = TradeUtil.Kind(f.Request.requestThingDef), ["count"] = f.Request.requestCount });
            }
            else if (f.Site != null) what = "go to " + f.Site.Label;
            else what = Short(q.description.ToString(), 100);
            var o = new JObject
            {
                ["id"] = TradeKeys.Make('Q', q.id), ["name"] = Short(q.name, 60), ["state"] = active ? "active" : "offer", ["kind"] = f.Kind,
                ["what"] = what, ["reward"] = Reward(q), ["expires_h"] = ticks > 0 ? (JToken)TradeUtil.Hours(ticks) : JValue.CreateNull(),
                ["days"] = Math.Round(days, 1)
            };
            if (f.Tile.Valid) o["site"] = TradeKeys.Make(f.Site != null ? 'W' : 'T', (int)f.Tile);
            if (items.Count > 0) o["items"] = items;
            o["danger"] = threat > 0 ? (int)threat + " points" : "none";
            o["can"] = fz.can;
            o["why_not"] = fz.can ? JValue.CreateNull() : (JToken)fz.whyNot;
            return o;
        }
    }
}

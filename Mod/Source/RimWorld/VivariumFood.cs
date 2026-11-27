using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `food` block and the food/clock facts: short counts and days, never item lists.
    /// Perishable and crop scans are cached per game tick so the digest and the facts share one pass.</summary>
    public static class VivariumFood
    {
        static readonly HashSet<string> logged = new HashSet<string>();
        const int MaxScan = 4000, MaxAnimals = 300;

        static void Warn(string what, Exception e) { if (logged.Add(what)) Log.Warning("[RimWorldLLM] digest food '" + what + "' failed: " + e.Message); }
        static double R1(double d) { return Math.Round(d, 1); }

        class Scan
        {
            public int Meals, Raw;
            public List<RotGroup> Rot = new List<RotGroup>();
            public int RotWithinDay;
            public List<HarvestGroup> Harvest = new List<HarvestGroup>();
            public int CropsReady;
            public List<GameGroup> Game = new List<GameGroup>();
            public int Huntable;
            public int Hunters;
        }

        static int cachedMap = -1, cachedTick = -1;
        static Scan cached;

        static Scan Get(Map map)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cached != null && cachedMap == map.uniqueID && cachedTick == tick) return cached;
            var s = new Scan();
            try { CountFood(map, s); } catch (Exception e) { Warn("counts", e); }
            try { Rotting(map, s); } catch (Exception e) { Warn("rot", e); }
            try { Crops(map, s); } catch (Exception e) { Warn("harvest", e); }
            try { Game(map, s); } catch (Exception e) { Warn("game", e); }
            try { Hunters(map, s); } catch (Exception e) { Warn("hunters", e); }
            cached = s; cachedMap = map.uniqueID; cachedTick = tick;
            return s;
        }

        static void CountFood(Map map, Scan s)
        {
            foreach (var kv in map.resourceCounter.AllCountedAmounts)
            {
                var d = kv.Key;
                if (d.ingestible == null || !d.IsNutritionGivingIngestible || d.IsCorpse || d.IsDrug) continue;
                var p = d.ingestible.preferability;
                if (p == FoodPreferability.MealAwful || p == FoodPreferability.MealSimple || p == FoodPreferability.MealFine || p == FoodPreferability.MealLavish) s.Meals += kv.Value;
                else if (p == FoodPreferability.RawBad || p == FoodPreferability.RawTasty) s.Raw += kv.Value;
            }
        }

        static bool IsMeal(ThingDef d)
        {
            var p = d.ingestible.preferability;
            return p == FoodPreferability.MealAwful || p == FoodPreferability.MealSimple || p == FoodPreferability.MealFine || p == FoodPreferability.MealLavish;
        }

        /// <summary>Loose meals and raw food (not in storage), from the shared loose scan.</summary>
        static void Loose(Map map, out int meals, out int raw)
        {
            meals = 0; raw = 0;
            foreach (var kv in VivariumLoose.Get(map).Loose)
            {
                var d = kv.Key;
                if (d.ingestible == null || !d.IsNutritionGivingIngestible || d.IsCorpse || d.IsDrug) continue;
                if (IsMeal(d)) meals += kv.Value;
                else if (d.ingestible.preferability == FoodPreferability.RawBad || d.ingestible.preferability == FoodPreferability.RawTasty) raw += kv.Value;
            }
        }

        static void Rotting(Map map, Scan s)
        {
            var stacks = new List<FoodStack>();
            int n = 0;
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (++n > MaxScan) break;
                if (t == null || !t.Spawned || t.def.ingestible == null || !t.def.IsNutritionGivingIngestible) continue;
                var rot = t.TryGetComp<CompRottable>();
                if (rot == null || rot.Stage != RotStage.Fresh) continue;
                if (t.AmbientTemperature <= 0f) continue;   // frozen
                double days = rot.TicksUntilRotAtCurrentTemp / 60000.0;
                if (days >= NarratorFood.RotWithinDays) continue;
                stacks.Add(new FoodStack(t.def.label ?? t.def.defName, t.stackCount, days));
                if (days < 1.0) s.RotWithinDay += t.stackCount;
            }
            s.Rot = NarratorFood.GroupRot(stacks);
        }

        static void Crops(Map map, Scan s)
        {
            var cells = new List<CropCell>();
            foreach (var z in map.zoneManager.AllZones)
            {
                var gz = z as Zone_Growing;
                if (gz == null) continue;
                foreach (var c in gz.Cells)
                {
                    if (cells.Count >= MaxScan) break;
                    var pl = c.GetPlant(map);
                    if (pl == null || pl.def.plant == null || !pl.def.plant.Harvestable) continue;
                    bool ready = pl.HarvestableNow;
                    double days = double.PositiveInfinity;
                    if (!ready)
                    {
                        float perTick = pl.GrowthRate / (60000f * pl.def.plant.growDays);
                        float need = pl.def.plant.harvestMinGrowth - pl.Growth;
                        // plants rest at night (they grow only between 25% and 80% of the day), so the growing hours are 0.55 of a day
                        if (perTick > 1e-9f && need > 0f) days = need / perTick / 60000.0 / 0.55;
                    }
                    else s.CropsReady++;
                    cells.Add(new CropCell(pl.def.label ?? pl.def.defName, ready, days));
                }
            }
            s.Harvest = NarratorFood.GroupHarvest(cells);
        }

        static void Game(Map map, Scan s)
        {
            var home = VivariumBuildView.HomeOf(map);
            if (home == null) return;
            var list = new List<GameAnimal>();
            int n = 0;
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (++n > MaxAnimals) break;
                if (p == null || p.Dead || !p.RaceProps.Animal || p.Faction != null || p.RaceProps.meatDef == null) continue;
                if (p.InAggroMentalState || p.Fogged() || p.kindDef.combatPower >= 150f) continue;
                list.Add(new GameAnimal(p.LabelNoCount, p.Position.x, p.Position.z));
            }
            s.Huntable = list.Count;
            s.Game = NarratorFood.GroupGame(list, home[0], home[1]);
        }

        static void Hunters(Map map, Scan s)
        {
            s.Hunters = map.mapPawns.FreeColonistsSpawned.Count(p => p.workSettings != null && p.workSettings.EverWork
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && p.workSettings.WorkIsActive(WorkTypeDefOf.Hunting));
        }

        /// <summary>The digest's `food` object.</summary>
        public static JToken Digest(Map map)
        {
            var s = Get(map);
            int lm, lr; Loose(map, out lm, out lr);
            var o = new JObject { ["meals"] = s.Meals, ["meals_loose"] = lm, ["raw"] = s.Raw, ["raw_loose"] = lr };
            o["rot_soon"] = new JArray(s.Rot.Select(g => new JObject { ["label"] = g.Label, ["count"] = g.Count, ["days"] = R1(g.Days) }));
            o["harvest"] = new JArray(s.Harvest.Select(g =>
            {
                var h = new JObject { ["plant"] = g.Plant, ["cells"] = g.Cells, ["ready"] = g.Ready };
                if (g.Days != null) h["days"] = R1(g.Days.Value);
                return h;
            }));
            o["game"] = new JArray(s.Game.Select(g => new JObject { ["label"] = g.Label, ["count"] = g.Count, ["dist"] = g.Dist, ["dir"] = g.Dir }));
            o["hunters"] = s.Hunters;
            return o;
        }

        /// <summary>Flat food facts, merged into the digest `facts`.</summary>
        public static void AddFacts(JObject o, Map map)
        {
            var s = Get(map);
            o["meals"] = s.Meals; o["raw_food"] = s.Raw; o["food_rot_soon"] = s.RotWithinDay;
            o["crops_ready"] = s.CropsReady; o["huntable"] = s.Huntable;
        }

        /// <summary>The soonest food stack group that rots, for the deadlines list.</summary>
        public static Deadline RotDeadline(Map map)
        {
            var g = Get(map).Rot.FirstOrDefault();
            return g == null ? null : new Deadline(g.Label + " rots", g.Days * 24.0, "rot");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>Phase 4 game helpers shared by the trade/caravan hands, the digest view and the caravan watch. Game thread only.</summary>
    public static class TradeUtil
    {
        public const float FallbackNutritionPerDay = 1.6f;   // used when a pawn has no food need to read

        public static Map Home()
        {
            var m = Find.CurrentMap;
            if (m != null && m.IsPlayerHome) return m;
            return Find.Maps.FirstOrDefault(x => x.IsPlayerHome);
        }

        /// <summary>Per-day fall of a pawn's food need in nutrition; FallbackNutritionPerDay when it has none.</summary>
        public static float NutritionPerDay(Pawn p)
        {
            try { var f = p.needs != null ? p.needs.food : null; if (f != null) return f.FoodFallPerTick * 60000f; } catch (Exception) { }
            return FallbackNutritionPerDay;
        }

        public static double ColonyNutritionPerDay(Map map)
        {
            double sum = 0; int n = 0;
            foreach (var p in map.mapPawns.FreeColonistsSpawned) { sum += NutritionPerDay(p); n++; }
            return n == 0 ? 0 : sum;
        }

        public static bool CanTalk(Pawn p)
        {
            if (p == null || p.Dead || p.Downed || p.Drafted || p.InMentalState || !p.Spawned) return false;
            if (p.WorkTagIsDisabled(WorkTags.Social)) return false;
            return p.health.capacities.CapableOf(PawnCapacityDefOf.Talking);
        }

        /// <summary>The free colonist with the best trade price stat who can talk (and reach the console when one is given).</summary>
        public static Pawn BestNegotiator(Map map, Thing reach = null)
        {
            Pawn best = null; float bs = float.MinValue;
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                if (!CanTalk(p)) continue;
                if (reach != null && !p.CanReach(reach, PathEndMode.InteractionCell, Danger.Some)) continue;
                float s = p.GetStatValue(StatDefOf.TradePriceImprovement);
                if (s > bs) { bs = s; best = p; }
            }
            return best;
        }

        /// <summary>"ready", "no power" or "none" for the colony's comms consoles; the best usable console in `console`.</summary>
        public static string CommsStatus(Map map, out Building_CommsConsole console)
        {
            console = null;
            var all = map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>().ToList();
            if (all.Count == 0) return "none";
            foreach (var c in all) if (c.CanUseCommsNow) { console = c; return "ready"; }
            return "no power";
        }

        public static bool HasBeacon(Map map)
        {
            foreach (var b in map.listerBuildings.AllBuildingsColonistOfClass<Building_OrbitalTradeBeacon>())
            {
                var p = b.GetComp<CompPowerTrader>();
                if (p == null || p.PowerOn) return true;
            }
            return false;
        }

        public static int ShipNum(PassingShip s) { return TradeKeys.TrailingNumber(s.GetUniqueLoadID()); }

        public static IEnumerable<Pawn> TraderPawns(Map map)
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (p.trader != null && p.TraderKind != null && p.Faction != null && !p.Faction.IsPlayer) yield return p;
        }

        public static IEnumerable<TradeShip> Ships(Map map)
        {
            foreach (var s in map.passingShipManager.passingShips) { var t = s as TradeShip; if (t != null && !t.Departed) yield return t; }
        }

        public static bool Hostile(Faction f) { return f != null && f.HostileTo(Faction.OfPlayer); }

        public static string Kind(ThingDef d) { return d.label ?? d.defName; }

        public static int ShipLeavesHours(TradeShip s) { return Math.Max(0, s.ticksUntilDeparture) / 2500; }

        /// <summary>Hours a settlement-less deadline in ticks, rounded; negative = none.</summary>
        public static int Hours(int ticks) { return ticks < 0 ? -1 : ticks / 2500; }

        public static string Money(double d) { return ((int)Math.Round(d)).ToString(); }

        /// <summary>The caravan key the digest and plans use: C + the world object id.</summary>
        public static string CaravanKey(Caravan c) { return TradeKeys.Make('C', c.ID); }

        /// <summary>The forming key: C + the lord's load id; ids of a forming and a formed caravan can collide, so they are told apart by what the plan records.</summary>
        public static string FormingKey(Lord l) { return TradeKeys.Make('C', l.loadID); }

        public static Caravan CaravanByKey(string key)
        {
            char k; int n;
            if (!TradeKeys.TryParse(key, out k, out n) || k != 'C') return null;
            foreach (var c in Find.WorldObjects.Caravans) if (c.ID == n && c.IsPlayerControlled) return c;
            return null;
        }

        public static Lord FormingLordByKey(string key)
        {
            char k; int n;
            if (!TradeKeys.TryParse(key, out k, out n) || k != 'C') return null;
            foreach (var map in Find.Maps)
                foreach (var l in map.lordManager.lords)
                    if (l.loadID == n && l.LordJob is LordJob_FormAndSendCaravan) return l;
            return null;
        }

        public static IEnumerable<Lord> FormingLords()
        {
            foreach (var map in Find.Maps)
                foreach (var l in map.lordManager.lords.ToList())
                    if (l.faction == Faction.OfPlayer && l.LordJob is LordJob_FormAndSendCaravan) yield return l;
        }

        /// <summary>Days of walking from the home map's tile to `to` (game's estimate, else tiles / 9).</summary>
        public static double TravelDays(PlanetTile from, PlanetTile to, Caravan caravan = null)
        {
            try
            {
                int t = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(from, to, caravan);
                if (t > 0 && t < 60000 * 400) return t / 60000.0;
            }
            catch (Exception) { }
            try { return Find.WorldGrid.TraversalDistanceBetween(from, to) / 9.0; } catch (Exception) { return 9; }
        }

        public static IEnumerable<Settlement> TradingSettlements()
        {
            foreach (var s in Find.WorldObjects.Settlements)
            {
                if (s.Faction == null || s.Faction.IsPlayer || Hostile(s.Faction)) continue;
                if (s.TraderKind == null) continue;
                yield return s;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `cmd` block: what the command tools (job, medical, prisoner, animal, social) have to work with.
    /// Always an object when the map has colonists (the mind offers the job tool whenever it exists), possibly empty.
    /// Cheap reads; each part is independent and dropped if it throws (logged once).</summary>
    public static class VivariumCmdView
    {
        const int MaxPatients = 6, MaxPrisoners = 6, MaxAnimals = 8, MaxWild = 5, MaxSurgeryPawns = 30;
        static readonly HashSet<string> logged = new HashSet<string>();
        static int socialTick = -999999, socialMap = -1; static bool socialCached;

        static void Part(JObject o, string name, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[name] = t; }
            catch (Exception e) { if (logged.Add(name)) Log.Warning("[RimWorldLLM] digest cmd part '" + name + "' failed: " + e.Message); }
        }

        static JToken List(IEnumerable<string> xs, int max)
        {
            var l = xs.Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(max).ToList();
            return l.Count == 0 ? null : new JArray(l);
        }

        public static JObject Build(Map map)
        {
            if (map == null || map.mapPawns.FreeColonistsSpawnedCount == 0) return null;
            var o = new JObject();
            var people = map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned).Concat(map.mapPawns.PrisonersOfColonySpawned).ToList();
            Part(o, "patients", () => List(people.Where(Hurt).Select(p => p.LabelShort), MaxPatients));
            Part(o, "surgery", () => people.Take(MaxSurgeryPawns).Any(SurgeryAvailable) ? (JToken)true : null);
            Part(o, "prisoners", () => List(map.mapPawns.PrisonersOfColonySpawned.Select(p => p.LabelShort), MaxPrisoners));
            Part(o, "animals", () => List(map.mapPawns.SpawnedColonyAnimals.Where(p => p.Faction == Faction.OfPlayer).Select(AnimalName), MaxAnimals));
            Part(o, "wild", () => List(WildKinds(map), MaxWild));
            Part(o, "social", () => Social(map) ? (JToken)true : null);
            Part(o, "ideo", () => ModsConfig.IdeologyActive ? (JToken)true : null);
            Part(o, "may_execute", () => RimWorldLLMMod.Settings != null && RimWorldLLMMod.Settings.narratorMayExecutePrisoners ? (JToken)true : null);
            return o;
        }

        static string AnimalName(Pawn p)
        {
            return p.Name != null && !p.Name.Numerical ? p.LabelShort : (p.kindDef?.label ?? p.def.label);
        }

        static bool Hurt(Pawn p)
        {
            return !p.Dead && p.health != null && (p.health.HasHediffsNeedingTend(false) || p.health.summaryHealth.SummaryHealthPercent < 0.98f);
        }

        /// <summary>As the health tab's operation list: a surgery recipe the game offers on this pawn now (any body part for part recipes).</summary>
        static bool SurgeryAvailable(Pawn p)
        {
            if (p.Dead || p.def?.AllRecipes == null) return false;
            foreach (var r in p.def.AllRecipes)
            {
                if (r == null || !r.IsSurgery || !r.AvailableNow || !VivariumHands.CasualSurgery(r)) continue;
                if (!r.Worker.AvailableOnNow(p, null)) continue;
                if (!r.targetsBodyPart) return true;
                if (r.Worker.GetPartsToApplyOn(p, r).Any()) return true;
            }
            return false;
        }

        static IEnumerable<string> WildKinds(Map map)
        {
            var home = map.areaManager.Home;
            var cols = map.mapPawns.FreeColonistsSpawned;
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == null || p.Faction != null || !p.RaceProps.Animal || p.Dead || p.Downed) continue;
                if (p.Position.Fogged(map)) continue;
                bool near = home != null && home.TrueCount > 0 && home[p.Position];
                if (!near) foreach (var c in cols) { if ((c.Position - p.Position).LengthHorizontalSquared <= 40 * 40) { near = true; break; } }
                if (!near || !TameUtility.CanTame(p)) continue;
                if (map.designationManager.DesignationOn(p, DesignationDefOf.Tame) != null) continue;
                yield return p.kindDef?.label ?? p.def.label;
            }
        }

        /// <summary>A party could start now (checked at most every ~600 ticks) or an engaged pair of colonists exists.</summary>
        static bool Social(Map map)
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                var f = p.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Fiance, null);
                if (f != null && f.Spawned && f.Map == map && f.IsFreeNonSlaveColonist) return true;
            }
            int t = Find.TickManager.TicksGame;
            if (socialMap != map.uniqueID || t < socialTick || t - socialTick > 600)
            {
                socialMap = map.uniqueID; socialTick = t;
                socialCached = GatheringDefOf.Party != null && GatheringDefOf.Party.Worker.CanExecute(map, null);
            }
            return socialCached;
        }
    }
}

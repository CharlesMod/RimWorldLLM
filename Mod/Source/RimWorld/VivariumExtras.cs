using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Small narrator extras: pawn break risk and why, tattered apparel, ancient danger, unreachable loose piles.
    /// Logic lives in Bridge/NarratorExtras.cs.</summary>
    public static class VivariumExtras
    {
        static readonly HashSet<string> logged = new HashSet<string>();
        static void Warn(string what, Exception e) { if (logged.Add(what)) Log.Warning("[RimWorldLLM] digest extras '" + what + "' failed: " + e.Message); }

        public static void AddPawn(JObject o, Pawn p)
        {
            var mood = p.needs != null ? p.needs.mood : null;
            if (mood == null || p.mindState == null) return;
            var mb = p.mindState.mentalBreaker;
            string risk = NarratorExtras.BreakRisk(mood.CurLevel, mb.BreakThresholdMinor, mb.BreakThresholdMajor, mb.BreakThresholdExtreme);
            if (risk == null) return;
            o["break_risk"] = risk;
            var list = new List<Thought>();
            mood.thoughts.GetAllMoodThoughts(list);
            var why = NarratorExtras.MoodWhy(list.Select(t => new MoodThought(t.LabelCap, t.MoodOffset())));
            if (why.Count > 0) o["mood_why"] = new JArray(why);
        }

        public static void AddFacts(JObject o, Map map)
        {
            try
            {
                int n = 0;
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                    if (p.apparel != null) foreach (var a in p.apparel.WornApparel) if (a.HitPoints < a.MaxHitPoints * 0.5f) n++;
                o["tattered_apparel"] = n;
            }
            catch (Exception e) { Warn("tattered", e); }
            try
            {
                bool danger = false;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail("AncientCryptosleepCasket");
                if (def != null)
                    foreach (var t in map.listerThings.ThingsOfDef(def))
                    {
                        var c = t as Building_Casket;
                        if (c != null && c.HasAnyContents && !t.Fogged()) { danger = true; break; }
                    }
                o["ancient_danger"] = danger;
            }
            catch (Exception e) { Warn("ancient", e); }
            try
            {
                int n = 0, seen = 0;
                foreach (var it in VivariumLoose.Get(map).Items)
                {
                    if (it.Forbidden) continue;
                    if (++seen > 300) break;
                    if (!map.reachability.CanReachColony(new IntVec3(it.X, 0, it.Z))) n++;
                }
                o["unreachable_piles"] = n;
            }
            catch (Exception e) { Warn("unreachable", e); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `deadlines` block and its facts: clocks the narrator should feel as hours (bleeding out,
    /// infections, rot, winter, letters that expire). The math is in Bridge/NarratorClock.cs.</summary>
    public static class VivariumDeadlines
    {
        static readonly HashSet<string> logged = new HashSet<string>();
        static void Warn(string what, Exception e) { if (logged.Add(what)) Log.Warning("[RimWorldLLM] digest deadlines '" + what + "' failed: " + e.Message); }
        static double R1(double d) { return Math.Round(d, 1); }

        class Clocks
        {
            public List<Deadline> Items = new List<Deadline>();
            public double? DaysToWinter, DaysToGrowing;
            public double? BleedMin;
            public int InfectionsLosing;
        }

        static int cachedMap = -1, cachedTick = -1;
        static Clocks cached;

        static Clocks Get(Map map)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cached != null && cachedMap == map.uniqueID && cachedTick == tick) return cached;
            var c = new Clocks();
            try { Health(map, c); } catch (Exception e) { Warn("health", e); }
            try { Season(map, c); } catch (Exception e) { Warn("season", e); }
            try { var r = VivariumFood.RotDeadline(map); if (r != null) c.Items.Add(r); } catch (Exception e) { Warn("rot", e); }
            try { Letters(c); } catch (Exception e) { Warn("letters", e); }
            cached = c; cachedMap = map.uniqueID; cachedTick = tick;
            return c;
        }

        static void Health(Map map, Clocks c)
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                var hs = p.health != null ? p.health.hediffSet : null;
                if (hs == null) continue;
                if (hs.BleedRateTotal > 0.01f)
                {
                    int ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
                    if (ticks > 0 && ticks < 60000 * 5)
                    {
                        double hours = ticks / 2500.0;
                        c.Items.Add(new Deadline(p.LabelShort + " bleeds out", hours, "bleed"));
                        if (c.BleedMin == null || hours < c.BleedMin) c.BleedMin = hours;
                    }
                }
                foreach (var h in hs.hediffs)
                {
                    var imm = h.TryGetComp<HediffComp_Immunizable>();
                    if (imm == null || h.def.lethalSeverity <= 0f || h.FullyImmune()) continue;
                    var rec = p.health.immunity.GetImmunityRecord(h.def);
                    if (rec == null) continue;
                    double immPerDay = rec.ImmunityChangePerTick(p, true, h) * 60000.0;
                    double sevPerDay = imm.SeverityChangePerDay();
                    var race = NarratorClock.ImmunityRace(rec.immunity, immPerDay, h.Severity, sevPerDay, h.def.lethalSeverity);
                    if (double.IsInfinity(race.Value)) continue;
                    if (!race.Key) c.InfectionsLosing++;
                    c.Items.Add(new Deadline(p.LabelShort + "'s " + (h.def.label ?? h.def.defName) + ": " + (race.Key ? "winning" : "losing") + " the race", race.Value, "infection"));
                }
            }
        }

        static void Season(Map map, Clocks c)
        {
            int tile = map.Tile;
            var means = new double[12];
            for (int i = 0; i < 12; i++) means[i] = GenTemperature.AverageTemperatureAtTileForTwelfth(tile, (Twelfth)i);
            int cur = (int)GenLocalDate.Twelfth(map);
            double day = GenLocalDate.DayOfTwelfth(map) + GenLocalDate.HourFloat(map) / 24.0;
            if (NarratorClock.TooColdToGrow(means, cur))
            {
                var w = NarratorClock.DaysToWarm(means, cur, day);
                if (w != null && w.Value > 0)
                {
                    c.DaysToGrowing = w;
                    c.Items.Add(new Deadline("growing season starts", w.Value * 24.0, "winter"));
                }
            }
            var cold = NarratorClock.DaysToCold(means, cur, day);
            if (cold != null && cold.Value > 0)
            {
                c.DaysToWinter = cold;
                c.Items.Add(new Deadline("winter (crops die)", cold.Value * 24.0, "winter"));
            }
        }

        static void Letters(Clocks c)
        {
            int now = Find.TickManager.TicksGame;
            foreach (var l in Find.LetterStack.LettersListForReading)
            {
                var lt = l as LetterWithTimeout;
                if (lt == null || !lt.TimeoutActive) continue;
                c.Items.Add(new Deadline(l.Label.Resolve() + " expires", Math.Max(0, (lt.disappearAtTick - now) / 2500.0), "letter"));
            }
        }

        public static JToken Digest(Map map)
        {
            return new JArray(NarratorClock.SortCap(Get(map).Items).Select(d => new JObject { ["what"] = d.What, ["hours"] = R1(d.Hours), ["kind"] = d.Kind }));
        }

        public static void AddFacts(JObject o, Map map)
        {
            var c = Get(map);
            if (c.DaysToWinter != null) o["days_to_winter"] = R1(c.DaysToWinter.Value);
            if (c.DaysToGrowing != null) o["days_to_growing"] = R1(c.DaysToGrowing.Value);
            if (c.BleedMin != null) o["bleed_out_hours"] = R1(c.BleedMin.Value);
            o["infections_losing"] = c.InfectionsLosing;
        }
    }
}

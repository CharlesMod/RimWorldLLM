using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM
{
    /// <summary>A clock the narrator should feel: what runs out, in how many hours, and what kind of clock it is.</summary>
    public class Deadline
    {
        public string What; public double Hours; public string Kind;
        public Deadline(string what, double hours, string kind) { What = what; Hours = hours; Kind = kind; }
    }

    /// <summary>Verse-free time-to-event helpers for the digest's `deadlines` block and its facts.</summary>
    public static class NarratorClock
    {
        public const int MaxDeadlines = 6;
        public const double DaysPerTwelfth = 5.0;
        public const double ColdC = 0.0, GrowC = 6.0;

        /// <summary>Sorted by hours (soonest first), the first <paramref name="max"/>; rows with no text or no finite hours are dropped.</summary>
        public static List<Deadline> SortCap(IEnumerable<Deadline> items, int max = MaxDeadlines)
        {
            if (items == null) return new List<Deadline>();
            return items.Where(d => d != null && !string.IsNullOrWhiteSpace(d.What) && !double.IsNaN(d.Hours) && !double.IsInfinity(d.Hours))
                .Select(d => new Deadline(d.What.Trim(), Math.Max(0, d.Hours), d.Kind))
                .OrderBy(d => d.Hours).ThenBy(d => d.What, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }

        /// <summary>An infection is a race: severity climbs toward <paramref name="lethal"/> while immunity climbs toward 1.
        /// Rates are per day (severity may be falling: negative). Winning means immunity gets there first (or the illness is fading);
        /// hours is the time to the winner's finish line, positive infinity when nothing moves.</summary>
        public static KeyValuePair<bool, double> ImmunityRace(double immunity, double immunityPerDay, double severity, double severityPerDay, double lethal = 1.0)
        {
            double toImmune = immunity >= 1.0 ? 0 : immunityPerDay > 1e-9 ? (1.0 - immunity) / immunityPerDay * 24.0 : double.PositiveInfinity;
            double toDeath = severity >= lethal ? 0 : severityPerDay > 1e-9 ? (lethal - severity) / severityPerDay * 24.0 : double.PositiveInfinity;
            if (double.IsInfinity(toDeath)) return new KeyValuePair<bool, double>(true, toImmune);
            if (toImmune <= toDeath) return new KeyValuePair<bool, double>(true, toImmune);
            return new KeyValuePair<bool, double>(false, toDeath);
        }

        static int Wrap(int i) { return ((i % 12) + 12) % 12; }

        static double? DaysTo(double[] means, int cur, double dayInTwelfth, Func<double, bool> hit)
        {
            if (means == null || means.Length != 12) return null;
            cur = Wrap(cur);
            if (hit(means[cur])) return 0;
            double left = Math.Max(0, DaysPerTwelfth - Math.Max(0, dayInTwelfth));
            for (int k = 1; k <= 12; k++)
                if (hit(means[Wrap(cur + k)])) return left + DaysPerTwelfth * (k - 1);
            return null;
        }

        /// <summary>Days until the first twelfth (5 days each) whose mean temperature is below 0 C; 0 when the current one is; null if never.</summary>
        public static double? DaysToCold(double[] twelfthMeans, int curTwelfth, double dayInTwelfth)
        { return DaysTo(twelfthMeans, curTwelfth, dayInTwelfth, t => t < ColdC); }

        /// <summary>Days until the first twelfth whose mean temperature is at least 6 C (crops grow); 0 when the current one is; null if never.</summary>
        public static double? DaysToWarm(double[] twelfthMeans, int curTwelfth, double dayInTwelfth)
        { return DaysTo(twelfthMeans, curTwelfth, dayInTwelfth, t => t >= GrowC); }

        /// <summary>True when the current twelfth is too cold for crops.</summary>
        public static bool TooColdToGrow(double[] twelfthMeans, int curTwelfth)
        { return twelfthMeans != null && twelfthMeans.Length == 12 && twelfthMeans[Wrap(curTwelfth)] < GrowC; }
    }
}

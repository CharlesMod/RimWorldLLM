using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM
{
    /// <summary>Why a building site or bill cannot go on (see <see cref="StuckReason"/>).</summary>
    public enum StuckKind { None, Forbidden, Unreachable, NoBuilder, StuffForbidden, NoMaterial, BillNoPower, BillNoWorker, BillNoIngredients }

    /// <summary>A reason: its kind (for counting) and its short English text.</summary>
    public struct StuckReason
    {
        public StuckKind Kind;
        public string Text;
        public StuckReason(StuckKind kind, string text) { Kind = kind; Text = text; }
        public bool IsNone { get { return Kind == StuckKind.None; } }
    }

    /// <summary>One material a site still needs, in total over every unbuilt site that wants it.</summary>
    public struct MaterialNeed
    {
        public string Label;
        public int Need, Usable, ForbiddenStock;
        public MaterialNeed(string label, int need, int usable, int forbiddenStock) { Label = label; Need = need; Usable = usable; ForbiddenStock = forbiddenStock; }
    }

    /// <summary>One stuck blueprint, frame or bill. Kind is "blueprint", "frame" or "bill".</summary>
    public struct StuckItem
    {
        public string Kind, Thing, Stuff;
        public StuckReason Reason;
        public int X, Z;
    }

    public class StuckRow
    {
        public string What, Reason, Dir;
        public int Count, Dist;
        public StuckKind Kind;
        public bool Bill;
    }

    public class StuckSummary
    {
        public List<StuckRow> Rows = new List<StuckRow>();
        /// <summary>Blueprints and frames in stuck groups (before the cap).</summary>
        public int BlueprintsStuck;
        /// <summary>Groups held up by material (before the cap).</summary>
        public int NoMaterialGroups;
        public bool NoBuilder;
        public int BillsStuck;
    }

    /// <summary>Verse-free helpers of the narrator's view of why things are loose, stuck, idle or impossible.</summary>
    public static class NarratorWork
    {
        public const int FarCells = 40, MaxStuck = 6, MaxCannot = 3, JobChars = 60;

        // ---- loose piles: where could they go ----

        /// <summary>"none": no storage accepts the item; "full": some does but no cell has room; "far": the best cell with
        /// room is more than <see cref="FarCells"/> from the pile; "ok" otherwise.</summary>
        public static string StoreVerdict(bool anyAccepts, bool foundCell, double distToCell)
        {
            if (!anyAccepts) return "none";
            if (!foundCell) return "full";
            return distToCell > FarCells ? "far" : "ok";
        }

        // ---- stuck sites ----

        static string Fmt(int n) { return n.ToString(System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>The first reason a blueprint or frame cannot be built, in the contract's order; None when nothing is wrong.
        /// <paramref name="materials"/> are the materials this site still needs (an empty list when it has all it needs).</summary>
        public static StuckReason SiteReason(bool forbidden, bool unreachable, bool noBuilder, IEnumerable<MaterialNeed> materials)
        {
            if (forbidden) return new StuckReason(StuckKind.Forbidden, "forbidden");
            if (unreachable) return new StuckReason(StuckKind.Unreachable, "unreachable");
            if (noBuilder) return new StuckReason(StuckKind.NoBuilder, "no one can build (construction off or incapable)");
            if (materials != null)
            {
                foreach (var m in materials)
                {
                    if (m.Need <= 0 || m.Usable >= m.Need) continue;
                    string l = string.IsNullOrWhiteSpace(m.Label) ? "materials" : m.Label.Trim();
                    if (m.ForbiddenStock > 0 && m.Usable + m.ForbiddenStock >= m.Need)
                        return new StuckReason(StuckKind.StuffForbidden, l + " is forbidden: unforbid it");
                    return new StuckReason(StuckKind.NoMaterial, "waiting on " + l + ": need " + Fmt(m.Need) + ", have " + Fmt(Math.Max(0, m.Usable)));
                }
            }
            return default(StuckReason);
        }

        /// <summary>Why a bill that should run now cannot: no power, nobody who does the work, a missing ingredient.</summary>
        public static StuckReason BillReason(bool benchNoPower, bool noWorker, string workType, string firstMissingIngredient)
        {
            if (benchNoPower) return new StuckReason(StuckKind.BillNoPower, "bench has no power");
            if (noWorker) return new StuckReason(StuckKind.BillNoWorker, "no one does " + (string.IsNullOrWhiteSpace(workType) ? "this work" : workType.Trim()));
            if (!string.IsNullOrWhiteSpace(firstMissingIngredient))
                return new StuckReason(StuckKind.BillNoIngredients, "no ingredients (" + firstMissingIngredient.Trim() + ")");
            return default(StuckReason);
        }

        /// <summary>"steel wall blueprints", "wall frames"; for bills the thing is already the whole phrase.</summary>
        public static string SiteWhat(string kind, string thing, string stuff)
        {
            string t = (thing ?? "").Trim();
            if (kind == "bill") return t;
            string s = (stuff ?? "").Trim();
            string name = s.Length > 0 && !t.StartsWith(s, StringComparison.OrdinalIgnoreCase) ? s + " " + t : t;
            return name + (kind == "frame" ? " frames" : " blueprints");
        }

        /// <summary>Groups stuck items by what + reason (items with no reason are dropped), biggest groups first (nearest on a
        /// tie), at most <paramref name="max"/> rows. Distance and direction are from home to the group's centre.</summary>
        public static StuckSummary GroupStuck(IEnumerable<StuckItem> items, int hx, int hz, int max = MaxStuck)
        {
            var sum = new StuckSummary();
            var groups = new Dictionary<string, StuckRow>();
            var acc = new Dictionary<string, double[]>();
            if (items != null)
                foreach (var it in items)
                {
                    if (it.Reason.IsNone || string.IsNullOrWhiteSpace(it.Thing)) continue;
                    string what = SiteWhat(it.Kind, it.Thing, it.Stuff);
                    string key = what.ToLowerInvariant() + "|" + it.Reason.Text;
                    StuckRow g;
                    if (!groups.TryGetValue(key, out g))
                    {
                        groups[key] = g = new StuckRow { What = what, Reason = it.Reason.Text, Kind = it.Reason.Kind, Bill = it.Kind == "bill" };
                        acc[key] = new double[3];
                    }
                    g.Count++;
                    var a = acc[key]; a[0] += it.X; a[1] += it.Z;
                }
            foreach (var kv in groups)
            {
                var g = kv.Value; var a = acc[kv.Key];
                double dx = a[0] / g.Count - hx, dz = a[1] / g.Count - hz;
                g.Dist = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
                g.Dir = BuildView.Direction(dx, dz);
                if (g.Bill) sum.BillsStuck += g.Count; else sum.BlueprintsStuck += g.Count;
                if (g.Kind == StuckKind.NoMaterial || g.Kind == StuckKind.StuffForbidden) sum.NoMaterialGroups++;
                if (g.Kind == StuckKind.NoBuilder) sum.NoBuilder = true;
            }
            sum.Rows = BuildView.Cap(groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Dist).ThenBy(g => g.What, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Reason, StringComparer.Ordinal), max);
            return sum;
        }

        // ---- what a person can't do ----

        /// <summary>Short words for capacities that block work (manipulation, moving, sight, talking), at most
        /// <see cref="MaxCannot"/>. Capacity levels are 0..1+ (1 = normal).</summary>
        public static List<string> CannotReasons(double manipulation, double moving, double sight, double talking)
        {
            var o = new List<string>();
            if (manipulation < 0.2) o.Add("no manipulation: can't build, cook or craft");
            else if (manipulation < 0.5) o.Add("weak hands");
            if (moving < 0.16) o.Add("can't walk");
            if (sight < 0.3) o.Add("can't see well");
            if (talking < 0.2) o.Add("can't talk");
            return BuildView.Cap(o, MaxCannot);
        }

        /// <summary>"Cooking: Ana can't (a, b)": the refusal for a disabled work type, at most two reasons.</summary>
        public static string WorkRefusal(string workLabel, string name, IEnumerable<string> reasons)
        {
            var r = (reasons ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().TrimEnd('.')).Distinct().Take(2).ToList();
            string head = workLabel + ": " + (string.IsNullOrWhiteSpace(name) ? "they" : name.Trim()) + " can't";
            return r.Count == 0 ? head : head + " (" + string.Join(", ", r) + ")";
        }

        // ---- doing vs scheduled ----

        static readonly HashSet<string> IdleJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Wait_Wander", "GotoWander", "Wait", "Wait_MaintainPosture", "IdleWhileDespawned"
        };

        /// <summary>True for no job (null or empty) and the idle or wandering jobs.</summary>
        public static bool IsIdleJob(string defName)
        {
            return string.IsNullOrWhiteSpace(defName) || IdleJobs.Contains(defName.Trim());
        }

        /// <summary>The job report cut to <paramref name="max"/> chars, with "..." when cut.</summary>
        public static string CutJob(string s, int max = JobChars)
        {
            s = (s ?? "").Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 3)).TrimEnd() + "...";
        }
    }
}

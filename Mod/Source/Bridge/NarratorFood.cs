using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM
{
    /// <summary>One stack of perishable food: label, count, days until it rots (at its current temperature).</summary>
    public class FoodStack
    {
        public string Label; public int Count; public double Days;
        public FoodStack(string label, int count, double days) { Label = label; Count = count; Days = days; }
    }

    /// <summary>Food about to rot: all stacks of one label, soonest first.</summary>
    public class RotGroup { public string Label; public int Count; public double Days; }

    /// <summary>One planted cell of a grow zone: the plant, whether it can be harvested now, and (if not) days until it can.</summary>
    public class CropCell
    {
        public string Plant; public bool Ready; public double Days;
        public CropCell(string plant, bool ready, double days) { Plant = plant; Ready = ready; Days = days; }
    }

    public class HarvestGroup { public string Plant; public int Cells; public int Ready; public double? Days; }

    public class GameAnimal
    {
        public string Label; public int X, Z;
        public GameAnimal(string label, int x, int z) { Label = label; X = x; Z = z; }
    }

    /// <summary>Wild animals of one kind, with the distance and direction of the nearest.</summary>
    public class GameGroup { public string Label; public int Count; public int Dist; public string Dir; }

    /// <summary>Verse-free helpers of the digest's `food` block: short summaries, never item lists.</summary>
    public static class NarratorFood
    {
        public const int MaxRot = 3, MaxHarvest = 3, MaxGame = 3;
        public const double RotWithinDays = 2.0;

        /// <summary>Groups perishable stacks by label; keeps groups that rot within <paramref name="withinDays"/>, soonest first.
        /// A group's days are those of its soonest stack; its count is the stacks that rot within the window.</summary>
        public static List<RotGroup> GroupRot(IEnumerable<FoodStack> stacks, double withinDays = RotWithinDays, int max = MaxRot)
        {
            var acc = new Dictionary<string, RotGroup>(StringComparer.OrdinalIgnoreCase);
            if (stacks != null)
                foreach (var s in stacks)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Label) || s.Count <= 0 || double.IsNaN(s.Days) || s.Days >= withinDays) continue;
                    var days = Math.Max(0, s.Days);
                    RotGroup g;
                    if (!acc.TryGetValue(s.Label.Trim(), out g)) acc[s.Label.Trim()] = new RotGroup { Label = s.Label.Trim(), Count = s.Count, Days = days };
                    else { g.Count += s.Count; if (days < g.Days) g.Days = days; }
                }
            return acc.Values.OrderBy(g => g.Days).ThenByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }

        /// <summary>Per plant: planted cells, cells harvestable now, and the soonest days until one of the rest is. Biggest plants first.</summary>
        public static List<HarvestGroup> GroupHarvest(IEnumerable<CropCell> cells, int max = MaxHarvest)
        {
            var acc = new Dictionary<string, HarvestGroup>(StringComparer.OrdinalIgnoreCase);
            if (cells != null)
                foreach (var c in cells)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Plant)) continue;
                    HarvestGroup g;
                    if (!acc.TryGetValue(c.Plant.Trim(), out g)) acc[c.Plant.Trim()] = g = new HarvestGroup { Plant = c.Plant.Trim() };
                    g.Cells++;
                    if (c.Ready) g.Ready++;
                    else if (!double.IsNaN(c.Days) && !double.IsInfinity(c.Days) && c.Days >= 0 && (g.Days == null || c.Days < g.Days)) g.Days = c.Days;
                }
            return acc.Values.OrderByDescending(g => g.Cells).ThenBy(g => g.Plant, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }

        /// <summary>Wild animals by kind; each group reports the distance and direction (from home) of its nearest animal. Nearest group first.</summary>
        public static List<GameGroup> GroupGame(IEnumerable<GameAnimal> animals, int hx, int hz, int max = MaxGame)
        {
            var acc = new Dictionary<string, GameGroup>(StringComparer.OrdinalIgnoreCase);
            if (animals != null)
                foreach (var a in animals)
                {
                    if (a == null || string.IsNullOrWhiteSpace(a.Label)) continue;
                    double dx = a.X - hx, dz = a.Z - hz;
                    int d = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
                    GameGroup g;
                    if (!acc.TryGetValue(a.Label.Trim(), out g)) acc[a.Label.Trim()] = new GameGroup { Label = a.Label.Trim(), Count = 1, Dist = d, Dir = BuildView.Direction(dx, dz) };
                    else { g.Count++; if (d < g.Dist) { g.Dist = d; g.Dir = BuildView.Direction(dx, dz); } }
                }
            return acc.Values.OrderBy(g => g.Dist).ThenByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
    }
}

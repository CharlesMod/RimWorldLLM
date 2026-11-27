using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM
{
    /// <summary>Plain counts for <see cref="BuildView.Missing"/> (no game types: pure and tested).</summary>
    public class MissingInput
    {
        public int Colonists;
        /// <summary>Colonists who do not sleep in a roofed, enclosed room.</summary>
        public int ColonistsWithoutRoofedRoom;
        /// <summary>Colonists with no bed of their own.</summary>
        public int ColonistsWithoutBed;
        /// <summary>Beds nobody owns yet.</summary>
        public int BedsFree;
        public bool HasStove;
        public bool HasFoodStockpile;
        public bool HasFreezer;
        public bool CanBuildCooler;
        public bool HasDiningTable;
        public bool HasGrowZone;
        public bool HasResearchBench;
    }

    /// <summary>One resource-rock cell (the mineable thing's label, its position).</summary>
    public struct OreCell
    {
        public string Ore;
        public int X, Z;
        public OreCell(string ore, int x, int z) { Ore = ore; X = x; Z = z; }
    }

    public class OreGroup
    {
        public string Ore;
        public int Cells;
        /// <summary>Distance in cells from home to the nearest cell of this ore.</summary>
        public int Dist;
    }

    /// <summary>One loose stack of a material (its label, cell, count, whether the player forbade it).</summary>
    public struct LooseItem
    {
        public string Label;
        public int X, Z, Count;
        public bool Forbidden;
        public LooseItem(string label, int x, int z, int count, bool forbidden) { Label = label; X = x; Z = z; Count = count; Forbidden = forbidden; }
    }

    /// <summary>A summarised pile of loose material: what, how much, how far from home, which way.</summary>
    public class LoosePile
    {
        public string Label;
        public int Count;
        /// <summary>Cells from home to the pile's centre.</summary>
        public int Dist;
        /// <summary>8-point compass (N, NE, E, SE, S, SW, W, NW; north is +z); empty when at home.</summary>
        public string Dir;
        public bool Forbidden;
    }

    /// <summary>Verse-free helpers of the digest's `build` block.</summary>
    public static class BuildView
    {
        public const int MaxMissing = 8, MaxOres = 6, OreRadius = 60, MaxPiles = 6, PileCell = 12;

        /// <summary>What the colony still lacks, in the playbook's build order (shelter, beds, stove, food stockpile,
        /// freezer, dining table, growing zone, research bench). Empty when there are no colonists.</summary>
        public static List<string> Missing(MissingInput i)
        {
            var o = new List<string>();
            if (i == null || i.Colonists <= 0) return o;
            if (i.ColonistsWithoutRoofedRoom > 0)
                o.Add(i.ColonistsWithoutRoofedRoom >= i.Colonists ? "shelter" : "shelter for " + Plural(i.ColonistsWithoutRoofedRoom, "colonist"));
            int needBeds = Math.Max(0, i.ColonistsWithoutBed - Math.Max(0, i.BedsFree));
            if (needBeds > 0) o.Add("beds for " + Plural(needBeds, "colonist"));
            if (!i.HasStove) o.Add("stove");
            if (!i.HasFoodStockpile) o.Add("food stockpile");
            if (!i.HasFreezer && i.CanBuildCooler) o.Add("freezer");
            if (!i.HasDiningTable) o.Add("dining table");
            if (!i.HasGrowZone) o.Add("growing zone");
            if (!i.HasResearchBench) o.Add("research bench");
            return Cap(o, MaxMissing);
        }

        static string Plural(int n, string noun) { return n + " " + noun + (n == 1 ? "" : "s"); }

        /// <summary>The first <paramref name="max"/> items (a copy).</summary>
        public static List<T> Cap<T>(IEnumerable<T> items, int max)
        {
            return items == null || max <= 0 ? new List<T>() : items.Take(max).ToList();
        }

        /// <summary>Mean of the points, rounded; null when there are none.</summary>
        public static int[] HomeCenter(IList<int[]> points)
        {
            if (points == null || points.Count == 0) return null;
            long sx = 0, sz = 0;
            foreach (var p in points) { sx += p[0]; sz += p[1]; }
            return new[] { (int)Math.Round((double)sx / points.Count), (int)Math.Round((double)sz / points.Count) };
        }

        /// <summary>Groups ore cells within <paramref name="radius"/> of (hx,hz) by ore, nearest group first, at most
        /// <paramref name="max"/> groups.</summary>
        public static List<OreGroup> GroupOres(IEnumerable<OreCell> cells, int hx, int hz, int radius = OreRadius, int max = MaxOres)
        {
            var groups = new Dictionary<string, OreGroup>(StringComparer.OrdinalIgnoreCase);
            if (cells != null)
                foreach (var c in cells)
                {
                    if (string.IsNullOrWhiteSpace(c.Ore)) continue;
                    int d = (int)Math.Round(Math.Sqrt((double)(c.X - hx) * (c.X - hx) + (double)(c.Z - hz) * (c.Z - hz)));
                    if (d > radius) continue;
                    OreGroup g;
                    if (!groups.TryGetValue(c.Ore, out g)) groups[c.Ore] = g = new OreGroup { Ore = c.Ore, Dist = d };
                    g.Cells++;
                    if (d < g.Dist) g.Dist = d;
                }
            return Cap(groups.Values.OrderBy(g => g.Dist).ThenByDescending(g => g.Cells).ThenBy(g => g.Ore, StringComparer.OrdinalIgnoreCase), max);
        }

        static readonly string[] Compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        /// <summary>8-point compass word of the offset (dx, dz); north is +z. Empty for no offset.</summary>
        public static string Direction(double dx, double dz)
        {
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dz) < 1e-9) return "";
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;   // 0 = north, clockwise
            if (deg < 0) deg += 360.0;
            return Compass[(int)Math.Round(deg / 45.0) % 8];
        }

        /// <summary>Groups loose items into piles: one per label, forbidden or not, and grid square (<paramref name="cell"/>
        /// cells wide, counted from home). A pile's distance and direction are those of its count-weighted centre. The
        /// largest <paramref name="max"/> piles come back, biggest first (nearest first on a tie).</summary>
        public static List<LoosePile> GroupLoose(IEnumerable<LooseItem> items, int hx, int hz, int cell = PileCell, int max = MaxPiles)
        {
            if (cell < 1) cell = PileCell;
            var acc = new Dictionary<string, double[]>();   // sum count, sum x*count, sum z*count
            var meta = new Dictionary<string, KeyValuePair<string, bool>>();
            if (items != null)
                foreach (var it in items)
                {
                    if (string.IsNullOrWhiteSpace(it.Label) || it.Count <= 0) continue;
                    int gx = (int)Math.Floor((double)(it.X - hx) / cell), gz = (int)Math.Floor((double)(it.Z - hz) / cell);
                    string key = it.Label.Trim().ToLowerInvariant() + "|" + (it.Forbidden ? 1 : 0) + "|" + gx + "|" + gz;
                    double[] a;
                    if (!acc.TryGetValue(key, out a)) { acc[key] = a = new double[3]; meta[key] = new KeyValuePair<string, bool>(it.Label.Trim(), it.Forbidden); }
                    a[0] += it.Count; a[1] += (double)it.X * it.Count; a[2] += (double)it.Z * it.Count;
                }
            var piles = new List<LoosePile>();
            foreach (var kv in acc)
            {
                double dx = kv.Value[1] / kv.Value[0] - hx, dz = kv.Value[2] / kv.Value[0] - hz;
                piles.Add(new LoosePile
                {
                    Label = meta[kv.Key].Key, Forbidden = meta[kv.Key].Value, Count = (int)kv.Value[0],
                    Dist = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz)), Dir = Direction(dx, dz)
                });
            }
            return Cap(piles.OrderByDescending(p => p.Count).ThenBy(p => p.Dist).ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Forbidden), max);
        }
    }
}

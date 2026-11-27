using System;
using System.Collections.Generic;
using System.Linq;

// Verse-free logic the phase 2 game hands share (tested in Mod.Tests): choosing stuff by stock, site scoring,
// nearest-first ordering, mark filtering and the plain-English notes.
namespace RimWorldLLM.Bridge
{
    public static class BuildHelpers
    {
        /// <summary>Mean of flat x,z pairs rounded to the nearest cell; false when empty.</summary>
        public static bool Mean(IEnumerable<int[]> cells, out int x, out int z)
        {
            long sx = 0, sz = 0; int n = 0;
            foreach (var c in cells) { sx += c[0]; sz += c[1]; n++; }
            if (n == 0) { x = z = 0; return false; }
            x = (int)Math.Round((double)sx / n, MidpointRounding.AwayFromZero); z = (int)Math.Round((double)sz / n, MidpointRounding.AwayFromZero);
            return true;
        }

        public static int Dist2(int ax, int az, int bx, int bz) { int dx = ax - bx, dz = az - bz; return dx * dx + dz * dz; }

        /// <summary>Cells nearest the centre first (stable for ties), at most max.</summary>
        public static List<int[]> NearestFirst(IEnumerable<int[]> cells, int cx, int cz, int max)
        {
            return cells.Select((c, i) => new { c, i, d = Dist2(c[0], c[1], cx, cz) })
                .OrderBy(t => t.d).ThenBy(t => t.i).Take(Math.Max(0, max)).Select(t => t.c).ToList();
        }

        /// <summary>Rotations to try, south first (the hand's default when the narrator gave none).</summary>
        public static int[] RotOrder(int given)
        {
            if (given >= 0 && given <= 3) return new[] { given };
            return new[] { 2, 0, 1, 3 };
        }

        public static readonly string[] WallStuffPreference = { "Blocks", "WoodLog", "Steel" };

        /// <summary>Rank of a stuff defName for walls: stone blocks 0, wood 1, steel 2, anything else 3.</summary>
        public static int WallRank(string stuffDef)
        {
            if (string.IsNullOrEmpty(stuffDef)) return 3;
            if (stuffDef.StartsWith("Blocks", StringComparison.Ordinal)) return 0;
            if (stuffDef == "WoodLog") return 1;
            if (stuffDef == "Steel") return 2;
            return 3;
        }

        /// <summary>The stuff to build with: among allowed defNames, those in stock with at least need; best by rank
        /// (when ranked is true: stone blocks, wood, steel), then most plentiful. If none has enough, the most plentiful
        /// with any stock; null when nothing is in stock.</summary>
        public static string PickStuff(IList<KeyValuePair<string, int>> allowedWithCounts, int need, bool ranked)
        {
            var inStock = allowedWithCounts.Where(k => k.Value > 0).ToList();
            if (inStock.Count == 0) return null;
            var enough = inStock.Where(k => k.Value >= need).ToList();
            if (enough.Count > 0)
            {
                var best = ranked
                    ? enough.OrderBy(k => WallRank(k.Key)).ThenByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                    : enough.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal);
                return best.First().Key;
            }
            return inStock.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).First().Key;
        }

        /// <summary>How many stuff items a wall costs (vanilla walls: 5 stuff each).</summary>
        public const int WallStuffCost = 5;

        /// <summary>"no thing 'stoev' (try: stove, wall, ...)": the word and up to n known words, closest first.</summary>
        public static string Unknown(string kind, string word, IEnumerable<string> known, int n = 6)
        {
            string w = (word ?? "").Trim().ToLowerInvariant();
            var ks = known.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ranked = ks.OrderBy(k => Lev(k.ToLowerInvariant(), w)).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).Take(n).ToList();
            string s = "no " + kind + " '" + (word ?? "").Trim() + "'";
            if (ranked.Count > 0) s += " (try: " + string.Join(", ", ranked) + ")";
            return s;
        }

        public static int Lev(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>Wall-adjacency score of an inside cell of a room: 2 against a wall (not diagonal), 0 in the open.
        /// Cells beside the door cell score -1 so the door stays clear.</summary>
        public static int WallScore(int x, int z, CellBox room, int doorX, int doorZ)
        {
            int ix0 = room.X + 1, ix1 = room.MaxX - 1, iz0 = room.Z + 1, iz1 = room.MaxZ - 1;
            if (x < ix0 || x > ix1 || z < iz0 || z > iz1) return -1;
            if (Math.Abs(x - doorX) + Math.Abs(z - doorZ) <= 1) return -1;
            bool atWall = x == ix0 || x == ix1 || z == iz0 || z == iz1;
            return atWall ? 2 : 0;
        }

        /// <summary>Does a w x h footprint with its south-west cell at (x,z) lie inside the map?</summary>
        public static bool InMap(int x, int z, int w, int h, int mapW, int mapH) { return x >= 0 && z >= 0 && x + w <= mapW && z + h <= mapH; }

        /// <summary>The cells of a circle of the given radius (grid distance squared), clipped to the map.</summary>
        public static List<int[]> Circle(int cx, int cz, int r, int mapW, int mapH)
        {
            var l = new List<int[]>();
            for (int z = cz - r; z <= cz + r; z++)
                for (int x = cx - r; x <= cx + r; x++)
                    if (x >= 0 && z >= 0 && x < mapW && z < mapH && Dist2(x, z, cx, cz) <= r * r) l.Add(new[] { x, z });
            return l;
        }

        /// <summary>Cells of a where: box, circle (x,z,radius) or null when neither (clipped to the map).</summary>
        public static List<int[]> WhereCells(Where w, int mapW, int mapH)
        {
            if (w == null) return null;
            if (w.Box != null)
            {
                var l = new List<int[]>();
                foreach (var c in BuildPure.BoxCells(w.Box)) if (c[0] >= 0 && c[1] >= 0 && c[0] < mapW && c[1] < mapH) l.Add(c);
                return l;
            }
            if (w.X != null && w.Z != null && w.Radius > 0) return Circle(w.X.Value, w.Z.Value, w.Radius, mapW, mapH);
            return null;
        }

        /// <summary>One blueprint mark per def: root cells collected in order, ids kept alongside.</summary>
        public sealed class MarkBuilder
        {
            readonly int _map;
            readonly List<NarratorMark> _marks = new List<NarratorMark>();
            public MarkBuilder(int map) { _map = map; }
            public List<NarratorMark> Marks { get { return _marks; } }

            public void Blueprint(string def, int x, int z, int thingId)
            {
                var m = _marks.FirstOrDefault(k => k.Kind == "blueprint" && k.Def == def);
                if (m == null) { m = new NarratorMark { Kind = "blueprint", Map = _map, Def = def }; _marks.Add(m); }
                m.AddCell(x, z);
                if (thingId != 0) m.Things.Add(thingId);
            }

            public void Designation(string def, int thingId, int x, int z)
            {
                var m = _marks.FirstOrDefault(k => k.Kind == "designation" && k.Def == def);
                if (m == null) { m = new NarratorMark { Kind = "designation", Map = _map, Def = def }; _marks.Add(m); }
                if (thingId != 0) m.Things.Add(thingId); else m.AddCell(x, z);
            }

            public int CountOf(string kind) { return _marks.Where(m => m.Kind == kind).Sum(m => Math.Max(m.CellCount, m.Things.Count)); }
        }

        /// <summary>Plain count of what a list of marks holds, for the note: "3 wall blueprints".</summary>
        public static string Plural(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        /// <summary>"placed a 5x5 bedroom (15 granite walls, a door) at (118,101)".</summary>
        public static string RoomNote(string purpose, CellBox b, int walls, string stuffLabel, bool doorPlaced)
        {
            string s = "placed a " + b.W + "x" + b.H + " " + (string.IsNullOrEmpty(purpose) ? "room" : purpose) + " (" + walls + " " +
                       (string.IsNullOrEmpty(stuffLabel) ? "" : stuffLabel + " ") + (walls == 1 ? "wall" : "walls") + (doorPlaced ? ", a door" : "") + ")";
            return s + " at (" + b.X + "," + b.Z + ")";
        }

        /// <summary>The bill mode text of the benches listing: "forever", "count 5", "until 15".</summary>
        public static string BillMode(string repeatMode, int repeatCount, int targetCount)
        {
            switch (repeatMode)
            {
                case "count": return "count " + repeatCount;
                case "until": return "until " + targetCount;
                default: return "forever";
            }
        }

        /// <summary>"cancelled 15 blueprints, removed 1 zone, 3 already built (left)".</summary>
        public static string UndoNote(int cancelled, int designations, int zones, int zoneCells, int areaCells, int bills, int built, int missing)
        {
            var p = new List<string>();
            if (cancelled > 0) p.Add("cancelled " + Plural(cancelled, "blueprint", "blueprints"));
            if (designations > 0) p.Add("removed " + Plural(designations, "designation", "designations"));
            if (zones > 0) p.Add("removed " + Plural(zones, "zone", "zones"));
            if (zoneCells > 0) p.Add("took " + Plural(zoneCells, "cell", "cells") + " out of zones");
            if (areaCells > 0) p.Add("cleared " + Plural(areaCells, "area cell", "area cells"));
            if (bills > 0) p.Add("deleted " + Plural(bills, "bill", "bills"));
            if (built > 0) p.Add(built + " already built (left)");
            if (missing > 0) p.Add(missing + " already gone");
            return p.Count == 0 ? "nothing left to undo" : string.Join(", ", p);
        }

        /// <summary>Marks to record for a deconstruct/cancel skip report: "skipped 3 (2 beds, 1 grave)".</summary>
        public static string SkipNote(IDictionary<string, int> reasons)
        {
            if (reasons == null || reasons.Count == 0) return "";
            int total = reasons.Values.Sum();
            return "skipped " + total + " (" + string.Join(", ", reasons.Select(k => k.Value + " " + k.Key)) + ")";
        }

        /// <summary>Of marks, those on a given map.</summary>
        public static List<NarratorMark> OnMap(IEnumerable<NarratorMark> marks, int map) { return marks.Where(m => m.Map == map).ToList(); }

        /// <summary>Flat cells pairs to cell list.</summary>
        public static List<int[]> CellPairs(NarratorMark m)
        {
            var l = new List<int[]>();
            for (int i = 0; i + 1 < m.Cells.Count; i += 2) l.Add(new[] { m.Cells[i], m.Cells[i + 1] });
            return l;
        }

        /// <summary>Score a candidate growing site by its cell fertilities: -1 if any cell is under minFertility,
        /// else the mean (higher is better).</summary>
        public static double SoilScore(IEnumerable<double> fertilities, double minFertility)
        {
            double sum = 0; int n = 0;
            foreach (var f in fertilities) { if (f < minFertility) return -1; sum += f; n++; }
            return n == 0 ? -1 : sum / n;
        }

        /// <summary>Is a distance from home within the dump ring 12..30 cells?</summary>
        public static bool InDumpRing(int x, int z, int hx, int hz)
        {
            int d = Dist2(x, z, hx, hz);
            return d >= 12 * 12 && d <= 30 * 30;
        }

        /// <summary>Candidate corners for a dump box: SiteCandidates around home widened to the ring, filtered to 12..30.</summary>
        public static IEnumerable<int[]> DumpCandidates(int hx, int hz, int w, int h)
        {
            foreach (var c in BuildPure.SiteCandidates(hx, hz, w, h, 30))
                if (InDumpRing(c[0] + w / 2, c[1] + h / 2, hx, hz)) yield return c;
        }

        /// <summary>Case-insensitive recipe match: defName first, then label.</summary>
        public static int FindByNameOrLabel(IList<KeyValuePair<string, string>> defAndLabel, string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return -1;
            string w = word.Trim();
            for (int i = 0; i < defAndLabel.Count; i++) if (string.Equals(defAndLabel[i].Key, w, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < defAndLabel.Count; i++) if (string.Equals(defAndLabel[i].Value, w, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Cell cap for a stockpile in a room: left cells and 200.</summary>
        public static int StockpileCap(int leftCells) { return Math.Max(0, Math.Min(leftCells, 200)); }
    }
}

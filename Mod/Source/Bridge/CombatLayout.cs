using System;
using System.Collections.Generic;
using System.Linq;

// Verse-free defense layouts for Vivarium phase 3: killbox geometry, straight lines, trap and turret spots.
namespace RimWorldLLM.Bridge
{
    public sealed class Layout
    {
        public List<int[]> Walls = new List<int[]>(), Sandbags = new List<int[]>(), Traps = new List<int[]>(), Turrets = new List<int[]>(), Shooters = new List<int[]>();
    }

    public static class DefenseLayout
    {
        // unit vector of a side letter; false when unknown
        static bool Dir(string d, out int dx, out int dz)
        {
            dx = dz = 0;
            switch ((d ?? "").ToLowerInvariant())
            {
                case "n": dz = 1; return true;
                case "s": dz = -1; return true;
                case "e": dx = 1; return true;
                case "w": dx = -1; return true;
            }
            return false;
        }

        /// <summary>Killbox in front of the door at (dx,dz); outward = the side the enemy comes from. Unknown side: empty layout.
        /// Outside: funnel cells 1..funnel out from the door, wall arms on both sides of each funnel cell, traps on funnel cells 2,4,.. (at most maxTraps).
        /// Inside: killzone depth cells behind the door; sandbag line `width` wide centred at depth+1; shooters on the row just behind (depth+2),
        /// one per sandbag, middle first (centre, then alternating to either side); turrets one cell past each end of the sandbag line.</summary>
        public static Layout Killbox(int dx, int dz, string outward, int funnel = 5, int depth = 5, int width = 7, int maxTraps = 3)
        {
            var l = new Layout();
            int ox, oz;
            if (!Dir(outward, out ox, out oz)) return l;
            int px = -oz, pz = ox; // perpendicular
            for (int k = 1; k <= funnel; k++)
            {
                int fx = dx + ox * k, fz = dz + oz * k;
                l.Walls.Add(new[] { fx + px, fz + pz });
                l.Walls.Add(new[] { fx - px, fz - pz });
                if (k >= 2 && k % 2 == 0 && l.Traps.Count < maxTraps) l.Traps.Add(new[] { fx, fz });
            }
            int sbRow = depth + 1;
            int half = width / 2;
            int bx = dx - ox * sbRow, bz = dz - oz * sbRow;
            for (int o = -half; o < -half + width; o++) l.Sandbags.Add(new[] { bx + px * o, bz + pz * o });
            int sx = dx - ox * (depth + 2), sz = dz - oz * (depth + 2);
            var offs = new List<int> { 0 };
            for (int o = 1; offs.Count < width; o++) { offs.Add(-o); if (offs.Count < width) offs.Add(o); }
            foreach (int o in offs) l.Shooters.Add(new[] { sx + px * o, sz + pz * o });
            int endLo = -half, endHi = -half + width - 1;
            l.Turrets.Add(new[] { bx + px * (endLo - 1), bz + pz * (endLo - 1) });
            l.Turrets.Add(new[] { bx + px * (endHi + 1), bz + pz * (endHi + 1) });
            return l;
        }

        /// <summary>n cells in a straight line perpendicular to facing (the enemy side), centred on (cx,cz) (the line starts -(n-1)/2 cells from the centre, so an even n has its extra cell on the far side).</summary>
        public static List<int[]> LineAcross(int cx, int cz, string facing, int n)
        {
            var r = new List<int[]>();
            int ox, oz;
            if (!Dir(facing, out ox, out oz) || n <= 0) return r;
            int px = -oz, pz = ox, start = -((n - 1) / 2);
            for (int i = 0; i < n; i++) r.Add(new[] { cx + px * (start + i), cz + pz * (start + i) });
            return r;
        }

        /// <summary>Path cells to trap: index skip, skip+every, ... at most n, never a door cell.</summary>
        public static List<int[]> TrapsOnPath(CombatGrid g, List<int[]> path, int n, int skip = 3, int every = 2)
        {
            var r = new List<int[]>();
            if (path == null) return r;
            every = Math.Max(1, every);
            for (int i = Math.Max(0, skip); i < path.Count && r.Count < n; i += every)
                if ((g.F(path[i][0], path[i][1]) & CellFlag.Door) == 0) r.Add(path[i]);
            return r;
        }

        static bool Ok(CombatGrid g, int x, int z)
        {
            if (!g.In(x, z)) return false;
            var f = g.F(x, z);
            return (f & CellFlag.Buildable) != 0 && (f & CellFlag.Walkable) != 0 && (f & (CellFlag.Fogged | CellFlag.Door)) == 0;
        }

        public static List<int[]> Buildable(CombatGrid g, IEnumerable<int[]> cells, out int dropped)
        {
            var kept = new List<int[]>(); dropped = 0;
            foreach (var c in cells) { if (Ok(g, c[0], c[1])) kept.Add(c); else dropped++; }
            return kept;
        }

        /// <summary>Up to n buildable, non-indoor cells within radius of (cx,cz) that see the approach path (clear line to at least one path
        /// cell within range), at least 3 apart (Euclidean). Best = most path cells seen, then nearest the anchor, then z, x.</summary>
        public static List<int[]> TurretSpots(CombatGrid g, int cx, int cz, int radius, List<int[]> approachPath, int range, int n)
        {
            var res = new List<int[]>();
            if (n <= 0 || approachPath == null) return res;
            int r2 = radius * radius, range2 = range * range;
            var cands = new List<int[]>(); // x, z, seen, dist2
            for (int z = cz - radius; z <= cz + radius; z++)
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    int d2 = BuildHelpers.Dist2(x, z, cx, cz);
                    if (d2 > r2 || !Ok(g, x, z) || (g.F(x, z) & CellFlag.Indoors) != 0) continue;
                    int seen = 0;
                    foreach (var p in approachPath)
                        if (BuildHelpers.Dist2(x, z, p[0], p[1]) <= range2 && LineOfFire.Clear(g, x, z, p[0], p[1])) seen++;
                    if (seen > 0) cands.Add(new[] { x, z, seen, d2 });
                }
            foreach (var c in cands.OrderByDescending(c => c[2]).ThenBy(c => c[3]).ThenBy(c => c[1]).ThenBy(c => c[0]))
            {
                if (res.Count >= n) break;
                if (res.Any(r => BuildHelpers.Dist2(r[0], r[1], c[0], c[1]) < 9)) continue;
                res.Add(new[] { c[0], c[1] });
            }
            return res;
        }
    }
}

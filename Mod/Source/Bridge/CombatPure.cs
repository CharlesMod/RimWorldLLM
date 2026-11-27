using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Verse-free combat logic for Vivarium phase 3 (tested in Mod.Tests): cell grid, line of fire, cover, position finder,
// approach path and chokepoint, threat grouping and words, and the squad's tiny tactics brain.
// Coordinates are RimWorld cells: x east, z north.
namespace RimWorldLLM.Bridge
{
    [Flags]
    public enum CellFlag : byte { None = 0, Walkable = 1, BlocksSight = 2, Roofed = 4, Indoors = 8, Fogged = 16, Door = 32, Buildable = 64 }

    public sealed class CombatGrid
    {
        public readonly int W, H;
        public CellFlag[] Flags;   // index z*W+x
        public float[] Cover;      // 0..1 cover a thing in this cell gives a pawn standing next to it

        public CombatGrid(int w, int h)
        {
            W = Math.Max(0, w); H = Math.Max(0, h);
            Flags = new CellFlag[W * H]; Cover = new float[W * H];
        }

        public bool In(int x, int z) { return x >= 0 && z >= 0 && x < W && z < H; }
        public int Idx(int x, int z) { return z * W + x; }
        public CellFlag F(int x, int z) { return In(x, z) ? Flags[Idx(x, z)] : CellFlag.None; }
        public bool Walk(int x, int z) { return (F(x, z) & CellFlag.Walkable) != 0; }
        /// <summary>True = sight passes. Off the grid nothing passes.</summary>
        public bool Sight(int x, int z) { return In(x, z) && (Flags[Idx(x, z)] & CellFlag.BlocksSight) == 0; }
        public float CoverAt(int x, int z) { return In(x, z) ? Cover[Idx(x, z)] : 0f; }

        public void Set(int x, int z, CellFlag f, float cover = 0f)
        {
            if (!In(x, z)) return;
            Flags[Idx(x, z)] = f; Cover[Idx(x, z)] = cover;
        }

        /// <summary>Test helper. First string is the top row (highest z). '.' open buildable, '#' wall (blocks sight, cover .75, not walkable),
        /// 's' sandbag (not walkable, cover .55, sight passes), 'D' door (walkable), 'r' roofed indoor floor (walkable, buildable),
        /// '~' unwalkable no-cover (water), 'f' fogged walkable. Anything else is treated as '.'.</summary>
        public static CombatGrid FromAscii(params string[] rowsNorthFirst)
        {
            int h = rowsNorthFirst.Length, w = 0;
            foreach (var r in rowsNorthFirst) w = Math.Max(w, r.Length);
            var g = new CombatGrid(w, h);
            for (int row = 0; row < h; row++)
            {
                int z = h - 1 - row;
                string s = rowsNorthFirst[row];
                for (int x = 0; x < w; x++)
                {
                    char c = x < s.Length ? s[x] : '~';
                    switch (c)
                    {
                        case '#': g.Set(x, z, CellFlag.BlocksSight, 0.75f); break;
                        case 's': g.Set(x, z, CellFlag.None, 0.55f); break;
                        case 'D': g.Set(x, z, CellFlag.Door | CellFlag.Walkable); break;
                        case 'r': g.Set(x, z, CellFlag.Walkable | CellFlag.Roofed | CellFlag.Indoors | CellFlag.Buildable); break;
                        case '~': g.Set(x, z, CellFlag.None); break;
                        case 'f': g.Set(x, z, CellFlag.Walkable | CellFlag.Fogged); break;
                        default: g.Set(x, z, CellFlag.Walkable | CellFlag.Buildable); break;
                    }
                }
            }
            return g;
        }
    }

    public static class LineOfFire
    {
        /// <summary>Bresenham cells from a to b, both endpoints included.</summary>
        public static IEnumerable<int[]> Line(int ax, int az, int bx, int bz)
        {
            int dx = Math.Abs(bx - ax), dz = Math.Abs(bz - az);
            int sx = ax < bx ? 1 : -1, sz = az < bz ? 1 : -1;
            int err = dx - dz, x = ax, z = az;
            while (true)
            {
                yield return new[] { x, z };
                if (x == bx && z == bz) yield break;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x += sx; }
                if (e2 < dx) { err += dx; z += sz; }
            }
        }

        /// <summary>Endpoints never block; any middle cell that blocks sight does. Sandbags never block.</summary>
        public static bool Clear(CombatGrid g, int ax, int az, int bx, int bz)
        {
            foreach (var c in Line(ax, az, bx, bz))
            {
                if ((c[0] == ax && c[1] == az) || (c[0] == bx && c[1] == bz)) continue;
                if (!g.Sight(c[0], c[1])) return false;
            }
            return true;
        }
    }

    public static class CoverCalc
    {
        /// <summary>Best cover among the 8 neighbours of (x,z) lying within 45 degrees of the direction to (fromX,fromZ).
        /// Under 2 cells from the shooter: 0 (point blank).</summary>
        public static float CoverFrom(CombatGrid g, int x, int z, int fromX, int fromZ)
        {
            double wx = fromX - x, wz = fromZ - z;
            double wl = Math.Sqrt(wx * wx + wz * wz);
            if (wl < 2.0) return 0f;
            float best = 0f;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    double vl = Math.Sqrt(dx * dx + dz * dz);
                    double cos = (dx * wx + dz * wz) / (vl * wl);
                    if (cos < Math.Cos(Math.PI / 4) - 1e-6) continue;
                    float c = g.CoverAt(x + dx, z + dz);
                    if (c > best) best = c;
                }
            return best;
        }
    }

    public sealed class Spot { public int X, Z; public float Cover; public bool SeesApproach; public double Score; }

    public static class CoverFinder
    {
        /// <summary>Pick up to n distinct standing cells near the anchor from which pawns fight the threat.
        /// Candidates: Walk, not Fogged, not Door, more than 2 cells from every threat cell, within radius of the anchor.
        /// Tier 0: cover toward the threat centroid. Tier 1: cover toward some threat cell (Cover reported 0). Tier 2: Indoors (Cover 0).
        /// Tier 3 (only allowOpen): anything else (Cover 0). Tiers are filled in order, never open ground unless allowOpen.
        /// Within a tier: score = 3*cover + 2*(SeesApproach ? 1 : 0) - 0.08*dist(spot, anchor), where cover is the real value toward the
        /// centroid (tier 0) or toward the best threat (tier 1). Best score first, ties by z then x. A first pass takes spots that are not
        /// 8-adjacent to an already chosen one; a second pass fills the rest from the adjacent ones.</summary>
        public static List<Spot> Find(CombatGrid g, int anchorX, int anchorZ, int radius, IList<int[]> threatCells, IList<int[]> approachPath, int range, int n, bool allowOpen = false)
        {
            var result = new List<Spot>();
            if (n <= 0) return result;
            threatCells = threatCells ?? new List<int[]>();
            approachPath = approachPath ?? new List<int[]>();
            int cx = 0, cz = 0; bool hasCentroid = BuildHelpers.Mean(threatCells, out cx, out cz);
            int r2 = radius * radius, range2 = range * range;
            var cands = new List<Tuple<int, Spot>>();
            for (int z = anchorZ - radius; z <= anchorZ + radius; z++)
                for (int x = anchorX - radius; x <= anchorX + radius; x++)
                {
                    if (!g.In(x, z) || BuildHelpers.Dist2(x, z, anchorX, anchorZ) > r2) continue;
                    var f = g.F(x, z);
                    if ((f & CellFlag.Walkable) == 0 || (f & (CellFlag.Fogged | CellFlag.Door)) != 0) continue;
                    bool nearThreat = false;
                    foreach (var t in threatCells) if (BuildHelpers.Dist2(x, z, t[0], t[1]) <= 4) { nearThreat = true; break; }
                    if (nearThreat) continue;
                    float coverC = hasCentroid ? CoverCalc.CoverFrom(g, x, z, cx, cz) : 0f;
                    float coverAny = coverC;
                    if (coverC <= 0f)
                        foreach (var t in threatCells) coverAny = Math.Max(coverAny, CoverCalc.CoverFrom(g, x, z, t[0], t[1]));
                    int tier; float cover;
                    if (coverC > 0f) { tier = 0; cover = coverC; }
                    else if (coverAny > 0f) { tier = 1; cover = coverAny; }
                    else if ((f & CellFlag.Indoors) != 0) { tier = 2; cover = 0f; }
                    else if (allowOpen) { tier = 3; cover = 0f; }
                    else continue;
                    bool sees = false;
                    foreach (var p in approachPath)
                    {
                        if (BuildHelpers.Dist2(x, z, p[0], p[1]) > range2) continue;
                        if (LineOfFire.Clear(g, x, z, p[0], p[1])) { sees = true; break; }
                    }
                    double dist = Math.Sqrt(BuildHelpers.Dist2(x, z, anchorX, anchorZ));
                    double score = 3 * cover + (sees ? 2 : 0) - 0.08 * dist;
                    cands.Add(Tuple.Create(tier, new Spot { X = x, Z = z, Cover = tier <= 0 ? cover : 0f, SeesApproach = sees, Score = score }));
                }
            var ordered = cands.OrderBy(c => c.Item1).ThenByDescending(c => c.Item2.Score).ThenBy(c => c.Item2.Z).ThenBy(c => c.Item2.X).Select(c => c.Item2).ToList();
            foreach (var s in ordered)
            {
                if (result.Count >= n) break;
                if (result.Any(r => Math.Abs(r.X - s.X) <= 1 && Math.Abs(r.Z - s.Z) <= 1)) continue;
                result.Add(s);
            }
            foreach (var s in ordered)
            {
                if (result.Count >= n) break;
                if (!result.Contains(s)) result.Add(s);
            }
            return result;
        }
    }

    public static class Approach
    {
        static readonly int[] Dx = { 0, 1, 0, -1 };
        static readonly int[] Dz = { 1, 0, -1, 0 };

        /// <summary>BFS 4-neighbour shortest path over Walk cells (doors walkable); null when unreachable or either end is not walkable.</summary>
        public static List<int[]> Path(CombatGrid g, int sx, int sz, int tx, int tz, int maxNodes = 40000)
        {
            if (!g.Walk(sx, sz) || !g.Walk(tx, tz)) return null;
            var prev = new Dictionary<int, int>();
            var q = new Queue<int>();
            int s = g.Idx(sx, sz), t = g.Idx(tx, tz);
            prev[s] = -1; q.Enqueue(s);
            int visited = 0;
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (cur == t) break;
                if (++visited > maxNodes) return null;
                int x = cur % g.W, z = cur / g.W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Dx[d], nz = z + Dz[d];
                    if (!g.Walk(nx, nz)) continue;
                    int ni = g.Idx(nx, nz);
                    if (prev.ContainsKey(ni)) continue;
                    prev[ni] = cur; q.Enqueue(ni);
                }
            }
            if (!prev.ContainsKey(t)) return null;
            var path = new List<int[]>();
            for (int c = t; c != -1; c = prev[c]) path.Add(new[] { c % g.W, c / g.W });
            path.Reverse();
            return path;
        }

        /// <summary>The narrowest passage along the path, at least minFromTarget cells (path index) from the target (last) end; ties go to
        /// the cell nearest the target. Width = walkable run perpendicular to the local path direction, capped at 9. The cell is returned
        /// whatever its width; callers treat width &lt;= 3 as a chokepoint. Null when the path is shorter than minFromTarget.</summary>
        public static int[] Chokepoint(CombatGrid g, List<int[]> path, int minFromTarget, out int width)
        {
            width = 0;
            if (path == null || path.Count == 0) return null;
            int last = path.Count - 1, maxI = last - Math.Max(0, minFromTarget);
            if (maxI < 0) return null;
            int bestI = -1, bestW = int.MaxValue;
            for (int i = 0; i <= maxI; i++)
            {
                var a = path[Math.Max(0, i - 1)]; var b = path[Math.Min(last, i + 1)];
                bool horizontal = Math.Abs(b[0] - a[0]) >= Math.Abs(b[1] - a[1]);
                int px = horizontal ? 0 : 1, pz = horizontal ? 1 : 0;
                int w = 1;
                for (int k = 1; k < 9 && w < 9; k++) { if (g.Walk(path[i][0] + px * k, path[i][1] + pz * k)) w++; else break; }
                for (int k = 1; k < 9 && w < 9; k++) { if (g.Walk(path[i][0] - px * k, path[i][1] - pz * k)) w++; else break; }
                if (w <= bestW) { bestW = w; bestI = i; } // <= : later (nearer the target) wins ties
            }
            width = bestW;
            return path[bestI];
        }

        static int Octant(int cx, int cz, int x, int z)
        {
            // 0 = n, then clockwise: ne, e, se, s, sw, w, nw
            double ang = Math.Atan2(x - cx, z - cz) * 180.0 / Math.PI; // 0 = north, 90 = east
            if (ang < 0) ang += 360;
            return (int)Math.Floor((ang + 22.5) / 45.0) % 8;
        }

        static readonly string[] Words = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        static readonly string[] Keys = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };

        public static string Compass(int cx, int cz, int x, int z) { return (cx == x && cz == z) ? "here" : Words[Octant(cx, cz, x, z)]; }
        public static string CompassKey(int cx, int cz, int x, int z) { return (cx == x && cz == z) ? "here" : Keys[Octant(cx, cz, x, z)]; }
    }

    public sealed class HostileInfo
    {
        public int Id; public string Faction; public string Kind;
        public bool Mech, Animal, Insect, Humanlike, Ranged, Siege, Manhunter, Downed;
        public string Weapon; public int X, Z; public int PrevX = -1, PrevZ = -1;
    }

    public sealed class ThreatGroup
    {
        public int Id; public string Faction, Kind; public int Count; public List<int> Ids = new List<int>();
        public int X, Z; public string Where, Heading; public List<string> Labels = new List<string>(); public int Dist;
    }

    public sealed class DefenseCounts
    {
        public int Turrets, TurretsPowered, TurretsNoAmmo = -1, Traps, Sandbags, Barricades, Doors, ExteriorDoors, WallCells, PerimeterGaps = -1;
        public bool Enclosed; public List<string> Chokepoints = new List<string>(); public bool Killbox;
    }

    public static class CombatWords
    {
        public static string KindOf(HostileInfo h)
        {
            if (h.Insect) return "infestation";
            if (h.Manhunter) return "manhunter";
            if (h.Siege) return "siege";
            if (h.Mech) return "mech";
            return h.Ranged ? "ranged" : "melee";
        }

        static string FactionKey(HostileInfo h) { return string.IsNullOrEmpty(h.Faction) ? "wild" : h.Faction; }

        static readonly string[] Special = { "manhunter", "mech", "siege", "infestation" }; // tie priority

        public static List<ThreatGroup> Group(IList<HostileInfo> hostiles, int homeX, int homeZ)
        {
            var live = (hostiles ?? new List<HostileInfo>()).Where(h => h != null && !h.Downed).ToList();
            int n = live.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (FactionKey(live[i]) == FactionKey(live[j]) && BuildHelpers.Dist2(live[i].X, live[i].Z, live[j].X, live[j].Z) <= 144)
                        parent[find(i)] = find(j);
            var groups = new List<ThreatGroup>();
            foreach (var members in Enumerable.Range(0, n).GroupBy(find))
            {
                var ms = members.Select(i => live[i]).OrderBy(h => h.Id).ToList();
                var tg = new ThreatGroup { Id = ms[0].Id, Faction = FactionKey(ms[0]), Count = ms.Count, Ids = ms.Select(h => h.Id).ToList() };
                // kind
                var kinds = ms.GroupBy(KindOf).ToDictionary(k => k.Key, k => k.Count());
                if (kinds.Count == 1) tg.Kind = kinds.Keys.First();
                else
                {
                    string win = Special.Where(k => kinds.ContainsKey(k) && kinds[k] * 2 >= ms.Count).OrderByDescending(k => kinds[k]).FirstOrDefault();
                    tg.Kind = win ?? "mixed";
                }
                int cx, cz; BuildHelpers.Mean(ms.Select(h => new[] { h.X, h.Z }), out cx, out cz);
                tg.X = cx; tg.Z = cz;
                tg.Dist = (int)Math.Round(Math.Sqrt(BuildHelpers.Dist2(cx, cz, homeX, homeZ)));
                tg.Where = tg.Dist == 0 ? "at home" : Approach.Compass(homeX, homeZ, cx, cz) + ", " + tg.Dist + " cells from home";
                // heading from the members that know where they were
                var known = ms.Where(h => h.PrevX >= 0 && h.PrevZ >= 0).ToList();
                tg.Heading = "holding";
                if (known.Count > 0)
                {
                    double nx = known.Average(h => (double)h.X), nz = known.Average(h => (double)h.Z);
                    double px = known.Average(h => (double)h.PrevX), pz = known.Average(h => (double)h.PrevZ);
                    double dNow = Math.Sqrt((nx - homeX) * (nx - homeX) + (nz - homeZ) * (nz - homeZ));
                    double dPrev = Math.Sqrt((px - homeX) * (px - homeX) + (pz - homeZ) * (pz - homeZ));
                    if (dPrev - dNow >= 3) tg.Heading = "toward home";
                    else if (dNow - dPrev >= 3) tg.Heading = "away";
                }
                tg.Labels = ms.GroupBy(h => string.IsNullOrEmpty(h.Kind) ? "hostile" : h.Kind.ToLowerInvariant())
                    .Select(k => new { k.Key, C = k.Count() }).OrderByDescending(k => k.C).ThenBy(k => k.Key).Take(3)
                    .Select(k => k.C + " " + (k.C > 1 && !k.Key.EndsWith("s") ? k.Key + "s" : k.Key)).ToList();
                groups.Add(tg);
            }
            return groups.OrderBy(t => t.Dist).ThenBy(t => t.Id).ToList();
        }

        static string Plural(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        /// <summary>Defense summary: "3 turrets (2 powered); 6 traps; base walled with 2 doors; chokepoint: north gap, 2 wide".</summary>
        public static string Defenses(DefenseCounts d)
        {
            var parts = new List<string>();
            if (d.Turrets > 0)
            {
                var det = new List<string>();
                if (d.TurretsPowered >= 0) det.Add(d.TurretsPowered + " powered");
                if (d.TurretsNoAmmo > 0) det.Add(d.TurretsNoAmmo + " no ammo");
                parts.Add(Plural(d.Turrets, "turret", "turrets") + (det.Count > 0 ? " (" + string.Join(", ", det) + ")" : ""));
            }
            if (d.Traps > 0) parts.Add(Plural(d.Traps, "trap", "traps"));
            if (d.Sandbags > 0) parts.Add(Plural(d.Sandbags, "sandbag", "sandbags"));
            if (d.Barricades > 0) parts.Add(Plural(d.Barricades, "barricade", "barricades"));
            if (d.Enclosed)
            {
                int doors = d.ExteriorDoors > 0 ? d.ExteriorDoors : d.Doors;
                parts.Add(doors > 0 ? "base walled with " + Plural(doors, "door", "doors") : "base walled");
            }
            else if (d.WallCells > 0)
                parts.Add(d.PerimeterGaps > 0 ? "walls with " + Plural(d.PerimeterGaps, "gap", "gaps") : "partly walled");
            if (d.Chokepoints != null && d.Chokepoints.Count > 0) parts.Add("chokepoint: " + string.Join("; ", d.Chokepoints));
            if (d.Killbox) parts.Add("killbox set");
            if (parts.Count == 0)
                return "no walls, no turrets: open ground";
            return string.Join("; ", parts);
        }

        public static string MoodRisk(float mood, float minor, float major, bool inBreak)
        {
            if (inBreak) return "breaking";
            if (mood < major) return "major";
            if (mood < minor) return "minor";
            return "";
        }

        public static string Health(float health01, bool bleeding)
        {
            string w = health01 >= 0.8f ? "ok" : health01 >= 0.5f ? "hurt" : health01 >= 0.25f ? "badly hurt" : "critical";
            return bleeding ? w + ", bleeding" : w;
        }
    }

    public sealed class SquadMember
    {
        public int Id; public float Health; public bool Downed, Drafted, Bleeding, Fleeing; public int X, Z; public int TargetId; public bool Busy;
    }

    public sealed class SquadState
    {
        public List<int> Members = new List<int>(); public string Stance = "fire_at_will";
        public int FocusGroup; public int FocusPawn;
        public int RetreatX = -1, RetreatZ = -1; public float RetreatBelow = 0.45f; public bool AutoRescue = true;
        public int SinceTick;
        /// <summary>Members the controller already sent back wounded (so a retreat happens once); the game passes them as Fleeing.</summary>
        public List<int> Fell = new List<int>();
        /// <summary>The focused group's member ids when it was chosen, so the focus survives the group's id changing (its
        /// smallest-id member dying).</summary>
        public List<int> FocusIds = new List<int>();
        public bool Active { get { return Members.Count > 0; } }

        public JObject ToJson()
        {
            return new JObject
            {
                ["members"] = new JArray(Members), ["stance"] = Stance, ["focus_group"] = FocusGroup, ["focus_pawn"] = FocusPawn,
                ["retreat_x"] = RetreatX, ["retreat_z"] = RetreatZ, ["retreat_below"] = RetreatBelow, ["auto_rescue"] = AutoRescue,
                ["since_tick"] = SinceTick, ["fell"] = new JArray(Fell), ["focus_ids"] = new JArray(FocusIds)
            };
        }

        public static SquadState FromJson(JToken t)
        {
            var s = new SquadState();
            var o = t as JObject;
            if (o == null) return s;
            var m = o["members"] as JArray;
            if (m != null) foreach (var x in m) if (x.Type == JTokenType.Integer) s.Members.Add((int)x);
            if (o["stance"] != null && o["stance"].Type == JTokenType.String) s.Stance = (string)o["stance"];
            s.FocusGroup = I(o["focus_group"], 0); s.FocusPawn = I(o["focus_pawn"], 0);
            s.RetreatX = I(o["retreat_x"], -1); s.RetreatZ = I(o["retreat_z"], -1);
            if (o["retreat_below"] != null && (o["retreat_below"].Type == JTokenType.Float || o["retreat_below"].Type == JTokenType.Integer)) s.RetreatBelow = (float)o["retreat_below"];
            if (o["auto_rescue"] != null && o["auto_rescue"].Type == JTokenType.Boolean) s.AutoRescue = (bool)o["auto_rescue"];
            s.SinceTick = I(o["since_tick"], 0);
            var fl = o["fell"] as JArray;
            if (fl != null) foreach (var x in fl) if (x.Type == JTokenType.Integer) s.Fell.Add((int)x);
            var fi = o["focus_ids"] as JArray;
            if (fi != null) foreach (var x in fi) if (x.Type == JTokenType.Integer) s.FocusIds.Add((int)x);
            return s;
        }

        static int I(JToken t, int d) { return t != null && t.Type == JTokenType.Integer ? (int)t : d; }
    }

    public sealed class TacticOrder { public int Pawn; public string Kind; public int Target; public int X, Z; public string Note; }

    public static class SquadTactics
    {
        /// <summary>The squad's focused group: by id, else the group holding most of the remembered FocusIds (the id moves when
        /// its smallest-id member dies). Updates s.FocusGroup when it moved; null when the group is gone.</summary>
        public static ThreatGroup FocusedGroup(SquadState s, IList<ThreatGroup> groups)
        {
            if (groups == null || s.FocusGroup == 0) return null;
            var g = groups.FirstOrDefault(x => x.Id == s.FocusGroup);
            if (g == null && s.FocusIds.Count > 0)
                g = groups.Select(x => new { x, n = x.Ids.Count(i => s.FocusIds.Contains(i)) }).Where(t => t.n > 0)
                    .OrderByDescending(t => t.n).ThenBy(t => t.x.Id).Select(t => t.x).FirstOrDefault();
            if (g != null) s.FocusGroup = g.Id;
            return g;
        }

        /// <summary>Orders for one ~250 tick pass, at most one per member, ordered by pawn id.
        /// Retreat (once) beats rescue beats attack. Rescue: one rescuer per downed colonist, the nearest healthy (&gt;= .7), not busy member
        /// within 15 cells, skipped when some member already targets the downed pawn. Attack: FocusPawn if alive, else the nearest live member
        /// of FocusGroup; never an order the member's TargetId already matches, never for Busy members.</summary>
        public static List<TacticOrder> Decide(SquadState s, IList<SquadMember> members, IList<HostileInfo> hostiles, IList<ThreatGroup> groups, IList<int[]> downedColonists)
        {
            var orders = new Dictionary<int, TacticOrder>();
            var live = (members ?? new List<SquadMember>()).Where(m => m != null && !m.Downed && m.Drafted).OrderBy(m => m.Id).ToList();
            hostiles = hostiles ?? new List<HostileInfo>();

            // 1. retreat
            bool retreatSet = s.RetreatX >= 0 && s.RetreatZ >= 0;
            var skip = new HashSet<int>();
            foreach (var m in live)
            {
                if (m.Fleeing) { skip.Add(m.Id); continue; }
                bool low = m.Health < s.RetreatBelow || (m.Bleeding && m.Health < s.RetreatBelow + 0.15f);
                if (low && retreatSet)
                {
                    orders[m.Id] = new TacticOrder { Pawn = m.Id, Kind = "retreat", X = s.RetreatX, Z = s.RetreatZ, Note = "fell back, " + CombatWords.Health(m.Health, m.Bleeding) };
                    skip.Add(m.Id);
                }
            }

            // 2. rescue
            if (s.AutoRescue && downedColonists != null)
            {
                foreach (var d in downedColonists)
                {
                    if (d == null || d.Length < 3) continue;
                    if (live.Any(m => m.TargetId == d[0])) continue;
                    var pick = live.Where(m => !skip.Contains(m.Id) && !m.Busy && m.Health >= 0.7f && BuildHelpers.Dist2(m.X, m.Z, d[1], d[2]) <= 225)
                        .OrderBy(m => BuildHelpers.Dist2(m.X, m.Z, d[1], d[2])).ThenBy(m => m.Id).FirstOrDefault();
                    if (pick == null) continue;
                    orders[pick.Id] = new TacticOrder { Pawn = pick.Id, Kind = "rescue", Target = d[0], X = d[1], Z = d[2], Note = "going to carry a downed colonist" };
                    skip.Add(pick.Id);
                }
            }

            // 3. attack
            var focus = s.FocusPawn != 0 ? hostiles.FirstOrDefault(h => h.Id == s.FocusPawn && !h.Downed) : null;
            var grp = (focus == null && s.FocusGroup != 0 && groups != null) ? FocusedGroup(s, groups) : null;
            foreach (var m in live)
            {
                if (skip.Contains(m.Id) || m.Busy) continue;
                HostileInfo target = focus;
                if (target == null && grp != null)
                    target = hostiles.Where(h => !h.Downed && grp.Ids.Contains(h.Id))
                        .OrderBy(h => BuildHelpers.Dist2(m.X, m.Z, h.X, h.Z)).ThenBy(h => h.Id).FirstOrDefault();
                if (target == null || m.TargetId == target.Id) continue;
                orders[m.Id] = new TacticOrder { Pawn = m.Id, Kind = "attack", Target = target.Id, X = target.X, Z = target.Z, Note = "attacking " + (target.Kind ?? "hostile") };
            }
            return orders.Values.OrderBy(o => o.Pawn).ToList();
        }
    }
}

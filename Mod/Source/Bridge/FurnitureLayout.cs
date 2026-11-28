using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One piece of furniture a room role wants. W x H is the footprint facing north (the game passes the def's real size).
    /// Mode: wall (back against a wall, front kept clear), center (in the open middle), chair (beside the last table, facing it),
    /// mount (in an outer wall cell: a cooler).</summary>
    public sealed class FurnitureItem
    {
        public string Word; public int W = 1, H = 1; public string Mode = "wall";
        public FurnitureItem() { }
        public FurnitureItem(string word, int w, int h, string mode) { Word = word; W = w; H = h; Mode = mode; }
    }

    /// <summary>Where a piece goes: the occupied rect and the rotation (0 north, 1 east, 2 south, 3 west). For a wall piece the
    /// rotation is the wall its back is against (a bed's head, a bench's back; the front, where a pawn works, faces into the
    /// room). For a cooler it is the wall side (hot side out, cold side in). For a chair it faces the table.</summary>
    public sealed class FurniturePlace
    {
        public string Word; public CellBox Rect; public int Rot;
        public override string ToString() { return Word + "@" + Rect + "r" + Rot; }
    }

    /// <summary>A small per-role furniture layout inside a planned (or existing, empty) rectangular room. Pure; the hands
    /// resolve each word to a researched def, pass its size, and place blueprints. Keeps the door's inside cell and every
    /// piece's front clear, and checks that every free cell and every front stays reachable from the door.</summary>
    public static class FurnitureLayout
    {
        static readonly Dictionary<string, int[]> Sizes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bed"] = new[] { 1, 2 }, ["hospital bed"] = new[] { 1, 2 }, ["end table"] = new[] { 1, 1 }, ["dresser"] = new[] { 2, 1 },
            ["stove"] = new[] { 3, 1 }, ["butcher table"] = new[] { 3, 1 }, ["cooler"] = new[] { 1, 1 },
            ["table"] = new[] { 2, 2 }, ["big table"] = new[] { 2, 4 }, ["chair"] = new[] { 1, 1 },
            ["horseshoes"] = new[] { 1, 1 }, ["chess table"] = new[] { 1, 1 }, ["research bench"] = new[] { 3, 2 },
            ["tailoring bench"] = new[] { 3, 1 }, ["smithy"] = new[] { 3, 1 }, ["stonecutter"] = new[] { 3, 1 }, ["shelf"] = new[] { 2, 1 },
        };

        static FurnitureItem I(string word, string mode)
        {
            int[] s; if (!Sizes.TryGetValue(word, out s)) s = new[] { 1, 1 };
            return new FurnitureItem(word, s[0], s[1], mode);
        }

        /// <summary>The pieces a role gets, in placing order (most important first).</summary>
        public static List<FurnitureItem> Items(string role, string size, int beds)
        {
            var l = new List<FurnitureItem>();
            switch (role)
            {
                case "bedroom": l.Add(I("bed", "wall")); l.Add(I("end table", "wall")); l.Add(I("dresser", "wall")); break;
                case "barracks":
                {
                    int n = beds > 0 ? beds : size == "small" ? 4 : size == "large" ? 10 : 6;
                    for (int i = 0; i < n; i++) l.Add(I("bed", "wall"));
                    break;
                }
                case "hospital":
                {
                    int n = beds > 0 ? beds : size == "large" ? 4 : 2;
                    for (int i = 0; i < n; i++) l.Add(I("hospital bed", "wall"));
                    break;
                }
                case "kitchen": l.Add(I("stove", "wall")); l.Add(I("butcher table", "wall")); break;
                case "freezer": l.Add(I("cooler", "mount")); break;
                case "dining":
                    l.Add(I(size == "large" ? "big table" : "table", "center"));
                    for (int i = 0; i < (size == "large" ? 6 : size == "small" ? 2 : 4); i++) l.Add(I("chair", "chair"));
                    break;
                case "rec": l.Add(I("horseshoes", "center")); l.Add(I("chess table", "wall")); break;
                case "research": l.Add(I("research bench", "wall")); break;
                case "workshop": l.Add(I("tailoring bench", "wall")); l.Add(I("smithy", "wall")); l.Add(I("stonecutter", "wall")); break;
                case "storage": l.Add(I("shelf", "wall")); l.Add(I("shelf", "wall")); break;
            }
            return l;
        }

        /// <summary>Lays the items out in the room's interior. door: the door cell (in the wall). outerWalls: wall cells with open
        /// air outside (for a cooler). blocked: interior cells already taken. Items that don't fit are left out.</summary>
        public static List<FurniturePlace> Place(CellBox inside, int[] door, IList<FurnitureItem> items, IList<int[]> outerWalls = null, ICollection<long> blocked = null)
        {
            var used = new HashSet<long>(blocked ?? new HashSet<long>());
            var keep = new HashSet<long>();
            int[] entry = null;   // the cell inside the door
            foreach (var d in BaseGrid.Dirs)
            {
                int x = door[0] + d[0], z = door[1] + d[1];
                if (inside.Contains(x, z)) { entry = new[] { x, z }; keep.Add(BaseGrid.Key(x, z)); }
            }
            if (entry == null) return new List<FurniturePlace>();
            var placed = new List<FurniturePlace>();
            CellBox lastTable = null;
            foreach (var it in items)
            {
                FurniturePlace best = null; List<long> bestFront = null; double bestScore = double.MinValue;
                foreach (var cand in Candidates(inside, it, lastTable, outerWalls, door))
                {
                    var cells = BuildPure.BoxCells(cand.Rect).Select(c => BaseGrid.Key(c[0], c[1])).ToList();
                    if (it.Mode != "mount" && cells.Any(k => used.Contains(k) || keep.Contains(k))) continue;
                    if (it.Mode == "mount" && cells.Any(k => used.Contains(k))) continue;
                    var front = Front(inside, cand, it.Mode);
                    if (front.Any(k => used.Contains(k))) continue;
                    // everything stays reachable from the door with this piece in
                    var trial = new HashSet<long>(used);
                    if (it.Mode != "mount") foreach (var k in cells) trial.Add(k);
                    var allKeep = new HashSet<long>(keep); foreach (var k in front) allKeep.Add(k);
                    if (!Connected(inside, entry, trial, allKeep)) continue;
                    double s = cand.Rot >= 0 ? Score(inside, cand, door, it.Mode) : 0;
                    if (s > bestScore) { bestScore = s; best = cand; bestFront = front; }
                }
                if (best == null) continue;
                if (it.Mode == "mount") used.Add(BaseGrid.Key(best.Rect.X, best.Rect.Z));
                else foreach (var c in BuildPure.BoxCells(best.Rect)) used.Add(BaseGrid.Key(c[0], c[1]));
                foreach (var k in bestFront) keep.Add(k);
                if (it.Mode == "center") lastTable = best.Rect;
                placed.Add(best);
            }
            return placed;
        }

        static IEnumerable<FurniturePlace> Candidates(CellBox inside, FurnitureItem it, CellBox table, IList<int[]> outerWalls, int[] door)
        {
            switch (it.Mode)
            {
                case "mount":
                    if (outerWalls == null) yield break;
                    var outer = new CellBox(inside.X - 1, inside.Z - 1, inside.W + 2, inside.H + 2);
                    foreach (var c in outerWalls)
                    {
                        string side = BasePlanner.SideOfEdge(outer, c[0], c[1]);
                        if (side == null) continue;
                        if (Math.Abs(c[0] - door[0]) + Math.Abs(c[1] - door[1]) <= 1) continue;   // not right beside the door
                        yield return new FurniturePlace { Word = it.Word, Rect = new CellBox(c[0], c[1], 1, 1), Rot = RotOf(side) };
                    }
                    yield break;
                case "center":
                    for (int r = 0; r < 2; r++)
                    {
                        int w = r == 0 ? it.W : it.H, h = r == 0 ? it.H : it.W;
                        if (r == 1 && w == h) break;
                        for (int z = inside.Z + 1; z + h - 1 <= inside.MaxZ - 1; z++)
                            for (int x = inside.X + 1; x + w - 1 <= inside.MaxX - 1; x++)
                                yield return new FurniturePlace { Word = it.Word, Rect = new CellBox(x, z, w, h), Rot = r == 0 ? 0 : 1 };
                    }
                    yield break;
                case "chair":
                    if (table == null) yield break;
                    for (int x = table.X - 1; x <= table.MaxX + 1; x++)
                        for (int z = table.Z - 1; z <= table.MaxZ + 1; z++)
                        {
                            bool sideX = (x == table.X - 1 || x == table.MaxX + 1) && z >= table.Z && z <= table.MaxZ;
                            bool sideZ = (z == table.Z - 1 || z == table.MaxZ + 1) && x >= table.X && x <= table.MaxX;
                            if (!sideX && !sideZ || !inside.Contains(x, z)) continue;
                            int rot = sideZ ? (z < table.Z ? 0 : 2) : (x < table.X ? 1 : 3);   // face the table
                            yield return new FurniturePlace { Word = it.Word, Rect = new CellBox(x, z, 1, 1), Rot = rot };
                        }
                    yield break;
                default:   // wall
                    foreach (var side in new[] { "n", "e", "s", "w" })
                    {
                        int rot = RotOf(side);
                        int fw = rot % 2 == 0 ? it.W : it.H, fh = rot % 2 == 0 ? it.H : it.W;
                        if (fw > inside.W || fh > inside.H) continue;
                        if (side == "n" || side == "s")
                        {
                            int z = side == "n" ? inside.MaxZ - fh + 1 : inside.Z;
                            for (int x = inside.X; x + fw - 1 <= inside.MaxX; x++) yield return new FurniturePlace { Word = it.Word, Rect = new CellBox(x, z, fw, fh), Rot = rot };
                        }
                        else
                        {
                            int x = side == "e" ? inside.MaxX - fw + 1 : inside.X;
                            for (int z = inside.Z; z + fh - 1 <= inside.MaxZ; z++) yield return new FurniturePlace { Word = it.Word, Rect = new CellBox(x, z, fw, fh), Rot = rot };
                        }
                    }
                    yield break;
            }
        }

        public static int RotOf(string side) { return side == "n" ? 0 : side == "e" ? 1 : side == "s" ? 2 : 3; }

        /// <summary>The cells in front of a piece (where a pawn stands to use it, or the foot of a bed): kept clear.</summary>
        static List<long> Front(CellBox inside, FurniturePlace p, string mode)
        {
            var l = new List<long>();
            var r = p.Rect;
            if (mode == "mount")
            {
                // the cold side: the interior cell next to the wall cell
                foreach (var d in BaseGrid.Dirs)
                    if (inside.Contains(r.X + d[0], r.Z + d[1])) l.Add(BaseGrid.Key(r.X + d[0], r.Z + d[1]));
                return l;
            }
            if (mode == "chair") return l;
            if (mode == "center")
            {
                // a walkway on at least the long sides; chairs take some of it later
                return l;
            }
            switch (p.Rot)
            {
                case 0: for (int x = r.X; x <= r.MaxX; x++) Add(inside, l, x, r.Z - 1); break;
                case 2: for (int x = r.X; x <= r.MaxX; x++) Add(inside, l, x, r.MaxZ + 1); break;
                case 1: for (int z = r.Z; z <= r.MaxZ; z++) Add(inside, l, r.X - 1, z); break;
                default: for (int z = r.Z; z <= r.MaxZ; z++) Add(inside, l, r.MaxX + 1, z); break;
            }
            return l;
        }

        static void Add(CellBox inside, List<long> l, int x, int z) { if (inside.Contains(x, z)) l.Add(BaseGrid.Key(x, z)); }

        static double Score(CellBox inside, FurniturePlace p, int[] door, string mode)
        {
            double cx = p.Rect.X + (p.Rect.W - 1) / 2.0, cz = p.Rect.Z + (p.Rect.H - 1) / 2.0;
            double dd = Math.Abs(cx - door[0]) + Math.Abs(cz - door[1]);
            if (mode == "center")
            {
                double mx = inside.X + (inside.W - 1) / 2.0, mz = inside.Z + (inside.H - 1) / 2.0;
                return -(Math.Abs(cx - mx) + Math.Abs(cz - mz));
            }
            if (mode == "chair") return -0.01 * dd;
            // wall pieces: away from the door; snug into a corner a little
            bool corner = p.Rect.X == inside.X || p.Rect.MaxX == inside.MaxX;
            bool cornerZ = p.Rect.Z == inside.Z || p.Rect.MaxZ == inside.MaxZ;
            return dd + (corner && cornerZ ? 0.5 : 0);
        }

        /// <summary>Every free interior cell and every kept cell reachable from the door's inside cell.</summary>
        static bool Connected(CellBox inside, int[] entry, HashSet<long> used, HashSet<long> keep)
        {
            if (used.Contains(BaseGrid.Key(entry[0], entry[1]))) return false;
            var seen = new HashSet<long> { BaseGrid.Key(entry[0], entry[1]) };
            var q = new Queue<int[]>(); q.Enqueue(entry);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var d in BaseGrid.Dirs)
                {
                    int x = c[0] + d[0], z = c[1] + d[1];
                    long k = BaseGrid.Key(x, z);
                    if (!inside.Contains(x, z) || used.Contains(k) || !seen.Add(k)) continue;
                    q.Enqueue(new[] { x, z });
                }
            }
            foreach (var k in keep) if (!seen.Contains(k) && !used.Contains(k)) { /* kept cell walled off */ return false; }
            foreach (var k in keep) if (used.Contains(k)) return false;
            for (int z = inside.Z; z <= inside.MaxZ; z++)
                for (int x = inside.X; x <= inside.MaxX; x++)
                {
                    long k = BaseGrid.Key(x, z);
                    if (!used.Contains(k) && !seen.Contains(k)) return false;
                }
            return true;
        }

        /// <summary>The layout drawn into an overlay for BaseGrid.ToAscii: B bed, T other furniture, C cooler, h chair.</summary>
        public static void Draw(IDictionary<long, char> overlay, IEnumerable<FurniturePlace> places)
        {
            foreach (var p in places)
            {
                char ch = p.Word.Contains("bed") ? 'B' : p.Word == "cooler" ? 'C' : p.Word == "chair" ? 'h' : 'T';
                foreach (var c in BuildPure.BoxCells(p.Rect)) overlay[BaseGrid.Key(c[0], c[1])] = ch;
            }
        }
    }
}

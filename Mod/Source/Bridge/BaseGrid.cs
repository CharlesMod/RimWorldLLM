using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimWorldLLM.Bridge
{
    /// <summary>What a cell of the base grid holds. Several flags can be set at once (a door is also Walk).</summary>
    [Flags]
    public enum BaseFlag : ushort
    {
        None = 0,
        Walk = 1,        // a pawn can walk here (doors included)
        Free = 2,        // empty buildable ground: a wall or room may go here
        Wall = 4,        // a built or planned impassable building (wall, cooler, ...)
        Door = 8,
        Fertile = 16,    // soil good for fields (fertility 100%+): keep for crops
        Water = 32,      // water a wall can't stand on
        Marsh = 64,      // marsh/mud/shallow water: walkable, not buildable
        Mountain = 128,  // under an overhead mountain (thick roof)
        Fog = 256,       // not seen yet
        Zone = 512,      // a stockpile or growing zone
        Thing = 1024,    // furniture, a bench, a bed, a blueprint of a walkable thing: occupied
        Rock = 2048,     // natural rock: impassable, counts as a wall for a room
    }

    /// <summary>A thing the reachability guard keeps reachable: a door, a bed, a bench, a stockpile, a colonist, a room.</summary>
    public sealed class BasePoi
    {
        public string Kind;      // entrance, door, bed, bench, stockpile, colonist, room
        public string Label;     // "the south entrance", "Bram's bed", "the stockpile"
        public List<int[]> Cells = new List<int[]>();
        public BasePoi() { }
        public BasePoi(string kind, string label, int x, int z) { Kind = kind; Label = label; Cells.Add(new[] { x, z }); }
    }

    /// <summary>A room of the base: role word, its interior cells' bounding box, id, owner.</summary>
    public sealed class BaseRoom
    {
        public int Id;
        public string Role;      // bedroom, barracks, kitchen, dining, storage, freezer, hospital, workshop, rec, research, room
        public string Label;     // "Bram's bedroom", "the kitchen"
        public string Owner;
        public int MinX, MinZ, MaxX, MaxZ;   // interior bounding box
        public int Cells;
        public double CX { get { return (MinX + MaxX) / 2.0; } }
        public double CZ { get { return (MinZ + MaxZ) / 2.0; } }
        public string Name { get { return string.IsNullOrEmpty(Label) ? "the " + (Role ?? "room") : Label; } }
    }

    /// <summary>A snapshot of the base's map window: per-cell flags, room ids, the things to keep reachable and the rooms.
    /// Built from the live map each time (no hidden state); Verse-free so it is tested on hand-made grids and drawn as ASCII.
    /// Coordinates are map coordinates: the grid covers OX..OX+W-1, OZ..OZ+H-1; x grows east, z grows north.</summary>
    public sealed class BaseGrid
    {
        public readonly int OX, OZ, W, H;
        public readonly BaseFlag[] Flags;
        public readonly int[] RoomIds;   // 0 = outdoors
        public readonly List<BasePoi> Pois = new List<BasePoi>();
        public readonly List<BaseRoom> Rooms = new List<BaseRoom>();
        public int HomeX, HomeZ;
        /// <summary>True when the window's border is the real map edge on that side; a window border inside the map still
        /// counts as "outside" (a way off the map), which is what the guard needs.</summary>
        public bool EdgeIsMap = true;

        public BaseGrid(int ox, int oz, int w, int h)
        {
            OX = ox; OZ = oz; W = w; H = h;
            Flags = new BaseFlag[w * h];
            RoomIds = new int[w * h];
            HomeX = ox + w / 2; HomeZ = oz + h / 2;
        }

        public bool In(int x, int z) { return x >= OX && z >= OZ && x < OX + W && z < OZ + H; }
        public int Idx(int x, int z) { return (z - OZ) * W + (x - OX); }
        public BaseFlag F(int x, int z) { return In(x, z) ? Flags[Idx(x, z)] : BaseFlag.None; }
        public bool Has(int x, int z, BaseFlag f) { return (F(x, z) & f) != 0; }
        public void Set(int x, int z, BaseFlag f) { if (In(x, z)) Flags[Idx(x, z)] = f; }
        public void Add(int x, int z, BaseFlag f) { if (In(x, z)) Flags[Idx(x, z)] |= f; }
        public int RoomAt(int x, int z) { return In(x, z) ? RoomIds[Idx(x, z)] : 0; }
        public bool Solid(int x, int z) { return Has(x, z, BaseFlag.Wall | BaseFlag.Rock); }
        public BaseRoom Room(int id) { return Rooms.FirstOrDefault(r => r.Id == id); }

        public BaseGrid Clone()
        {
            var g = new BaseGrid(OX, OZ, W, H) { HomeX = HomeX, HomeZ = HomeZ, EdgeIsMap = EdgeIsMap };
            Array.Copy(Flags, g.Flags, Flags.Length);
            Array.Copy(RoomIds, g.RoomIds, RoomIds.Length);
            foreach (var p in Pois) g.Pois.Add(new BasePoi { Kind = p.Kind, Label = p.Label, Cells = p.Cells.Select(c => new[] { c[0], c[1] }).ToList() });
            foreach (var r in Rooms) g.Rooms.Add(new BaseRoom { Id = r.Id, Role = r.Role, Label = r.Label, Owner = r.Owner, MinX = r.MinX, MinZ = r.MinZ, MaxX = r.MaxX, MaxZ = r.MaxZ, Cells = r.Cells });
            return g;
        }

        // ------------------------------------------------------------ ASCII (tests, the live check, a picture for the vision worker)
        /// <summary>Legend of the ASCII form, one char per cell kind.</summary>
        public const string Legend = ". ground  , fertile soil  # wall  R rock  + door  ~ water  m marsh  B bed  T bench/furniture  S stockpile  @ colonist  ? fog  ^ ground under mountain roof";

        /// <summary>A grid from ASCII rows, north row first. Origin (ox, oz) is the south-west cell.
        /// Beds, benches, stockpiles, colonists and doors become things to keep reachable; doors on the outside of
        /// the base become entrances; enclosed regions become rooms (role "room").</summary>
        public static BaseGrid FromAscii(int ox, int oz, params string[] rowsNorthFirst)
        {
            int h = rowsNorthFirst.Length, w = rowsNorthFirst.Max(r => r.Length);
            var g = new BaseGrid(ox, oz, w, h);
            var stock = new BasePoi { Kind = "stockpile", Label = "the stockpile" };
            int beds = 0, benches = 0, cols = 0;
            for (int row = 0; row < h; row++)
            {
                string s = rowsNorthFirst[row];
                int z = oz + h - 1 - row;
                for (int i = 0; i < w; i++)
                {
                    char ch = i < s.Length ? s[i] : '.';
                    int x = ox + i;
                    BaseFlag f;
                    switch (ch)
                    {
                        case '#': f = BaseFlag.Wall; break;
                        case 'R': f = BaseFlag.Rock; break;
                        case '+': f = BaseFlag.Door | BaseFlag.Walk; break;
                        case '~': f = BaseFlag.Water; break;
                        case 'm': f = BaseFlag.Marsh | BaseFlag.Walk; break;
                        case ',': f = BaseFlag.Walk | BaseFlag.Free | BaseFlag.Fertile; break;
                        case '^': f = BaseFlag.Walk | BaseFlag.Free | BaseFlag.Mountain; break;
                        case '?': f = BaseFlag.Fog; break;
                        case 'B': f = BaseFlag.Walk | BaseFlag.Thing; g.Pois.Add(new BasePoi("bed", "bed " + (++beds), x, z)); break;
                        case 'T': f = BaseFlag.Walk | BaseFlag.Thing; g.Pois.Add(new BasePoi("bench", "bench " + (++benches), x, z)); break;
                        case 'S': f = BaseFlag.Walk | BaseFlag.Zone; stock.Cells.Add(new[] { x, z }); break;
                        case '@': f = BaseFlag.Walk | BaseFlag.Free; g.Pois.Add(new BasePoi("colonist", "colonist " + (++cols), x, z)); break;
                        default: f = BaseFlag.Walk | BaseFlag.Free; break;
                    }
                    g.Flags[g.Idx(x, z)] = f;
                }
            }
            if (stock.Cells.Count > 0) g.Pois.Add(stock);
            g.DetectRooms();
            g.AddDoorPois();
            int hx, hz;
            var pts = g.Pois.Where(p => p.Kind == "bed" || p.Kind == "colonist").Select(p => p.Cells[0]).ToList();
            if (pts.Count == 0) pts = g.Rooms.Select(r => new[] { (int)r.CX, (int)r.CZ }).ToList();
            if (BuildHelpers.Mean(pts, out hx, out hz)) { g.HomeX = hx; g.HomeZ = hz; }
            return g;
        }

        /// <summary>Marks enclosed walkable regions (not touching the window border, walls and doors as borders) as rooms.
        /// Keeps the roles of rooms already known by any cell they still hold; new ones get role "room".</summary>
        public void DetectRooms(int maxCells = 400)
        {
            var old = new Dictionary<int, BaseRoom>();
            foreach (var r in Rooms) old[r.Id] = r;
            var oldIds = (int[])RoomIds.Clone();
            Array.Clear(RoomIds, 0, RoomIds.Length);
            Rooms.Clear();
            var seen = new bool[W * H];
            int next = old.Count == 0 ? 1 : old.Keys.Max() + 1;
            for (int i = 0; i < W * H; i++)
            {
                if (seen[i] || !Inner(Flags[i])) continue;
                var cells = new List<int>();
                bool border = false;
                var q = new Queue<int>(); q.Enqueue(i); seen[i] = true;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); cells.Add(c);
                    int cx = c % W, cz = c / W;
                    if (cx == 0 || cz == 0 || cx == W - 1 || cz == H - 1) border = true;
                    foreach (var d in Dirs)
                    {
                        int nx = cx + d[0], nz = cz + d[1];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int n = nz * W + nx;
                        if (seen[n] || !Inner(Flags[n])) continue;
                        seen[n] = true; q.Enqueue(n);
                    }
                }
                if (border || cells.Count > maxCells) continue;
                int keep = cells.Select(c => oldIds[c]).Where(id => id != 0).GroupBy(id => id).OrderByDescending(gr => gr.Count()).Select(gr => gr.Key).FirstOrDefault();
                BaseRoom was;
                var room = new BaseRoom { Id = keep != 0 ? keep : next++, Role = "room", Cells = cells.Count, MinX = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxZ = int.MinValue };
                if (keep != 0 && old.TryGetValue(keep, out was)) { room.Role = was.Role; room.Label = was.Label; room.Owner = was.Owner; old.Remove(keep); }
                foreach (int c in cells)
                {
                    RoomIds[c] = room.Id;
                    int x = OX + c % W, z = OZ + c / W;
                    room.MinX = Math.Min(room.MinX, x); room.MaxX = Math.Max(room.MaxX, x);
                    room.MinZ = Math.Min(room.MinZ, z); room.MaxZ = Math.Max(room.MaxZ, z);
                }
                Rooms.Add(room);
            }
            Pois.RemoveAll(p => p.Kind == "room");
            foreach (var r in Rooms)
            {
                var p = new BasePoi { Kind = "room", Label = r.Name };
                for (int z = r.MinZ; z <= r.MaxZ; z++)
                    for (int x = r.MinX; x <= r.MaxX; x++)
                        if (RoomAt(x, z) == r.Id) p.Cells.Add(new[] { x, z });
                Pois.Add(p);
            }
        }

        static bool Inner(BaseFlag f) { return (f & BaseFlag.Walk) != 0 && (f & BaseFlag.Door) == 0; }

        /// <summary>Adds every door as a thing to keep reachable: "entrance" when one side is outdoors, else "door".</summary>
        public void AddDoorPois()
        {
            Pois.RemoveAll(p => p.Kind == "door" || p.Kind == "entrance");
            for (int z = OZ; z < OZ + H; z++)
                for (int x = OX; x < OX + W; x++)
                {
                    if (!Has(x, z, BaseFlag.Door)) continue;
                    string side = OutdoorSide(x, z);
                    if (side != null) Pois.Add(new BasePoi("entrance", "the " + SideWord(side) + " entrance at (" + x + "," + z + ")", x, z));
                    else Pois.Add(new BasePoi("door", "the door at (" + x + "," + z + ")", x, z));
                }
        }

        /// <summary>The side (n/e/s/w) of a door whose neighbour is walkable outdoors (not in a room), or null.</summary>
        public string OutdoorSide(int x, int z)
        {
            foreach (var d in Dirs)
            {
                int nx = x + d[0], nz = z + d[1];
                if (In(nx, nz) && Has(nx, nz, BaseFlag.Walk) && !Has(nx, nz, BaseFlag.Door) && RoomAt(nx, nz) == 0) return SideOf(d[0], d[1]);
            }
            return null;
        }

        public static readonly int[][] Dirs = { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, 0 } };   // n e s w
        public static string SideOf(int dx, int dz) { return dz > 0 ? "n" : dx > 0 ? "e" : dz < 0 ? "s" : "w"; }
        public static int[] Step(string side)
        {
            switch (side) { case "n": return Dirs[0]; case "e": return Dirs[1]; case "s": return Dirs[2]; default: return Dirs[3]; }
        }
        public static string SideWord(string s)
        {
            switch (s) { case "n": return "north"; case "e": return "east"; case "s": return "south"; case "w": return "west"; default: return s; }
        }
        public static string Opposite(string s)
        {
            switch (s) { case "n": return "s"; case "e": return "w"; case "s": return "n"; default: return "e"; }
        }

        /// <summary>The grid as ASCII rows, north first, with optional overlay chars at cells (planned walls, a door, furniture).</summary>
        public string ToAscii(IDictionary<long, char> overlay = null)
        {
            var sb = new StringBuilder();
            var thingAt = new Dictionary<long, char>();
            foreach (var p in Pois)
            {
                char c = p.Kind == "bed" ? 'B' : p.Kind == "bench" ? 'T' : p.Kind == "colonist" ? '@' : p.Kind == "stockpile" ? 'S' : '\0';
                if (c == '\0') continue;
                foreach (var cell in p.Cells) thingAt[Key(cell[0], cell[1])] = c;
            }
            for (int z = OZ + H - 1; z >= OZ; z--)
            {
                for (int x = OX; x < OX + W; x++)
                {
                    char o;
                    long k = Key(x, z);
                    if (overlay != null && overlay.TryGetValue(k, out o)) { sb.Append(o); continue; }
                    if (thingAt.TryGetValue(k, out o)) { sb.Append(o); continue; }
                    var f = F(x, z);
                    if ((f & BaseFlag.Fog) != 0) sb.Append('?');
                    else if ((f & BaseFlag.Door) != 0) sb.Append('+');
                    else if ((f & BaseFlag.Wall) != 0) sb.Append('#');
                    else if ((f & BaseFlag.Rock) != 0) sb.Append('R');
                    else if ((f & BaseFlag.Water) != 0) sb.Append('~');
                    else if ((f & BaseFlag.Marsh) != 0) sb.Append('m');
                    else if ((f & BaseFlag.Thing) != 0) sb.Append('T');
                    else if ((f & BaseFlag.Zone) != 0) sb.Append('S');
                    else if ((f & BaseFlag.Fertile) != 0) sb.Append(',');
                    else if ((f & BaseFlag.Mountain) != 0) sb.Append('^');
                    else sb.Append('.');
                }
                if (z > OZ) sb.Append('\n');
            }
            return sb.ToString();
        }

        public static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }
    }

    /// <summary>The never-seal-off check: before a set of blueprints is placed, simulate it on the grid and refuse when a door,
    /// room, bed, bench, stockpile, colonist or the entrance that was reachable (from the map edge, or from the base's main
    /// area) would no longer be. Verse-free; the hands build the grid and pass the cells the blueprints make solid.</summary>
    public static class ReachGuard
    {
        /// <summary>Null when the plan keeps everything reachable, else a plain reason ("would block the entrance ...").
        /// solid: cells that become impassable (walls, coolers, any impassable building). doors: cells that become doors.
        /// mustReach: cells (new doors) that must be reachable from outside after the plan.</summary>
        public static string Check(BaseGrid g, IEnumerable<int[]> solid, IEnumerable<int[]> doors = null, IEnumerable<int[]> mustReach = null)
        {
            var solidSet = new HashSet<long>();
            foreach (var c in solid ?? Enumerable.Empty<int[]>()) solidSet.Add(BaseGrid.Key(c[0], c[1]));
            var doorSet = new HashSet<long>();
            foreach (var c in doors ?? Enumerable.Empty<int[]>()) doorSet.Add(BaseGrid.Key(c[0], c[1]));
            if (solidSet.Count == 0) return null;

            // a thing to keep reachable that the plan builds right over
            foreach (var p in g.Pois)
            {
                if (p.Kind == "room" || p.Kind == "stockpile") continue;
                foreach (var c in p.Cells)
                    if (solidSet.Contains(BaseGrid.Key(c[0], c[1])))
                        return p.Kind == "entrance" ? "would block the entrance: a wall would go over " + p.Label : "would build over " + p.Label;
            }

            Func<int, int, bool> walkBefore = (x, z) => g.Has(x, z, BaseFlag.Walk);
            Func<int, int, bool> walkAfter = (x, z) =>
            {
                long k = BaseGrid.Key(x, z);
                if (solidSet.Contains(k)) return false;
                if (doorSet.Contains(k)) return true;
                return g.Has(x, z, BaseFlag.Walk);
            };
            int[] main = MainCell(g, walkAfter);
            var edge0 = Flood(g, walkBefore, EdgeSources(g, walkBefore));
            var edge1 = Flood(g, walkAfter, EdgeSources(g, walkAfter));
            // the base's main area counts only when it has no way out to begin with (a sealed base); otherwise reach from
            // outside covers it, and a plan that walls in home itself is caught as cutting home off from outside
            if (main != null && edge0.Contains(g.Idx(main[0], main[1]))) main = null;
            var main0 = main == null ? null : Flood(g, walkBefore, new List<int[]> { main });
            var main1 = main == null ? null : Flood(g, walkAfter, new List<int[]> { main });

            var cut = new List<BasePoi>();
            bool fromEdge = false;
            foreach (var p in g.Pois)
            {
                bool e0 = Reached(g, edge0, p), e1 = Reached(g, edge1, p);
                bool m0 = main0 != null && Reached(g, main0, p), m1 = main1 != null && Reached(g, main1, p);
                if (e0 && !e1) { cut.Add(p); fromEdge = true; }
                else if (m0 && !m1) cut.Add(p);
            }
            if (cut.Count > 0)
            {
                var ent = cut.FirstOrDefault(p => p.Kind == "entrance");
                if (ent != null) return "would block the entrance (" + ent.Label + " would be cut off from outside)";
                var names = cut.OrderBy(p => Rank(p.Kind)).Select(p => p.Label).Distinct().ToList();
                string list = names.Count <= 3 ? Join(names) : string.Join(", ", names.Take(3)) + " and " + (names.Count - 3) + " more";
                return "would cut off " + list + " from " + (fromEdge ? "the way out of the map" : "the rest of the base");
            }
            if (mustReach != null)
                foreach (var c in mustReach)
                    if (!edge1.Contains(g.Idx(c[0], c[1])) && (main1 == null || !main1.Contains(g.Idx(c[0], c[1]))))
                        return "the new door at (" + c[0] + "," + c[1] + ") would open onto a dead end";
            return null;
        }

        static int Rank(string kind)
        {
            switch (kind) { case "colonist": return 0; case "bed": return 1; case "room": return 2; case "door": return 3; case "bench": return 4; default: return 5; }
        }

        static string Join(IList<string> l)
        {
            if (l.Count == 1) return l[0];
            return string.Join(", ", l.Take(l.Count - 1)) + " and " + l[l.Count - 1];
        }

        static bool Reached(BaseGrid g, HashSet<int> set, BasePoi p)
        {
            foreach (var c in p.Cells)
            {
                if (!g.In(c[0], c[1])) continue;
                if (set.Contains(g.Idx(c[0], c[1]))) return true;
                foreach (var d in BaseGrid.Dirs)
                {
                    int x = c[0] + d[0], z = c[1] + d[1];
                    if (g.In(x, z) && set.Contains(g.Idx(x, z))) return true;
                }
            }
            return false;
        }

        static List<int[]> EdgeSources(BaseGrid g, Func<int, int, bool> walk)
        {
            var l = new List<int[]>();
            for (int x = g.OX; x < g.OX + g.W; x++)
            {
                if (walk(x, g.OZ)) l.Add(new[] { x, g.OZ });
                if (walk(x, g.OZ + g.H - 1)) l.Add(new[] { x, g.OZ + g.H - 1 });
            }
            for (int z = g.OZ + 1; z < g.OZ + g.H - 1; z++)
            {
                if (walk(g.OX, z)) l.Add(new[] { g.OX, z });
                if (walk(g.OX + g.W - 1, z)) l.Add(new[] { g.OX + g.W - 1, z });
            }
            return l;
        }

        /// <summary>The base's main area: the walkable cell nearest home that stays walkable after the plan.</summary>
        static int[] MainCell(BaseGrid g, Func<int, int, bool> walkAfter)
        {
            for (int r = 0; r < 12; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                        int x = g.HomeX + dx, z = g.HomeZ + dz;
                        if (g.In(x, z) && g.Has(x, z, BaseFlag.Walk) && walkAfter(x, z)) return new[] { x, z };
                    }
            return null;
        }

        /// <summary>Cells (as grid indexes) reachable from the sources, four-way (RimWorld pawns never squeeze diagonally between walls).</summary>
        public static HashSet<int> Flood(BaseGrid g, Func<int, int, bool> walk, IEnumerable<int[]> sources)
        {
            var seen = new HashSet<int>();
            var q = new Queue<int[]>();
            foreach (var s in sources)
                if (g.In(s[0], s[1]) && walk(s[0], s[1]) && seen.Add(g.Idx(s[0], s[1]))) q.Enqueue(s);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var d in BaseGrid.Dirs)
                {
                    int x = c[0] + d[0], z = c[1] + d[1];
                    if (!g.In(x, z) || !walk(x, z)) continue;
                    if (seen.Add(g.Idx(x, z))) q.Enqueue(new[] { x, z });
                }
            }
            return seen;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>What the narrator asks for: a room role, a size word, and optionally which room or person it goes near
    /// and who it is for. The game picks where.</summary>
    public sealed class RoomRequest
    {
        public string Role;              // bedroom, barracks, kitchen, dining, storage, freezer, hospital, workshop, rec, research
        public string Size = "medium";   // small, medium, large
        public string Near;              // a room role, a room label ("Bram's bedroom") or a person's name; optional
        public string For;               // who it is for ("Bram"); optional, words only
        public string Door;              // n/e/s/w for scripts; null or "auto" = the planner picks
        public int Beds;                 // barracks/hospital bed count; 0 = from the size
        public int InW, InH;             // explicit interior size (scripts); 0 = from the table
    }

    /// <summary>A planned room: the outer box (walls included), the door, which wall cells need a blueprint, and the words.</summary>
    public sealed class RoomPlan
    {
        public string Role;
        public CellBox Box;                       // outer box, walls included
        public int[] Door;                        // door cell
        public string DoorSide;                   // n/e/s/w: the wall the door is in
        public List<int[]> Walls = new List<int[]>();   // wall cells that need a wall blueprint (not reused, not the door)
        public int Reused;                        // wall cells already standing (shared with another room or rock)
        public List<int> SharedWith = new List<int>();  // ids of rooms whose wall this room shares
        public string AttachWords;                // "sharing the barracks' east wall"
        public string DoorWords;                  // "door onto the north corridor"
        public List<string> Notes = new List<string>();
        public double Score;
        public string Words;                      // the whole sentence for the narrator
        public CellBox Inside { get { return new CellBox(Box.X + 1, Box.Z + 1, Box.W - 2, Box.H - 2); } }
    }

    /// <summary>The base planner: the narrator says WHAT room it needs; this decides WHERE and HOW. New rooms attach to the
    /// existing structure sharing walls, doors open onto a kept-clear walkway, sizes follow vanilla's expectations, related
    /// rooms sit together (kitchen, freezer, dining; bedrooms together; research away from noise; hospital near the
    /// entrance), terrain is respected (no water or marsh, fertile soil left for fields), and every pick passes the
    /// never-seal-off guard. Verse-free; the hands pass a BaseGrid read from the live map each time.</summary>
    public static class BasePlanner
    {
        public static readonly string[] Roles = { "bedroom", "barracks", "kitchen", "dining", "storage", "freezer", "hospital", "workshop", "rec", "research" };

        /// <summary>Interior size (inside the walls) for a role and size word, from vanilla's expectations:
        /// bedroom 4x5 (small 3x4), barracks two rows of beds (about 3 cells a bed), kitchen 5x5, freezer 5x5,
        /// dining 6x6, rec 6x7, hospital 6x5, research 5x5, workshop 6x6, storage 7x7. Large adds 2, small takes 1.</summary>
        public static void Inside(string role, string size, int beds, out int w, out int h)
        {
            int d = size == "small" ? -1 : size == "large" ? 2 : 0;
            switch (role)
            {
                case "bedroom": w = 4; h = 5; break;
                case "barracks":
                {
                    int n = beds > 0 ? beds : size == "small" ? 4 : size == "large" ? 10 : 6;
                    w = 5; h = Math.Max(3, (n + 1) / 2 + 1);   // bed (2) + aisle (1) + bed (2) across; a bed a cell along, plus the door end
                    d = 0;
                    break;
                }
                case "hospital":
                {
                    if (beds > 0) { w = 6; h = Math.Max(4, (beds + 1) / 2 * 2 + 1); d = 0; }
                    else { w = 6; h = 5; }
                    break;
                }
                case "kitchen": w = 5; h = 5; break;
                case "freezer": w = 5; h = 5; break;
                case "dining": w = 6; h = 6; break;
                case "rec": w = 6; h = 7; break;
                case "research": w = 5; h = 5; break;
                case "workshop": w = 6; h = 6; break;
                case "storage": w = 7; h = 7; break;
                default: w = 5; h = 5; break;
            }
            w = Clamp(w + d, 1, BuildPure.MaxRoom - 2);
            h = Clamp(h + d, 1, BuildPure.MaxRoom - 2);
            if (role == "bedroom") { w = Math.Max(w, 3); h = Math.Max(h, 4); }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        // Rooms that like to share a wall or sit close: bonus when attached, half when within 8 cells.
        static double Relation(string role, string other)
        {
            switch (role)
            {
                case "kitchen": return other == "freezer" ? 6 : other == "dining" ? 5 : other == "storage" ? 1 : 0;
                case "freezer": return other == "kitchen" ? 6 : other == "dining" ? 1 : 0;
                case "dining": return other == "kitchen" ? 5 : other == "rec" ? 3 : other == "freezer" ? 1 : 0;
                case "rec": return other == "dining" ? 3 : 0;
                case "bedroom": return other == "bedroom" || other == "barracks" ? 3 : other == "kitchen" || other == "workshop" ? -1 : 0;
                case "barracks": return other == "bedroom" ? 3 : other == "kitchen" || other == "workshop" ? -1 : 0;
                case "research": return Noisy(other) ? -3 : 0;
                case "workshop": return other == "storage" ? 4 : 0;
                case "storage": return other == "workshop" ? 4 : other == "kitchen" ? 1 : 0;
                case "hospital": return other == "bedroom" || other == "barracks" ? 1 : 0;
                default: return 0;
            }
        }

        static bool Noisy(string role) { return role == "workshop" || role == "kitchen" || role == "rec" || role == "dining"; }

        /// <summary>Cells in front of every door (both sides): kept clear so a door never opens onto a wall.</summary>
        public static HashSet<long> KeepClear(BaseGrid g)
        {
            var s = new HashSet<long>();
            for (int z = g.OZ; z < g.OZ + g.H; z++)
                for (int x = g.OX; x < g.OX + g.W; x++)
                {
                    if (!g.Has(x, z, BaseFlag.Door)) continue;
                    foreach (var d in BaseGrid.Dirs) s.Add(BaseGrid.Key(x + d[0], z + d[1]));
                }
            return s;
        }

        /// <summary>Plans a room for the request on the grid, or null with why.</summary>
        public static RoomPlan Plan(BaseGrid g, RoomRequest r, out string why, int maxGuard = 40)
        {
            why = null;
            string role = r.Role ?? "room";
            int iw, ih;
            if (r.InW > 0 && r.InH > 0) { iw = r.InW; ih = r.InH; }
            else Inside(role, r.Size ?? "medium", r.Beds, out iw, out ih);
            int w = iw + 2, h = ih + 2;
            var clear = KeepClear(g);
            var near = FindNear(g, r.Near);
            var entrances = g.Pois.Where(p => p.Kind == "entrance").Select(p => p.Cells[0]).ToList();

            // where to look: around the built part of the base (rooms and walls), else around home
            int bx0 = int.MaxValue, bz0 = int.MaxValue, bx1 = int.MinValue, bz1 = int.MinValue;
            foreach (var rm in g.Rooms) { bx0 = Math.Min(bx0, rm.MinX - 1); bz0 = Math.Min(bz0, rm.MinZ - 1); bx1 = Math.Max(bx1, rm.MaxX + 1); bz1 = Math.Max(bz1, rm.MaxZ + 1); }
            for (int z = g.OZ; z < g.OZ + g.H; z++)
                for (int x = g.OX; x < g.OX + g.W; x++)
                    if (g.Has(x, z, BaseFlag.Wall | BaseFlag.Door)) { bx0 = Math.Min(bx0, x); bz0 = Math.Min(bz0, z); bx1 = Math.Max(bx1, x); bz1 = Math.Max(bz1, z); }
            bool built = bx0 != int.MaxValue;
            int sx0, sz0, sx1, sz1;
            if (built) { sx0 = bx0 - Math.Max(w, h) - 2; sz0 = bz0 - Math.Max(w, h) - 2; sx1 = bx1 + 2; sz1 = bz1 + 2; }
            else { sx0 = g.HomeX - 14 - w; sz0 = g.HomeZ - 14 - h; sx1 = g.HomeX + 14; sz1 = g.HomeZ + 14; }
            long baseArea = built ? (long)(bx1 - bx0 + 1) * (bz1 - bz0 + 1) : 0;

            var cands = new List<RoomPlan>();
            string firstProblem = null;
            var dims = iw == ih ? new[] { new[] { w, h } } : new[] { new[] { w, h }, new[] { h, w } };
            foreach (var dm in dims)
                for (int z = sz0; z <= sz1; z++)
                    for (int x = sx0; x <= sx1; x++)
                    {
                        var box = new CellBox(x, z, dm[0], dm[1]);
                        string problem;
                        var p = Try(g, box, role, r, clear, near, entrances, built, bx0, bz0, bx1, bz1, baseArea, out problem);
                        if (p != null) cands.Add(p);
                        else if (firstProblem == null) firstProblem = problem;
                    }
            if (cands.Count == 0)
            {
                why = "no open ground for a " + w + "x" + h + " " + role + " near the base" + (firstProblem == null ? "" : " (" + firstProblem + ")");
                return null;
            }
            string guardWhy = null;
            var edge0 = ReachGuard.EdgeBefore(g);
            foreach (var p in cands.OrderByDescending(c => c.Score).Take(maxGuard))
            {
                var solid = new List<int[]>(p.Walls);
                string gw = ReachGuard.Check(g, solid, new[] { p.Door }, new[] { p.Door }, edge0);
                if (gw == null) { Words(g, p, r); return p; }
                if (guardWhy == null) guardWhy = gw;
            }
            why = "every spot near the base " + (guardWhy ?? "would cut something off");
            return null;
        }

        static BaseRoom FindNear(BaseGrid g, string near)
        {
            if (string.IsNullOrWhiteSpace(near)) return null;
            string n = near.Trim().ToLowerInvariant();
            if (n.StartsWith("the ")) n = n.Substring(4);
            foreach (var rm in g.Rooms) if ((rm.Label ?? "").ToLowerInvariant().Replace("the ", "") == n) return rm;
            foreach (var rm in g.Rooms) if (rm.Role == n) return rm;
            foreach (var rm in g.Rooms) if (!string.IsNullOrEmpty(rm.Owner) && rm.Owner.ToLowerInvariant() == n) return rm;
            foreach (var rm in g.Rooms) if ((rm.Label ?? "").ToLowerInvariant().Contains(n)) return rm;
            return null;
        }

        static RoomPlan Try(BaseGrid g, CellBox box, string role, RoomRequest r, HashSet<long> clear, BaseRoom near, List<int[]> entrances,
                            bool built, int bx0, int bz0, int bx1, int bz1, long baseArea, out string problem)
        {
            problem = null;
            if (box.X < g.OX || box.Z < g.OZ || box.MaxX >= g.OX + g.W || box.MaxZ >= g.OZ + g.H) { problem = "the edge of the known map"; return null; }
            int fertile = 0, mountain = 0;
            for (int z = box.Z + 1; z < box.MaxZ; z++)
                for (int x = box.X + 1; x < box.MaxX; x++)
                {
                    var f = g.F(x, z);
                    if ((f & BaseFlag.Free) == 0 || (f & (BaseFlag.Zone | BaseFlag.Thing | BaseFlag.Water | BaseFlag.Marsh | BaseFlag.Fog)) != 0 || g.RoomAt(x, z) != 0)
                    { problem = Problem(f, "inside"); return null; }
                    if (clear.Contains(BaseGrid.Key(x, z))) { problem = "a doorway must stay clear"; return null; }
                    if ((f & BaseFlag.Fertile) != 0) fertile++;
                    if ((f & BaseFlag.Mountain) != 0) mountain++;
                }
            var plan = new RoomPlan { Role = role, Box = box };
            var walls = BuildPure.RoomWalls(box, "s", out int[] _sdoor);
            walls.Add(_sdoor);
            var freeEdge = new List<int[]>();
            double shared = 0;
            var attached = new Dictionary<int, string>();   // room id -> its side we share
            foreach (var c in walls)
            {
                var f = g.F(c[0], c[1]);
                if ((f & (BaseFlag.Wall | BaseFlag.Rock)) != 0 && (f & BaseFlag.Door) == 0)
                {
                    plan.Reused++;
                    shared += (f & BaseFlag.Rock) != 0 ? 0.5 : 1.0;
                    // the room on the far side of this wall cell
                    foreach (var d in BaseGrid.Dirs)
                    {
                        int ox = c[0] + d[0], oz = c[1] + d[1];
                        if (box.Contains(ox, oz)) continue;
                        int id = g.RoomAt(ox, oz);
                        if (id != 0 && !attached.ContainsKey(id)) attached[id] = BaseGrid.SideOf(-d[0], -d[1]);
                    }
                    continue;
                }
                if ((f & BaseFlag.Free) == 0 || (f & (BaseFlag.Zone | BaseFlag.Thing | BaseFlag.Water | BaseFlag.Marsh | BaseFlag.Fog | BaseFlag.Door)) != 0)
                { problem = Problem(f, "wall line"); return null; }
                if (clear.Contains(BaseGrid.Key(c[0], c[1]))) { problem = "a doorway must stay clear"; return null; }
                if ((f & BaseFlag.Fertile) != 0) fertile++;
                freeEdge.Add(c);
            }

            // the door: a free wall cell (no corner) whose outside is walkable open ground
            double cx = box.X + (box.W - 1) / 2.0, cz = box.Z + (box.H - 1) / 2.0;
            int[] bestDoor = null; string bestSide = null; double bestDoorScore = double.MinValue; bool bestCorridor = false;
            foreach (var c in freeEdge)
            {
                string side = SideOfEdge(box, c[0], c[1]);
                if (side == null) continue;   // corner
                if (!string.IsNullOrEmpty(r.Door) && r.Door != "auto" && r.Door != side) continue;
                var st = BaseGrid.Step(side);
                int ox = c[0] + st[0], oz = c[1] + st[1];
                if (!g.In(ox, oz) || !g.Has(ox, oz, BaseFlag.Walk) || g.Has(ox, oz, BaseFlag.Door) || g.RoomAt(ox, oz) != 0) continue;
                // the cells beside the door's outside must not be the new room's own walls (always true) — and the door cell's
                // inside neighbour is the room
                double s = 0;
                bool corridor = OnCorridor(g, ox, oz, side);
                if (corridor) s += 3;
                double hx = g.HomeX - cx, hz = g.HomeZ - cz, len = Math.Sqrt(hx * hx + hz * hz);
                if (len > 0.1) s += 1.5 * (st[0] * hx + st[1] * hz) / len;   // faces home
                if (g.Has(ox, oz, BaseFlag.Fertile)) s -= 0.5;
                // a centred door reads better
                s -= 0.1 * (side == "n" || side == "s" ? Math.Abs(c[0] - cx) : Math.Abs(c[1] - cz));
                if (s > bestDoorScore) { bestDoorScore = s; bestDoor = c; bestSide = side; bestCorridor = corridor; }
            }
            if (bestDoor == null) { problem = "no wall with open ground outside for a door"; return null; }
            plan.Door = bestDoor; plan.DoorSide = bestSide;
            foreach (var c in freeEdge) if (!(c[0] == bestDoor[0] && c[1] == bestDoor[1])) plan.Walls.Add(c);

            if (role == "freezer")
            {
                // the cooler needs a plain outer wall: a new wall cell (no corner, not the door) with open air outside
                bool outer = plan.Walls.Any(c => OuterWallCell(g, box, c));
                if (!outer) { problem = "a freezer needs an outside wall for its cooler"; return null; }
            }

            double score = 0;
            score += Math.Min(shared, 2 * Math.Max(box.W, box.H));
            score += bestDoorScore;
            plan.Notes.Clear();
            foreach (var kv in attached)
            {
                plan.SharedWith.Add(kv.Key);
                var rm = g.Room(kv.Key);
                if (rm != null) score += Relation(role, rm.Role);
                if (near != null && kv.Key == near.Id) score += 8;
            }
            foreach (var rm in g.Rooms)
            {
                if (attached.ContainsKey(rm.Id)) continue;
                double dd = Math.Sqrt((rm.CX - cx) * (rm.CX - cx) + (rm.CZ - cz) * (rm.CZ - cz));
                if (dd <= 9) { double rel = Relation(role, rm.Role); score += rel > 0 ? rel / 2 : rel / 3; }
                if (near != null && rm.Id == near.Id) score += Math.Max(0, 4 - dd / 4);
            }
            if (built && attached.Count == 0 && plan.Reused == 0) score -= 3;   // a lone box away from the base
            score -= 0.6 * fertile;
            double hd = Math.Sqrt((g.HomeX - cx) * (g.HomeX - cx) + (g.HomeZ - cz) * (g.HomeZ - cz));
            score -= 0.15 * hd;
            if (built)
            {
                long ux0 = Math.Min(bx0, box.X), uz0 = Math.Min(bz0, box.Z), ux1 = Math.Max(bx1, box.MaxX), uz1 = Math.Max(bz1, box.MaxZ);
                long grow = (ux1 - ux0 + 1) * (uz1 - uz0 + 1) - baseArea - box.Area;
                score -= 0.03 * Math.Max(0, grow);
            }
            if (role == "hospital" && entrances.Count > 0)
            {
                double ed = entrances.Min(e => Math.Sqrt((e[0] - cx) * (e[0] - cx) + (e[1] - cz) * (e[1] - cz)));
                score -= 0.2 * ed;
            }
            if (mountain > 0) plan.Notes.Add("under overhead mountain");
            if (fertile > 0) plan.Notes.Add("covers " + fertile + " cells of good soil");
            plan.Score = score;
            plan.DoorWords = bestCorridor ? "door onto the " + BaseGrid.SideWord(bestSide) + " corridor" : "door on the " + BaseGrid.SideWord(bestSide) + " side";
            if (attached.Count > 0)
            {
                var first = attached.OrderByDescending(kv => near != null && kv.Key == near.Id ? 1 : 0).ThenByDescending(kv => { var rr = g.Room(kv.Key); return rr == null ? 0 : Relation(role, rr.Role); }).First();
                var rm = g.Room(first.Key);
                string nm = rm == null ? "the room" : rm.Name;
                plan.AttachWords = "sharing " + Possessive(nm) + " " + BaseGrid.SideWord(first.Value) + " wall" + (attached.Count > 1 ? " (and " + (attached.Count - 1) + " more)" : "");
            }
            else if (plan.Reused > 0) plan.AttachWords = "against the existing wall";
            else plan.AttachWords = built ? "on open ground " + Direction(g, cx, cz) + " of the base" : "by home";
            return plan;
        }

        static string Problem(BaseFlag f, string where)
        {
            if ((f & BaseFlag.Fog) != 0) return where + " is not seen yet";
            if ((f & BaseFlag.Water) != 0) return where + " is water";
            if ((f & BaseFlag.Marsh) != 0) return where + " is marsh";
            if ((f & BaseFlag.Zone) != 0) return where + " is in a zone";
            if ((f & BaseFlag.Door) != 0) return where + " has a door";
            if ((f & (BaseFlag.Wall | BaseFlag.Rock)) != 0) return where + " is solid";
            if ((f & BaseFlag.Thing) != 0) return where + " has something built";
            return where + " is not buildable ground";
        }

        /// <summary>n/e/s/w for a cell on the box's edge, null for a corner.</summary>
        public static string SideOfEdge(CellBox b, int x, int z)
        {
            bool cornerX = x == b.X || x == b.MaxX, cornerZ = z == b.Z || z == b.MaxZ;
            if (cornerX && cornerZ) return null;
            if (z == b.MaxZ) return "n";
            if (z == b.Z) return "s";
            if (x == b.MaxX) return "e";
            if (x == b.X) return "w";
            return null;
        }

        /// <summary>A wall cell of the box whose outside is open (walkable, outdoors): where a cooler can blow its heat.</summary>
        public static bool OuterWallCell(BaseGrid g, CellBox box, int[] c)
        {
            string side = SideOfEdge(box, c[0], c[1]);
            if (side == null) return false;
            var st = BaseGrid.Step(side);
            int ox = c[0] + st[0], oz = c[1] + st[1];
            return g.In(ox, oz) && g.Has(ox, oz, BaseFlag.Walk) && !g.Has(ox, oz, BaseFlag.Door) && g.RoomAt(ox, oz) == 0;
        }

        /// <summary>The outside cell of a door lies on a corridor when another door's outside cell (facing the same way) shares its
        /// row (doors in a north or south wall) or column (east or west), with walkable outdoor ground between.</summary>
        static bool OnCorridor(BaseGrid g, int ox, int oz, string side)
        {
            bool row = side == "n" || side == "s";
            for (int dir = -1; dir <= 1; dir += 2)
                for (int k = 1; k <= 14; k++)
                {
                    int x = row ? ox + dir * k : ox, z = row ? oz : oz + dir * k;
                    if (!g.In(x, z) || !g.Has(x, z, BaseFlag.Walk) || g.RoomAt(x, z) != 0) break;
                    if (g.Has(x, z, BaseFlag.Door)) break;
                    // a door beside this walkway cell (in the wall it runs along, on either side)
                    foreach (var d in BaseGrid.Dirs)
                    {
                        if (row ? d[1] == 0 : d[0] == 0) continue;
                        if (g.Has(x + d[0], z + d[1], BaseFlag.Door)) return true;
                    }
                }
            return false;
        }

        static string Possessive(string name)
        {
            if (name.EndsWith("'s bedroom") || name.EndsWith("s")) return name + "'";
            return name + "'s";
        }

        static string Direction(BaseGrid g, double x, double z)
        {
            double dx = x - g.HomeX, dz = z - g.HomeZ;
            if (Math.Abs(dx) < 1 && Math.Abs(dz) < 1) return "in the middle";
            return Approach.Compass(g.HomeX, g.HomeZ, (int)Math.Round(x), (int)Math.Round(z));
        }

        static void Words(BaseGrid g, RoomPlan p, RoomRequest r)
        {
            string forWho = string.IsNullOrWhiteSpace(r.For) ? "" : " for " + r.For.Trim();
            var inside = p.Inside;
            string s = "planned a " + p.Role + forWho + " (" + inside.W + "x" + inside.H + " inside) " + p.AttachWords + ", " + p.DoorWords;
            if (!string.IsNullOrWhiteSpace(r.Near) && FindNear(g, r.Near) == null) p.Notes.Add("no " + r.Near.Trim() + " found to put it near");
            if (p.Notes.Count > 0) s += "; " + string.Join("; ", p.Notes);
            p.Words = s;
        }

        /// <summary>Stamps a plan onto a grid (walls and door built, the room detected with its role): for the next spots,
        /// the digest's suggestions and the Verse-free simulation.</summary>
        public static void Apply(BaseGrid g, RoomPlan p, string label = null, string owner = null)
        {
            foreach (var c in p.Walls) g.Set(c[0], c[1], BaseFlag.Wall);
            g.Set(p.Door[0], p.Door[1], BaseFlag.Door | BaseFlag.Walk);
            for (int z = p.Box.Z + 1; z < p.Box.MaxZ; z++)
                for (int x = p.Box.X + 1; x < p.Box.MaxX; x++)
                    g.Flags[g.Idx(x, z)] = (g.F(x, z) & (BaseFlag.Fertile | BaseFlag.Mountain)) | BaseFlag.Walk | BaseFlag.Free;
            g.DetectRooms();
            int id = g.RoomAt(p.Box.X + 1, p.Box.Z + 1);
            var rm = g.Room(id);
            if (rm != null)
            {
                rm.Role = p.Role; rm.Owner = owner;
                rm.Label = label ?? (owner != null && p.Role == "bedroom" ? owner + "'s bedroom" : "the " + p.Role);
                foreach (var poi in g.Pois) if (poi.Kind == "room" && poi.Cells.Count > 0 && g.RoomAt(poi.Cells[0][0], poi.Cells[0][1]) == id) poi.Label = rm.Label;
            }
            g.AddDoorPois();
        }

        // ------------------------------------------------------------ the digest's BASE line
        /// <summary>The base in words for the digest: outline, rooms, entrance, corridor, room to grow per side and the
        /// planner's suggested next spots. About 200 characters.</summary>
        public static string Describe(BaseGrid g, int suggest = 3)
        {
            var rooms = g.Rooms.Where(r => r.Cells >= 4).ToList();
            if (rooms.Count == 0) return "no rooms yet; home at (" + g.HomeX + "," + g.HomeZ + ")" + Grow(g, g.HomeX - 2, g.HomeZ - 2, g.HomeX + 2, g.HomeZ + 2) + Next(g, suggest);
            int x0 = rooms.Min(r => r.MinX) - 1, z0 = rooms.Min(r => r.MinZ) - 1, x1 = rooms.Max(r => r.MaxX) + 1, z1 = rooms.Max(r => r.MaxZ) + 1;
            int groups = Clusters(rooms);
            string outline = groups == 1
                ? (rooms.Count == 1 ? "one room " : "compact block ") + (x1 - x0 + 1) + "x" + (z1 - z0 + 1)
                : groups + " separate buildings over " + (x1 - x0 + 1) + "x" + (z1 - z0 + 1);
            string s = outline + ", " + rooms.Count + (rooms.Count == 1 ? " room" : " rooms");
            var ent = g.Pois.Where(p => p.Kind == "entrance").Select(p => g.OutdoorSide(p.Cells[0][0], p.Cells[0][1])).Where(x => x != null)
                        .GroupBy(x => x).OrderByDescending(gr => gr.Count()).Select(gr => BaseGrid.SideWord(gr.Key)).ToList();
            s += ent.Count == 0 ? ", no outside door" : ent.Count == 1 ? ", entrance " + ent[0] : ", entrances " + string.Join(" and ", ent.Take(2));
            string cor = Corridor(g);
            if (cor != null) s += ", corridor " + cor;
            return s + Grow(g, x0, z0, x1, z1) + Next(g, suggest);
        }

        static int Clusters(List<BaseRoom> rooms)
        {
            var parent = Enumerable.Range(0, rooms.Count).ToArray();
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            for (int i = 0; i < rooms.Count; i++)
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    var a = rooms[i]; var b = rooms[j];
                    // outer boxes (interior + 1) touching or overlapping = shared wall or a short hop
                    if (a.MinX - 1 <= b.MaxX + 2 && b.MinX - 1 <= a.MaxX + 2 && a.MinZ - 1 <= b.MaxZ + 2 && b.MinZ - 1 <= a.MaxZ + 2)
                        parent[find(i)] = find(j);
                }
            return Enumerable.Range(0, rooms.Count).Select(find).Distinct().Count();
        }

        /// <summary>"east-west" / "north-south" when two or more doors open onto one straight outdoor walkway.</summary>
        static string Corridor(BaseGrid g)
        {
            var fronts = new List<int[]>();   // x, z, 0 = row (n/s doors), 1 = column
            foreach (var p in g.Pois.Where(p => p.Kind == "door" || p.Kind == "entrance"))
            {
                int x = p.Cells[0][0], z = p.Cells[0][1];
                foreach (var d in BaseGrid.Dirs)
                {
                    int ox = x + d[0], oz = z + d[1];
                    if (g.Has(ox, oz, BaseFlag.Walk) && !g.Has(ox, oz, BaseFlag.Door) && g.RoomAt(ox, oz) == 0) fronts.Add(new[] { ox, oz, d[0] == 0 ? 0 : 1 });
                }
            }
            int rowHits = 0, colHits = 0;
            foreach (var f in fronts)
                foreach (var o in fronts)
                {
                    if (f == o || f[2] != o[2]) continue;
                    if (f[2] == 0 && f[1] == o[1] && f[0] < o[0] && o[0] - f[0] <= 14 && Clear(g, f[0], f[1], o[0], o[1])) rowHits++;
                    if (f[2] == 1 && f[0] == o[0] && f[1] < o[1] && o[1] - f[1] <= 14 && Clear(g, f[0], f[1], o[0], o[1])) colHits++;
                }
            if (rowHits == 0 && colHits == 0) return null;
            return rowHits >= colHits ? "east-west" : "north-south";
        }

        static bool Clear(BaseGrid g, int ax, int az, int bx, int bz)
        {
            for (int x = ax; x <= bx; x++)
                for (int z = az; z <= bz; z++)
                    if (!g.Has(x, z, BaseFlag.Walk) || g.RoomAt(x, z) != 0) return false;
            return true;
        }

        /// <summary>"; room to grow: east 14, south 9, north 3, west 0 (water)" — open buildable ground out from each side of the box.</summary>
        static string Grow(BaseGrid g, int x0, int z0, int x1, int z1)
        {
            var parts = new List<KeyValuePair<int, string>>();
            foreach (var side in new[] { "n", "e", "s", "w" })
            {
                int n = 0; string stop = null;
                for (int k = 1; k <= 25; k++)
                {
                    int ok = 0, all = 0, water = 0, rock = 0, fog = 0;
                    bool row = side == "n" || side == "s";
                    int a0 = row ? x0 : z0, a1 = row ? x1 : z1;
                    for (int a = a0; a <= a1; a++)
                    {
                        int x = row ? a : (side == "e" ? x1 + k : x0 - k), z = row ? (side == "n" ? z1 + k : z0 - k) : a;
                        all++;
                        var f = g.F(x, z);
                        if (!g.In(x, z)) { fog++; continue; }
                        else if ((f & BaseFlag.Free) != 0 && (f & (BaseFlag.Zone | BaseFlag.Thing)) == 0) ok++;
                        else if ((f & (BaseFlag.Water | BaseFlag.Marsh)) != 0) water++;
                        else if ((f & BaseFlag.Rock) != 0) rock++;
                        else if ((f & BaseFlag.Fog) != 0) fog++;
                    }
                    if (ok * 2 < all)
                    {
                        int bad = all - ok;
                        stop = fog * 2 >= bad ? "map edge or unseen" : water * 2 >= bad ? "water" : rock * 2 >= bad ? "rock" : "built up";
                        break;
                    }
                    n = k;
                }
                string word = BaseGrid.SideWord(side) + " " + (n >= 25 ? "25+" : n.ToString()) + (n < 25 && stop != null ? " (" + stop + ")" : "");
                parts.Add(new KeyValuePair<int, string>(n, word));
            }
            return "; room to grow: " + string.Join(", ", parts.OrderByDescending(p => p.Key).Select(p => p.Value));
        }

        /// <summary>"; next: room for 3 more bedrooms east of the barracks" from planning bedrooms on a copy of the grid.</summary>
        static string Next(BaseGrid g, int suggest)
        {
            if (suggest <= 0) return "";
            var copy = g.Clone();
            RoomPlan first = null; int n = 0;
            for (int i = 0; i < suggest; i++)
            {
                string why;
                var p = Plan(copy, new RoomRequest { Role = "bedroom" }, out why, 6);
                if (p == null) break;
                if (first == null) first = p;
                n++;
                Apply(copy, p);
            }
            if (first == null) return "; next: no open spot for another bedroom";
            string where;
            if (first.SharedWith.Count > 0)
            {
                var rm = g.Room(first.SharedWith[0]);
                double cx = first.Box.X + (first.Box.W - 1) / 2.0, cz = first.Box.Z + (first.Box.H - 1) / 2.0;
                where = rm == null ? "against the base" : Approach.Compass((int)rm.CX, (int)rm.CZ, (int)cx, (int)cz) + " of " + rm.Name;
            }
            else where = first.AttachWords;
            return "; next: room for " + n + (n >= suggest ? "+" : "") + " more " + (n == 1 ? "bedroom" : "bedrooms") + " " + where;
        }
    }
}

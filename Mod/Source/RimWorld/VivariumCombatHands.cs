using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>Phase 3 game bodies: the combat view, the squad's orders, defense planning and the fight's speed rule
    /// (docs/VIVARIUM.md "Phase 3"). Game thread only. The math (grid, cover, positions, layouts, words) is the Verse-free
    /// CombatPure/CombatLayout; this file reads the map into it and turns its answers into vanilla jobs and blueprints.</summary>
    public sealed partial class VivariumHands
    {
        // ---------------------------------------------------------------- reading the map
        internal static bool IsHostile(Pawn p)
        {
            return p != null && p.Spawned && !p.Dead && p.HostileTo(Faction.OfPlayer) && !p.Position.Fogged(p.Map);
        }

        internal static bool Dormant(Pawn p)
        {
            var c = p.GetComp<CompCanBeDormant>();
            return c != null && !c.Awake;
        }

        static bool RangedPawn(Pawn p)
        {
            try
            {
                var v = p.equipment != null && p.equipment.PrimaryEq != null ? p.equipment.PrimaryEq.PrimaryVerb : null;
                if (v != null) return !v.IsMeleeAttack;
                var cv = p.TryGetAttackVerb(null);
                return cv != null && !cv.IsMeleeAttack;
            }
            catch (Exception) { return false; }
        }

        internal static HostileInfo Info(Pawn p, Dictionary<int, int[]> prev)
        {
            var h = new HostileInfo
            {
                Id = p.thingIDNumber, Faction = p.Faction != null ? p.Faction.Name : null, Kind = p.KindLabel,
                Mech = p.RaceProps.IsMechanoid, Animal = p.RaceProps.Animal, Insect = p.RaceProps.Insect, Humanlike = p.RaceProps.Humanlike,
                Ranged = RangedPawn(p), Downed = p.Downed, X = p.Position.x, Z = p.Position.z,
                Weapon = p.equipment != null && p.equipment.Primary != null ? p.equipment.Primary.LabelNoCount : null
            };
            h.Manhunter = p.MentalStateDef == MentalStateDefOf.Manhunter || p.MentalStateDef == MentalStateDefOf.ManhunterPermanent;
            var lord = p.GetLord();
            h.Siege = lord != null && lord.LordJob is LordJob_Siege;
            int[] pv;
            if (prev != null && prev.TryGetValue(h.Id, out pv)) { h.PrevX = pv[0]; h.PrevZ = pv[1]; }
            return h;
        }

        /// <summary>Hostile pawns the colony can see on the map, as HostileInfo (downed included, flagged).</summary>
        internal static List<HostileInfo> Hostiles(Map map, out HashSet<int> dormant)
        {
            dormant = new HashSet<int>();
            var l = new List<HostileInfo>();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (!IsHostile(p)) continue;
                if (Dormant(p)) dormant.Add(p.thingIDNumber);
                l.Add(Info(p, VivariumCombatTicker.Prev));
            }
            return l;
        }

        /// <summary>Colonists of the player who can fight: not downed, capable of violence, 13 or older.</summary>
        internal static bool Fighter(Pawn p)
        {
            return p.Spawned && !p.Dead && !p.Downed && p.drafter != null && !p.WorkTagIsDisabled(WorkTags.Violent)
                && (p.ageTracker == null || p.ageTracker.AgeBiologicalYears >= 13)
                && !(p.health != null && HealthAIUtility.ShouldSeekMedicalRest(p));
        }

        static string NonFighterWhy(Pawn p)
        {
            if (p.Downed) return "downed";
            if (p.ageTracker != null && p.ageTracker.AgeBiologicalYears < 13) return "child";
            if (p.WorkTagIsDisabled(WorkTags.Violent)) return "no violence";
            if (p.health != null && HealthAIUtility.ShouldSeekMedicalRest(p)) return "sick";
            return null;
        }

        /// <summary>The whole map as a CombatGrid: walkability, sight, cover (fill percent, at most .75), roof, indoors, fog, doors,
        /// buildable ground.</summary>
        internal static CombatGrid Grid(Map map)
        {
            var g = new CombatGrid(map.Size.x, map.Size.z);
            foreach (var c in map.AllCells)
            {
                CellFlag f = CellFlag.None;
                if (c.Walkable(map)) f |= CellFlag.Walkable;
                if (!c.CanBeSeenOver(map)) f |= CellFlag.BlocksSight;
                if (c.Roofed(map)) f |= CellFlag.Roofed;
                var room = c.GetRoom(map);
                if (room != null && !room.PsychologicallyOutdoors) f |= CellFlag.Indoors;
                if (c.Fogged(map)) f |= CellFlag.Fogged;
                if (c.GetDoor(map) != null) f |= CellFlag.Door;
                if (c.GetEdifice(map) == null && c.Standable(map) && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)) f |= CellFlag.Buildable;
                float cover = 0f;
                var t = c.GetCover(map);
                if (t != null) cover = Math.Min(0.75f, t.def.fillPercent);
                g.Set(c.x, c.z, f, cover);
            }
            return g;
        }

        static IntVec3 V(int[] c) { return new IntVec3(c[0], 0, c[1]); }

        static string WhereFrom(IntVec3 home, int x, int z)
        {
            int d = (int)Math.Round(Math.Sqrt(BuildHelpers.Dist2(x, z, home.x, home.z)));
            return d <= 2 ? "at home" : Approach.Compass(home.x, home.z, x, z) + ", " + d + " cells from home";
        }

        /// <summary>Cover a pawn standing at c has against a shooter at s: the same rule CoverCalc uses, read from the map.</summary>
        static float CoverAgainst(Map map, IntVec3 c, IntVec3 s)
        {
            var g = new CombatGrid(3, 3);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var n = new IntVec3(c.x + dx, 0, c.z + dz);
                    float cov = 0f;
                    if (n.InBounds(map)) { var t = n.GetCover(map); if (t != null) cov = Math.Min(0.75f, t.def.fillPercent); }
                    g.Set(dx + 1, dz + 1, CellFlag.Walkable, cov);
                }
            return CoverCalc.CoverFrom(g, 1, 1, 1 + (s.x - c.x), 1 + (s.z - c.z));
        }

        static string CoverWord(float c)
        {
            if (c >= 0.7f) return "behind a wall";
            if (c >= 0.45f) return "behind sandbags";
            if (c > 0f) return "light cover";
            return "in the open";
        }

        /// <summary>Doors between an indoor room and the outdoors.</summary>
        static List<Building_Door> ExteriorDoors(Map map)
        {
            var l = new List<Building_Door>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var d = b as Building_Door;
                if (d == null) continue;
                bool outdoor = false, indoor = false;
                foreach (var dir in GenAdj.CardinalDirections)
                {
                    var n = d.Position + dir;
                    if (!n.InBounds(map) || !n.Walkable(map)) continue;
                    var r = n.GetRoom(map);
                    if (r == null) continue;
                    if (r.PsychologicallyOutdoors) outdoor = true; else indoor = true;
                }
                if (outdoor && indoor) l.Add(d);
            }
            return l;
        }

        /// <summary>The cardinal side of a door that is outdoors ("n","e","s","w"), or null.</summary>
        static string OutwardSide(Map map, IntVec3 door)
        {
            string[] keys = { "n", "e", "s", "w" };
            IntVec3[] dirs = { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };
            for (int i = 0; i < 4; i++)
            {
                var n = door + dirs[i];
                if (!n.InBounds(map)) continue;
                var r = n.GetRoom(map);
                if (r != null && r.PsychologicallyOutdoors && n.Walkable(map)) return keys[i];
            }
            return null;
        }

        /// <summary>The base's outer wall on a side: walking out from home, the last player-owned impassable cell whose next
        /// outward cell is outdoors (or off the map), within 45 cells; Invalid when there is none. dist = cells from home.</summary>
        static IntVec3 OuterWall(Map map, IntVec3 home, string side, out int dist)
        {
            var step = Step(side);
            var c = home; IntVec3 found = IntVec3.Invalid; dist = 0;
            for (int k = 1; k <= 45; k++)
            {
                c += step;
                if (!c.InBounds(map)) break;
                var e = c.GetEdifice(map);
                if (e == null || e.def.passability != Traversability.Impassable || e.Faction != Faction.OfPlayer) continue;
                var next = c + step;
                var r = next.InBounds(map) ? next.GetRoom(map) : null;
                if (!next.InBounds(map) || (r != null && r.PsychologicallyOutdoors)) { found = c; dist = k; break; }
            }
            return found;
        }

        /// <summary>A cell inset cells back from the wall toward home, never at or behind home.</summary>
        static IntVec3 Inset(IntVec3 wall, string side, int dist, int inset)
        {
            int back = Math.Max(1, Math.Min(inset, dist - 1));
            return wall - Step(side) * back;
        }

        static IntVec3 Step(string side)
        {
            switch (side) { case "n": return IntVec3.North; case "e": return IntVec3.East; case "s": return IntVec3.South; default: return IntVec3.West; }
        }

        /// <summary>Is the base closed: can a walker reach the map edge from home without passing a door?</summary>
        static bool Enclosed(CombatGrid g, IntVec3 home)
        {
            if (!g.Walk(home.x, home.z)) return false;
            var seen = new bool[g.W * g.H];
            var q = new Queue<int>();
            q.Enqueue(g.Idx(home.x, home.z)); seen[g.Idx(home.x, home.z)] = true;
            int[] dx = { 0, 1, 0, -1 }, dz = { 1, 0, -1, 0 };
            while (q.Count > 0)
            {
                int cur = q.Dequeue(), x = cur % g.W, z = cur / g.W;
                if (x == 0 || z == 0 || x == g.W - 1 || z == g.H - 1) return false;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], nz = z + dz[k];
                    if (!g.In(nx, nz)) continue;
                    int ni = g.Idx(nx, nz);
                    if (seen[ni]) continue;
                    var f = g.F(nx, nz);
                    if ((f & CellFlag.Walkable) == 0 || (f & CellFlag.Door) != 0) continue;
                    seen[ni] = true; q.Enqueue(ni);
                }
            }
            return true;
        }

        /// <summary>Everything a combat decision needs, read once.</summary>
        sealed class Fight
        {
            public Map Map; public IntVec3 Home; public CombatGrid Grid;
            public List<HostileInfo> All; public HashSet<int> Dormant; public List<ThreatGroup> Groups;
            public ThreatGroup Nearest;           // nearest awake group, or null
            public List<int[]> Path;              // nearest group's centroid → home (walkable path or a straight line)
            public bool PathReal;                 // Path is a walking path (false: the straight-line fallback; no chokepoint on it)
        }

        static Fight ReadFight(Map map, bool withGrid)
        {
            var f = new Fight { Map = map, Home = HomeCenter(map) };
            f.All = Hostiles(map, out f.Dormant);
            f.Groups = CombatWords.Group(f.All, f.Home.x, f.Home.z);
            var dorm = f.Dormant;
            f.Nearest = f.Groups.FirstOrDefault(g => !g.Ids.All(dorm.Contains));
            if (withGrid) f.Grid = Grid(map);
            if (f.Nearest != null && f.Grid != null)
            {
                var start = NearestWalkable(f.Grid, f.Nearest.X, f.Nearest.Z);
                var home = NearestWalkable(f.Grid, f.Home.x, f.Home.z);
                f.Path = start != null && home != null ? Approach.Path(f.Grid, start[0], start[1], home[0], home[1], f.Grid.W * f.Grid.H) : null;
                f.PathReal = f.Path != null;
                if (f.Path == null) f.Path = LineOfFire.Line(f.Nearest.X, f.Nearest.Z, f.Home.x, f.Home.z).ToList();
            }
            return f;
        }

        static int[] NearestWalkable(CombatGrid g, int x, int z)
        {
            for (int r = 0; r <= 6; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                        if (g.Walk(x + dx, z + dz) && (g.F(x + dx, z + dz) & CellFlag.Door) == 0) return new[] { x + dx, z + dz };
                    }
            return null;
        }

        static List<int[]> GroupCells(Fight f, ThreatGroup g)
        {
            if (g == null) return new List<int[]>();
            return f.All.Where(h => g.Ids.Contains(h.Id)).Select(h => new[] { h.X, h.Z }).ToList();
        }

        // ---------------------------------------------------------------- GET /combat
        public JObject Combat(CombatState state)
        {
            try { return CombatDo(state); }
            catch (Exception e)
            {
                Failed(e);
                return new JObject { ["active"] = state != null && state.Active, ["error"] = "the combat view failed: " + e.Message };
            }
        }

        internal static IntVec3 HomeCenterOf(Map map) { return HomeCenter(map); }

        JObject CombatDo(CombatState state)
        {
            var map = HomeMap();
            if (map == null) return new JObject { ["active"] = false, ["error"] = "no colony map" };
            var f = ReadFight(map, true);
            var home = f.Home;
            var o = new JObject { ["active"] = state != null && state.Active, ["home"] = new JObject { ["x"] = home.x, ["z"] = home.z } };

            var threats = new JArray();
            foreach (var g in f.Groups.Take(8))
            {
                var t = new JObject
                {
                    ["id"] = g.Id, ["faction"] = g.Faction, ["kind"] = g.Kind, ["count"] = g.Count, ["where"] = g.Where, ["dist"] = g.Dist,
                    ["heading"] = g.Heading, ["labels"] = new JArray(g.Labels), ["ids"] = new JArray(g.Ids.Take(12))
                };
                if (g.Ids.All(f.Dormant.Contains)) t["dormant"] = true;
                threats.Add(t);
            }
            o["threats"] = threats;

            var nearestHostile = f.All.Where(h => !h.Downed && !f.Dormant.Contains(h.Id)).ToList();
            var fighters = new JArray(); var others = new JArray(); var downedCols = new JArray();
            foreach (var p in map.mapPawns.FreeColonistsSpawned.OrderByDescending(p => p.Drafted).ThenBy(p => p.thingIDNumber))
            {
                if (p.Downed)
                {
                    if (p.InBed()) continue;   // already in a bed: no rescue needed
                    downedCols.Add(new JObject
                    {
                        ["id"] = p.thingIDNumber, ["name"] = Name(p), ["bleeding"] = p.health.hediffSet.BleedRateTotal > 0.01f,
                        ["where"] = WhereFrom(home, p.Position.x, p.Position.z)
                    });
                    continue;
                }
                if (!Fighter(p))
                {
                    var room = p.Position.GetRoom(map);
                    others.Add(new JObject
                    {
                        ["id"] = p.thingIDNumber, ["name"] = Name(p), ["why"] = NonFighterWhy(p) ?? "cannot fight",
                        ["safe"] = p.Position.Roofed(map) && room != null && !room.PsychologicallyOutdoors
                    });
                    continue;
                }
                var e = new JObject { ["id"] = p.thingIDNumber, ["name"] = Name(p), ["drafted"] = p.Drafted };
                bool bleeding = p.health.hediffSet.BleedRateTotal > 0.01f;
                e["health"] = CombatWords.Health(p.health.summaryHealth.SummaryHealthPercent, bleeding);
                var eq = p.equipment != null ? p.equipment.Primary : null;
                e["weapon"] = eq != null ? eq.LabelNoCount : "fists";
                var verb = p.equipment != null && p.equipment.PrimaryEq != null ? p.equipment.PrimaryEq.PrimaryVerb : null;
                e["range"] = verb != null && !verb.IsMeleeAttack ? (int)verb.verbProps.range : 1;
                if (nearestHostile.Count > 0)
                {
                    var nh = nearestHostile.OrderBy(h => BuildHelpers.Dist2(h.X, h.Z, p.Position.x, p.Position.z)).First();
                    e["cover"] = CoverWord(CoverAgainst(map, p.Position, new IntVec3(nh.X, 0, nh.Z)));
                }
                if (p.needs != null && p.needs.mood != null && p.mindState != null && p.mindState.mentalBreaker != null)
                {
                    string mr = CombatWords.MoodRisk(p.needs.mood.CurLevel, p.mindState.mentalBreaker.BreakThresholdMinor, p.mindState.mentalBreaker.BreakThresholdMajor, p.InMentalState);
                    if (mr.Length > 0) e["mood"] = mr;
                }
                try { if (p.CurJob != null) e["job"] = VivJsonClean(p.CurJob.GetReport(p), 48); } catch (Exception) { }
                e["where"] = WhereFrom(home, p.Position.x, p.Position.z);
                fighters.Add(e);
            }
            o["fighters"] = fighters; o["others"] = others;
            o["downed"] = new JObject { ["colonists"] = downedCols, ["enemies"] = f.All.Count(h => h.Downed) };

            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            var homeArea = map.areaManager.Home;
            var homeFires = fires.Where(x => homeArea != null && homeArea[x.Position]).ToList();
            var fo = new JObject { ["count"] = fires.Count, ["home"] = homeFires.Count };
            int fx, fz;
            if (BuildHelpers.Mean(homeFires.Select(x => new[] { x.Position.x, x.Position.z }), out fx, out fz)) fo["where"] = WhereFrom(home, fx, fz);
            o["fires"] = fo;

            var dc = DefenseCountsOf(map, f);
            o["defenses"] = CombatWords.Defenses(dc);
            var pos = new JArray();
            foreach (var p in SquadArgs.Positions)
            {
                if (p == "chokepoint" && dc.Chokepoints.Count == 0) continue;
                if (p == "door" && dc.ExteriorDoors == 0) continue;
                pos.Add(p);
            }
            o["positions"] = pos;
            return o;
        }

        static string VivJsonClean(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }

        static DefenseCounts DefenseCountsOf(Map map, Fight f)
        {
            var d = new DefenseCounts { TurretsNoAmmo = 0 };
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (b is Building_TurretGun)
                {
                    d.Turrets++;
                    var pw = b.GetComp<CompPowerTrader>();
                    if (pw == null || pw.PowerOn) d.TurretsPowered++;
                    var fuel = b.GetComp<CompRefuelable>();
                    if (fuel != null && !fuel.HasFuel) d.TurretsNoAmmo++;
                }
                else if (b is Building_Trap) d.Traps++;
                else if (b is Building_Door) d.Doors++;
                else if (b.def.defName == "Sandbags") d.Sandbags++;
                else if (b.def.defName == "Barricade") d.Barricades++;
                else if (b.def == ThingDefOf.Wall) d.WallCells++;
            }
            d.ExteriorDoors = ExteriorDoors(map).Count;
            if (f.Grid != null)
            {
                d.Enclosed = d.WallCells > 0 && Enclosed(f.Grid, f.Home);
                if (f.Path != null && f.PathReal && f.Nearest != null)
                {
                    int w;
                    var cp = Approach.Chokepoint(f.Grid, f.Path, 3, out w);
                    if (cp != null && w <= 3) d.Chokepoints.Add(Approach.Compass(f.Home.x, f.Home.z, cp[0], cp[1]) + " gap, " + w + " wide");
                }
            }
            return d;
        }

        // ---------------------------------------------------------------- positions
        /// <summary>Standing cells for n pawns at a named position, or the reason there are none.</summary>
        static List<int[]> Spots(Fight f, string at, int room, int? x, int? z, int n, out string why, out string words)
        {
            why = null; words = CombatText.PositionWords(at, room);
            var g = f.Grid; var home = f.Home; var map = f.Map;
            var threat = GroupCells(f, f.Nearest);
            var path = f.Path ?? new List<int[]>();
            int range = 28;
            if (x != null)
            {
                words = "(" + x + "," + z + ")";
                var near = CoverFinder.Find(g, x.Value, z.Value, 4, threat, path, range, n, true);
                if (near.Count == 0) why = "no standing room near (" + x + "," + z + ")";
                return near.Select(s => new[] { s.X, s.Z }).ToList();
            }
            switch (at)
            {
                case "cover":
                {
                    if (threat.Count == 0) { why = "no enemy in sight to take cover from; try home or inside"; return null; }
                    var l = CoverFinder.Find(g, home.x, home.z, 20, threat, path, range, n);
                    if (l.Count == 0) { why = "no cover facing the enemy near the base; try inside"; return null; }
                    return l.Select(s => new[] { s.X, s.Z }).ToList();
                }
                case "chokepoint":
                {
                    if (threat.Count == 0 || path.Count == 0) { why = "no enemy approach to hold; try home"; return null; }
                    if (!f.PathReal) { why = "the enemy has no walking path to home (walled in?); try door or cover"; return null; }
                    int w;
                    var cp = Approach.Chokepoint(g, path, 3, out w);
                    if (cp == null || w > 3) { why = "no chokepoint between the enemy and home; try cover"; return null; }
                    int i = path.FindIndex(c => c[0] == cp[0] && c[1] == cp[1]);
                    var anchor = path[Math.Min(path.Count - 1, i + 4)];   // a few steps behind the gap, toward home
                    var l = CoverFinder.Find(g, anchor[0], anchor[1], 6, threat, path, range, n, true);
                    if (l.Count == 0) { why = "no room to stand behind the chokepoint"; return null; }
                    words = "the " + Approach.Compass(home.x, home.z, cp[0], cp[1]) + " chokepoint";
                    return l.Select(s => new[] { s.X, s.Z }).ToList();
                }
                case "door":
                {
                    var doors = ExteriorDoors(map);
                    if (room != 0) doors = doors.Where(d => DoorOfRoom(map, d, room)).ToList();
                    if (doors.Count == 0) { why = room != 0 ? "that room has no door to the outside" : "the base has no outside door"; return null; }
                    int tx = f.Nearest != null ? f.Nearest.X : home.x, tz = f.Nearest != null ? f.Nearest.Z : home.z;
                    var door = doors.OrderBy(d => BuildHelpers.Dist2(d.Position.x, d.Position.z, tx, tz)).First();
                    string outSide = OutwardSide(map, door.Position);
                    var inside = outSide == null ? door.Position : door.Position - Step(outSide) * 2;
                    var l = CoverFinder.Find(g, inside.x, inside.z, 4, threat.Count > 0 ? threat : new List<int[]> { new[] { door.Position.x, door.Position.z } }, path, range, n, true);
                    if (l.Count == 0) { why = "no room to stand inside that door"; return null; }
                    return l.Select(s => new[] { s.X, s.Z }).ToList();
                }
                case "wall_n": case "wall_e": case "wall_s": case "wall_w":
                {
                    string side = at.Substring(5);
                    var step = Step(side);
                    int dist;
                    var wall = OuterWall(map, home, side, out dist);
                    if (!wall.IsValid) { why = "no outer wall on the " + CombatText.SideWord(side) + " side of the base; try cover"; return null; }
                    var anchor = Inset(wall, side, dist, 2);
                    var enemy = threat.Count > 0 ? threat : new List<int[]> { new[] { (wall + step * 15).x, (wall + step * 15).z } };
                    var l = CoverFinder.Find(g, anchor.x, anchor.z, 6, enemy, path, range, n);
                    if (l.Count == 0) { why = "no cover behind the " + CombatText.SideWord(side) + " wall"; return null; }
                    return l.Select(s => new[] { s.X, s.Z }).ToList();
                }
                case "home":
                {
                    var l = CoverFinder.Find(g, home.x, home.z, 8, threat, path, range, n, true);
                    if (l.Count == 0) { why = "no standing room at home"; return null; }
                    return l.Select(s => new[] { s.X, s.Z }).ToList();
                }
                case "inside":
                default:
                {
                    int tx = f.Nearest != null ? f.Nearest.X : -1, tz = f.Nearest != null ? f.Nearest.Z : -1;
                    var cells = new List<int[]>();
                    foreach (var p in BuildHelpers.Circle(home.x, home.z, 30, map.Size.x, map.Size.z))
                    {
                        var ff = g.F(p[0], p[1]);
                        if ((ff & CellFlag.Walkable) == 0 || (ff & (CellFlag.Door | CellFlag.Fogged)) != 0) continue;
                        if ((ff & CellFlag.Indoors) == 0 || (ff & CellFlag.Roofed) == 0) continue;
                        if (room != 0) { var r = V(p).GetRoom(map); if (r == null || r.ID != room) continue; }
                        if (!V(p).Standable(map)) continue;
                        cells.Add(p);
                    }
                    if (cells.Count == 0) { why = room != 0 ? "no free roofed cell in that room" : "no roofed indoor room near the base"; return null; }
                    // farthest from the enemy first (when there is one), then nearest home; spaced one cell apart
                    var ordered = cells.OrderByDescending(p => tx >= 0 ? BuildHelpers.Dist2(p[0], p[1], tx, tz) / 16 : 0)
                        .ThenBy(p => BuildHelpers.Dist2(p[0], p[1], home.x, home.z)).ToList();
                    var pick = new List<int[]>();
                    foreach (var p in ordered) { if (pick.Count >= n) break; if (pick.Any(q => Math.Abs(q[0] - p[0]) <= 1 && Math.Abs(q[1] - p[1]) <= 1)) continue; pick.Add(p); }
                    foreach (var p in ordered) { if (pick.Count >= n) break; if (!pick.Contains(p)) pick.Add(p); }
                    return pick;
                }
            }
        }

        static bool DoorOfRoom(Map map, Building_Door d, int room)
        {
            foreach (var dir in GenAdj.CardinalDirections)
            {
                var n = d.Position + dir;
                if (!n.InBounds(map)) continue;
                var r = n.GetRoom(map);
                if (r != null && r.ID == room) return true;
            }
            return false;
        }

        static bool GoTo(Pawn p, IntVec3 c)
        {
            if (!c.InBounds(p.Map) || !p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return false;
            var job = JobMaker.MakeJob(JobDefOf.Goto, c);
            job.playerForced = true;
            return p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        /// <summary>Each pawn to its nearest free spot (pawns by id). Returns the names that moved.</summary>
        static List<string> MoveTo(List<Pawn> pawns, List<int[]> spots, List<string> skipped)
        {
            var moved = new List<string>();
            var free = new List<int[]>(spots);
            foreach (var p in pawns.OrderBy(p => p.thingIDNumber))
            {
                if (free.Count == 0) { skipped.Add(Name(p) + " had no spot left"); continue; }
                var s = free.OrderBy(c => BuildHelpers.Dist2(c[0], c[1], p.Position.x, p.Position.z)).First();
                if (GoTo(p, V(s))) { moved.Add(Name(p)); free.Remove(s); }
                else skipped.Add(Name(p) + " cannot get there");
            }
            return moved;
        }

        // ---------------------------------------------------------------- POST /act/squad
        public HandResult Squad(SquadArgs a, CombatState state)
        {
            return Described(SquadDo(a, state), () => CombatText.SquadWhat(a.Op, a.At, a.Room));
        }

        /// <summary>The pawns an order is about: the listed ones (checked colonists), else the squad's drafted members.</summary>
        static List<Pawn> SquadPawns(SquadArgs a, CombatState state, List<string> skipped, out HandResult no)
        {
            no = null;
            var ids = a.Pawns.Count > 0 ? a.Pawns : state.Squad.Members;
            var l = new List<Pawn>();
            foreach (int id in ids)
            {
                HandResult n;
                var p = Mine(id, out n);
                if (p == null) { skipped.Add(n.Reason); continue; }
                if (p.Downed) { skipped.Add(Name(p) + " is downed"); continue; }
                l.Add(p);
            }
            if (l.Count == 0)
                no = HandResult.No(a.Pawns.Count > 0 ? (skipped.Count > 0 ? string.Join("; ", skipped) : "none of those can act")
                    : "there is no squad yet; draft one first (squad draft)");
            return l;
        }

        static string WithSkipped(string note, List<string> skipped)
        {
            return skipped.Count == 0 ? note : note + " (" + string.Join("; ", skipped.Take(4)) + ")";
        }

        HandResult SquadDo(SquadArgs a, CombatState state)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var skipped = new List<string>();
                HandResult no = null;
                int tick = Find.TickManager.TicksGame;
                switch (a.Op)
                {
                    case "draft":
                    {
                        List<Pawn> want;
                        if (a.Pawns.Count > 0)
                        {
                            want = new List<Pawn>();
                            foreach (int id in a.Pawns)
                            {
                                var p = Mine(id, out no);
                                if (p == null) { skipped.Add(no.Reason); continue; }
                                if (!Fighter(p)) { skipped.Add(Name(p) + " can't fight (" + (NonFighterWhy(p) ?? "no") + ")"); continue; }
                                want.Add(p);
                            }
                        }
                        else want = map.mapPawns.FreeColonistsSpawned.Where(p => !p.IsSlave && Fighter(p)).ToList();
                        if (want.Count == 0) return HandResult.No(skipped.Count > 0 ? string.Join("; ", skipped) : "nobody here can fight");
                        foreach (var p in want) if (!p.Drafted) p.drafter.Drafted = true;
                        foreach (var p in want) p.drafter.FireAtWill = true;
                        state.Squad = new SquadState { Members = want.Select(p => p.thingIDNumber).ToList(), SinceTick = tick };
                        return Squadded(WithSkipped("drafted " + CombatText.Names(want.Select(Name).ToList()) + " as the squad", skipped), state);
                    }
                    case "move":
                    case "retreat":
                    {
                        var pawns = SquadPawns(a, state, skipped, out no);
                        if (no != null) return no;
                        foreach (var p in pawns) if (!p.Drafted && Fighter(p)) p.drafter.Drafted = true;
                        var f = ReadFight(map, true);
                        string why, words;
                        var spots = Spots(f, a.At, a.Room, a.X, a.Z, pawns.Count, out why, out words);
                        if (spots == null || spots.Count == 0) return HandResult.No(why ?? "no spot found");
                        var moved = MoveTo(pawns, spots, skipped);
                        if (moved.Count == 0) return HandResult.No(string.Join("; ", skipped));
                        if (a.Op == "move") foreach (var p in pawns) state.Squad.Fell.Remove(p.thingIDNumber);
                        if (a.Op == "retreat")
                        {
                            state.Squad.RetreatX = spots[0][0]; state.Squad.RetreatZ = spots[0][1];
                            if (a.Below > 0) state.Squad.RetreatBelow = a.Below;
                        }
                        var extra = new JObject { ["spots"] = new JArray(spots.Select(s => new JArray(s[0], s[1]))) };
                        string verbWord = a.Op == "retreat" ? "pulled " : "sent ";
                        return Squadded(WithSkipped(verbWord + CombatText.Names(moved) + (a.Op == "retreat" ? " back to " : " to ") + words, skipped), state, extra);
                    }
                    case "hold":
                    case "fire_at_will":
                    {
                        var pawns = SquadPawns(a, state, skipped, out no);
                        if (no != null) return no;
                        var done = new List<string>();
                        foreach (var p in pawns)
                        {
                            if (!p.Drafted) { skipped.Add(Name(p) + " is not drafted"); continue; }
                            p.drafter.FireAtWill = true;
                            if (a.Op == "hold") p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                            done.Add(Name(p));
                        }
                        if (done.Count == 0) return HandResult.No(string.Join("; ", skipped));
                        state.Squad.Stance = a.Op;
                        if (a.Op == "hold") { state.Squad.FocusGroup = 0; state.Squad.FocusPawn = 0; state.Squad.FocusIds.Clear(); }
                        return Squadded(WithSkipped(a.Op == "hold" ? CombatText.Names(done) + " hold their ground and shoot from there"
                            : CombatText.Names(done) + " fire at will", skipped), state);
                    }
                    case "focus": return Focus(a, state, map, skipped);
                    case "rescue": return Rescue(map, skipped);
                    case "extinguish": return Extinguish(map);
                    case "flee": return Flee(map, a.Room);
                    case "undraft":
                    {
                        var drafted = a.Pawns.Count > 0 ? SquadPawns(a, state, skipped, out no) : map.mapPawns.FreeColonistsSpawned.Where(p => p.Drafted).ToList();
                        if (a.Pawns.Count > 0 && no != null) return no;
                        if (a.Pawns.Count == 0)
                        {
                            var home = HomeCenter(map);
                            HashSet<int> dorm;
                            var near = Hostiles(map, out dorm).Where(h => !h.Downed && !dorm.Contains(h.Id) && BuildHelpers.Dist2(h.X, h.Z, home.x, home.z) <= 900).ToList();
                            if (near.Count > 0) return HandResult.No(near.Count + " enem" + (near.Count == 1 ? "y is" : "ies are") + " still within 30 cells of home; undraft by name if you must");
                        }
                        var done = new List<string>();
                        foreach (var p in drafted) if (p.Drafted) { p.drafter.Drafted = false; done.Add(Name(p)); }
                        if (a.Pawns.Count == 0) state.Squad = new SquadState();
                        else state.Squad.Members.RemoveAll(id => a.Pawns.Contains(id));
                        if (done.Count == 0) return HandResult.No("nobody is drafted");
                        return Squadded("undrafted " + CombatText.Names(done), state);
                    }
                    default: return HandResult.No("unknown squad order '" + a.Op + "'");
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        static HandResult Squadded(string note, CombatState state, JObject extra = null)
        {
            extra = extra ?? new JObject();
            extra["squad"] = state.Squad.ToJson();
            return HandResult.Done(note, extra);
        }

        static bool MeleeOnly(Pawn p) { return !RangedPawn(p); }

        HandResult Focus(SquadArgs a, CombatState state, Map map, List<string> skipped)
        {
            HandResult no;
            var pawns = SquadPawns(a, state, skipped, out no);
            if (no != null) return no;
            var f = ReadFight(map, false);
            var live = f.All.Where(h => !h.Downed && !f.Dormant.Contains(h.Id)).ToList();
            if (live.Count == 0) return HandResult.No(f.All.Any(h => !h.Downed) ? "the only hostiles are dormant; leave them asleep unless ready" : "no hostiles on the map to focus");
            var group = f.Groups.FirstOrDefault(g => g.Id == a.Target);
            if (group != null && group.Ids.All(f.Dormant.Contains)) return HandResult.No("that group is dormant; attacking wakes it");
            HostileInfo one = group == null ? live.FirstOrDefault(h => h.Id == a.Target) : null;
            if (group == null && one == null)
                return HandResult.No("no threat group or hostile " + a.Target + " (groups: " + string.Join(", ", f.Groups.Take(6).Select(g => "#" + g.Id + " " + g.Kind)) + ")");
            bool mech = group != null ? group.Kind == "mech" : one.Mech;
            var done = new List<string>();
            foreach (var p in pawns)
            {
                if (!p.Drafted) { skipped.Add(Name(p) + " is not drafted"); continue; }
                if (mech && MeleeOnly(p)) { skipped.Add(Name(p) + " kept back: no gun against mechs"); continue; }
                var target = one ?? live.Where(h => group.Ids.Contains(h.Id)).OrderBy(h => BuildHelpers.Dist2(h.X, h.Z, p.Position.x, p.Position.z)).FirstOrDefault();
                if (target == null) continue;
                var t = ThingById(map, target.Id);
                if (t == null) continue;
                string fail;
                var act = FloatMenuUtility.GetAttackAction(p, t, out fail);
                if (act == null) { skipped.Add(Name(p) + " cannot attack " + t.LabelShort + (fail.NullOrEmpty() ? "" : ": " + fail)); continue; }
                act();
                var cj = p.CurJob;
                if (cj == null || cj.targetA.Thing != t) { skipped.Add(Name(p) + " can't hit " + t.LabelShort + " from there (range or sight)"); continue; }
                done.Add(Name(p));
            }
            if (done.Count == 0) return HandResult.No(skipped.Count > 0 ? string.Join("; ", skipped) : "nobody could attack");
            state.Squad.FocusGroup = group != null ? group.Id : 0;
            state.Squad.FocusIds = group != null ? new List<int>(group.Ids) : new List<int>();
            state.Squad.FocusPawn = one != null ? one.Id : 0;
            if (state.Squad.Stance == "hold") state.Squad.Stance = "fire_at_will";
            string what = group != null ? "the " + (group.Labels.Count > 0 ? string.Join(", ", group.Labels) : group.Kind) + " (" + group.Where + ")"
                : "the " + (one.Kind ?? "hostile");
            return Squadded(WithSkipped(CombatText.Names(done) + " focus fire on " + what, skipped), state);
        }

        HandResult Rescue(Map map, List<string> skipped)
        {
            var downed = map.mapPawns.FreeColonistsSpawned.Where(p => p.Downed && !p.InBed()).ToList();
            if (downed.Count == 0) return HandResult.No("nobody to rescue: no colonist is down outside a bed");
            var busy = new HashSet<Pawn>();
            var done = new List<string>();
            foreach (var d in downed)
            {
                var rescuer = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Downed && !busy.Contains(p) && !p.WorkTagIsDisabled(WorkTags.Caring) && !p.InMentalState && p.CanReserveAndReach(d, PathEndMode.OnCell, Danger.Deadly))
                    .OrderBy(p => p.Position.DistanceToSquared(d.Position)).FirstOrDefault();
                if (rescuer == null) { skipped.Add("nobody can reach " + Name(d)); continue; }
                var bed = RestUtility.FindBedFor(d, rescuer, false, false, null);
                if (bed == null) { skipped.Add("no free bed for " + Name(d)); continue; }
                var job = JobMaker.MakeJob(JobDefOf.Rescue, d, bed);
                job.count = 1;
                job.playerForced = true;
                if (!rescuer.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { skipped.Add(Name(rescuer) + " could not take the order"); continue; }
                busy.Add(rescuer);
                done.Add(Name(rescuer) + " carries " + Name(d));
            }
            if (done.Count == 0) return HandResult.No(string.Join("; ", skipped));
            return HandResult.Done(WithSkipped(string.Join("; ", done) + " to bed", skipped));
        }

        HandResult Extinguish(Map map)
        {
            var home = map.areaManager.Home;
            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Where(x => home != null && home[x.Position]).ToList();
            if (fires.Count == 0) return HandResult.No("no fires in the home area");
            var crew = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Downed && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter))
                .OrderBy(p => fires.Min(x => x.Position.DistanceToSquared(p.Position))).Take(4).ToList();
            if (crew.Count == 0) return HandResult.No("nobody here can fight fires");
            var done = new List<string>();
            int i = 0;
            foreach (var p in crew)
            {
                var reach = fires.Where(x => p.CanReach(x, PathEndMode.Touch, Danger.Deadly)).ToList();
                if (reach.Count == 0) continue;
                var fire = reach.OrderBy(x => x.Position.DistanceToSquared(p.Position)).Skip(i % Math.Max(1, Math.Min(reach.Count, crew.Count))).First();
                i++;
                var job = JobMaker.MakeJob(JobDefOf.BeatFire, fire);
                job.playerForced = true;
                if (p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) done.Add(Name(p));
            }
            if (done.Count == 0) return HandResult.No("nobody could take the order");
            return HandResult.Done(CombatText.Names(done) + " beat out the fires (" + fires.Count + " in the home area)");
        }

        HandResult Flee(Map map, int room)
        {
            var who = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.InBed() && NonFighterWhy(p) != null).ToList();
            if (who.Count == 0) return HandResult.No("everyone who is up can fight; nobody needs to flee");
            var f = ReadFight(map, true);
            string why, words;
            var spots = Spots(f, "inside", room, null, null, who.Count, out why, out words);
            if (spots == null || spots.Count == 0) return HandResult.No(why ?? "no safe room");
            var done = new List<string>(); var skipped = new List<string>();
            var free = new List<int[]>(spots);
            foreach (var p in who)
            {
                if (free.Count == 0) break;
                var s = free.OrderBy(c => BuildHelpers.Dist2(c[0], c[1], p.Position.x, p.Position.z)).First();
                var cell = V(s);
                if (!p.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) { skipped.Add(Name(p) + " cannot get indoors"); continue; }
                var job = JobMaker.MakeJob(JobDefOf.FleeAndCower, cell);
                job.playerForced = true;
                job.expiryInterval = 2500;   // an in-game hour; the next order (or the fight's end) lets them go back to work
                if (p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { done.Add(Name(p)); free.Remove(s); }
                else skipped.Add(Name(p) + " could not take the order");
            }
            if (done.Count == 0) return HandResult.No(skipped.Count > 0 ? string.Join("; ", skipped) : "nobody could go");
            return HandResult.Done(WithSkipped("sent " + CombatText.Names(done) + " indoors to safety", skipped));
        }

        // ---------------------------------------------------------------- POST /act/defend
        public HandResult Defend(DefendArgs a, BuildBudget left, CombatState state)
        {
            return Described(DefendDo(a, left, state), () => CombatText.DefendWhat(a.Kind));
        }

        /// <summary>n/e/s/w for side=threat: the nearest awake group, else the last raid's side, else the map edge nearest home.</summary>
        static string ResolveSide(Fight f, string side, CombatState state)
        {
            if (side != "threat") return side;
            if (f.Nearest != null) return CombatText.Cardinal(Approach.CompassKey(f.Home.x, f.Home.z, f.Nearest.X, f.Nearest.Z));
            if (state != null && state.LastSide != null) return CombatText.Cardinal(state.LastSide);
            var m = f.Map; var h = f.Home;
            int dn = m.Size.z - 1 - h.z, ds = h.z, de = m.Size.x - 1 - h.x, dw = h.x;
            int min = Math.Min(Math.Min(dn, ds), Math.Min(de, dw));
            return min == dn ? "n" : min == de ? "e" : min == ds ? "s" : "w";
        }

        /// <summary>Where a defense line on a side goes: a few cells inside the base wall on that side, else 10 cells out from home.</summary>
        static IntVec3 SideAnchor(Fight f, string side, out bool walled)
        {
            var map = f.Map; var step = Step(side);
            int dist;
            var wall = OuterWall(map, f.Home, side, out dist);
            walled = wall.IsValid;
            if (walled) return Inset(wall, side, dist, 3);
            var a = f.Home + step * 10;
            return new IntVec3(Math.Max(1, Math.Min(map.Size.x - 2, a.x)), 0, Math.Max(1, Math.Min(map.Size.z - 2, a.z)));
        }

        static ThingDef Researched(string defName, string what, out HandResult no)
        {
            no = null;
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (d == null) { no = HandResult.No("this game has no " + what); return null; }
            if (!d.IsResearchFinished)
            {
                var need = d.researchPrerequisites == null ? "" : string.Join(", ", d.researchPrerequisites.Where(r => !r.IsFinished).Select(r => r.LabelCap.ToString()));
                no = HandResult.No(what + " need research first" + (need.Length > 0 ? " (" + need + ")" : ""));
                return null;
            }
            return d;
        }

        /// <summary>Watts the best power net can spare now (gain minus use), plus whether any battery holds charge.</summary>
        static float SpareWatts(Map map, out bool stored)
        {
            float best = 0f; stored = false;
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                float w = net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick;
                if (w > best) best = w;
                if (net.CurrentStoredEnergy() > 1f) stored = true;
            }
            return best;
        }

        /// <summary>Places blueprints of def (with stuff) at the cells that take one; marks and counts. Never over beds or graves.</summary>
        static int PlaceAll(Map map, ThingDef def, ThingDef stuff, IEnumerable<int[]> cells, int max, BuildHelpers.MarkBuilder mb, ref string firstReason)
        {
            int n = 0;
            foreach (var p in cells)
            {
                if (n >= max) break;
                var c = V(p);
                if (!c.InBounds(map) || c.Fogged(map)) continue;
                var rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                string blocked = FootprintBlock(map, rect);
                if (blocked != null) { if (firstReason == null) firstReason = blocked; continue; }
                var rep = GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map, false, null, null, stuff);
                if (!rep.Accepted) { if (firstReason == null) firstReason = rep.Reason.NullOrEmpty() ? "it does not fit" : rep.Reason; continue; }
                var bp = GenConstruct.PlaceBlueprintForBuild(def, c, map, Rot4.North, Faction.OfPlayer, stuff);
                if (bp == null) continue;
                mb.Blueprint(def.defName, c.x, c.z, bp.thingIDNumber);
                n++;
            }
            return n;
        }

        HandResult DefendDo(DefendArgs a, BuildBudget left, CombatState state)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                if (left.Blueprints < 1) return HandResult.No("no blueprints left this cycle");
                var f = ReadFight(map, true);
                HandResult no;
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                string first = null;
                string side = ResolveSide(f, a.Side, state) ?? "n";
                IntVec3 anchor; string where; string facing = side;
                bool walled = false;
                if (a.X != null && a.Kind != "killbox") { anchor = new IntVec3(a.X.Value, 0, a.Z.Value); where = "at (" + a.X + "," + a.Z + ")"; }
                else if (a.At == "chokepoint")
                {
                    if (f.Path == null || f.Nearest == null || !f.PathReal) return HandResult.No("no enemy walking path to find a chokepoint on; give a side");
                    int w;
                    var cp = Approach.Chokepoint(f.Grid, f.Path, 3, out w);
                    if (cp == null || w > 3) return HandResult.No("no chokepoint between the enemy and home; give a side");
                    int i = f.Path.FindIndex(c => c[0] == cp[0] && c[1] == cp[1]);
                    var an = f.Path[Math.Min(f.Path.Count - 1, i + 3)];
                    anchor = V(an);
                    facing = CombatText.Cardinal(Approach.CompassKey(an[0], an[1], cp[0], cp[1])) ?? side;
                    where = "behind the " + Approach.Compass(f.Home.x, f.Home.z, cp[0], cp[1]) + " chokepoint";
                }
                else if (a.At == "entrance" || a.Kind == "killbox")
                {
                    var doors = ExteriorDoors(map);
                    if (doors.Count == 0) return HandResult.No("the base has no outside door to " + (a.Kind == "killbox" ? "build a killbox in front of" : "defend"));
                    var step = Step(side);
                    var toward = a.X != null ? new IntVec3(a.X.Value, 0, a.Z.Value) : f.Home + step * 30;   // killbox at x,z: the door nearest it
                    var door = doors.OrderBy(d => BuildHelpers.Dist2(d.Position.x, d.Position.z, toward.x, toward.z)).First();
                    string outSide = OutwardSide(map, door.Position) ?? side;
                    facing = outSide;
                    anchor = door.Position - Step(outSide) * 3;
                    where = "inside the " + Approach.Compass(f.Home.x, f.Home.z, door.Position.x, door.Position.z) + " door";
                    if (a.Kind == "killbox") return Killbox(map, f.Grid, door.Position, outSide, a, left, mb, where);
                }
                else { anchor = SideAnchor(f, side, out walled); where = (walled ? "inside the " : "on the ") + CombatText.SideWord(side) + " side"; }

                switch (a.Kind)
                {
                    case "sandbags":
                    case "barricade":
                    case "wall":
                    {
                        ThingDef def;
                        if (a.Kind == "wall") def = ThingDefOf.Wall;
                        else
                        {
                            def = Researched(a.Kind == "sandbags" ? "Sandbags" : "Barricade", a.Kind, out no);
                            if (def == null) return no;
                        }
                        var line = DefenseLayout.LineAcross(anchor.x, anchor.z, facing, Math.Min(a.Length, left.Blueprints));
                        int dropped;
                        var cells = DefenseLayout.Buildable(f.Grid, line, out dropped);
                        if (cells.Count == 0) return HandResult.No("no buildable ground for a line " + where);
                        var stuff = ResolveStuff(map, def, a.Stuff, a.Kind == "wall", cells.Count * BuildHelpers.WallStuffCost, out no);
                        if (no != null) return no;
                        int n = PlaceAll(map, def, stuff, cells, left.Blueprints, mb, ref first);
                        if (n == 0) return HandResult.No("couldn't place any " + def.label + " " + where + (first == null ? "" : ": " + first));
                        string what = (stuff == null ? "" : StuffLabel(stuff) + " ") + def.label;
                        return HandResult.Placed("planned " + n + " " + what + (n == 1 ? "" : " cells") + " " + where + ", facing " + CombatText.SideWord(facing), mb.Marks);
                    }
                    case "turret":
                    {
                        var def = Researched("Turret_MiniTurret", "turrets", out no);
                        if (def == null) return no;
                        var pc = def.GetCompProperties<CompProperties_Power>();
                        float need = pc != null ? Math.Abs(pc.PowerConsumption) * a.Count : 0f;
                        if (need > 0f)
                        {
                            bool stored;
                            float spare = SpareWatts(map, out stored);
                            if (spare < need && !stored)
                                return HandResult.No("turrets need power: the grid spares " + (int)spare + " W, " + a.Count + " turret" + (a.Count == 1 ? "" : "s") + " need " + (int)need + " W");
                        }
                        var spots = DefenseLayout.TurretSpots(f.Grid, anchor.x, anchor.z, 8, f.Path ?? new List<int[]>(), 25, a.Count * 4);
                        if (need > 0f)
                        {
                            float per = need / a.Count;
                            int inReach = 0;
                            spots = spots.Where(s =>
                            {
                                var tr = PowerConnectionMaker.BestTransmitterForConnector(V(s), map);
                                if (tr == null || tr.PowerNet == null) return false;
                                inReach++;
                                var net = tr.PowerNet;
                                return net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick >= per || net.CurrentStoredEnergy() > 1f;
                            }).ToList();
                            if (spots.Count == 0 && inReach > 0) return HandResult.No("the power net near " + where + " can't spare " + (int)per + " W for a turret");
                        }
                        if (spots.Count == 0)
                            return HandResult.No(need > 0f ? "no spot " + where + " is in reach of a power conduit; run a conduit there first" : "no spot for a turret " + where);
                        var stuff = ResolveStuff(map, def, a.Stuff, false, 0, out no);
                        if (no != null) return no;
                        int n = PlaceAll(map, def, stuff, spots, Math.Min(a.Count, left.Blueprints), mb, ref first);
                        if (n == 0) return HandResult.No("couldn't place a turret " + where + (first == null ? "" : ": " + first));
                        return HandResult.Placed("planned " + n + " " + def.label + (n == 1 ? "" : "s") + " " + where, mb.Marks);
                    }
                    case "traps":
                    {
                        var def = Researched("TrapSpike", "spike traps", out no);
                        if (def == null) return no;
                        // the enemy's way in from that side: a path from far out on the side to home, its outdoor stretch near the base
                        var step = Step(facing);
                        var far = anchor + step * 25;
                        far = new IntVec3(Math.Max(1, Math.Min(map.Size.x - 2, far.x)), 0, Math.Max(1, Math.Min(map.Size.z - 2, far.z)));
                        var s0 = NearestWalkable(f.Grid, far.x, far.z); var h0 = NearestWalkable(f.Grid, f.Home.x, f.Home.z);
                        var path = s0 != null && h0 != null ? Approach.Path(f.Grid, s0[0], s0[1], h0[0], h0[1]) : null;
                        if (path == null) return HandResult.No("no walking path into the base from the " + CombatText.SideWord(facing));
                        path.Reverse();   // home first
                        var outdoor = path.Where(c => (f.Grid.F(c[0], c[1]) & CellFlag.Indoors) == 0
                            && BuildHelpers.Dist2(c[0], c[1], f.Home.x, f.Home.z) <= 30 * 30).ToList();
                        var cells = DefenseLayout.TrapsOnPath(f.Grid, outdoor, Math.Min(a.Count, left.Blueprints));
                        int dropped;
                        cells = DefenseLayout.Buildable(f.Grid, cells, out dropped);
                        if (cells.Count == 0) return HandResult.No("no buildable ground for traps on the way in from the " + CombatText.SideWord(facing));
                        var stuff = ResolveStuff(map, def, a.Stuff, false, 0, out no);
                        if (no != null) return no;
                        int n = PlaceAll(map, def, stuff, cells, left.Blueprints, mb, ref first);
                        if (n == 0) return HandResult.No("couldn't place traps" + (first == null ? "" : ": " + first));
                        return HandResult.Placed("planned " + n + " spike trap" + (n == 1 ? "" : "s") + " on the way in from the " + CombatText.SideWord(facing), mb.Marks);
                    }
                    default: return HandResult.No("unknown defense '" + a.Kind + "'");
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        HandResult Killbox(Map map, CombatGrid g, IntVec3 door, string outward, DefendArgs a, BuildBudget left, BuildHelpers.MarkBuilder mb, string where)
        {
            HandResult no = null;
            var lay = DefenseLayout.Killbox(door.x, door.z, outward);
            int d1, d2, d3;
            var walls = DefenseLayout.Buildable(g, lay.Walls, out d1);
            var bags = DefenseLayout.Buildable(g, lay.Sandbags, out d2);
            var traps = DefenseLayout.Buildable(g, lay.Traps, out d3);
            var bagDef = DefDatabase<ThingDef>.GetNamedSilentFail("Sandbags");
            if (bagDef != null && !bagDef.IsResearchFinished) bagDef = null;
            var trapDef = DefDatabase<ThingDef>.GetNamedSilentFail("TrapSpike");
            if (trapDef != null && !trapDef.IsResearchFinished) trapDef = null;
            int total = walls.Count + (bagDef != null ? bags.Count : 0) + (trapDef != null ? traps.Count : 0);
            if (total == 0) return HandResult.No("no buildable ground around the " + where.Replace("inside the ", "") + " for a killbox");
            if (total > left.Blueprints) return HandResult.No("a killbox here needs " + total + " blueprints; " + left.Blueprints + " are left this cycle");
            var wallStuff = walls.Count > 0 ? ResolveStuff(map, ThingDefOf.Wall, a.Stuff, true, walls.Count * BuildHelpers.WallStuffCost, out no) : null;
            if (walls.Count > 0 && no != null) return no;
            string first = null;
            int nw = walls.Count > 0 ? PlaceAll(map, ThingDefOf.Wall, wallStuff, walls, left.Blueprints, mb, ref first) : 0;
            int nb = 0, nt = 0;
            if (bagDef != null && bags.Count > 0)
            {
                var bs = ResolveStuff(map, bagDef, null, false, 0, out no);
                if (no == null) nb = PlaceAll(map, bagDef, bs, bags, left.Blueprints - nw, mb, ref first);
            }
            if (trapDef != null && traps.Count > 0)
            {
                var ts = ResolveStuff(map, trapDef, null, false, 0, out no);
                if (no == null) nt = PlaceAll(map, trapDef, ts, traps, left.Blueprints - nw - nb, mb, ref first);
            }
            if (nw + nb + nt == 0) return HandResult.No("couldn't place any of the killbox" + (first == null ? "" : ": " + first));
            var parts = new List<string>();
            if (nw > 0) parts.Add(nw + " funnel walls");
            if (nb > 0) parts.Add(nb + " sandbags");
            if (nt > 0) parts.Add(nt + " spike traps");
            string missing = (bagDef == null ? " (no sandbags yet: research)" : "") + (trapDef == null ? " (no traps yet: research)" : "");
            var extra = new JObject { ["shooters"] = new JArray(lay.Shooters.Take(6).Select(s => new JArray(s[0], s[1]))) };
            return HandResult.Placed("planned a killbox at the " + where.Replace("inside the ", "") + ": " + string.Join(", ", parts) + missing, mb.Marks, extra);
        }

        // ---------------------------------------------------------------- the fight's speed
        public HandResult CombatSpeed(int speed, bool restore)
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return HandResult.No("no game");
                if (tm.Paused) return HandResult.No("the game is paused", restore ? "put the speed back" : "slow the game for the fight");
                int cur = (int)tm.CurTimeSpeed;
                if (!restore)
                {
                    if (cur <= 1) return HandResult.No("the game is already at normal speed", "slow the game for the fight");
                    tm.CurTimeSpeed = TimeSpeed.Normal;
                    return HandResult.Done("slowed the game to normal speed for the fight", new JObject { ["before"] = cur });
                }
                if (cur != 1) return HandResult.No("the player changed the speed during the fight; leaving it", "put the speed back");
                if (speed < 2 || speed > 4) return HandResult.No("no speed to put back", "put the speed back");
                tm.CurTimeSpeed = (TimeSpeed)speed;
                return HandResult.Done("put the speed back to " + speed + "x after the fight");
            }
            catch (Exception e) { return Failed(e); }
        }
    }

    /// <summary>Every 250 game ticks: refreshes the combat status the agent reads in GET /vivarium, remembers hostile positions
    /// (for "heading"), and runs the squad's tiny controller (retreat the wounded, keep focus fire, rescue the downed).
    /// Each controller act is a greentext line and an action-log entry by "controller".</summary>
    public static class VivariumCombatTicker
    {
        /// <summary>Hostile positions one pass ago (id → x,z).</summary>
        public static readonly Dictionary<int, int[]> Prev = new Dictionary<int, int[]>();
        static readonly Dictionary<int, int[]> Last = new Dictionary<int, int[]>();
        static int _failures, _prevAwake;

        public static void Tick()
        {
            try { TickDo(); }
            catch (Exception e)
            {
                if (_failures++ % 100 == 0) Log.Warning("[RimWorldLLM] vivarium combat tick failed (" + _failures + "x): " + e);
            }
        }

        static void TickDo()
        {
            var v = VivariumGame.State;
            var map = Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome ? Find.CurrentMap : Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
            int tick = Find.TickManager.TicksGame;
            if (map == null) { v.Combat.Update(false, 0, tick); return; }
            var awake = new List<Pawn>();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (VivariumHands.IsHostile(p) && !p.Downed && !VivariumHands.Dormant(p)) awake.Add(p);
            // positions: Prev = what Last held, Last = now
            Prev.Clear();
            foreach (var kv in Last) Prev[kv.Key] = kv.Value;
            Last.Clear();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (VivariumHands.IsHostile(p)) Last[p.thingIDNumber] = new[] { p.Position.x, p.Position.z };
            var homeArea = map.areaManager.Home;
            bool fire = homeArea != null && map.listerThings.ThingsOfDef(ThingDefOf.Fire).Any(x => homeArea[x.Position]);
            v.Combat.Update(awake.Count > 0 || fire, awake.Count, tick);
            if (_prevAwake == 0 && awake.Count > 0)
            {
                int x, z;
                if (BuildHelpers.Mean(awake.Select(p => new[] { p.Position.x, p.Position.z }), out x, out z))
                {
                    var home = VivariumHands.HomeCenterOf(map);
                    v.Combat.LastSide = Approach.CompassKey(home.x, home.z, x, z);
                }
            }
            _prevAwake = awake.Count;
            var sq = v.Combat.Squad;
            if (awake.Count == 0) sq.Fell.Clear();   // a new fight starts with nobody marked as fallen back
            if (!sq.Active || awake.Count == 0) return;
            Control(v, map, sq, tick);
        }

        static void Control(Vivarium v, Map map, SquadState sq, int tick)
        {
            HashSet<int> dorm;
            var hostiles = VivariumHands.Hostiles(map, out dorm).Where(h => !dorm.Contains(h.Id)).ToList();
            var hc = VivariumHands.HomeCenterOf(map);
            var groups = CombatWords.Group(hostiles, hc.x, hc.z);
            var members = new List<SquadMember>();
            var pawns = new Dictionary<int, Pawn>();
            foreach (int id in sq.Members)
            {
                var p = Routes.FindPawn(id);
                if (p == null || !p.Spawned || p.Dead || p.Map != map) continue;
                pawns[id] = p;
                var job = p.CurJob;
                int target = 0;
                if (job != null && (job.def == JobDefOf.AttackStatic || job.def == JobDefOf.AttackMelee) && job.targetA.Thing != null) target = job.targetA.Thing.thingIDNumber;
                if (job != null && job.def == JobDefOf.Rescue && job.targetA.Thing != null) target = job.targetA.Thing.thingIDNumber;
                bool busy = job != null && job.playerForced && (job.def == JobDefOf.Goto || job.def == JobDefOf.Rescue || job.def == JobDefOf.BeatFire);
                if (p.stances != null && p.stances.curStance is Stance_Busy) busy = true;   // mid-burst: don't interrupt
                float hp = p.health.summaryHealth.SummaryHealthPercent;
                bool bleed = p.health.hediffSet.BleedRateTotal > 0.1f;
                if (sq.Fell.Contains(id) && hp >= sq.RetreatBelow + 0.2f && !bleed) sq.Fell.Remove(id);   // patched up: back in the fight
                members.Add(new SquadMember
                {
                    Id = id, Health = p.health.summaryHealth.SummaryHealthPercent, Downed = p.Downed, Drafted = p.Drafted,
                    Bleeding = bleed, Fleeing = sq.Fell.Contains(id),
                    X = p.Position.x, Z = p.Position.z, TargetId = target, Busy = busy
                });
            }
            var downed = map.mapPawns.FreeColonistsSpawned.Where(p => p.Downed && !p.InBed()).Select(p => new[] { p.thingIDNumber, p.Position.x, p.Position.z }).ToList();
            var orders = SquadTactics.Decide(sq, members, hostiles, groups, downed);
            foreach (var o in orders)
            {
                Pawn p;
                if (!pawns.TryGetValue(o.Pawn, out p)) continue;
                string note = null;
                if (o.Kind == "retreat")
                {
                    var cell = new IntVec3(o.X, 0, o.Z);
                    var job = JobMaker.MakeJob(JobDefOf.Goto, cell); job.playerForced = true;
                    if (p.CanReach(cell, PathEndMode.OnCell, Danger.Deadly) && p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                    { sq.Fell.Add(p.thingIDNumber); note = p.LabelShort + " " + o.Note; }
                }
                else if (o.Kind == "attack")
                {
                    if (sq.Stance == "hold") continue;   // holding: they shoot from where they stand
                    Thing t = null;
                    foreach (var th in map.mapPawns.AllPawnsSpawned) if (th.thingIDNumber == o.Target) { t = th; break; }
                    if (t == null) continue;
                    string fail;
                    var act = FloatMenuUtility.GetAttackAction(p, t, out fail);
                    if (act != null) { act(); }   // keeping focus is routine: no chat line
                }
                else if (o.Kind == "rescue")
                {
                    var d = Routes.FindPawn(o.Target);
                    if (d == null || !d.Downed || !p.CanReserveAndReach(d, PathEndMode.OnCell, Danger.Deadly)) continue;
                    var bed = RestUtility.FindBedFor(d, p, false, false, null);
                    if (bed == null) continue;
                    var job = JobMaker.MakeJob(JobDefOf.Rescue, d, bed); job.count = 1; job.playerForced = true;
                    if (p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) note = p.LabelShort + " is carrying " + d.LabelShort + " to safety";
                }
                if (note != null)
                {
                    v.Actions.Add("tactic", note, tick, "controller");
                    ChatFeed.Action(v.Chat, tick, note, null);
                }
            }
        }
    }
}

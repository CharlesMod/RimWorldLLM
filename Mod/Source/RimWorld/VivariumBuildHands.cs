using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Phase 2 game bodies: designations, blueprints, rooms, zones, areas, bills and undo
    /// (docs/VIVARIUM.md "Phase 2"). Game thread only. Every body goes through the calls the vanilla designators make.</summary>
    public sealed partial class VivariumHands
    {
        // ---------------------------------------------------------------- shared
        /// <summary>The current map if it is a player home, else the first player home map; null when none.</summary>
        static Map HomeMap()
        {
            var m = Find.CurrentMap;
            if (m != null && m.IsPlayerHome) return m;
            return Find.Maps.FirstOrDefault(x => x.IsPlayerHome);
        }

        /// <summary>The centre of the base: mean of the home-area cells, else of the colonists' beds, else of free colonists.</summary>
        static IntVec3 HomeCenter(Map map)
        {
            int x, z;
            var home = map.areaManager.Home;
            if (home != null && home.TrueCount > 0 && BuildHelpers.Mean(home.ActiveCells.Select(c => new[] { c.x, c.z }), out x, out z))
                return new IntVec3(x, 0, z);
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Select(b => new[] { b.Position.x, b.Position.z }).ToList();
            if (BuildHelpers.Mean(beds, out x, out z)) return new IntVec3(x, 0, z);
            var cols = map.mapPawns.FreeColonistsSpawned.Select(p => new[] { p.Position.x, p.Position.z }).ToList();
            if (BuildHelpers.Mean(cols, out x, out z)) return new IntVec3(x, 0, z);
            return map.Center;
        }

        static List<int[]> WhereList(Where w, Map map) { return BuildHelpers.WhereCells(w, map.Size.x, map.Size.z); }

        static Thing ThingById(Map map, int id)
        {
            foreach (var t in map.listerThings.AllThings) if (t.thingIDNumber == id) return t;
            return null;
        }

        static bool IsBedOrGrave(Thing t) { return t is Building_Bed || t is Building_Grave; }

        static ThingDef BuiltDef(Thing t)
        {
            if (t is Blueprint || t is Frame) return t.def.entityDefToBuild as ThingDef;
            return t.def;
        }

        /// <summary>Why a cell cannot take an edifice or zone, or null: bed/grave/blueprint check for footprints.</summary>
        static string FootprintBlock(Map map, CellRect rect)
        {
            foreach (var c in rect.Cells)
            {
                if (!c.InBounds(map)) return "(" + c.x + "," + c.z + ") is outside the map";
                foreach (var t in c.GetThingList(map))
                {
                    if (IsBedOrGrave(t)) return "a " + t.LabelNoCount + " is at (" + c.x + "," + c.z + "); never build over beds or graves";
                    var bd = (t is Blueprint || t is Frame) ? t.def.entityDefToBuild as ThingDef : null;
                    if (bd != null && bd.IsBed)
                        return "a bed is planned at (" + c.x + "," + c.z + "); never build over beds";
                }
            }
            return null;
        }

        static string StuffLabel(ThingDef s) { return s == null ? "" : (s.label ?? s.defName); }

        /// <summary>Stuff in stock, as allowed for the def, counts: stored plus loose.</summary>
        static List<KeyValuePair<string, int>> AllowedCounts(Map map, ThingDef def)
        {
            var l = new List<KeyValuePair<string, int>>();
            foreach (var s in GenStuff.AllowedStuffsFor(def)) l.Add(new KeyValuePair<string, int>(s.defName, VivariumLoose.Usable(map, s)));
            return l;
        }

        /// <summary>Resolves a stuff word for a def: the narrator's pick (checked) or the best in stock. no is set on refusal.</summary>
        static ThingDef ResolveStuff(Map map, ThingDef def, string word, bool ranked, int need, out HandResult no)
        {
            no = null;
            if (!def.MadeFromStuff) return null;
            if (!string.IsNullOrWhiteSpace(word))
            {
                string dn = BuildPure.StuffDef(word);
                var s = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
                if (s == null)
                    s = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d => d.IsStuff && Eq(d.label, word));
                if (s == null || !s.IsStuff)
                {
                    no = HandResult.No(BuildHelpers.Unknown("material", word, BuildPure.StuffAliases.Keys));
                    return null;
                }
                if (!GenStuff.AllowedStuffsFor(def).Contains(s))
                {
                    no = HandResult.No("a " + def.label + " can't be made of " + (s.label ?? s.defName));
                    return null;
                }
                return s;
            }
            string pick = BuildHelpers.PickStuff(AllowedCounts(map, def), need, ranked);
            if (pick == null)
            {
                no = HandResult.No("no material in stock to make a " + def.label + " from");
                return null;
            }
            return DefDatabase<ThingDef>.GetNamed(pick);
        }

        /// <summary>First buildable, researched def for a word (alias list, defName, then label), or the reason not.</summary>
        static ThingDef ResolveThing(string word, out HandResult no)
        {
            no = null;
            var names = BuildPure.ThingDefs(word);
            var found = new List<ThingDef>();
            foreach (var n in names)
            {
                var d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null) found.Add(d);
            }
            if (found.Count == 0 && !string.IsNullOrWhiteSpace(word))
            {
                var d = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(x => x.BuildableByPlayer && Eq(x.label, word));
                if (d != null) found.Add(d);
            }
            if (found.Count == 0) { no = HandResult.No(BuildHelpers.Unknown("thing", word, BuildPure.ThingAliases.Keys)); return null; }
            foreach (var d in found) if (d.BuildableByPlayer && d.IsResearchFinished) return d;
            var first = found[0];
            if (!first.BuildableByPlayer) { no = HandResult.No("a " + first.label + " can't be built"); return null; }
            var need = first.researchPrerequisites == null ? "" : string.Join(", ", first.researchPrerequisites.Where(r => !r.IsFinished).Select(r => r.LabelCap.ToString()));
            no = HandResult.No("a " + first.label + " needs research first" + (need.Length > 0 ? " (" + need + ")" : ""));
            return null;
        }

/// <summary>Coolers and vents go into a wall, not on a room's floor.</summary>
        static bool WallMounted(ThingDef def)
        {
            return typeof(Building_Cooler).IsAssignableFrom(def.thingClass) || typeof(Building_Vent).IsAssignableFrom(def.thingClass);
        }

        /// <summary>The room's plain wall cells (impassable edifices that are not doors), not corners: each has a room
        /// cell as a cardinal neighbour.</summary>
        static List<IntVec3> RoomWallCells(Map map, Room room)
        {
            var seen = new HashSet<IntVec3>();
            foreach (var c in room.Cells)
                foreach (var d in GenAdj.CardinalDirections)
                {
                    var n = c + d;
                    if (!n.InBounds(map) || seen.Contains(n)) continue;
                    var e = n.GetEdifice(map);
                    if (e == null || e is Building_Door || e.def.passability != Traversability.Impassable || !e.def.building.isPlaceOverableWall && e.def != ThingDefOf.Wall) continue;
                    seen.Add(n);
                }
            return seen.ToList();
        }

                static bool IsImpassableEdifice(Map map, IntVec3 c)
        {
            var e = c.GetEdifice(map);
            return e != null && e.def.passability == Traversability.Impassable;
        }

        // ---------------------------------------------------------------- build
        public HandResult Build(BuildArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                if (left.Blueprints < 1) return HandResult.No("no blueprints left this cycle");
                HandResult no;
                var def = ResolveThing(a.Thing, out no);
                if (def == null) return no;
                var stuff = ResolveStuff(map, def, a.Stuff, false, 0, out no);
                if (no != null) return no;
                var home = HomeCenter(map);
                int[] rots = BuildHelpers.RotOrder(a.Rot);
                IntVec3 cell = IntVec3.Invalid; Rot4 rot = Rot4.South;
                string firstReason = null;

                Func<IntVec3, bool> tryCell = c =>
                {
                    foreach (int r in rots)
                    {
                        var rt = new Rot4(r);
                        var rect = GenAdj.OccupiedRect(c, rt, def.size);
                        string blocked = FootprintBlock(map, rect);
                        if (blocked != null) { if (firstReason == null) firstReason = blocked; continue; }
                        var rep = GenConstruct.CanPlaceBlueprintAt(def, c, rt, map, false, null, null, stuff);
                        if (rep.Accepted) { cell = c; rot = rt; return true; }
                        if (firstReason == null) firstReason = rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason;
                    }
                    return false;
                };

                if (a.At.X != null && a.At.Z != null)
                {
                    var c = new IntVec3(a.At.X.Value, 0, a.At.Z.Value);
                    if (!c.InBounds(map)) return HandResult.No("(" + c.x + "," + c.z + ") is outside the map");
                    if (c.Fogged(map)) return HandResult.No("(" + c.x + "," + c.z + ") is not visible yet");
                    if (!tryCell(c)) return HandResult.No("can't put a " + def.label + " at (" + c.x + "," + c.z + "): " + firstReason);
                }
                else if (a.At.Room != 0)
                {
                    Room room = null;
                    foreach (var r in map.regionGrid.AllRooms) if (r.ID == a.At.Room) { room = r; break; }
                    if (room == null) return HandResult.No("no room " + a.At.Room + " on the map");
                    if (WallMounted(def))
                    {
                        // a cooler (or vent) sits in the room's wall: cold side (South rotated by its rotation) inside,
                        // the other side outdoors or in another room; it replaces a plain wall cell, never a door or a corner
                        foreach (var c in RoomWallCells(map, room).OrderBy(c => BuildHelpers.Dist2(c.x, c.z, home.x, home.z)))
                        {
                            foreach (int r in new[] { 0, 1, 2, 3 })
                            {
                                var rt = new Rot4(r);
                                var cold = c + IntVec3.South.RotatedBy(rt);
                                var hot = c + IntVec3.North.RotatedBy(rt);
                                if (!cold.InBounds(map) || !hot.InBounds(map) || cold.GetRoom(map) != room || hot.GetRoom(map) == room) continue;
                                if (IsImpassableEdifice(map, hot)) continue;
                                var rep = GenConstruct.CanPlaceBlueprintAt(def, c, rt, map, false, null, null, stuff);
                                if (rep.Accepted) { cell = c; rot = rt; break; }
                                if (firstReason == null) firstReason = rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason;
                            }
                            if (cell.IsValid) break;
                        }
                        if (!cell.IsValid) return HandResult.No("no wall cell of that room takes a " + def.label + " (it needs a plain wall with open air or another room behind it)" + (firstReason == null ? "" : ": " + firstReason));
                        goto place;
                    }
                    var scored = new List<KeyValuePair<int, IntVec3>>();
                    foreach (var c in room.Cells)
                    {
                        int walls = 0; bool nearDoor = false;
                        foreach (var d in GenAdj.CardinalDirections)
                        {
                            var n = c + d;
                            if (!n.InBounds(map)) continue;
                            if (IsImpassableEdifice(map, n)) walls++;
                            if (n.GetDoor(map) != null) nearDoor = true;
                        }
                        if (c.GetDoor(map) != null) continue;
                        scored.Add(new KeyValuePair<int, IntVec3>(BuildHelpers.PlaceScore(walls, nearDoor), c));
                    }
                    foreach (var kv in scored.OrderByDescending(k => k.Key).ThenBy(k => BuildHelpers.Dist2(k.Value.x, k.Value.z, home.x, home.z)))
                        if (tryCell(kv.Value)) break;
                    if (!cell.IsValid) return HandResult.No("no free spot for a " + def.label + " in that room" + (firstReason == null ? "" : ": " + firstReason));
                }
                else
                {
                    var cand = BuildHelpers.Circle(home.x, home.z, 25, map.Size.x, map.Size.z);
                    foreach (var p in BuildHelpers.NearestFirst(cand, home.x, home.z, cand.Count))
                    {
                        var c = new IntVec3(p[0], 0, p[1]);
                        if (c.Fogged(map) || c.Roofed(map) || !c.Standable(map)) continue;
                        if (tryCell(c)) break;
                    }
                    if (!cell.IsValid) return HandResult.No("no valid spot for a " + def.label + " near the base" + (firstReason == null ? "" : ": " + firstReason));
                }

            place:
                string sealOff = VivariumBaseRead.Guard(map, def, new[] { cell }, rot);
                if (sealOff != null) return HandResult.No("can't put a " + def.label + " at (" + cell.x + "," + cell.z + "): it " + sealOff);
                var bp = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
                if (bp == null) return HandResult.No("the game would not place a " + def.label + " at (" + cell.x + "," + cell.z + ")");
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                mb.Blueprint(def.defName, cell.x, cell.z, bp.thingIDNumber);
                string what = (stuff == null ? "" : StuffLabel(stuff) + " ") + def.label;
                return HandResult.Placed("placed a " + what + " blueprint at (" + cell.x + "," + cell.z + ")", mb.Marks);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---------------------------------------------------------------- room
        /// <summary>Why a candidate room box cannot be built (null when it can). edges lists wall cells needing a blueprint.</summary>
        static string RoomBoxProblem(Map map, CellBox box, ThingDef wallDef, ThingDef wallStuff, out List<int[]> wallsToPlace, out int[] doorCell, string side, ThingDef doorDef, ThingDef doorStuff, out bool doorNeedsPlacing)
        {
            wallsToPlace = new List<int[]>(); doorCell = null; doorNeedsPlacing = true;
            if (!BuildHelpers.InMap(box.X, box.Z, box.W, box.H, map.Size.x, map.Size.z)) return "that box is not inside the map";
            var rect = new CellRect(box.X, box.Z, box.W, box.H);
            var walls = BuildPure.RoomWalls(box, side, out doorCell);
            foreach (var c in BuildPure.RoomInside(box))
            {
                var cell = new IntVec3(c[0], 0, c[1]);
                string at = "(" + c[0] + "," + c[1] + ")";
                if (cell.Fogged(map)) return "inside " + at + " is not visible yet";
                if (!cell.Standable(map)) return "inside " + at + " can't be stood on";
                var terr = cell.GetTerrain(map);
                if (terr.IsWater) return "inside " + at + " is water";
                if (!terr.affordances.Contains(TerrainAffordanceDefOf.Light)) return "inside " + at + " is not buildable ground";
                if (map.zoneManager.ZoneAt(cell) != null) return "inside " + at + " is in a zone";
                foreach (var t in cell.GetThingList(map))
                {
                    if (IsBedOrGrave(t)) return "a " + t.LabelNoCount + " is inside at " + at;
                    if (t is Building || t is Blueprint || t is Frame) return "something is already built or planned inside at " + at;
                    var pl = t as Plant;
                    if (pl != null && pl.def.plant.IsTree) return "a tree is inside at " + at;
                }
            }
            foreach (var w in walls)
            {
                var cell = new IntVec3(w[0], 0, w[1]);
                string at = "(" + w[0] + "," + w[1] + ")";
                if (cell.Fogged(map)) return "wall cell " + at + " is not visible yet";
                if (map.zoneManager.ZoneAt(cell) != null) return "wall cell " + at + " is in a zone";
                if (cell.GetThingList(map).Any(IsBedOrGrave)) return "a bed or grave is on the wall line at " + at;
                if (IsImpassableEdifice(map, cell)) continue;   // reused
                if (cell.GetThingList(map).Any(t => (t is Blueprint || t is Frame) && BuiltDef(t) == wallDef)) continue;
                var rep = GenConstruct.CanPlaceBlueprintAt(wallDef, cell, Rot4.North, map, false, null, null, wallStuff);
                if (!rep.Accepted) return "can't put a wall at " + at + ": " + (rep.Reason.NullOrEmpty() ? "it does not fit" : rep.Reason);
                wallsToPlace.Add(w);
            }
            var dc = new IntVec3(doorCell[0], 0, doorCell[1]);
            string dat = "(" + doorCell[0] + "," + doorCell[1] + ")";
            if (dc.Fogged(map)) return "door cell " + dat + " is not visible yet";
            if (dc.GetThingList(map).Any(IsBedOrGrave)) return "a bed or grave is at the door cell " + dat;
            if (dc.GetDoor(map) != null || dc.GetThingList(map).Any(t => (t is Blueprint || t is Frame) && BuiltDef(t) == doorDef)) doorNeedsPlacing = false;
            else
            {
                if (IsImpassableEdifice(map, dc)) return "the door cell " + dat + " is already solid";
                var rep = GenConstruct.CanPlaceBlueprintAt(doorDef, dc, Rot4.North, map, false, null, null, doorStuff);
                if (!rep.Accepted) return "can't put a door at " + dat + ": " + (rep.Reason.NullOrEmpty() ? "it does not fit" : rep.Reason);
            }
            return null;
        }

        public HandResult Room(RoomArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                HandResult no;
                var wallDef = ResolveThing("wall", out no);
                if (wallDef == null) return no;
                var doorDef = ResolveThing("door", out no);
                if (doorDef == null) return no;
                int w = a.W, h = a.H;
                if (w <= 0 || h <= 0) { BasePlanner.Inside(a.Purpose, a.Size, a.Beds, out w, out h); w += 2; h += 2; }
                if (w < BuildPure.MinRoom || h < BuildPure.MinRoom) return HandResult.No("a room needs at least 3x3 cells");

                // stuff: the walls need 5 each; ask for the full wall count of a closed box as the stock check
                int needStuff = (2 * (w + h) - 4) * BuildHelpers.WallStuffCost;
                var wallStuff = ResolveStuff(map, wallDef, a.Stuff, true, needStuff, out no);
                if (no != null) return no;
                ThingDef doorStuff = null;
                if (doorDef.MadeFromStuff)
                {
                    if (wallStuff != null && GenStuff.AllowedStuffsFor(doorDef).Contains(wallStuff)) doorStuff = wallStuff;
                    else { doorStuff = ResolveStuff(map, doorDef, null, false, 0, out no); if (no != null) return no; }
                }
                if (a.At.X != null && a.At.Z != null) return RoomAtCorner(map, a, left, w, h, wallDef, wallStuff, doorDef, doorStuff);

                // the base planner picks the site: attached to the base, door onto a kept-clear walkway, guard-checked
                var g = VivariumBaseRead.Read(map);
                var req = new RoomRequest { Role = a.Purpose, Size = a.Size, Near = a.Near, For = a.For, Door = a.Door, Beds = a.Beds };
                if (a.W > 0 && a.H > 0) { req.InW = a.W - 2; req.InH = a.H - 2; }
                RoomPlan plan = null; string why = null;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    plan = BasePlanner.Plan(g, req, out why);
                    if (plan == null) break;
                    // the grid can't see everything the game checks (a pawn's corpse, a minified thing, a mod's rule): ask the game
                    // about each new cell; a cell it refuses is taken off the grid and the planner picks again
                    int[] bad = null; string badWhy = null;
                    foreach (var c in plan.Walls.Concat(new[] { plan.Door }))
                    {
                        var cell = new IntVec3(c[0], 0, c[1]);
                        bool isDoor = c == plan.Door;
                        string fb = FootprintBlock(map, CellRect.SingleCell(cell));
                        if (fb != null) { bad = c; badWhy = fb; break; }
                        var rep = GenConstruct.CanPlaceBlueprintAt(isDoor ? doorDef : wallDef, cell, Rot4.North, map, false, null, null, isDoor ? doorStuff : wallStuff);
                        if (!rep.Accepted) { bad = c; badWhy = rep.Reason.NullOrEmpty() ? "it does not fit" : rep.Reason; break; }
                    }
                    if (bad == null) break;
                    g.Flags[g.Idx(bad[0], bad[1])] &= ~BaseFlag.Free;
                    why = "the game refused (" + bad[0] + "," + bad[1] + "): " + badWhy;
                    plan = null;
                }
                if (plan == null) return HandResult.No("can't fit a " + a.Purpose + " into the base: " + (why ?? "no site"));

                // furniture first: a cooler takes a wall cell instead of a wall
                var furniture = new List<KeyValuePair<FurniturePlace, ThingDef>>();
                var skipped = new List<string>();
                if (a.Furnish)
                {
                    var items = FurnitureLayout.Items(a.Purpose, a.Size, a.Beds);
                    var defs = new Dictionary<FurnitureItem, ThingDef>();
                    foreach (var it in items)
                    {
                        HandResult n2;
                        var d = ResolveThing(it.Word, out n2);
                        if (d == null) { if (!skipped.Contains(it.Word)) skipped.Add(it.Word); continue; }
                        it.W = d.size.x; it.H = d.size.z;
                        defs[it] = d;
                    }
                    var outer = plan.Walls.Where(c => BasePlanner.OuterWallCell(g, plan.Box, c)).ToList();
                    var placesByItem = FurnitureLayout.Place(plan.Inside, plan.Door, defs.Keys.ToList(), outer);
                    // Place returns the pieces in item order; pair them back by word in order
                    var queue = defs.Keys.ToList();
                    foreach (var p in placesByItem)
                    {
                        int i = queue.FindIndex(it => it.Word == p.Word);
                        if (i < 0) continue;
                        furniture.Add(new KeyValuePair<FurniturePlace, ThingDef>(p, defs[queue[i]]));
                        queue.RemoveAt(i);
                    }
                    foreach (var it in queue) if (!skipped.Contains(it.Word + " (no room for it)")) skipped.Add(it.Word + " (no room for it)");
                }
                var mounted = new HashSet<long>(furniture.Where(f => f.Value.passability == Traversability.Impassable && !plan.Inside.Contains(f.Key.Rect.X, f.Key.Rect.Z))
                                                          .Select(f => BaseGrid.Key(f.Key.Rect.X, f.Key.Rect.Z)));
                var walls = plan.Walls.Where(c => !mounted.Contains(BaseGrid.Key(c[0], c[1]))).ToList();

                int cost = walls.Count + 1;
                if (cost > left.Blueprints) return HandResult.No("that room needs " + cost + " blueprints but only " + left.Blueprints + " are left this cycle");
                if (wallStuff != null)
                {
                    int have = VivariumLoose.Usable(map, wallStuff);
                    if (have < walls.Count * BuildHelpers.WallStuffCost)
                        return HandResult.No("not enough " + StuffLabel(wallStuff) + " in stock: " + walls.Count + " walls need " + walls.Count * BuildHelpers.WallStuffCost + ", there is " + have);
                }
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                foreach (var wc in walls)
                {
                    var bp = GenConstruct.PlaceBlueprintForBuild(wallDef, new IntVec3(wc[0], 0, wc[1]), map, Rot4.North, Faction.OfPlayer, wallStuff);
                    if (bp != null) mb.Blueprint(wallDef.defName, wc[0], wc[1], bp.thingIDNumber);
                }
                var dbp = GenConstruct.PlaceBlueprintForBuild(doorDef, new IntVec3(plan.Door[0], 0, plan.Door[1]), map, Rot4.North, Faction.OfPlayer, doorStuff);
                if (dbp != null) mb.Blueprint(doorDef.defName, plan.Door[0], plan.Door[1], dbp.thingIDNumber);

                // the furniture, while blueprints are left
                var placed = new List<string>();
                int budget = left.Blueprints - cost;
                foreach (var kv in furniture)
                {
                    var p = kv.Key; var def = kv.Value;
                    if (!PlaceFurniture(map, p, def, budget, mb, skipped))
                    {
                        // a cooler that could not go in leaves its wall cell: close it with a wall
                        if (!plan.Inside.Contains(p.Rect.X, p.Rect.Z))
                        {
                            var bp = GenConstruct.PlaceBlueprintForBuild(wallDef, new IntVec3(p.Rect.X, 0, p.Rect.Z), map, Rot4.North, Faction.OfPlayer, wallStuff);
                            if (bp != null) mb.Blueprint(wallDef.defName, p.Rect.X, p.Rect.Z, bp.thingIDNumber);
                        }
                        continue;
                    }
                    placed.Add(p.Word);
                    budget--;
                }
                string note = plan.Words + " (" + BuildHelpers.Plural(walls.Count, "new " + (StuffLabel(wallStuff) + " wall").Trim(), "new " + (StuffLabel(wallStuff) + " walls").Trim())
                              + ", " + plan.Reused + " shared" + ")";
                if (placed.Count > 0) note += "; furniture: " + string.Join(", ", placed.GroupBy(x => x).Select(gr => gr.Count() > 1 ? gr.Count() + " " + gr.Key + "s" : gr.Key));
                if (skipped.Count > 0) note += "; skipped " + string.Join(", ", skipped.Distinct());
                var extra = new JObject { ["box"] = new JArray(plan.Box.X, plan.Box.Z, plan.Box.W, plan.Box.H), ["door"] = new JArray(plan.Door[0], plan.Door[1]) };
                return HandResult.Placed(note, mb.Marks, extra);
            }
            catch (Exception e) { return Failed(e); }
        }

        /// <summary>One piece of a room's furniture as a blueprint: the layout's rect and rotation back to a root cell; false (with
        /// a skipped word) when it can't go in.</summary>
        static bool PlaceFurniture(Map map, FurniturePlace p, ThingDef def, int budget, BuildHelpers.MarkBuilder mb, List<string> skipped)
        {
            if (budget <= 0) { skipped.Add(p.Word + " (cap)"); return false; }
            var rot = new Rot4(p.Rot);
            IntVec3 root = IntVec3.Invalid;
            foreach (var c in BuildPure.BoxCells(p.Rect))
            {
                var r = GenAdj.OccupiedRect(new IntVec3(c[0], 0, c[1]), rot, def.size);
                if (r.minX == p.Rect.X && r.minZ == p.Rect.Z && r.maxX == p.Rect.MaxX && r.maxZ == p.Rect.MaxZ) { root = new IntVec3(c[0], 0, c[1]); break; }
            }
            if (!root.IsValid) { skipped.Add(p.Word); return false; }
            HandResult no;
            var stuff = ResolveStuff(map, def, null, false, 0, out no);
            if (no != null) { skipped.Add(p.Word + " (no material)"); return false; }
            var rep = GenConstruct.CanPlaceBlueprintAt(def, root, rot, map, false, null, null, stuff);
            if (!rep.Accepted) { skipped.Add(p.Word); return false; }
            var bp = GenConstruct.PlaceBlueprintForBuild(def, root, map, rot, Faction.OfPlayer, stuff);
            if (bp == null) { skipped.Add(p.Word); return false; }
            mb.Blueprint(def.defName, root.x, root.z, bp.thingIDNumber);
            return true;
        }

        /// <summary>The scripts' path: a room box at an exact south-west corner (x, z), door side as given or toward home,
        /// still checked by the never-seal-off guard.</summary>
        HandResult RoomAtCorner(Map map, RoomArgs a, BuildBudget left, int w, int h, ThingDef wallDef, ThingDef wallStuff, ThingDef doorDef, ThingDef doorStuff)
        {
            var home = HomeCenter(map);
            var box = new CellBox(a.At.X.Value, a.At.Z.Value, w, h);
            string side = (a.Door ?? "auto").ToLowerInvariant();
            var sides = new List<string>();
            if (side == "auto") { string pref = BuildPure.DoorToward(box, home.x, home.z); sides.Add(pref); foreach (var s in new[] { "n", "e", "s", "w" }) if (s != pref) sides.Add(s); }
            else sides.Add(side);
            string firstReason = null;
            BaseGrid g = null;
            foreach (var sd in sides)
            {
                List<int[]> toPlace; int[] door; bool doorNeeds;
                string why = RoomBoxProblem(map, box, wallDef, wallStuff, out toPlace, out door, sd, doorDef, doorStuff, out doorNeeds);
                if (why == null)
                {
                    if (g == null) g = VivariumBaseRead.Read(map);
                    why = ReachGuard.Check(g, toPlace, new[] { door }, new[] { door });
                }
                if (firstReason == null) firstReason = why;
                if (why != null) continue;
                int cost = toPlace.Count + (doorNeeds ? 1 : 0);
                if (cost > left.Blueprints) return HandResult.No("that room needs " + cost + " blueprints but only " + left.Blueprints + " are left this cycle");
                if (wallStuff != null)
                {
                    int have = VivariumLoose.Usable(map, wallStuff);
                    if (have < toPlace.Count * BuildHelpers.WallStuffCost)
                        return HandResult.No("not enough " + StuffLabel(wallStuff) + " in stock: " + toPlace.Count + " walls need " + toPlace.Count * BuildHelpers.WallStuffCost + ", there is " + have);
                }
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                foreach (var wc in toPlace)
                {
                    var bp = GenConstruct.PlaceBlueprintForBuild(wallDef, new IntVec3(wc[0], 0, wc[1]), map, Rot4.North, Faction.OfPlayer, wallStuff);
                    if (bp != null) mb.Blueprint(wallDef.defName, wc[0], wc[1], bp.thingIDNumber);
                }
                if (doorNeeds)
                {
                    var bp = GenConstruct.PlaceBlueprintForBuild(doorDef, new IntVec3(door[0], 0, door[1]), map, Rot4.North, Faction.OfPlayer, doorStuff);
                    if (bp != null) mb.Blueprint(doorDef.defName, door[0], door[1], bp.thingIDNumber);
                }
                return HandResult.Placed(BuildHelpers.RoomNote(a.Purpose, box, toPlace.Count, StuffLabel(wallStuff), doorNeeds), mb.Marks);
            }
            return HandResult.No("can't build the room at (" + a.At.X + "," + a.At.Z + "): " + (firstReason ?? "no fit"));
        }

        // ---------------------------------------------------------------- zone
        static StoragePriority? PriorityOf(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "low": return StoragePriority.Low;
                case "normal": return StoragePriority.Normal;
                case "preferred": return StoragePriority.Preferred;
                case "important": return StoragePriority.Important;
                case "critical": return StoragePriority.Critical;
                default: return null;
            }
        }

        static bool ZoneCellFree(Map map, IntVec3 c, bool needStandable)
        {
            if (!c.InBounds(map) || c.Fogged(map)) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;
            var terr = c.GetTerrain(map);
            if (terr.IsWater) return false;
            if (needStandable && !c.Standable(map)) return false;
            foreach (var t in c.GetThingList(map)) if (t is Building || t is Blueprint || t is Frame) return false;
            return true;
        }

        /// <summary>First free w x h box among candidate corners, or null. ok tests each cell; rank gives optional tie scores (higher first).</summary>
        static CellBox FindBox(Map map, IEnumerable<int[]> corners, int w, int h, Func<IntVec3, bool> cellOk, Func<CellBox, double> score)
        {
            CellBox best = null; double bestScore = double.MinValue; int seen = 0;
            foreach (var cn in corners)
            {
                if (!BuildHelpers.InMap(cn[0], cn[1], w, h, map.Size.x, map.Size.z)) continue;
                var box = new CellBox(cn[0], cn[1], w, h);
                bool ok = true;
                foreach (var c in BuildPure.BoxCells(box)) if (!cellOk(new IntVec3(c[0], 0, c[1]))) { ok = false; break; }
                if (!ok) continue;
                double s = score == null ? 0 : score(box);
                if (s < 0 && score != null) continue;
                if (score == null) return box;
                if (s > bestScore) { best = box; bestScore = s; }
                if (++seen >= 12) break;
            }
            return best;
        }

        static void SetPreset(Zone_Stockpile z, string preset)
        {
            var f = z.settings.filter;
            switch ((preset ?? "all").ToLowerInvariant())
            {
                case "food":
                    f.SetDisallowAll(); f.SetAllow(ThingCategoryDefOf.Foods, true);
                    if (ThingCategoryDefOf.Corpses != null) f.SetAllow(ThingCategoryDefOf.Corpses, false);
                    break;
                case "meds":
                    f.SetDisallowAll(); f.SetAllow(ThingCategoryDefOf.Medicine, true);
                    break;
                case "raw":
                    f.SetDisallowAll(); f.SetAllow(ThingCategoryDefOf.ResourcesRaw, true);
                    break;
                case "waste":
                    // Biotech: wastepacks only (the zone sits away from home, like a dump)
                    f.SetDisallowAll(); if (ThingDefOf.Wastepack != null) f.SetAllow(ThingDefOf.Wastepack, true);
                    break;
            }
        }

        public HandResult Zone(ZoneArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var home = HomeCenter(map);
                int w = a.W, h = a.H;
                if (w <= 0 || h <= 0) BuildPure.ZoneSize(a.Kind, a.Size, out w, out h);
                w = Math.Min(w, BuildPure.MaxZoneSide); h = Math.Min(h, BuildPure.MaxZoneSide);
                bool exact = a.At.X != null && a.At.Z != null;
                IEnumerable<int[]> corners = exact ? new[] { new[] { a.At.X.Value, a.At.Z.Value } } : BuildPure.SiteCandidates(home.x, home.z, w, h, 30);
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);

                if (a.Kind == "grow")
                {
                    string cropErr;
                    var plant = ResolveCropDef(a.Plant, false, out cropErr);   // alias, label or defName; refuses with the research or soil reason
                    if (plant == null) return HandResult.No(cropErr);
                    if (w * h > left.Cells) return HandResult.No("that field is " + (w * h) + " cells but only " + left.Cells + " are left this cycle");
                    Func<IntVec3, bool> ok = c =>
                    {
                        if (!ZoneCellFree(map, c, false) || c.Roofed(map)) return false;
                        var tr = c.GetPlant(map);
                        if (tr != null && tr.def.plant.IsTree) return false;
                        return c.GetTerrain(map).fertility >= 0.7f;
                    };
                    CellBox box;
                    if (exact)
                    {
                        box = new CellBox(a.At.X.Value, a.At.Z.Value, w, h);
                        if (!BuildHelpers.InMap(box.X, box.Z, w, h, map.Size.x, map.Size.z)) return HandResult.No("that field would leave the map");
                        foreach (var c in BuildPure.BoxCells(box)) if (!ok(new IntVec3(c[0], 0, c[1]))) return HandResult.No("(" + c[0] + "," + c[1] + ") is not open, fertile, unroofed ground");
                    }
                    else
                    {
                        box = FindBox(map, corners, w, h, ok, b => BuildHelpers.SoilScore(BuildPure.BoxCells(b).Select(c => (double)map.fertilityGrid.FertilityAt(new IntVec3(c[0], 0, c[1]))), 0.7));
                        if (box == null) return HandResult.No("no open, fertile, unroofed " + w + "x" + h + " patch of soil near the base");
                    }
                    var anyCell = new IntVec3(box.X, 0, box.Z);
                    if (!PlantUtility.GrowthSeasonNow(anyCell, map, plant)) return HandResult.No((plant.label ?? plant.defName) + " can't grow there right now (wrong season or temperature)");
                    var z = new Zone_Growing(map.zoneManager);
                    map.zoneManager.RegisterZone(z);
                    z.SetPlantDefToGrow(plant);
                    foreach (var c in BuildPure.BoxCells(box)) z.AddCell(new IntVec3(c[0], 0, c[1]));
                    mb.Marks.Add(new NarratorMark { Kind = "zone", Map = map.uniqueID, Id = z.ID, Def = plant.defName });
                    foreach (var c in BuildPure.BoxCells(box)) mb.Marks[0].AddCell(c[0], c[1]);
                    return HandResult.Placed("made a " + w + "x" + h + " " + (plant.label ?? plant.defName) + " field at (" + box.X + "," + box.Z + ")", mb.Marks);
                }

                // stockpile and dump
                bool dump = a.Kind == "dump";
                bool waste = !dump && string.Equals(a.Preset, "waste", StringComparison.OrdinalIgnoreCase);
                if (waste && (!ModsConfig.BiotechActive || ThingDefOf.Wastepack == null)) return HandResult.No("wastepacks need Biotech");
                var preset = dump ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile;
                var cells = new List<IntVec3>();
                string where;
                if (!dump && !waste && a.At.Room != 0)
                {
                    Room room = null;
                    foreach (var r in map.regionGrid.AllRooms) if (r.ID == a.At.Room) { room = r; break; }
                    if (room == null) return HandResult.No("no room " + a.At.Room + " on the map");
                    foreach (var c in room.Cells) if (ZoneCellFree(map, c, true) && c.GetDoor(map) == null) cells.Add(c);
                    int cap = BuildHelpers.StockpileCap(left.Cells);
                    if (cells.Count == 0) return HandResult.No("that room has no free floor for a stockpile");
                    if (cells.Count > cap) cells = BuildHelpers.NearestFirst(cells.Select(c => new[] { c.x, c.z }), home.x, home.z, cap).Select(p => new IntVec3(p[0], 0, p[1])).ToList();
                    where = "in room " + a.At.Room;
                }
                else
                {
                    if (w * h > left.Cells) return HandResult.No("that zone is " + (w * h) + " cells but only " + left.Cells + " are left this cycle");
                    Func<IntVec3, bool> ok = c => ZoneCellFree(map, c, true);
                    CellBox box;
                    if (exact)
                    {
                        box = new CellBox(a.At.X.Value, a.At.Z.Value, w, h);
                        if (!BuildHelpers.InMap(box.X, box.Z, w, h, map.Size.x, map.Size.z)) return HandResult.No("that zone would leave the map");
                        foreach (var c in BuildPure.BoxCells(box)) if (!ok(new IntVec3(c[0], 0, c[1]))) return HandResult.No("(" + c[0] + "," + c[1] + ") is not free open ground");
                    }
                    else if (dump || waste)
                    {
                        box = FindBox(map, BuildHelpers.DumpCandidates(home.x, home.z, w, h), w, h, ok, null);
                        if (box == null) return HandResult.No("no free " + w + "x" + h + " place 12 to 30 cells from the base for a " + (waste ? "waste stockpile" : "dump"));
                    }
                    else
                    {
                        // roofed preferred: score 1 for an all-roofed box, 0 otherwise
                        box = FindBox(map, corners, w, h, ok, b => BuildPure.BoxCells(b).All(c => new IntVec3(c[0], 0, c[1]).Roofed(map)) ? 1.0 : 0.0);
                        if (box == null) return HandResult.No("no free " + w + "x" + h + " place near the base for a stockpile");
                    }
                    foreach (var c in BuildPure.BoxCells(box)) cells.Add(new IntVec3(c[0], 0, c[1]));
                    where = "at (" + box.X + "," + box.Z + ")";
                }
                var sz = new Zone_Stockpile(preset, map.zoneManager);
                map.zoneManager.RegisterZone(sz);
                if (!dump) SetPreset(sz, a.Preset);
                var pr = PriorityOf(a.Priority);
                if (pr.HasValue) sz.settings.Priority = pr.Value;
                foreach (var c in cells) sz.AddCell(c);
                var mark = new NarratorMark { Kind = "zone", Map = map.uniqueID, Id = sz.ID, Def = dump ? "dump" : (a.Preset ?? "all") };
                foreach (var c in cells) mark.AddCell(c.x, c.z);
                return HandResult.Placed("made a " + (dump ? "dumping" : (a.Preset ?? "all")) + " stockpile of " + cells.Count + " cells " + where, new List<NarratorMark> { mark });
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---------------------------------------------------------------- area
        public HandResult Area(AreaArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                if (a.Box == null) return HandResult.No("an area needs a box");
                bool add = a.Op == "add";
                var cells = BuildPure.BoxCells(a.Box).Where(c => c[0] >= 0 && c[1] >= 0 && c[0] < map.Size.x && c[1] < map.Size.z).ToList();
                if (cells.Count == 0) return HandResult.No("that box is outside the map");
                Area area; string label;
                if (a.Kind == "home") { area = map.areaManager.Home; label = "Home"; }
                else
                {
                    if (string.IsNullOrWhiteSpace(a.Name)) return HandResult.No("an allowed area needs a name");
                    label = a.Name.Trim();
                    area = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => Eq(x.Label, label));
                    if (area == null)
                    {
                        if (!add) return HandResult.No("no allowed area named '" + label + "'");
                        Area_Allowed made;
                        if (!map.areaManager.TryMakeNewAllowed(out made)) return HandResult.No("there is no room for another allowed area");
                        made.SetLabel(label);
                        area = made;
                    }
                }
                var changed = cells.Where(c => area[new IntVec3(c[0], 0, c[1])] != add).ToList();
                if (add && changed.Count > left.Cells) return HandResult.No("that would change " + changed.Count + " cells but only " + left.Cells + " are left this cycle");
                foreach (var c in changed) area[new IntVec3(c[0], 0, c[1])] = add;
                var notes = new List<string>();
                notes.Add((add ? "added " : "removed ") + changed.Count + " cells " + (add ? "to " : "from ") + "the " + label + " area");
                if (a.Kind == "allowed" && a.Assign != null && a.Assign.Count > 0 && area is Area_Allowed)
                {
                    var names = new List<string>();
                    foreach (int id in a.Assign)
                    {
                        HandResult no;
                        var p = Mine(id, out no);
                        if (p == null || p.playerSettings == null) { notes.Add(no == null ? "pawn " + id + " takes no area" : no.Reason); continue; }
                        p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                        names.Add(Name(p));
                    }
                    if (names.Count > 0) notes.Add("restricted " + string.Join(", ", names) + " to it");
                }
                List<NarratorMark> marks = null;
                if (add && changed.Count > 0)
                {
                    var m = new NarratorMark { Kind = "area_cells", Map = map.uniqueID, Def = label };
                    foreach (var c in changed) m.AddCell(c[0], c[1]);
                    marks = new List<NarratorMark> { m };
                }
                else if (!add) notes.Add("(removing cells cannot be undone)");
                return marks != null ? HandResult.Placed(string.Join("; ", notes), marks) : HandResult.Done(string.Join("; ", notes));
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---------------------------------------------------------------- bills
        static Building FindBench(int id, out Map map)
        {
            foreach (var m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                foreach (var b in m.listerBuildings.allBuildingsColonist)
                    if (b.thingIDNumber == id) { map = m; return b; }
            }
            map = null; return null;
        }

        static string ModeText(Bill b)
        {
            var bp = b as Bill_Production;
            if (bp == null) return "forever";
            if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount) return BuildHelpers.BillMode("count", bp.repeatCount, 0);
            if (bp.repeatMode == BillRepeatModeDefOf.TargetCount) return BuildHelpers.BillMode("until", 0, bp.targetCount);
            return "forever";
        }

        public HandResult Bill(BillArgs a, BuildBudget left)
        {
            try
            {
                Map map;
                var bench = FindBench(a.Bench, out map);
                if (bench == null) return HandResult.No("no colony workbench " + a.Bench);
                var giver = bench as IBillGiver;
                if (giver == null || giver.BillStack == null) return HandResult.No(bench.LabelCap + " takes no bills");
                var stack = giver.BillStack;
                if (a.Op == "add")
                {
                    if (left.Bills < 1) return HandResult.No("no bills left this cycle");
                    var rs = bench.def.AllRecipes.Where(r => r.AvailableNow).ToList();
                    int i = BuildHelpers.FindByNameOrLabel(rs.Select(r => new KeyValuePair<string, string>(r.defName, r.label)).ToList(), a.Recipe);
                    if (i < 0) return HandResult.No(BuildHelpers.Unknown("recipe", a.Recipe, rs.Select(r => r.label)) + " at " + bench.LabelCap);
                    var recipe = rs[i];
                    var bill = recipe.MakeNewBill();
                    var bp = bill as Bill_Production;
                    string mode = "forever";
                    if (bp != null)
                    {
                        if (a.Count > 0) { bp.repeatMode = BillRepeatModeDefOf.RepeatCount; bp.repeatCount = a.Count; mode = "x" + a.Count; }
                        else if (a.Until > 0) { bp.repeatMode = BillRepeatModeDefOf.TargetCount; bp.targetCount = a.Until; mode = "until " + a.Until; }
                        else bp.repeatMode = BillRepeatModeDefOf.Forever;
                    }
                    if (a.Radius > 0) bill.ingredientSearchRadius = a.Radius;
                    stack.AddBill(bill);
                    var m = new NarratorMark { Kind = "bill", Map = map.uniqueID, Id = bench.thingIDNumber, Sub = BuildHelpers.LoadIdOf(bill.GetUniqueLoadID()), Def = recipe.defName };
                    return HandResult.Placed("added a bill at " + bench.LabelCap + ": " + recipe.label + " (" + mode + ")", new List<NarratorMark> { m });
                }
                Bill target = null;
                if (a.Index > 0)
                {
                    if (a.Index > stack.Count) return HandResult.No(bench.LabelCap + " has only " + stack.Count + " bills");
                    target = stack.Bills[a.Index - 1];
                }
                else
                {
                    target = stack.Bills.FirstOrDefault(b => Eq(b.recipe.defName, a.Recipe) || Eq(b.recipe.label, a.Recipe));
                    if (target == null) return HandResult.No("no bill for '" + a.Recipe + "' at " + bench.LabelCap);
                }
                string rl = target.recipe.label;
                switch (a.Op)
                {
                    case "remove": stack.Delete(target); return HandResult.Done("removed the bill " + rl + " from " + bench.LabelCap);
                    case "suspend": target.suspended = true; return HandResult.Done("suspended the bill " + rl + " at " + bench.LabelCap);
                    case "resume": target.suspended = false; return HandResult.Done("resumed the bill " + rl + " at " + bench.LabelCap);
                    default: return HandResult.No("unknown bill op '" + a.Op + "'");
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        public JObject Benches()
        {
            var arr = new JArray();
            var map = HomeMap();
            if (map != null)
            {
                foreach (var b in map.listerBuildings.allBuildingsColonist)
                {
                    if (arr.Count >= 30) break;
                    var giver = b as IBillGiver;
                    if (giver == null || giver.BillStack == null) continue;
                    string room = "";
                    try { room = b.GetRoom()?.Role?.label ?? ""; } catch (Exception) { }
                    var bills = new JArray();
                    int idx = 0;
                    foreach (var bill in giver.BillStack.Bills)
                    {
                        idx++;
                        string mode = ModeText(bill);
                        bills.Add(new JObject { ["index"] = idx, ["recipe"] = bill.recipe.label, ["mode"] = mode, ["suspended"] = bill.suspended });
                    }
                    var recipes = new JArray();
                    foreach (var r in b.def.AllRecipes) { if (recipes.Count >= 30) break; if (r.AvailableNow) recipes.Add(r.label); }
                    arr.Add(new JObject { ["id"] = b.thingIDNumber, ["label"] = b.LabelCap.ToString(), ["room"] = room, ["bills"] = bills, ["recipes"] = recipes });
                }
            }
            return new JObject { ["benches"] = arr };
        }

        // ---------------------------------------------------------------- designate
        static bool Designated(Map map, Thing t, DesignationDef d) { return map.designationManager.DesignationOn(t, d) != null; }
        static bool Designated(Map map, IntVec3 c, DesignationDef d) { return map.designationManager.DesignationAt(c, d) != null; }

        static bool OreMatches(Building m, string ore)
        {
            if (string.IsNullOrWhiteSpace(ore)) return true;
            string o = ore.Trim().ToLowerInvariant();
            var mt = m.def.building == null ? null : m.def.building.mineableThing;
            if (mt != null && (mt.defName.ToLowerInvariant().Contains(o) || (mt.label ?? "").ToLowerInvariant().Contains(o))) return true;
            string n = m.def.defName.ToLowerInvariant(), l = (m.def.label ?? "").ToLowerInvariant();
            return n.Contains(o) || l.Contains(o);
        }

        public HandResult Designate(DesignateArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var home = HomeCenter(map);
                var area = WhereList(a.At, map);
                Thing target = a.Target != 0 ? ThingById(map, a.Target) : null;
                if (a.Target != 0 && target == null) return HandResult.No("no thing " + a.Target + " on the colony map");
                int cap = Math.Min(Math.Max(1, a.Max), left.Designations);
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                var dm = map.designationManager;
                int n = 0; string note;

                // things in the place (box or circle), by cell
                Func<IEnumerable<Thing>> inPlace = () =>
                {
                    var list = new List<Thing>();
                    if (area == null) return list;
                    foreach (var p in area)
                        foreach (var t in new IntVec3(p[0], 0, p[1]).GetThingList(map)) if (!list.Contains(t)) list.Add(t);
                    return list;
                };

                switch (a.Kind)
                {
                    case "mine":
                    {
                        if (cap <= 0) return HandResult.No("no designations left this cycle");
                        var cands = new List<int[]>();
                        if (area != null)
                        {
                            foreach (var p in area)
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                var m = c.GetFirstMineable(map);
                                if (m != null && !c.Fogged(map) && OreMatches(m, a.Ore)) cands.Add(p);
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(a.Ore)) return HandResult.No("mine needs a place or an ore to look for");
                            foreach (var c in map.AllCells)
                            {
                                var e = map.edificeGrid[c];
                                if (e == null || !e.def.mineable) continue;
                                if (c.Fogged(map) || !OreMatches(e, a.Ore)) continue;
                                cands.Add(new[] { c.x, c.z });
                            }
                        }
                        foreach (var p in BuildHelpers.NearestFirst(cands, home.x, home.z, cands.Count))
                        {
                            if (n >= cap) break;
                            var c = new IntVec3(p[0], 0, p[1]);
                            if (Designated(map, c, DesignationDefOf.Mine)) continue;
                            dm.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                            mb.Designation(DesignationDefOf.Mine.defName, 0, p[0], p[1]);
                            n++;
                        }
                        if (n == 0) return HandResult.No("nothing to mine" + (string.IsNullOrWhiteSpace(a.Ore) ? "" : " matching '" + a.Ore + "'") + " that is visible and not already marked");
                        note = "marked " + BuildHelpers.Plural(n, "cell", "cells") + " to mine" + (string.IsNullOrWhiteSpace(a.Ore) ? "" : " (" + a.Ore + ")");
                        break;
                    }
                    case "chop":
                    {
                        if (cap <= 0) return HandResult.No("no designations left this cycle");
                        var trees = new List<Plant>();
                        if (target != null) { var pl = target as Plant; if (pl == null) return HandResult.No("thing " + a.Target + " is not a plant"); trees.Add(pl); }
                        else if (area != null) trees.AddRange(inPlace().OfType<Plant>().Where(x => x.def.plant.IsTree));
                        else
                        {
                            var seen = new HashSet<Thing>();
                            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
                            {
                                if (t.Faction != Faction.OfPlayer) continue;
                                foreach (var c in t.OccupiedRect().Cells) { var pl = c.GetPlant(map); if (pl != null && pl.def.plant.IsTree && seen.Add(pl)) trees.Add(pl); }
                            }
                            foreach (var z in map.zoneManager.AllZones.OfType<Zone_Growing>())
                                foreach (var c in z.Cells) { var pl = c.GetPlant(map); if (pl != null && pl.def.plant.IsTree && seen.Add(pl)) trees.Add(pl); }
                        }
                        // CutPlant on a tree yields its wood, as the Chop wood designator's work does
                        foreach (var pl in trees.OrderBy(x => BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z)))
                        {
                            if (n >= cap) break;
                            if (Designated(map, pl, DesignationDefOf.CutPlant) || Designated(map, pl, DesignationDefOf.HarvestPlant)) continue;
                            dm.AddDesignation(new Designation(pl, DesignationDefOf.CutPlant));
                            mb.Designation(DesignationDefOf.CutPlant.defName, pl.thingIDNumber, 0, 0);
                            n++;
                        }
                        if (n == 0) return HandResult.No("no trees to chop there (or all already marked)");
                        note = "marked " + BuildHelpers.Plural(n, "tree", "trees") + " to chop";
                        break;
                    }
                    case "harvest":
                    {
                        if (cap <= 0) return HandResult.No("no designations left this cycle");
                        IEnumerable<Plant> plants;
                        if (target != null) { var pl = target as Plant; if (pl == null) return HandResult.No("thing " + a.Target + " is not a plant"); plants = new[] { pl }; }
                        else if (area != null) plants = inPlace().OfType<Plant>();
                        else plants = map.listerThings.ThingsInGroup(ThingRequestGroup.HarvestablePlant).OfType<Plant>()
                            .Where(x => !(map.zoneManager.ZoneAt(x.Position) is Zone_Growing) && BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z) <= 30 * 30);
                        foreach (var pl in plants.Where(x => !x.def.plant.IsTree && x.HarvestableNow && !x.Position.Fogged(map))
                                     .OrderBy(x => BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z)))
                        {
                            if (n >= cap) break;
                            if (Designated(map, pl, DesignationDefOf.HarvestPlant)) continue;
                            dm.AddDesignation(new Designation(pl, DesignationDefOf.HarvestPlant));
                            mb.Designation(DesignationDefOf.HarvestPlant.defName, pl.thingIDNumber, 0, 0);
                            n++;
                        }
                        if (n == 0) return HandResult.No("no ripe plants to harvest" + (area == null && target == null ? " in the wild near the base" : " there"));
                        note = "marked " + BuildHelpers.Plural(n, "ripe plant", "ripe plants") + " to harvest";
                        break;
                    }
                    case "haul":
                    {
                        if (cap <= 0) return HandResult.No("no designations left this cycle");
                        IEnumerable<Thing> items;
                        if (target != null) items = new[] { target };
                        else if (area != null) items = inPlace();
                        else items = map.listerHaulables.ThingsPotentiallyNeedingHauling().Where(x => BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z) <= 20 * 20);
                        foreach (var t in items.Where(x => x.def.EverHaulable && x.def.designateHaulable && !x.IsInAnyStorage() && !x.IsForbidden(Faction.OfPlayer) && !x.Fogged())
                                     .OrderBy(x => BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z)))
                        {
                            if (n >= cap) break;
                            if (Designated(map, t, DesignationDefOf.Haul)) continue;
                            dm.AddDesignation(new Designation(t, DesignationDefOf.Haul));
                            mb.Designation(DesignationDefOf.Haul.defName, t.thingIDNumber, 0, 0);
                            n++;
                        }
                        if (n == 0) return HandResult.No("nothing loose to haul" + (area == null && target == null ? " within 20 cells of the base" : " there"));
                        note = "marked " + BuildHelpers.Plural(n, "item", "items") + " to haul";
                        break;
                    }
                    case "deconstruct":
                    {
                        if (cap <= 0) return HandResult.No("no designations left this cycle");
                        if (target == null && area == null) return HandResult.No("deconstruct needs a place or a thing id");
                        IEnumerable<Thing> things = target != null ? new[] { target } : inPlace();
                        var skipped = new Dictionary<string, int>();
                        Action<string> skip = r => { int v; skipped.TryGetValue(r, out v); skipped[r] = v + 1; };
                        foreach (var t in things.OfType<Building>())
                        {
                            if (n >= cap) break;
                            if (t is Building_Bed) { skip("beds"); continue; }
                            if (t is Building_Grave) { skip("graves"); continue; }
                            if (t.Faction == Faction.OfPlayer && !a.PlayerAsked) { skip("player-made buildings"); continue; }
                            if (!t.DeconstructibleBy(Faction.OfPlayer)) { skip("not deconstructible"); continue; }
                            if (Designated(map, t, DesignationDefOf.Deconstruct)) continue;
                            dm.AddDesignation(new Designation(t, DesignationDefOf.Deconstruct));
                            mb.Designation(DesignationDefOf.Deconstruct.defName, t.thingIDNumber, 0, 0);
                            n++;
                        }
                        string sk = BuildHelpers.SkipNote(skipped);
                        if (n == 0) return HandResult.No("nothing was marked" + (sk.Length > 0 ? "; " + sk : " (no buildings there)"));
                        note = "marked " + BuildHelpers.Plural(n, "building", "buildings") + " to deconstruct" + (sk.Length > 0 ? "; " + sk : "");
                        break;
                    }
                    case "cancel":
                    {
                        if (target == null && area == null) return HandResult.No("cancel needs a place or a thing id");
                        int des = 0, bps = 0;
                        var seenT = new HashSet<Thing>();
                        if (target != null)
                        {
                            foreach (var d in dm.AllDesignationsOn(target).ToList()) { dm.RemoveDesignation(d); des++; }
                            if ((target is Blueprint || target is Frame) && target.Faction == Faction.OfPlayer) { target.Destroy(DestroyMode.Cancel); bps++; }
                        }
                        else
                        {
                            foreach (var p in area)
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                foreach (var d in dm.AllDesignationsAt(c).ToList()) { dm.RemoveDesignation(d); des++; }
                                foreach (var t in c.GetThingList(map).ToList())
                                {
                                    if (!seenT.Add(t)) continue;
                                    foreach (var d in dm.AllDesignationsOn(t).ToList()) { dm.RemoveDesignation(d); des++; }
                                    // blueprints and frames are only cancelled when the box (or a thing id) named them
                                    if (a.At.Box != null && (t is Blueprint || t is Frame) && t.Faction == Faction.OfPlayer && !t.Destroyed) { t.Destroy(DestroyMode.Cancel); bps++; }
                                }
                            }
                        }
                        if (des == 0 && bps == 0) return HandResult.No("nothing to cancel there");
                        var parts = new List<string>();
                        if (des > 0) parts.Add("removed " + BuildHelpers.Plural(des, "designation", "designations"));
                        if (bps > 0) parts.Add("cancelled " + BuildHelpers.Plural(bps, "blueprint or frame", "blueprints and frames"));
                        return HandResult.Done(string.Join(", ", parts) + " (a cancel itself cannot be undone)");
                    }
                    default: return HandResult.No("unknown designate kind '" + a.Kind + "'");
                }
                return HandResult.Placed(note, mb.Marks);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---------------------------------------------------------------- undo
        public HandResult Undo(IList<NarratorMark> marks)
        {
            try
            {
                int cancelled = 0, desigs = 0, zones = 0, zoneCells = 0, areaCells = 0, bills = 0, built = 0, gone = 0, restored = 0;
                foreach (var mk in marks.Reverse())
                {
                    var map = Find.Maps.FirstOrDefault(m => m.uniqueID == mk.Map);
                    if (map == null) continue;
                    switch (mk.Kind)
                    {
                        case "blueprint":
                            foreach (var p in BuildHelpers.CellPairs(mk))
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                if (!c.InBounds(map)) { gone++; continue; }
                                bool did = false, finished = false;
                                foreach (var t in c.GetThingList(map).ToList())
                                {
                                    if (t.Faction != Faction.OfPlayer) continue;
                                    var bd = BuiltDef(t);
                                    if (bd == null || bd.defName != mk.Def) continue;
                                    if (t is Blueprint || t is Frame) { t.Destroy(DestroyMode.Cancel); did = true; }
                                    else if (t is Building) finished = true;
                                }
                                if (did) cancelled++; else if (finished) built++; else gone++;
                            }
                            break;
                        case "floor":
                            foreach (var p in BuildHelpers.CellPairs(mk))
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                if (!c.InBounds(map)) { gone++; continue; }
                                bool did = false;
                                foreach (var t in c.GetThingList(map).ToList())
                                {
                                    if (t.Faction != Faction.OfPlayer || !(t is Blueprint || t is Frame)) continue;
                                    if (t.def.entityDefToBuild == null || t.def.entityDefToBuild.defName != mk.Def) continue;
                                    t.Destroy(DestroyMode.Cancel); did = true;
                                }
                                var laid = map.terrainGrid.TerrainAt(c);
                                if (did) cancelled++; else if (laid != null && laid.defName == mk.Def) built++; else gone++;
                            }
                            break;
                        case "designation":
                        {
                            var dd = DefDatabase<DesignationDef>.GetNamedSilentFail(mk.Def);
                            if (dd == null) break;
                            var dm = map.designationManager;
                            foreach (int id in mk.Things)
                            {
                                var t = ThingById(map, id);
                                if (t != null && dm.DesignationOn(t, dd) != null) { dm.TryRemoveDesignationOn(t, dd); desigs++; }
                            }
                            foreach (var p in BuildHelpers.CellPairs(mk))
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                if (c.InBounds(map) && dm.DesignationAt(c, dd) != null) { dm.TryRemoveDesignation(c, dd); desigs++; }
                            }
                            break;
                        }
                        case "zone":
                        {
                            var z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == mk.Id);
                            if (z != null) { z.Delete(); zones++; } else gone++;
                            break;
                        }
                        case "zone_cells":
                        {
                            var z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == mk.Id);
                            if (z == null) { gone++; break; }
                            foreach (var p in BuildHelpers.CellPairs(mk))
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                if (z.ContainsCell(c)) { z.RemoveCell(c); zoneCells++; }
                            }
                            break;
                        }
                        case "area_cells":
                        {
                            Area ar = Eq(mk.Def, "Home") ? map.areaManager.Home : (Area)map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => Eq(x.Label, mk.Def));
                            if (ar == null) { gone++; break; }
                            foreach (var p in BuildHelpers.CellPairs(mk))
                            {
                                var c = new IntVec3(p[0], 0, p[1]);
                                if (c.InBounds(map) && ar[c]) { ar[c] = false; areaCells++; }
                            }
                            break;
                        }
                        case "zone_settings": if (UndoZoneSettings(mk, map)) restored++; else gone++; break;
                        case "bill":
                        {
                            Map bm;
                            var bench = FindBench(mk.Id, out bm);
                            var giver = bench as IBillGiver;
                            var bill = giver == null ? null : giver.BillStack.Bills.FirstOrDefault(x => BuildHelpers.LoadIdOf(x.GetUniqueLoadID()) == mk.Sub);
                            if (bill != null) { giver.BillStack.Delete(bill); bills++; } else gone++;
                            break;
                        }
                    }
                }
                string undone = BuildHelpers.UndoNote(cancelled, desigs, zones, zoneCells, areaCells, bills, built, gone);
                if (restored > 0) undone = (undone == "nothing left to undo" ? "" : undone + ", ") + ZonesPure.RestoredNote(restored);
                return HandResult.Done(undone);
            }
            catch (Exception e) { return Failed(e); }
        }
    }
}

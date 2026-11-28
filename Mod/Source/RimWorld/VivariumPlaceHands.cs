using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Game bodies of the "place" tool: furniture in a named room, generators and batteries with their conduits,
    /// switches, wiring a room, heaters and coolers with a target temperature, and ship parts. Game thread only.
    /// Placement goes through VivariumHands.Build (planner, interaction cells, never-seal-off guard, marks, caps); the decisions
    /// that need no game live in PlacePending / ShipPlan / ConduitRoute (Verse-free, tested).</summary>
    public sealed class VivariumPlaceHands : IPlaceHands
    {
        static readonly string[] LivingWords = { "bedroom", "barracks", "hospital", "dining", "rec" };

        public HandResult Place(PlaceArgs a, BuildBudget left)
        {
            try
            {
                var map = VivariumHands.HomeMap();
                if (map == null) return HandResult.No("no colony map");
                if (a.Op != "target" && left.Blueprints < 1) return HandResult.No("no blueprints left this cycle");
                switch (a.Op)
                {
                    case "furnish": return Furnish(map, a, left);
                    case "generator": return Generator(map, a, left);
                    case "battery": return Battery(map, a, left);
                    case "switch": return Switch(map, a, left);
                    case "connect": return Connect(map, a, left);
                    case "heater":
                    case "cooler": return Climate(map, a, left);
                    case "target": return Target(map, a);
                    case "ship": return Ship(map, a, left);
                    default: return HandResult.No("unknown place op " + a.Op + "; ops: " + string.Join(", ", PlacePure.Ops));
                }
            }
            catch (Exception e) { return VivariumHands.Failed(e); }
        }

        // ---------------------------------------------------------------- rooms and anchors
        sealed class RoomRow { public Room Room; public string Label, Role, Word; }

        /// <summary>The digest's rooms (same labels: "Ana's bedroom" or the role), nearest to home first.</summary>
        static List<RoomRow> Rooms(Map map)
        {
            var home = VivariumHands.HomeCenter(map);
            var rows = new List<RoomRow>();
            var seen = new HashSet<Room>();
            foreach (var r in map.regionGrid.AllRooms)
            {
                if (r == null || !seen.Add(r) || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None || r.CellCount < 4) continue;
                string role = (r.Role.label ?? r.Role.defName).ToLowerInvariant();
                var owners = new HashSet<string>();
                foreach (var bed in r.ContainedBeds)
                    foreach (var p in bed.OwnersForReading) if (p != null && p.Name != null) owners.Add(p.Name.ToStringShort);
                string label = owners.Count == 1 && r.Role == RoomRoleDefOf.Bedroom ? owners.First() + "'s bedroom" : role;
                rows.Add(new RoomRow { Room = r, Label = label, Role = role, Word = VivariumBaseRead.RoleWord(r) });
            }
            return rows.OrderBy(w => Dist2(w.Room.Cells.FirstOrDefault(), home)).ToList();
        }

        static int Dist2(IntVec3 a, IntVec3 b) { return BuildHelpers.Dist2(a.x, a.z, b.x, b.z); }

        static string LabelOf(Map map, Room room)
        {
            foreach (var w in Rooms(map)) if (w.Room == room) return w.Label;
            return room.Role == null ? "room" : (room.Role.label ?? room.Role.defName).ToLowerInvariant();
        }

        sealed class At { public Room Room; public Thing Thing; public IntVec3 Center; public string Label; public bool Given; }

        /// <summary>The place a call names: a room (label, role, "room N"), a building ("#id") or nothing (the base). no is set on refusal.</summary>
        static At ResolveAt(Map map, string at, out HandResult no)
        {
            no = null;
            var res = new At { Center = VivariumHands.HomeCenter(map), Label = "the base" };
            if (string.IsNullOrWhiteSpace(at)) return res;
            res.Given = true;
            var p = PlaceWords.ParseAt(at);
            var rows = Rooms(map);
            if (p.Item1 == "id")
            {
                var t = VivariumHands.ThingById(map, int.Parse(p.Item2));
                if (t == null || !t.Spawned) { no = HandResult.No("no building #" + p.Item2 + " on the map; read the base to get an id"); return null; }
                res.Thing = t; res.Center = t.Position; res.Label = (VivariumHands.BuiltDef(t) ?? t.def).label;
                res.Room = t.Position.GetRoom(map);
                if (res.Room != null && (res.Room.PsychologicallyOutdoors || res.Room.IsDoorway)) res.Room = null;
                return res;
            }
            if (p.Item1 == "room")
            {
                int id = int.Parse(p.Item2);
                Room room = map.regionGrid.AllRooms.FirstOrDefault(r => r.ID == id);
                if (room == null) { no = HandResult.No(PlaceWords.NoRoomMessage(at, rows.Select(w => "room " + w.Room.ID + " " + w.Label))); return null; }
                res.Room = room; res.Label = LabelOf(map, room); res.Center = Center(room);
                return res;
            }
            int i = PlacePending.MatchRoom(rows.Select(w => w.Label).ToList(), rows.Select(w => w.Word).ToList(), p.Item2);
            if (i < 0) i = PlacePending.MatchRoom(rows.Select(w => w.Label).ToList(), rows.Select(w => w.Role).ToList(), p.Item2);
            if (i < 0) { no = HandResult.No(PlaceWords.NoRoomMessage(at, rows.Select(w => w.Label).Distinct())); return null; }
            res.Room = rows[i].Room; res.Label = rows[i].Label; res.Center = Center(rows[i].Room);
            return res;
        }

        static IntVec3 Center(Room room)
        {
            var cells = room.Cells.ToList();
            if (cells.Count == 0) return IntVec3.Invalid;
            double cx = cells.Average(c => c.x), cz = cells.Average(c => c.z);
            return cells.OrderBy(c => (c.x - cx) * (c.x - cx) + (c.z - cz) * (c.z - cz)).First();
        }

        /// <summary>Is a thing (or blueprint/frame) of a def climate-wise in a room? Wall-mounted ones count by their cold side.</summary>
        static bool InClimate(Map map, Thing t, ThingDef def, Room room)
        {
            if (VivariumHands.WallMounted(def))
            {
                var cold = t.Position + IntVec3.South.RotatedBy(t.Rotation);
                return cold.InBounds(map) && cold.GetRoom(map) == room;
            }
            return t.Position.GetRoom(map) == room;
        }

        /// <summary>Things standing or planned in a room, including those in its walls.</summary>
        static List<Thing> ThingsAround(Map map, Room room)
        {
            var set = new HashSet<Thing>();
            foreach (var c in room.Cells)
            {
                foreach (var t in c.GetThingList(map)) set.Add(t);
                foreach (var d in GenAdj.CardinalDirections)
                {
                    var n = c + d;
                    if (n.InBounds(map)) foreach (var t in n.GetThingList(map)) set.Add(t);
                }
            }
            return set.ToList();
        }

        static IEnumerable<IntVec3> Ring(Map map, CellRect r)
        {
            for (int x = r.minX; x <= r.maxX; x++)
            {
                var a = new IntVec3(x, 0, r.minZ - 1); if (a.InBounds(map)) yield return a;
                var b = new IntVec3(x, 0, r.maxZ + 1); if (b.InBounds(map)) yield return b;
            }
            for (int z = r.minZ; z <= r.maxZ; z++)
            {
                var a = new IntVec3(r.minX - 1, 0, z); if (a.InBounds(map)) yield return a;
                var b = new IntVec3(r.maxX + 1, 0, z); if (b.InBounds(map)) yield return b;
            }
        }

        static IEnumerable<IntVec3> Cells(CellRect r) { foreach (var c in r) yield return c; }

        static ThingDef Built(Thing t) { return VivariumHands.BuiltDef(t); }

        static Blueprint BlueprintOf(Map map, List<NarratorMark> marks, ThingDef def)
        {
            if (marks == null) return null;
            foreach (var m in marks)
            {
                if (m.Cells == null) continue;
                for (int i = 0; i + 1 < m.Cells.Count; i += 2)
                    foreach (var t in new IntVec3(m.Cells[i], 0, m.Cells[i + 1]).GetThingList(map))
                    {
                        var bp = t as Blueprint;
                        if (bp != null && Built(bp) == def) return bp;
                    }
            }
            return null;
        }

        static Blueprint PlaceBp(Map map, ThingDef def, IntVec3 cell, Rot4 rot, BuildHelpers.MarkBuilder mb)
        {
            // a stuff-made def needs a stuff or its frame cannot be paid for: the game's default for it
            var stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            var bp = GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
            if (bp == null) return null;
            mb.Blueprint(def.defName, cell.x, cell.z, bp.thingIDNumber);
            return bp;
        }

        // ---------------------------------------------------------------- power
        static bool NetHasProduction(PowerNet net) { return net != null && (net.hasPowerSource || net.batteryComps.Count > 0); }

        static bool Consumes(ThingDef def)
        {
            var pc = def == null ? null : def.GetCompProperties<CompProperties_Power>();
            return pc != null && pc.PowerConsumption > 0f;
        }

        static bool Produces(ThingDef def)
        {
            if (def == null) return false;
            var pc = def.GetCompProperties<CompProperties_Power>();
            return pc != null && (pc.PowerConsumption < 0f || def.GetCompProperties<CompProperties_Battery>() != null);
        }

        /// <summary>Cells of every transmitter on the map's nets (conduits, generators, batteries, switches). poweredOnly: only nets
        /// that hold a generator or battery, plus planned generators and batteries (blueprints and frames).</summary>
        static HashSet<long> NetCells(Map map, bool poweredOnly, PowerNet except)
        {
            var set = new HashSet<long>();
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                if (net == except) continue;
                if (poweredOnly && !NetHasProduction(net)) continue;
                foreach (var tr in net.transmitters)
                {
                    if (tr.parent == null || !tr.parent.Spawned) continue;
                    foreach (var c in tr.parent.OccupiedRect()) set.Add(BaseGrid.Key(c.x, c.z));
                }
            }
            foreach (var group in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
                foreach (var t in map.listerThings.ThingsInGroup(group))
                    if (Produces(Built(t)))
                        foreach (var c in t.OccupiedRect()) set.Add(BaseGrid.Key(c.x, c.z));
            return set;
        }

        static bool IsConduitThing(Thing t)
        {
            var d = (t is Blueprint || t is Frame) ? t.def.entityDefToBuild as ThingDef : t.def;
            return d != null && d.building != null && d.building.isPowerConduit;
        }

        /// <summary>Route cost of a conduit through a cell: -1 no, 0 existing or planned (or a goal), 1 free, 6 a doorway.</summary>
        static Func<int, int, int> ConduitCost(Map map, ThingDef conduit, HashSet<long> goals)
        {
            var cache = new Dictionary<long, int>();
            return (x, z) =>
            {
                long k = BaseGrid.Key(x, z);
                if (goals.Contains(k)) return 0;
                int v;
                if (cache.TryGetValue(k, out v)) return v;
                var c = new IntVec3(x, 0, z);
                if (!c.InBounds(map) || c.Fogged(map)) v = -1;
                else if (c.GetThingList(map).Any(IsConduitThing)) v = 0;
                else if (!GenConstruct.CanPlaceBlueprintAt(conduit, c, Rot4.North, map, false, null, null, null).Accepted) v = -1;
                else v = c.GetDoor(map) != null ? 6 : 1;
                cache[k] = v;
                return v;
            };
        }

        sealed class Consumer { public Thing T; public ThingDef Def; public string Label; }

        /// <summary>Plans conduit routes for each consumer to the goals, one after another (each new route may join the earlier ones).
        /// Returns the new cells; reached counts the consumers that got a route (or already touch a goal).</summary>
        static List<int[]> PlanLinks(Map map, ThingDef conduit, List<Consumer> consumers, HashSet<long> goals, out int reached, out List<string> unreached)
        {
            var all = new List<int[]>();
            unreached = new List<string>();
            reached = 0;
            var live = new HashSet<long>(goals);
            var cost = ConduitCost(map, conduit, live);
            foreach (var c in consumers)
            {
                var starts = Ring(map, c.T.OccupiedRect()).Select(p => new[] { p.x, p.z }).ToList();
                var route = ConduitRoute.Find(starts, live, cost, 40);
                if (route == null) { unreached.Add(c.Label); continue; }
                reached++;
                foreach (var cell in route.Cells) { all.Add(cell); live.Add(BaseGrid.Key(cell[0], cell[1])); }
            }
            return all;
        }

        static int PlaceConduits(Map map, ThingDef conduit, List<int[]> cells, BuildHelpers.MarkBuilder mb)
        {
            int n = 0;
            foreach (var p in cells)
                if (PlaceBp(map, conduit, new IntVec3(p[0], 0, p[1]), Rot4.North, mb) != null) n++;
            return n;
        }

        /// <summary>Wires a freshly placed blueprint into a net: conduits from its edge to the nearest net. toAny: join any net
        /// (generators, batteries), else only nets that make power. Returns the note fragment; never refuses (the placement stands).</summary>
        static string AutoLink(Map map, Thing bp, bool toAny, int budget, BuildHelpers.MarkBuilder mb)
        {
            HandResult no;
            var conduit = VivariumHands.ResolveThing("conduit", out no);
            if (conduit == null) return " (no conduits yet: " + no.Reason + ")";
            var goals = NetCells(map, !toAny, null);
            foreach (var c in bp.OccupiedRect()) goals.Remove(BaseGrid.Key(c.x, c.z));
            if (goals.Count == 0) return toAny ? " (no grid to join yet)" : " (no powered grid yet; place a generator first)";
            int reached; List<string> un;
            var cells = PlanLinks(map, conduit, new List<Consumer> { new Consumer { T = bp, Def = Built(bp), Label = Built(bp).label } }, goals, out reached, out un);
            if (reached == 0) return " (no route to a grid within 40 cells; run place connect once a generator is near)";
            if (cells.Count == 0) return " (touching the grid)";
            if (cells.Count > budget) return " (it needs " + PlacePending.Conduits(cells.Count) + " to reach the grid but only " + budget + " blueprints are left; ask place connect next cycle)";
            int n = PlaceConduits(map, conduit, cells, mb);
            return " and " + PlacePending.Conduits(n) + " to the grid";
        }

        static string PowerSummary(Map map)
        {
            double made = 0, used = 0, stored = 0; int bats = 0;
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                foreach (var c in net.powerComps)
                {
                    float o = c.PowerOutput;
                    if (o > 0f) made += o; else used -= o;
                }
                foreach (var b in net.batteryComps) { bats++; stored += b.StoredEnergy; }
            }
            return PowerWords.Summary(made, used, stored * 24.0, bats);   // stored is watt-days; 1 Wd = 24 Wh
        }

        static string WithPower(Map map, string note) { return note + " (" + PowerSummary(map) + ")"; }

        // ---------------------------------------------------------------- spots
        sealed class Spot { public IntVec3 Cell; public Rot4 Rot; public CellRect Rect; }

        /// <summary>Up to want free spots for a def around an anchor, nearest first, not overlapping each other. extra filters footprints.</summary>
        static List<Spot> Spots(Map map, ThingDef def, IntVec3 anchor, int radius, Func<CellRect, bool> extra, int want, out string firstReason)
        {
            string first = null;
            var found = new List<Spot>();
            var cand = BuildHelpers.Circle(anchor.x, anchor.z, radius, map.Size.x, map.Size.z);
            int[] rots = def.rotatable ? new[] { 0, 1, 2, 3 } : new[] { 0 };
            foreach (var p in BuildHelpers.NearestFirst(cand, anchor.x, anchor.z, cand.Count))
            {
                var c = new IntVec3(p[0], 0, p[1]);
                if (c.Fogged(map)) continue;
                foreach (int r in rots)
                {
                    var rot = new Rot4(r);
                    var rect = GenAdj.OccupiedRect(c, rot, def.size);
                    if (found.Any(s => s.Rect.Overlaps(rect))) break;
                    if (extra != null && !extra(rect)) continue;
                    string blocked = VivariumHands.FootprintBlock(map, rect);
                    if (blocked != null) { if (first == null) first = blocked; continue; }
                    var rep = GenConstruct.CanPlaceBlueprintAt(def, c, rot, map, false, null, null, null);
                    if (!rep.Accepted) { if (first == null) first = rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason; continue; }
                    found.Add(new Spot { Cell = c, Rot = rot, Rect = rect });
                    break;
                }
                if (found.Count >= want) break;
            }
            firstReason = first;
            return found;
        }

        static bool Unroofed(Map map, CellRect r) { foreach (var c in r) if (c.Roofed(map)) return false; return true; }

        static bool InLivingRoom(Map map, CellRect r)
        {
            var room = r.CenterCell.GetRoom(map);
            return room != null && room.Role != null && LivingWords.Contains(VivariumBaseRead.RoleWord(room));
        }

        /// <summary>Places a def at a spot (guarded), then wires it. Returns the HandResult.</summary>
        static HandResult PlaceWired(Map map, ThingDef def, Spot s, bool toAny, BuildBudget left, string what, string where)
        {
            string sealOff = VivariumBaseRead.Guard(map, def, new[] { s.Cell }, s.Rot);
            if (sealOff != null) return HandResult.No("can't put a " + def.label + " " + where + ": it " + sealOff);
            var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
            var bp = PlaceBp(map, def, s.Cell, s.Rot, mb);
            if (bp == null) return HandResult.No("the game would not place a " + def.label + " " + where);
            string link = AutoLink(map, bp, toAny, left.Blueprints - 1, mb);
            return HandResult.Placed(WithPower(map, "placed " + PlacePure.Article(what) + (where.Length > 0 ? " " + where : "") + link), mb.Marks);
        }

        // ---------------------------------------------------------------- furnish, heater, cooler
        /// <summary>One building in a named room through the Build hand, with refusals that say what to do. Wires powered buildings.</summary>
        static HandResult InRoom(Map map, string word, At at, BuildBudget left, bool wire, out Blueprint made, out ThingDef def)
        {
            made = null; def = null;
            HandResult no;
            def = VivariumHands.ResolveThing(word, out no);
            if (def == null) return no;
            if (at.Room == null) return HandResult.No(at.Given ? "that is not inside a room; name a room instead" : "name a room to put the " + def.label + " in");
            var ba = new BuildArgs { Thing = word };
            ba.At.Room = at.Room.ID;
            var r = new VivariumHands().Build(ba, left);
            if (!r.Ok)
            {
                if (PlacePending.IsNoSpot(r.Reason))
                    return HandResult.No(PlacePending.NoSpotMessage(at.Label, def.size.x, def.size.z, def.label, PlacePending.Detail(r.Reason)));
                // the Build hand names cells; the model names rooms (words, never coordinates)
                string words = System.Text.RegularExpressions.Regex.Replace(r.Reason ?? "", @"\s*\(\d+,\d+\)", "");
                return HandResult.No(words == r.Reason ? words : words + "; try another room or place something else first");
            }
            made = BlueprintOf(map, r.Marks, def);
            string note = "placed " + PlacePure.Article(def.label) + " in " + PlacePending.The(at.Label);
            if (wire && made != null && Consumes(def))
            {
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                string link = AutoLink(map, made, false, left.Blueprints - 1, mb);
                r.Marks.AddRange(mb.Marks);
                note += link;
            }
            return HandResult.Placed(note, r.Marks);
        }

        HandResult Furnish(Map map, PlaceArgs a, BuildBudget left)
        {
            HandResult no;
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            Blueprint bp; ThingDef def;
            var r = InRoom(map, a.What, at, left, true, out bp, out def);
            return r.Ok && r.Note != null && r.Note.IndexOf("(power:", StringComparison.Ordinal) < 0 && Consumes(def) ? HandResult.Placed(WithPower(map, r.Note), r.Marks) : r;
        }

        HandResult Climate(Map map, PlaceArgs a, BuildBudget left)
        {
            HandResult no;
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            Blueprint bp; ThingDef def;
            var r = InRoom(map, a.Op, at, left, true, out bp, out def);
            if (!r.Ok) return r;
            string role = at.Room == null ? "" : VivariumBaseRead.RoleWord(at.Room);
            int? deg = PlacePending.DefaultDegrees(a.Op, a.Degrees, at.Label, role);
            string note = r.Note;
            if (deg != null && bp != null)
            {
                PlaceGameComponent.Queue(map, bp.Position.x, bp.Position.z, def.defName, deg.Value);
                note += "; " + PlacePending.PendingNote(deg.Value);
            }
            if (note.IndexOf("(power:", StringComparison.Ordinal) < 0) note = WithPower(map, note);
            return HandResult.Placed(note, r.Marks);
        }

        // ---------------------------------------------------------------- generator, battery, switch
        HandResult Generator(Map map, PlaceArgs a, BuildBudget left)
        {
            string defName = PlaceWords.GeneratorDef(a.What);
            if (defName == null) return HandResult.No("no generator kind " + a.What + "; kinds: " + string.Join(", ", PlacePure.GeneratorKinds));
            HandResult no;
            var def = VivariumHands.ResolveThing(defName, out no);
            if (def == null) return no;
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            if (a.What == "geothermal") return Geothermal(map, def, at, left);
            bool open = a.What == "solar" || a.What == "wind";
            Func<CellRect, bool> extra = r => (!open || Unroofed(map, r)) && (open || at.Room != null || !InLivingRoom(map, r));
            string first;
            var spots = Spots(map, def, at.Center, open ? 35 : 25, extra, 6, out first);
            if (spots.Count == 0)
                return HandResult.No("no room for a " + def.label + " near " + (at.Given ? at.Label : "the base") + (open ? " (it needs open, unroofed ground)" : "") + (first == null ? "" : ": " + first));
            var best = BestByRoute(map, def, spots, true);
            return PlaceWired(map, def, best, true, left, def.label, "near " + (at.Given ? at.Label : "the base"));
        }

        /// <summary>Of a few spots, the one whose conduit route to a net is shortest (the first when there is no net to join).</summary>
        static Spot BestByRoute(Map map, ThingDef def, List<Spot> spots, bool toAny)
        {
            if (spots.Count == 1) return spots[0];
            HandResult no;
            var conduit = VivariumHands.ResolveThing("conduit", out no);
            if (conduit == null) return spots[0];
            var goals = NetCells(map, !toAny, null);
            if (goals.Count == 0) return spots[0];
            var cost = ConduitCost(map, conduit, goals);
            Spot best = spots[0]; int bestN = int.MaxValue;
            foreach (var s in spots)
            {
                var starts = Ring(map, s.Rect).Select(p => new[] { p.x, p.z }).ToList();
                var route = ConduitRoute.Find(starts, goals, cost, 40);
                int n = route == null ? 1000 : route.NewCells;
                if (n < bestN) { bestN = n; best = s; }
            }
            return best;
        }

        HandResult Geothermal(Map map, ThingDef def, At at, BuildBudget left)
        {
            var geyserDef = DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser");
            if (geyserDef == null) return HandResult.No("this game has no steam geysers");
            var home = VivariumHands.HomeCenter(map);
            var geysers = map.listerThings.ThingsOfDef(geyserDef)
                .Where(g => g.Spawned && !g.Position.Fogged(map) && Dist2(g.Position, home) <= 70 * 70)
                .OrderBy(g => Dist2(g.Position, at.Center)).ToList();
            if (geysers.Count == 0) return HandResult.No("no steam geyser near the base; geothermal needs one in sight");
            string first = null; bool anyFree = false;
            foreach (var g in geysers)
            {
                var sg = g as Building_SteamGeyser;
                if (sg != null && sg.harvester != null) continue;
                bool planned = false;
                foreach (var t in g.Position.GetThingList(map)) if ((t is Blueprint || t is Frame) && Built(t) == def) planned = true;
                if (planned) continue;
                anyFree = true;
                for (int r = 0; r < 4; r++)
                {
                    var rot = new Rot4(r);
                    var rect = GenAdj.OccupiedRect(g.Position, rot, def.size);
                    var rep = GenConstruct.CanPlaceBlueprintAt(def, g.Position, rot, map, false, null, null, null);
                    if (!rep.Accepted) { if (first == null) first = rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason; continue; }
                    return PlaceWired(map, def, new Spot { Cell = g.Position, Rot = rot, Rect = rect }, true, left, def.label, "on the steam geyser");
                }
            }
            return HandResult.No(anyFree ? "can't build on the steam geysers near the base" + (first == null ? "" : ": " + first) : "every steam geyser near the base already has a generator");
        }

        HandResult Battery(Map map, PlaceArgs a, BuildBudget left)
        {
            HandResult no;
            var def = VivariumHands.ResolveThing("battery", out no);
            if (def == null) return no;
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            string first;
            var spots = Spots(map, def, at.Center, 20, r => at.Room != null || !InLivingRoom(map, r), 6, out first);
            if (spots.Count == 0) return HandResult.No("no room for a battery near " + (at.Given ? at.Label : "the base") + (first == null ? "" : ": " + first));
            return PlaceWired(map, def, BestByRoute(map, def, spots, true), true, left, def.label, "near " + (at.Given ? at.Label : "the base"));
        }

        HandResult Switch(Map map, PlaceArgs a, BuildBudget left)
        {
            HandResult no;
            var def = VivariumHands.ResolveThing("power switch", out no);
            if (def == null) return no;
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            var conduit = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
            if (conduit == null) return HandResult.No("this game has no power conduit");
            var cells = map.listerThings.ThingsOfDef(conduit).Where(t => t.Spawned && t.Position.GetDoor(map) == null)
                .OrderBy(t => Dist2(t.Position, at.Center)).Take(80).ToList();
            if (cells.Count == 0) return HandResult.No("no power conduit built yet to put a switch on; run power first (place generator, place connect)");
            string first = null;
            foreach (var t in cells)
            {
                var rep = GenConstruct.CanPlaceBlueprintAt(def, t.Position, Rot4.North, map, false, null, null, null);
                if (!rep.Accepted) { if (first == null) first = rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason; continue; }
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                if (PlaceBp(map, def, t.Position, Rot4.North, mb) == null) continue;
                return HandResult.Placed(WithPower(map, "placed a power switch on the conduit near " + (at.Given ? at.Label : "the base")), mb.Marks);
            }
            return HandResult.No("no conduit near " + (at.Given ? at.Label : "the base") + " takes a power switch" + (first == null ? "" : ": " + first));
        }

        // ---------------------------------------------------------------- connect
        HandResult Connect(Map map, PlaceArgs a, BuildBudget left)
        {
            HandResult no;
            if (string.IsNullOrWhiteSpace(a.At)) return HandResult.No("say what to connect: a room or a building (#id)");
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            var conduit = VivariumHands.ResolveThing("conduit", out no);
            if (conduit == null) return no;
            var consumers = new List<Consumer>();
            PowerNet own = null;
            if (at.Thing != null)
            {
                var t = at.Thing; var d = Built(t);
                var cp = (t as ThingWithComps) == null ? null : ((ThingWithComps)t).GetComp<CompPower>();
                bool planned = t is Blueprint || t is Frame;
                if (cp == null && !(planned && d != null && d.GetCompProperties<CompProperties_Power>() != null))
                    return HandResult.No("the " + at.Label + " does not use or carry power");
                if (cp != null) own = cp.PowerNet;
                consumers.Add(new Consumer { T = t, Def = d, Label = at.Label });
            }
            else if (at.Room != null)
            {
                foreach (var t in ThingsAround(map, at.Room))
                {
                    var d = Built(t);
                    if (d == null || !Consumes(d) || !InClimate(map, t, d, at.Room)) continue;
                    if (t is Blueprint || t is Frame) { consumers.Add(new Consumer { T = t, Def = d, Label = d.label }); continue; }
                    var tw = t as ThingWithComps;
                    var tr = tw == null ? null : tw.GetComp<CompPowerTrader>();
                    if (tr == null || t.Faction != Faction.OfPlayer) continue;
                    if (tr.PowerNet != null && NetHasProduction(tr.PowerNet)) continue;
                    consumers.Add(new Consumer { T = t, Def = d, Label = d.label });
                }
            }
            else return HandResult.No("name a room or a building (#id) to connect");
            if (consumers.Count == 0) return HandResult.Done("nothing in " + PlacePending.The(at.Label) + " needs wiring (" + PowerSummary(map) + ")");
            var goals = NetCells(map, at.Thing == null || own == null, own);
            if (goals.Count == 0) return HandResult.No(PlacePending.NoRouteMessage(at.Label));
            foreach (var c in consumers) foreach (var p in c.T.OccupiedRect()) goals.Remove(BaseGrid.Key(p.x, p.z));
            int reached; List<string> un;
            var cells = PlanLinks(map, conduit, consumers, goals, out reached, out un);
            if (reached == 0) return HandResult.No(PlacePending.NoRouteMessage(at.Label));
            if (cells.Count > left.Blueprints) return HandResult.No(PlacePending.OverCapMessage(cells.Count, left.Blueprints));
            string sealOff = VivariumBaseRead.Guard(map, conduit, cells.Select(p => new IntVec3(p[0], 0, p[1])), Rot4.North);
            if (sealOff != null) return HandResult.No("the conduits would " + sealOff);
            string skipped = un.Count == 0 ? "" : "; could not reach " + string.Join(", ", un.Distinct());
            if (cells.Count == 0) return HandResult.Done("everything in " + PlacePending.The(at.Label) + " already touches a powered grid" + skipped + " (" + PowerSummary(map) + ")");
            var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
            int n = PlaceConduits(map, conduit, cells, mb);
            if (n == 0) return HandResult.No("the game would not place the conduits");
            return HandResult.Placed(WithPower(map, "ran " + PlacePending.Conduits(n) + " to wire " + PlacePending.The(at.Label) + " to the grid" + skipped), mb.Marks);
        }

        // ---------------------------------------------------------------- target
        HandResult Target(Map map, PlaceArgs a)
        {
            HandResult no;
            if (a.Degrees == null) return HandResult.No("say a temperature: " + string.Join(", ", PlacePure.TempWords));
            var at = ResolveAt(map, a.At, out no);
            if (at == null) return no;
            if (at.Room == null) return HandResult.No("name the room whose heaters and coolers to set");
            int deg = a.Degrees.Value, set = 0, queued = 0;
            foreach (var t in ThingsAround(map, at.Room))
            {
                var d = Built(t);
                if (d == null || !InClimate(map, t, d, at.Room)) continue;
                if (t is Blueprint || t is Frame)
                {
                    if (d.GetCompProperties<CompProperties_TempControl>() == null) continue;
                    PlaceGameComponent.Queue(map, t.Position.x, t.Position.z, d.defName, deg);
                    queued++;
                    continue;
                }
                var tw = t as ThingWithComps;
                var tc = tw == null ? null : tw.GetComp<CompTempControl>();
                if (tc == null || t.Faction != Faction.OfPlayer) continue;
                tc.TargetTemperature = deg;
                set++;
            }
            if (set + queued == 0) return HandResult.No(PlacePending.NoClimateMessage(at.Label));
            return HandResult.Done(PlacePending.TargetNote(set, queued, at.Label, deg));
        }

        // ---------------------------------------------------------------- ship
        HandResult Ship(Map map, PlaceArgs a, BuildBudget left)
        {
            string defName = ShipLayout.DefName(a.What);
            if (defName == null) return HandResult.No("no ship part " + a.What + "; parts: " + string.Join(", ", ShipLayout.Order));
            HandResult no;
            var def = VivariumHands.ResolveThing(defName, out no);
            if (def == null) return no;
            string part = ShipPlan.PartOfDef(def.defName) ?? "beam";
            var existing = new List<KeyValuePair<string, ShipRect>>();
            var seen = new HashSet<Thing>();
            foreach (var t in map.listerBuildings.allBuildingsColonist.Cast<Thing>()
                         .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
            {
                var d = Built(t);
                if (d == null || d.building == null || !d.building.shipPart || !seen.Add(t)) continue;
                var r = t.OccupiedRect();
                existing.Add(new KeyValuePair<string, ShipRect>(ShipPlan.PartOfDef(d.defName) ?? "other", new ShipRect(r.minX, r.minZ, r.Width, r.Height)));
            }
            int[] rots = def.rotatable ? new[] { 0, 1, 2, 3 } : new[] { 0 };
            Spot spot = null; string first = null; string where;
            BaseGrid grid = null;   // read once: the seal-off guard rereads the whole base per call otherwise
            if (existing.Count > 0)
            {
                int pad = Math.Max(def.size.x, def.size.z) + 1;
                int x0 = existing.Min(e => e.Value.X) - pad, x1 = existing.Max(e => e.Value.X + e.Value.W) + pad;
                int z0 = existing.Min(e => e.Value.Z) - pad, z1 = existing.Max(e => e.Value.Z + e.Value.H) + pad;
                var cands = new List<KeyValuePair<double, Spot>>();
                for (int x = Math.Max(0, x0); x <= Math.Min(map.Size.x - 1, x1); x++)
                    for (int z = Math.Max(0, z0); z <= Math.Min(map.Size.z - 1, z1); z++)
                        foreach (int r in rots)
                        {
                            var c = new IntVec3(x, 0, z); var rot = new Rot4(r);
                            var rect = GenAdj.OccupiedRect(c, rot, def.size);
                            double? sc = ShipPlan.Score(part, new ShipRect(rect.minX, rect.minZ, rect.Width, rect.Height), existing);
                            if (sc != null) cands.Add(new KeyValuePair<double, Spot>(sc.Value, new Spot { Cell = c, Rot = rot, Rect = rect }));
                        }
                int tried = 0;
                foreach (var kv in cands.OrderByDescending(k => k.Key))
                {
                    if (++tried > 400) break;
                    string why = FitReason(map, def, kv.Value);
                    if (why != null) { if (first == null) first = why; continue; }
                    if (grid == null) grid = VivariumBaseRead.Read(map);
                    if (VivariumBaseRead.Guard(map, def, new[] { kv.Value.Cell }, kv.Value.Rot, grid) != null) continue;
                    spot = kv.Value; break;
                }
                where = "next to the ship's " + (existing.Count == 1 ? "first part" : "other parts");
                if (spot == null) return HandResult.No("no free spot touching the ship takes a " + def.label + (first == null ? "" : ": " + first) + "; place the beams and bigger parts first or clear the ground beside the ship");
            }
            else
            {
                var home = VivariumHands.HomeCenter(map);
                var ring = BuildHelpers.Circle(home.x, home.z, 40, map.Size.x, map.Size.z).Where(p => BuildHelpers.Dist2(p[0], p[1], home.x, home.z) >= 64).ToList();
                int tried = 0;
                foreach (var p in BuildHelpers.NearestFirst(ring, home.x, home.z, ring.Count))
                {
                    var c = new IntVec3(p[0], 0, p[1]);
                    if (c.Fogged(map) || c.Roofed(map)) continue;
                    foreach (int r in rots)
                    {
                        var rot = new Rot4(r);
                        var rect = GenAdj.OccupiedRect(c, rot, def.size);
                        if (!OpenGround(map, rect)) continue;
                        if (++tried > 600) break;
                        var s = new Spot { Cell = c, Rot = rot, Rect = rect };
                        string why = FitReason(map, def, s);
                        if (why != null) { if (first == null) first = why; continue; }
                        if (grid == null) grid = VivariumBaseRead.Read(map);
                        if (VivariumBaseRead.Guard(map, def, new[] { c }, rot, grid) != null) continue;
                        spot = s; break;
                    }
                    if (spot != null || tried > 600) break;
                }
                where = "on open ground away from the base";
                if (spot == null) return HandResult.No("no open unroofed space for the ship near the base" + (first == null ? "" : ": " + first));
            }
            var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
            var bp = PlaceBp(map, def, spot.Cell, spot.Rot, mb);
            if (bp == null) return HandResult.No("the game would not place a " + def.label);
            return HandResult.Placed("placed " + PlacePure.Article(def.label) + " " + where, mb.Marks);
        }

        /// <summary>Why a ship part cannot go at a spot (null when it can): fog, roof, beds, the game's own placement check.</summary>
        static string FitReason(Map map, ThingDef def, Spot s)
        {
            foreach (var c in s.Rect) if (!c.InBounds(map) || c.Fogged(map)) return "part of it is outside what you can see";
            string blocked = VivariumHands.FootprintBlock(map, s.Rect);
            if (blocked != null) return blocked;
            var rep = GenConstruct.CanPlaceBlueprintAt(def, s.Cell, s.Rot, map, false, null, null, null);
            return rep.Accepted ? null : (rep.Reason.NullOrEmpty() ? "it does not fit there" : rep.Reason);
        }

        /// <summary>Open unroofed ground for the footprint and one ring around it: standable, no buildings, no roof.</summary>
        static bool OpenGround(Map map, CellRect rect)
        {
            foreach (var c in rect.ExpandedBy(1))
            {
                if (!c.InBounds(map)) return false;
                if (c.Roofed(map) || c.GetEdifice(map) != null) return false;
                if (rect.Contains(c) && !c.Standable(map)) return false;
            }
            return true;
        }
    }
}

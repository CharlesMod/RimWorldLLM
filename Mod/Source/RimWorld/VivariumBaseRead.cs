using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Reads the live map into a BaseGrid (the base planner's and the never-seal-off guard's snapshot): a window around
    /// the base, each cell's kind, the rooms with their roles and owners, and the things to keep reachable (doors, beds,
    /// benches, stockpiles, colonists, rooms). Built fresh on every call; nothing is kept between calls. Game thread only.</summary>
    public static class VivariumBaseRead
    {
        public const int Margin = 30, MaxSide = 170;

        public static BaseGrid Read(Map map)
        {
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            Action<IntVec3> grow = c => { x0 = Math.Min(x0, c.x); z0 = Math.Min(z0, c.z); x1 = Math.Max(x1, c.x); z1 = Math.Max(z1, c.z); };
            foreach (var b in map.listerBuildings.allBuildingsColonist) grow(b.Position);
            foreach (var p in map.mapPawns.FreeColonistsSpawned) grow(p.Position);
            var home = HomeCenterOf(map);
            grow(home);
            // a window around the base, at most MaxSide a side (centred on home when the base is spread wider)
            int wx0 = Math.Max(0, x0 - Margin), wz0 = Math.Max(0, z0 - Margin);
            int wx1 = Math.Min(map.Size.x - 1, x1 + Margin), wz1 = Math.Min(map.Size.z - 1, z1 + Margin);
            if (wx1 - wx0 + 1 > MaxSide) { wx0 = Math.Max(0, home.x - MaxSide / 2); wx1 = Math.Min(map.Size.x - 1, wx0 + MaxSide - 1); }
            if (wz1 - wz0 + 1 > MaxSide) { wz0 = Math.Max(0, home.z - MaxSide / 2); wz1 = Math.Min(map.Size.z - 1, wz0 + MaxSide - 1); }
            var g = new BaseGrid(wx0, wz0, wx1 - wx0 + 1, wz1 - wz0 + 1) { HomeX = home.x, HomeZ = home.z };
            g.EdgeIsMap = wx0 == 0 && wz0 == 0 && wx1 == map.Size.x - 1 && wz1 == map.Size.z - 1;

            var rooms = new Dictionary<int, BaseRoom>();
            var heavy = TerrainAffordanceDefOf.Heavy;
            for (int z = wz0; z <= wz1; z++)
                for (int x = wx0; x <= wx1; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    BaseFlag f = BaseFlag.None;
                    if (c.Fogged(map)) { g.Set(x, z, BaseFlag.Fog); continue; }
                    var terr = c.GetTerrain(map);
                    bool walk = c.Walkable(map);
                    bool solid = false, door = false, thing = false;
                    foreach (var t in c.GetThingList(map))
                    {
                        ThingDef d = t.def;
                        if (t is Blueprint || t is Frame) d = t.def.entityDefToBuild as ThingDef;
                        if (d == null || d.category != ThingCategory.Building && !(t is Blueprint) && !(t is Frame)) continue;
                        if (d.IsDoor) { door = true; continue; }
                        if (d.passability == Traversability.Impassable)
                        {
                            if (d.building != null && d.building.isNaturalRock) f |= BaseFlag.Rock;
                            else f |= BaseFlag.Wall;
                            solid = true;
                        }
                        else if (d.category == ThingCategory.Building || d.IsBlueprint || t is Blueprint || t is Frame) thing = true;
                    }
                    if (door) f |= BaseFlag.Door | BaseFlag.Walk;
                    else if (!solid)
                    {
                        if (terr.IsWater && terr.passability == Traversability.Impassable) f |= BaseFlag.Water;
                        else if (walk)
                        {
                            f |= BaseFlag.Walk;
                            if (terr.IsWater || !terr.affordances.Contains(heavy)) f |= BaseFlag.Marsh;
                            else if (!thing) f |= BaseFlag.Free;
                        }
                        else if (terr.IsWater) f |= BaseFlag.Water;
                        if (thing) f |= BaseFlag.Thing;
                        if (terr.fertility >= 1f && (f & BaseFlag.Free) != 0) f |= BaseFlag.Fertile;
                        if (map.zoneManager.ZoneAt(c) != null) f |= BaseFlag.Zone;
                    }
                    var roof = map.roofGrid.RoofAt(c);
                    if (roof != null && roof.isThickRoof) f |= BaseFlag.Mountain;
                    g.Set(x, z, f);

                    if (!walk || door) continue;
                    var room = c.GetRoom(map);
                    if (room == null || room.PsychologicallyOutdoors || room.IsDoorway || room.TouchesMapEdge) continue;
                    g.RoomIds[g.Idx(x, z)] = room.ID;
                    BaseRoom br;
                    if (!rooms.TryGetValue(room.ID, out br))
                    {
                        br = new BaseRoom { Id = room.ID, Role = RoleWord(room), MinX = x, MaxX = x, MinZ = z, MaxZ = z };
                        rooms[room.ID] = br;
                        var owners = room.ContainedBeds.SelectMany(b => b.OwnersForReading).Where(p => p?.Name != null).Select(p => p.Name.ToStringShort).Distinct().ToList();
                        if (owners.Count == 1) br.Owner = owners[0];
                        br.Label = br.Owner != null && br.Role == "bedroom" ? br.Owner + "'s bedroom" : "the " + br.Role;
                    }
                    br.Cells++;
                    br.MinX = Math.Min(br.MinX, x); br.MaxX = Math.Max(br.MaxX, x);
                    br.MinZ = Math.Min(br.MinZ, z); br.MaxZ = Math.Max(br.MaxZ, z);
                }
            g.Rooms.AddRange(rooms.Values);
            g.AddRoomPois();
            g.AddDoorPois();

            // things to keep reachable
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!g.In(b.Position.x, b.Position.z)) continue;
                var bed = b as Building_Bed;
                if (bed != null)
                {
                    var who = bed.OwnersForReading.FirstOrDefault(p => p?.Name != null);
                    g.Pois.Add(new BasePoi("bed", who != null ? who.Name.ToStringShort + "'s bed" : "a " + (bed.Medical ? "hospital bed" : "bed"), b.Position.x, b.Position.z));
                }
                else if (b is Building_WorkTable || b is Building_ResearchBench)
                    g.Pois.Add(new BasePoi("bench", "the " + b.LabelNoCount, b.Position.x, b.Position.z));
            }
            foreach (var z in map.zoneManager.AllZones)
            {
                var sp = z as Zone_Stockpile;
                if (sp == null) continue;
                var poi = new BasePoi { Kind = "stockpile", Label = "the " + (sp.label ?? "stockpile").ToLowerInvariant() };
                foreach (var c in sp.Cells) if (g.In(c.x, c.z)) poi.Cells.Add(new[] { c.x, c.z });
                if (poi.Cells.Count > 0) g.Pois.Add(poi);
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                if (g.In(p.Position.x, p.Position.z)) g.Pois.Add(new BasePoi("colonist", p.Name?.ToStringShort ?? p.LabelShort, p.Position.x, p.Position.z));
            return g;
        }

        static IntVec3 HomeCenterOf(Map map)
        {
            var h = VivariumBuildView.HomeOf(map);
            if (h != null) return new IntVec3(h[0], 0, h[1]);
            return map.Center;
        }

        /// <summary>The planner's role word for a vanilla room role; a cold storeroom is a freezer.</summary>
        public static string RoleWord(Room r)
        {
            var role = r.Role;
            if (role == null || role == RoomRoleDefOf.None) return "room";
            switch (role.defName)
            {
                case "Bedroom": return "bedroom";
                case "Barracks": return "barracks";
                case "Kitchen": return "kitchen";
                case "DiningRoom": return "dining";
                case "RecRoom": return "rec";
                case "Hospital": return "hospital";
                case "Laboratory": return "research";
                case "Workshop": return "workshop";
                case "Storeroom":
                    try { return r.Temperature < 0f ? "freezer" : "storage"; } catch (Exception) { return "storage"; }
                default: return (role.label ?? role.defName).ToLowerInvariant();
            }
        }

        /// <summary>The never-seal-off guard for a set of blueprints of one def: null when fine (or the def doesn't block), else
        /// the plain reason. cells: each blueprint's root cell (its footprint is taken from the def and rotation).</summary>
        public static string Guard(Map map, ThingDef def, IEnumerable<IntVec3> roots, Rot4 rot, BaseGrid g = null)
        {
            if (def == null || def.passability != Traversability.Impassable || def.IsDoor) return null;
            var solid = new List<int[]>();
            foreach (var c in roots)
                foreach (var o in GenAdj.OccupiedRect(c, rot, def.size)) solid.Add(new[] { o.x, o.z });
            if (solid.Count == 0) return null;
            return ReachGuard.Check(g ?? Read(map), solid);
        }
    }
}

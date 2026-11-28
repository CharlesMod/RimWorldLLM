using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The floor hand (POST /act/floor): a terrain blueprint on each interior cell of a room, or SmoothFloor designations on its
    /// rough stone. Game thread. Decisions (words, refusals, notes) are in FloorsPure; blueprints are marked "floor" and designations
    /// "designation", so the player's undo takes both back.</summary>
    public sealed partial class VivariumHands : IFloorHands
    {
        /// <summary>The floors the player can build, researched or not (stuff-made terrain is left out: every floor here is paid from a cost list).</summary>
        internal static List<FloorInfo> Floors()
        {
            var l = new List<FloorInfo>();
            foreach (var d in DefDatabase<TerrainDef>.AllDefs.OrderBy(x => x.uiOrder).ThenBy(x => x.defName, StringComparer.Ordinal))
            {
                if (d.designationCategory != DesignationCategoryDefOf.Floors || !d.BuildableByPlayer || d.MadeFromStuff) continue;
                var cost = d.CostList;
                if (cost == null || cost.Count == 0) continue;
                var miss = d.researchPrerequisites == null ? null : d.researchPrerequisites.FirstOrDefault(r => !r.IsFinished);
                var f = new FloorInfo { Def = d.defName, Label = d.label ?? d.defName, Research = miss == null ? null : (miss.label ?? miss.defName) };
                foreach (var c in cost) if (c.thingDef != null && c.count > 0) f.Cost.Add(new FloorCost(c.thingDef.defName, c.thingDef.label ?? c.thingDef.defName, c.count));
                if (f.Cost.Count > 0) l.Add(f);
            }
            return l;
        }

        /// <summary>The stock of a material by defName (the same count the room hand uses).</summary>
        internal static Func<string, int> StockOfMap(Map map)
        {
            return def => { var d = DefDatabase<ThingDef>.GetNamedSilentFail(def); return d == null ? 0 : VivariumLoose.Usable(map, d); };
        }

        public HandResult Floor(FloorArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                Room room = null;
                foreach (var r in map.regionGrid.AllRooms) if (r.ID == a.Room) { room = r; break; }
                if (room == null) return HandResult.No("no room " + a.Room + " on the map; use a room id from ROOMS", "lay a floor");
                if (room.PsychologicallyOutdoors || room.TouchesMapEdge) return HandResult.No("room " + a.Room + " is open ground, not a room; use a room id from ROOMS", "lay a floor");
                string name = FloorsPure.RoomName(room.Role == null ? null : room.Role.label);
                // the interior: not a door cell, not fogged
                var cells = room.Cells.Where(c => c.InBounds(map) && !c.Fogged(map) && c.GetDoor(map) == null).ToList();
                if (cells.Count == 0) return HandResult.No("no floor cells to work on in " + name, "lay a floor");
                if (FloorsPure.IsSmooth(a.Kind)) return SmoothFloors(map, cells, name, left);

                FloorInfo info; string refusal;
                if (!FloorsPure.Resolve(a.Kind, Floors(), out info, out refusal)) return HandResult.No(refusal, "lay a floor");
                string word = FloorsPure.Word(info);
                string what = "lay " + word + " in " + name;
                var def = DefDatabase<TerrainDef>.GetNamedSilentFail(info.Def);
                if (def == null) return HandResult.No(word + " is not in this game", what);

                var lay = new List<IntVec3>();
                int already = 0, skipped = 0;
                string firstReason = null;
                foreach (var c in cells)
                {
                    if (map.terrainGrid.TerrainAt(c) == def || c.GetThingList(map).Any(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild == def)) { already++; continue; }
                    var rep = GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map, false, null, null, null);
                    if (!rep.Accepted) { skipped++; if (firstReason == null) firstReason = rep.Reason.NullOrEmpty() ? "it does not fit" : rep.Reason; continue; }
                    lay.Add(c);
                    if (lay.Count > left.Blueprints) break;   // over the cycle's budget already: no need to test the rest
                }
                if (lay.Count == 0)
                    return skipped > 0 ? HandResult.No("can't lay " + word + " in " + name + ": " + firstReason, what) : HandResult.Done(FloorsPure.AlreadyNote(word, name));
                string no = FloorsPure.Budget(lay.Count, left.Blueprints) ?? FloorsPure.Stock(info, lay.Count, StockOfMap(map));
                if (no != null) return HandResult.No(no, what);

                var mark = new NarratorMark { Kind = "floor", Map = map.uniqueID, Def = def.defName };
                foreach (var c in lay)
                {
                    var bp = GenConstruct.PlaceBlueprintForBuild(def, c, map, Rot4.North, Faction.OfPlayer, null);
                    if (bp == null) { skipped++; continue; }
                    mark.AddCell(c.x, c.z);
                    mark.Things.Add(bp.thingIDNumber);
                }
                if (mark.CellCount == 0) return HandResult.No("the game placed no floor blueprints in " + name, what);
                return HandResult.Placed(FloorsPure.Note(word, name, mark.CellCount, skipped), new List<NarratorMark> { mark });
            }
            catch (Exception e) { return Failed(e); }
        }

        HandResult SmoothFloors(Map map, List<IntVec3> cells, string name, BuildBudget left)
        {
            string what = "smooth " + name + "'s floor";
            var dd = DesignationDefOf.SmoothFloor;
            var dm = map.designationManager;
            var todo = new List<IntVec3>();
            int skipped = 0;
            foreach (var c in cells)
            {
                var t = map.terrainGrid.TerrainAt(c);
                if (dm.DesignationAt(c, dd) != null) continue;   // already marked
                if (t.smoothedTerrain == null || !t.affordances.Contains(TerrainAffordanceDefOf.SmoothableStone)) { skipped++; continue; }
                todo.Add(c);
            }
            if (todo.Count == 0) return HandResult.No("no rough stone floor left to smooth in " + name, what);
            string no = FloorsPure.Budget(todo.Count, left.Designations, "designations");
            if (no != null) return HandResult.No(no, what);
            var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
            foreach (var c in todo)
            {
                dm.AddDesignation(new Designation(c, dd));
                mb.Designation(dd.defName, 0, c.x, c.z);
            }
            return HandResult.Placed(FloorsPure.SmoothNote(name, todo.Count, skipped), mb.Marks);
        }
    }
}

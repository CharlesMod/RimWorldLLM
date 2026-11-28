using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The word form of the area hand (POST /act/area with where/who): the box comes from a digest room id, a zone id or the
    /// built base (AreasPure does the box math), then the existing Area() hand changes the cells; pawns and animals are restricted
    /// by name. The old restriction of each pawn is marked ("area_restrict") so the player's undo restores it. Game thread.</summary>
    public sealed partial class VivariumHands : IAreaHands
    {
        public HandResult AreaWords(AreaWordArgs a, BuildBudget left)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                string label = AreasPure.AreaLabel(a.Name, a.Home);
                var notes = new List<string>();
                var marks = new List<NarratorMark>();
                if (a.Op != "assign")
                {
                    CellBox box; string whereLabel;
                    string no = ResolveAreaBox(map, a.Where, out box, out whereLabel);
                    if (no != null) return HandResult.No(no, (a.Op == "add" ? "add to the " : "take from the ") + label + " area");
                    var args = new AreaArgs { Kind = a.Home ? "home" : "allowed", Op = a.Op, Name = a.Home ? null : a.Name, Box = box };
                    var r = Area(args, left);
                    if (!r.Ok) return HandResult.No(AreasPure.CapHint(r.Reason, a.Where), r.What);
                    int changed = 0;
                    if (r.Marks != null) { foreach (var m in r.Marks) changed += m.CellCount; marks.AddRange(r.Marks); }
                    notes.Add(a.Op == "add" ? AreasPure.AddNote(whereLabel, label, changed) : AreasPure.RemoveNote(whereLabel, label));
                }
                if (a.Who.Count > 0 && !a.Home)
                {
                    Area_Allowed area = null;
                    if (!a.Release)
                    {
                        area = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => Eq(x.Label, a.Name));
                        if (area == null)
                        {
                            var have = map.areaManager.AllAreas.OfType<Area_Allowed>().Select(x => x.Label).ToList();
                            return HandResult.No("no allowed area named '" + a.Name + "'; " + (have.Count == 0 ? "there are none yet (add one with where)" : "the areas are: " + string.Join(", ", have)), "restrict " + string.Join(", ", a.Who));
                        }
                    }
                    var pool = new List<KeyValuePair<string, int>>();
                    var byId = new Dictionary<int, Pawn>();
                    foreach (var p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
                    {
                        if (p.Dead || p.playerSettings == null || byId.ContainsKey(p.thingIDNumber)) continue;
                        byId[p.thingIDNumber] = p;
                        pool.Add(new KeyValuePair<string, int>(p.LabelShort, p.thingIDNumber));
                    }
                    List<int> ids; List<string> names; string refusal;
                    if (!AreasPure.ResolveWho(a.Who, pool, out ids, out names, out refusal)) return HandResult.No(refusal, "restrict " + string.Join(", ", a.Who));
                    var olds = new List<KeyValuePair<int, string>>();
                    foreach (int id in ids)
                    {
                        var ps = byId[id].playerSettings;
                        var old = ps.AreaRestrictionInPawnCurrentMap;
                        olds.Add(new KeyValuePair<int, string>(id, old == null ? "" : old.Label));
                        ps.AreaRestrictionInPawnCurrentMap = area;   // null releases
                    }
                    notes.Add(a.Release ? AreasPure.ReleaseNote(names) : AreasPure.RestrictNote(names, label));
                    marks.Add(new NarratorMark { Kind = AreasPure.RestrictKind, Map = map.uniqueID, Def = label, Extra = AreasPure.EncodeRestrict(olds) });
                }
                string note = string.Join("; ", notes);
                return marks.Count > 0 ? HandResult.Placed(note, marks) : HandResult.Done(note);
            }
            catch (Exception e) { return Failed(e); }
        }

        /// <summary>The box of a room, a zone or the base, or the refusal.</summary>
        static string ResolveAreaBox(Map map, AreaWhere w, out CellBox box, out string label)
        {
            box = null; label = null;
            int mw = map.Size.x, mh = map.Size.z;
            if (w.Kind == AreaWhere.Room)
            {
                Room room = null;
                foreach (var r in map.regionGrid.AllRooms) if (r.ID == w.Id) { room = r; break; }
                if (room == null) return "no room " + w.Id + " on the map; use a room id from ROOMS";
                var cells = room.Cells.Where(c => c.InBounds(map)).ToList();
                if (cells.Count == 0) return "room " + w.Id + " has no cells";
                box = AreasPure.BoxFromRoom(cells.Min(c => c.x), cells.Min(c => c.z), cells.Max(c => c.x), cells.Max(c => c.z), mw, mh);
                label = FloorsPure.RoomName(room.Role == null ? null : room.Role.label);
            }
            else if (w.Kind == AreaWhere.Zone)
            {
                var z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == w.Id);
                if (z == null) return "no zone #" + w.Id + "; use a zone id from ZONES";
                box = AreasPure.BoxFromCells(z.Cells.Select(c => new[] { c.x, c.z }), 0, mw, mh);
                label = "zone #" + w.Id;
            }
            else
            {
                var pts = map.listerBuildings.allBuildingsColonist.Select(b => new[] { b.Position.x, b.Position.z }).ToList();
                box = pts.Count > 0 ? AreasPure.BoxFromCells(pts, AreasPure.BasePad, mw, mh)
                                    : AreasPure.BoxAround(HomeCenter(map).x, HomeCenter(map).z, AreasPure.BasePad, mw, mh);
                label = "the base";
            }
            return box == null ? "that place is off the map" : null;
        }

        /// <summary>Undo of an "area_restrict" mark: every pawn gets its old area back (none when it had none).</summary>
        bool UndoAreaRestrict(NarratorMark mk, Map map)
        {
            bool any = false;
            foreach (var p in AreasPure.DecodeRestrict(mk.Extra))
            {
                var pawn = Routes.FindPawn(p.Key);
                if (pawn == null || pawn.playerSettings == null) continue;
                Area old = p.Value.Length == 0 ? null : (pawn.Map ?? map).areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => Eq(x.Label, p.Value));
                if (p.Value.Length > 0 && old == null) continue;   // that area is gone
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = old;
                any = true;
            }
            return any;
        }
    }
}

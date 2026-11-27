using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `build` block (docs/VIVARIUM.md): what the colony has built and what it could build.
    /// Vanilla reads only, nothing the player could not see in the UI. Each part is independent and dropped if it throws.</summary>
    public static class VivariumBuildView
    {
        const int MaxRooms = 12, MaxBenches = 8, MaxBills = 5, MaxRecipes = 8, MaxZones = 10, MaxStuff = 8, MaxPlants = 10;
        static readonly HashSet<string> logged = new HashSet<string>();

        static void Part(JObject o, string name, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[name] = t; }
            catch (Exception e) { if (logged.Add(name)) Log.Warning("[RimWorldLLM] digest build part '" + name + "' failed: " + e.Message); }
        }

        public static JObject Build(Map map)
        {
            var o = new JObject();
            int[] home = null;
            try { home = Home(map); } catch (Exception e) { if (logged.Add("home")) Log.Warning("[RimWorldLLM] digest build part 'home' failed: " + e.Message); }
            if (home != null) o["home"] = new JObject { ["x"] = home[0], ["z"] = home[1] };

            List<RoomInfo> rooms = null;
            try { rooms = ScanRooms(map); } catch (Exception e) { if (logged.Add("rooms")) Log.Warning("[RimWorldLLM] digest build part 'rooms' failed: " + e.Message); }
            if (rooms != null)
                o["rooms"] = new JArray(rooms.OrderByDescending(r => r.Imp).Take(MaxRooms).Select(r => new JObject
                {
                    ["id"] = r.Id, ["role"] = r.Role, ["label"] = r.Label, ["w"] = r.W, ["h"] = r.H, ["cells"] = r.Cells,
                    ["impressiveness"] = (int)Math.Round(r.Imp), ["bed"] = r.Bed, ["door"] = r.Door, ["roofed"] = r.Roofed, ["x"] = r.X, ["z"] = r.Z
                }));

            Part(o, "missing", () => new JArray(Missing(map, rooms)));
            Part(o, "benches", () => Benches(map));
            Part(o, "zones", () => Zones(map));
            Part(o, "can_build", () => new JArray(CanBuildWords()));
            Part(o, "stuff", () => Stuff(map));
            Part(o, "plants", () => Plants(map));
            if (home != null) Part(o, "ores", () => Ores(map, home));
            if (home != null) Part(o, "loose", () => Loose(map, home));
            return o;
        }

        class RoomInfo
        {
            public int Id, W, H, Cells, X, Z;
            public string Role, Label;
            public float Imp;
            public bool Bed, Door, Roofed, Bedroom;
            public HashSet<Pawn> Sleepers = new HashSet<Pawn>();
        }

        static int[] Home(Map map)
        {
            var pts = new List<int[]>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var bed = b as Building_Bed;
                if (bed != null && !bed.ForPrisoners && !bed.Medical) pts.Add(new[] { b.Position.x, b.Position.z });
            }
            if (pts.Count == 0)
                foreach (var p in map.mapPawns.FreeColonistsSpawned) pts.Add(new[] { p.Position.x, p.Position.z });
            return BuildView.HomeCenter(pts);
        }

        static List<RoomInfo> ScanRooms(Map map)
        {
            var list = new List<RoomInfo>();
            var seen = new HashSet<Room>();
            foreach (var region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
            {
                var r = region.Room;
                if (r == null || !seen.Add(r) || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None) continue;
                int n = r.CellCount;
                if (n < 4) continue;
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                IntVec3 mid = IntVec3.Invalid;
                var cells = r.Cells.ToList();
                foreach (var c in cells)
                {
                    if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x;
                    if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z;
                }
                float cx = (minX + maxX) / 2f, cz = (minZ + maxZ) / 2f, best = float.MaxValue;
                foreach (var c in cells)
                {
                    float d = (c.x - cx) * (c.x - cx) + (c.z - cz) * (c.z - cz);
                    if (d < best) { best = d; mid = c; }
                }
                var info = new RoomInfo
                {
                    Id = r.ID, Role = (r.Role.label ?? r.Role.defName).ToLowerInvariant(), W = maxX - minX + 1, H = maxZ - minZ + 1, Cells = n,
                    X = mid.x, Z = mid.z, Imp = r.GetStat(RoomStatDefOf.Impressiveness), Roofed = r.OpenRoofCount == 0,
                    Bedroom = r.Role == RoomRoleDefOf.Bedroom || r.Role == RoomRoleDefOf.Barracks
                };
                foreach (var t in r.ContainedAndAdjacentThings)
                    if (t is Building_Door) { info.Door = true; break; }
                foreach (var bed in r.ContainedBeds)
                {
                    info.Bed = true;
                    foreach (var p in bed.OwnersForReading) info.Sleepers.Add(p);
                }
                var owners = info.Sleepers.Where(p => p != null && p.Name != null).Select(p => p.Name.ToStringShort).ToList();
                info.Label = owners.Count == 1 && r.Role == RoomRoleDefOf.Bedroom ? owners[0] + "'s bedroom" : info.Role;
                list.Add(info);
            }
            return list;
        }

        static IEnumerable<string> Missing(Map map, List<RoomInfo> rooms)
        {
            var colonists = map.mapPawns.FreeColonists.ToList();
            var housed = new HashSet<Pawn>();
            if (rooms != null)
                foreach (var r in rooms) if (r.Roofed && r.Bedroom) foreach (var p in r.Sleepers) housed.Add(p);
            int noRoom = colonists.Count(p => !housed.Contains(p));
            var owned = new HashSet<Pawn>();
            int free = 0;
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var bed = b as Building_Bed;
                if (bed == null || bed.ForPrisoners || bed.Medical) continue;
                var ow = bed.OwnersForReading;
                if (ow.Count == 0) free++; else foreach (var p in ow) owned.Add(p);
            }
            int noBed = colonists.Count(p => !owned.Contains(p));
            var things = map.listerBuildings.allBuildingsColonist;
            Func<string, bool> has = def => things.Any(b => b.def.defName == def);
            bool stove = has("ElectricStove") || has("FueledStove") || has("Campfire");
            bool stockpile = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Any();
            bool freezer = false;
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Take(300))
            {
                var r = t.GetRoom();
                if (r != null && !r.PsychologicallyOutdoors && !r.UsesOutdoorTemperature && r.Temperature <= 0f) { freezer = true; break; }
            }
            var can = new HashSet<string>(CanBuildWords(), StringComparer.OrdinalIgnoreCase);
            var input = new MissingInput
            {
                Colonists = colonists.Count, ColonistsWithoutRoofedRoom = noRoom, ColonistsWithoutBed = noBed, BedsFree = free,
                HasStove = stove, HasFoodStockpile = stockpile, HasFreezer = freezer, CanBuildCooler = can.Contains("cooler"),
                HasDiningTable = things.Any(b => b.def.building != null && b.def.defName.StartsWith("Table", StringComparison.Ordinal) && b.def.surfaceType == SurfaceType.Eat),
                HasGrowZone = map.zoneManager.AllZones.OfType<Zone_Growing>().Any(),
                HasResearchBench = things.Any(b => b is Building_ResearchBench)
            };
            return BuildView.Missing(input);
        }

        static JToken Benches(Map map)
        {
            var arr = new JArray();
            foreach (var b in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
            {
                if (arr.Count >= MaxBenches) break;
                var o = new JObject { ["id"] = b.thingIDNumber, ["label"] = b.def.label };
                var room = b.GetRoom();
                if (room != null && !room.PsychologicallyOutdoors && room.Role != null && room.Role != RoomRoleDefOf.None) o["room"] = room.Role.LabelCap.ToString();
                o["bills"] = new JArray(b.BillStack.Bills.Take(MaxBills).Select(BillText));
                o["recipes"] = new JArray(b.def.AllRecipes.Where(r => r.AvailableNow).Take(MaxRecipes).Select(r => r.label));
                arr.Add(o);
            }
            return arr;
        }

        static string BillText(Bill bill)
        {
            string s = bill.recipe.label;
            var p = bill as Bill_Production;
            if (p != null)
            {
                if (p.repeatMode == BillRepeatModeDefOf.RepeatCount) s += " x" + p.repeatCount;
                else if (p.repeatMode == BillRepeatModeDefOf.TargetCount) s += " until " + p.targetCount;
                else if (p.repeatMode == BillRepeatModeDefOf.Forever) s += " forever";
            }
            if (bill.suspended) s += " (suspended)";
            return s;
        }

        static JToken Zones(Map map)
        {
            var arr = new JArray();
            foreach (var z in map.zoneManager.AllZones)
            {
                if (arr.Count >= MaxZones) break;
                var g = z as Zone_Growing;
                var s = z as Zone_Stockpile;
                if (g != null)
                {
                    var plant = g.GetPlantDefToGrow();
                    var o = new JObject { ["id"] = z.ID, ["kind"] = "grow", ["label"] = z.label, ["cells"] = z.Cells.Count };
                    if (plant != null) o["plant"] = plant.label;
                    arr.Add(o);
                }
                else if (s != null)
                    arr.Add(new JObject { ["id"] = z.ID, ["kind"] = "stockpile", ["label"] = z.label, ["cells"] = z.Cells.Count, ["preset"] = Preset(s), ["priority"] = s.GetStoreSettings().Priority.ToString().ToLowerInvariant() });
            }
            return arr;
        }

        static string Preset(Zone_Stockpile s)
        {
            var f = s.GetStoreSettings().filter;
            var allowed = f.AllowedThingDefs.ToList();
            if (allowed.Count == 0) return "none";
            if (allowed.All(d => d.IsMedicine)) return "meds";
            if (allowed.All(d => d.IsNutritionGivingIngestible)) return "food";
            if (allowed.All(d => d.IsWithinCategory(ThingCategoryDefOf.ResourcesRaw))) return "raw";
            return allowed.Count >= 100 ? "all" : "mixed";
        }

        static IEnumerable<string> CanBuildWords()
        {
            foreach (var kv in RimWorldLLM.Bridge.BuildPure.ThingAliases)
            {
                foreach (var name in kv.Value)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def == null) continue;
                    if (def.BuildableByPlayer && def.IsResearchFinished) yield return kv.Key;
                    break;
                }
            }
        }

        static JToken Stuff(Map map)
        {
            var rows = new List<KeyValuePair<string, int>>();
            foreach (var kv in RimWorldLLM.Bridge.BuildPure.StuffAliases)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Value);
                if (def == null) continue;
                int n = VivariumLoose.Usable(map, def);
                if (n > 0) rows.Add(new KeyValuePair<string, int>(kv.Key, n));
            }
            return new JArray(rows.OrderByDescending(r => r.Value).Take(MaxStuff).Select(r => new JObject { ["stuff"] = r.Key, ["count"] = r.Value }));
        }

        static JToken Plants(Map map)
        {
            var words = new List<string>();
            var seen = new HashSet<string>();
            foreach (var kv in RimWorldLLM.Bridge.BuildPure.PlantAliases)
            {
                if (words.Count >= MaxPlants) break;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Value);
                if (def == null || def.plant == null || !seen.Add(kv.Value)) continue;
                if (def.plant.sowTags == null || !def.plant.sowTags.Contains("Ground")) continue;
                if (def.plant.sowResearchPrerequisites != null && def.plant.sowResearchPrerequisites.Any(r => !r.IsFinished)) continue;
                words.Add(kv.Key);
            }
            return new JArray(words);
        }

        static JToken Loose(Map map, int[] home)
        {
            return new JArray(BuildView.GroupLoose(VivariumLoose.Get(map).Items, home[0], home[1]).Select(p => new JObject
            {
                ["label"] = p.Label, ["count"] = p.Count, ["dist"] = p.Dist, ["dir"] = p.Dir, ["forbidden"] = p.Forbidden
            }));
        }

        static JToken Ores(Map map, int[] home)
        {
            var cells = new List<OreCell>();
            foreach (var b in map.listerThings.AllThings)
            {
                if (b.def.building == null || !b.def.building.isResourceRock || b.def.building.mineableThing == null) continue;
                var c = b.Position;
                if (map.fogGrid.IsFogged(c)) continue;
                if (Math.Abs(c.x - home[0]) > BuildView.OreRadius || Math.Abs(c.z - home[1]) > BuildView.OreRadius) continue;
                cells.Add(new OreCell(b.def.building.mineableThing.label, c.x, c.z));
            }
            return new JArray(BuildView.GroupOres(cells, home[0], home[1]).Select(g => new JObject { ["ore"] = g.Ore, ["cells"] = g.Cells, ["dist"] = g.Dist }));
        }
    }
}

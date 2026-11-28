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
        const int MaxRooms = 12, MaxBenches = 8, MaxBills = 5, MaxRecipes = 8, MaxZones = 10, MaxStuff = 8, MaxPlants = 14, MaxFloors = 8;
        static readonly HashSet<string> logged = new HashSet<string>();

        // The BASE line is worked out from the live map, then kept until the colony's buildings, blueprints or the hour change
        // (planning a few rooms on a big window costs tens of milliseconds; the digest is read every steward cycle).
        static string baseKey, baseLine;

        static string BaseLine(Map map)
        {
            string key = map.uniqueID + "/" + map.listerBuildings.allBuildingsColonist.Count + "/" +
                         map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count + "/" +
                         map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count + "/" + GenTicks.TicksGame / 2500;
            if (key == baseKey && baseLine != null) return baseLine;
            baseLine = RimWorldLLM.Bridge.BasePlanner.Describe(VivariumBaseRead.Read(map));
            baseKey = key;
            return baseLine;
        }

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
                    ["impressiveness"] = (int)Math.Round(r.Imp), ["bed"] = r.Bed, ["door"] = r.Door, ["roofed"] = r.Roofed, ["x"] = r.X, ["z"] = r.Z,
                    ["temp_c"] = r.TempC, ["mountain"] = r.Mountain, ["dirty"] = r.Dirty
                }));

            Part(o, "base", () => BaseLine(map));
            Part(o, "missing", () => new JArray(Missing(map, rooms)));
            Part(o, "benches", () => Benches(map));
            Part(o, "zones", () => Zones(map));
            Part(o, "can_build", () => new JArray(CanBuildWords()));
            Part(o, "stuff", () => Stuff(map));
            Part(o, "plants", () => Plants(map));
            Part(o, "floors", () => new JArray(RimWorldLLM.Bridge.FloorsPure.DigestWords(VivariumHands.Floors(), VivariumHands.StockOfMap(map), MaxFloors)));
            if (home != null) Part(o, "ores", () => Ores(map, home));
            if (home != null) Part(o, "loose", () => Loose(map, home));
            return o;
        }

        class RoomInfo
        {
            public int Id, W, H, Cells, X, Z;
            public string Role, Label;
            public float Imp;
            public bool Bed, Door, Roofed, Bedroom, Mountain, Dirty;
            public int TempC;
            public HashSet<Pawn> Sleepers = new HashSet<Pawn>();
        }

        internal static int[] HomeOf(Map map) { try { return Home(map); } catch (Exception) { return null; } }

        internal static int[] Home(Map map)
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
                try
                {
                    info.TempC = (int)Math.Round(r.Temperature);
                    info.Dirty = NarratorExtras.IsDirty(r.GetStat(RoomStatDefOf.Cleanliness));
                    int look = 0;
                    foreach (var c in cells) { if (++look > 12) break; var roof = map.roofGrid.RoofAt(c); if (roof != null && roof.isThickRoof) { info.Mountain = true; break; } }
                }
                catch (Exception e) { if (logged.Add("room_extras")) Log.Warning("[RimWorldLLM] digest room extras failed: " + e.Message); }
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
                    if (plant != null) o["plant"] = RimWorldLLM.Bridge.ZonesPure.CropWord(plant.defName, plant.label);
                    arr.Add(o);
                }
                else if (s != null)
                    arr.Add(new JObject { ["id"] = z.ID, ["kind"] = "stockpile", ["label"] = z.label, ["cells"] = z.Cells.Count, ["preset"] = Preset(s), ["priority"] = s.GetStoreSettings().Priority.ToString().ToLowerInvariant() });
            }
            var basins = VivariumHands.HydroBasins(map);
            if (basins.Count > 0)
            {
                var hp = basins[0].GetPlantDefToGrow();
                var ho = new JObject { ["kind"] = "hydroponics", ["label"] = "hydroponics", ["cells"] = basins.Count };
                if (hp != null) ho["plant"] = RimWorldLLM.Bridge.ZonesPure.CropWord(hp.defName, hp.label);
                arr.Add(ho);
            }
            return arr;
        }

        internal static string Preset(Zone_Stockpile s)
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
            // every sowable, researched crop (labels as the model says them), food crops first, then the aliased ones
            var words = new List<string>();
            var rows = VivariumHands.Crops().Where(c => c.Ground && c.Research == null).Select(c =>
            {
                var d = DefDatabase<ThingDef>.GetNamedSilentFail(c.Def);
                bool food = d != null && d.plant != null && d.plant.harvestedThingDef != null && d.plant.harvestedThingDef.IsNutritionGivingIngestible;
                string w = RimWorldLLM.Bridge.ZonesPure.CropWord(c.Def, c.Label);
                bool alias = RimWorldLLM.Bridge.BuildPure.PlantAliases.ContainsKey(w);
                return new { w, rank = (food ? 0 : 2) + (alias ? 0 : 1) };
            }).OrderBy(r => r.rank).ThenBy(r => r.w, StringComparer.Ordinal);
            foreach (var r in rows) { if (words.Count >= MaxPlants) break; if (!words.Contains(r.w)) words.Add(r.w); }
            return new JArray(words);
        }

        static JToken Loose(Map map, int[] home)
        {
            return new JArray(VivariumStuck.LoosePiles(map, home).Take(BuildView.MaxPiles).Select(p =>
            {
                var o = new JObject { ["label"] = p.Label, ["count"] = p.Count, ["dist"] = p.Dist, ["dir"] = p.Dir, ["forbidden"] = p.Forbidden };
                if (p.Store != null) o["store"] = p.Store;
                return o;
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

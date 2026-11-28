using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>inspect(target): resolves a name / #id / room label on the current map and reads one thing into the pure
    /// fact classes (Bridge/InspectPure.cs). Read only, game thread. The pawn facts live in VivariumInspectPawn.cs.</summary>
    public sealed partial class VivariumInspectHands
    {
        const int AreaIdBase = 1000000;      // area candidates share one id list with zones

        static Map InspectMap()
        {
            var m = Verse.Find.CurrentMap;
            if (m != null && m.IsPlayerHome) return m;
            return Verse.Find.Maps.FirstOrDefault(x => x.IsPlayerHome) ?? m;
        }

        /// <summary>Distance, compass key and room label of a cell relative to the base centre (the one combat uses).</summary>
        static void Rel(Map map, IntVec3 pos, out int dist, out string dirKey, out string room)
        {
            var home = VivariumHands.HomeCenterOf(map);
            dist = (int)Math.Round(Math.Sqrt((double)(pos.x - home.x) * (pos.x - home.x) + (double)(pos.z - home.z) * (pos.z - home.z)));
            dirKey = dist <= 2 ? "" : Approach.CompassKey(home.x, home.z, pos.x, pos.z);
            room = "";
            try { room = RoomLabel(pos.GetRoom(map)); } catch (Exception) { }
        }

        /// <summary>"the kitchen", "Mara's bedroom"; "" outdoors / no role.</summary>
        static string RoomLabel(Room r)
        {
            if (r == null || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None) return "";
            string role = (r.Role.label ?? r.Role.defName).ToLowerInvariant();
            if (r.Role == RoomRoleDefOf.Bedroom)
            {
                var owners = r.Owners.Where(p => p != null && p.Name != null).Take(2).ToList();
                if (owners.Count == 1) return owners[0].Name.ToStringShort + "'s bedroom";
            }
            return "the " + role;
        }

        /// <summary>True for a spawned thing standing in fog of war: the narrator must not learn of it.</summary>
        static bool Hidden(Thing t)
        {
            try { return t != null && t.Spawned && t.Fogged(); } catch (Exception) { return false; }
        }

        static bool HiddenRoom(Room r)
        {
            try { return r.Fogged; } catch (Exception) { return false; }
        }

        static string RoomDigestName(Room r)
        {
            string l = RoomLabel(r);
            return l.StartsWith("the ") ? l.Substring(4) : l;
        }

        static List<Room> InspectRooms(Map map)
        {
            var list = new List<Room>();
            var seen = new HashSet<Room>();
            foreach (var region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
            {
                var r = region.Room;
                if (r == null || !seen.Add(r) || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None) continue;
                if (r.CellCount < 4 || HiddenRoom(r)) continue;
                list.Add(r);
            }
            return list;
        }

        sealed class Group
        {
            public string Kind;
            public List<(string name, int id)> Items = new List<(string name, int id)>();
            public Func<int, JObject> Read;
        }

        static int TierRank(string t) { return t == "id" ? 4 : t == "exact" ? 3 : t == "prefix" ? 2 : t == "substring" ? 1 : 0; }

        JObject InspectImpl(string target)
        {
            string q = (target ?? "").Trim();
            if (q == "") return Refused("inspect what? give a colonist name, room label, or #id from find");
            try
            {
                var map = InspectMap();
                if (map == null) return Refused("could not read the map");
                return Resolve(map, q);
            }
            catch (Exception e)
            {
                return Refused("could not read " + q + " (" + e.GetType().Name + ")");
            }
        }

        JObject Resolve(Map map, string q)
        {
            string lower = q.ToLowerInvariant();
            int num;
            // "zone N" / "area N" (ids printed by the formatter)
            if ((lower.StartsWith("zone ") || lower.StartsWith("area ")) && int.TryParse(lower.Substring(5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out num))
            {
                var zr = lower.StartsWith("zone ") ? ReadZone(map, num) : ReadZone(map, AreaIdBase + num);
                if (zr != null) return zr;
            }
            if (lower.StartsWith("room ") && int.TryParse(lower.Substring(5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out num))
            {
                var rr = ReadRoom(map, num);
                if (rr != null) return rr;
                return Refused("no room " + num + "; say a room label like \"the kitchen\"");
            }
            if (InspectNames.TryId(q, out num) && !lower.StartsWith("room "))
            {
                var p = FindPawnId(map, num);
                if (p != null) return ReadPawn(map, p);
                var t = FindThingId(map, num);
                if (t != null) return ReadThing(map, t);
                return Refused("nothing with #" + num + "; try a colonist name, room label, or #id from find");
            }

            var groups = new List<Group>();
            groups.Add(PawnGroup(map));
            groups.Add(RoomGroup(map));
            groups.Add(ZoneGroup(map));
            groups.Add(BuildingGroup(map));
            groups.Add(StackGroup(map));

            Group best = null; NameMatch bestMatch = null; int bestRank = 0;
            foreach (var g in groups)
            {
                var m = InspectNames.MatchName(q, g.Items);
                int rank = TierRank(m.Tier);
                if (m.Matches.Count > 0 && rank > bestRank) { best = g; bestMatch = m; bestRank = rank; }
            }
            if (best == null) return Refused(NotFound(map, q));
            if (bestMatch.Single) return best.Read(bestMatch.Matches[0].id) ?? Refused("could not read " + q);
            return Refused(AmbiguityText(map, q, best, bestMatch));
        }

        static string NotFound(Map map, string q)
        {
            string names = "";
            try { names = string.Join(", ", map.mapPawns.FreeColonistsSpawned.Take(5).Select(p => p.LabelShort)); } catch (Exception) { }
            return "nothing called " + q + "; try a colonist name, room label, or #id from find" + (names != "" ? " (colonists: " + names + ")" : "");
        }

        static string AmbiguityText(Map map, string q, Group g, NameMatch m)
        {
            if (g.Kind == "room" || g.Kind == "zone")
            {
                var show = m.Matches.Take(6).Select(c => c.name + " (" + (g.Kind == "room" ? "room " + c.id : c.id >= AreaIdBase ? "area " + (c.id - AreaIdBase) : "zone " + c.id) + ")").ToList();
                return InspectText.Cap(m.Matches.Count + " match \"" + q + "\": " + string.Join(", ", show) + ". say the " + g.Kind + " number");
            }
            var cands = m.Matches.Select(c => (c.name, c.id, RoomOfId(map, g.Kind, c.id))).ToList();
            return InspectFind.Ambiguity(q, cands);
        }

        static string RoomOfId(Map map, string kind, int id)
        {
            try
            {
                Thing t = kind == "pawn" ? (Thing)FindPawnId(map, id) : FindThingId(map, id);
                if (t != null && t.Spawned) return RoomLabel(t.GetRoom());
            }
            catch (Exception) { }
            return "";
        }

        // ------------------------------------------------------------------ lookups
        static Pawn FindPawnId(Map map, int id)
        {
            // only pawns on this map that are not in fog; unspawned ones (carried, in a bed pod) only when they are the colony's
            foreach (var p in map.mapPawns.AllPawns)
                if (p.thingIDNumber == id)
                {
                    if (Hidden(p)) return null;
                    if (!p.Spawned && p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony) return null;
                    return p;
                }
            return null;
        }

        static Thing FindThingId(Map map, int id)
        {
            foreach (var t in map.listerThings.AllThings) if (t.thingIDNumber == id) return Hidden(t) ? null : t;
            foreach (var p in map.mapPawns.PawnsInFaction(Faction.OfPlayer))
            {
                if (p.inventory != null) foreach (var t in p.inventory.innerContainer) if (t.thingIDNumber == id) return t;
                if (p.equipment != null) foreach (var t in p.equipment.AllEquipmentListForReading) if (t.thingIDNumber == id) return t;
                if (p.apparel != null) foreach (var t in p.apparel.WornApparel) if (t.thingIDNumber == id) return t;
            }
            return null;
        }

        // ------------------------------------------------------------------ candidate groups
        Group PawnGroup(Map map)
        {
            var g = new Group { Kind = "pawn" };
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || Hidden(p)) continue;
                string n = p.LabelShort;
                if (!string.IsNullOrEmpty(n)) g.Items.Add((n, p.thingIDNumber));
            }
            g.Read = id => { var p = FindPawnId(map, id); return p == null ? null : ReadPawn(map, p); };
            return g;
        }

        Group RoomGroup(Map map)
        {
            var g = new Group { Kind = "room" };
            foreach (var r in InspectRooms(map)) g.Items.Add((RoomDigestName(r), r.ID));
            g.Read = id => ReadRoom(map, id);
            return g;
        }

        Group ZoneGroup(Map map)
        {
            var g = new Group { Kind = "zone" };
            foreach (var z in map.zoneManager.AllZones) if (!string.IsNullOrEmpty(z.label)) g.Items.Add((z.label, z.ID));
            foreach (var a in map.areaManager.AllAreas) if (!string.IsNullOrEmpty(a.Label)) g.Items.Add((a.Label, AreaIdBase + a.ID));
            g.Read = id => ReadZone(map, id);
            return g;
        }

        Group BuildingGroup(Map map)
        {
            var g = new Group { Kind = "building" };
            foreach (var b in map.listerBuildings.allBuildingsColonist) g.Items.Add((b.LabelNoCount, b.thingIDNumber));
            g.Read = id => { var t = FindThingId(map, id); return t == null ? null : ReadThing(map, t); };
            return g;
        }

        Group StackGroup(Map map)
        {
            var g = new Group { Kind = "stack" };
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)) if (!Hidden(t)) g.Items.Add((t.LabelNoCount, t.thingIDNumber));
            g.Read = id => { var t = FindThingId(map, id); return t == null ? null : ReadThing(map, t); };
            return g;
        }

        // ------------------------------------------------------------------ things
        JObject ReadThing(Map map, Thing t)
        {
            var pawn = t as Pawn;
            if (pawn != null) return ReadPawn(map, pawn);
            try
            {
                if (t.def.category == ThingCategory.Building) return ReadBuilding(map, t);
                if (t.def.category == ThingCategory.Item) return ReadStack(map, t);
                return ReadBuilding(map, t);    // plants, filth and the rest: a plain one-line read
            }
            catch (Exception e)
            {
                return Refused("could not read " + t.LabelNoCount + " (" + e.GetType().Name + ")");
            }
        }

        static bool Forbidden(Thing t) { try { return t.IsForbidden(Faction.OfPlayer); } catch (Exception) { return false; } }

        static int HpPct(Thing t) { return t.MaxHitPoints > 0 ? (int)Math.Round(100.0 * t.HitPoints / t.MaxHitPoints) : 100; }

        JObject ReadStack(Map map, Thing t)
        {
            var f = new StackFacts { Label = t.LabelNoCount, Id = t.thingIDNumber, Count = Math.Max(1, t.stackCount), Forbidden = Forbidden(t) };
            f.HpPct = HpPct(t);
            QualityCategory qc;
            if (t.TryGetQuality(out qc)) f.Quality = qc.GetLabel().ToLowerInvariant();
            var rot = t.TryGetComp<CompRottable>();
            if (rot != null && rot.Stage == RotStage.Fresh)
            {
                int ticks = rot.TicksUntilRotAtCurrentTemp;
                if (ticks > 0 && ticks < 60 * 60000) f.RotDays = ticks / 60000.0;
            }
            if (t.Spawned)
            {
                int d; string k, room;
                Rel(map, t.Position, out d, out k, out room);
                f.Dist = d; f.DirKey = k; f.Room = room;
            }
            else
            {
                var holder = ThingOwnerUtility.GetFirstSpawnedParentThing(t) as Pawn;
                var ipawn = holder ?? (t.ParentHolder as Pawn_InventoryTracker)?.pawn ?? (t.ParentHolder as Pawn_ApparelTracker)?.pawn ?? (t.ParentHolder as Pawn_EquipmentTracker)?.pawn;
                f.Holder = ipawn != null ? ipawn.LabelShort : "someone";
            }
            return Text(StackFacts.Format(f));
        }

        JObject ReadBuilding(Map map, Thing t)
        {
            var f = new BuildingFacts { Label = t.LabelNoCount, Id = t.thingIDNumber, HpPct = HpPct(t), Forbidden = Forbidden(t) };
            var tw = t as ThingWithComps;
            if (tw != null)
            {
                var trader = tw.GetComp<CompPowerTrader>();
                if (trader != null)
                {
                    f.HasPower = true; f.PowerOn = trader.PowerOn; f.NetWatts = (int)Math.Round(trader.PowerOutput);
                    try
                    {
                        var net = trader.PowerNet;
                        if (net != null)
                        {
                            int gain = (int)Math.Round(net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick);
                            f.Note = "grid net " + InspectText.Sign(gain) + " W, stored " + (int)Math.Round(net.CurrentStoredEnergy()) + " Wd";
                        }
                    }
                    catch (Exception) { }
                }
                else
                {
                    var batt = tw.GetComp<CompPowerBattery>();
                    if (batt != null) { f.HasPower = true; f.PowerOn = true; f.Note = "battery " + InspectText.Pct(batt.StoredEnergyPct); }
                }
                var fuel = tw.GetComp<CompRefuelable>();
                if (fuel != null) f.FuelPct = (int)Math.Round(fuel.FuelPercentOfMax * 100);
                var bd = tw.GetComp<CompBreakdownable>();
                if (bd != null && bd.BrokenDown) f.BrokenDown = true;
            }
            var plant = t as Plant;
            if (plant != null)
                f.Note = InspectText.Join(new[] { f.Note, "growth " + InspectText.Pct(plant.Growth), plant.HarvestableNow ? "ready to harvest" : "" }, ", ");
            var giver = t as IBillGiver;
            if (giver != null && giver.BillStack != null)
                foreach (var b in giver.BillStack.Bills.Take(5)) f.Bills.Add(BillWords(b));
            var bed = t as Building_Bed;
            if (bed != null)
            {
                var ow = bed.OwnersForReading.Where(p => p != null).Select(p => p.LabelShort).ToList();
                f.Owner = string.Join(" and ", ow);
                if (ow.Count == 0 && !bed.Medical && !bed.ForPrisoners) f.Note = InspectText.Join(new[] { f.Note, "unowned" }, ", ");
                if (bed.Medical) f.Note = InspectText.Join(new[] { f.Note, "medical bed" }, ", ");
                var sl = bed.CurOccupants.Select(p => p.LabelShort).ToList();
                if (sl.Count > 0) f.Note = InspectText.Join(new[] { f.Note, "occupied by " + string.Join(", ", sl) }, ", ");
            }
            var store = t as Building_Storage;
            if (store != null)
            {
                var held = new Dictionary<string, int>();
                foreach (var h in store.slotGroup.HeldThings)
                {
                    int c; held.TryGetValue(h.LabelNoCount, out c); held[h.LabelNoCount] = c + Math.Max(1, h.stackCount);
                }
                foreach (var kv in held.OrderByDescending(k => k.Value).Take(8)) f.Storage.Add(kv.Key + " x" + kv.Value);
                if (held.Count > 8) f.Storage.Add("+" + (held.Count - 8) + " more kinds");
                if (held.Count == 0) f.Note = InspectText.Join(new[] { f.Note, "empty" }, ", ");
            }
            else
            {
                var holder = t as IThingHolder;
                if (holder != null && t is Building_Casket)
                {
                    var inner = holder.GetDirectlyHeldThings();
                    if (inner != null)
                        foreach (var h in inner.Take(4)) f.Storage.Add(h.LabelNoCount + (h.stackCount > 1 ? " x" + h.stackCount : ""));
                }
            }
            if (t.Spawned) f.Room = RoomLabel(t.GetRoom());
            return Text(BuildingFacts.Format(f));
        }

        static string BillWords(Bill bill)
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

        // ------------------------------------------------------------------ rooms
        JObject ReadRoom(Map map, int id)
        {
            Room r = InspectRooms(map).FirstOrDefault(x => x.ID == id);
            if (r == null) return null;
            var f = new RoomFacts { Id = r.ID, Role = (r.Role.label ?? r.Role.defName).ToLowerInvariant(), Label = RoomLabel(r), Cells = r.CellCount };
            try
            {
                float imp = r.GetStat(RoomStatDefOf.Impressiveness);
                f.Impressiveness = Math.Round(imp, 0);
                var st = RoomStatDefOf.Impressiveness.GetScoreStage(imp);
                if (st != null) f.ImpressiveLabel = (st.label ?? "").ToLowerInvariant();
            }
            catch (Exception) { }
            try { f.Beauty = Math.Round(r.GetStat(RoomStatDefOf.Beauty), 1); } catch (Exception) { }
            try { f.Cleanliness = Math.Round(r.GetStat(RoomStatDefOf.Cleanliness), 1); } catch (Exception) { }
            try
            {
                var sp = RoomStatDefOf.Space.GetScoreStage(r.GetStat(RoomStatDefOf.Space));
                if (sp != null) f.SpaceLabel = (sp.label ?? "").ToLowerInvariant();
            }
            catch (Exception) { }
            try { f.Temperature = Math.Round(r.Temperature, 0); } catch (Exception) { }
            try
            {
                var bcount = new Dictionary<string, int>(); var bone = new Dictionary<string, int>(); var items = new Dictionary<string, int>();
                foreach (var t in r.ContainedAndAdjacentThings)
                {
                    if (t is Pawn || t is Building_Door || Hidden(t) || t.def.category == ThingCategory.Plant || t.def.category == ThingCategory.Filth) continue;
                    string n = t.LabelNoCount;
                    if (t.def.category == ThingCategory.Building)
                    {
                        if ((t.def.designationCategory != null && t.def.designationCategory.defName == "Structure") || t.def.building == null) continue;
                        int c; bcount.TryGetValue(n, out c); bcount[n] = c + 1; bone[n] = t.thingIDNumber;
                    }
                    else if (t.def.category == ThingCategory.Item)
                    {
                        int c; items.TryGetValue(n, out c); items[n] = c + Math.Max(1, t.stackCount);
                    }
                }
                foreach (var kv in bcount.OrderByDescending(k => k.Value))
                    f.Contents.Add(kv.Value == 1 ? kv.Key + " #" + bone[kv.Key] : kv.Value + " " + kv.Key + "s");
                foreach (var kv in items.OrderByDescending(k => k.Value).Take(4)) f.Contents.Add(kv.Key + " x" + kv.Value);
            }
            catch (Exception) { }
            try { f.Owners = r.Owners.Where(p => p != null).Select(p => p.LabelShort).Take(4).ToList(); } catch (Exception) { }
            return Text(RoomFacts.Format(f));
        }

        // ------------------------------------------------------------------ zones and areas
        JObject ReadZone(Map map, int id)
        {
            if (id >= AreaIdBase)
            {
                var a = map.areaManager.AllAreas.FirstOrDefault(x => x.ID == id - AreaIdBase);
                if (a == null) return null;
                return Text(ZoneFacts.Format(new ZoneFacts { Label = a.Label, Id = a.ID, Kind = "area", Cells = a.TrueCount }));
            }
            var z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == id);
            if (z == null) return null;
            var f = new ZoneFacts { Label = z.label, Id = z.ID, Kind = "other", Cells = z.Cells.Count };
            var g = z as Zone_Growing;
            var s = z as Zone_Stockpile;
            if (g != null)
            {
                f.Kind = "grow";
                var plant = g.GetPlantDefToGrow();
                if (plant != null) f.Crop = plant.label;
                double sum = 0; int n = 0;
                foreach (var c in z.Cells)
                {
                    var pl = c.GetPlant(map);
                    if (pl != null && plant != null && pl.def == plant) { sum += pl.Growth; n++; }
                }
                if (n > 0) f.GrowthPct = (int)Math.Round(100 * sum / n);
            }
            else if (s != null)
            {
                f.Kind = "stockpile";
                f.Priority = s.GetStoreSettings().Priority.ToString().ToLowerInvariant();
                var held = new Dictionary<string, int>();
                foreach (var h in s.slotGroup.HeldThings)
                {
                    int c; held.TryGetValue(h.LabelNoCount, out c); held[h.LabelNoCount] = c + Math.Max(1, h.stackCount);
                }
                foreach (var kv in held.OrderByDescending(k => k.Value).Take(8)) f.Contents.Add(kv.Key + " x" + kv.Value);
                if (held.Count > 8) f.Contents.Add("+" + (held.Count - 8) + " more kinds");
            }
            return Text(ZoneFacts.Format(f));
        }
    }
}

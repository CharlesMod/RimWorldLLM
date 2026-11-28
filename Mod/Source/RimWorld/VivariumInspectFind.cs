using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;

namespace RimWorldLLM
{
    /// <summary>find(what, near, item) and survey(what) bodies. Read only, game thread, no coordinates in output.</summary>
    public sealed partial class VivariumInspectHands
    {
        sealed class Cand
        {
            public string Label = ""; public int Count = 1, Id; public IntVec3 Cell; public Map Map; public ThingDef Def;
        }

        // ---------------------------------------------------------------- shared helpers
        static Map HomeMapI()
        {
            var m = Verse.Find.CurrentMap;
            if (m != null && m.IsPlayerHome) return m;
            return Verse.Find.Maps.FirstOrDefault(x => x.IsPlayerHome);
        }

        static bool IsMealDef(ThingDef d)
        {
            var p = d.ingestible != null ? d.ingestible.preferability : FoodPreferability.Undefined;
            return p == FoodPreferability.MealAwful || p == FoodPreferability.MealSimple || p == FoodPreferability.MealFine || p == FoodPreferability.MealLavish;
        }

        static string RoomLabelOf(Room r)
        {
            if (r == null || r.PsychologicallyOutdoors) return "";
            if (r.Role == null || r.Role == RoomRoleDefOf.None) return "a room";
            if (r.Role == RoomRoleDefOf.Bedroom)
            {
                var owners = new HashSet<Pawn>();
                foreach (var bed in r.ContainedBeds) foreach (var p in bed.OwnersForReading) owners.Add(p);
                var one = owners.Where(p => p != null && p.Name != null).ToList();
                if (one.Count == 1) return one[0].Name.ToStringShort + "'s bedroom";
            }
            return "the " + (r.Role.label ?? r.Role.defName).ToLowerInvariant();
        }

        static string RoomLabelAt(Map map, IntVec3 c)
        {
            try { return c.InBounds(map) ? RoomLabelOf(c.GetRoom(map)) : ""; }
            catch (Exception) { return ""; }
        }

        static int DistCells(IntVec3 a, IntVec3 b)
        {
            double dx = a.x - b.x, dz = a.z - b.z;
            return (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
        }

        static string NameOf(Pawn p) { return p.LabelShort; }

        static bool Mine(Pawn p) { return p.Faction == Faction.OfPlayer; }

        static bool Injured(Pawn p)
        {
            var hs = p.health != null ? p.health.hediffSet : null;
            if (hs == null) return false;
            if (hs.BleedRateTotal > 0.01f) return true;
            foreach (var h in hs.hediffs) if (h is Hediff_Injury && h.Visible) return true;
            return false;
        }

        static bool Bleeding(Pawn p) { return p.health != null && p.health.hediffSet != null && p.health.hediffSet.BleedRateTotal > 0.01f; }

        static string SickLabel(Pawn p)
        {
            var hs = p.health != null ? p.health.hediffSet : null;
            if (hs == null) return null;
            Hediff best = null;
            foreach (var h in hs.hediffs)
            {
                if (h is Hediff_Injury || h is Hediff_MissingPart || !h.Visible) continue;
                if (!(h.def.makesSickThought || h.def.lethalSeverity > 0f)) continue;
                if (best == null || h.Severity > best.Severity) best = h;
            }
            return best != null ? best.LabelCap : null;
        }

        static bool IdleJob(Pawn p)
        {
            if (p.Drafted) return false;
            var j = p.CurJob;
            if (j == null) return true;
            string n = j.def.defName;
            return n.StartsWith("Wait") || n == "GotoWander" || n == "Wait_Wander" || n == "Wait_MaintainPosture";
        }

        /// <summary>Pawn that "near" names: a pawn name, a room label ("the kitchen", "room 12") or "#id". Null cell with a refusal when unknown.</summary>
        static bool ResolveNear(Map map, string near, IntVec3 home, out IntVec3 origin, out string label, out string refused)
        {
            origin = home; label = ""; refused = null;
            if (string.IsNullOrWhiteSpace(near)) return true;
            string q = near.Trim();
            var pawns = map.mapPawns.AllPawnsSpawned.Where(p => (p.Name != null || p.RaceProps.Animal) && !p.Fogged()).ToList();
            var pm = InspectNames.MatchName(q, pawns.Select(p => (NameOf(p), p.thingIDNumber)).ToList());
            if (pm.Single)
            {
                var p = pawns.First(x => x.thingIDNumber == pm.Matches[0].id);
                origin = p.Position; label = NameOf(p); return true;
            }
            int id;
            if (InspectNames.TryId(q, out id) && !q.ToLowerInvariant().StartsWith("room"))
            {
                var t = map.listerThings.AllThings.FirstOrDefault(x => x.thingIDNumber == id && x.Spawned && !x.Fogged());
                if (t != null) { origin = t.Position; label = "#" + id; return true; }
            }
            var rooms = new List<(string name, int id)>(); var cells = new Dictionary<int, IntVec3>();
            var seen = new HashSet<Room>();
            foreach (var region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
            {
                var r = region.Room;
                if (r == null || !seen.Add(r) || r.PsychologicallyOutdoors || r.CellCount < 4 || r.Fogged) continue;
                string l = RoomLabelOf(r);
                if (l == "") continue;
                rooms.Add((l, r.ID));
                cells[r.ID] = r.Cells.ElementAt(r.CellCount / 2);
            }
            var rm = InspectNames.MatchName(q, rooms);
            if (rm.Matches.Count >= 1)
            {
                var best = rm.Matches.OrderBy(m => DistCells(cells[m.id], home)).First();
                origin = cells[best.id]; label = best.name; return true;
            }
            var names = map.mapPawns.FreeColonistsSpawned.Select(NameOf).Take(8);
            refused = "no pawn, room or #id matches \"" + q + "\" to measure from; try a colonist name (" + string.Join(", ", names) + ") or leave near empty";
            return false;
        }

        // ---------------------------------------------------------------- find
        JObject FindImpl(string what, string near, string item)
        {
            FindKind kind;
            if (!FindKinds.Parse(what, out kind))
                return Refused("unknown what \"" + what + "\"; use one of: " + FindKinds.List());
            try
            {
                var map = HomeMapI();
                if (map == null) return Refused("no colony map to search");
                bool needItem = kind == FindKind.Item || kind == FindKind.Building;
                if (needItem && string.IsNullOrWhiteSpace(item)) return Refused("say what with item=steel");
                var home = VivariumHands.HomeCenterOf(map);
                IntVec3 origin; string nearLabel, why;
                if (!ResolveNear(map, near, home, out origin, out nearLabel, out why)) return Refused(why);

                List<Cand> cands;
                try { cands = Gather(map, kind, item == null ? "" : item.Trim()); }
                catch (Exception e) { return Refused("could not read " + FindKinds.Label(kind, item) + " (" + e.GetType().Name + ")"); }

                cands = cands.OrderBy(c => DistCells(c.Cell, origin)).ThenBy(c => c.Id).ToList();
                var rows = new List<FindRow>();
                foreach (var c in cands.Take(InspectFind.MaxRows))
                {
                    int d = DistCells(c.Cell, home);
                    rows.Add(new FindRow(c.Label, c.Count, c.Id, d, d <= 2 ? "" : Approach.CompassKey(home.x, home.z, c.Cell.x, c.Cell.z), RoomLabelAt(map, c.Cell)));
                }
                string label = FindKinds.Label(kind, item) + (nearLabel != "" ? " nearest " + nearLabel : "");
                var ids = new JArray(rows.Where(r => r.Id > 0).Select(r => (object)r.Id).ToArray());
                var o = Text(InspectFind.FormatFind(label, rows, cands.Count));
                o["ids"] = ids; o["total"] = cands.Count;
                return o;
            }
            catch (Exception e) { return Refused("could not read " + FindKinds.Label(kind, item) + " (" + e.GetType().Name + ")"); }
        }

        static Cand Of(Thing t, string label, int count = 1) { return new Cand { Label = label, Count = count, Id = t.thingIDNumber, Cell = t.Position, Map = t.Map, Def = t.def }; }

        static bool LabelHit(Thing t, string q)
        {
            string l = t.def.label ?? t.def.defName;
            return l.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.LabelNoCount.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<Pawn> Spawned(Map map) { return map.mapPawns.AllPawnsSpawned.Where(p => p.Spawned && !p.Dead); }

        static List<Cand> Gather(Map map, FindKind kind, string item)
        {
            var l = new List<Cand>();
            switch (kind)
            {
                case FindKind.Item:
                {
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                    {
                        if (t == null || !t.Spawned || t.Fogged() || !LabelHit(t, item)) continue;
                        l.Add(Of(t, t.def.LabelCap.RawText ?? t.def.defName, t.stackCount));
                    }
                    if (l.Count > InspectFind.MaxRows)
                    {
                        // many stacks: one row per def, count summed, placed at the stack nearest home
                        var home = VivariumHands.HomeCenterOf(map);
                        l = l.GroupBy(c => c.Def).Select(g =>
                        {
                            var near = g.OrderBy(c => DistCells(c.Cell, home)).First();
                            return new Cand { Label = near.Label, Count = g.Sum(c => c.Count), Id = near.Id, Cell = near.Cell, Map = map, Def = g.Key };
                        }).ToList();
                    }
                    break;
                }
                case FindKind.Forbidden:
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                        if (t != null && t.Spawned && !t.Fogged() && t.IsForbidden(Faction.OfPlayer)) l.Add(Of(t, t.def.LabelCap.RawText ?? t.def.defName, t.stackCount));
                    break;
                case FindKind.Wounded:
                    foreach (var p in Spawned(map))
                        if ((Mine(p) || p.IsPrisonerOfColony) && Injured(p)) l.Add(Of(p, NameOf(p) + (Bleeding(p) ? " (bleeding)" : "") + (p.IsPrisonerOfColony ? " (prisoner)" : p.RaceProps.Animal ? " (animal)" : "")));
                    break;
                case FindKind.Idle:
                    foreach (var p in map.mapPawns.FreeColonistsSpawned)
                        if (!p.Downed && !p.InMentalState && IdleJob(p)) l.Add(Of(p, NameOf(p)));
                    break;
                case FindKind.Hostiles:
                    foreach (var p in Spawned(map))
                        if (VivariumHands.IsHostile(p) && !VivariumHands.Dormant(p)) l.Add(Of(p, p.KindLabel + (p.Downed ? " (downed)" : "")));
                    break;
                case FindKind.Fires:
                    foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
                        if (t.Spawned && !t.Fogged()) { var c = Of(t, "Fire"); c.Id = 0; l.Add(c); }
                    break;
                case FindKind.Corpses:
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                    {
                        var c = t as Corpse;
                        if (c == null || !c.Spawned || c.Fogged()) continue;
                        string stage = c.GetRotStage() == RotStage.Fresh ? "fresh" : c.GetRotStage() == RotStage.Rotting ? "rotting" : "dessicated";
                        l.Add(Of(c, (c.InnerPawn != null ? c.InnerPawn.LabelShort : "corpse") + " corpse (" + stage + ")"));
                    }
                    break;
                case FindKind.BedFree:
                    foreach (var b in map.listerBuildings.allBuildingsColonist)
                    {
                        var bed = b as Building_Bed;
                        if (bed == null || bed.ForPrisoners || bed.Medical || bed.ForSlaves || !bed.def.building.bed_humanlike) continue;
                        if (bed.OwnersForReading.Count == 0) l.Add(Of(bed, bed.LabelNoCount));
                    }
                    break;
                case FindKind.DoorOut:
                    foreach (var b in map.listerBuildings.allBuildingsColonist)
                    {
                        var d = b as Building_Door;
                        if (d == null) continue;
                        bool outdoor = false; IntVec3 inside = IntVec3.Invalid;
                        foreach (var dir in GenAdj.CardinalDirections)
                        {
                            var n = d.Position + dir;
                            if (!n.InBounds(map) || !n.Walkable(map)) continue;
                            var r = n.GetRoom(map);
                            if (r == null) continue;
                            if (r.PsychologicallyOutdoors) outdoor = true; else inside = n;
                        }
                        if (outdoor && inside.IsValid) { var c = Of(d, d.LabelNoCount); c.Cell = inside; l.Add(c); }
                    }
                    break;
                case FindKind.Tameable:
                    foreach (var p in Spawned(map))
                    {
                        if (!p.RaceProps.Animal || p.Faction != null || p.Fogged() || p.RaceProps.IsMechanoid || p.HostileTo(Faction.OfPlayer)) continue;
                        l.Add(Of(p, p.KindLabel + " (wildness " + InspectText.Pct(p.GetStatValue(StatDefOf.Wildness)) + ")"));
                    }
                    break;
                case FindKind.Rotting:
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                    {
                        if (t == null || !t.Spawned || t.Fogged() || t is Corpse || !t.def.IsIngestible) continue;
                        var rot = t.TryGetComp<CompRottable>();
                        if (rot == null) continue;
                        string state = null;
                        if (rot.Stage != RotStage.Fresh) state = "spoiled";
                        else
                        {
                            float start = rot.PropsRot.TicksToRotStart;
                            if (start > 0f && rot.RotProgress / start >= 0.6f) state = InspectText.Pct(rot.RotProgress / start) + " rotted";
                        }
                        if (state != null) l.Add(Of(t, t.def.LabelCap.RawText + " (" + state + ")", t.stackCount));
                    }
                    break;
                case FindKind.Sick:
                    foreach (var p in Spawned(map))
                    {
                        if (!(Mine(p) || p.IsPrisonerOfColony)) continue;
                        string s = SickLabel(p);
                        if (s != null) l.Add(Of(p, NameOf(p) + " (" + s + ")"));
                    }
                    break;
                case FindKind.Downed:
                    foreach (var p in Spawned(map))
                        if (p.Downed && (Mine(p) || p.IsPrisonerOfColony || (p.RaceProps.Humanlike && !p.HostileTo(Faction.OfPlayer) && !p.Fogged())))
                            l.Add(Of(p, NameOf(p) + (p.IsPrisonerOfColony ? " (prisoner)" : p.RaceProps.Animal ? " (animal)" : "")));
                    break;
                case FindKind.Prisoners:
                    foreach (var p in map.mapPawns.PrisonersOfColonySpawned) l.Add(Of(p, NameOf(p)));
                    break;
                case FindKind.Animals:
                    foreach (var p in map.mapPawns.SpawnedColonyAnimals) l.Add(Of(p, NameOf(p) + (NameOf(p).Equals(p.KindLabel, StringComparison.OrdinalIgnoreCase) ? "" : " (" + p.KindLabel + ")")));
                    break;
                case FindKind.Guests:
                    foreach (var p in Spawned(map))
                        if (p.RaceProps.Humanlike && p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony && !p.HostileTo(Faction.OfPlayer) && !p.Fogged())
                            l.Add(Of(p, NameOf(p)));
                    break;
                case FindKind.Building:
                    foreach (var b in map.listerBuildings.allBuildingsColonist)
                        if (LabelHit(b, item)) l.Add(Of(b, b.LabelNoCount));
                    break;
            }
            return l;
        }

        // ---------------------------------------------------------------- survey
        JObject SurveyImpl(string what)
        {
            string w = (what ?? "").Trim().ToLowerInvariant();
            if (w != "stock" && w != "threats") return Refused("unknown what \"" + what + "\"; use stock or threats");
            try
            {
                var map = HomeMapI();
                if (map == null) return Refused("no colony map to survey");
                if (w == "stock")
                {
                    try { return Text(StockFacts.Format(ReadStock(map))); }
                    catch (Exception e) { return Refused("could not read stock (" + e.GetType().Name + ")"); }
                }
                try { return Text(ThreatFacts.Format(ReadThreats(map))); }
                catch (Exception e) { return Refused("could not read threats (" + e.GetType().Name + ")"); }
            }
            catch (Exception e) { return Refused("could not read " + w + " (" + e.GetType().Name + ")"); }
        }

        static readonly string[] KeyDefs = { "Steel", "WoodLog", "ComponentIndustrial", "Plasteel", "Silver", "Gold", "Uranium", "ComponentSpacer", "Chemfuel" };

        static StockFacts ReadStock(Map map)
        {
            var f = new StockFacts();
            var scan = VivariumLoose.Get(map);
            Func<ThingDef, StockRow> row = d => new StockRow(d.LabelCap.RawText ?? d.defName, map.resourceCounter.GetCount(d), scan.LooseOf(d));
            foreach (var n in KeyDefs)
            {
                var d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null) f.Rows.Add(row(d));
            }
            int medS = 0, medL = 0, meals = 0, mealsL = 0, raw = 0, rawL = 0;
            foreach (var d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.category != ThingCategory.Item) continue;
                if (d.IsMedicine) { medS += map.resourceCounter.GetCount(d); medL += scan.LooseOf(d); }
                else if (d.IsNutritionGivingIngestible && !d.IsDrug && d.ingestible != null && d.ingestible.HumanEdible)
                {
                    if (IsMealDef(d)) { meals += map.resourceCounter.GetCount(d); mealsL += scan.LooseOf(d); }
                    else { raw += map.resourceCounter.GetCount(d); rawL += scan.LooseOf(d); }
                }
            }
            f.Rows.Add(new StockRow("medicine", medS, medL));
            f.Rows.Add(new StockRow("meals", meals, mealsL));
            f.Rows.Add(new StockRow("raw food", raw, rawL));
            f.Medicine = medS + medL;
            var ci = DefDatabase<ThingDef>.GetNamedSilentFail("ComponentIndustrial"); var cs = DefDatabase<ThingDef>.GetNamedSilentFail("ComponentSpacer");
            f.Components = (ci != null ? map.resourceCounter.GetCount(ci) + scan.LooseOf(ci) : 0) + (cs != null ? map.resourceCounter.GetCount(cs) + scan.LooseOf(cs) : 0);
            f.Colonists = map.mapPawns.FreeColonistsSpawnedCount;
            f.Prisoners = map.mapPawns.PrisonersOfColonySpawnedCount;
            f.Injured = map.mapPawns.FreeColonistsSpawned.Count(Injured);
            f.FoodNutritionStored = map.resourceCounter.TotalHumanEdibleNutrition;
            return f;
        }

        static ThreatFacts ReadThreats(Map map)
        {
            var f = new ThreatFacts();
            var home = VivariumHands.HomeCenterOf(map);
            HashSet<int> dormant;
            var infos = VivariumHands.Hostiles(map, out dormant).Where(h => !dormant.Contains(h.Id)).ToList();
            var byId = new Dictionary<int, Pawn>();
            foreach (var p in Spawned(map)) byId[p.thingIDNumber] = p;
            var mine = map.mapPawns.FreeColonistsSpawned.Select(p => p.Position).ToList();
            mine.AddRange(map.listerBuildings.allBuildingsColonist.Where(b => b.def.building != null && b.def.building.IsTurret).Select(b => b.Position));
            var infoById = infos.ToDictionary(h => h.Id, h => h);
            foreach (var g in CombatWords.Group(infos, home.x, home.z))
            {
                bool attacking = false, man = false, mech = false, siege = false;
                foreach (var id in g.Ids)
                {
                    HostileInfo h; if (!infoById.TryGetValue(id, out h)) continue;
                    man |= h.Manhunter; mech |= h.Mech; siege |= h.Siege;
                    Pawn p;
                    if (!byId.TryGetValue(id, out p)) continue;
                    var j = p.CurJob;
                    if (j != null && (j.def == JobDefOf.AttackMelee || j.def == JobDefOf.AttackStatic)) attacking = true;
                    else if (!p.Downed && mine.Any(c => DistCells(c, p.Position) <= 5)) attacking = true;
                }
                f.Groups.Add(ThreatEntry.From(g, home.x, home.z, attacking, man, mech, siege));
            }

            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Where(t => t.Spawned && !t.Fogged()).ToList();
            f.Fires = fires.Count;
            if (fires.Count > 0)
            {
                var nf = fires.OrderBy(t => DistCells(t.Position, home)).First();
                f.NearestFireDist = DistCells(nf.Position, home);
                f.NearestFireDirKey = f.NearestFireDist <= 2 ? "" : Approach.CompassKey(home.x, home.z, nf.Position.x, nf.Position.z);
            }

            foreach (var c in map.gameConditionManager.ActiveConditions) f.Conditions.Add((c.Label ?? c.def.label ?? c.def.defName).ToLowerInvariant());

            var bugs = new List<IntVec3>();
            foreach (var p in Spawned(map)) if (p.RaceProps.Insect && VivariumHands.IsHostile(p)) bugs.Add(p.Position);
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Hive)) if (t.Spawned && !t.Fogged()) bugs.Add(t.Position);
            f.Insects = bugs.Count;
            if (bugs.Count > 0)
            {
                var nb = bugs.OrderBy(c => DistCells(c, home)).First();
                f.InsectDist = DistCells(nb, home);
                f.InsectDirKey = f.InsectDist <= 2 ? "" : Approach.CompassKey(home.x, home.z, nb.x, nb.z);
            }
            return f;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;

namespace RimWorldLLM
{
    /// <summary>Command tools, job ops (docs/VIVARIUM.md): a player right-click order on a colonist. Game thread.
    /// Every order goes through the job givers and checks the vanilla float menus use, as a forced ordered job.</summary>
    public sealed partial class VivariumHands
    {
        public HandResult Job(CmdArgs a)
        {
            string op = ((a == null ? null : a.Op) ?? "").Trim().ToLowerInvariant();
            string who = a == null ? "" : a.Who, target = a == null ? "" : a.Target;
            return Described(JobDo(op, who, target), () => CommandPure.JobWhat(op, who, target));
        }

        HandResult JobDo(string op, string who, string target)
        {
            try
            {
                HandResult no;
                var pawn = JobColonist(who, out no);
                if (pawn == null) return no;
                string t = (target ?? "").Trim();
                if (op == "stay") return JobStay(pawn);
                if (pawn.jobs == null) return HandResult.No(Name(pawn) + " cannot take orders");
                if (pawn.Downed) return HandResult.No(Name(pawn) + " is downed");
                if (pawn.InMentalState) return HandResult.No(Name(pawn) + " is in a mental break and cannot take orders");
                if (t.Length == 0) return HandResult.No("this order needs a target");
                switch (op)
                {
                    case "haul": return JobHaul(pawn, t);
                    case "rescue": return JobRescue(pawn, t);
                    case "tend": return JobTend(pawn, t);
                    case "clean": return JobClean(pawn, t);
                    case "repair": return JobScan(pawn, t, "repair", new[] { typeof(WorkGiver_Repair), typeof(WorkGiver_FixBrokenDownBuilding) }, "repair", "has nothing that needs repairing");
                    case "refuel": return JobScan(pawn, t, "refuel", new[] { typeof(WorkGiver_Refuel) }, "refuel", "does not need fuel (or has no refuelable part)");
                    case "equip": return JobEquip(pawn, t);
                    case "wear": return JobWear(pawn, t);
                    case "drop": return JobDrop(pawn, t);
                    case "consume": return JobConsume(pawn, t);
                    case "go": return JobGo(pawn, t);
                    default: return HandResult.No("no job op " + op);
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---------------------------------------------------------------- resolving
        static IEnumerable<Pawn> JobAllColonists()
        {
            foreach (var m in Find.Maps)
            {
                if (m.mapPawns == null) continue;
                foreach (var p in m.mapPawns.FreeColonistsSpawned) yield return p;
                foreach (var p in m.mapPawns.SlavesOfColonySpawned) yield return p;
            }
        }

        static bool JobNameIs(Pawn p, string s)
        {
            if (Eq(p.LabelShort, s)) return true;
            if (p.Name == null) return false;
            return Eq(p.Name.ToStringFull, s) || Eq(p.Name.ToStringShort, s);
        }

        static Pawn JobColonist(string who, out HandResult no)
        {
            no = null;
            var all = JobAllColonists().ToList();
            string w = (who ?? "").Trim();
            Func<string> list = () => all.Count == 0 ? "none" : string.Join(", ", all.Select(p => p.LabelShort).Take(15));
            if (w.Length == 0) { no = HandResult.No("this order needs a colonist (who); colonists: " + list()); return null; }
            Pawn found = null;
            if (w[0] == '#')
            {
                int id;
                if (int.TryParse(w.Substring(1), out id)) found = all.FirstOrDefault(p => p.thingIDNumber == id);
            }
            if (found == null) found = all.FirstOrDefault(p => JobNameIs(p, w));
            if (found == null)
            {
                var pre = all.Where(p => p.LabelShort.StartsWith(w, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pre.Count == 1) found = pre[0];
            }
            if (found == null) { no = HandResult.No("no colonist named " + w + "; colonists: " + list()); return null; }
            if (found.Dead || found.Destroyed) { no = HandResult.No(Name(found) + " is dead"); return null; }
            return found;
        }

        static string JobDesc(Thing t)
        {
            if (t is Pawn) return ((Pawn)t).LabelShort;
            return t.stackCount > 1 ? t.stackCount + " " + t.LabelNoCount : t.LabelNoCount;
        }

        static Thing JobThingById(Map map, string t)
        {
            if (t.Length < 2 || t[0] != '#') return null;
            int id;
            if (!int.TryParse(t.Substring(1), out id)) return null;
            return ThingById(map, id);
        }

        static bool JobLabelIs(Thing th, string s)
        {
            return Eq(th.LabelNoCount, s) || Eq(th.Label, s) || Eq(th.def.label, s) || Eq(th.def.defName, s) || Eq(th.LabelCap, s)
                || (th.def.label != null && th.def.label.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 && s.Length >= 3);
        }

        /// <summary>Things matching a "#id" or a label, nearest to the pawn first: spawned things on its map plus what it carries.</summary>
        static List<Thing> JobThings(Pawn pawn, string t, Func<Thing, bool> kind, bool includeCarried)
        {
            var map = pawn.Map;
            var list = new List<Thing>();
            var byId = JobThingById(map, t);
            if (byId != null) { list.Add(byId); return list; }
            if (t.Length > 0 && t[0] == '#')
            {
                int id;
                if (int.TryParse(t.Substring(1), out id))
                {
                    var held = JobHeld(pawn).FirstOrDefault(x => x.thingIDNumber == id);
                    if (held != null) list.Add(held);
                }
                return list;
            }
            if (includeCarried) list.AddRange(JobHeld(pawn).Where(x => kind(x) && JobLabelIs(x, t)));
            foreach (var th in map.listerThings.AllThings)
                if (th.Spawned && !(th is Pawn) && kind(th) && JobLabelIs(th, t)) list.Add(th);
            return list.OrderBy(x => x.Spawned ? (x.Position - pawn.Position).LengthHorizontalSquared : -1).ToList();
        }

        /// <summary>Everything the pawn has on it: inventory, equipment, worn apparel, the thing in its hands.</summary>
        static IEnumerable<Thing> JobHeld(Pawn pawn)
        {
            if (pawn.inventory != null) foreach (var x in pawn.inventory.innerContainer) yield return x;
            if (pawn.equipment != null) foreach (var x in pawn.equipment.AllEquipmentListForReading) yield return x;
            if (pawn.apparel != null) foreach (var x in pawn.apparel.WornApparel) yield return x;
            if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null) yield return pawn.carryTracker.CarriedThing;
        }

        /// <summary>The first candidate that passes check (returns null), else the first refusal; no candidates gives noneWord.</summary>
        static Thing JobPick(IEnumerable<Thing> cands, Func<Thing, string> check, string none, out HandResult no)
        {
            no = null;
            string first = null;
            foreach (var c in cands)
            {
                string why = check(c);
                if (why == null) return c;
                if (first == null) first = why;
            }
            no = HandResult.No(first ?? none);
            return null;
        }

        static string JobUnusable(Pawn pawn, Thing t, PathEndMode pe)
        {
            if (t.Destroyed) return JobDesc(t) + " is gone";
            if (!t.Spawned) return null;
            if (t.Map != pawn.Map) return JobDesc(t) + " is on another map";
            if (ForbidUtility.IsForbidden(t, pawn)) return JobDesc(t) + " is forbidden; allow it first";
            if (!pawn.CanReach(t, pe, Danger.Deadly)) return Name(pawn) + " cannot reach " + JobDesc(t);
            if (!pawn.CanReserve(t))
            {
                var by = t.Map.reservationManager.FirstRespectedReserver(t, pawn);
                return JobDesc(t) + " is reserved by " + (by != null ? by.LabelShort : "someone else") + "; try again later";
            }
            return null;
        }

        static string JobBusy(Pawn pawn, Job job)
        {
            var cur = pawn.CurJob;
            if (cur != null && cur.def == job.def && cur.targetA == job.targetA && cur.targetB == job.targetB && cur.playerForced)
                return Name(pawn) + " is already doing it";
            return null;
        }

        static HandResult JobTake(Pawn pawn, Job job, string note)
        {
            string busy = JobBusy(pawn, job);
            if (busy != null) return HandResult.No(busy);
            job.playerForced = true;
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return HandResult.No(Name(pawn) + " could not take the order (busy with something that cannot be interrupted)");
            return HandResult.Done(note);
        }

        static string JobWorkBlock(Pawn pawn, WorkTypeDef wt, string verb)
        {
            if (pawn.Drafted) return Name(pawn) + " is drafted; undraft first";
            if (wt != null && pawn.WorkTypeIsDisabled(wt)) return Name(pawn) + " is incapable of " + wt.labelShort + " and cannot " + verb;
            return null;
        }

        static List<Room> JobRooms(Map map, string t)
        {
            var rooms = new List<Room>();
            string s = t.Trim();
            int id = -1;
            if (s.StartsWith("room ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5).Trim();
            else if (s.StartsWith("#")) s = s.Substring(1);
            bool numeric = int.TryParse(s, out id);
            foreach (var r in map.regionGrid.AllRooms)
            {
                if (r == null || r.Dereferenced || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None) continue;
                if (numeric && r.ID == id) { rooms.Add(r); continue; }
                if (!numeric && Eq(JobRoomLabel(r), t)) rooms.Add(r);
            }
            if (rooms.Count == 0 && !numeric)
                foreach (var r in map.regionGrid.AllRooms)
                {
                    if (r == null || r.Dereferenced || r.PsychologicallyOutdoors || r.Role == null || r.Role == RoomRoleDefOf.None) continue;
                    if (Eq(r.Role.label, t) || Eq(r.Role.defName, t)) rooms.Add(r);
                }
            return rooms;
        }

        static string JobRoomLabel(Room r)
        {
            string role = (r.Role.label ?? r.Role.defName).ToLowerInvariant();
            var owners = new List<string>();
            foreach (var bed in r.ContainedBeds)
                foreach (var p in bed.OwnersForReading)
                    if (p != null && p.Name != null) owners.Add(p.Name.ToStringShort);
            return owners.Count == 1 && r.Role == RoomRoleDefOf.Bedroom ? owners[0] + "'s bedroom" : role;
        }

        static string JobRoomSay(Room r) { return "the " + JobRoomLabel(r); }

        static List<WorkGiver_Scanner> JobScanners(params Type[] types)
        {
            var res = new List<WorkGiver_Scanner>();
            foreach (var d in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                if (d.giverClass == null || !types.Any(ty => ty.IsAssignableFrom(d.giverClass))) continue;
                var s = d.Worker as WorkGiver_Scanner;
                if (s != null) res.Add(s);
            }
            return res;
        }

        /// <summary>Why this pawn cannot do the work giver at all (work type off, missing capacity), or null.</summary>
        static string JobGiverBlock(Pawn pawn, WorkGiver g, string verb)
        {
            var wt = g.def.workType;
            if (wt != null && pawn.WorkTypeIsDisabled(wt)) return Name(pawn) + " is incapable of " + wt.labelShort + " and cannot " + verb;
            var cap = g.MissingRequiredCapacity(pawn);
            if (cap != null) return Name(pawn) + " cannot " + verb + ": no " + cap.label;
            return null;
        }

        /// <summary>Runs the giver chain on a thing as a forced job. Returns the job, or sets why.</summary>
        static Job JobFromGivers(Pawn pawn, Thing t, List<WorkGiver_Scanner> givers, string verb, out string why)
        {
            why = null;
            foreach (var g in givers)
            {
                string blk = JobGiverBlock(pawn, g, verb);
                if (blk != null) { if (why == null) why = blk; continue; }
                Job j = null;
                try { if (g.HasJobOnThing(pawn, t, true)) j = g.JobOnThing(pawn, t, true); } catch (Exception) { }
                if (j != null) return j;
            }
            return null;
        }

        // ---------------------------------------------------------------- haul
        HandResult JobHaul(Pawn pawn, string t)
        {
            string blk = JobWorkBlock(pawn, WorkTypeDefOf.Hauling, "haul");
            if (blk != null) return HandResult.No(blk);
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return HandResult.No(Name(pawn) + " cannot haul: no working hands");
            HandResult no;
            var th = JobPick(JobThings(pawn, t, x => x.def.EverHaulable, false), x =>
            {
                if (!x.Spawned) return JobDesc(x) + " is not lying on the ground";
                if (!x.def.EverHaulable) return JobDesc(x) + " cannot be hauled";
                return JobUnusable(pawn, x, PathEndMode.ClosestTouch);
            }, "no hauling target " + t + " on the map (use #id from the digest)", out no);
            if (th == null) return no;
            if (!HaulAIUtility.PawnCanAutomaticallyHaul(pawn, th, true))
                return HandResult.No(Name(pawn) + " cannot haul " + JobDesc(th) + " right now");
            var job = HaulAIUtility.HaulToStorageJob(pawn, th, true);
            if (job == null) return HandResult.No("no storage accepts " + JobDesc(th) + " (or it is already in the best place); build or allow a stockpile for it");
            return JobTake(pawn, job, Name(pawn) + " will haul " + JobDesc(th) + " to storage");
        }

        // ---------------------------------------------------------------- rescue
        HandResult JobRescue(Pawn pawn, string t)
        {
            string blk = JobWorkBlock(pawn, null, "rescue");
            if (blk != null) return HandResult.No(blk);
            var victim = JobFindPawn(pawn.Map, t);
            if (victim == null) return HandResult.No("no pawn " + t + " here to rescue; downed pawns: " + JobDownedList(pawn.Map));
            if (victim == pawn) return HandResult.No(Name(pawn) + " cannot rescue themselves");
            if (victim.Dead) return HandResult.No(victim.LabelShort + " is dead");
            if (!victim.Downed) return HandResult.No(victim.LabelShort + " is not downed");
            if (victim.HostileTo(Faction.OfPlayer) && victim.Faction != null && victim.HostFaction != Faction.OfPlayer)
                return HandResult.No(victim.LabelShort + " is hostile; arrest or capture is a different order");
            if (victim.InBed()) return HandResult.No(victim.LabelShort + " is already in a bed");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return HandResult.No(Name(pawn) + " cannot rescue: no working hands");
            string why = JobUnusable(pawn, victim, PathEndMode.OnCell);
            if (why != null) return HandResult.No(why);
            var bed = RestUtility.FindBedFor(victim, pawn, victim.HostFaction == pawn.Faction, false, null);
            if (bed == null) return HandResult.No("no bed is free for " + victim.LabelShort + "; build or free a bed first");
            var job = JobMaker.MakeJob(JobDefOf.Rescue, victim, bed);
            job.count = 1;
            return JobTake(pawn, job, Name(pawn) + " will rescue " + victim.LabelShort + " and carry them to a bed");
        }

        static string JobDownedList(Map map)
        {
            var d = map.mapPawns.AllPawnsSpawned.Where(p => p.Downed && !p.Dead).Select(p => p.LabelShort).Take(8).ToList();
            return d.Count == 0 ? "none" : string.Join(", ", d);
        }

        static Pawn JobFindPawn(Map map, string t)
        {
            if (t.Length > 1 && t[0] == '#')
            {
                int id;
                if (int.TryParse(t.Substring(1), out id)) return map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
                return null;
            }
            var all = map.mapPawns.AllPawnsSpawned;
            var hit = all.FirstOrDefault(p => JobNameIs(p, t)) ?? all.FirstOrDefault(p => Eq(p.LabelCap, t) || Eq(p.Label, t));
            if (hit != null) return hit;
            var pre = all.Where(p => p.LabelShort.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
            return pre.Count == 1 ? pre[0] : null;
        }

        // ---------------------------------------------------------------- tend
        HandResult JobTend(Pawn pawn, string t)
        {
            string blk = JobWorkBlock(pawn, WorkTypeDefOf.Doctor, "tend");
            if (blk != null) return HandResult.No(blk);
            var patient = JobFindPawn(pawn.Map, t);
            if (patient == null) return HandResult.No("no pawn " + t + " here to tend");
            if (patient.Dead) return HandResult.No(patient.LabelShort + " is dead");
            if (!patient.health.HasHediffsNeedingTend(false)) return HandResult.No(patient.LabelShort + " has nothing that needs tending right now");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return HandResult.No(Name(pawn) + " cannot tend: no working hands");
            if (pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Medicine).TotallyDisabled) return HandResult.No(Name(pawn) + " cannot do Medicine work");
            if (patient != pawn)
            {
                string why = JobUnusable(pawn, patient, PathEndMode.InteractionCell);
                if (why != null) return HandResult.No(why);
            }
            string gwhy;
            var job = JobFromGivers(pawn, patient, JobScanners(typeof(WorkGiver_Tend)), "tend", out gwhy);
            if (job == null)
            {
                if (gwhy != null) return HandResult.No(gwhy);
                if (patient == pawn) return HandResult.No(Name(pawn) + " cannot tend themselves (not allowed or incapable); have someone else do it");
                return HandResult.No(Name(pawn) + " cannot tend " + patient.LabelShort + " now (the patient may be set to no medical care, or the game found no way to do it)");
            }
            var med = job.targetB.Thing;
            return JobTake(pawn, job, Name(pawn) + " will tend " + (patient == pawn ? "themselves" : patient.LabelShort) + (med != null ? " using " + med.LabelNoCount : " without medicine"));
        }

        // ---------------------------------------------------------------- clean
        HandResult JobClean(Pawn pawn, string t)
        {
            string blk = JobWorkBlock(pawn, WorkTypeDefOf.Cleaning, "clean");
            if (blk != null) return HandResult.No(blk);
            var givers = JobScanners(typeof(WorkGiver_CleanFilth));
            if (givers.Count == 0) return HandResult.No("the game has no cleaning work");
            string gb = JobGiverBlock(pawn, givers[0], "clean");
            if (gb != null) return HandResult.No(gb);
            var map = pawn.Map;
            var filth = new List<Thing>();
            string place;
            var one = JobThingById(map, t);
            if (one != null && one is Filth)
            {
                filth.Add(one);
                place = "that filth";
            }
            else
            {
                var rooms = JobRooms(map, t);
                if (rooms.Count == 0) return HandResult.No("no room called " + t + "; rooms: " + JobRoomNames(map));
                filth.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Where(f => f.Spawned && rooms.Contains(f.GetRoom())));
                place = JobRoomSay(rooms[0]);
            }
            if (filth.Count == 0) return HandResult.No("there is no filth to clean in " + place);
            string firstWhy = null;
            foreach (var f in filth.OrderBy(f => (f.Position - pawn.Position).LengthHorizontalSquared))
            {
                string why = JobUnusable(pawn, f, PathEndMode.ClosestTouch);
                if (why != null) { if (firstWhy == null) firstWhy = why; continue; }
                Job j = null;
                try { if (givers[0].HasJobOnThing(pawn, f, true)) j = givers[0].JobOnThing(pawn, f, true); } catch (Exception) { }
                if (j == null) continue;
                return JobTake(pawn, j, Name(pawn) + " will clean the " + f.LabelNoCount + " in " + place);
            }
            return HandResult.No(firstWhy ?? Name(pawn) + " cannot clean anything in " + place + " now (nothing reachable, allowed or free)");
        }

        static string JobRoomNames(Map map)
        {
            var n = map.regionGrid.AllRooms.Where(r => r != null && !r.Dereferenced && !r.PsychologicallyOutdoors && r.Role != null && r.Role != RoomRoleDefOf.None)
                .Select(JobRoomLabel).Distinct().Take(10).ToList();
            return n.Count == 0 ? "none" : string.Join(", ", n);
        }

        // ---------------------------------------------------------------- repair / refuel (building by id or label)
        HandResult JobScan(Pawn pawn, string t, string op, Type[] giverTypes, string verb, string nothing)
        {
            var givers = JobScanners(giverTypes);
            if (givers.Count == 0) return HandResult.No("the game has no " + op + " work");
            var map = pawn.Map;
            var cands = new List<Thing>();
            var byId = JobThingById(map, t);
            if (byId != null) cands.Add(byId);
            else
            {
                if (t.Length > 0 && t[0] == '#') return HandResult.No("no building " + t + " on the map");
                cands.AddRange(map.listerBuildings.allBuildingsColonist.Where(b => JobLabelIs(b, t)).Cast<Thing>()
                    .OrderBy(b => (b.Position - pawn.Position).LengthHorizontalSquared));
            }
            if (cands.Count == 0) return HandResult.No("no building called " + t + " on the map (use #id from the digest)");
            string firstWhy = null;
            foreach (var th in cands)
            {
                string why = JobWorkBlockAny(pawn, givers, verb);
                if (why == null) why = JobUnusable(pawn, th, PathEndMode.Touch);
                if (why != null) { if (firstWhy == null) firstWhy = why; continue; }
                string gwhy;
                var job = JobFromGivers(pawn, th, givers, verb, out gwhy);
                if (job == null) { if (firstWhy == null) firstWhy = gwhy ?? th.LabelNoCount + " " + nothing; continue; }
                return JobTake(pawn, job, Name(pawn) + " will " + verb + " the " + th.LabelNoCount);
            }
            return HandResult.No(firstWhy ?? "nothing to " + verb);
        }

        static string JobWorkBlockAny(Pawn pawn, List<WorkGiver_Scanner> givers, string verb)
        {
            if (pawn.Drafted) return Name(pawn) + " is drafted; undraft first";
            string first = null;
            foreach (var g in givers)
            {
                string b = JobGiverBlock(pawn, g, verb);
                if (b == null) return null;
                if (first == null) first = b;
            }
            return first;
        }

        // ---------------------------------------------------------------- equip / wear
        HandResult JobEquip(Pawn pawn, string t)
        {
            if (pawn.equipment == null) return HandResult.No(Name(pawn) + " cannot carry a weapon");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return HandResult.No(Name(pawn) + " cannot equip: no working hands");
            HandResult no;
            var th = JobPick(JobThings(pawn, t, x => x.def.IsWeapon, true), x =>
            {
                if (!x.def.IsWeapon || !(x is ThingWithComps)) return JobDesc(x) + " is not a weapon";
                if (pawn.equipment.AllEquipmentListForReading.Contains((ThingWithComps)x)) return Name(pawn) + " is already wielding " + JobDesc(x);
                string reason;
                if (!EquipmentUtility.CanEquip(x, pawn, out reason, true)) return Name(pawn) + " cannot equip " + JobDesc(x) + (string.IsNullOrEmpty(reason) ? "" : ": " + reason);
                if (CompBiocodable.IsBiocoded(x) && !CompBiocodable.IsBiocodedFor(x, pawn)) return JobDesc(x) + " is biocoded to someone else";
                return JobUnusable(pawn, x, PathEndMode.ClosestTouch);
            }, "no weapon " + t + " found (use #id from the digest)", out no);
            if (th == null) return no;
            return JobTake(pawn, JobMaker.MakeJob(JobDefOf.Equip, th), Name(pawn) + " will pick up and equip the " + th.LabelNoCount);
        }

        HandResult JobWear(Pawn pawn, string t)
        {
            if (pawn.apparel == null) return HandResult.No(Name(pawn) + " cannot wear apparel");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return HandResult.No(Name(pawn) + " cannot dress: no working hands");
            HandResult no;
            var th = JobPick(JobThings(pawn, t, x => x.def.IsApparel, true), x =>
            {
                var ap = x as Apparel;
                if (ap == null) return JobDesc(x) + " is not apparel";
                if (pawn.apparel.Wearing(ap)) return Name(pawn) + " is already wearing " + JobDesc(x);
                if (!ApparelUtility.HasPartsToWear(pawn, ap.def)) return Name(pawn) + " has no body parts to wear " + JobDesc(x) + " on";
                if (CompBiocodable.IsBiocoded(x) && !CompBiocodable.IsBiocodedFor(x, pawn)) return JobDesc(x) + " is biocoded to someone else";
                string rr;
                if (EquipmentUtility.RolePreventsFromUsing(pawn, x, out rr)) return Name(pawn) + " cannot wear it: " + rr;
                if (pawn.apparel.WouldReplaceLockedApparel(ap)) return "wearing " + JobDesc(x) + " would replace apparel " + Name(pawn) + " cannot take off";
                return JobUnusable(pawn, x, PathEndMode.ClosestTouch);
            }, "no apparel " + t + " found (use #id from the digest)", out no);
            if (th == null) return no;
            return JobTake(pawn, JobMaker.MakeJob(JobDefOf.Wear, th), Name(pawn) + " will put on the " + th.LabelNoCount);
        }

        // ---------------------------------------------------------------- drop
        HandResult JobDrop(Pawn pawn, string t)
        {
            HandResult no;
            var th = JobPick(JobThings(pawn, t, x => true, true).Where(x => !x.Spawned), x => null, "no item " + t + " on " + Name(pawn) + "; they carry: " + JobHeldList(pawn), out no);
            if (th == null) return no;
            if (th.Spawned) return HandResult.No(JobDesc(th) + " is not carried by " + Name(pawn));
            var ap = th as Apparel;
            if (ap != null && pawn.apparel != null && pawn.apparel.Wearing(ap))
            {
                if (pawn.apparel.IsLocked(ap)) return HandResult.No(JobDesc(th) + " is locked on " + Name(pawn) + " and cannot be taken off");
                return JobTake(pawn, JobMaker.MakeJob(JobDefOf.RemoveApparel, th), Name(pawn) + " will take off the " + th.LabelNoCount);
            }
            var eq = th as ThingWithComps;
            if (eq != null && pawn.equipment != null && pawn.equipment.AllEquipmentListForReading.Contains(eq))
                return JobTake(pawn, JobMaker.MakeJob(JobDefOf.DropEquipment, th), Name(pawn) + " will drop the " + th.LabelNoCount);
            if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing == th)
            {
                Thing res;
                string d = JobDesc(th);
                if (!pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out res)) return HandResult.No(Name(pawn) + " could not put down " + d);
                return HandResult.Done(Name(pawn) + " put down " + d);
            }
            if (pawn.inventory != null && pawn.inventory.innerContainer.Contains(th))
            {
                if (!pawn.Spawned) return HandResult.No(Name(pawn) + " is not on a map");
                Thing res;
                string d = JobDesc(th);
                if (!pawn.inventory.innerContainer.TryDrop(th, pawn.Position, pawn.Map, ThingPlaceMode.Near, out res, null, null, true))
                    return HandResult.No(Name(pawn) + " could not drop " + d + " (no room)");
                return HandResult.Done(Name(pawn) + " dropped " + d);
            }
            return HandResult.No(JobDesc(th) + " is not something " + Name(pawn) + " is carrying");
        }

        static string JobHeldList(Pawn pawn)
        {
            var l = JobHeld(pawn).Select(JobDesc).Take(12).ToList();
            return l.Count == 0 ? "nothing" : string.Join(", ", l);
        }

        // ---------------------------------------------------------------- consume
        HandResult JobConsume(Pawn pawn, string t)
        {
            if (pawn.needs == null || pawn.needs.food == null && !pawn.RaceProps.Humanlike) return HandResult.No(Name(pawn) + " cannot eat");
            HandResult no;
            var th = JobPick(JobThings(pawn, t, x => x.def.IsIngestible, true), x =>
            {
                if (!x.def.IsIngestible || x.def.ingestible == null) return JobDesc(x) + " cannot be eaten or taken";
                if (x.def.IsDrug && x.def.IsNonMedicalDrug && PawnUtility.IsTeetotaler(pawn)) return Name(pawn) + " is a teetotaler and will not take " + JobDesc(x);
                if (!x.def.IsDrug && !FoodUtility.WillEat(pawn, x, pawn, true, true)) return Name(pawn) + " will not eat " + JobDesc(x) + " (food restrictions, diet or ideology)";
                if (x.def.IsDrug && !FoodUtility.DrugIsSuitable(pawn, x.def)) return Name(pawn) + " cannot take " + JobDesc(x);
                if (!x.Spawned) return null;
                return JobUnusable(pawn, x, PathEndMode.OnCell);
            }, "no food or drug " + t + " found (use #id from the digest)", out no);
            if (th == null) return no;
            var job = JobMaker.MakeJob(JobDefOf.Ingest, th);
            job.count = th.def.IsDrug ? 1 : Math.Max(1, Math.Min(th.stackCount, FoodUtility.WillIngestStackCountOf(pawn, th.def, FoodUtility.GetNutrition(pawn, th, th.def))));
            return JobTake(pawn, job, Name(pawn) + " will " + (th.def.IsDrug ? "take" : "eat") + " the " + th.LabelNoCount);
        }

        // ---------------------------------------------------------------- go / stay
        HandResult JobGo(Pawn pawn, string t)
        {
            if (pawn.Drafted) return HandResult.No(Name(pawn) + " is drafted; use the order tool to move a drafted colonist");
            var map = pawn.Map;
            IntVec3 cell = IntVec3.Invalid;
            string place = null, why = null;
            Thing th = JobThingById(map, t);
            if (th == null && !(t.Length > 0 && t[0] == '#')) th = JobFindPawn(map, t);
            if (th != null)
            {
                if (th.Destroyed || !th.Spawned || th.Map != map) return HandResult.No(JobDesc(th) + " is not on the map");
                cell = JobCellNear(pawn, th, out why);
                place = th is Pawn ? ((Pawn)th).LabelShort : "the " + th.LabelNoCount;
            }
            else
            {
                var rooms = JobRooms(map, t);
                if (rooms.Count > 0)
                {
                    foreach (var r in rooms.OrderBy(r => (r.Cells.FirstOrDefault() - pawn.Position).LengthHorizontalSquared))
                    {
                        cell = JobCellInRoom(pawn, r);
                        if (cell.IsValid) { place = JobRoomSay(r); break; }
                    }
                    if (!cell.IsValid) why = Name(pawn) + " cannot reach " + t;
                }
                else
                {
                    var bs = map.listerBuildings.allBuildingsColonist.Where(b => JobLabelIs(b, t)).OrderBy(b => (b.Position - pawn.Position).LengthHorizontalSquared).ToList();
                    if (bs.Count == 0) return HandResult.No("no colonist, building or room called " + t + "; rooms: " + JobRoomNames(map));
                    foreach (var b in bs)
                    {
                        cell = JobCellNear(pawn, b, out why);
                        if (cell.IsValid) { place = "the " + b.LabelNoCount; break; }
                    }
                }
            }
            if (!cell.IsValid) return HandResult.No(why ?? Name(pawn) + " cannot reach " + t);
            var cur = pawn.CurJob;
            if (cur != null && cur.def == JobDefOf.Goto && cur.targetA.Cell == cell) return HandResult.No(Name(pawn) + " is already going there");
            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            return JobTake(pawn, job, Name(pawn) + " will go to " + place);
        }

        static IntVec3 JobCellNear(Pawn pawn, Thing th, out string why)
        {
            why = null;
            var map = pawn.Map;
            var cells = new List<IntVec3>();
            if (th.Position.Standable(map)) cells.Add(th.Position);
            cells.AddRange(GenAdj.CellsAdjacent8Way(th).Where(c => c.InBounds(map) && c.Standable(map)));
            foreach (var c in cells.OrderBy(c => (c - pawn.Position).LengthHorizontalSquared))
                if (pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            why = Name(pawn) + " cannot reach " + (th is Pawn ? ((Pawn)th).LabelShort : "the " + th.LabelNoCount);
            return IntVec3.Invalid;
        }

        static IntVec3 JobCellInRoom(Pawn pawn, Room r)
        {
            var map = pawn.Map;
            var cells = r.Cells.Where(c => c.Standable(map) && c.GetEdifice(map) == null).ToList();
            if (cells.Count == 0) return IntVec3.Invalid;
            float cx = (float)cells.Average(c => c.x), cz = (float)cells.Average(c => c.z);
            foreach (var c in cells.OrderBy(c => (c.x - cx) * (c.x - cx) + (c.z - cz) * (c.z - cz)).Take(40))
                if (pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            return IntVec3.Invalid;
        }

        HandResult JobStay(Pawn pawn)
        {
            if (pawn.drafter == null || !pawn.Drafted) return HandResult.No(Name(pawn) + " is not drafted; draft first");
            if (pawn.CurJobDef == JobDefOf.Wait_Combat && !pawn.pather.Moving) return HandResult.No(Name(pawn) + " is already standing still");
            var job = JobMaker.MakeJob(JobDefOf.Wait_Combat);
            job.playerForced = true;
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return HandResult.No(Name(pawn) + " could not take the order");
            return HandResult.Done(Name(pawn) + " will hold position and stay drafted");
        }
    }
}

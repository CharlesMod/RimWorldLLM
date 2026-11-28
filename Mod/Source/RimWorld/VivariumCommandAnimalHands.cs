using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>The command hands for animals and social gatherings (IVivariumCommandHands.Animal / Social). Game thread.
    /// Animals: the same designations and training calls the vanilla Tame / Slaughter / Release-to-wild designators and the
    /// Training tab make. Social: the gathering workers the game's own party and marriage logic run. Verse code: not unit-tested.</summary>
    public sealed partial class VivariumHands
    {
        // ---------------------------------------------------------------- shared small helpers (prefix Cmd to avoid clashes)
        static bool CmdThingId(string s, out int id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            return int.TryParse(s, out id) && id > 0;
        }

        static bool CmdNameMatches(Pawn p, string who)
        {
            if (p == null || string.IsNullOrWhiteSpace(who)) return false;
            if (Eq(p.LabelShort, who) || Eq(p.LabelNoCount, who)) return true;
            try { if (p.Name != null && Eq(p.Name.ToStringFull, who)) return true; } catch (Exception) { }
            return false;
        }

        static bool CmdKindMatches(Pawn p, string who)
        {
            if (p == null || string.IsNullOrWhiteSpace(who)) return false;
            return Eq(p.kindDef?.label, who) || Eq(p.kindDef?.defName, who) || Eq(p.def?.label, who) || Eq(p.def?.defName, who)
                   || Eq(p.kindDef?.labelPlural, who);
        }

        static bool CmdNamed(Pawn p) { return p.Name != null && !p.Name.Numerical; }

        static bool CmdBonded(Pawn p)
        {
            try { return p.relations != null && p.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond, null) != null; }
            catch (Exception) { return false; }
        }

        static string CmdAnimalLabel(Pawn p)
        {
            return CmdNamed(p) ? p.LabelShort + " the " + (p.kindDef?.label ?? p.def.label) : p.LabelShort;
        }

        static string CmdList(IEnumerable<string> xs, int max)
        {
            var l = xs.Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(max).ToList();
            return l.Count == 0 ? "none" : string.Join(", ", l);
        }

        static IEnumerable<Pawn> CmdColonyAnimals(Map map)
        {
            return map.mapPawns.SpawnedColonyAnimals.Where(p => p != null && !p.Dead && p.Faction == Faction.OfPlayer && p.RaceProps.Animal);
        }

        /// <summary>The colony animal named by who ("#id", name or kind label); nearest-to-base order for kinds. Null + reason when none.</summary>
        static List<Pawn> CmdFindColonyAnimals(Map map, string who, out HandResult no)
        {
            no = null;
            int id;
            if (CmdThingId(who, out id))
            {
                var t = CmdColonyAnimals(map).FirstOrDefault(p => p.thingIDNumber == id);
                if (t == null) { no = HandResult.No("no colony animal #" + id + " on the map; animals: " + CmdList(CmdColonyAnimals(map).Select(CmdAnimalLabel), 10)); return null; }
                return new List<Pawn> { t };
            }
            if (string.IsNullOrWhiteSpace(who)) { no = HandResult.No("say which animal; animals: " + CmdList(CmdColonyAnimals(map).Select(CmdAnimalLabel), 10)); return null; }
            var byName = CmdColonyAnimals(map).Where(p => CmdNameMatches(p, who)).ToList();
            if (byName.Count > 0) return byName;
            var byKind = CmdColonyAnimals(map).Where(p => CmdKindMatches(p, who)).ToList();
            if (byKind.Count > 0) return byKind;
            no = HandResult.No("no colony animal called " + who + "; animals: " + CmdList(CmdColonyAnimals(map).Select(CmdAnimalLabel), 10));
            return null;
        }

        // ---------------------------------------------------------------- Animal
        public HandResult Animal(CmdArgs a)
        {
            return Described(AnimalDo(a), () => CommandPure.AnimalWhat(a.Op, a.Who, a.Value));
        }

        HandResult AnimalDo(CmdArgs a)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                switch (a.Op)
                {
                    case "tame": return AnimalTame(map, a);
                    case "train": return AnimalTrain(map, a);
                    case "slaughter": return AnimalSlaughter(map, a);
                    case "release": return AnimalRelease(map, a);
                    case "area": return AnimalArea(map, a);
                    default: return HandResult.No("unknown animal op " + a.Op + "; ops: " + string.Join(", ", CommandPure.AnimalOps));
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        HandResult AnimalTame(Map map, CmdArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.Who)) return HandResult.No("say which wild animal to tame (a kind such as 'wild boar', or '#id')");
            var wild = map.mapPawns.AllPawnsSpawned.Where(p => p != null && p.Faction == null && p.RaceProps.Animal && !p.Dead && !p.Position.Fogged(map)).ToList();
            int id;
            List<Pawn> match = CmdThingId(a.Who, out id) ? wild.Where(p => p.thingIDNumber == id).ToList()
                : wild.Where(p => CmdKindMatches(p, a.Who) || CmdNameMatches(p, a.Who)).ToList();
            if (match.Count == 0)
            {
                var kinds = wild.Select(p => p.kindDef?.label ?? p.def.label);
                return HandResult.No("no wild " + a.Who + " in sight; wild animals around: " + CmdList(kinds, 8));
            }
            var home = HomeCenter(map);
            var dm = map.designationManager;
            string whyNot = null;
            var des = new Designator_Tame();
            foreach (var p in match.OrderBy(x => BuildHelpers.Dist2(x.Position.x, x.Position.z, home.x, home.z)))
            {
                if (dm.DesignationOn(p, DesignationDefOf.Tame) != null) { whyNot = whyNot ?? "the " + CmdKindLabel(p) + " is already marked to be tamed"; continue; }
                AcceptanceReport r;
                try { r = des.CanDesignateThing(p); } catch (Exception) { r = TameUtility.CanTame(p); }
                if (!r.Accepted) { whyNot = string.IsNullOrEmpty(r.Reason) ? "the " + CmdKindLabel(p) + " cannot be tamed" : r.Reason; continue; }
                dm.AddDesignation(new Designation(p, DesignationDefOf.Tame));
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                mb.Designation(DesignationDefOf.Tame.defName, p.thingIDNumber, 0, 0);
                return HandResult.Placed("marked a wild " + CmdKindLabel(p) + " to be tamed", mb.Marks);
            }
            return HandResult.No(whyNot ?? ("no wild " + a.Who + " can be tamed") + "; try another kind of wild animal");
        }

        static string CmdKindLabel(Pawn p) { return p.kindDef?.label ?? p.def.label; }

        HandResult AnimalTrain(Map map, CmdArgs a)
        {
            HandResult no;
            var found = CmdFindColonyAnimals(map, a.Who, out no); if (found == null) return no;
            if (string.IsNullOrWhiteSpace(a.Value))
                return HandResult.No("say what to train: " + CmdList(TrainableUtility.TrainableDefsInListOrder.Select(t => t.label), 10));
            var def = TrainableUtility.TrainableDefsInListOrder.FirstOrDefault(t => Eq(t.label, a.Value) || Eq(t.defName, a.Value));
            if (def == null) return HandResult.No("no training called " + a.Value + "; trainings: " + CmdList(TrainableUtility.TrainableDefsInListOrder.Select(t => t.label), 10));
            string last = null;
            foreach (var p in found)
            {
                if (p.training == null) { last = CmdAnimalLabel(p) + " cannot be trained"; continue; }
                if (p.training.HasLearned(def)) { last = CmdAnimalLabel(p) + " already knows " + def.label; continue; }
                if (p.training.GetWanted(def)) { last = CmdAnimalLabel(p) + " is already being trained in " + def.label; continue; }
                bool visible;
                var r = p.training.CanAssignToTrain(def, out visible);
                if (!r.Accepted || !visible)
                {
                    last = CmdAnimalLabel(p) + " cannot learn " + def.label + (string.IsNullOrEmpty(r.Reason) ? "" : ": " + r.Reason) + "; try another training";
                    continue;
                }
                p.training.SetWantedRecursive(def, true);
                return HandResult.Done("set " + CmdAnimalLabel(p) + " to be trained in " + def.label);
            }
            return HandResult.No(last ?? "nothing to train");
        }

        HandResult AnimalSlaughter(Map map, CmdArgs a)
        {
            HandResult no;
            var found = CmdFindColonyAnimals(map, a.Who, out no); if (found == null) return no;
            bool byName = found.Count == 1 && (CmdThingId(a.Who, out _) || CmdNameMatches(found[0], a.Who));
            var dm = map.designationManager;
            var des = new Designator_Slaughter();
            string last = null;
            IEnumerable<Pawn> order = byName ? found : found.OrderBy(p => p.ageTracker != null && p.ageTracker.CurLifeStage != null && p.ageTracker.CurLifeStage.reproductive ? 0 : 1);
            foreach (var p in order)
            {
                if ((CmdNamed(p) || CmdBonded(p)) && !a.AllowNamed)
                {
                    last = CmdAnimalLabel(p) + " is a named pet; the player has not let the narrator slaughter named pets (an unnamed animal can be slaughtered instead)";
                    continue;
                }
                if (dm.DesignationOn(p, DesignationDefOf.Slaughter) != null) { last = CmdAnimalLabel(p) + " is already marked for slaughter"; continue; }
                if (!byName && !CmdAdult(p)) { last = "only young " + CmdKindLabel(p) + " are left; wait for them to grow"; continue; }
                AcceptanceReport r;
                try { r = des.CanDesignateThing(p); } catch (Exception) { r = true; }
                if (!r.Accepted) { last = string.IsNullOrEmpty(r.Reason) ? CmdAnimalLabel(p) + " cannot be slaughtered" : r.Reason; continue; }
                dm.AddDesignation(new Designation(p, DesignationDefOf.Slaughter));
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                mb.Designation(DesignationDefOf.Slaughter.defName, p.thingIDNumber, 0, 0);
                return HandResult.Placed("marked " + CmdAnimalLabel(p) + " for slaughter", mb.Marks);
            }
            return HandResult.No(last ?? "no animal to slaughter");
        }

        static bool CmdAdult(Pawn p)
        {
            try { var ls = p.ageTracker?.CurLifeStage; return ls == null || ls.reproductive; } catch (Exception) { return true; }
        }

        HandResult AnimalRelease(Map map, CmdArgs a)
        {
            HandResult no;
            var found = CmdFindColonyAnimals(map, a.Who, out no); if (found == null) return no;
            var dm = map.designationManager;
            var des = new Designator_ReleaseAnimalToWild();
            string last = null;
            foreach (var p in found)
            {
                if ((CmdNamed(p) || CmdBonded(p)) && !a.AllowNamed)
                {
                    last = CmdAnimalLabel(p) + " is a named pet; the player has not let the narrator release named pets (an unnamed animal can be released instead)";
                    continue;
                }
                if (dm.DesignationOn(p, DesignationDefOf.ReleaseAnimalToWild) != null) { last = CmdAnimalLabel(p) + " is already marked for release"; continue; }
                AcceptanceReport r;
                try { r = des.CanDesignateThing(p); } catch (Exception) { r = true; }
                if (!r.Accepted) { last = string.IsNullOrEmpty(r.Reason) ? CmdAnimalLabel(p) + " cannot be released" : r.Reason; continue; }
                dm.AddDesignation(new Designation(p, DesignationDefOf.ReleaseAnimalToWild));
                var mb = new BuildHelpers.MarkBuilder(map.uniqueID);
                mb.Designation(DesignationDefOf.ReleaseAnimalToWild.defName, p.thingIDNumber, 0, 0);
                return HandResult.Placed("marked " + CmdAnimalLabel(p) + " to be released to the wild", mb.Marks);
            }
            return HandResult.No(last ?? "no animal to release");
        }

        HandResult AnimalArea(Map map, CmdArgs a)
        {
            HandResult no;
            var found = CmdFindColonyAnimals(map, a.Who, out no); if (found == null) return no;
            var p = found[0];
            if (p.playerSettings == null || !p.playerSettings.SupportsAllowedAreas) return HandResult.No(CmdAnimalLabel(p) + " cannot be kept to an area");
            string v = (a.Value ?? "").Trim();
            if (v.Length == 0) return HandResult.No("say which area: " + CmdList(CmdAreas(map).Select(x => x.Label), 12) + " (or 'none' to unrestrict)");
            if (Eq(v, "none") || Eq(v, "unrestricted") || Eq(v, "no area"))
            {
                if (p.playerSettings.AreaRestrictionInPawnCurrentMap == null) return HandResult.No(CmdAnimalLabel(p) + " is already unrestricted");
                p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                return HandResult.Done("let " + CmdAnimalLabel(p) + " roam unrestricted");
            }
            var areas = CmdAreas(map).ToList();
            var area = areas.FirstOrDefault(x => Eq(x.Label, v)) ?? areas.FirstOrDefault(x => x.Label != null && x.Label.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
            if (area == null) return HandResult.No("no area called " + v + "; areas: " + CmdList(areas.Select(x => x.Label), 12) + " (areas cannot be made here; use the area tool first)");
            if (p.playerSettings.AreaRestrictionInPawnCurrentMap == area) return HandResult.No(CmdAnimalLabel(p) + " is already kept to the " + area.Label + " area");
            p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            return HandResult.Done("kept " + CmdAnimalLabel(p) + " to the " + area.Label + " area");
        }

        static IEnumerable<Area> CmdAreas(Map map)
        {
            return map.areaManager.AllAreas.Where(x => x != null && x.AssignableAsAllowed());
        }

        // ---------------------------------------------------------------- Social
        public HandResult Social(CmdArgs a)
        {
            return Described(SocialDo(a), () => CommandPure.SocialWhat(a.Op, a.Who, a.Value));
        }

        HandResult SocialDo(CmdArgs a)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                switch (a.Op)
                {
                    case "party": return SocialGather(map, GatheringDefOf.Party, a, "a party", false);
                    case "wedding": return SocialWedding(map, a);
                    case "other":
                    {
                        var defs = DefDatabase<GatheringDef>.AllDefsListForReading;
                        if (string.IsNullOrWhiteSpace(a.Value)) return HandResult.No("say which gathering: " + CmdList(defs.Select(d => d.label ?? d.defName), 10));
                        var def = defs.FirstOrDefault(d => Eq(d.label, a.Value) || Eq(d.defName, a.Value));
                        if (def == null) return HandResult.No("no gathering called " + a.Value + "; gatherings: " + CmdList(defs.Select(d => d.label ?? d.defName), 10));
                        return SocialGather(map, def, a, "a " + (def.label ?? def.defName), false);
                    }
                    default: return HandResult.No("unknown social op " + a.Op + "; ops: " + string.Join(", ", CommandPure.SocialOps));
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        static Pawn CmdColonist(Map map, string who)
        {
            if (string.IsNullOrWhiteSpace(who)) return null;
            int id;
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (CmdThingId(who, out id)) return cols.FirstOrDefault(p => p.thingIDNumber == id);
            return cols.FirstOrDefault(p => CmdNameMatches(p, who));
        }

        /// <summary>The game's own reasons a gathering cannot start now, in plain words; null when none is found.</summary>
        static string GatherWhy(Map map, GatheringDef def, Pawn organizer)
        {
            try
            {
                if (GatheringsUtility.AnyLordJobPreventsNewGatherings(map)) return "another gathering, ritual or event is already going on";
                if (!GatheringsUtility.AcceptableGameConditionsToStartGathering(map, def)) return "the weather or an event (a raid, toxic fallout, extreme heat or cold, an eclipse) makes it a bad time";
                if (organizer != null && !GatheringsUtility.PawnCanStartOrContinueGathering(organizer)) return organizer.LabelShort + " cannot organise right now (downed, in a mental break, drafted, hungry or tired)";
                if (!GatheringsUtility.EnoughPotentialGuestsToStartGathering(map, def, null)) return "too few colonists are free to attend (some are busy, hurt, or in a mental break)";
            }
            catch (Exception) { }
            return null;
        }

        HandResult SocialGather(Map map, GatheringDef def, CmdArgs a, string what, bool unused)
        {
            if (def == null || def.Worker == null) return HandResult.No("this gathering is not available in this game");
            Pawn org = null; string orgNote = "";
            if (!string.IsNullOrWhiteSpace(a.Who))
            {
                org = CmdColonist(map, a.Who);
                if (org == null)
                    return HandResult.No("no colonist named " + a.Who + " to organise; colonists: " + CmdList(map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort), 10));
                if (!GatheringsUtility.PawnCanStartOrContinueGathering(org)) { orgNote = " (" + org.LabelShort + " could not organise, so the game picked someone)"; org = null; }
            }
            if (!def.Worker.CanExecute(map, org))
            {
                string why = GatherWhy(map, def, org);
                string extra = def == GatheringDefOf.Party ? "; a roofed room or a party spot helps" : "";
                return HandResult.No("cannot start " + what + " now: " + (why ?? "the game finds no organiser or good place for it") + extra + "; try again later");
            }
            if (!def.Worker.TryExecute(map, org))
                return HandResult.No("the game could not find a spot or an organiser for " + what + "; build a party spot or try again when the colonists are free");
            return HandResult.Done("started " + what + (org != null ? ", organised by " + org.LabelShort : "") + orgNote);
        }

        static bool CmdMarrying(Pawn p) { var l = p.GetLord(); return l != null && l.LordJob is LordJob_Joinable_MarriageCeremony; }

        HandResult SocialWedding(Map map, CmdArgs a)
        {
            var def = GatheringDefOf.MarriageCeremony;
            Pawn one = null, two = null;
            if (!string.IsNullOrWhiteSpace(a.Who))
            {
                one = CmdColonist(map, a.Who);
                if (one == null) return HandResult.No("no colonist named " + a.Who + "; colonists: " + CmdList(map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort), 10));
                two = one.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Fiance, null);
                if (two == null) return HandResult.No(one.LabelShort + " is not engaged; only engaged colonists can marry (a romance must reach an engagement first)");
                if (!two.Spawned || two.Map != map || !two.IsFreeNonSlaveColonist) return HandResult.No(one.LabelShort + "'s fiance(e) " + two.LabelShort + " is not a free colonist on the map");
            }
            else
            {
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    var f = p.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Fiance, null);
                    if (f != null && f.Spawned && f.Map == map && f.IsFreeNonSlaveColonist) { one = p; two = f; break; }
                }
                if (one == null) return HandResult.No("no two engaged colonists are on the map; a wedding needs an engagement first");
            }
            if (CmdMarrying(one) || CmdMarrying(two))
                return HandResult.No(one.LabelShort + " and " + two.LabelShort + " are already in a wedding");
            if (!MarriageCeremonyUtility.AcceptableGameConditionsToStartCeremony(map))
                return HandResult.No("cannot start the wedding now: the weather or an event (a raid, fallout, extreme heat or cold) makes it a bad time; try again later");
            if (!MarriageCeremonyUtility.FianceReadyToStartCeremony(one, two) || !MarriageCeremonyUtility.FianceReadyToStartCeremony(two, one))
                return HandResult.No("cannot start the wedding now: " + one.LabelShort + " and " + two.LabelShort + " are not both free (downed, asleep, in a mental break, drafted or far away); try again when they are");
            if (!def.Worker.CanExecute(map, one))
                return HandResult.No("cannot start the wedding now: " + (GatherWhy(map, def, one) ?? "no marriage spot or good place is available") + "; a marriage spot or a roofed room helps");
            if (!def.Worker.TryExecute(map, one))
                return HandResult.No("the game could not find a place for the wedding of " + one.LabelShort + " and " + two.LabelShort + "; build a marriage spot or try again later");
            return HandResult.Done("started the wedding of " + one.LabelShort + " and " + two.LabelShort);
        }
    }
}

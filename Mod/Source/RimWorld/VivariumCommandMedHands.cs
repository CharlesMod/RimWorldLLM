using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;

namespace RimWorldLLM
{
    /// <summary>The command hands for medicine and prisoners (docs/VIVARIUM.md "Command tools"). Game thread. They make the same
    /// calls the health tab, the bed gizmos and the prisoner tab make; each refuses with a reason and what to do instead.</summary>
    public sealed partial class VivariumHands
    {
        const int MaxSurgeryChoices = 10;

        /// <summary>A pawn on a map (colonist, slave, prisoner, colony animal) by "#id", short label or full name.</summary>
        static Pawn FindPatient(string who, out HandResult no)
        {
            no = null;
            string w = (who ?? "").Trim();
            if (w.Length == 0) { no = HandResult.No("say who it is for (a name)"); return null; }
            var pawns = new List<Pawn>();
            foreach (var map in Find.Maps)
                pawns.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony));
            if (w.StartsWith("#"))
            {
                int id;
                if (int.TryParse(w.Substring(1), out id)) { var byId = pawns.FirstOrDefault(p => p.thingIDNumber == id); if (byId != null) return byId; }
            }
            var hits = pawns.Where(p => Eq(p.LabelShort, w) || Eq(p.LabelCap, w) || (p.Name != null && Eq(p.Name.ToStringFull, w))).ToList();
            if (hits.Count == 0) hits = pawns.Where(p => p.LabelShort.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 1) return hits[0];
            var names = string.Join(", ", (hits.Count > 1 ? hits : pawns.Where(p => !p.RaceProps.Animal || p.Name != null).ToList()).Take(12).Select(p => p.LabelShort));
            no = HandResult.No(hits.Count > 1 ? "more than one pawn matches " + w + ": " + names : "no colonist, slave, prisoner or colony animal named " + w + (names.Length > 0 ? "; try: " + names : ""));
            return null;
        }

        static string PawnKind(Pawn p)
        {
            return p.IsPrisonerOfColony ? "prisoner" : p.IsSlaveOfColony ? "slave" : p.RaceProps.Animal ? "animal" : "colonist";
        }

        // ---------------------------------------------------------------- medical
        public HandResult Medical(CmdArgs a)
        {
            return Described(MedicalDo(a), () => CommandPure.MedicalWhat(a == null ? "" : a.Op, a == null ? "" : a.Who, a == null ? "" : a.Value));
        }

        HandResult MedicalDo(CmdArgs a)
        {
            try
            {
                HandResult no;
                var p = FindPatient(a.Who, out no); if (p == null) return no;
                if (p.Dead || p.Destroyed) return HandResult.No(Name(p) + " is dead");
                if (p.Map == null || !p.Spawned) return HandResult.No(Name(p) + " is not on a map");
                switch (a.Op)
                {
                    case "operate": return Operate(p, a.Value);
                    case "rest": return RestInBed(p);
                    case "care": return SetCare(p, a.Value);
                    default: return HandResult.No("unknown medical op '" + a.Op + "' (operate, rest or care)");
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        sealed class SurgeryOption
        {
            public RecipeDef Recipe;
            public BodyPartRecord Part;
            public string Label;
            public string Missing; // null = ingredients exist
        }

        static bool CasualSurgery(RecipeDef r)
        {
            if (r.isViolation) return false;
            string d = r.defName ?? "";
            return d.IndexOf("Euthan", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("Execute", StringComparison.OrdinalIgnoreCase) < 0
                && d.IndexOf("Vivisect", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) < 0
                && d.IndexOf("Kill", StringComparison.OrdinalIgnoreCase) < 0;
        }

        static List<SurgeryOption> Surgeries(Pawn p)
        {
            var list = new List<SurgeryOption>();
            foreach (var r in p.def.AllRecipes)
            {
                if (!r.IsSurgery || !r.AvailableNow || !CasualSurgery(r)) continue;
                if (!r.AvailableOnNow(p, null) && !r.targetsBodyPart) continue;
                string missing = null;
                try
                {
                    var miss = r.PotentiallyMissingIngredients(null, p.Map).ToList();
                    if (miss.Count > 0) missing = string.Join(" and ", miss.Select(t => t.label));
                }
                catch (Exception) { }
                if (!r.targetsBodyPart)
                {
                    list.Add(new SurgeryOption { Recipe = r, Part = null, Label = r.label, Missing = missing });
                    continue;
                }
                IEnumerable<BodyPartRecord> parts = r.Worker.GetPartsToApplyOn(p, r);
                if (parts == null) continue;
                foreach (var part in parts)
                {
                    if (!r.AvailableOnNow(p, part)) continue;
                    list.Add(new SurgeryOption { Recipe = r, Part = part, Label = r.label + (r.hideBodyPartNames ? "" : " " + part.Label), Missing = missing });
                }
            }
            return list.OrderBy(o => o.Label).ToList();
        }

        static bool SameOperation(Pawn p, SurgeryOption o)
        {
            return p.BillStack.Bills.OfType<Bill_Medical>().Any(b => b.recipe == o.Recipe && b.Part == o.Part);
        }

        HandResult Operate(Pawn p, string value)
        {
            if (p.BillStack == null) return HandResult.No(Name(p) + " cannot have operations");
            var all = Surgeries(p);
            var ready = all.Where(o => o.Missing == null && !SameOperation(p, o)).ToList();
            string q = (value ?? "").Trim();
            Func<string> choices = () => ready.Count == 0
                ? "no operation is possible on " + Name(p) + " right now"
                : "choices: " + string.Join(", ", ready.Take(MaxSurgeryChoices).Select(o => o.Label)) + (ready.Count > MaxSurgeryChoices ? ", ... (" + (ready.Count - MaxSurgeryChoices) + " more)" : "");
            if (q.Length == 0) return HandResult.No("name the operation for " + Name(p) + "; " + choices());
            var hits = all.Where(o => Eq(o.Label, q)).ToList();
            if (hits.Count == 0) hits = all.Where(o => o.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0) return HandResult.No("no operation '" + q + "' for " + Name(p) + "; " + choices());
            var fresh = hits.Where(o => !SameOperation(p, o)).ToList();
            if (fresh.Count == 0) return HandResult.No(hits[0].Label + " is already queued for " + Name(p));
            var okOnes = fresh.Where(o => o.Missing == null).ToList();
            if (okOnes.Count == 0) return HandResult.No("the colony has no " + fresh[0].Missing + " for " + fresh[0].Label + "; build or buy it first");
            if (okOnes.Count > 1) return HandResult.No("'" + q + "' fits more than one operation: " + string.Join(", ", okOnes.Take(MaxSurgeryChoices).Select(o => o.Label)));
            var pick = okOnes[0];
            var bill = HealthCardUtility.CreateSurgeryBill(p, pick.Recipe, pick.Part, null, false);
            if (bill == null) return HandResult.No("the game would not queue " + pick.Label + " for " + Name(p));
            if (!p.BillStack.Bills.Contains(bill)) p.BillStack.AddBill(bill);
            var mark = new NarratorMark { Kind = "bill", Map = p.Map.uniqueID, Id = p.thingIDNumber, Sub = BuildHelpers.LoadIdOf(bill.GetUniqueLoadID()), Def = pick.Recipe.defName };
            string note = "queued " + pick.Label + " for " + Name(p) + (p.IsPrisonerOfColony ? " (prisoner)" : "") + "; a doctor will do it when a bed and the materials are ready";
            return HandResult.Placed(note, new List<NarratorMark> { mark });
        }

        HandResult RestInBed(Pawn p)
        {
            if (!(p.IsColonist || p.IsSlaveOfColony) || p.RaceProps.Animal)
                return HandResult.No("only a colonist can be sent to bed; prisoners and animals use their own beds");
            if (p.Downed) return HandResult.No(Name(p) + " is downed; have someone rescue them to a bed instead");
            if (p.Drafted) return HandResult.No(Name(p) + " is drafted; undraft first");
            if (RestUtility.InBed(p) && p.CurJobDef == JobDefOf.LayDown && p.CurJob != null && p.CurJob.restUntilHealed) return HandResult.No(Name(p) + " is already resting in bed");
            if (!HealthAIUtility.ShouldSeekMedicalRest(p)) return HandResult.No(Name(p) + " is not hurt or sick enough to need bed rest");
            var bed = RestUtility.FindPatientBedFor(p);
            if (bed == null || !bed.Medical) return HandResult.No("no medical bed is free for " + Name(p) + "; build or free one first");
            var job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            job.forceSleep = false;
            job.restUntilHealed = true;
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return HandResult.No(Name(p) + " cannot go to the bed right now (unreachable or busy)");
            return HandResult.Done(Name(p) + " is going to a medical bed to rest");
        }

        static readonly string[] CareWords = { "none", "no_meds", "herbal", "normal", "best" };
        static MedicalCareCategory[] CareValues() { return new[] { MedicalCareCategory.NoCare, MedicalCareCategory.NoMeds, MedicalCareCategory.HerbalOrWorse, MedicalCareCategory.NormalOrWorse, MedicalCareCategory.Best }; }
        static readonly string[] CarePlain = { "no care", "no medicine", "herbal medicine or worse", "normal medicine or worse", "the best medicine" };

        HandResult SetCare(Pawn p, string value)
        {
            if (p.playerSettings == null) return HandResult.No(Name(p) + " has no medical care setting");
            string v = (value ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
            int i = Array.IndexOf(CareWords, v);
            if (i < 0) return HandResult.No("care level must be one of " + string.Join(", ", CareWords));
            var cat = CareValues()[i];
            if (p.playerSettings.medCare == cat) return HandResult.No(Name(p) + " is already on " + CarePlain[i]);
            p.playerSettings.medCare = cat;
            return HandResult.Done("set " + Name(p) + " (" + PawnKind(p) + ") to get " + CarePlain[i] + " when hurt or sick");
        }

        // ---------------------------------------------------------------- prisoners
        public HandResult Prisoner(CmdArgs a)
        {
            return Described(PrisonerDo(a), () => CommandPure.PrisonerWhat(a == null ? "" : a.Op, a == null ? "" : a.Who, a == null ? "" : a.Value));
        }

        HandResult PrisonerDo(CmdArgs a)
        {
            try
            {
                HandResult no;
                var p = FindPatient(a.Who, out no); if (p == null) return no;
                if (p.Dead || p.Destroyed) return HandResult.No(Name(p) + " is dead");
                if (!p.IsPrisonerOfColony || p.Map == null || !p.Spawned || p.guest == null)
                    return HandResult.No(Name(p) + " is not a prisoner of the colony on a map");
                if (a.Op == "free") return FreePrisoner(p);
                if (a.Op != "mode") return HandResult.No("unknown prisoner op '" + a.Op + "' (mode or free)");
                return PrisonerMode(p, (a.Value ?? "").Trim().ToLowerInvariant());
            }
            catch (Exception e) { return Failed(e); }
        }

        static PrisonerInteractionModeDef ModeDef(string word)
        {
            switch (word)
            {
                case "none": return PrisonerInteractionModeDefOf.MaintainOnly;
                case "chat": return PrisonerInteractionModeDefOf.ReduceResistance;
                case "recruit": return PrisonerInteractionModeDefOf.AttemptRecruit;
                case "convert": return PrisonerInteractionModeDefOf.Convert;
                case "enslave": return PrisonerInteractionModeDefOf.Enslave;
                case "release": return PrisonerInteractionModeDefOf.Release;
                case "execute": return PrisonerInteractionModeDefOf.Execution;
                default: return null;
            }
        }

        static string ModePlain(string word)
        {
            switch (word)
            {
                case "none": return "be held with no interaction";
                case "chat": return "be talked to (reducing resistance)";
                case "recruit": return "be recruited";
                case "convert": return "be converted to the colony's ideoligion";
                case "enslave": return "be enslaved";
                case "release": return "be released";
                default: return "be executed";
            }
        }

        HandResult PrisonerMode(Pawn p, string word)
        {
            if (Array.IndexOf(CommandPure.PrisonerModes, word) < 0) return HandResult.No("mode must be one of " + string.Join(", ", CommandPure.PrisonerModes));
            if ((word == "convert" || word == "enslave") && !ModsConfig.IdeologyActive) return HandResult.No("Ideology is not active");
            if (word == "execute")
            {
                var s = RimWorldLLMMod.Settings;
                if (s == null || !s.narratorMayExecutePrisoners) return HandResult.No("the player has not let the narrator execute prisoners");
            }
            var def = ModeDef(word);
            if (def == null) return HandResult.No("this game has no '" + word + "' prisoner mode");
            if (p.guest.ExclusiveInteractionMode == def) return HandResult.No(Name(p) + " is already set to " + word);
            if (def.hideIfNotRecruitable && !p.guest.Recruitable) return HandResult.No(Name(p) + " cannot be recruited, so '" + word + "' is not offered for them");
            if (def.hideOnHemogenicPawns && p.genes != null && p.genes.HasActiveGene(GeneDefOf.Hemogenic)) return HandResult.No("'" + word + "' is not offered for a hemogenic prisoner");
            if (p.IsWildMan() && !def.allowOnWildMan) return HandResult.No("'" + word + "' is not offered for a wild man");
            if (word == "convert" && p.Ideo == Faction.OfPlayer.ideos?.PrimaryIdeo) return HandResult.No(Name(p) + " already follows the colony's ideoligion");
            p.guest.SetExclusiveInteraction(def);
            return HandResult.Done("set " + Name(p) + " (prisoner) to " + ModePlain(word));
        }

        HandResult FreePrisoner(Pawn p)
        {
            string name = Name(p);
            GenGuest.PrisonerRelease(p);
            if (p.IsPrisonerOfColony) return HandResult.No("the game kept " + name + " as a prisoner");
            return HandResult.Done("freed " + name + ", who is no longer a prisoner");
        }
    }
}

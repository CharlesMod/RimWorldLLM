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
    /// <summary>inspect(target) for a pawn: fills PawnFacts (health, needs, mood, skills, gear, job, relations, prisoner/slave state).
    /// Every section is read on its own so one failing mod hediff or need cannot lose the rest.</summary>
    public sealed partial class VivariumInspectHands
    {
        const double CapacityShowBelow = 0.8;

        static void Try(Action a) { try { a(); } catch (Exception) { } }

        JObject ReadPawn(Map map, Pawn p)
        {
            var f = new PawnFacts { Name = p.LabelShort, Id = p.thingIDNumber };
            Try(() => f.Kind = PawnKind(p));
            Try(() => { if (p.ageTracker != null) f.Age = p.ageTracker.AgeBiologicalYears; });
            Try(() => f.Xenotype = XenoWord(p));
            Try(() => f.Role = RoleWord(p));
            Try(() => { if (p.Faction != null && p.Faction != Faction.OfPlayer) f.Faction = p.Faction.Name; });
            Try(() => f.Downed = p.Downed);
            Try(() => { if (p.InMentalState && p.MentalStateDef != null) f.MentalBreak = (p.MentalStateDef.label ?? p.MentalStateDef.defName).ToLowerInvariant(); });
            Try(() => ReadHealth(p, f));
            Try(() => ReadCapacities(p, f));
            Try(() => ReadNeeds(p, f));
            Try(() => ReadSkills(p, f));
            Try(() =>
            {
                if (p.equipment != null && p.equipment.Primary != null) f.Weapon = p.equipment.Primary.LabelNoCount;
                if (p.apparel != null) f.Apparel = p.apparel.WornApparel.Select(a => a.LabelNoCount).ToList();
            });
            Try(() => ReadJobs(p, f));
            Try(() => ReadRelations(p, f));
            Try(() => ReadGuest(p, f));
            if (p.Spawned)
                Try(() =>
                {
                    int d; string k, room;
                    Rel(map, p.Position, out d, out k, out room);
                    f.Dist = d; f.DirKey = k; f.Room = room;
                });
            return Text(PawnFacts.Format(f));
        }

        static string PawnKind(Pawn p)
        {
            if (p.IsPrisonerOfColony) return "prisoner";
            if (p.IsSlaveOfColony) return "slave";
            if (p.IsColonist) return "colonist";
            if (p.Faction == Faction.OfPlayer && p.RaceProps.Animal) return "animal";
            if (VivariumHands.IsHostile(p) || (p.HostileTo(Faction.OfPlayer) && p.Spawned)) return "enemy";
            if (p.RaceProps.Animal) return "animal";
            if (p.guest != null && p.guest.HostFaction == Faction.OfPlayer) return "guest";
            return p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer) ? "guest" : "enemy";
        }

        static string XenoWord(Pawn p)
        {
            if (!ModsConfig.BiotechActive || p.genes == null) return "";
            if (p.genes.Xenotype == XenotypeDefOf.Baseliner && !p.genes.UniqueXenotype) return "";
            return (p.genes.XenotypeLabel ?? "").ToLowerInvariant();
        }

        static string RoleWord(Pawn p)
        {
            if (ModsConfig.IdeologyActive && p.Ideo != null)
            {
                var role = p.Ideo.GetRole(p);
                if (role != null) return role.LabelForPawn(p);
            }
            if (ModsConfig.RoyaltyActive && p.royalty != null)
            {
                var title = p.royalty.MostSeniorTitle;
                if (title != null && title.def != null) return title.def.GetLabelFor(p);
            }
            return "";
        }

        // ------------------------------------------------------------------ health
        static void ReadHealth(Pawn p, PawnFacts f)
        {
            if (p.health == null || p.health.hediffSet == null) return;
            var hs = p.health.hediffSet;
            var infected = new HashSet<BodyPartRecord>();
            foreach (var h in hs.hediffs)
                if (h.def == HediffDefOf.WoundInfection && h.Part != null) infected.Add(h.Part);
            foreach (var h in hs.hediffs)
            {
                try
                {
                    if (h.def == HediffDefOf.WoundInfection) continue;
                    var inj = h as Hediff_Injury;
                    var miss = h as Hediff_MissingPart;
                    if (inj != null)
                    {
                        double max = h.Part != null ? h.Part.def.GetMaxHealth(p) : 1;
                        f.Injuries.Add(new InjuryFact
                        {
                            Part = h.Part != null ? h.Part.Label : "", Label = (h.def.label ?? h.def.defName).ToLowerInvariant(),
                            Severity = max > 0 ? Math.Min(1.0, h.Severity / max) : 1, Bleeding = h.Bleeding,
                            Infected = h.Part != null && infected.Contains(h.Part), Permanent = h.IsPermanent()
                        });
                    }
                    else if (miss != null)
                    {
                        if (h.Part != null && h.Part.parent != null && hs.PartIsMissing(h.Part.parent)) continue;   // only the root of a lost limb
                        f.Injuries.Add(new InjuryFact { Part = h.Part != null ? h.Part.Label : "", Label = "missing", Severity = 1, Permanent = true });
                    }
                    else if (h.Visible && h.def.isBad && (h.def.lethalSeverity > 0 || h.def.makesSickThought || h.def.tendable))
                    {
                        double denom = h.def.lethalSeverity > 0 ? h.def.lethalSeverity : (h.def.maxSeverity > 0 && h.def.maxSeverity < 1000 ? h.def.maxSeverity : 1);
                        double imm = -1;
                        if (p.health.immunity != null && p.health.immunity.GetImmunityRecord(h.def) != null)
                            imm = Math.Min(1.0, p.health.immunity.GetImmunity(h.def, false));
                        f.Diseases.Add(new DiseaseFact
                        {
                            Label = (h.Label ?? h.def.label ?? h.def.defName).ToLowerInvariant(), Severity = Math.Max(0, Math.Min(1.0, h.Severity / denom)), Immunity = imm,
                            Lethal = h.CurStage != null && h.CurStage.lifeThreatening
                        });
                    }
                }
                catch (Exception) { }
            }
            f.Pain = Math.Min(1.0, hs.PainTotal);
        }

        static void ReadCapacities(Pawn p, PawnFacts f)
        {
            if (p.health == null || p.health.capacities == null) return;
            foreach (var def in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
            {
                try
                {
                    if (!def.showOnHumanlikes && p.RaceProps.Humanlike) continue;
                    if (!def.showOnAnimals && p.RaceProps.Animal) continue;
                    if (!PawnCapacityUtility.BodyCanEverDoCapacity(p.RaceProps.body, def)) continue;
                    float lvl = p.health.capacities.GetLevel(def);
                    if (lvl < CapacityShowBelow) f.LowCapacities.Add((def.label ?? def.defName).ToLowerInvariant() + " " + (int)Math.Round(lvl * 100) + "%");
                }
                catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ needs, mood, skills
        static void ReadNeeds(Pawn p, PawnFacts f)
        {
            if (p.needs == null) return;
            foreach (var n in p.needs.AllNeeds)
            {
                try
                {
                    if (n is Need_Mood || !n.ShowOnNeedList) continue;
                    f.Needs.Add(new NeedFact { Label = (n.def.label ?? n.def.defName).ToLowerInvariant(), Pct = (int)Math.Round(n.CurLevelPercentage * 100) });
                }
                catch (Exception) { }
            }
            var mood = p.needs.mood;
            if (mood != null)
            {
                f.Mood = (int)Math.Round(mood.CurLevelPercentage * 100);
                var groups = new List<Thought>();
                mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                foreach (var g in groups)
                {
                    try
                    {
                        int off = (int)Math.Round(mood.thoughts.MoodOffsetOfGroup(g));
                        f.Thoughts.Add(new ThoughtFact { Label = (g.LabelCap ?? g.def.defName).ToString().ToLowerInvariant(), Mood = off });
                    }
                    catch (Exception) { }
                }
            }
            var sup = p.needs.TryGetNeed<Need_Suppression>();
            if (sup != null && p.IsSlaveOfColony) f.Suppression = (int)Math.Round(sup.CurLevelPercentage * 100);
        }

        static void ReadSkills(Pawn p, PawnFacts f)
        {
            if (p.skills == null) return;
            foreach (var s in p.skills.skills)
            {
                if (s.TotallyDisabled) continue;
                f.Skills.Add(new SkillFact { Label = (s.def.label ?? s.def.defName).ToLowerInvariant(), Level = s.Level, Passion = s.passion == Passion.Major ? 2 : s.passion == Passion.Minor ? 1 : 0 });
            }
        }

        // ------------------------------------------------------------------ jobs, relations, guests
        static string JobWords(Pawn p, Job j)
        {
            string s = j.GetReport(p);
            return (s ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        }

        static void ReadJobs(Pawn p, PawnFacts f)
        {
            if (p.jobs == null) return;
            if (p.CurJob != null) f.Job = JobWords(p, p.CurJob);
            if (p.jobs.jobQueue != null)
                foreach (var qj in p.jobs.jobQueue)
                    if (qj != null && qj.job != null) f.Queue.Add(JobWords(p, qj.job));
        }

        static void ReadRelations(Pawn p, PawnFacts f)
        {
            if (p.relations == null) return;
            var rows = new List<KeyValuePair<float, string>>();
            foreach (var r in p.relations.DirectRelations)
            {
                try
                {
                    if (r.otherPawn == null || r.def == null || r.def.importance <= 0) continue;
                    rows.Add(new KeyValuePair<float, string>(r.def.importance, r.def.GetGenderSpecificLabel(r.otherPawn).ToLowerInvariant() + " " + r.otherPawn.LabelShort));
                }
                catch (Exception) { }
            }
            f.Relations = rows.OrderByDescending(x => x.Key).Take(3).Select(x => x.Value).ToList();
        }

        static void ReadGuest(Pawn p, PawnFacts f)
        {
            var g = p.guest;
            if (g == null) return;
            if (p.IsPrisonerOfColony)
            {
                f.RecruitResistance = g.Resistance;
                f.Will = g.will;
                if (g.ExclusiveInteractionMode != null) f.InteractionMode = (g.ExclusiveInteractionMode.label ?? g.ExclusiveInteractionMode.defName).ToLowerInvariant();
            }
            else if (p.IsSlaveOfColony && g.slaveInteractionMode != null)
                f.InteractionMode = (g.slaveInteractionMode.label ?? g.slaveInteractionMode.defName).ToLowerInvariant();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI;

namespace RimWorldLLM
{
    /// <summary>The Biotech hands (docs/VIVARIUM.md "Biotech"): mechs, genes, blood, children. Game thread. The routes
    /// checked shapes and the gene-implant setting; this does the game checks and applies the change through the calls
    /// the vanilla gizmos and float menus make.</summary>
    public sealed partial class VivariumHands : IVivariumBioHands, IVivariumCommandHands
    {
        static HandResult NoBiotech() { return ModsConfig.BiotechActive ? null : HandResult.No("Biotech is not active"); }

        static Pawn Mechanitor(int id, out HandResult no)
        {
            no = null;
            if (id > 0)
            {
                var p = Mine(id, out no);
                if (p == null) return null;
                if (!MechanitorUtility.IsMechanitor(p) || p.mechanitor == null) { no = HandResult.No(Name(p) + " is not a mechanitor"); return null; }
                return p;
            }
            var map = HomeMap();
            var m = map?.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => MechanitorUtility.IsMechanitor(p) && p.mechanitor != null);
            if (m == null) no = HandResult.No("the colony has no mechanitor");
            return m;
        }

        // ---- mechs ----
        public HandResult MechMode(int mechanitor, int group, string mode)
        {
            return Described(MechModeDo(mechanitor, group, mode), () => "set control group " + group + " to " + mode);
        }

        HandResult MechModeDo(int mechanitor, int group, string mode)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var m = Mechanitor(mechanitor, out no); if (m == null) return no;
                var groups = m.mechanitor.controlGroups;
                if (group < 1 || group > groups.Count) return HandResult.No(Name(m) + " has " + groups.Count + " control group" + (groups.Count == 1 ? "" : "s"));
                var def = DefDatabase<MechWorkModeDef>.GetNamedSilentFail(BioPure.ModeDef(mode));
                if (def == null) return HandResult.No("no mech mode " + mode);
                var g = groups[group - 1];
                if (g.WorkMode == def) return HandResult.No("group " + group + " is already on " + mode);
                g.SetWorkMode(def);
                return HandResult.Done("set " + Name(m) + "'s control group " + group + " (" + g.MechsForReading.Count + " mechs) to " + mode);
            }
            catch (Exception e) { return Failed(e); }
        }

        public HandResult MechGroup(int mech, int group)
        {
            return Described(MechGroupDo(mech, group), () => "move " + PawnName(mech) + " to group " + group);
        }

        HandResult MechGroupDo(int mech, int group)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var p = Routes.FindPawn(mech);
                if (p == null || !p.RaceProps.IsMechanoid || p.Faction != Faction.OfPlayer) return HandResult.No("no colony mech " + mech);
                if (p.Dead) return HandResult.No(p.LabelShort + " is dead");
                var ov = p.GetOverseer();
                if (ov == null || ov.mechanitor == null) return HandResult.No(p.LabelShort + " has no overseer");
                var groups = ov.mechanitor.controlGroups;
                if (group < 1 || group > groups.Count) return HandResult.No(Name(ov) + " has " + groups.Count + " control group" + (groups.Count == 1 ? "" : "s"));
                var cur = p.GetMechControlGroup();
                if (cur == groups[group - 1]) return HandResult.No(p.LabelShort + " is already in group " + group);
                cur?.TryUnassign(p);
                groups[group - 1].Assign(p);
                if (p.GetMechControlGroup() != groups[group - 1]) return HandResult.No("the game kept " + p.LabelShort + " in its group");
                return HandResult.Done("moved " + p.LabelShort + " to " + Name(ov) + "'s group " + group + " (" + BioPure.ModeWord(groups[group - 1].WorkMode?.defName) + ")");
            }
            catch (Exception e) { return Failed(e); }
        }

        static bool KindMatches(RecipeDef r, string kind)
        {
            var t = r.ProducedThingDef;
            return Eq(r.defName, kind) || (t != null && (Eq(t.defName, kind) || Eq(t.label, kind))) || Eq(r.label, kind);
        }

        public HandResult MechGestate(string kind, int gestator, int count, int headroom)
        {
            return Described(MechGestateDo(kind, gestator, count, headroom), () => "start gestating a " + kind);
        }

        HandResult MechGestateDo(string kind, int gestator, int count, int headroom)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var all = MechanitorUtility.MechRecipes.Where(r => r != null && r.ProducedThingDef != null).ToList();
                var recipe = all.FirstOrDefault(r => KindMatches(r, kind));
                if (recipe == null)
                    return HandResult.No("no mech called " + kind + "; the game has: " + string.Join(", ", all.Select(r => r.ProducedThingDef.label).Distinct().Take(12)));
                if (!recipe.AvailableNow)
                {
                    var miss = (recipe.researchPrerequisites ?? new List<ResearchProjectDef>()).Concat(recipe.researchPrerequisite != null ? new[] { recipe.researchPrerequisite } : new ResearchProjectDef[0])
                        .Where(r => !r.IsFinished).Select(r => r.LabelCap.ToString()).Distinct().ToList();
                    return HandResult.No(recipe.ProducedThingDef.label + " needs research first" + (miss.Count > 0 ? " (" + string.Join(", ", miss) + ")" : ""));
                }
                var m = map.mapPawns.FreeColonistsSpawned.Where(x => MechanitorUtility.IsMechanitor(x) && x.mechanitor != null)
                          .OrderByDescending(x => x.mechanitor.TotalBandwidth - x.mechanitor.UsedBandwidth).FirstOrDefault();
                if (m == null) return HandResult.No("the colony has no mechanitor");
                var gestators = map.listerBuildings.allBuildingsColonist.OfType<Building_MechGestator>().ToList();
                Building_MechGestator g;
                if (gestator > 0)
                {
                    g = gestators.FirstOrDefault(x => x.thingIDNumber == gestator);
                    if (g == null) return HandResult.No("no mech gestator " + gestator);
                    if (!g.def.AllRecipes.Contains(recipe)) return HandResult.No("that " + g.def.label + " can't make a " + recipe.ProducedThingDef.label);
                }
                else
                {
                    var able = gestators.Where(x => x.def.AllRecipes.Contains(recipe)).ToList();
                    g = able.FirstOrDefault(x => x.PoweredOn && x.ActiveMechBill == null && x.BillStack.Count == 0) ?? able.FirstOrDefault(x => x.PoweredOn) ?? able.FirstOrDefault();
                    if (g == null) return HandResult.No("no mech gestator that can make a " + recipe.ProducedThingDef.label + ": build one first");
                }
                int need = VivariumBioView.BandwidthOf(recipe.ProducedThingDef);
                var t = m.mechanitor;
                // bills queued but not bound to a mechanitor yet will take bandwidth too
                int queued = gestators.SelectMany(x => x.BillStack.Bills.OfType<Bill_Mech>()).Where(b => b.BoundPawn == null && !b.suspended)
                                      .Sum(b => (int)Math.Round(b.BandwidthCost * Math.Max(1, b.repeatMode == BillRepeatModeDefOf.RepeatCount ? b.repeatCount : 1)));
                string reason;
                if (!BioPure.BandwidthOk(t.UsedBandwidthFromSubjects, t.UsedBandwidthFromGestation + queued, t.TotalBandwidth, need, count, headroom, recipe.ProducedThingDef.label, out reason))
                    return HandResult.No(reason);
                var bill = recipe.MakeNewBill();
                var bm = bill as Bill_Mech;
                if (bm == null) return HandResult.No("that recipe is not a mech gestation");
                if (!t.HasBandwidthForBill(bm)) return HandResult.No("not enough bandwidth for a " + recipe.ProducedThingDef.label);
                var bp = bill as Bill_Production;
                if (bp != null) { bp.repeatMode = BillRepeatModeDefOf.RepeatCount; bp.repeatCount = Math.Max(1, count); }
                g.BillStack.AddBill(bill);
                var mark = new NarratorMark { Kind = "bill", Map = map.uniqueID, Id = g.thingIDNumber, Sub = BuildHelpers.LoadIdOf(bill.GetUniqueLoadID()), Def = recipe.defName };
                string note = "queued " + (count > 1 ? count + " " : "a ") + recipe.ProducedThingDef.label + (count > 1 ? "s" : "") + " at the " + g.def.label
                              + " (bandwidth " + (t.UsedBandwidthFromSubjects + t.UsedBandwidthFromGestation + queued + need * count) + " of " + t.TotalBandwidth + " once done)";
                if (!g.PoweredOn) note += "; it has no power yet";
                return HandResult.Placed(note, new List<NarratorMark> { mark });
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- genes ----
        static Pawn Hauler(Map map, Thing thing, Thing dest)
        {
            return map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Downed && !p.Drafted && !p.InMentalState && p.DevelopmentalStage.Adult() && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                            && p.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some) && p.CanReach(dest, PathEndMode.Touch, Danger.Some))
                .OrderBy(p => p.Position.DistanceToSquared(thing.Position)).FirstOrDefault();
        }

        public HandResult Genepack(int genepack, int bank)
        {
            return Described(GenepackDo(genepack, bank), () => "store genepacks");
        }

        HandResult GenepackDo(int genepack, int bank)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var packs = VivariumBioView.Unstored(map);
                if (genepack > 0) packs = packs.Where(p => p.thingIDNumber == genepack).ToList();
                if (packs.Count == 0) return HandResult.No(genepack > 0 ? "no loose genepack " + genepack : "no loose genepacks to store");
                var banks = map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompGenepackContainer>()).Where(c => c != null).ToList();
                if (bank > 0) banks = banks.Where(c => c.parent.thingIDNumber == bank).ToList();
                if (banks.Count == 0) return HandResult.No(bank > 0 ? "no gene bank " + bank : "no gene bank: build one first");
                banks = banks.Where(b => b.PowerOn).ToList();
                if (banks.Count == 0) return HandResult.No("the gene bank has no power");
                int sent = 0; var who = new HashSet<string>(); string why = null;
                var going = new Dictionary<CompGenepackContainer, int>();
                foreach (var pack in packs.Take(4))
                {
                    var c = banks.FirstOrDefault(b => b.CanLoadMore && b.ContainedGenepacks.Count + (going.TryGetValue(b, out int n) ? n : 0) < b.Props.maxCapacity);
                    if (c == null) { why = "the gene banks are full"; break; }
                    var h = Hauler(map, pack, c.parent);
                    if (h == null) { why = "no free colonist can carry it there"; break; }
                    var job = JobMaker.MakeJob(JobDefOf.CarryGenepackToContainer, pack, c.parent);
                    job.count = 1;
                    if (h.jobs.TryTakeOrderedJob(job, JobTag.Misc, true)) { sent++; who.Add(Name(h)); going[c] = (going.TryGetValue(c, out int k) ? k : 0) + 1; }
                }
                if (sent == 0) return HandResult.No(why ?? "no colonist took the job");
                return HandResult.Done(string.Join(" and ", who) + " will carry " + sent + " genepack" + (sent == 1 ? "" : "s") + " to the gene bank"
                                       + (packs.Count > sent ? " (" + (packs.Count - sent) + " left)" : ""));
            }
            catch (Exception e) { return Failed(e); }
        }

        static string Clip(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        public HandResult Xenogerm(XenogermArgs a)
        {
            return Described(a.Op == "implant" ? ImplantDo(a) : AssembleDo(a), () => a.Op == "implant" ? "implant a xenogerm" : "assemble a xenogerm");
        }

        HandResult AssembleDo(XenogermArgs a)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var asm = map.listerBuildings.allBuildingsColonist.OfType<Building_GeneAssembler>().FirstOrDefault();
                if (asm == null) return HandResult.No("no gene assembler: build one first");
                if (asm.Working) return HandResult.No("the gene assembler is already working");
                if (!asm.PowerOn) return HandResult.No("the gene assembler has no power");
                var avail = asm.GetGenepacks(true, false);
                var picked = new List<Genepack>();
                foreach (var key in a.Genepacks)
                {
                    int id;
                    var gp = int.TryParse(key, out id) ? avail.FirstOrDefault(x => x.thingIDNumber == id)
                                                       : avail.FirstOrDefault(x => !picked.Contains(x) && Eq(x.LabelNoCount, key));
                    if (gp == null) return HandResult.No("no genepack " + key + " in a gene bank next to the assembler" + (avail.Count > 0 ? "; there are: " + string.Join(", ", avail.Select(x => x.LabelNoCount).Distinct().Take(8)) : ""));
                    if (!picked.Contains(gp)) picked.Add(gp);
                }
                var genes = picked.SelectMany(p => p.GeneSet.GenesListForReading).ToList();
                var dup = genes.GroupBy(g => g).FirstOrDefault(x => x.Count() > 1);
                if (dup != null) return HandResult.No(dup.Key.label + " is in two of those packs");
                for (int x = 0; x < genes.Count; x++)
                    for (int y = x + 1; y < genes.Count; y++)
                        if (genes[x].ConflictsWith(genes[y])) return HandResult.No(genes[x].label + " conflicts with " + genes[y].label);
                int cpx = genes.Sum(g => g.biostatCpx), met = genes.Sum(g => g.biostatMet), arc = genes.Sum(g => g.biostatArc);
                if (cpx > asm.MaxComplexity()) return HandResult.No("complexity " + cpx + " is over the assembler's " + asm.MaxComplexity() + " (build gene processors)");
                if (met < GeneTuning.BiostatRange.TrueMin) return HandResult.No("metabolism " + met + " is below " + GeneTuning.BiostatRange.TrueMin);
                if (arc > 0 && asm.ArchitesCount < arc) return HandResult.No("it needs " + arc + " archite capsules");
                var icon = DefDatabase<XenotypeIconDef>.AllDefsListForReading.FirstOrDefault();
                if (icon == null) return HandResult.No("the game has no xenotype icons");
                string name = Clip(a.Name, 40);
                if (name.Length == 0) name = "xenogerm";
                asm.Start(picked, arc, name, icon);
                return HandResult.Done("started assembling the xenogerm \"" + name + "\" from " + string.Join(", ", picked.Select(p => p.LabelNoCount)) + " (complexity " + cpx + ", metabolism " + met + "; plan: " + Clip(a.Plan, 120) + ")");
            }
            catch (Exception e) { return Failed(e); }
        }

        HandResult ImplantDo(XenogermArgs a)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var germ = map.listerThings.ThingsOfDef(ThingDefOf.Xenogerm).OfType<Xenogerm>().FirstOrDefault(x => x.thingIDNumber == a.Xenogerm);
                if (germ == null) return HandResult.No("no xenogerm " + a.Xenogerm + " on the map");
                var p = Mine(a.Pawn, out no); if (p == null) return no;
                if (!p.DevelopmentalStage.Adult()) return HandResult.No(Name(p) + " is a child; the narrator never implants genes in children");
                if (!p.IsColonist) return HandResult.No(Name(p) + " is not a free colonist");
                if (germ.PawnIdeoDisallowsImplanting(p)) return HandResult.No(Name(p) + "'s beliefs forbid it");
                if (!germ.ValidateTarget(p, false)) return HandResult.No("the game won't let " + Name(p) + " take this xenogerm now");
                germ.SetTargetPawn(p);
                return HandResult.Done("set " + Name(p) + " to receive the xenogerm \"" + (germ.xenotypeName ?? germ.LabelNoCount) + "\" (a doctor implants it; plan: " + Clip(a.Plan, 120) + ")");
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- blood ----
        public HandResult Hemogen(string op, int prisoner)
        {
            return Described(HemogenDo(op, prisoner), () => op == "stop" ? "stop blood donation" : "set up blood donation");
        }

        HandResult HemogenDo(string op, int prisoner)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var p = Routes.FindPawn(prisoner);
                if (p == null || p.Dead) return HandResult.No("no prisoner " + prisoner);
                if (!p.IsPrisonerOfColony || p.guest == null) return HandResult.No(p.LabelShort + " is not the colony's prisoner");
                if (!p.DevelopmentalStage.Adult()) return HandResult.No(p.LabelShort + " is a child");
                if (p.genes?.GetFirstGeneOfType<Gene_Hemogen>() != null) return HandResult.No(p.LabelShort + " is hemogenic and can't give blood");
                var def = PrisonerInteractionModeDefOf.HemogenFarm;
                if (def == null) return HandResult.No("the game has no hemogen farm mode");
                bool on = op != "stop";
                bool was = p.guest.IsInteractionEnabled(def);
                if (was == on) return HandResult.No(p.LabelShort + (on ? " is already giving blood" : " is not giving blood"));
                if (def.isNonExclusiveInteraction) p.guest.ToggleNonExclusiveInteraction(def, on);
                else p.guest.SetExclusiveInteraction(on ? def : PrisonerInteractionModeDefOf.MaintainOnly);
                return HandResult.Done(on ? "set prisoner " + p.LabelShort + " to give blood (hemogen farm)" : "stopped taking blood from prisoner " + p.LabelShort);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- children ----
        static TimeAssignmentDef Assignment(string letter)
        {
            switch (letter)
            {
                case "S": return TimeAssignmentDefOf.Sleep;
                case "J": return TimeAssignmentDefOf.Joy;
                case "W": return TimeAssignmentDefOf.Work;
                default: return TimeAssignmentDefOf.Anything;
            }
        }

        public HandResult ChildSchedule(int pawn, string preset, string[] hours)
        {
            return Described(ChildScheduleDo(pawn, preset), () => "set " + PawnName(pawn) + "'s day");
        }

        HandResult ChildScheduleDo(int pawn, string preset)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var p = Mine(pawn, out no); if (p == null) return no;
                if (!p.DevelopmentalStage.Juvenile()) return HandResult.No(Name(p) + " is not a child");
                if (p.timetable == null) return HandResult.No(Name(p) + " has no schedule (babies keep their own rhythm)");
                int age = p.ageTracker.AgeBiologicalYears;
                var h = BioPure.ChildHours(preset, age);
                if (h == null) return HandResult.No(Name(p) + " is not a child");
                string reason = BioPure.CheckChildHours(age, h);
                if (reason != null) return HandResult.No(reason);
                for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, Assignment(h[i]));
                string word = "the " + preset + " day (" + BioWords.Hours(h) + ")";
                return HandResult.Done("set " + Name(p) + "'s schedule to " + word);
            }
            catch (Exception e) { return Failed(e); }
        }

        public HandResult Feed(int baby, int feeder, string mode)
        {
            return Described(FeedDo(baby, feeder, mode), () => "set who feeds " + PawnName(baby));
        }

        HandResult FeedDo(int baby, int feeder, string mode)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var b = Mine(baby, out no); if (b == null) return no;
                if (!(b.DevelopmentalStage.Baby() || b.DevelopmentalStage.Newborn())) return HandResult.No(Name(b) + " is not a baby");
                AutofeedMode m = mode == "never" ? AutofeedMode.Never : mode == "urgent" ? AutofeedMode.Urgent : AutofeedMode.Childcare;
                if (feeder == 0)
                {
                    var set = b.mindState.Autofeeders().ToList();
                    foreach (var f in set) b.mindState.SetAutofeeder(f, AutofeedMode.Childcare);
                    return HandResult.Done("reset who feeds " + Name(b) + " to the carers' default");
                }
                var who = Mine(feeder, out no); if (who == null) return no;
                if (who == b) return HandResult.No("a baby can't feed itself");
                if (!who.DevelopmentalStage.Adult()) return HandResult.No(Name(who) + " is a child");
                if (m != AutofeedMode.Never)
                {
                    ChildcareUtility.BreastfeedFailReason? r;
                    if (!ChildcareUtility.CanFeed(who, out r)) return HandResult.No(Name(who) + " can't feed babies now");
                    if (who.WorkTypeIsDisabled(WorkTypeDefOf.Childcare) && !ChildcareUtility.CanBreastfeed(who, out r)) return HandResult.No(Name(who) + " can't do childcare");
                }
                b.mindState.SetAutofeeder(who, m);
                string how = m == AutofeedMode.Never ? "never feeds " : m == AutofeedMode.Urgent ? "feeds " + Name(b) + " first, even before work" : "feeds ";
                string note = m == AutofeedMode.Urgent ? Name(who) + " " + how : Name(who) + " " + how + Name(b) + (m == AutofeedMode.Childcare ? " during childcare" : "");
                return HandResult.Done("set it so " + note + (m == AutofeedMode.Never ? "" : ChildcareUtility.CanBreastfeed(who, out _) ? " (breastfeeding)" : " (baby food)"));
            }
            catch (Exception e) { return Failed(e); }
        }

        public HandResult ChildCare(int pawn, int priority)
        {
            return Described(ChildCareDo(pawn, priority), () => "set " + PawnName(pawn) + "'s child care");
        }

        HandResult ChildCareDo(int pawn, int priority)
        {
            try
            {
                HandResult no = NoBiotech(); if (no != null) return no;
                var p = Mine(pawn, out no); if (p == null) return no;
                if (!p.DevelopmentalStage.Adult()) return HandResult.No(Name(p) + " is a child");
                var w = WorkTypeDefOf.Childcare;
                if (w == null) return HandResult.No("the game has no Childcare work");
                if (p.WorkTypeIsDisabled(w)) return HandResult.No(Name(p) + " is incapable of Childcare");
                return SetWork(pawn, new Dictionary<string, int> { { w.defName, priority } });   // the work hand: priorities on, every pawn refreshed
            }
            catch (Exception e) { return Failed(e); }
        }
    }
}

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
    /// <summary>The game bodies of the narrator's hands. Game thread only. The routes already checked shapes and
    /// mod settings; this applies the change through the same calls the vanilla UI makes.</summary>
    public sealed partial class VivariumHands : IVivariumHands
    {
        static bool _warned;

        internal static HandResult Failed(Exception e)
        {
            if (!_warned) { _warned = true; Log.Warning("[RimWorldLLM] vivarium hand failed: " + e); }
            return HandResult.No("the game refused: " + e.Message);
        }

        static bool Eq(string a, string b) { return string.Equals(a == null ? null : a.Trim(), b == null ? null : b.Trim(), StringComparison.OrdinalIgnoreCase); }

        static string Name(Pawn p) { return p.LabelShort; }

        static string PawnName(int id)
        {
            try { var p = Routes.FindPawn(id); return p != null ? p.LabelShort : "pawn " + id; } catch (Exception) { return "pawn " + id; }
        }

        /// <summary>Fills in what was attempted on a refusal, so the chat can say "couldn't {what}: {reason}".</summary>
        static HandResult Described(HandResult r, Func<string> what)
        {
            if (r != null && !r.Ok && string.IsNullOrEmpty(r.What))
                try { r.What = what(); } catch (Exception) { }
            return r;
        }

        /// <summary>A colonist or slave of the player standing on a map, or the reason not.</summary>
        static Pawn Mine(int id, out HandResult no)
        {
            no = null;
            var p = Routes.FindPawn(id);
            if (p == null) { no = HandResult.No("no pawn " + id); return null; }
            if (p.Dead || p.Destroyed) { no = HandResult.No(Name(p) + " is dead"); return null; }
            if (!(p.IsColonist || p.IsSlaveOfColony) || p.Map == null || !p.Spawned)
            {
                no = HandResult.No(Name(p) + " is not a colonist or slave on a map");
                return null;
            }
            return p;
        }

        public JObject Digest() { return VivariumDigest.Build(); }

        // ---- time ----
        public HandResult SetTime(bool? pause, int? speed)
        {
            return Described(SetTimeDo(pause, speed), () => VivariumHandsPure.TimeWhat(pause, speed));
        }

        HandResult SetTimeDo(bool? pause, int? speed)
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return HandResult.No("no game running");
                var notes = new List<string>();
                if (speed.HasValue)
                {
                    TimeSpeed s;
                    switch (speed.Value)
                    {
                        case 1: s = TimeSpeed.Normal; break;
                        case 2: s = TimeSpeed.Fast; break;
                        case 3: s = TimeSpeed.Superfast; break;
                        case 4: s = TimeSpeed.Ultrafast; break;
                        default: return HandResult.No("speed must be 1 to 4");
                    }
                    tm.CurTimeSpeed = s;   // like the speed buttons; also unpauses
                    notes.Add("s");
                }
                if (pause.HasValue)
                {
                    if (pause.Value) { if (!tm.Paused) tm.Pause(); notes.Add("p"); }
                    else { if (tm.Paused) tm.TogglePaused(); notes.Add("p"); }
                }
                if (notes.Count == 0) return HandResult.No("nothing to change");
                return HandResult.Done(VivariumHandsPure.TimeNote(pause, speed));
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- work ----
        static WorkTypeDef FindWork(string key)
        {
            foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                if (Eq(w.defName, key)) return w;
            foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                if (Eq(w.labelShort, key) || Eq(w.label, key) || Eq(w.gerundLabel, key)) return w;
            return null;
        }

        public HandResult SetWork(int pawnId, IDictionary<string, int> priorities)
        {
            return Described(SetWorkDo(pawnId, priorities), () => "change " + VivariumHandsPure.Possessive(PawnName(pawnId)) + " work priorities");
        }

        HandResult SetWorkDo(int pawnId, IDictionary<string, int> priorities)
        {
            try
            {
                HandResult no;
                var pawn = Mine(pawnId, out no);
                if (pawn == null) return no;
                if (priorities == null || priorities.Count == 0) return HandResult.No("no work priorities given");
                if (pawn.workSettings == null) return HandResult.No(Name(pawn) + " does not take work orders");
                var done = new List<string>();
                var refused = new List<string>();
                foreach (var kv in priorities)
                {
                    var w = FindWork(kv.Key);
                    if (w == null) { refused.Add(kv.Key + ": no such work type"); continue; }
                    string lbl = w.labelShort.NullOrEmpty() ? w.defName : w.labelShort;
                    if (kv.Value < 0 || kv.Value > 4) { refused.Add(lbl + ": priority must be 0 to 4"); continue; }
                    if (pawn.WorkTypeIsDisabled(w)) { refused.Add(VivariumStuck.Refusal(pawn, w, lbl)); continue; }
                    done.Add(lbl + "\t" + kv.Value);
                    if (!pawn.workSettings.Initialized) pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
                    // work priorities must be on for numbers to matter; turned on once below
                    if (Current.Game != null && !Current.Game.playSettings.useWorkPriorities)
                    {
                        Current.Game.playSettings.useWorkPriorities = true;
                        foreach (var p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
                            if (p.workSettings != null) p.workSettings.Notify_UseWorkPrioritiesChanged();
                        done.Add("(turned work priorities on)");
                    }
                    pawn.workSettings.SetPriority(w, kv.Value);
                }
                if (done.Count == 0 || done.All(s => s.StartsWith("(")))
                    return HandResult.No(string.Join("; ", refused));
                // Entries in done that are real settings all succeeded.
                var set = done.Where(s => !s.StartsWith("(")).ToList();
                var extra = done.Where(s => s.StartsWith("(")).ToList();
                var pairs = set.Select(x => { var q = x.Split('\t'); return new KeyValuePair<string, int>(q[0], int.Parse(q[1])); }).ToList();
                string note = VivariumHandsPure.WorkNote(Name(pawn), pairs);
                if (extra.Count > 0) note += " (turned work priorities on)";
                if (refused.Count > 0) note += " (couldn't: " + string.Join(", ", refused) + ")";
                return HandResult.Done(note);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- schedule ----
        public HandResult SetSchedule(int pawnId, int fromHour, int toHour, string assignment)
        {
            return Described(SetScheduleDo(pawnId, fromHour, toHour, assignment), () => "change " + VivariumHandsPure.Possessive(PawnName(pawnId)) + " schedule");
        }

        HandResult SetScheduleDo(int pawnId, int fromHour, int toHour, string assignment)
        {
            try
            {
                HandResult no;
                var pawn = Mine(pawnId, out no);
                if (pawn == null) return no;
                if (pawn.timetable == null) return HandResult.No(Name(pawn) + " has no schedule");
                TimeAssignmentDef def;
                switch ((assignment ?? "").Trim().ToLowerInvariant())
                {
                    case "anything": def = TimeAssignmentDefOf.Anything; break;
                    case "work": def = TimeAssignmentDefOf.Work; break;
                    case "joy": def = TimeAssignmentDefOf.Joy; break;
                    case "sleep": def = TimeAssignmentDefOf.Sleep; break;
                    case "meditate":
                        def = TimeAssignmentDefOf.Meditate;
                        if (def == null) return HandResult.No("meditation hours are not in this game");
                        break;
                    default: return HandResult.No("unknown schedule block '" + assignment + "'");
                }
                var hours = VivariumHandsPure.HourRange(fromHour, toHour);
                if (hours.Count == 0) return HandResult.No("hours " + fromHour + " to " + toHour + " are not a valid range");
                foreach (int h in hours) pawn.timetable.SetAssignment(h, def);
                return HandResult.Done(VivariumHandsPure.ScheduleNote(Name(pawn), fromHour, toHour, assignment));
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- policies ----
        static string Sample(IEnumerable<string> names)
        {
            return string.Join(", ", names.Where(n => !n.NullOrEmpty()).Take(6));
        }

        public HandResult SetPolicy(int pawnId, string kind, string name)
        {
            return Described(SetPolicyDo(pawnId, kind, name), () => VivariumHandsPure.PolicyWhat(PawnName(pawnId), kind, name));
        }

        HandResult SetPolicyDo(int pawnId, string kind, string name)
        {
            try
            {
                HandResult no;
                var pawn = Mine(pawnId, out no);
                if (pawn == null) return no;
                var game = Current.Game;
                if (game == null) return HandResult.No("no game running");
                switch ((kind ?? "").Trim().ToLowerInvariant())
                {
                    case "outfit":
                    {
                        if (pawn.outfits == null) return HandResult.No(Name(pawn) + " has no outfit");
                        var all = game.outfitDatabase.AllOutfits;
                        var hit = all.FirstOrDefault(o => Eq(o.label, name));
                        if (hit == null) return HandResult.No("no outfit '" + name + "'; there are: " + Sample(all.Select(o => o.label)));
                        pawn.outfits.CurrentApparelPolicy = hit;
                        return HandResult.Done(VivariumHandsPure.PolicyNote(Name(pawn), "outfit", hit.label));
                    }
                    case "food":
                    {
                        if (pawn.foodRestriction == null) return HandResult.No(Name(pawn) + " has no food restriction");
                        var all = game.foodRestrictionDatabase.AllFoodRestrictions;
                        var hit = all.FirstOrDefault(o => Eq(o.label, name));
                        if (hit == null) return HandResult.No("no food policy '" + name + "'; there are: " + Sample(all.Select(o => o.label)));
                        pawn.foodRestriction.CurrentFoodPolicy = hit;
                        return HandResult.Done(VivariumHandsPure.PolicyNote(Name(pawn), "food", hit.label));
                    }
                    case "drug":
                    {
                        if (pawn.drugs == null) return HandResult.No(Name(pawn) + " has no drug policy");
                        var all = game.drugPolicyDatabase.AllPolicies;
                        var hit = all.FirstOrDefault(o => Eq(o.label, name));
                        if (hit == null) return HandResult.No("no drug policy '" + name + "'; there are: " + Sample(all.Select(o => o.label)));
                        pawn.drugs.CurrentPolicy = hit;
                        return HandResult.Done(VivariumHandsPure.PolicyNote(Name(pawn), "drug", hit.label));
                    }
                    default: return HandResult.No("unknown policy kind '" + kind + "'");
                }
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- bed ----
        public HandResult AssignBed(int pawnId, int bedId)
        {
            return Described(AssignBedDo(pawnId, bedId), () => "give " + PawnName(pawnId) + " that bed");
        }

        HandResult AssignBedDo(int pawnId, int bedId)
        {
            try
            {
                HandResult no;
                var pawn = Mine(pawnId, out no);
                if (pawn == null) return no;
                Building_Bed bed = null;
                foreach (var map in Find.Maps)
                {
                    bed = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().FirstOrDefault(b => b.thingIDNumber == bedId);
                    if (bed != null) break;
                }
                if (bed == null) return HandResult.No("no colony bed " + bedId);
                if (bed.Map != pawn.Map) return HandResult.No("that bed is on another map than " + Name(pawn));
                if (bed.Medical) return HandResult.No("that is a medical bed; patients are placed there, not assigned");
                var comp = bed.TryGetComp<CompAssignableToPawn>();
                if (comp == null) return HandResult.No("that bed cannot be assigned");
                if (bed.OwnersForReading.Contains(pawn)) return HandResult.Done(Name(pawn) + " already had that bed");
                var ok = comp.CanAssignTo(pawn);
                if (!ok.Accepted) return HandResult.No(Name(pawn) + " cannot take that bed" + (ok.Reason.NullOrEmpty() ? "" : ": " + ok.Reason));
                if (!comp.HasFreeSlot)
                {
                    // vanilla's bed gizmo lets you click a pawn; with no free slot the oldest owner is replaced
                    // by TryAssignPawn itself for beds, so a full multi-bed is the one thing we refuse.
                    if (comp.MaxAssignedPawnsCount > 1) return HandResult.No("that bed has no free place");
                }
                comp.TryAssignPawn(pawn);
                if (!bed.OwnersForReading.Contains(pawn)) return HandResult.No("the game would not assign that bed to " + Name(pawn));
                return HandResult.Done("gave " + Name(pawn) + " the bed " + bed.LabelCap + " (" + bed.thingIDNumber + ")");
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- research ----
        public HandResult SetResearch(string project)
        {
            return Described(SetResearchDo(project), () => "start researching " + project);
        }

        HandResult SetResearchDo(string project)
        {
            try
            {
                var rm = Find.ResearchManager;
                if (rm == null) return HandResult.No("no game running");
                ResearchProjectDef def = null;
                foreach (var d in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                    if (Eq(d.defName, project)) { def = d; break; }
                if (def == null)
                    foreach (var d in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                        if (Eq(d.label, project) || Eq(d.LabelCap.ToString(), project)) { def = d; break; }
                if (def == null) return HandResult.No("no research project '" + project + "'");
                if (def.IsFinished) return HandResult.No(def.LabelCap + " is already finished");
                if (def.IsHidden) return HandResult.No(def.LabelCap + " is not known yet");
                if (!def.PrerequisitesCompleted)
                {
                    var missing = (def.prerequisites ?? new List<ResearchProjectDef>()).Where(p => !p.IsFinished).Select(p => p.LabelCap.ToString());
                    return HandResult.No(def.LabelCap + " needs " + string.Join(", ", missing) + " first");
                }
                if (!def.CanStartNow) return HandResult.No(def.LabelCap + " cannot be started yet (techprints or other requirements missing)");
                if (rm.IsCurrentProject(def)) return HandResult.Done("kept researching " + def.LabelCap + " (already the current project)");
                rm.SetCurrentProject(def);
                return HandResult.Done("started researching " + def.LabelCap);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- letters ----
        static readonly System.Reflection.FieldInfo TextField =
            typeof(DiaOption).GetField("text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        /// <summary>DiaOption.text is protected in 1.7; the label is read by reflection.</summary>
        static string OptText(DiaOption o)
        {
            try { return TextField != null ? (TextField.GetValue(o) as string) ?? "" : ""; } catch { return ""; }
        }

        public HandResult AnswerLetter(int letterId, string choice)
        {
            return Described(AnswerLetterDo(letterId, choice), () => "answer the letter with " + choice);
        }

        HandResult AnswerLetterDo(int letterId, string choice)
        {
            try
            {
                var stack = Find.LetterStack;
                if (stack == null) return HandResult.No("no game running");
                Letter letter = stack.LettersListForReading.FirstOrDefault(l => l.ID == letterId);
                if (letter == null) return HandResult.No("no letter " + letterId + " in the stack (maybe already answered)");
                string c = (choice ?? "").Trim();
                string title = letter.Label.ToString();
                if (Eq(c, "dismiss"))
                {
                    if (!letter.CanDismissWithRightClick) return HandResult.No("\"" + title + "\" needs an answer, it cannot be dismissed");
                    stack.RemoveLetter(letter);
                    return HandResult.Done("dismissed the letter \"" + title + "\"");
                }
                var cl = letter as ChoiceLetter;
                if (cl == null) return HandResult.No("\"" + title + "\" has no choices (only \"dismiss\")");
                var opts = cl.Choices.ToList();
                DiaOption opt = null;
                if (c.StartsWith("#"))
                {
                    int i;
                    if (int.TryParse(c.Substring(1), out i) && i >= 0 && i < opts.Count) opt = opts[i];
                    else return HandResult.No("no choice " + c + "; choices: " + Describe(opts));
                }
                else opt = opts.FirstOrDefault(o => Eq(OptText(o), c));
                if (opt == null) return HandResult.No("no choice '" + c + "'; choices: " + Describe(opts));
                if (opt.disabled) return HandResult.No("'" + OptText(opt) + "' is not available" + (opt.disabledReason.NullOrEmpty() ? "" : ": " + opt.disabledReason));
                if (opt.action == null)
                    return HandResult.No("'" + OptText(opt) + "' only opens a window; pick another choice or \"dismiss\"");
                // The dialog runs Activate(): action, then link/resolveTree window handling. Letter options carry no
                // dialog node of their own, so we run the action (which removes the letter itself where vanilla does).
                opt.action();
                return HandResult.Done("answered \"" + title + "\" with " + OptText(opt));
            }
            catch (Exception e) { return Failed(e); }
        }

        public HandResult Choose(DialogRecord dialog, int option, IDictionary<string, string> texts)
        {
            return Described(VivariumDialogHost.Answer(dialog, option, texts), () => "answer the dialog \"" + (dialog != null ? dialog.Title : "") + "\"");
        }

        static string Describe(List<DiaOption> opts)
        {
            return string.Join(", ", opts.Select((o, i) => "#" + i + " " + OptText(o)));
        }

        // ---- draft ----
        public HandResult Draft(IList<int> pawns, bool drafted)
        {
            return Described(DraftDo(pawns, drafted), () => VivariumHandsPure.DraftWhat((pawns ?? new int[0]).Select(PawnName).ToList(), drafted));
        }

        HandResult DraftDo(IList<int> pawns, bool drafted)
        {
            try
            {
                if (pawns == null || pawns.Count == 0) return HandResult.No("no pawns given");
                var changed = new List<string>();
                var skipped = new List<string>();
                foreach (int id in pawns)
                {
                    HandResult no;
                    var p = Mine(id, out no);
                    if (p == null) { skipped.Add(no.Reason); continue; }
                    if (p.IsSlave && !p.IsColonist) { skipped.Add(Name(p) + " is a slave"); continue; }
                    if (p.drafter == null) { skipped.Add(Name(p) + " cannot be drafted"); continue; }
                    if (drafted && p.WorkTagIsDisabled(WorkTags.Violent)) { skipped.Add(Name(p) + " is incapable of violence"); continue; }
                    if (drafted && p.Downed) { skipped.Add(Name(p) + " is down"); continue; }
                    if (p.drafter.Drafted == drafted) { skipped.Add(Name(p) + " already " + (drafted ? "drafted" : "undrafted")); continue; }
                    p.drafter.Drafted = drafted;
                    changed.Add(Name(p));
                }
                if (changed.Count == 0) return HandResult.No(string.Join("; ", skipped));
                string note = VivariumHandsPure.DraftNote(changed, drafted);
                if (skipped.Count > 0) note += " (" + string.Join("; ", skipped) + ")";
                return HandResult.Done(note);
            }
            catch (Exception e) { return Failed(e); }
        }

        // ---- orders ----
        public HandResult Order(int pawnId, string order, int x, int z, int target, bool hold)
        {
            return Described(OrderDo(pawnId, order, x, z, target, hold), () => VivariumHandsPure.OrderWhat(PawnName(pawnId), order, x, z, "target " + target, hold));
        }

        HandResult OrderDo(int pawnId, string order, int x, int z, int target, bool hold)
        {
            try
            {
                HandResult no;
                var pawn = Mine(pawnId, out no);
                if (pawn == null) return no;
                if (pawn.drafter == null || !pawn.Drafted) return HandResult.No(Name(pawn) + " is not drafted; draft first");
                switch ((order ?? "").Trim().ToLowerInvariant())
                {
                    case "move":
                    {
                        var cell = new IntVec3(x, 0, z);
                        var map = pawn.Map;
                        if (!cell.InBounds(map)) return HandResult.No("(" + x + "," + z + ") is outside the map");
                        if (cell.Fogged(map)) return HandResult.No("(" + x + "," + z + ") is not visible yet");
                        if (!cell.Standable(map)) return HandResult.No("(" + x + "," + z + ") cannot be stood on");
                        if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return HandResult.No(Name(pawn) + " cannot reach (" + x + "," + z + ")");
                        var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                        job.playerForced = true;   // as the right-click "Go here" does
                        if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return HandResult.No(Name(pawn) + " could not take the order");
                        return HandResult.Done(VivariumHandsPure.OrderNote(Name(pawn), "move", x, z, null, false));
                    }
                    case "attack":
                    {
                        Thing t = null;
                        foreach (var th in pawn.Map.listerThings.AllThings)
                            if (th.thingIDNumber == target) { t = th; break; }
                        if (t == null) return HandResult.No("no target " + target + " on " + Name(pawn) + "'s map");
                        if (t.Fogged()) return HandResult.No("target is not visible");
                        bool hostilePawn = t is Pawn && !((Pawn)t).Dead && pawn.HostileTo(t);
                        bool hostileThing = t is Building && t.Faction != null && t.Faction.HostileTo(Faction.OfPlayer);
                        if (!hostilePawn && !hostileThing) return HandResult.No(t.LabelShort + " is not a hostile target");
                        string fail;
                        Action act = FloatMenuUtility.GetAttackAction(pawn, t, out fail);
                        if (act == null) return HandResult.No(Name(pawn) + " cannot attack " + t.LabelShort + (fail.NullOrEmpty() ? "" : ": " + fail));
                        act();
                        return HandResult.Done(VivariumHandsPure.OrderNote(Name(pawn), "attack", 0, 0, t.LabelShort, false));
                    }
                    case "hold":
                    {
                        // Vanilla has no "hold position" order. Closest concept: stop what the pawn is doing where it
                        // stands, and hold fire (the drafted gizmo's "hold fire" = FireAtWill off). hold=false gives
                        // fire-at-will back.
                        if (hold)
                        {
                            pawn.jobs.StopAll();
                            pawn.drafter.FireAtWill = false;
                            return HandResult.Done(VivariumHandsPure.OrderNote(Name(pawn), "hold", 0, 0, null, true) + " and held fire");
                        }
                        pawn.drafter.FireAtWill = true;
                        return HandResult.Done(VivariumHandsPure.OrderNote(Name(pawn), "hold", 0, 0, null, false));
                    }
                    default: return HandResult.No("unknown order '" + order + "'");
                }
            }
            catch (Exception e) { return Failed(e); }
        }
    }
}

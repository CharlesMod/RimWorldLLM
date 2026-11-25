using System;
using System.Collections.Generic;
using HarmonyLib;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HermesVoice
{
    /// <summary>POST /social_action: one small social move the agent chose (seek_out or avoid). Never romantic, never forced.</summary>
    public static partial class Routes
    {
        static void RegisterWants(ApiRouter r)
        {
            r.Add("POST", "/social_action", true, SocialAction);
        }

        static ApiResult SocialAction(RouteContext c)
        {
            SocialRequest req;
            string bad = SocialRequest.Parse(c.Body, out req);
            if (bad != null) return ApiResult.Fail("bad_request", bad);
            var pawn = FindPawn(req.Pawn);
            if (pawn == null) return ApiResult.Fail("not_found", "no pawn " + req.Pawn + " (pawn)");
            var target = FindPawn(req.Target);
            if (target == null) return ApiResult.Fail("not_found", "no pawn " + req.Target + " (target)");

            bool seek = req.Action == "seek_out";
            var s = HermesVoiceMod.Settings;
            var facts = WantsProbe.Collect(pawn, target, seek, s != null && s.wantsEnabled);
            string reason = WantsGate.Refusal(facts);
            if (reason != null) return ApiResult.Fail("refused", reason, new JObject { ["reason"] = reason });

            int pa = pawn.ageTracker.AgeBiologicalYears, ta = target.ageTracker.AgeBiologicalYears;
            string topic = SocialRequest.GuardTopic(req.Topic, pa, ta);
            var res = new JObject { ["applied"] = true };
            if (req.Topic.Length > 0 && topic.Length == 0) res["topic_dropped"] = true;

            if (!seek)
            {
                var comp = HermesVoiceGameComponent.Instance;
                if (comp == null) return ApiResult.Fail("no_game", "no game component");
                int now = Find.TickManager.TicksGame;
                comp.Avoids.Add(pawn.thingIDNumber, target.thingIDNumber, now, AvoidTable.HoursToTicks(s.wantsAvoidHours));
                return ApiResult.Success(res);
            }

            var def = DefDatabase<JobDef>.GetNamedSilentFail("HV_SeekOut");
            if (def == null) return ApiResult.Fail("internal", "HV_SeekOut job def is missing");
            var job = JobMaker.MakeJob(def, target);
            job.playerForced = false;
            job.expiryInterval = JobDriver_SeekOut.MaxTicks;
            job.locomotionUrgency = LocomotionUrgency.Walk;
            // Optional interrupt of the current (interruptible) job, which resumes afterwards.
            pawn.jobs.StartJob(job, lastJobEndCondition: JobCondition.InterruptOptional, resumeCurJobAfterwards: true);
            return ApiResult.Success(res);
        }
    }

    /// <summary>Reads the game state the refusal decision needs (all of it on the game thread).</summary>
    public static class WantsProbe
    {
        // Jobs that must never be interrupted for a chat (conservative).
        static readonly HashSet<string> Blocked = new HashSet<string>
        {
            "Rescue", "Capture", "Arrest", "TendPatient", "Tend", "FeedPatient", "Wait_Combat", "AttackMelee", "AttackStatic",
            "ManTurret", "Flee", "FleeAndCower", "GotoSafeTemperature", "ExtinguishSelf", "Vomit", "Deathrest", "Lovin",
            "MarryAdjacentPawn", "SpectateCeremony", "GiveSpeech", "BeatFire", "Wait_Downed", "ReleasePrisoner", "Kidnap",
            "CarryToCryptosleepCasket", "DeliverToBed", "Hunt", "Slaughter", "BringBabyToSafety", "Breastfeed", "BottleFeedBaby",
            "ReviveCorpse"
        };

        public static WantsFacts Collect(Pawn p, Pawn target, bool seek, bool enabled)
        {
            var f = new WantsFacts { Enabled = enabled, Action_SeekOut = seek };
            f.Dead = p.Dead;
            f.Spawned = p.Spawned;
            f.SameMap = p.Spawned && target.Spawned && p.Map == target.Map;
            if (!f.Spawned || f.Dead) return f;
            f.Downed = p.Downed;
            f.Drafted = p.Drafted;
            f.Asleep = !p.Awake();
            f.MentalState = p.InMentalState;
            var job = p.CurJob;
            bool forcedJob = job != null && job.playerForced;
            f.ForcedWork = (p.IsPrisoner || p.IsSlave) && (forcedJob || (job != null && job.workGiverDef != null));
            f.InCombat = p.mindState != null && (p.mindState.enemyTarget != null || p.mindState.meleeThreat != null)
                || (p.Faction == Faction.OfPlayer && p.Map.dangerWatcher.DangerRating != StoryDanger.None);
            f.JobBlocks = forcedJob || p.GetLord() != null || (job != null && (!job.def.casualInterruptible || Blocked.Contains(job.def.defName)))
                || (p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p));
            f.TargetSpawned = target.Spawned && !target.Dead;
            f.TargetReachable = seek && f.TargetSpawned && f.SameMap && !p.HostileTo(target)
                && p.CanReach(target, PathEndMode.Touch, Danger.Some);
            return f;
        }
    }

    /// <summary>Walk to the target at chatting distance, then start one normal vanilla chat (never a romance interaction).</summary>
    public class JobDriver_SeekOut : JobDriver
    {
        public const int MaxTicks = 1500;       // game ticks the whole job may take
        const long MaxRealMs = 60000;           // and real time (the pace the player sees at any speed)
        long _startReal;

        Pawn Other { get { return (Pawn)job.GetTarget(TargetIndex.A).Thing; } }

        public override bool TryMakePreToilReservations(bool errorOnFailed) { return true; }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Other.Dead || Other.Downed || Other.InMentalState || pawn.HostileTo(Other));
            _startReal = Environment.TickCount64;
            var go = Toils_Interpersonal.GotoInteractablePosition(TargetIndex.A);
            go.AddPreTickAction(() => { if (Environment.TickCount64 - _startReal > MaxRealMs) EndJobWith(JobCondition.Incompletable); });
            yield return go;
            yield return new Toil
            {
                defaultCompleteMode = ToilCompleteMode.Instant,
                initAction = () =>
                {
                    var o = Other;
                    if (!SocialInteractionUtility.CanInitiateInteraction(pawn) || !SocialInteractionUtility.CanReceiveInteraction(o)) return;
                    // Vanilla only: Chitchat, or DeepTalk between two adults. Never a romance interaction.
                    bool adults = pawn.ageTracker.AgeBiologicalYears >= SocialRequest.AdultAge && o.ageTracker.AgeBiologicalYears >= SocialRequest.AdultAge;
                    var def = adults && Rand.Chance(0.35f) ? InteractionDefOf.DeepTalk : InteractionDefOf.Chitchat;
                    WantsHooks.Bypass = true;
                    try { pawn.interactions.TryInteractWith(o, def); }
                    finally { WantsHooks.Bypass = false; }
                }
            };
        }
    }

    public static class WantsHooks
    {
        /// <summary>True while the mod itself starts a chat (seek_out), so the avoid table does not block it.</summary>
        public static bool Bypass;
    }

    /// <summary>A pawn that avoids someone does not start a social interaction with them (this is also where the game's
    /// random chitchat ends up: it picks a target, then calls TryInteractWith). Cheap: one dictionary lookup.</summary>
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_AvoidSocial
    {
        static AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> _pawn;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, ref bool __result)
        {
            try
            {
                if (WantsHooks.Bypass || recipient == null) return true;
                var comp = HermesVoiceGameComponent.Instance;
                if (comp == null || comp.Avoids.Count == 0) return true;
                if (_pawn == null) _pawn = AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");
                var p = _pawn(__instance);
                if (p == null || !comp.Avoids.IsAvoiding(p.thingIDNumber, recipient.thingIDNumber, Find.TickManager.TicksGame)) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Log.Warning("[HermesVoice] avoid prefix: " + e.Message); return true; }
        }
    }
}

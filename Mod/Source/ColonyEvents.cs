using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HermesVoice
{
    /// <summary>Shared payload helpers for colony events. Game thread only.</summary>
    public static class EventPayload
    {
        public static object PawnObj(Pawn p)
        {
            return new { id = p.thingIDNumber, name = p.LabelShort };
        }

        public static object MapObj(Map m)
        {
            return m == null ? null : (object)new { index = m.Index, home = m.IsPlayerHome };
        }

        public static string Label(Thing t)
        {
            return t == null ? null : (t is Pawn p ? p.LabelShort : t.LabelShort);
        }

        public static float Round(float f) { return (float)Math.Round(f, 2); }

        /// <summary>Run a patch body so that a bad event can never break the game.</summary>
        public static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warning("[HermesVoice] event '" + what + "' failed: " + e.Message); }
        }
    }

    /// <summary>Flushes merged injuries once their cooldown window has passed, and resets state on load.</summary>
    public class ColonyEventsComponent : GameComponent
    {
        public ColonyEventsComponent(Game game) : base()
        {
            EventGate.Reset();
            InjuryEvents.Clear();
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 60 == 0)
                EventPayload.Safe("injury-flush", InjuryEvents.Flush);
        }
    }

    // ---------------------------------------------------------------- injuries
    public static class InjuryEvents
    {
        class Entry { public string Label; public string Part; public float Severity; public string Damage; public string Attacker; }
        class Pending { public Pawn Pawn; public List<Entry> Entries = new List<Entry>(); }

        static readonly Dictionary<int, Pending> Merged = new Dictionary<int, Pending>();

        public static void Clear() { Merged.Clear(); }

        public static void OnInjury(Pawn pawn, Hediff_Injury inj, DamageInfo? dinfo)
        {
            if (!EventGate.Wanted(EventKind.Injury, pawn)) return;
            var e = new Entry
            {
                Label = inj.def.label,
                Part = inj.Part?.Label,
                Severity = EventPayload.Round(inj.Severity),
                Damage = dinfo?.Def?.label,
                Attacker = EventPayload.Label(dinfo?.Instigator)
            };
            int id = pawn.thingIDNumber;
            if (EventGate.CooldownOk(EventKind.Injury, id, EventGate.CooldownFor(EventKind.Injury)))
            {
                Send(pawn, new List<Entry> { e });
                return;
            }
            Pending p;
            if (!Merged.TryGetValue(id, out p)) Merged[id] = p = new Pending { Pawn = pawn };
            if (p.Entries.Count < 30) p.Entries.Add(e);
        }

        public static void Flush()
        {
            if (Merged.Count == 0) return;
            foreach (int id in Merged.Keys.ToList())
            {
                var p = Merged[id];
                if (!EventGate.CooldownOk(EventKind.Injury, id, EventGate.CooldownFor(EventKind.Injury))) continue;
                Merged.Remove(id);
                if (EventGate.Wanted(EventKind.Injury, p.Pawn)) Send(p.Pawn, p.Entries);
            }
        }

        static void Send(Pawn pawn, List<Entry> entries)
        {
            if (!EventGate.GlobalCapOk()) return;
            var parts = entries.Select(x => x.Label + (x.Part != null ? " on " + x.Part : "")).ToList();
            var attackers = entries.Select(x => x.Attacker).Where(a => a != null).Distinct().ToList();
            string summary = pawn.LabelShort + " was hurt: " + string.Join(", ", parts.Take(5))
                + (parts.Count > 5 ? " and " + (parts.Count - 5) + " more" : "")
                + (attackers.Count > 0 ? " (by " + string.Join(", ", attackers) + ")" : "") + ".";
            AgentDialOut.Send("injury", new
            {
                pawn = EventPayload.PawnObj(pawn),
                map = EventPayload.MapObj(pawn.MapHeld),
                count = entries.Count,
                total_severity = EventPayload.Round(entries.Sum(x => x.Severity)),
                injuries = entries.Select(x => new { part = x.Part, hediff = x.Label, severity = x.Severity, damage_def = x.Damage, attacker = x.Attacker }).ToList(),
                attackers,
                summary
            });
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
        new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    public static class Patch_Injury
    {
        [HarmonyPostfix]
        public static void Postfix(Hediff hediff, DamageInfo? dinfo)
        {
            EventPayload.Safe("injury", () =>
            {
                var inj = hediff as Hediff_Injury;
                if (inj == null || !dinfo.HasValue || inj.pawn == null) return;
                InjuryEvents.OnInjury(inj.pawn, inj, dinfo);
            });
        }
    }

    // ------------------------------------------------------------------ death
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Death
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance, out bool __state) { __state = __instance.Dead; }

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, bool __state, DamageInfo? dinfo, Hediff exactCulprit)
        {
            EventPayload.Safe("death", () =>
            {
                if (__state || !__instance.Dead) return;
                if (!EventGate.Wanted(EventKind.Death, __instance, true)) return;
                string cause = dinfo?.Def?.label ?? exactCulprit?.def?.label;
                string killer = EventPayload.Label(dinfo?.Instigator);
                string summary = __instance.LabelShort + " died"
                    + (cause != null ? " from " + cause : "")
                    + (killer != null ? ", killed by " + killer : "") + ".";
                EventGate.Emit(EventKind.Death, "death", __instance, new
                {
                    pawn = EventPayload.PawnObj(__instance),
                    map = EventPayload.MapObj(__instance.MapHeld),
                    animal = !__instance.RaceProps.Humanlike,
                    cause,
                    culprit = exactCulprit?.LabelCap,
                    killer,
                    summary
                });
            });
        }
    }

    // ----------------------------------------------------------- mental break
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class Patch_MentalBreak
    {
        static AccessTools.FieldRef<MentalStateHandler, Pawn> _pawn;

        static Pawn PawnOf(MentalStateHandler h)
        {
            if (_pawn == null) _pawn = AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");
            return _pawn(h);
        }

        [HarmonyPostfix]
        public static void Postfix(MentalStateHandler __instance, bool __result, MentalStateDef stateDef, string reason)
        {
            EventPayload.Safe("mental break", () =>
            {
                if (!__result) return;
                var pawn = PawnOf(__instance);
                if (!EventGate.Wanted(EventKind.MentalBreak, pawn)) return;
                string label = stateDef.label;
                EventGate.Emit(EventKind.MentalBreak, "mental_break", pawn, new
                {
                    pawn = EventPayload.PawnObj(pawn),
                    map = EventPayload.MapObj(pawn.MapHeld),
                    state = stateDef.defName,
                    state_label = label,
                    reason,
                    summary = pawn.LabelShort + " is having a mental break (" + label + ")" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason)
                });
            });
        }
    }

    // ---------------------------------------------------------------- level-up
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn))]
    public static class Patch_LevelUp
    {
        [HarmonyPrefix]
        public static void Prefix(SkillRecord __instance, out int __state) { __state = __instance.Level; }

        [HarmonyPostfix]
        public static void Postfix(SkillRecord __instance, int __state)
        {
            // Learn runs every tick for every working pawn: bail before allocating the Safe closure.
            if (__instance.Level <= __state) return;
            EventPayload.Safe("level up", () =>
            {
                var pawn = __instance.Pawn;
                if (!EventGate.Wanted(EventKind.LevelUp, pawn)) return;
                string skill = __instance.def.label;
                EventGate.Emit(EventKind.LevelUp, "skill_level_up", pawn, new
                {
                    pawn = EventPayload.PawnObj(pawn),
                    map = EventPayload.MapObj(pawn.MapHeld),
                    skill,
                    old_level = __state,
                    new_level = __instance.Level,
                    passion = __instance.passion.ToString(),
                    summary = pawn.LabelShort + " reached " + skill + " level " + __instance.Level + "."
                });
            });
        }
    }

    // ----------------------------------------------------------------- tending
    public static class TendContext
    {
        public static float Quality = -1f;
    }

    [HarmonyPatch(typeof(HediffComp_TendDuration), nameof(HediffComp_TendDuration.CompTended))]
    public static class Patch_TendQuality
    {
        [HarmonyPostfix]
        public static void Postfix(float quality) { TendContext.Quality = Math.Max(TendContext.Quality, quality); }
    }

    [HarmonyPatch(typeof(TendUtility), nameof(TendUtility.DoTend))]
    public static class Patch_Tend
    {
        [HarmonyPrefix]
        public static void Prefix() { TendContext.Quality = -1f; }

        [HarmonyPostfix]
        public static void Postfix(Pawn doctor, Pawn patient, Medicine medicine)
        {
            EventPayload.Safe("tend", () =>
            {
                float q = TendContext.Quality;
                TendContext.Quality = -1f;
                if (doctor == null || patient == null || doctor == patient) return; // self-tend is noise
                if (!EventGate.Wanted(EventKind.Tend, patient)) return;
                string med = medicine?.def?.label;
                string qText = q >= 0f ? Math.Round(q * 100) + "% quality" : "unknown quality";
                EventGate.Emit(EventKind.Tend, "tend", patient, new
                {
                    pawn = EventPayload.PawnObj(patient),
                    doctor = EventPayload.PawnObj(doctor),
                    map = EventPayload.MapObj(patient.MapHeld),
                    quality = q >= 0f ? (float?)EventPayload.Round(q) : null,
                    medicine = med,
                    summary = doctor.LabelShort + " tended " + patient.LabelShort + " (" + qText + (med != null ? ", " + med : "") + ")."
                });
            });
        }
    }

    // ------------------------------------------------------------------ social
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_Social
    {
        static AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> _pawn;

        [HarmonyPostfix]
        public static void Postfix(Pawn_InteractionsTracker __instance, bool __result, Pawn recipient, InteractionDef intDef)
        {
            EventPayload.Safe("social", () =>
            {
                if (!__result || recipient == null || intDef == null) return;
                if (_pawn == null) _pawn = AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");
                var initiator = _pawn(__instance);
                if (!EventGate.Wanted(EventKind.Social, initiator)) return;
                EventGate.Emit(EventKind.Social, "social", initiator, new
                {
                    pawn = EventPayload.PawnObj(initiator),
                    recipient = EventPayload.PawnObj(recipient),
                    map = EventPayload.MapObj(initiator.MapHeld),
                    interaction = intDef.defName,
                    interaction_label = intDef.label,
                    summary = initiator.LabelShort + " -> " + recipient.LabelShort + ": " + intDef.label + "."
                });
            });
        }
    }

    // -------------------------------------------------------------------- raids
    public static class RaidContext
    {
        public static int RaiderCount = -1;
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_RaidInfo
    {
        [HarmonyPostfix]
        public static void Postfix(bool __result, List<Pawn> pawns)
        {
            if (__result && pawns != null) RaidContext.RaiderCount = pawns.Count;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
    public static class Patch_Raid
    {
        [HarmonyPrefix]
        public static void Prefix() { RaidContext.RaiderCount = -1; }

        [HarmonyPostfix]
        public static void Postfix(bool __result, IncidentParms parms)
        {
            EventPayload.Safe("raid", () =>
            {
                int count = RaidContext.RaiderCount;
                RaidContext.RaiderCount = -1;
                if (!__result || parms == null) return;
                if (!EventGate.GameLive() || !EventGate.KindEnabled(EventKind.Raid)) return;
                var map = parms.target as Map;
                if (map == null || !map.IsPlayerHome) return;
                if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer)) return;
                string fac = parms.faction.Name;
                string strat = parms.raidStrategy?.label;
                string arrive = parms.raidArrivalMode?.label;
                if (!EventGate.GlobalCapOk()) return;
                AgentDialOut.Send("raid", new
                {
                    map = EventPayload.MapObj(map),
                    faction = fac,
                    strategy = strat,
                    arrival = arrive,
                    points = EventPayload.Round(parms.points),
                    raider_count = count >= 0 ? (int?)count : null,
                    summary = "Raid by " + fac + (count >= 0 ? " (" + count + " raiders)" : "")
                        + (strat != null ? ", strategy: " + strat : "") + (arrive != null ? ", arriving: " + arrive : "") + "."
                });
            });
        }
    }
}

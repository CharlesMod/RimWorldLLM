using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    public enum EventKind { Injury, Death, MentalBreak, LevelUp, Tend, Social, Raid }

    /// <summary>
    /// Throttling and filtering for colony events. Game thread only (not thread-safe by design).
    /// </summary>
    public static class EventGate
    {
        public const int MaxPerRealMinute = 20;

        // Cooldowns in game ticks (60 ticks = 1 s at 1x speed).
        const int InjuryCooldown = 30 * 60;
        const int SocialCooldown = 60 * 60;
        const int TendCooldown = 20 * 60;
        const int MentalCooldown = 10 * 60;

        static readonly Dictionary<(EventKind, int), int> LastTick = new Dictionary<(EventKind, int), int>();
        static readonly Queue<float> RealTimes = new Queue<float>();
        static int _lastPrune;

        public static bool KindEnabled(EventKind kind)
        {
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.enabled || !s.sendColonyEvents) return false;
            switch (kind)
            {
                case EventKind.Injury: return s.evInjury;
                case EventKind.Death: return s.evDeath;
                case EventKind.MentalBreak: return s.evMentalBreak;
                case EventKind.LevelUp: return s.evLevelUp;
                case EventKind.Tend: return s.evTend;
                case EventKind.Social: return s.evSocial;
                case EventKind.Raid: return s.evRaid;
            }
            return false;
        }

        /// <summary>True when the game is in a state where events should be watched (not loading / generating).</summary>
        public static bool GameLive()
        {
            return Current.ProgramState == ProgramState.Playing && Current.Game != null && Find.TickManager != null;
        }

        /// <summary>Colonist (or, when allowAnimals, colony animal) on a player-home map.</summary>
        public static bool IsColonyPawn(Pawn pawn, bool allowAnimals)
        {
            if (pawn == null || pawn.Faction == null || !pawn.Faction.IsPlayer) return false;
            if (pawn.RaceProps == null) return false;
            if (pawn.RaceProps.Humanlike)
            {
                if (pawn.IsQuestLodger()) return false;
            }
            else if (!(allowAnimals && pawn.RaceProps.Animal)) return false;
            var map = pawn.MapHeld;
            return map != null && map.IsPlayerHome;
        }

        /// <summary>Cheap pre-check before building a payload.</summary>
        public static bool Wanted(EventKind kind, Pawn pawn, bool allowAnimals = false)
        {
            if (!GameLive() || !KindEnabled(kind)) return false;
            if (string.IsNullOrWhiteSpace(HermesVoiceMod.Settings.agentUrl)) return false;
            return IsColonyPawn(pawn, allowAnimals);
        }

        /// <summary>
        /// Cooldown per (kind, pawn): returns false (and does not consume) while still cooling down.
        /// cooldownTicks 0 means no cooldown.
        /// </summary>
        public static bool CooldownOk(EventKind kind, int pawnId, int cooldownTicks)
        {
            if (cooldownTicks <= 0) return true;
            int now = Find.TickManager.TicksGame;
            var key = (kind, pawnId);
            int last;
            if (LastTick.TryGetValue(key, out last) && now - last >= 0 && now - last < cooldownTicks) return false;
            LastTick[key] = now;
            if (now - _lastPrune > 60000 || now < _lastPrune) Prune(now);
            return true;
        }

        public static int CooldownFor(EventKind kind)
        {
            switch (kind)
            {
                case EventKind.Injury: return InjuryCooldown;
                case EventKind.Social: return SocialCooldown;
                case EventKind.Tend: return TendCooldown;
                case EventKind.MentalBreak: return MentalCooldown;
                default: return 0;
            }
        }

        /// <summary>Global cap: at most MaxPerRealMinute events per real minute; extras are dropped.</summary>
        public static bool GlobalCapOk()
        {
            float now = Time.realtimeSinceStartup;
            while (RealTimes.Count > 0 && now - RealTimes.Peek() > 60f) RealTimes.Dequeue();
            if (RealTimes.Count >= MaxPerRealMinute) return false;
            RealTimes.Enqueue(now);
            return true;
        }

        /// <summary>Apply cooldown + global cap, then send. Returns true if sent.</summary>
        public static bool Emit(EventKind kind, string eventType, Pawn pawn, object payload)
        {
            if (!CooldownOk(kind, pawn?.thingIDNumber ?? 0, CooldownFor(kind))) return false;
            if (!GlobalCapOk()) return false;
            AgentDialOut.Send(eventType, payload);
            return true;
        }

        public static void Reset()
        {
            LastTick.Clear();
            RealTimes.Clear();
            _lastPrune = 0;
        }

        static void Prune(int now)
        {
            _lastPrune = now;
            var dead = new List<(EventKind, int)>();
            foreach (var kv in LastTick)
                if (now - kv.Value > 120 * 60 || now < kv.Value) dead.Add(kv.Key);
            foreach (var k in dead) LastTick.Remove(k);
        }
    }
}

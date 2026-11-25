using System;

namespace HermesVoice
{
    /// <summary>Game-number rules that need no game: witness test, severities, effect caps.</summary>
    public static class Rules
    {
        public const float HearingBaseRange = 10f;

        /// <summary>A pawn hears or sees an event when it can hear at all, is within range x Hearing, and shares the room or has line of sight.</summary>
        public static bool Witnessed(float distance, float hearingLevel, bool sameRoom, bool lineOfSight, float baseRange = HearingBaseRange)
        {
            if (hearingLevel <= 0f) return false;
            if (distance > baseRange * hearingLevel) return false;
            return sameRoom || lineOfSight;
        }

        static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        /// <summary>Total injury severity in hit points, scaled by the pawn's health scale: 15 hp on a normal pawn is 1.0.</summary>
        public static float InjurySeverity(float totalSeverity, float healthScale) { return Clamp01(totalSeverity / (15f * Math.Max(0.1f, healthScale))); }
        public static float MentalBreakSeverity(bool aggro, bool extreme) { return extreme ? 1f : (aggro ? 0.85f : 0.65f); }
        public static float LevelUpSeverity(int newLevel) { return Clamp01(0.1f + newLevel / 40f); }
        public static float RaidSeverity(float points) { return Clamp01(points / 2000f); }
        public static float ThoughtSeverity(float moodOffset) { return Clamp01(Math.Abs(moodOffset) / 20f); }
    }

    /// <summary>Per pair daily cap on applied effects (SPEC section 8). Day is the game day number.</summary>
    public sealed class PairCap
    {
        readonly int _cap;
        int _day = -1;
        readonly System.Collections.Generic.Dictionary<string, int> _count = new System.Collections.Generic.Dictionary<string, int>();

        public PairCap(int capPerDay) { _cap = capPerDay; }

        public bool TryConsume(int speaker, int listener, int day)
        {
            if (day != _day) { _day = day; _count.Clear(); }
            string key = speaker + ">" + listener;
            int n;
            _count.TryGetValue(key, out n);
            if (n >= _cap) return false;
            _count[key] = n + 1;
            return true;
        }

        public void Clear() { _count.Clear(); _day = -1; }
    }
}

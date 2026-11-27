using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>What happened, as the director camera scores it.</summary>
    public enum FocusSignal
    {
        SpeechPlain, SpeechMedium, SpeechHigh,   // balloon shown: plain / confide, whisper / insult, shout
        Thought,                                  // a thought cloud shown
        Injury, Death, MentalBreak, RaidStart     // colony events (Death, MentalBreak, RaidStart are urgent)
    }

    /// <summary>
    /// Verse-free brain of the director camera: pawns (by id) collect interest scores from signals, scores decay over
    /// real time, and <see cref="Pick"/> names the pawn to follow, or -1 for "stay put". Hysteresis: a minimum dwell
    /// on the current focus and a margin a rival must beat; urgent signals cut in at once. The clock is an argument
    /// (real seconds) so the logic is deterministic. Not thread-safe (game thread only).
    /// </summary>
    public sealed class DirectorFocus
    {
        public const double HalfLifeSec = 8.0;
        public const double MinDwellSec = 6.0;
        public const double Margin = 1.5;          // a rival must beat the focus score by this factor
        public const double MinScore = 0.5;        // below this a pawn is not interesting at all
        public const double MaxScore = 40.0;
        public const double ConversationBonus = 4.0;
        public const double UrgentWindowSec = 3.0; // an urgent signal older than this no longer cuts in

        sealed class Cand { public double Score; public double Stamp; public double ConvUntil = -1; public double UrgentAt = -1; }

        readonly Dictionary<int, Cand> _c = new Dictionary<int, Cand>();
        int _focus = -1;
        double _since;

        public int Current { get { return _focus; } }
        public int Count { get { return _c.Count; } }

        public static double Weight(FocusSignal s)
        {
            switch (s)
            {
                case FocusSignal.Thought: return 1.0;
                case FocusSignal.SpeechPlain: return 3.0;
                case FocusSignal.SpeechMedium: return 5.0;
                case FocusSignal.SpeechHigh: return 8.0;
                case FocusSignal.Injury: return 6.0;
                case FocusSignal.MentalBreak: return 15.0;
                case FocusSignal.Death: return 20.0;
                case FocusSignal.RaidStart: return 20.0;
                default: return 0.0;
            }
        }

        public static bool IsUrgent(FocusSignal s)
        {
            return s == FocusSignal.Death || s == FocusSignal.MentalBreak || s == FocusSignal.RaidStart;
        }

        static double Decayed(Cand c, double now)
        {
            double dt = now - c.Stamp;
            return dt <= 0 ? c.Score : c.Score * Math.Pow(0.5, dt / HalfLifeSec);
        }

        Cand Get(int id, double now)
        {
            Cand c;
            if (!_c.TryGetValue(id, out c)) { c = new Cand { Stamp = now }; _c[id] = c; }
            return c;
        }

        /// <summary>Add a signal for a pawn. <paramref name="severity"/> scales events (1 = normal); ignored for speech.</summary>
        public void Signal(int id, FocusSignal sig, double now, double severity = 1.0)
        {
            double w = Weight(sig);
            if (w <= 0) return;
            if (severity > 0 && sig != FocusSignal.SpeechPlain && sig != FocusSignal.SpeechMedium && sig != FocusSignal.SpeechHigh && sig != FocusSignal.Thought)
                w *= Math.Min(2.0, Math.Max(0.5, severity));
            var c = Get(id, now);
            c.Score = Math.Min(MaxScore, Decayed(c, now) + w);
            c.Stamp = now;
            if (IsUrgent(sig)) c.UrgentAt = now;
        }

        /// <summary>Two pawns are in a conversation until <c>now + durationSec</c>: both get a bonus while it lasts.</summary>
        public void Conversation(int a, int b, double now, double durationSec)
        {
            double until = now + Math.Max(0.0, durationSec);
            var ca = Get(a, now); if (ca.ConvUntil < until) ca.ConvUntil = until;
            var cb = Get(b, now); if (cb.ConvUntil < until) cb.ConvUntil = until;
        }

        /// <summary>A pawn died or left the map: forget it; if it was the focus, the next Pick may switch at once.</summary>
        public void Remove(int id)
        {
            _c.Remove(id);
            if (_focus == id) { _focus = -1; _since = 0; }
        }

        /// <summary>Forget everything (map change, game end).</summary>
        public void Clear() { _c.Clear(); _focus = -1; _since = 0; }

        public void CopyIds(List<int> into)
        {
            into.Clear();
            foreach (var k in _c.Keys) into.Add(k);
        }

        /// <summary>Current interest in a pawn (decayed, with the conversation bonus).</summary>
        public double ScoreOf(int id, double now)
        {
            Cand c;
            if (!_c.TryGetValue(id, out c)) return 0.0;
            return Decayed(c, now) + (c.ConvUntil > now ? ConversationBonus : 0.0);
        }

        /// <summary>The pawn id to follow now, or -1 when nothing is interesting (the camera stays where it is).</summary>
        public int Pick(double now)
        {
            int best = -1; double bestScore = 0.0;
            int urgent = -1; double urgentAt = -1;
            List<int> dead = null;
            foreach (var kv in _c)
            {
                var c = kv.Value;
                double s = Decayed(c, now) + (c.ConvUntil > now ? ConversationBonus : 0.0);
                if (s < MinScore && c.ConvUntil <= now && kv.Key != _focus) { (dead ?? (dead = new List<int>())).Add(kv.Key); continue; }
                if (s > bestScore || (s == bestScore && best >= 0 && kv.Key < best)) { best = kv.Key; bestScore = s; }
                if (c.UrgentAt >= 0 && now - c.UrgentAt <= UrgentWindowSec && c.UrgentAt > urgentAt) { urgent = kv.Key; urgentAt = c.UrgentAt; }
            }
            if (dead != null) foreach (var id in dead) _c.Remove(id);

            if (urgent >= 0 && urgent != _focus) { _focus = urgent; _since = now; _c[urgent].UrgentAt = -1; return _focus; }

            double cur = _focus >= 0 ? ScoreOf(_focus, now) : 0.0;
            if (_focus >= 0 && cur < MinScore)
            {
                // Interest in the focus is gone. After the dwell it lets go (or hands over to any rival).
                if (now - _since < MinDwellSec) return _focus;
                _c.Remove(_focus);
                _focus = -1;
            }
            if (_focus < 0)
            {
                if (best >= 0 && bestScore >= MinScore) { _focus = best; _since = now; }
                return _focus;
            }
            if (best != _focus && now - _since >= MinDwellSec && bestScore > cur * Margin) { _focus = best; _since = now; }
            return _focus;
        }
    }
}

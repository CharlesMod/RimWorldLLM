using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HermesVoice.Bridge
{
    public enum RecapVerdict { Accept, Disabled, NotLoaded, StaleEpoch, Duplicate, Expired }

    /// <summary>
    /// Decides whether POST /recap may show the "Previously on..." letter. No game references.
    /// The game arms it on every load (<c>loadedGame</c> true) and every new game (false). A recap is accepted only
    /// when: the setting is on, the epoch is the current one and began with a loaded save (a new colony has no
    /// "previously"), none was shown for this epoch yet, and the load is at most <see cref="WindowMs"/> of real time ago.
    /// </summary>
    public sealed class RecapGate
    {
        public const long WindowMs = 180000;
        public const int MaxLines = 3;
        public const int MaxChars = 160;

        int _epoch = -1;
        bool _loaded, _shown;
        long _armedAt;

        public void Arm(int epoch, bool loadedGame, long nowMs)
        {
            _epoch = epoch;
            _loaded = loadedGame;
            _shown = false;
            _armedAt = nowMs;
        }

        /// <summary>The game ended: nothing is accepted until the next Arm.</summary>
        public void Disarm() { _epoch = -1; _loaded = false; }

        /// <summary>Accept consumes the epoch's one recap; every other verdict leaves the state alone.</summary>
        public RecapVerdict Check(int epoch, long nowMs, bool enabled)
        {
            if (!enabled) return RecapVerdict.Disabled;
            if (epoch != _epoch) return RecapVerdict.StaleEpoch;
            if (!_loaded) return RecapVerdict.NotLoaded;
            if (_shown) return RecapVerdict.Duplicate;
            if (nowMs - _armedAt > WindowMs) return RecapVerdict.Expired;
            _shown = true;
            return RecapVerdict.Accept;
        }

        /// <summary>The ApiResult error code for a verdict other than Accept.</summary>
        public static string ErrorCode(RecapVerdict v)
        {
            switch (v)
            {
                case RecapVerdict.StaleEpoch:
                case RecapVerdict.Duplicate: return "conflict";
                case RecapVerdict.Expired: return "expired";
                default: return "refused";
            }
        }

        /// <summary>Whitespace collapsed, empty lines dropped, each cut at <see cref="MaxChars"/>, at most <see cref="MaxLines"/> kept.</summary>
        public static List<string> CleanLines(IEnumerable<string> raw)
        {
            var outp = new List<string>();
            if (raw == null) return outp;
            foreach (var r in raw)
            {
                string t = Regex.Replace(r ?? "", @"\s+", " ").Trim();
                if (t.Length == 0) continue;
                if (t.Length > MaxChars) t = t.Substring(0, MaxChars - 1).TrimEnd() + "…";
                outp.Add(t);
                if (outp.Count == MaxLines) break;
            }
            return outp;
        }
    }
}

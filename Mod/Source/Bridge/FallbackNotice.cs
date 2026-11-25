using System;

namespace HermesVoice.Bridge
{
    /// <summary>
    /// Decides when to tell the player once that the small fallback model is in use. No game references.
    /// The game arms it when a save is loaded; it then waits (the engine may still be starting) for a status that says
    /// a fallback model is in use, answers true exactly once per game session, and gives up after <see cref="TimeoutMs"/>.
    /// A player who switched the warning off never sees it.
    /// </summary>
    public sealed class FallbackNotice
    {
        public const long TimeoutMs = 120000;

        bool _armed, _warned;
        long _armedAt;

        /// <summary>True once the notice has been given this session (a later load does not repeat it).</summary>
        public bool Warned { get { return _warned; } }
        public bool Armed { get { return _armed; } }

        /// <summary>A save was loaded or a new game started: start waiting for the engine's answer.</summary>
        public void Arm(long nowMs)
        {
            if (_warned) return;
            _armed = true;
            _armedAt = nowMs;
        }

        /// <summary>Stop waiting (the game ended).</summary>
        public void Disarm() { _armed = false; }

        /// <summary>True while the caller should keep the engine status fresh and call <see cref="Check"/>.</summary>
        public bool Waiting(long nowMs, bool suppressed)
        {
            if (!_armed) return false;
            if (_warned || suppressed || nowMs - _armedAt > TimeoutMs) { _armed = false; return false; }
            return true;
        }

        /// <summary>True exactly once: armed, not suppressed, and the engine reports a fallback model in use.</summary>
        public bool Check(long nowMs, bool statusKnown, bool fallbackInUse, bool suppressed)
        {
            if (!Waiting(nowMs, suppressed)) return false;
            if (!statusKnown || !fallbackInUse) return false;
            _warned = true;
            _armed = false;
            return true;
        }
    }
}

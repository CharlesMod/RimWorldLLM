namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Follows the engine's fast first run (no game references): a small starter model answers while the chosen model
    /// downloads, then the engine swaps. Tells the caller once when the starter is first seen and once when the full
    /// model has taken over, and whether to keep polling the engine status meanwhile.
    /// </summary>
    public sealed class StarterWatch
    {
        public enum Event { None, Starter, Ready }

        bool _seen, _done;

        /// <summary>A starter was seen and the full model has not taken over yet.</summary>
        public bool KeepPolling { get { return _seen && !_done; } }

        public static bool IsStarter(string tier) { return tier == "starter"; }

        public Event Update(bool statusKnown, string tier)
        {
            if (!statusKnown || _done) return Event.None;
            if (IsStarter(tier))
            {
                if (_seen) return Event.None;
                _seen = true;
                return Event.Starter;
            }
            if (_seen && tier == "full") { _done = true; return Event.Ready; }
            return Event.None;
        }

        /// <summary>Whole percent of the chosen model's download, or -1 when unknown.</summary>
        public static int Percent(long done, long total)
        {
            if (total <= 0) return -1;
            long p = done * 100 / total;
            return (int)(p < 0 ? 0 : p > 100 ? 100 : p);
        }
    }
}

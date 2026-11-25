using System.Collections.Generic;

namespace HermesVoice
{
    /// <summary>
    /// Pacing for the map's thought clouds (Verse-free, tested). Rules: at most one thought cloud per pawn at a time;
    /// a per-pawn and a global minimum gap; and a cloud never goes over a pawn whose speech balloon is visible: it
    /// waits (one pending thought per pawn) and is dropped when it would be stale.
    /// </summary>
    public sealed class ThoughtPacing
    {
        public enum Verdict { Show, Wait, Drop }

        public const int MaxSurfaceChars = 90;
        public const int StaleTicks = 600;          // a waiting thought older than this is dropped (10 s at 1x)

        public const int StaleRealSec = 10;         // ... or this many real seconds at the current speed, whichever is longer

        /// <summary>A gap given in real seconds, as game ticks at the current speed: never shorter than the 1x count, so a
        /// gap of 20 s stays about 20 s of watching at 2x, 3x and 4x instead of shrinking to 10, 3 and 1.3 s.</summary>
        public static int GapTicks(int seconds, float ticksPerRealSecond)
        {
            if (seconds <= 0) return 0;
            return (int)System.Math.Min(int.MaxValue / 2, (long)seconds * (long)System.Math.Max(60f, ticksPerRealSecond));
        }

        /// <summary>The stale limit for a waiting thought at the current speed.</summary>
        public static int StaleFor(float ticksPerRealSecond)
        { return (int)System.Math.Max(StaleTicks, StaleRealSec * System.Math.Max(60f, ticksPerRealSecond)); }

        readonly Dictionary<int, int> _lastShown = new Dictionary<int, int>();
        readonly Dictionary<int, int> _pendingAt = new Dictionary<int, int>();
        readonly Dictionary<int, string> _pendingText = new Dictionary<int, string>();
        int _lastGlobal = int.MinValue / 2;

        public int PendingCount { get { return _pendingText.Count; } }

        /// <summary>The short line for the map: the surface field, cut at a word; else the text if already short; else null.</summary>
        public static string Surface(string surface, string text)
        {
            string s = (surface ?? "").Trim();
            if (s.Length == 0)
            {
                s = (text ?? "").Trim();
                return s.Length > 0 && s.Length <= MaxSurfaceChars ? s : null;
            }
            if (s.Length <= MaxSurfaceChars) return s;
            int cut = s.LastIndexOf(' ', MaxSurfaceChars - 1);
            if (cut < MaxSurfaceChars / 2) cut = MaxSurfaceChars - 1;
            return s.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.') + "…";
        }

        /// <param name="thoughtActive">this pawn already has a thought cloud on screen</param>
        /// <param name="speechVisible">this pawn has a speech balloon on screen</param>
        public Verdict Offer(int pawnId, string text, int now, bool thoughtActive, bool speechVisible,
                             int perPawnGapTicks, int globalGapTicks)
        {
            if (string.IsNullOrEmpty(text) || thoughtActive || _pendingText.ContainsKey(pawnId)) return Verdict.Drop;
            int last;
            if (_lastShown.TryGetValue(pawnId, out last) && now - last < perPawnGapTicks) return Verdict.Drop;
            if (now - _lastGlobal < globalGapTicks) return Verdict.Drop;
            if (speechVisible)
            {
                _pendingAt[pawnId] = now; _pendingText[pawnId] = text;
                return Verdict.Wait;
            }
            MarkShown(pawnId, now);
            return Verdict.Show;
        }

        /// <summary>Ready thoughts: pawns whose speech is gone are released (text out), stale ones are dropped.</summary>
        public void Promote(int now, System.Func<int, bool> speechVisible, int globalGapTicks, List<KeyValuePair<int, string>> ready, int staleTicks = StaleTicks)
        {
            if (_pendingText.Count == 0) return;
            var ids = new List<int>(_pendingText.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                if (now - _pendingAt[id] > staleTicks) { Remove(id); continue; }
                if (speechVisible(id) || now - _lastGlobal < globalGapTicks) continue;
                ready.Add(new KeyValuePair<int, string>(id, _pendingText[id]));
                MarkShown(id, now);
                Remove(id);
            }
        }

        void MarkShown(int pawnId, int now) { _lastShown[pawnId] = now; _lastGlobal = now; }
        void Remove(int id) { _pendingAt.Remove(id); _pendingText.Remove(id); }
        public void Clear() { _lastShown.Clear(); _pendingAt.Clear(); _pendingText.Clear(); _lastGlobal = int.MinValue / 2; }
    }
}

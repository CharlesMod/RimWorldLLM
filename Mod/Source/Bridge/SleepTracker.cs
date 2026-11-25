using System.Collections.Generic;

namespace HermesVoice.Bridge
{
    /// <summary>
    /// Turns a stream of "is this pawn asleep in a bed right now" samples into one "fell asleep" signal per pawn,
    /// at most once per cooldown (about one night). Verse-free; game thread.
    /// </summary>
    public sealed class SleepTracker
    {
        public const int DefaultCooldownTicks = 8 * 2500;   // 8 game hours

        readonly Dictionary<int, bool> _asleep = new Dictionary<int, bool>();
        readonly Dictionary<int, int> _last = new Dictionary<int, int>();
        readonly int _cooldown;

        public SleepTracker(int cooldownTicks = DefaultCooldownTicks) { _cooldown = cooldownTicks; }

        /// <summary>
        /// Record a sample. True only on the awake-to-asleep edge when the pawn has not signalled within the cooldown.
        /// A pawn first seen already asleep (a loaded save) never signals: its night was already under way.
        /// </summary>
        public bool Observe(int pawnId, bool asleepInBed, int tick)
        {
            bool known, was;
            known = _asleep.TryGetValue(pawnId, out was);
            _asleep[pawnId] = asleepInBed;
            if (!asleepInBed || !known || was) return false;
            int last;
            if (_last.TryGetValue(pawnId, out last) && tick - last >= 0 && tick - last < _cooldown) return false;
            _last[pawnId] = tick;
            return true;
        }

        /// <summary>Forget pawns not in <paramref name="present"/> (left the map, died).</summary>
        public void Retain(ICollection<int> present)
        {
            var gone = new List<int>();
            foreach (var id in _asleep.Keys) if (!present.Contains(id)) gone.Add(id);
            foreach (var id in gone) { _asleep.Remove(id); _last.Remove(id); }
        }

        public void Clear() { _asleep.Clear(); _last.Clear(); }
    }
}

using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One eating-or-not sample of one awake colonist: which room they are in.</summary>
    public struct MealSample
    {
        public int PawnId;
        public int RoomId;
        public bool Eating;
        public MealSample(int pawnId, int roomId, bool eating) { PawnId = pawnId; RoomId = roomId; Eating = eating; }
    }

    /// <summary>A meal gathering in one room: the room and the pawns eating there.</summary>
    public sealed class MealGroup
    {
        public int RoomId;
        public List<int> Pawns = new List<int>();
        public string Key { get { return MealDetector.KeyFor(RoomId); } }
    }

    /// <summary>
    /// Turns "who is eating in which room" samples into meal gathering starts and ends. Verse-free; game thread.
    /// A meal starts when 3+ pawns eat in one room (and the room is off cooldown), and ends when fewer than 2 are still
    /// eating there or after <see cref="MaxMealTicks"/>. A meal first seen already under way after a load still counts as new.
    /// </summary>
    public sealed class MealDetector
    {
        public const int MinStart = 3;
        public const int MinKeep = 2;
        public const int DefaultCooldownTicks = 20000;   // 8 game hours is 20000 ticks
        public const int MaxMealTicks = 2500;

        sealed class Open { public int StartTick; public List<int> Pawns = new List<int>(); }

        readonly Dictionary<int, Open> _open = new Dictionary<int, Open>();
        readonly Dictionary<int, int> _lastStart = new Dictionary<int, int>();
        readonly int _cooldown;

        public MealDetector(int cooldownTicks = DefaultCooldownTicks) { _cooldown = cooldownTicks; }

        public static string KeyFor(int roomId) { return "meal:room" + roomId; }

        public IEnumerable<int> OpenRooms { get { return _open.Keys; } }

        /// <summary>Process one poll. Starts are new meals to announce; Ends are meals that just closed (with their last pawns).</summary>
        public void Observe(IEnumerable<MealSample> samples, int now, out List<MealGroup> starts, out List<MealGroup> ends)
        {
            starts = new List<MealGroup>();
            ends = new List<MealGroup>();
            var eating = new Dictionary<int, List<int>>();
            foreach (var s in samples)
            {
                if (!s.Eating) continue;
                List<int> l;
                if (!eating.TryGetValue(s.RoomId, out l)) eating[s.RoomId] = l = new List<int>();
                if (!l.Contains(s.PawnId)) l.Add(s.PawnId);
            }

            foreach (var room in _open.Keys.ToList())
            {
                var o = _open[room];
                List<int> now_;
                int still = eating.TryGetValue(room, out now_) ? now_.Count : 0;
                bool timeout = now - o.StartTick >= MaxMealTicks || now < o.StartTick;
                if (still >= MinKeep && !timeout)
                {
                    foreach (var id in now_) if (!o.Pawns.Contains(id)) o.Pawns.Add(id);
                    continue;
                }
                _open.Remove(room);
                var g = new MealGroup { RoomId = room };
                g.Pawns.AddRange(o.Pawns.OrderBy(i => i));
                ends.Add(g);
            }

            foreach (var kv in eating)
            {
                if (kv.Value.Count < MinStart || _open.ContainsKey(kv.Key)) continue;
                int last;
                if (_lastStart.TryGetValue(kv.Key, out last) && now - last >= 0 && now - last < _cooldown) continue;
                _lastStart[kv.Key] = now;
                var o = new Open { StartTick = now };
                o.Pawns.AddRange(kv.Value);
                _open[kv.Key] = o;
                var g = new MealGroup { RoomId = kv.Key };
                g.Pawns.AddRange(kv.Value.OrderBy(i => i));
                starts.Add(g);
            }
        }

        public void Clear() { _open.Clear(); _lastStart.Clear(); }
    }
}

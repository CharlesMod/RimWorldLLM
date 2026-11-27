using System.Collections.Generic;

namespace RimWorldLLM
{
    /// <summary>Verse-free helpers of the vivarium hands.</summary>
    public static class VivariumHandsPure
    {
        /// <summary>Hours from..to-1, wrapping past 24: (22,6) = 22,23,0..5; to may be 24; from == to = none... a full day is (0,24).
        /// Values are normalised into 0..24 first; returns an empty list for out-of-range input.</summary>
        public static List<int> HourRange(int from, int to)
        {
            var l = new List<int>();
            if (from < 0 || from > 23 || to < 0 || to > 24) return l;
            int h = from;
            int n = to - from;
            if (n <= 0) n += 24;
            if (from == to) return l;
            for (int i = 0; i < n; i++) l.Add((h + i) % 24);
            return l;
        }
    }
}

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

        // ---- plain-English notes (the player reads these as the greentext "what"; verbs past tense) ----

        public static string Ordinal(int n)
        {
            switch (n) { case 1: return "1st"; case 2: return "2nd"; case 3: return "3rd"; default: return n + "th"; }
        }

        /// <summary>"A", "A and B", "A, B and C".</summary>
        public static string JoinAnd(IList<string> items)
        {
            if (items == null || items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            return string.Join(", ", new List<string>(items).GetRange(0, items.Count - 1)) + " and " + items[items.Count - 1];
        }

        public static string Possessive(string name)
        {
            name = (name ?? "").Trim();
            return name.EndsWith("s") ? name + "'" : name + "'s";
        }

        /// <summary>"set Ana's Cooking to 1st and Hauling to 3rd"; priority 0 = "turned off Ana's Hauling".</summary>
        public static string WorkNote(string pawn, IList<KeyValuePair<string, int>> set)
        {
            var on = new List<string>(); var off = new List<string>();
            foreach (var kv in set)
                if (kv.Value <= 0) off.Add(kv.Key); else on.Add(kv.Key + " to " + Ordinal(kv.Value));
            string who = Possessive(pawn);
            if (on.Count > 0 && off.Count > 0) return "set " + who + " " + JoinAnd(on) + " and turned off " + JoinAnd(off);
            if (on.Count > 0) return "set " + who + " " + JoinAnd(on);
            return "turned off " + who + " " + JoinAnd(off);
        }

        public static string HourText(int h) { return (h % 24) + ":00"; }

        /// <summary>"put Ana on sleep from 22:00 to 6:00".</summary>
        public static string ScheduleNote(string pawn, int from, int to, string assignment)
        {
            return "put " + pawn + " on " + (assignment ?? "").Trim().ToLowerInvariant() + " from " + HourText(from) + " to " + HourText(to);
        }

        /// <summary>"paused the game", "set the game speed to 2", "set the game speed to 2 and paused the game".</summary>
        public static string TimeNote(bool? pause, int? speed)
        {
            var parts = new List<string>();
            if (speed.HasValue) parts.Add("set the game speed to " + speed.Value);
            if (pause.HasValue) parts.Add(pause.Value ? "paused the game" : "unpaused the game");
            return string.Join(" and ", parts);
        }

        /// <summary>What a time call tries to do, for "couldn't ..." lines.</summary>
        public static string TimeWhat(bool? pause, int? speed)
        {
            var parts = new List<string>();
            if (speed.HasValue) parts.Add("change the game speed");
            if (pause.HasValue) parts.Add(pause.Value ? "pause the game" : "unpause the game");
            return string.Join(" and ", parts);
        }

        public static string DraftNote(IList<string> names, bool drafted)
        {
            return drafted ? "drafted " + JoinAnd(names) : "released " + JoinAnd(names) + " from the draft";
        }

        public static string DraftWhat(IList<string> names, bool drafted)
        {
            return drafted ? "draft " + JoinAnd(names) : "release " + JoinAnd(names) + " from the draft";
        }

        public static string PolicyNote(string pawn, string kind, string label)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "outfit": return "had " + pawn + " wear the " + label + " outfit";
                case "food": return "put " + pawn + " on the " + label + " food policy";
                case "drug": return "put " + pawn + " on the " + label + " drug policy";
                default: return "put " + pawn + " on the " + label + " " + kind + " policy";
            }
        }

        public static string PolicyWhat(string pawn, string kind, string name)
        {
            return "set " + Possessive(pawn) + " " + (kind ?? "").Trim().ToLowerInvariant() + " policy to " + name;
        }

        public static string OrderNote(string pawn, string order, int x, int z, string target, bool hold)
        {
            switch ((order ?? "").Trim().ToLowerInvariant())
            {
                case "move": return "sent " + pawn + " to (" + x + ", " + z + ")";
                case "attack": return "had " + pawn + " attack " + target;
                default: return hold ? "had " + pawn + " hold position" : "let " + pawn + " fire at will again";
            }
        }

        public static string OrderWhat(string pawn, string order, int x, int z, string target, bool hold)
        {
            switch ((order ?? "").Trim().ToLowerInvariant())
            {
                case "move": return "send " + pawn + " to (" + x + ", " + z + ")";
                case "attack": return "have " + pawn + " attack " + target;
                default: return hold ? "have " + pawn + " hold position" : "let " + pawn + " fire at will";
            }
        }
    }
}

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    public sealed class ChronicleEntry
    {
        public int Day;        // day index on the game clock, 0 = first day
        public string Kind;    // "day" or "major"
        public string Text;
        public int Tick;       // game tick at which the entry arrived
    }

    /// <summary>The colony chronicle: entries from the agent, newest 120 kept. Saved with the game by the host. Game thread.</summary>
    public sealed class ChronicleBook
    {
        public const int MaxEntries = 120;
        public const int MaxTextChars = 2000;
        public const int MaxLore = 12;     // kind "lore" entries kept; the oldest fade
        public const string KindDay = "day", KindMajor = "major", KindLore = "lore";

        public List<ChronicleEntry> Entries = new List<ChronicleEntry>();

        public static bool KnownKind(string kind) { return kind == KindDay || kind == KindMajor || kind == KindLore; }

        /// <summary>Appends an entry (text trimmed and cut), dropping the oldest beyond the cap. Returns the count held.</summary>
        public int Add(int day, string kind, string text, int tick)
        {
            text = (text ?? "").Trim();
            if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);
            Entries.Add(new ChronicleEntry { Day = day, Kind = kind, Text = text, Tick = tick });
            if (kind == KindLore)
            {
                int lore = 0;
                foreach (var e in Entries) if (e.Kind == KindLore) lore++;
                for (int i = 0; lore > MaxLore && i < Entries.Count; )
                    if (Entries[i].Kind == KindLore) { Entries.RemoveAt(i); lore--; } else i++;
            }
            if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
            return Entries.Count;
        }

        /// <summary>Entries whose day is at least sinceDay, oldest first.</summary>
        public JArray Since(int sinceDay)
        {
            var a = new JArray();
            foreach (var e in Entries)
                if (e.Day >= sinceDay)
                    a.Add(new JObject { ["day"] = e.Day, ["kind"] = e.Kind, ["text"] = e.Text, ["tick"] = e.Tick });
            return a;
        }

        /// <summary>The entries the Voices window lists: everything except the shared-name (lore) entries, oldest first.</summary>
        public List<ChronicleEntry> History()
        {
            var l = new List<ChronicleEntry>();
            foreach (var e in Entries) if (e.Kind != KindLore) l.Add(e);
            return l;
        }

        public void Clear() { Entries.Clear(); }
    }
}

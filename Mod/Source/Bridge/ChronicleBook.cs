using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
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
        public const string KindDay = "day", KindMajor = "major";

        public List<ChronicleEntry> Entries = new List<ChronicleEntry>();

        public static bool KnownKind(string kind) { return kind == KindDay || kind == KindMajor; }

        /// <summary>Appends an entry (text trimmed and cut), dropping the oldest beyond the cap. Returns the count held.</summary>
        public int Add(int day, string kind, string text, int tick)
        {
            text = (text ?? "").Trim();
            if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);
            Entries.Add(new ChronicleEntry { Day = day, Kind = kind, Text = text, Tick = tick });
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

        public void Clear() { Entries.Clear(); }
    }
}

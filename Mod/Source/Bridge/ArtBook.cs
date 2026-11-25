using System.Collections.Generic;

namespace HermesVoice.Bridge
{
    public sealed class ArtEntry
    {
        public int Thing;     // thingIDNumber of the artwork
        public string Text;
        public int Tick;      // game tick at which the text arrived
    }

    /// <summary>Agent-written art descriptions, by artwork thing id. Newest MaxEntries kept. Saved with the game by the host. Game thread.</summary>
    public sealed class ArtBook
    {
        public const int MaxEntries = 200;
        public const int MaxTextChars = 600;

        public List<ArtEntry> Entries = new List<ArtEntry>();

        /// <summary>Stores (or replaces) the text for a thing. Text is trimmed and cut; empty text stores nothing. Returns the count held.</summary>
        public int Set(int thing, string text, int tick)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Entries.Count;
            if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);
            Entries.RemoveAll(e => e.Thing == thing);
            Entries.Add(new ArtEntry { Thing = thing, Text = text, Tick = tick });
            if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
            return Entries.Count;
        }

        /// <summary>The stored text, or null when none.</summary>
        public string Get(int thing)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].Thing == thing) return Entries[i].Text;
            return null;
        }

        public bool Remove(int thing) { return Entries.RemoveAll(e => e.Thing == thing) > 0; }

        public void Clear() { Entries.Clear(); }
    }
}

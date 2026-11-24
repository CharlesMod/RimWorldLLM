using System.Collections.Generic;
using Verse;

namespace HermesVoice
{
    public class ConversationEntry : IExposable
    {
        public int PawnId;
        public string PawnName;
        public string Text;
        public int Tick;

        public ConversationEntry() { }

        public ConversationEntry(int pawnId, string pawnName, string text)
        {
            PawnId = pawnId;
            PawnName = pawnName ?? "";
            Text = text ?? "";
            Tick = Find.TickManager.TicksGame;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref PawnId, "pawnId", 0);
            Scribe_Values.Look(ref PawnName, "pawnName", "");
            Scribe_Values.Look(ref Text, "text", "");
            Scribe_Values.Look(ref Tick, "tick", 0);
        }
    }

    public class ConversationLog
    {
        public List<ConversationEntry> Entries = new List<ConversationEntry>();
        const int MaxEntries = 500;

        public void Add(int pawnId, string pawnName, string text)
        {
            Entries.Add(new ConversationEntry(pawnId, pawnName, text));
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
        }
    }
}

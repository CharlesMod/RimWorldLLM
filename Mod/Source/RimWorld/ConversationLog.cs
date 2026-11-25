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
        public int ListenerId;          // 0 = nobody addressed
        public string ListenerName;
        public string Trigger;          // what prompted the line: "player" (a talk request) or "agent" (its own initiative)
        public string Intent;           // intent label the agent sent ("none" for plain speech)
        public string Effect;           // intent whose effect was applied; empty when none
        public string Refused;          // why no effect was applied (effects_off, pair_cap, ...); empty when applied or not sought

        public ConversationEntry() { }

        public ConversationEntry(int pawnId, string pawnName, string text)
        {
            PawnId = pawnId;
            PawnName = pawnName ?? "";
            Text = text ?? "";
            ListenerName = "";
            Trigger = "agent";
            Intent = "none";
            Effect = "";
            Refused = "";
            Tick = Find.TickManager.TicksGame;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref PawnId, "pawnId", 0);
            Scribe_Values.Look(ref PawnName, "pawnName", "");
            Scribe_Values.Look(ref Text, "text", "");
            Scribe_Values.Look(ref Tick, "tick", 0);
            Scribe_Values.Look(ref ListenerId, "listenerId", 0);
            Scribe_Values.Look(ref ListenerName, "listenerName", "");
            Scribe_Values.Look(ref Trigger, "trigger", "agent");
            Scribe_Values.Look(ref Intent, "intent", "none");
            Scribe_Values.Look(ref Effect, "effect", "");
            Scribe_Values.Look(ref Refused, "refused", "");
        }
    }

    public class ConversationLog
    {
        public List<ConversationEntry> Entries = new List<ConversationEntry>();
        public const int MaxEntries = 500;

        public ConversationEntry Add(int pawnId, string pawnName, string text)
        {
            var e = new ConversationEntry(pawnId, pawnName, text);
            Entries.Add(e);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            return e;
        }
    }
}

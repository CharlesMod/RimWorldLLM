using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// A spoken line in the vanilla play log. Subclasses the vanilla interaction entry so the usual icon, click-to-jump
    /// and Log tab filtering work for speaker and listener; the line itself is saved with the entry.
    /// </summary>
    internal static class SayLogFormat
    {
        /// <summary>"Emma to Thorn: "..."" in both pawns' Log tabs; "Emma: "..."" when there is no listener.</summary>
        internal static string Format(Pawn speaker, Pawn listener, string line)
        {
            string s = speaker != null ? speaker.LabelShort : "?";
            if (listener == null || listener == speaker) return UiUtil.T("HV_LogLine", s, line ?? "");
            return UiUtil.T("HV_LogLineTo", s, listener.LabelShort, line ?? "");
        }
    }

    public class PlayLogEntry_HermesSay : PlayLogEntry_Interaction
    {
        string text = "";

        public PlayLogEntry_HermesSay() { }

        public PlayLogEntry_HermesSay(InteractionDef def, Pawn speaker, Pawn listener, string line)
            : base(def, speaker, listener ?? speaker, new List<RulePackDef>())
        {
            text = line ?? "";
        }

        public string Line { get { return text; } }

        protected override string ToGameStringFromPOV_Worker(Thing pov, bool forceLog)
        {
            return SayLogFormat.Format(initiator, recipient, text);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref text, "hvText", "");
        }
    }

    /// <summary>A line said during a fight, for the combat log. Concerns speaker and listener.</summary>
    public class BattleLogEntry_HermesSay : LogEntry
    {
        Pawn speaker;
        Pawn listener;
        string text = "";

        public BattleLogEntry_HermesSay() { }

        public BattleLogEntry_HermesSay(Pawn speaker, Pawn listener, string line)
        {
            this.speaker = speaker;
            this.listener = listener;
            text = line ?? "";
        }

        public override IEnumerable<Thing> GetConcerns()
        {
            if (speaker != null) yield return speaker;
            if (listener != null && listener != speaker) yield return listener;
        }

        public override bool Concerns(Thing t) { return t != null && (t == speaker || t == listener); }

        protected override string ToGameStringFromPOV_Worker(Thing pov, bool forceLog)
        {
            return SayLogFormat.Format(speaker, listener, text);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref speaker, "hvSpeaker", true);
            Scribe_References.Look(ref listener, "hvListener", true);
            Scribe_Values.Look(ref text, "hvText", "");
        }
    }

    /// <summary>Mood thought that shows the line that caused it as its description.</summary>
    public class Thought_HVMemory : Thought_Memory
    {
        public string line;

        public override string Description
        {
            get
            {
                string d = base.Description;
                return string.IsNullOrEmpty(line) ? d : d + "\n\n\"" + line + "\"";
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref line, "hvLine");
        }
    }

    /// <summary>Opinion thought that shows the line that caused it as its description.</summary>
    public class Thought_HVSocial : Thought_MemorySocial
    {
        public string line;

        public override string Description
        {
            get
            {
                string d = base.Description;
                return string.IsNullOrEmpty(line) ? d : d + "\n\n\"" + line + "\"";
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref line, "hvLine");
        }
    }
}

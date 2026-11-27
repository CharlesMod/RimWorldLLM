using System.Collections.Generic;
using RimWorldLLM.Bridge;
using RimWorld;
using Verse;

namespace RimWorldLLM
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
            if (listener == null || listener == speaker) return UiUtil.T("RWL_LogLine", s, line ?? "");
            return UiUtil.T("RWL_LogLineTo", s, listener.LabelShort, line ?? "");
        }
    }

    public class PlayLogEntry_RWLSay : PlayLogEntry_Interaction
    {
        string text = "";
        string effect = "";     // the intent the game applied for this line, "" when none: one merged entry, never a second one

        public PlayLogEntry_RWLSay() { }

        public PlayLogEntry_RWLSay(InteractionDef def, Pawn speaker, Pawn listener, string line, string appliedEffect = null)
            : base(def, speaker, listener ?? speaker, new List<RulePackDef>())
        {
            text = line ?? "";
            effect = EntryKinds.EffectWording(appliedEffect) ?? "";
        }

        public string Line { get { return text; } }

        protected override string ToGameStringFromPOV_Worker(Thing pov, bool forceLog)
        {
            var s = RimWorldLLMMod.Settings;
            if (effect.Length > 0 && (s == null || s.showQuotes) && recipient != null && recipient != initiator)
                return UiUtil.T("RWL_LogEffect_" + effect, initiator.LabelShort, recipient.LabelShort, text);
            return SayLogFormat.Format(initiator, recipient, text);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref text, "hvText", "");
            Scribe_Values.Look(ref effect, "hvEffect", "");
        }
    }

    /// <summary>A line said during a fight, for the combat log. Concerns speaker and listener.</summary>
    public class BattleLogEntry_RWLSay : LogEntry
    {
        Pawn speaker;
        Pawn listener;
        string text = "";

        public BattleLogEntry_RWLSay() { }

        public BattleLogEntry_RWLSay(Pawn speaker, Pawn listener, string line)
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
}

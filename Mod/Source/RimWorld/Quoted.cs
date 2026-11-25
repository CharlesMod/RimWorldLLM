using System;
using System.Collections.Generic;
using System.Linq;
using HermesVoice.Bridge;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>Reads and copies fields the game keeps private or protected. A failure throws, and the caller leaves the vanilla object as it was.</summary>
    internal static class Reflect
    {
        public static T Get<T>(object o, string field)
        {
            var f = HarmonyLib.AccessTools.Field(o.GetType(), field);
            if (f == null) throw new MissingFieldException(o.GetType().Name, field);
            return (T)f.GetValue(o);
        }

        public static void Copy(Type owner, string field, object from, object to)
        {
            var f = HarmonyLib.AccessTools.Field(owner, field);
            if (f == null) throw new MissingFieldException(owner.Name, field);
            f.SetValue(to, f.GetValue(from));
        }
    }

    /// <summary>Shared behaviour of the two quoted thought classes.</summary>
    public static class QuotedThought
    {
        public static bool Shown { get { var s = HermesVoiceMod.Settings; return s == null || s.showQuotes; } }

        /// <summary>Quotes of this thought and of its same-def, same-person siblings (a stacked group shows as one row),
        /// newest first, at most three.</summary>
        public static string Describe(Thought_Memory self, List<QuoteEntry> own)
        {
            if (!Shown) return "";
            var all = new List<QuoteEntry>();
            foreach (var q in own) Quotes.Push(all, q);
            var mems = self.pawn?.needs?.mood?.thoughts?.memories?.Memories;
            if (mems != null)
                foreach (var m in mems)
                {
                    if (m == self || m.def != self.def || m.otherPawn != self.otherPawn) continue;
                    var q = m is Thought_HVMemory a ? a.quotes : (m is Thought_HVSocial b ? b.quotes : null);
                    if (q != null) foreach (var e in q) Quotes.Push(all, e);
                }
            all.Sort((x, y) => y.Tick.CompareTo(x.Tick));
            if (all.Count > Quotes.MaxShown) all.RemoveRange(Quotes.MaxShown, all.Count - Quotes.MaxShown);
            if (all.Count == 0) return "";
            string label = self.LabelCap;
            return Quotes.Tooltip(all, q => string.IsNullOrEmpty(q.Speaker)
                ? UiUtil.T("HV_QuoteNoSpeaker", label, q.Text)
                : UiUtil.T("HV_QuoteThought", label, q.Speaker, q.Text));
        }

        /// <summary>When a new quoted memory stacks onto an existing one, the older memory also keeps the new quote.</summary>
        public static void ShareWithSiblings(Thought_Memory self, List<QuoteEntry> own)
        {
            var mems = self.pawn?.needs?.mood?.thoughts?.memories?.Memories;
            if (mems == null) return;
            foreach (var m in mems)
            {
                if (m == self || m.def != self.def || m.otherPawn != self.otherPawn) continue;
                var q = m is Thought_HVMemory a ? a.quotes : (m is Thought_HVSocial b ? b.quotes : null);
                if (q != null) foreach (var e in own) Quotes.Push(q, e);
            }
        }

        public static string Merge(string baseText, string quotes)
        {
            return string.IsNullOrEmpty(quotes) ? baseText : baseText + "\n\n" + quotes;
        }

        // Save: three parallel lists, so a plain save has no nested custom types.
        public static void Save(List<QuoteEntry> quotes, string legacyLine, out string legacyOut)
        {
            var texts = quotes.Select(q => q.Text).ToList();
            var who = quotes.Select(q => q.Speaker ?? "").ToList();
            var ticks = quotes.Select(q => q.Tick).ToList();
            Scribe_Collections.Look(ref texts, "hvQuoteTexts", LookMode.Value);
            Scribe_Collections.Look(ref who, "hvQuoteSpeakers", LookMode.Value);
            Scribe_Collections.Look(ref ticks, "hvQuoteTicks", LookMode.Value);
            legacyOut = legacyLine;
            Scribe_Values.Look(ref legacyOut, "hvLine");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                quotes.Clear();
                if (texts != null)
                    for (int i = 0; i < texts.Count; i++)
                        quotes.Add(new QuoteEntry(texts[i], who != null && i < who.Count ? who[i] : "", ticks != null && i < ticks.Count ? ticks[i] : 0));
                if (quotes.Count == 0 && !string.IsNullOrEmpty(legacyOut)) quotes.Add(new QuoteEntry(legacyOut, "", 0)); // saves from before speakers
            }
        }

        /// <summary>Copies a vanilla memory into the quoted class with the same def, stage, age, offsets and other pawn.</summary>
        public static Thought_Memory Wrap(Thought_Memory v)
        {
            Thought_Memory n;
            if (v is Thought_MemorySocial vs)
            {
                var hs = new Thought_HVSocial();
                hs.opinionOffset = vs.opinionOffset;
                n = hs;
            }
            else n = new Thought_HVMemory();
            n.def = v.def;
            n.pawn = v.pawn;
            n.sourcePrecept = v.sourcePrecept;
            n.age = v.age;
            n.moodPowerFactor = v.moodPowerFactor;
            n.moodOffset = v.moodOffset;
            n.otherPawn = v.otherPawn;
            n.permanent = v.permanent;
            Reflect.Copy(typeof(Thought_Memory), "forcedStage", v, n);
            n.durationTicksOverride = v.durationTicksOverride;
            return n;
        }

        public static void AddTo(Thought_Memory t, QuoteEntry q)
        {
            var list = t is Thought_HVMemory a ? a.quotes : (t is Thought_HVSocial b ? b.quotes : null);
            if (list != null) Quotes.Push(list, q);
        }
    }

    /// <summary>Mood thought that carries the lines that caused it. Description reads: label by speaker, "line".
    /// Without this mod a save holding one logs a load error for the thought and drops it; nothing else breaks.</summary>
    public class Thought_HVMemory : Thought_Memory
    {
        public List<QuoteEntry> quotes = new List<QuoteEntry>();
        string legacy;

        public override string Description { get { return QuotedThought.Merge(base.Description, QuotedThought.Describe(this, quotes)); } }

        public override bool TryMergeWithExistingMemory(out bool showBubble)
        {
            QuotedThought.ShareWithSiblings(this, quotes);
            return base.TryMergeWithExistingMemory(out showBubble);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            string l;
            QuotedThought.Save(quotes, legacy, out l);
            legacy = l;
        }
    }

    /// <summary>Opinion thought that carries the lines that caused it.</summary>
    public class Thought_HVSocial : Thought_MemorySocial
    {
        public List<QuoteEntry> quotes = new List<QuoteEntry>();
        string legacy;

        public override string Description { get { return QuotedThought.Merge(base.Description, QuotedThought.Describe(this, quotes)); } }

        public override bool TryMergeWithExistingMemory(out bool showBubble)
        {
            QuotedThought.ShareWithSiblings(this, quotes);
            return base.TryMergeWithExistingMemory(out showBubble);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            string l;
            QuotedThought.Save(quotes, legacy, out l);
            legacy = l;
        }
    }

    /// <summary>
    /// A vanilla interaction's log entry once the line for it has arrived: same def, pair, id and time as the original, the
    /// vanilla sentence as the lead-in and the quote after it. Replaces the original in the play log.
    /// </summary>
    public class PlayLogEntry_HermesInteraction : PlayLogEntry_Interaction
    {
        string quote = "";

        public PlayLogEntry_HermesInteraction() { }

        public PlayLogEntry_HermesInteraction(PlayLogEntry_Interaction orig, string quote)
            : base(Reflect.Get<InteractionDef>(orig, "intDef"), Reflect.Get<Pawn>(orig, "initiator"), Reflect.Get<Pawn>(orig, "recipient"),
                   Reflect.Get<List<RulePackDef>>(orig, "extraSentencePacks") ?? new List<RulePackDef>())
        {
            this.quote = quote ?? "";
            Reflect.Copy(typeof(LogEntry), "logID", orig, this);          // same id, so the same grammar seed: the same vanilla sentence
            Reflect.Copy(typeof(LogEntry), "ticksAbs", orig, this);
            Reflect.Copy(typeof(PlayLogEntry_Interaction), "initiatorFaction", orig, this);
            Reflect.Copy(typeof(PlayLogEntry_Interaction), "initiatorIdeo", orig, this);
        }

        public string Quote { get { return quote; } }

        protected override string ToGameStringFromPOV_Worker(Thing pov, bool forceLog)
        {
            string v = base.ToGameStringFromPOV_Worker(pov, forceLog);
            if (quote.Length == 0 || !QuotedThought.Shown) return v;
            return Quotes.LeadIn(v, quote, (lead, q) => UiUtil.T("HV_LogLeadIn", lead, q));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref quote, "hvQuote", "");
        }
    }

    /// <summary>What a vanilla interaction left behind that a late-arriving line can attach to.</summary>
    public sealed class VanillaPending
    {
        public PlayLogEntry_Interaction Entry;
        public Pawn Initiator, Recipient;
        public List<Thought_Memory> Thoughts = new List<Thought_Memory>();
    }

    /// <summary>The pending table for the game's own interactions, and attaching a line to one. Game thread only.</summary>
    public static class QuotedInteractions
    {
        public static readonly PendingTable<VanillaPending> Pending = new PendingTable<VanillaPending>(64);
        static int _n;

        public static string NewRef() { return "v" + Find.TickManager.TicksGame + "-" + (++_n); }

        public static int WindowTicks
        {
            get
            {
                int m = HermesVoiceMod.Settings?.attachWindowMinutes ?? 10;
                return Math.Max(1, (int)(m * (60000f / 1440f)));
            }
        }

        public static void Reset() { Pending.Sweep(int.MaxValue, 0); }

        /// <summary>Attach the line to the vanilla entry and thoughts of that interaction. False leaves everything vanilla.</summary>
        public static PendingTable<VanillaPending>.Result TryAttach(string vref, Pawn speaker, Pawn listener, string line)
        {
            int now = Find.TickManager.TicksGame;
            VanillaPending p;
            var r = Pending.TryAttach(vref, speaker.thingIDNumber, listener != null ? listener.thingIDNumber : -1, now, WindowTicks, out p);
            if (r != PendingTable<VanillaPending>.Result.Attached) return r;
            string quote = Quotes.Trim(line, HermesVoiceMod.Settings?.quoteMaxChars ?? Quotes.DefaultMaxChars);
            if (quote.Length == 0) return PendingTable<VanillaPending>.Result.Unknown;
            var q = new QuoteEntry(quote, speaker.LabelShort, now);
            try
            {
                var entries = Find.PlayLog.AllEntries;
                for (int i = 0; i < entries.Count && i < 40; i++)
                    if (ReferenceEquals(entries[i], p.Entry)) { entries[i] = new PlayLogEntry_HermesInteraction(p.Entry, quote); break; }
                foreach (var t in p.Thoughts)
                {
                    var mems = t.pawn?.needs?.mood?.thoughts?.memories?.Memories;
                    if (mems == null) continue;
                    int idx = mems.IndexOf(t);
                    if (idx < 0) continue;                              // merged away or expired: stays as the game made it
                    var w = QuotedThought.Wrap(t);
                    QuotedThought.AddTo(w, q);
                    mems[idx] = w;
                }
            }
            catch (Exception e) { Log.Warning("[HermesVoice] quote attach failed: " + e.Message); }
            return r;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using HermesVoice.Bridge;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>Applies spoken-line effects as thoughts, under the strength setting, per-pair daily cap and stack limits.</summary>
    public static class SayEffects
    {
        public const int PairDailyCap = 4;
        static readonly PairCap Caps = new PairCap(PairDailyCap);

        public static void Reset() { Caps.Clear(); }

        public sealed class Outcome
        {
            public string Effect;           // intent that was applied
            public List<string> Thoughts = new List<string>();
            public string Refused;          // reason, when not applied
        }

        static bool HasNegativeThought(Pawn p)
        {
            var mood = p.needs?.mood;
            if (mood == null) return false;
            var groups = new List<Thought>();
            mood.thoughts.GetDistinctMoodThoughtGroups(groups);
            return groups.Any(t => mood.thoughts.MoodOffsetOfGroup(t) < 0f);
        }

        /// <summary>Boast and complain: a listener who is kind takes it well; an abrasive or cold one does not.</summary>
        static float TraitFactor(Pawn listener)
        {
            var traits = listener.story?.traits;
            if (traits == null) return 1f;
            if (traits.HasTrait(TraitDefOf.Kind)) return -1f;       // flips to a small gain
            if (traits.HasTrait(TraitDefOf.Psychopath)) return 1.5f;
            return 1f;
        }

        static int Cap() { return HermesVoiceMod.Settings?.quoteMaxChars ?? Quotes.DefaultMaxChars; }

        static bool Give(Pawn target, ThoughtDef def, Pawn other, bool social, float amount, float strength, string line, string speakerName, Outcome o)
        {
            var mem = target.needs?.mood?.thoughts?.memories;
            if (mem == null || def == null) return false;
            var t = ThoughtMaker.MakeThought(def) as Thought_Memory;
            if (t == null) return false;
            var hv = t as Thought_HVMemory;
            var hs = t as Thought_HVSocial;
            if (hv != null) QuotedThought.AddTo(hv, new QuoteEntry(Quotes.Trim(line, Cap()), speakerName, Find.TickManager.TicksGame));
            if (hs != null) QuotedThought.AddTo(hs, new QuoteEntry(Quotes.Trim(line, Cap()), speakerName, Find.TickManager.TicksGame));
            t.pawn = target;            // set before the stack merge so a merged memory shares its quote
            t.otherPawn = other;
            if (!social) t.moodPowerFactor = strength;
            mem.TryGainMemory(t, other);
            if (!mem.Memories.Contains(t)) return false; // merged or refused by the vanilla stack rules
            if (social && hs != null) hs.opinionOffset = amount * strength;
            o.Thoughts.Add(def.defName);
            return true;
        }

        public static Outcome Apply(string intent, Pawn speaker, Pawn listener, Pawn about, List<Pawn> witnesses, string line)
        {
            var o = new Outcome();
            var s = HermesVoiceMod.Settings;
            float strength = EffectTable.Strength(s?.effects ?? "normal");
            if (intent == "none" || string.IsNullOrEmpty(intent)) { o.Refused = "no_intent"; return o; }
            if (strength <= 0f) { o.Refused = "effects_off"; return o; }
            var specs = EffectTable.For(intent);
            if (specs.Count == 0) { o.Refused = "no_effect"; return o; }
            if (listener == null && intent != "gossip") { o.Refused = "no_listener"; return o; }
            if (intent == "comfort" && !HasNegativeThought(listener)) { o.Refused = "not_negative"; return o; }
            if (intent == "gossip" && (about == null || about == listener || about == speaker || listener == null)) { o.Refused = "no_about"; return o; }
            int day = GenDate.DaysPassed;
            if (!Caps.TryConsume(speaker.thingIDNumber, (listener ?? speaker).thingIDNumber, day)) { o.Refused = "pair_cap"; return o; }

            foreach (var e in specs)
            {
                var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(e.Def);
                float amount = e.Amount;
                if (intent == "boast" || intent == "complain") amount = e.Amount * TraitFactor(listener);
                switch (e.Target)
                {
                    case EffectTarget.Listener:
                        Give(listener, def, speaker, e.Social, amount, strength, line, speaker.LabelShort, o);
                        break;
                    case EffectTarget.Speaker:
                        Give(speaker, def, listener, e.Social, amount, strength, line, speaker.LabelShort, o);
                        break;
                    case EffectTarget.About:
                        Give(listener, def, about, e.Social, amount, strength, line, speaker.LabelShort, o);
                        break;
                    case EffectTarget.Witnesses:
                        foreach (var w in witnesses)
                            if (w != speaker && w != listener) Give(w, def, speaker, e.Social, amount, strength, line, speaker.LabelShort, o);
                        break;
                }
            }
            o.Effect = intent;
            if (o.Thoughts.Count == 0) { o.Effect = null; o.Refused = "stack_full"; }
            return o;
        }
    }
}

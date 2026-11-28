// What stands out right now: picks the few facts about the scene, the speaker and the listener that a spoken line, a thought or a diary
// entry should be built from. Pure: no game types, no IO, deterministic (stable tie-break by Key).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Mind
{
    public enum SaliencePurpose { Line, Thought, Diary }

    public static class FactKind
    {
        public const string Room = "room", Weather = "weather", Time = "time", Season = "season", Job = "job", Nearby = "nearby",
            Event = "event", Wound = "wound", Need = "need", Mood = "mood", Listener = "listener", History = "history", Social = "social";
    }

    /// <summary>One thing a character could notice or mention. Text is plain words (no numbers the model is told never to say).</summary>
    public sealed class SalientFact
    {
        public string Kind = "";
        public string Key = "";
        public string Text = "";
        /// <summary>Intensity 0..1.</summary>
        public double Weight;
        /// <summary>In-game hours since it happened; 0 = now.</summary>
        public double AgeHours;
        public bool AboutListener;
        /// <summary>A social interaction or talk event.</summary>
        public bool Social;

        public SalientFact() { }
        public SalientFact(string kind, string key, string text, double weight, double ageHours = 0, bool aboutListener = false, bool social = false)
        {
            Kind = kind; Key = key; Text = text; Weight = weight; AgeHours = ageHours; AboutListener = aboutListener; Social = social;
        }
    }

    public sealed class SalienceAsk
    {
        public SaliencePurpose Purpose = SaliencePurpose.Line;
        public int Min = 3;
        public int Max = 6;
        /// <summary>Keys this character used in its last few prompts (novelty penalty).</summary>
        public ISet<string> RecentKeys;
        public string ListenerName = "";
    }

    public static class Salience
    {
        public const double HalfLifeShortHours = 4.0;
        public const double HalfLifeDiaryHours = 24.0;
        public const double ListenerBonus = 1.5;
        public const double NoveltyFactor = 0.35;
        public const double SocialFactor = 0.1;
        public const double SocialOutcomeWeight = 0.7;
        public const double Floor = 0.05;

        // Value talk is what the voice cards used to fixate on: such a fact is never picked.
        private static readonly Regex Price = new Regex(
            @"\bmarket value\b|\bprices?\b|\bpriced\b|\bsilver\b|\bworths?\b|\bcosts?\b|\bcostly\b|\bexpensive\b|\bcheap(er|ly)?\b|\bvaluable\b|\bvalue of\b|\bwealth\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool IsPriceTalk(string text) { return !string.IsNullOrEmpty(text) && Price.IsMatch(text); }

        public static double HalfLife(SaliencePurpose p) { return p == SaliencePurpose.Diary ? HalfLifeDiaryHours : HalfLifeShortHours; }

        public static double Score(SalientFact f, SalienceAsk ask)
        {
            double w = Math.Max(0.0, Math.Min(1.0, f.Weight));
            double age = Math.Max(0.0, f.AgeHours);
            double s = w * Math.Pow(0.5, age / HalfLife(ask.Purpose));
            if (ask.Purpose == SaliencePurpose.Line && f.AboutListener) s *= ListenerBonus;
            if (ask.Purpose != SaliencePurpose.Line && f.Social && w < SocialOutcomeWeight) s *= SocialFactor;
            if (ask.RecentKeys != null && ask.RecentKeys.Contains(f.Key ?? "")) s *= NoveltyFactor;
            return s;
        }

        public static List<SalientFact> Pick(IEnumerable<SalientFact> facts, SalienceAsk ask)
        {
            ask = ask ?? new SalienceAsk();
            int max = Math.Max(0, ask.Max), min = Math.Max(0, Math.Min(ask.Min, max));
            if (facts == null || max == 0) return new List<SalientFact>();
            // one entry per key (the best), no empty text, no value talk, no zero weight
            var best = new Dictionary<string, (SalientFact F, double S)>(StringComparer.Ordinal);
            foreach (var f in facts)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Text) || f.Weight <= 0 || IsPriceTalk(f.Text)) continue;
                string key = f.Key ?? "";
                double s = Score(f, ask);
                (SalientFact F, double S) cur;
                if (!best.TryGetValue(key, out cur) || s > cur.S) best[key] = (f, s);
            }
            var ranked = best.Values.OrderByDescending(x => x.S).ThenBy(x => x.F.Key ?? "", StringComparer.Ordinal).ToList();
            var above = ranked.Where(x => x.S >= Floor).ToList();
            var chosen = new List<(SalientFact F, double S)>();
            var kinds = new HashSet<string>();
            foreach (var x in above)                                    // first pass: one per kind
                if (chosen.Count < max && kinds.Add(x.F.Kind ?? "")) chosen.Add(x);
            foreach (var x in above)                                    // then fill by score
                if (chosen.Count < max && !chosen.Contains(x)) chosen.Add(x);
            foreach (var x in ranked)                                   // below the floor only to reach Min
                if (chosen.Count < min && !chosen.Contains(x)) chosen.Add(x);
            return chosen.OrderByDescending(x => x.S).ThenBy(x => x.F.Key ?? "", StringComparer.Ordinal).Select(x => x.F).ToList();
        }
    }
}

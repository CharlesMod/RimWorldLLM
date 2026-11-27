// Port of agent/rimworldllm_agent/games/rimworld/register.py: speech register, how simple or articulate a pawn's words are
// (vocabulary and sentence complexity, never personality).
//
// Pure functions over a GET /pawn snapshot, no I/O. Two layers:
// - BaseRegister: from LASTING factors (Intellectual skill, passion, traits, genes, dementia, age). Frozen in block B.
// - TempStates: from the CURRENT health and needs (drunk, drugged, dazed, head injury, sore mouth, exhausted).
//   They live only in the scene and last only while the snapshot shows them.
// The weights are a first cut and all sit in the tables below so they are easy to tune.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class Register
    {
        public static readonly Scale RegisterScale = Scale.Of("register", new[] {
            "very simple words, short broken sentences, sometimes mixes up words",
            "simple words and short plain sentences",
            "plain everyday speech",
            "clear and well-spoken, a good vocabulary",
            "articulate, a wide vocabulary, precise wording" }, start: 1);
        public static readonly int Lo = RegisterScale.Lo;
        public static readonly int Hi = RegisterScale.Hi;
        public const double Start = 3.0;

        // Intellectual skill level (inclusive upper bound) -> shift
        private static readonly (int Hi, double W)[] SkillBands = { (2, -1.0), (7, 0.0), (12, 0.5), (16, 1.0), (20, 1.5) };
        private static readonly Dictionary<string, double> Passion = new Dictionary<string, double> { { "none", 0.0 }, { "", 0.0 }, { "minor", 0.25 }, { "major", 0.5 } };
        private const double TooSmart = 1.0;
        // gene def (lowercase) -> shift; label substrings are the fallback
        private static readonly Dictionary<string, double> GeneDefs = new Dictionary<string, double> {
            { "aptituderemarkable_intellectual", 1.0 }, { "aptitudestrong_intellectual", 0.5 }, { "learning_fast", 0.25 },
            { "aptitudepoor_intellectual", -0.5 }, { "aptitudeterrible_intellectual", -1.0 }, { "learning_slow", -0.25 } };
        private static readonly (string Key, double W)[] GeneLabels = {
            ("quick study", 0.25), ("slow study", -0.25), ("remarkable intellectual", 1.0),
            ("strong intellectual", 0.5), ("poor intellectual", -0.5), ("terrible intellectual", -1.0) };
        private const double Dementia = -1.5;
        private static readonly string[] DementiaKeys = { "dementia", "alzheimer" };

        // (condition def lowercase) -> (phrase, shift) for a current drug high
        private static readonly Dictionary<string, (string Phrase, int Shift)> Highs = new Dictionary<string, (string, int)> {
            { "smokeleafhigh", ("high and drifting", -1) }, { "gojuicehigh", ("wired, fast and clipped", -1) },
            { "yayohigh", ("wired, fast and clipped", -1) }, { "flakehigh", ("high, thoughts loose and jumpy", -1) },
            { "psychiteteahigh", ("high, thoughts loose and jumpy", -1) }, { "wakeuphigh", ("wired, fast and clipped", -1) } };

        private static readonly HashSet<string> TempDefs = new HashSet<string>(new[] { "alcoholhigh", "hangover", "anesthetic" }.Concat(Highs.Keys));

        /// <summary>A condition that passes on its own within hours (shown in the scene only, never in the frozen bio).</summary>
        public static bool IsTemporary(JToken c)
        {
            var o = c as JObject;
            return o != null && TempDefs.Contains(Js.StrOr(o, "def", "").ToLowerInvariant());
        }

        private static int Clamp(double x) { return Math.Max(Lo, Math.Min(Hi, (int)Math.Floor(x + 0.5))); }

        private static List<(string Def, string Label)> Defs(JToken items)
        {
            var outp = new List<(string, string)>();
            foreach (var x in Js.Items(items))
            {
                var o = x as JObject;
                if (o != null) outp.Add((Js.StrOr(o, "def", "").ToLowerInvariant(), Js.StrOr(o, "label", "").ToLowerInvariant()));
                else outp.Add(("", Js.Str(x).ToLowerInvariant()));
            }
            return outp;
        }

        private static int AgeCap(JToken age)
        {
            if (!Js.IsNum(age, noBool: true)) return Hi;
            double a = Js.Dbl(age);
            if (a < 8) return 1;
            if (a < 13) return 2;
            if (a < 18) return 4;
            return Hi;
        }

        /// <summary>The raw score before rounding and the age cap (start 3).</summary>
        public static double BaseScore(JObject p)
        {
            double s = Start;
            foreach (var sk in Js.ItemsGet(p, "skills"))
            {
                if (Js.StrOr(sk, "skill", "").ToLowerInvariant() == "intellectual")
                {
                    var lvl = Js.Get(sk, "level");
                    if (Js.IsNum(lvl))
                    {
                        double l = Js.Dbl(lvl);
                        double w = SkillBands[SkillBands.Length - 1].W;
                        foreach (var b in SkillBands) if (l <= b.Hi) { w = b.W; break; }
                        s += w;
                    }
                    double pw;
                    s += Passion.TryGetValue(Js.StrOr(sk, "passion", "").ToLowerInvariant(), out pw) ? pw : 0.0;
                }
            }
            foreach (var (d, l) in Defs(Js.Get(p, "traits")))
                if (d == "toosmart" || l == "too smart") s += TooSmart;
            foreach (var (d, l) in Defs(Js.Get(p, "genes")))
            {
                double gw;
                if (GeneDefs.TryGetValue(d, out gw)) s += gw;
                else
                {
                    double w = 0.0;
                    foreach (var g in GeneLabels) if (l.Contains(g.Key)) { w = g.W; break; }
                    s += w;
                }
            }
            if (HasDementia(p)) s += Dementia;
            return s;
        }

        private static bool HasDementia(JObject p)
        {
            var h = Js.Get(p, "health");
            foreach (var (d, l) in Defs(Js.Get(h, "conditions")))
                if (DementiaKeys.Any(k => d.Contains(k) || l.Contains(k))) return true;
            return false;
        }

        /// <summary>The lasting register bin, 1..5.</summary>
        public static int BaseRegister(JObject p) { return Math.Min(Clamp(BaseScore(p)), AgeCap(Js.Get(p, "age"))); }

        /// <summary>(phrase, shift) for what the snapshot shows right now, worst first. Empty when nothing applies.</summary>
        public static List<(string Phrase, int Shift)> TempStates(JObject p)
        {
            var h = Js.Get(p, "health");
            var needs = Js.Get(p, "needs");
            var outp = new List<(string, int)>();
            bool anesthetic = false, brain = false;
            foreach (var c in Js.ItemsGet(h, "conditions"))
            {
                if (!(c is JObject)) continue;
                string d = Js.StrOr(c, "def", "").ToLowerInvariant(), l = Js.StrOr(c, "label", "").ToLowerInvariant(), part = Js.StrOr(c, "part", "").ToLowerInvariant();
                if (d == "alcoholhigh")
                {
                    var sev = Js.Get(c, "severity");
                    if (Js.IsNum(sev) && Js.Dbl(sev) >= 0.6) outp.Add(("very drunk: words slur badly, barely coherent, much simpler and looser than usual", -2));
                    else outp.Add(("drunk: words slur, simpler and looser than usual", -1));
                }
                else if (Highs.ContainsKey(d)) outp.Add(Highs[d]);
                else if (d == "anesthetic") anesthetic = true;
                else if (part.Contains("brain") && !DementiaKeys.Any(k => d.Contains(k) || l.Contains(k))) brain = true;
            }
            var cons = Js.Get(h, "consciousness");
            if (anesthetic || (Js.IsNum(cons) && Js.Dbl(cons) < 0.5)) outp.Add(("dazed, struggles to form words", -2));
            else if (Js.IsNum(cons) && Js.Dbl(cons) < 0.8) outp.Add(("foggy", -1));
            if (brain) outp.Add(("head injury, thoughts come slowly", -1));
            var talk = Js.Get(h, "talking");
            if (Js.IsNum(talk) && Js.Dbl(talk) < 0.5)
                outp.Add(("talking is hard (mouth or jaw hurt): few words, mumbled; this limits how much is said, not the vocabulary", -1));
            var rest = Js.Get(needs, "rest");
            if (Js.IsNum(rest) && Js.Dbl(rest) < 0.1) outp.Add(("exhausted", -1));
            return outp.OrderBy(t => t.Item2).ToList();      // a stable sort, worst first
        }

        public static int EffectiveRegister(JObject p)
        {
            int shift = TempStates(p).Sum(t => t.Shift);
            return Math.Max(Lo, Math.Min(Hi, BaseRegister(p) + shift));
        }

        /// <summary>The lasting register as a phrase (for block B's Words line).</summary>
        public static string BaseWords(JObject p) { return RegisterScale.ToWords(BaseRegister(p)); }

        public static string EffectiveWords(JObject p) { return RegisterScale.ToWords(EffectiveRegister(p)); }

        /// <summary>Block B's line: sits beside the voice card's style, never replaces it.</summary>
        public static string WordsLine(string phrase) { return "Words: " + phrase + " (your style above still applies)."; }

        /// <summary>(scene sentence, short phrase) for the current temporary states; ("", "") when there are none.</summary>
        public static (string Now, string Short) SpeechNow(JObject p)
        {
            var ts = TempStates(p);
            if (ts.Count == 0) return ("", "");
            string phrase = EffectiveWords(p);
            return ("Right now: " + string.Join("; ", ts.Select(t => t.Phrase)) + ". So for now your words are: " + phrase + ".", phrase);
        }
    }
}

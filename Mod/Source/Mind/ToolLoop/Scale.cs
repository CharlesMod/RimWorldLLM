// Port of core/toolloop/scale.py: ordinal scales. A magnitude the model picks as words, the game reads as a number.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    public sealed class Scale
    {
        private static readonly Regex NonWord = new Regex("[^a-z0-9 ]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberText = new Regex(@"^-?\d+(\.\d+)?\z", RegexOptions.CultureInvariant);

        // modifier words fall into three classes so "pretty important" can meet "fairly important"
        private static readonly Dictionary<string, string> Class = BuildClass();
        private static readonly HashSet<string> Drop = new HashSet<string> { "of", "a", "an", "the", "at", "all", "is", "it", "i", "feel", "so", "to", "be" };

        private static Dictionary<string, string> BuildClass()
        {
            var d = new Dictionary<string, string>();
            foreach (var w in new[] { "very", "extremely", "highly", "really", "super", "hugely", "greatly", "much", "far", "lots", "totally", "utterly" }) d[w] = "<S>";
            foreach (var w in new[] { "fairly", "pretty", "quite", "rather", "moderately", "reasonably" }) d[w] = "<M>";
            foreach (var w in new[] { "slightly", "barely", "somewhat", "mildly", "little", "bit", "kind", "sort", "marginally", "minorly" }) d[w] = "<W>";
            return d;
        }

        internal static string Norm(string s) => NonWord.Replace((s ?? "").ToLowerInvariant(), " ").Trim(' ');

        private static HashSet<string> Tokens(string text)
        {
            var o = new HashSet<string>();
            foreach (var w in Norm(text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Class.TryGetValue(w, out string c)) o.Add(c);
                else if (Drop.Contains(w)) continue;
                else if (w.StartsWith("un", StringComparison.Ordinal) && PyStr.Len(w) > 4) { o.Add("not"); o.Add(w.Substring(2)); }
                else o.Add(w);
            }
            return o;
        }

        public string Name { get; }
        /// <summary>(phrase, value) pairs ordered from lowest to highest.</summary>
        public IReadOnlyList<(string Phrase, int Value)> Bins { get; }

        public Scale(string name, IEnumerable<(string Phrase, int Value)> bins)
        {
            Name = name;
            var b = bins.ToList();
            Bins = b;
            if (b.Count < 2) throw new PyValueError("a scale needs at least two bins");
            var vals = b.Select(x => x.Value).ToList();
            var sorted = vals.OrderBy(v => v).ToList();
            if (!vals.SequenceEqual(sorted) || vals.Distinct().Count() != vals.Count) throw new PyValueError("bin values must be strictly increasing");
            if (b.Select(x => Norm(x.Phrase)).Distinct().Count() != b.Count) throw new PyValueError("bin phrases must be distinct");
        }

        public static Scale Of(string name, IList<string> phrases, IList<int> values = null, int start = 1)
        {
            IList<int> vals = values ?? Enumerable.Range(start, phrases.Count).ToList();
            int n = Math.Min(phrases.Count, vals.Count);       // zip stops at the shorter
            return new Scale(name, Enumerable.Range(0, n).Select(i => (phrases[i], vals[i])));
        }

        public IReadOnlyList<string> Words => Bins.Select(b => b.Phrase).ToList();
        public IReadOnlyList<int> Values => Bins.Select(b => b.Value).ToList();
        public int Lo => Bins[0].Value;
        public int Hi => Bins[Bins.Count - 1].Value;

        /// <summary>Exact (case/punctuation-insensitive) phrase to number. KeyError when it is not a bin.</summary>
        public int ToValue(string phrase)
        {
            string n = Norm(phrase);
            foreach (var (p, v) in Bins) if (Norm(p) == n) return v;
            throw new PyKeyError(phrase);
        }

        public bool Has(object phrase)
        {
            try { ToValue(PyStr.StrOf(phrase)); return true; }
            catch (PyKeyError) { return false; }
        }

        // tuple compare (abs(v - number), v) as Python does it: a NaN never wins a comparison, so the first bin stays
        private static bool KeyLess((double d, int v) a, (double d, int v) b) => a.d != b.d ? a.d < b.d : a.v < b.v;

        /// <summary>The bin whose value is closest to number (ties go to the lower one); out-of-range clamps to the end.</summary>
        public (string Phrase, int Value) NearestBin(double number)
        {
            var best = Bins[0];
            var bestKey = (Math.Abs(best.Value - number), best.Value);
            for (int i = 1; i < Bins.Count; i++)
            {
                var k = (Math.Abs(Bins[i].Value - number), Bins[i].Value);
                if (KeyLess(k, bestKey)) { best = Bins[i]; bestKey = k; }
            }
            return best;
        }

        /// <summary>Phrase to show the model for a number the game produced.</summary>
        public string ToWords(double number) => NearestBin(number).Phrase;

        /// <summary>Tolerant parse for free text (outside grammar mode): exact phrase, a bare number (nearest bin), then the bin that
        /// shares the most content words with the text, modifier words compared by strength class; ties go toward the middle of the scale.
        /// Accepts a string, a number, a bool (never matches), or a decoded JSON value. Null when nothing fits.</summary>
        public int? Parse(object text)
        {
            if (text is JToken tok)
            {
                switch (tok.Type)
                {
                    case JTokenType.Boolean: return null;
                    case JTokenType.Integer: case JTokenType.Float: return NearestBin(Convert.ToDouble(((JValue)tok).Value, CultureInfo.InvariantCulture)).Value;
                    case JTokenType.String: text = (string)tok; break;
                    default: text = PyJson.Str(tok); break;
                }
            }
            if (text is bool) return null;
            switch (text)
            {
                case int i: return NearestBin(i).Value;
                case long l: return NearestBin(l).Value;
                case double d: return NearestBin(d).Value;
                case float f: return NearestBin(f).Value;
                case BigInteger bi: return NearestBin((double)bi).Value;
            }
            string raw = PyStr.StrOf(text);
            string t = Norm(raw);
            if (t.Length == 0) return null;
            foreach (var (p, v) in Bins) if (Norm(p) == t) return v;
            string stripped = PyStr.Strip(raw);
            if (NumberText.IsMatch(stripped) && double.TryParse(stripped, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                return NearestBin(num).Value;
            var want = Tokens(t);
            double mid = (Bins.Count - 1) / 2.0;
            int best = -1; (double jac, double pos) bestKey = (0, 0);
            for (int idx = 0; idx < Bins.Count; idx++)
            {
                var have = Tokens(Bins[idx].Phrase);
                var inter = want.Where(have.Contains).ToList();
                if (!inter.Any(w => !w.StartsWith("<", StringComparison.Ordinal))) continue;
                int union = want.Count + have.Count - inter.Count;
                double jac = (double)inter.Count / union;
                var key = (jac, -Math.Abs(idx - mid));
                bool better = best < 0 || (key.jac != bestKey.jac ? key.jac > bestKey.jac : key.Item2 > bestKey.pos);
                if (better) { best = idx; bestKey = key; }
            }
            return best < 0 ? (int?)null : Bins[best].Value;
        }

        // Two ready-made scales a game can use as they are or copy.
        public static readonly Scale Importance = Of("importance", new[] { "not important", "kind of important", "fairly important", "very important", "critical" }, start: 1);
        public static readonly Scale Change = Of("change", new[] { "much worse", "worse", "a little worse", "no change", "a little better", "better", "much better" }, new[] { -3, -2, -1, 0, 1, 2, 3 });
    }
}

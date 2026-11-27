// Port of agent/rimworldllm_agent/core/loopguard.py.
// Conversation loop guard: a new turn that repeats an earlier line is a validation failure.
// Every turn is checked against every earlier turn of its conversation and against the speaker's own
// last few lines from other conversations. Similarity is on normalised words: the larger of the word
// 3-gram Jaccard and the Jaccard of content words. Lines shorter than MinWords are never judged.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public sealed class Hit
    {
        public string Kind;      // "turn" (earlier turn of this conversation) or "own" (the speaker's line in another)
        public string Other;     // the earlier line it repeats
        public double Score;
        public bool Own;         // the earlier line was the speaker's own

        public Hit(string kind, string other, double score, bool own = false)
        {
            Kind = kind; Other = other; Score = score; Own = own;
        }

        public string Reason
        {
            get
            {
                string who = Own ? "your own earlier line" : "an earlier line in this conversation";
                return "it repeats " + who + ": \"" + Other + "\"";
            }
        }
    }

    public sealed class LoopStats
    {
        public int Checked;      // turns checked
        public int Caught;       // first attempts that repeated
        public int Retried;
        public int Recovered;    // the retry was clean
        public int Ended;        // retries exhausted: the conversation ended with no line
        public List<JObject> Trace = new List<JObject>();

        public JObject ToDict()
        {
            return new JObject
            {
                ["checked"] = Checked, ["caught"] = Caught, ["retried"] = Retried,
                ["recovered"] = Recovered, ["ended"] = Ended,
            };
        }
    }

    public static class LoopGuard
    {
        public const int MinWords = 4;               // a shorter line can't be called a loop
        public const double Threshold = 0.6;         // inside a conversation
        public const double CrossThreshold = 0.8;    // against the speaker's own lines from other conversations
        public const int CrossMinWords = 5;
        public const int MinContent = 2;             // content words each side needs for the content measure

        static readonly HashSet<string> Stop = new HashSet<string>(
            ("the a an and or but to of in on at for with is are was were be been it its this that i you he she we they my "
             + "your his her our their me him us them do does did not no so as if then there here have has had will would can "
             + "could just than too very what why how who when where from by about up out im ill ive id i'm i'll don't it's "
             + "that's okay hm well copy naturally think like totally").Split(
                new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

        static readonly Regex Word = new Regex("[a-z0-9]+(?:'[a-z]+)?", RegexOptions.CultureInvariant);

        public static List<string> Words(string text)
        {
            // Python str.lower() maps U+0130 to "i" + U+0307; .NET invariant keeps it. Match Python.
            string low = text.Replace("İ", "i̇").ToLowerInvariant().Replace("’", "'");
            var res = new List<string>();
            foreach (Match m in Word.Matches(low)) res.Add(m.Value);
            return res;
        }

        static HashSet<string> Grams(List<string> w, int n)
        {
            n = Math.Min(n, w.Count);
            var set = new HashSet<string>();
            if (n == 0) return set;
            for (int i = 0; i <= w.Count - n; i++)
                set.Add(string.Join("\n", w.GetRange(i, n)));
            return set;
        }

        static double Jac(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0.0;
            int inter = a.Count(x => b.Contains(x));
            int union = a.Count + b.Count - inter;
            return (double)inter / union;
        }

        /// <summary>0..1; 0 when either line is too short to judge.</summary>
        public static double Similarity(string a, string b, int minWords = MinWords)
        {
            var wa = Words(a); var wb = Words(b);
            if (wa.Count < minWords || wb.Count < minWords) return 0.0;
            if (wa.SequenceEqual(wb)) return 1.0;
            double s = Jac(Grams(wa, 3), Grams(wb, 3));
            var ca = new HashSet<string>(wa.Where(w => !Stop.Contains(w)));
            var cb = new HashSet<string>(wb.Where(w => !Stop.Contains(w)));
            if (ca.Count >= MinContent && cb.Count >= MinContent) s = Math.Max(s, Jac(ca, cb));
            return s;
        }

        /// <summary>The same speaker's new line is a cut-down copy of its own earlier one (all its content words are in it).</summary>
        public static bool Contained(string shortLine, string longLine, int minWords = MinWords)
        {
            var ws = Words(shortLine);
            if (ws.Count < minWords) return false;
            var cs = new HashSet<string>(ws.Where(w => !Stop.Contains(w)));
            var cl = new HashSet<string>(Words(longLine).Where(w => !Stop.Contains(w)));
            return cs.Count >= MinContent && cs.IsSubsetOf(cl);
        }

        /// <summary>The first earlier line the new one repeats, or null. turns is [(speaker id, text)] so far.</summary>
        public static Hit FindRepeat(string text, int speaker, IList<(int, string)> turns, IList<string> ownRecent = null,
            double threshold = Threshold, double crossThreshold = CrossThreshold)
        {
            Hit best = null;
            foreach (var (sp, prev) in turns)
            {
                bool own = sp == speaker;
                double s = Similarity(text, prev);
                if (s >= threshold || (own && Contained(text, prev)))
                {
                    var h = new Hit("turn", prev, Math.Max(s, threshold), own);
                    if (best == null || h.Score > best.Score) best = h;
                }
            }
            if (best != null) return best;
            if (ownRecent != null)
            {
                foreach (var prev in ownRecent)
                {
                    double s = Similarity(text, prev, CrossMinWords);
                    if (s >= crossThreshold) return new Hit("own", prev, s, true);
                }
            }
            return null;
        }

        /// <summary>The line request for a retry: the same request, plus why the last attempt was refused.</summary>
        public static string RetryRequest(string name, Hit hit)
        {
            return "=== REQUEST ===\n"
                + "Write " + name + "'s next line of speech now. One line only.\n"
                + "Your last attempt was refused because " + hit.Reason + ". Say something new: answer what was just said, "
                + "or move the talk on. Do not repeat or rephrase any earlier line.";
        }
    }
}

// Port of agent/rimworldllm_agent/core/gossip.py: what a speaker could pass on, and what the group "says" about each actor.
// Rule-based (no model call). Only public memories are offered or counted; romantic wording with a minor is never offered or kept.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Mind
{
    public static class Gossip
    {
        // Python's "." counts code points: a surrogate pair is one unit, a lone surrogate is not matched (never in valid text).
        private const string Dot = "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\n\\uD800-\\uDFFF])";
        private static readonly Regex Told = new Regex("^" + Dot + "{1,60}? told me:[\\t-\\r\\u001c-\\u001f \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*",
            RegexOptions.CultureInvariant);
        public const string HintPrefix = "Something you could pass on: ";
        public const int HintChars = 110;        // whole hint line, about 30 tokens
        public const int RepChars = 70;

        /// <summary>"Bram told me: X" -> "X" (the retelling's own words are what travels).</summary>
        public static string StripTeller(string text)
        {
            return Sanitize.PyStrip(Told.Replace(text, "", 1));
        }

        internal static string Clip(string text, int n)
        {
            text = string.Join(" ", Sanitize.PySplit(text));
            if (GameData.CpLen(text) <= n) return text;
            string head = GameData.CpPrefix(text, n);
            int sp = head.LastIndexOf(' ');
            string cut = (sp >= 0 ? head.Substring(0, sp) : head).TrimEnd(',', ';', ':', '-', ' ');
            return (cut.Length > 0 ? cut : head) + "...";
        }

        /// <summary>Has this listener already got this item (same event, in the chain, told to them, or near-identical words)?</summary>
        private static bool HeardBy(Mind listener, string lname, Memory m)
        {
            if (m.EventSeq.HasValue && listener.Memories.Any(x => x.EventSeq == m.EventSeq)) return true;
            if (lname.Length > 0 && (m.Chain.Contains(lname) || m.ToldTo.Contains(lname))) return true;
            var words = MemoryOps.Keywords(StripTeller(m.Text));
            foreach (var x in listener.Memories)
            {
                var xw = MemoryOps.Keywords(StripTeller(x.Text));
                if (words.Count > 0 && xw.Count > 0)
                {
                    int inter = words.Count(w => xw.Contains(w));
                    if ((double)inter / (words.Count + xw.Count - inter) >= 0.6) return true;
                }
            }
            return false;
        }

        /// <summary>At most k public memories of the speaker about absent third actors that the listener has not heard.</summary>
        public static List<Memory> PassOnCandidates(Mind speaker, Mind listener, int listenerId, ISet<int> present,
            IEnumerable<int> minors = null, int k = 2)
        {
            var minorSet = new HashSet<int>(minors ?? Enumerable.Empty<int>());
            int sid = speaker.ActorId;
            var outl = new List<(double, long, Memory)>();
            foreach (var m in speaker.Memories)
            {
                if (m.Visibility != "public" || m.SummaryOf != 0 || m.Confidence < 0.3) continue;
                var third = new HashSet<int>(m.People.Where(p => p != sid && p != listenerId && !(m.ToldById.HasValue && p == m.ToldById.Value)));
                if (third.Count == 0 || third.Overlaps(present) || m.People.Contains(listenerId)) continue;
                if (AgeGate.Romantic(m.Text) && new HashSet<int>(third) { sid, listenerId }.Overlaps(minorSet)) continue;
                if (HeardBy(listener, listener.Name, m)) continue;
                outl.Add((m.Importance * m.Confidence, m.Game, m));
            }
            return outl.OrderByDescending(t => t.Item1).ThenByDescending(t => t.Item2)
                .ThenBy(t => t.Item3.Id, StringComparer.Ordinal).Select(t => t.Item3).Take(k).ToList();
        }

        /// <summary>The block D line, or "" when there is nothing to pass on.</summary>
        public static string PassOnHint(Mind speaker, Mind listener, int listenerId, ISet<int> present,
            IEnumerable<int> minors = null, int k = 2)
        {
            var ms = PassOnCandidates(speaker, listener, listenerId, present, minors, k);
            if (ms.Count == 0) return "";
            int each = (HintChars - GameData.CpLen(HintPrefix)) / ms.Count - 2;     // ints here are >= 0 for k <= 2; floor like //
            return HintPrefix + string.Join("; ", ms.Select(m => Clip(GameData.Clean(StripTeller(m.Text), "event"), each)));
        }

        /// <summary>Per subject actor, the group's line about them, from heard/told public memories across all minds.
        /// Insertion order of the result is the order subjects were first seen (as in Python).</summary>
        public static Dictionary<int, string> Reputations(IDictionary<int, Mind> minds, IEnumerable<int> minors = null, double floor = 0.1)
        {
            var minorSet = new HashSet<int>(minors ?? Enumerable.Empty<int>());
            var best = new Dictionary<int, (double, string)>();
            var order = new List<int>();
            var holders = new Dictionary<int, Dictionary<string, HashSet<int>>>();
            foreach (var kv in minds)
            {
                int owner = kv.Key;
                foreach (var m in kv.Value.Memories)
                {
                    if (m.Source == "saw" || m.Visibility != "public" || m.SummaryOf != 0) continue;
                    double w = m.Importance * m.Confidence;
                    if (w < floor) continue;
                    string text = GameData.Clean(StripTeller(m.Text), "event");
                    foreach (int subj in m.People.Where(p => p != owner && !(m.ToldById.HasValue && p == m.ToldById.Value)))
                    {
                        if (AgeGate.Romantic(text) && (minorSet.Contains(subj) || minorSet.Contains(owner))) continue;
                        string key = m.EventSeq.HasValue ? "e" + m.EventSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                         : "t" + text.ToLowerInvariant();
                        Dictionary<string, HashSet<int>> h;
                        if (!holders.TryGetValue(subj, out h)) holders[subj] = h = new Dictionary<string, HashSet<int>>();
                        HashSet<int> s;
                        if (!h.TryGetValue(key, out s)) h[key] = s = new HashSet<int>();
                        s.Add(owner);
                        if (!best.ContainsKey(subj)) order.Add(subj);
                        if (!best.ContainsKey(subj) || w > best[subj].Item1) best[subj] = (w, text);
                    }
                }
            }
            var result = new Dictionary<int, string>();
            foreach (int subj in order)
            {
                int n = holders[subj].Values.Max(v => v.Count);
                result[subj] = Clip(best[subj].Item2, RepChars) + (n > 1 ? " (" + n + " people)" : "");
            }
            return result;
        }
    }
}

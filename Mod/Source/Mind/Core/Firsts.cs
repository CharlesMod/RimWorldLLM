// Port of agent/rimworldllm_agent/core/firsts.py: first-time reactions. The adapter says which events are firsts
// (IFirstsAdapter.FirstOf); this module keeps the bookkeeping in Mind.Firsts as "first:<key>".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The adapter's optional first_of(event) -> (key, phrase) | None.</summary>
    public interface IFirstsAdapter
    {
        (string Key, string Phrase)? FirstOf(JObject ev);
    }

    public static class Firsts
    {
        public const string Prefix = "first:";
        public const int MaxSpeakersPerEvent = 2;     // an everyone-on-the-map cue is voiced by at most this many actors

        public static string KeyOf(string key) { return Prefix + key; }

        public static bool Seen(Mind mind, string key) { return mind.Firsts.Contains(KeyOf(key)); }

        /// <summary>{actor id: phrase} for each witness in minds who has not met this kind of thing before; empty for a non-first event.
        /// An adapter that is not an IFirstsAdapter has no first_of: empty.</summary>
        public static Dictionary<int, string> Marks(IDictionary<int, Mind> minds, JObject ev, object adapter)
        {
            var fa = adapter as IFirstsAdapter;
            var hit = fa == null ? null : fa.FirstOf(ev);
            var outp = new Dictionary<int, string>();
            if (hit == null) return outp;
            var key = hit.Value.Key; var phrase = hit.Value.Phrase;
            foreach (var w in Witnesses(ev))
            {
                Mind m;
                if (minds.TryGetValue(w, out m) && m != null && !Seen(m, key)) outp[w] = phrase;
            }
            return outp;
        }

        /// <summary>Write the firsts into the minds. Returns the ids changed.</summary>
        public static List<int> Record(IDictionary<int, Mind> minds, JObject ev, IFirstsAdapter adapter, IDictionary<int, string> fm)
        {
            var hit = adapter.FirstOf(ev);
            var changed = new List<int>();
            if (hit == null) return changed;
            foreach (var aid in fm.Keys)
            {
                Mind m;
                if (minds.TryGetValue(aid, out m) && m != null && !Seen(m, hit.Value.Key))
                {
                    m.Firsts.Add(KeyOf(hit.Value.Key));
                    changed.Add(aid);
                }
            }
            return changed;
        }

        /// <summary>Which marked actors voice a cue: a stable shuffle by event and actor. <paramref name="seq"/> is formatted like
        /// Python's str() (int, str, float, bool, None).</summary>
        public static List<int> PickSpeakers(IDictionary<int, string> fm, object seq, int limit = MaxSpeakersPerEvent)
        {
            string s = PyStr(seq);
            return fm.Keys.Select(a => new { a, h = Sha1Hex(s + ":" + a.ToString(CultureInfo.InvariantCulture)) })
                .OrderBy(x => x.h, StringComparer.Ordinal).Take(Math.Max(0, limit)).Select(x => x.a).ToList();
        }

        private static IEnumerable<int> Witnesses(JObject ev)
        {
            var w = ev["witnesses"] as JArray;
            if (w == null) yield break;
            foreach (var t in w) yield return (int)t;
        }

        private static string PyStr(object o)
        {
            if (o == null) return "None";
            if (o is JToken)
            {
                var t = (JToken)o;
                switch (t.Type)
                {
                    case JTokenType.Null: return "None";
                    case JTokenType.Boolean: return (bool)t ? "True" : "False";
                    case JTokenType.Integer: return ((JValue)t).ToString(CultureInfo.InvariantCulture);
                    case JTokenType.Float: return PyFmt.Repr((double)t);
                    case JTokenType.String: return (string)t;
                }
                return t.ToString();
            }
            if (o is bool) return (bool)o ? "True" : "False";
            if (o is double) return PyFmt.Repr((double)o);
            if (o is float) return PyFmt.Repr((float)o);
            if (o is IFormattable) return ((IFormattable)o).ToString(null, CultureInfo.InvariantCulture);
            return o.ToString();
        }

        private static string Sha1Hex(string s)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>First-time lines per actor per game day.</summary>
    public class DayCap
    {
        public Dictionary<(int, int), int> N = new Dictionary<(int, int), int>();

        public bool Allow(int aid, int day, int cap)
        {
            int v;
            return (N.TryGetValue((aid, day), out v) ? v : 0) < cap;
        }

        public void Spend(int aid, int day)
        {
            int v;
            N[(aid, day)] = (N.TryGetValue((aid, day), out v) ? v : 0) + 1;
            foreach (var k in N.Keys.Where(k => k.Item2 < day - 1).ToList()) N.Remove(k);
        }
    }
}

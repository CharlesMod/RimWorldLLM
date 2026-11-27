// Small shared helpers of the "jobs" modules (art, lore, recap, dreams, steward): Python dict.get / int() / "is False" on decoded
// JSON, the "split at the REQUEST marker" step every creative prompt does, and the one note-appending step of write().
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    internal static class JobsJson
    {
        /// <summary>int(x) for a JSON value (TypeError / ValueError as Python raises them).</summary>
        public static long PyInt(JToken t)
        {
            if (t == null) throw new PyTypeError("int() argument must be a string or a real number, not 'NoneType'");
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.Float:
                    {
                        double d = (double)t;
                        if (double.IsNaN(d) || double.IsInfinity(d)) throw new PyValueError("cannot convert float to integer");
                        return (long)System.Math.Truncate(d);
                    }
                case JTokenType.String:
                    {
                        string s = PyStr.Strip((string)t);
                        if (long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long v)) return v;
                        throw new PyValueError("invalid literal for int() with base 10: " + PyJson.StrRepr((string)t));
                    }
                default: throw new PyTypeError("int() argument must be a string or a real number");
            }
        }

        /// <summary>Python's a // b for positive b.</summary>
        public static long FloorDiv(long a, long b)
        {
            long q = a / b;
            return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
        }

        /// <summary>gamedata._cut: cut at a word boundary under cap code points, with an ellipsis.</summary>
        public static string Cut(string t, int cap)
        {
            if (PyStr.Len(t) <= cap) return t;
            string head = PyStr.Head(t, cap);
            int sp = head.LastIndexOf(' ');
            string cut = sp >= 0 ? head.Substring(0, sp) : head;
            if (cut.Length == 0) cut = head;
            return cut.TrimEnd(' ', ',', ';', ':', '-') + "\u2026";
        }

        /// <summary>str.splitlines(): breaks at \n, \r\n, \r, \v, \f, \x1c-\x1e, \x85, \u2028, \u2029; no trailing empty piece.</summary>
        public static List<string> SplitLines(string s)
        {
            var o = new List<string>();
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '\r' || c == '\v' || c == '\f' || (c >= '\x1c' && c <= '\x1e') || c == '\x85' || c == '\u2028' || c == '\u2029')
                {
                    o.Add(s.Substring(start, i - start));
                    if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                    start = i + 1;
                }
            }
            if (start < s.Length) o.Add(s.Substring(start));
            return o;
        }

        public const string RequestMarker = "=== REQUEST ===";

        /// <summary>o.get(key) (null when missing or o is null; a JSON null stays a JValue null token).</summary>
        public static JToken Get(JToken o, string key)
        {
            var jo = o as JObject;
            return jo != null && jo.TryGetValue(key, out JToken v) ? v : null;
        }

        /// <summary>str(o.get(key, dflt)): "None" for a JSON null, as Python prints it.</summary>
        public static string Str(JToken o, string key, string dflt = "")
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(key, out JToken v)) return dflt;
            return PyJson.Str(v);
        }

        /// <summary>str(o.get(key) or ""): the empty string for a missing, null or otherwise falsy value.</summary>
        public static string StrOrEmpty(JToken o, string key)
        {
            JToken v = Get(o, key);
            return v != null && PyJson.Truthy(v) ? PyJson.Str(v) : "";
        }

        /// <summary>x is False (a JSON false, not a missing key or null).</summary>
        public static bool IsFalse(JToken t) { return t != null && t.Type == JTokenType.Boolean && !(bool)t; }

        /// <summary>x is True.</summary>
        public static bool IsTrue(JToken t) { return t != null && t.Type == JTokenType.Boolean && (bool)t; }

        /// <summary>d.bridge_cfg.get("events", {}).get(name) is not False.</summary>
        public static bool EventNotOff(JObject bridgeCfg, string name)
        {
            JToken ev = Get(bridgeCfg, "events");
            return !IsFalse(Get(ev, name));
        }

        /// <summary>text.split("=== REQUEST ===")[0]</summary>
        public static string BeforeRequest(string text)
        {
            int i = text.IndexOf(RequestMarker, System.StringComparison.Ordinal);
            return i < 0 ? text : text.Substring(0, i);
        }

        /// <summary>The request with a creative note (a plan, phrases to avoid) added at its end; unchanged for an empty note.</summary>
        public static List<LlmMessage> WithNote(IList<LlmMessage> msgs, string note)
        {
            var o = new List<LlmMessage>(msgs);
            if (string.IsNullOrEmpty(note)) return o;
            var last = o[o.Count - 1];
            o[o.Count - 1] = new LlmMessage(last.Role, last.Text.TrimEnd('\n') + "\n" + note);
            return o;
        }

        /// <summary>The plan call's messages: the request section replaced by the plan ask.</summary>
        public static List<LlmMessage> PlanMessages(IList<LlmMessage> msgs)
        {
            var o = new List<LlmMessage>(msgs);
            var last = o[o.Count - 1];
            o[o.Count - 1] = new LlmMessage(last.Role, BeforeRequest(last.Text) + RequestMarker + "\n" + Grounding.PlanAsk);
            return o;
        }

        public static HashSet<string> Union(IEnumerable<ISet<string>> sets)
        {
            var o = new HashSet<string>();
            foreach (var s in sets) o.UnionWith(s);
            return o;
        }

        private static readonly Regex Words = new Regex("[A-Za-z']+", RegexOptions.CultureInvariant);

        /// <summary>set(re.findall(r"[A-Za-z']+", text))</summary>
        public static HashSet<string> WordSet(string text)
        {
            var o = new HashSet<string>();
            foreach (Match m in Words.Matches(text)) o.Add(m.Value);
            return o;
        }
    }
}

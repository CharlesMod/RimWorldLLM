// Port of games/rimworld/steward_rules.py: the playbook rules (strategy/rules.json, embedded) evaluated safely.
//
// Each rule's condition goes through StewardExpr (a whitelist parser, no eval). A field that is missing (or None where a
// number is needed) makes the rule skip: three-valued, a False operand still decides an and, a True operand an or. "true"
// conditions are always-on guidance that belongs to the static prompt, so they are never in the per-cycle list.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>One shipped playbook rule.</summary>
    public sealed class PlaybookRule
    {
        public string Id;
        public long Priority;
        public string Action;
        public CompiledCondition Run;
        public bool AlwaysOn;

        public PlaybookRule(string id, long priority, string action, CompiledCondition run, bool alwaysOn)
        { Id = id; Priority = priority; Action = action; Run = run; AlwaysOn = alwaysOn; }
    }

    /// <summary>The steward's embedded data files (byte-identical copies of the Python package's strategy/rules.json,
    /// strategy/steward_strategy.md and steward_system.txt; see the EmbeddedResource items in both csproj files).</summary>
    public static class StewardResources
    {
        public const string RulesName = "RimWorldLLM.Mind.rules.json";
        public const string StrategyName = "RimWorldLLM.Mind.steward_strategy.md";
        public const string SystemName = "RimWorldLLM.Mind.steward_system.txt";

        /// <summary>The file's text (UTF-8, a BOM dropped), or throws when it is not embedded.</summary>
        public static string Text(string logicalName)
        {
            var asm = typeof(StewardResources).Assembly;
            using (var s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null) throw new System.IO.FileNotFoundException("embedded resource missing: " + logicalName);
                using (var r = new System.IO.StreamReader(s, new UTF8Encoding(false), true)) return r.ReadToEnd();
            }
        }
    }

    public static class StewardRules
    {
        public const string LoggerName = "rimworldllm.steward.rules";
        public const int MaxRules = 8;
        public const int SectionCap = 1500;
        public const string Heading = "=== PLAYBOOK ALERTS ===";

        // ---- compile / load --------------------------------------------------------------------
        /// <summary>A condition's compiled form (facts -> true | false | null). RuleError if it is not whitelisted.</summary>
        public static CompiledCondition CompileCondition(string cond) => StewardExpr.Compile(cond);

        private static List<PlaybookRule> _loaded;
        private static readonly object Gate = new object();

        /// <summary>The shipped rules, loaded once; bad ones are dropped with a log line.</summary>
        public static IReadOnlyList<PlaybookRule> LoadRules()
        {
            lock (Gate)
            {
                if (_loaded == null) _loaded = ParseRules(() => StewardResources.Text(StewardResources.RulesName));
                return _loaded;
            }
        }

        /// <summary>Forget the cached rules (tests).</summary>
        public static void ResetCache() { lock (Gate) _loaded = null; }

        /// <summary>Rules from a rules.json text (the loader behind LoadRules, and what tests feed).</summary>
        public static List<PlaybookRule> ParseRules(Func<string> read)
        {
            JToken data;
            try { data = PyJson.Loads(read()); }
            catch (Exception e)   // Python: except (OSError, ValueError); here any read or JSON failure
            {
                LlmLog.Warning(LoggerName, "playbook rules not loaded: " + e.Message);
                return new List<PlaybookRule>();
            }
            var outl = new List<PlaybookRule>();
            JToken rules = (data as JObject)?["rules"];
            if (rules == null || !PyJson.Truthy(rules)) return outl;
            foreach (var r in (IEnumerable<JToken>)rules)
            {
                try
                {
                    var ro = r as JObject;
                    if (ro == null) throw new PyTypeError("rule is not an object");
                    JToken ct = Req(ro, "condition");
                    var run = CompileCondition(ct.Type == JTokenType.String ? (string)ct : null);
                    string id = PyJson.Str(Req(ro, "id"));
                    long pri = PyInt(Req(ro, "priority"));
                    string action = PyStr.Strip(PyJson.Str(Req(ro, "action")));
                    outl.Add(new PlaybookRule(id, pri, action, run, run.AlwaysOn));
                }
                catch (Exception e) when (e is RuleError || e is PyKeyError || e is PyTypeError || e is PyValueError)
                {
                    string rid = r is JObject jo ? (jo.TryGetValue("id", out JToken idt) ? PyJson.Str(idt) : "None") : "?";
                    LlmLog.Warning(LoggerName, "playbook rule " + rid + " dropped: " + e.Message);
                }
            }
            return outl;
        }

        private static JToken Req(JObject o, string key)
        {
            if (!o.TryGetValue(key, out JToken t)) throw new PyKeyError(key);
            return t;
        }

        /// <summary>int(x) for a JSON value: ints, floats (truncated), numeric strings, bools; anything else raises.</summary>
        private static long PyInt(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.Float:
                    {
                        double d = (double)t;
                        if (double.IsNaN(d) || double.IsInfinity(d)) throw new PyValueError("cannot convert float to integer");
                        return (long)Math.Truncate(d);
                    }
                case JTokenType.String:
                    {
                        string s = PyStr.Strip((string)t);
                        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return v;
                        throw new PyValueError("invalid literal for int() with base 10: " + PyJson.StrRepr((string)t));
                    }
                default: throw new PyTypeError("int() argument must be a string or a real number");
            }
        }

        // ---- research names ---------------------------------------------------------------------
        private static readonly HashSet<string> WarnedResearch = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Names the game does not define, judged by facts["research_known"] (every ResearchProjectDef defName, sent
        /// by the mod). Without that list nothing can be judged and nothing is unknown. Each miss is logged once.</summary>
        public static HashSet<string> UnknownResearch(IEnumerable<string> names, IDictionary<string, object> facts)
        {
            var none = new HashSet<string>(StringComparer.Ordinal);
            if (names == null || facts == null || !facts.TryGetValue("research_known", out object kn)) return none;
            if (!(StewardExpr.Norm(kn) is List<object> known)) return none;
            var ns = names.ToList();
            if (ns.Count == 0) return none;
            var knownSet = new HashSet<string>(known.OfType<string>(), StringComparer.Ordinal);
            var miss = new HashSet<string>(ns.Where(n => !knownSet.Contains(n)), StringComparer.Ordinal);
            List<string> fresh;
            lock (WarnedResearch) fresh = miss.Where(n => WarnedResearch.Add(n)).ToList();
            foreach (var n in fresh)
                LlmLog.Warning(LoggerName, "playbook rules name research " + PyJson.StrRepr(n) + ", which this game does not define; those rules stay off");
            return miss;
        }

        /// <summary>Forget which unknown research names were already warned about (tests).</summary>
        public static void ResetWarned() { lock (WarnedResearch) WarnedResearch.Clear(); }

        // ---- facts ------------------------------------------------------------------------------
        private static JToken G(JToken o, string k)
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(k, out JToken t) || t.Type == JTokenType.Null) return null;
            return t;
        }

        private static bool IsNumTok(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        /// <summary>str(o.get(k, dflt)): a missing key gives dflt, a JSON null gives "None".</summary>
        private static string GetStr(JToken o, string k, string dflt)
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(k, out JToken t)) return dflt;
            return PyJson.Str(t);
        }

        private static string Low(string s) => s.ToLowerInvariant();

        private static object NumOf(JToken t)
        {
            if (t.Type == JTokenType.Float) return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
            object o = ((JValue)t).Value;
            if (o is BigInteger bi) return bi;
            return Convert.ToInt64(o, CultureInfo.InvariantCulture);
        }

        private static BigInteger IntOf(JToken t)
        {
            if (t.Type == JTokenType.Float)
            {
                double d = (double)t;
                return double.IsNaN(d) || double.IsInfinity(d) ? BigInteger.Zero : new BigInteger(Math.Truncate(d));
            }
            object o = ((JValue)t).Value;
            return o is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(o, CultureInfo.InvariantCulture));
        }

        private static object Narrow(BigInteger b) => b >= long.MinValue && b <= long.MaxValue ? (object)(long)b : b;

        private static double Dbl(object n) => n is double d ? d : n is BigInteger b ? (double)b : Convert.ToDouble(n, CultureInfo.InvariantCulture);
        private static bool IsInt(object n) => !(n is double);
        private static BigInteger Big(object n) => n is BigInteger b ? b : new BigInteger(Convert.ToInt64(n, CultureInfo.InvariantCulture));

        /// <summary>Python sum() of ints and floats: exact for ints, Neumaier-compensated once a float shows up (3.12+).</summary>
        private static object PySum(List<object> xs)
        {
            BigInteger iacc = BigInteger.Zero;
            int k = 0;
            while (k < xs.Count && IsInt(xs[k])) { iacc += Big(xs[k]); k++; }
            if (k == xs.Count) return Narrow(iacc);
            double f = (double)iacc, c = 0.0;
            for (; k < xs.Count; k++)
            {
                double x = Dbl(xs[k]);
                double t = f + x;
                if (Math.Abs(f) >= Math.Abs(x)) c += (f - t) + x; else c += (x - t) + f;
                f = t;
            }
            if (c != 0.0 && !double.IsNaN(c) && !double.IsInfinity(c)) f += c;
            return f;
        }

        private static object Sub(object a, object b)
            => IsInt(a) && IsInt(b) ? Narrow(Big(a) - Big(b)) : (object)(Dbl(a) - Dbl(b));

        private static BigInteger? Resource(IEnumerable<JToken> res, string label)
        {
            BigInteger tot = BigInteger.Zero;
            bool seen = false;
            foreach (var r in res)
            {
                if (r is JObject && Low(PyStr.Strip(GetStr(r, "label", ""))) == label && (IsNumTok(G(r, "count")) || IsNumTok(G(r, "loose"))))
                {
                    // stored plus loose (forbidden piles are the player's call and stay out)
                    tot += (IsNumTok(G(r, "count")) ? IntOf(G(r, "count")) : BigInteger.Zero) + (IsNumTok(G(r, "loose")) ? IntOf(G(r, "loose")) : BigInteger.Zero);
                    seen = true;
                }
            }
            return seen ? tot : (BigInteger?)null;
        }

        private static IEnumerable<JObject> Dicts(JToken t) => (t as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

        /// <summary>digest["facts"] (the mod's block) first, then gaps filled from the rest of the digest. Never invents a value:
        /// what cannot be read stays absent. (digest date.day is the day of the quadrum, so "day" is never derived.)
        /// Values are null, bool, long (BigInteger when huge), double, string, List&lt;object&gt; or Dictionary&lt;string, object&gt;.</summary>
        public static Dictionary<string, object> DeriveFacts(JObject digest)
        {
            var f = new Dictionary<string, object>(StringComparer.Ordinal);
            if (digest == null) return f;
            if (G(digest, "facts") is JObject given)
                foreach (var p in given.Properties()) f[p.Name] = NormOut(StewardExpr.FromToken(p.Value));

            void Fill(string k, object v) { if (v != null && !f.ContainsKey(k)) f[k] = v; }

            JToken fd = G(digest, "food_days");
            Fill("food_days", IsNumTok(fd) ? NumOf(fd) : null);
            var pawns = Dicts(G(digest, "pawns")).ToList();
            var cols = pawns.Where(p => Low(GetStr(p, "role", "colonist")) == "colonist").ToList();
            if (pawns.Count > 0)
            {
                Fill("colonists", (long)cols.Count);
                var moods = cols.Where(p => IsNumTok(G(p, "mood"))).Select(p => NumOf(G(p, "mood"))).ToList();
                if (moods.Count > 0)
                {
                    Fill("mood_avg", PyFmt.Round(Dbl(PySum(moods)) / moods.Count, 1));
                    object mn = moods[0];
                    foreach (var m in moods) if (StewardExpr.CompareNum(StewardExpr.Norm(m), StewardExpr.Norm(mn)) < 0) mn = m;
                    Fill("mood_min", mn);
                }
                Fill("colonists_downed", (long)cols.Count(p => PyJson.Truthy(G(p, "downed"))));
            }
            if (G(digest, "beds") is JArray)
                Fill("beds_free", (long)Dicts(G(digest, "beds")).Count(b => !PyJson.Truthy(G(b, "owner"))));
            if (G(digest, "resources") is JArray resArr)
            {
                foreach (var k in new[] { "steel", "wood", "components", "silver" })
                {
                    var r = Resource(resArr, k);
                    Fill(k, r.HasValue ? Narrow(r.Value) : null);
                }
                var med = Dicts(resArr).Where(r => Low(GetStr(r, "category", "")) == "medicine" && IsNumTok(G(r, "count"))).ToList();
                if (med.Count > 0)
                {
                    Fill("medicine_total", Narrow(med.Aggregate(BigInteger.Zero, (a, r) => a + IntOf(G(r, "count")))));
                    Fill("medicine_industrial", Narrow(med.Where(r => !Low(GetStr(r, "label", "")).Contains("herbal"))
                        .Aggregate(BigInteger.Zero, (a, r) => a + IntOf(G(r, "count")))));
                }
            }
            if (G(digest, "research") is JObject rs && rs.ContainsKey("current"))
            {
                JToken cur = G(rs, "current");
                object val = null;
                if (cur is JObject)
                {
                    JToken a = G(cur, "def");
                    val = PyJson.Truthy(a) ? StewardExpr.FromToken(a) : StewardExpr.FromToken(G(cur, "label"));
                    val = NormOut(val);
                }
                Fill("research_current", val);
                if (cur == null && !f.ContainsKey("research_current")) f["research_current"] = null;
            }
            if (G(digest, "threats") is JArray th)
            {
                BigInteger n = BigInteger.Zero;
                foreach (var t in Dicts(th))
                {
                    bool hostile = t.TryGetValue("hostile", out JToken h) ? PyJson.Truthy(h) : true;
                    if (hostile && IsNumTok(G(t, "count"))) n += IntOf(G(t, "count"));
                }
                Fill("enemies_count", Narrow(n));
                Fill("raid_active", n > 0);
            }
            if (G(digest, "alerts") is JArray)
            {
                long fires = Dicts(G(digest, "alerts")).Count(a => Low(GetStr(a, "alert", "")).Contains("fire") || Low(GetStr(a, "label", "")).StartsWith("fire", StringComparison.Ordinal));
                Fill("fires", fires);
            }
            JToken dt = G(digest, "date");
            string season = Low(PyStr.Strip(GetStr(dt, "season", "")));
            if (season == "autumn") season = "fall";
            if (season == "spring" || season == "summer" || season == "fall" || season == "winter") Fill("season", season);
            if (IsNumTok(G(digest, "temperature"))) Fill("temp_c", NumOf(G(digest, "temperature")));
            if (G(digest, "power") is JObject pw)
            {
                if (IsNumTok(G(pw, "produced")) && IsNumTok(G(pw, "used"))) Fill("power_net_w", Sub(NumOf(G(pw, "produced")), NumOf(G(pw, "used"))));
                if (IsNumTok(G(pw, "stored"))) Fill("battery_wd", NumOf(G(pw, "stored")));
            }
            return f;
        }

        // fact values handed back to callers: ints as long where they fit
        private static object NormOut(object v)
        {
            switch (v)
            {
                case BigInteger b: return Narrow(b);
                case List<object> l: return l.Select(NormOut).ToList();
                case Dictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => NormOut(kv.Value));
                default: return v;
            }
        }

        // ---- selection and rendering -------------------------------------------------------------
        /// <summary>(priority, id, action) of the rules that fire on these facts, always-on ones left out, sorted by priority then id.
        /// rules defaults to the shipped ones.</summary>
        public static List<(long Priority, string Id, string Action)> FiredRules(IDictionary<string, object> facts, IEnumerable<PlaybookRule> rules = null)
        {
            var outl = new List<(long, string, string)>();
            foreach (var r in rules ?? LoadRules())
            {
                if (r.AlwaysOn) continue;
                if (UnknownResearch(r.Run?.ResearchNames, facts).Count > 0) continue;   // a research name the game lacks: counts as false
                if (r.Run.Run(facts) == true) outl.Add((r.Priority, r.Id, r.Action));
            }
            // list.sort is stable; keys compare priority, then id by code point
            return outl.Select((x, i) => (x, i))
                .OrderBy(t => t.x.Item1).ThenBy(t => t.x.Item2, Comparer<string>.Create(PyJson.CompareCodePoints)).ThenBy(t => t.i)
                .Select(t => (t.x.Item1, t.x.Item2, t.x.Item3)).ToList();
        }

        /// <summary>All priority-1 rules, then the rest in order, at most limit in all (a priority-1 rule is never dropped).</summary>
        public static List<(long Priority, string Id, string Action)> Select(IList<(long Priority, string Id, string Action)> fired, int limit = MaxRules)
        {
            var p1 = fired.Where(r => r.Priority <= 1).ToList();
            var rest = fired.Where(r => r.Priority > 1).ToList();
            p1.AddRange(rest.Take(Math.Max(0, limit - p1.Count)));
            return p1;
        }

        private static string Trim(string text, int n)
        {
            if (PyStr.Len(text) <= n) return text;
            string head = PyStr.Head(text, Math.Max(n - 1, 1));
            int sp = head.LastIndexOf(' ');
            string cut = sp >= 0 ? head.Substring(0, sp) : head;
            return cut.TrimEnd(' ', ',', ';', ':', '.') + "…";
        }

        /// <summary>The "=== PLAYBOOK ALERTS ===" block for the user turn, or "" when no rule fires. At most cap characters:
        /// lower-priority lines go first, then priority-1 actions are shortened. rules defaults to the shipped ones.</summary>
        public static string AlertsSection(JObject digest, int cap = SectionCap, IEnumerable<PlaybookRule> rules = null)
        {
            var sel = Select(FiredRules(DeriveFacts(digest), rules));
            if (sel.Count == 0) return "";
            string head = Heading + "\n";

            string Line((long Priority, string Id, string Action) r, int? n = null)
                => "[P" + r.Priority.ToString(CultureInfo.InvariantCulture) + " " + r.Id + "] " + (n.HasValue ? Trim(r.Action, n.Value) : r.Action);
            int Total() => sel.Sum(r => PyStr.Len(Line(r)) + 1);

            while (PyStr.Len(head) + Total() > cap && sel.Any(r => r.Priority > 1))
            {
                for (int i = sel.Count - 1; i >= 0; i--)
                    if (sel[i].Priority > 1) { sel.RemoveAt(i); break; }
            }
            int budget = cap - PyStr.Len(head);
            var body = sel.Select(r => Line(r)).ToList();
            if (body.Sum(b => PyStr.Len(b) + 1) > budget)
            {
                int per = (int)Math.Max(40L, PyFmt.FloorDiv(budget, sel.Count) - 1 - 20);
                body = sel.Select(r => Line(r, per)).ToList();
            }
            return head + string.Join("\n", body) + "\n";
        }
    }
}

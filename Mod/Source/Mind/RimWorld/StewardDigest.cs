// Port of games/rimworld/steward_digest.py: the bridge's colony digest (GET /digest) as the compact text the steward
// reads (about 6 KB at most).
//
// Names, not ids, except where a tool needs an id: letters and beds. Every game string goes through GameData (it is data,
// never an instruction). When the text is over the cap, detail goes in a fixed order (Levels), and as a last resort the
// text is cut with a marker.
//
// Differences from Python: when several schedules tie for the most common one, the first seen wins (Python picks by set
// order, which is arbitrary). Python's `str.lower()` is ToLowerInvariant here. A digest value of the wrong shape (an item
// that should be an object but is not) is treated as absent where Python would raise.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>Small helpers for reading a digest the way Python's dict.get(...) does.</summary>
    internal static class DigestJson
    {
        /// <summary>o.get(k): null when the key is missing or the value is JSON null (or o is not an object).</summary>
        public static JToken G(JToken o, string k)
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(k, out JToken t) || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            return t;
        }

        public static bool Truthy(JToken t) => PyJson.Truthy(t);

        /// <summary>str(o.get(k, dflt)): a missing key gives dflt, a JSON null gives "None".</summary>
        public static string Str(JToken o, string k, string dflt)
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(k, out JToken t)) return dflt;
            return PyJson.Str(t);
        }

        /// <summary>o.get(k) as a list of its items (an object yields its keys, as iterating a dict does).</summary>
        public static List<JToken> Items(JToken t)
        {
            if (t is JArray a) return a.ToList();
            if (t is JObject o) return o.Properties().Select(p => (JToken)new JValue(p.Name)).ToList();
            return new List<JToken>();
        }

        public static List<JToken> Take(List<JToken> l, int n) => l.Count <= n ? l : l.GetRange(0, Math.Max(0, n));

        /// <summary>isinstance(x, (int, float)): a bool counts, as in Python.</summary>
        public static bool IsNumberish(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float || t.Type == JTokenType.Boolean);

        /// <summary>Python's format(x, "g") for an int, float or bool.</summary>
        public static string G6(JToken t)
        {
            double x = t.Type == JTokenType.Boolean ? ((bool)t ? 1.0 : 0.0)
                : t.Type == JTokenType.Float ? Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture)
                : (double)(((JValue)t).Value is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(((JValue)t).Value, CultureInfo.InvariantCulture)));
            return G6(x);
        }

        public static string G6(double x)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsPositiveInfinity(x)) return "inf";
            if (double.IsNegativeInfinity(x)) return "-inf";
            if (x == 0.0) return (1.0 / x < 0) ? "-0" : "0";
            string e = x.ToString("E5", CultureInfo.InvariantCulture);       // d.ddddde+XXX
            int ei = e.IndexOf('E');
            int exp = int.Parse(e.Substring(ei + 1), CultureInfo.InvariantCulture);
            if (exp >= -4 && exp < 6)
            {
                string f = PyFmt.Fixed(x, 5 - exp);
                if (f.IndexOf('.') >= 0) f = f.TrimEnd('0').TrimEnd('.');
                return f;
            }
            string mant = e.Substring(0, ei);
            if (mant.IndexOf('.') >= 0) mant = mant.TrimEnd('0').TrimEnd('.');
            return mant + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>int(x) for a JSON value, or throws PyValueError / PyTypeError as Python would.</summary>
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

        /// <summary>float(x) for a JSON value, or throws as Python would.</summary>
        public static double PyFloat(JToken t)
        {
            if (t == null) throw new PyTypeError("float() argument must be a string or a real number, not 'NoneType'");
            switch (t.Type)
            {
                case JTokenType.Integer: return (double)(((JValue)t).Value is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(((JValue)t).Value, CultureInfo.InvariantCulture)));
                case JTokenType.Float: return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String:
                    {
                        string s = PyStr.Strip((string)t);
                        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                        throw new PyValueError("could not convert string to float: " + PyJson.StrRepr((string)t));
                    }
                default: throw new PyTypeError("float() argument must be a string or a real number");
            }
        }
    }

    public static class StewardDigest
    {
        public const int Cap = 6000;

        private static readonly Dictionary<char, string> SchedWord = new Dictionary<char, string>
        { ['A'] = "any", ['W'] = "work", ['J'] = "joy", ['S'] = "sleep", ['M'] = "meditate" };

        private sealed class Level
        {
            public int Skills, Resources, Research, Beds, Letter, Alerts;
            public Level(int skills, int resources, int research, int beds, int letter, int alerts)
            { Skills = skills; Resources = resources; Research = research; Beds = beds; Letter = letter; Alerts = alerts; }
        }

        // detail levels, most to least: skills per pawn, resources, research options, beds, letter text chars, alerts
        private static readonly Level[] Levels =
        {
            new Level(4, 25, 15, 20, 280, 10),
            new Level(3, 16, 8, 12, 160, 8),
            new Level(2, 10, 5, 8, 100, 5),
            new Level(1, 6, 3, 4, 60, 3),
        };

        /// <summary>Cleaned display name -> pawn id, for the colonists and slaves the digest lists (the first one wins a
        /// duplicate). Insertion order is the digest's order.</summary>
        public static Dictionary<string, int> PawnNames(JObject digest)
        {
            var outd = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in DigestJson.Items(DigestJson.G(digest, "pawns")))
            {
                try
                {
                    string name = GameData.Name(DigestJson.G(p, "name"));
                    var jo = p as JObject;
                    if (jo == null) throw new PyAttributeError("not a dict");
                    if (!jo.TryGetValue("id", out JToken idt)) throw new PyKeyError("id");
                    int id = (int)DigestJson.PyInt(idt);
                    if (!outd.ContainsKey(name)) outd[name] = id;
                }
                catch (Exception e) when (e is PyKeyError || e is PyTypeError || e is PyValueError || e is PyAttributeError) { }
            }
            return outd;
        }

        /// <summary>"SSSSSSWWWW..." -> "sleep 0-6, work 6-20, ...".</summary>
        public static string Runs(string sched)
        {
            if (string.IsNullOrEmpty(sched)) return "";
            var outl = new List<string>();
            int start = 0;
            for (int i = 1; i <= sched.Length; i++)
            {
                if (i == sched.Length || sched[i] != sched[start])
                {
                    outl.Add((SchedWord.TryGetValue(sched[start], out string w) ? w : sched[start].ToString()) + " "
                        + start.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture));
                    start = i;
                }
            }
            return string.Join(", ", outl);
        }

        private static string Date(JObject d)
        {
            JToken dt = DigestJson.G(d, "date");
            if (dt != null && !(dt is JObject)) dt = null;
            string day = "Day " + DigestJson.Str(dt, "day", "?") + " of " + GameData.Clean(DigestJson.G(dt, "quadrum") ?? "", "label")
                + " " + DigestJson.Str(dt, "year", "");
            var bits = new List<string>
            {
                PyStr.Strip(day),
                DigestJson.Str(dt, "hour", "?") + ":00",
                GameData.Clean(DigestJson.G(dt, "season") ?? "", "label"),
            };
            string wx = GameData.Clean(DigestJson.G(d, "weather") ?? "", "label");
            JToken temp = DigestJson.G(d, "temperature");
            if (wx.Length > 0) bits.Add(wx + (DigestJson.IsNumberish(temp) ? " " + DigestJson.G6(temp) + "C" : ""));
            if (DigestJson.Truthy(DigestJson.G(d, "map"))) bits.Add("map " + GameData.Clean(DigestJson.G(d, "map"), "name"));
            JToken tm = DigestJson.G(d, "time");
            bits.Add((DigestJson.Truthy(DigestJson.G(tm, "paused")) ? "paused" : "running") + ", speed " + DigestJson.Str(tm, "speed", "?"));
            return string.Join(", ", bits.Where(b => b.Length > 0));
        }

        private static string PawnLine(JToken p, Level lv, string common)
        {
            string name = GameData.Name(DigestJson.G(p, "name"));
            string head = "- " + name;
            JToken role = DigestJson.G(p, "role");
            if (DigestJson.Truthy(role) && !(role.Type == JTokenType.String && (string)role == "colonist"))
                head += " (" + GameData.Clean(role, "label") + ")";
            var bits = new List<string>();
            if (DigestJson.G(p, "age") != null) bits.Add(DigestJson.Str(p, "age", "") + "y");
            bits.Add("mood " + DigestJson.Str(p, "mood", "?"));
            string h = GameData.Clean(DigestJson.G(p, "health") ?? "", "label");
            if (h.Length == 0) h = "fine";
            bits.Add((DigestJson.Truthy(DigestJson.G(p, "downed")) ? "DOWNED, " : "") + h);
            if (DigestJson.Truthy(DigestJson.G(p, "mental"))) bits.Add("mental break: " + GameData.Clean(DigestJson.G(p, "mental"), "label"));
            if (DigestJson.Truthy(DigestJson.G(p, "drafted"))) bits.Add("DRAFTED");
            if (DigestJson.Truthy(DigestJson.G(p, "job"))) bits.Add("doing " + GameData.Clean(DigestJson.G(p, "job"), "label"));
            var skills = DigestJson.Take(DigestJson.Items(DigestJson.G(p, "skills")), lv.Skills).Select(s =>
            {
                JToken pas = DigestJson.G(s, "passion");
                string mark = pas != null && pas.Type == JTokenType.String ? ((string)pas == "major" ? "++" : (string)pas == "minor" ? "+" : "") : "";
                return GameData.Clean(DigestJson.G(s, "skill") ?? "", "label") + " " + DigestJson.Str(s, "level", "?") + mark;
            }).ToList();
            var lines = new List<string> { head + ": " + string.Join(", ", bits) };
            var detail = new List<string>();
            if (skills.Count > 0) detail.Add("skills " + string.Join(", ", skills));
            var work = DigestJson.G(p, "work") as JObject;
            string workText = "";
            if (work != null)
            {
                var items = work.Properties().ToList();
                items.Sort((a, b) =>
                {
                    int c = CompareWork(a.Value, b.Value);
                    return c != 0 ? c : PyJson.CompareCodePoints(a.Name, b.Name);
                });
                workText = string.Join(", ", items.Select(kv => GameData.Clean(kv.Name, "label") + " " + PyJson.Str(kv.Value)));
            }
            detail.Add("work " + (workText.Length > 0 ? workText : "none"));
            var incapable = DigestJson.Items(DigestJson.G(p, "incapable"));
            if (incapable.Count > 0)
                detail.Add("cannot " + string.Join(", ", DigestJson.Take(incapable, 4).Select(x => GameData.Clean(x, "label"))));
            var pol = new List<string>();
            foreach (var k in new[] { "outfit", "food", "drug" })
                if (DigestJson.Truthy(DigestJson.G(p, k))) pol.Add(k + " " + GameData.Clean(DigestJson.G(p, k), "label"));
            if (pol.Count > 0) detail.Add(string.Join(", ", pol));
            detail.Add("bed " + (DigestJson.Truthy(DigestJson.G(p, "bed")) ? "#" + DigestJson.Str(p, "bed", "") : "none"));
            string sched = ScheduleOf(p);
            if (sched.Length > 0 && sched != common) detail.Add("schedule " + Runs(sched));
            lines.Add("    " + string.Join("; ", detail));
            return string.Join("\n", lines);
        }

        private static int CompareWork(JToken a, JToken b)
        {
            bool na = a.Type == JTokenType.Integer || a.Type == JTokenType.Float, nb = b.Type == JTokenType.Integer || b.Type == JTokenType.Float;
            if (na && nb) return DigestJson.PyFloat(a).CompareTo(DigestJson.PyFloat(b));
            return PyJson.CompareCodePoints(PyJson.Str(a), PyJson.Str(b));
        }

        /// <summary>p.get("schedule") or "" (a string when it is one).</summary>
        private static string ScheduleOf(JToken p)
        {
            JToken s = DigestJson.G(p, "schedule");
            return s != null && s.Type == JTokenType.String ? (string)s : "";
        }

        private static string Render(JObject d, Level lv)
        {
            var outl = new List<string> { "DATE: " + Date(d) };
            JToken fd = DigestJson.G(d, "food_days");
            if (fd != null)
                outl.Add(DigestJson.IsNumberish(fd) ? "FOOD: about " + DigestJson.G6(fd) + " days of food" : "FOOD: " + PyJson.Str(fd));
            var res = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "resources")), lv.Resources);
            if (res.Count > 0)
            {
                var cats = new List<KeyValuePair<string, List<string>>>();
                foreach (var r in res)
                {
                    string cat = GameData.Clean(HasKey(r, "category") ? DigestJson.G(r, "category") ?? "" : "other", "label");
                    var entry = cats.FirstOrDefault(c => c.Key == cat);
                    if (entry.Value == null) { entry = new KeyValuePair<string, List<string>>(cat, new List<string>()); cats.Add(entry); }
                    entry.Value.Add(GameData.Clean(DigestJson.G(r, "label") ?? "", "label") + " " + DigestJson.Str(r, "count", "?"));
                }
                outl.Add("RESOURCES: " + string.Join("; ", cats.Select(c => c.Key + ": " + string.Join(", ", c.Value))));
            }
            var pawns = DigestJson.Items(DigestJson.G(d, "pawns"));
            var scheds = pawns.Select(ScheduleOf).Where(s => s.Length > 0).ToList();
            string common = "";
            if (scheds.Count > 0)
            {
                int best = 0;
                foreach (var s in scheds.Distinct())
                {
                    int n = scheds.Count(x => x == s);
                    if (n > best) { best = n; common = s; }
                }
            }
            outl.Add("PEOPLE (" + pawns.Count.ToString(CultureInfo.InvariantCulture) + "):");
            outl.AddRange(pawns.Select(p => PawnLine(p, lv, common)));
            if (common.Length > 0) outl.Add("Schedule of everyone not listed otherwise: " + Runs(common));
            var wt = DigestJson.Items(DigestJson.G(d, "work_types"));
            if (wt.Count > 0)
                outl.Add("Work types: " + string.Join(", ", wt.Select(w => GameData.Clean(w, "label"))) + ". Manual priorities: "
                    + (DigestJson.Truthy(DigestJson.G(d, "manual_priorities")) ? "on" : "off") + " (1 is highest, 0 is off).");
            var pol = DigestJson.G(d, "policies") as JObject;
            if (pol != null && pol.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in pol.Properties())
                {
                    if (!DigestJson.Truthy(kv.Value)) continue;
                    var vs = DigestJson.Take(DigestJson.Items(kv.Value), 8);
                    parts.Add(kv.Name + ": " + string.Join(", ", vs.Select(x => GameData.Clean(x, "label"))));
                }
                outl.Add("POLICIES: " + string.Join("; ", parts));
            }
            var beds = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "beds")), lv.Beds);
            if (beds.Count > 0)
                outl.Add("BEDS: " + string.Join("; ", beds.Select(b =>
                    "#" + DigestJson.Str(b, "id", "None") + " " + GameData.Clean(HasKey(b, "label") ? DigestJson.G(b, "label") ?? "" : "bed", "label")
                    + (DigestJson.Truthy(DigestJson.G(b, "room")) ? " in " + GameData.Clean(DigestJson.G(b, "room"), "label") : "")
                    + (DigestJson.Truthy(DigestJson.G(b, "owner")) ? " (" + GameData.Clean(DigestJson.G(b, "owner"), "name") + ")" : " (free)"))));
            var al = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "alerts")), lv.Alerts);
            string alText = string.Join("; ", al.Select(a => GameData.Clean(DigestJson.G(a, "label") ?? "", "label") + (DigestJson.Truthy(DigestJson.G(a, "critical")) ? " [CRITICAL]" : "")));
            outl.Add("ALERTS: " + (alText.Length > 0 ? alText : "none"));
            var th = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "threats")), 6);
            string thText = string.Join("; ", th.Select(t =>
                DigestJson.Str(t, "count", "?") + " " + GameData.Clean(DigestJson.G(t, "label") ?? "", "label")
                + ((HasKey(t, "hostile") ? DigestJson.Truthy(DigestJson.G(t, "hostile")) : true) ? "" : " (not hostile)")));
            outl.Add("THREATS: " + (thText.Length > 0 ? thText : "none"));
            var lt = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "letters")), 10);
            if (lt.Count > 0)
            {
                outl.Add("LETTERS NEEDING AN ANSWER:");
                foreach (var l in lt)
                {
                    string ch = string.Join(", ", DigestJson.Items(DigestJson.G(l, "choices")).Select(c => GameData.Clean(c, "label")));
                    if (ch.Length == 0) ch = "no choice, can be dismissed";
                    string ex = DigestJson.G(l, "expires_in_hours") != null ? ", expires in " + DigestJson.Str(l, "expires_in_hours", "") + " h" : "";
                    outl.Add("- letter #" + DigestJson.Str(l, "id", "None") + ": " + GameData.Clean(DigestJson.G(l, "label") ?? "", "label")
                        + (DigestJson.Truthy(DigestJson.G(l, "quest")) ? " (quest)" : "") + " [choices: " + ch + "]" + ex);
                    if (DigestJson.Truthy(DigestJson.G(l, "text")) && lv.Letter != 0)
                        outl.Add("    " + GameData.Clean(DigestJson.G(l, "text"), "line", lv.Letter));
                }
            }
            JToken rs = DigestJson.G(d, "research");
            JToken cur = DigestJson.G(rs, "current");
            var avail = DigestJson.Take(DigestJson.Items(DigestJson.G(rs, "available")), lv.Research);
            string rsText = "RESEARCH: ";
            if (DigestJson.Truthy(cur))
            {
                double prog = HasKey(cur, "progress") ? DigestJson.PyFloat(DigestJson.G(cur, "progress")) : 0.0;
                rsText += "now " + GameData.Clean(DigestJson.G(cur, "label") ?? "", "label") + " ("
                    + PyFmt.RoundInt(prog * 100).ToString(CultureInfo.InvariantCulture) + "%)";
            }
            else rsText += "nothing selected";
            if (avail.Count > 0)
                rsText += "; available: " + string.Join(", ", avail.Select(a => GameData.Clean(DigestJson.G(a, "label") ?? "", "label") + " (" + DigestJson.Str(a, "cost", "?") + ")"));
            outl.Add(rsText);
            var rm = DigestJson.G(d, "rooms") as JObject;
            if (rm != null && rm.Count > 0)
                outl.Add("ROOMS: " + string.Join(", ", rm.Properties().Select(kv =>
                    kv.Name + " " + (kv.Value.Type == JTokenType.Boolean ? ((bool)kv.Value ? "yes" : "no") : PyJson.Str(kv.Value)))));
            JToken pw = DigestJson.G(d, "power");
            if (DigestJson.Truthy(pw))
                outl.Add("POWER: produced " + DigestJson.Str(pw, "produced", "0") + ", used " + DigestJson.Str(pw, "used", "0") + ", stored " + DigestJson.Str(pw, "stored", "0"));
            return string.Join("\n", outl);
        }

        private static bool HasKey(JToken o, string k) => o is JObject jo && jo.ContainsKey(k);

        /// <summary>The digest as compact text of at most cap characters (detail shrinks level by level, then a hard cut).</summary>
        public static string RenderDigest(JObject digest, int cap = Cap)
        {
            string text = "";
            foreach (var lv in Levels)
            {
                text = Render(digest ?? new JObject(), lv);
                if (PyStr.Len(text) <= cap) return text;
            }
            string cut = PyStr.Head(text, Math.Max(0, cap - 40));
            int nl = cut.LastIndexOf('\n');
            return (nl >= 0 ? cut.Substring(0, nl) : cut) + "\n(digest cut: too long)";
        }
    }
}

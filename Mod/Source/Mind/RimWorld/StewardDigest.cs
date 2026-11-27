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

    /// <summary>TODO(merge): the combat view text lives in worker A's StewardCombat (steward_combat.py: view_active, DIGEST_CAPS,
    /// render_combat). Until that file is merged these are stand-ins so the digest compiles; the lead replaces the bodies with
    /// StewardCombat.ViewActive / DigestCaps / RenderCombat(view, cap) (same signatures as the Python).</summary>
    internal static class CombatSeam
    {
        public static readonly int[] DigestCaps = { 1500, 1000, 700, 450 };

        /// <summary>isinstance(view, dict) and view.get("active") is not False and bool(view).</summary>
        public static bool ViewActive(JToken view)
        {
            var o = view as JObject;
            if (o == null || o.Count == 0) return false;
            JToken a = o["active"];
            return !(a != null && a.Type == JTokenType.Boolean && !(bool)a);
        }

        /// <summary>STAND-IN: StewardCombat.RenderCombat(view, cap).</summary>
        public static string RenderCombat(JToken view, int cap) => "";
    }

    public static class StewardDigest
    {
        public const int Cap = 6000;

        private static readonly Dictionary<char, string> SchedWord = new Dictionary<char, string>
        { ['A'] = "any", ['W'] = "work", ['J'] = "joy", ['S'] = "sleep", ['M'] = "meditate" };

        private sealed class Level
        {
            public int Skills, Resources, Research, Beds, Letter, Alerts, Stuck, Deadlines, Rot, Harvest, Game, Cannot, Why, Combat;
            public Level(int skills, int resources, int research, int beds, int letter, int alerts, int stuck, int deadlines, int rot, int harvest, int game, int cannot, int why, int combat)
            {
                Skills = skills; Resources = resources; Research = research; Beds = beds; Letter = letter; Alerts = alerts;
                Stuck = stuck; Deadlines = deadlines; Rot = rot; Harvest = harvest; Game = game; Cannot = cannot; Why = why; Combat = combat;
            }
        }

        // detail levels, most to least: skills per pawn, resources, research options, beds, letter text chars, alerts, then the narrator-view
        // lines (stuck rows, deadlines, rotting stacks, harvest rows, game rows, "can't" reasons, mood "why" items),
        // combat = the character cap of the COMBAT lines (after ALERTS; the fight's DIGEST_CAPS)
        private static readonly Level[] Levels =
        {
            new Level(4, 25, 15, 20, 280, 10, 6, 6, 3, 3, 3, 3, 2, CombatSeam.DigestCaps[0]),
            new Level(3, 16, 8, 12, 160, 8, 4, 6, 3, 3, 2, 3, 2, CombatSeam.DigestCaps[1]),
            new Level(2, 10, 5, 8, 100, 5, 2, 4, 2, 2, 1, 2, 1, CombatSeam.DigestCaps[2]),
            new Level(1, 6, 3, 4, 60, 3, 0, 3, 1, 1, 0, 1, 0, CombatSeam.DigestCaps[3]),
        };

        private sealed class BuildLevel
        {
            public int Rooms, Benches, Bills, Recipes, Zones, CanBuild, Stuff, Plants, Ores, Loose, Flags;
            public BuildLevel(int rooms, int benches, int bills, int recipes, int zones, int canBuild, int stuff, int plants, int ores, int loose, int flags)
            { Rooms = rooms; Benches = benches; Bills = bills; Recipes = recipes; Zones = zones; CanBuild = canBuild; Stuff = stuff; Plants = plants; Ores = ores; Loose = loose; Flags = flags; }
        }

        // the build block's detail (it shrinks first): rooms, benches, bills per bench, recipes per bench, zones, can_build, stuff,
        // plants, ores, loose piles, room flags (temp, mountain, dirty). The last step keeps just BUILD/missing and a short ROOMS.
        private static readonly BuildLevel[] BuildLevels =
        {
            new BuildLevel(12, 8, 5, 8, 10, 40, 8, 10, 6, 6, 1),
            new BuildLevel(8, 5, 3, 4, 6, 20, 6, 6, 4, 4, 1),
            new BuildLevel(5, 3, 2, 0, 4, 0, 4, 0, 2, 2, 1),
            new BuildLevel(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        private sealed class TradeLevel
        {
            public int Stock, Needs, Never, Traders, Sells, Buys, Places, Quests;
            public TradeLevel(int stock, int needs, int never, int traders, int sells, int buys, int places, int quests)
            { Stock = stock; Needs = needs; Never = never; Traders = traders; Sells = sells; Buys = buys; Places = places; Quests = quests; }
        }

        // the trade blocks' detail (after build, before the base): stock items, needs, never-sell rules, traders, sells and buys per
        // trader, settlements, quests. The last step keeps the TRADE line and the few traders and quests.
        // (The Python level also has "caravans"; the keeper dropped caravans, so the field is left out.)
        private static readonly TradeLevel[] TradeLevels =
        {
            new TradeLevel(10, 8, 8, 6, 6, 4, 6, 6),
            new TradeLevel(6, 5, 5, 4, 4, 3, 4, 4),
            new TradeLevel(3, 3, 3, 3, 3, 2, 2, 3),
            new TradeLevel(0, 2, 0, 2, 2, 0, 1, 2),
        };

        // (base level, build level, trade level): the build block shrinks all the way first, then the trade blocks, then the rest
        private static readonly int[][] Steps =
        {
            new[] { 0, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, 2, 0 }, new[] { 0, 3, 0 }, new[] { 0, 3, 1 },
            new[] { 0, 3, 2 }, new[] { 0, 3, 3 }, new[] { 1, 3, 3 }, new[] { 2, 3, 3 }, new[] { 3, 3, 3 },
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
            if (IsTrue(DigestJson.G(p, "child"))) bits.Add("child");
            string h = GameData.Clean(DigestJson.G(p, "health") ?? "", "label");
            if (h.Length == 0) h = "fine";
            bits.Add((DigestJson.Truthy(DigestJson.G(p, "downed")) ? "DOWNED, " : "") + h);
            if (DigestJson.Truthy(DigestJson.G(p, "mental"))) bits.Add("mental break: " + GameData.Clean(DigestJson.G(p, "mental"), "label"));
            if (DigestJson.Truthy(DigestJson.G(p, "drafted"))) bits.Add("DRAFTED");
            string risk = BreakRisk(p, lv);
            if (risk.Length > 0) bits.Add(risk);
            if (IsTrue(DigestJson.G(p, "idle"))) bits.Add("IDLE");
            else if (DigestJson.Truthy(DigestJson.G(p, "job"))) bits.Add("doing " + GameData.Clean(DigestJson.G(p, "job"), "label"));
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
            var incapable = DigestJson.G(p, "incapable") as JArray;
            if (incapable != null && incapable.Count > 0)
                detail.Add("cannot " + string.Join(", ", DigestJson.Take(incapable.ToList(), 4).Select(x => GameData.Clean(x, "label"))));
            var cant = Words(DigestJson.G(p, "cannot"), lv.Cannot);
            if (cant.Count > 0) detail.Add("can't: " + string.Join("; ", cant));
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

        /// <summary>x is True (a JSON true, not a truthy value).</summary>
        private static bool IsTrue(JToken t) => t != null && t.Type == JTokenType.Boolean && (bool)t;

        /// <summary>isinstance(x, (int, float)) and not a bool.</summary>
        private static bool IsNum(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        private static string BreakRisk(JToken p, Level lv)
        {
            JToken br = DigestJson.G(p, "break_risk");
            string r = br != null && br.Type == JTokenType.String ? CleanLabel(br) : "";
            if (r.Length == 0) return "";
            string outs = r == "near" ? "near a minor break" : r + " break risk";
            var why = Words(DigestJson.G(p, "mood_why"), lv.Why);
            return outs + (why.Count > 0 ? " (why: " + string.Join(", ", why) + ")" : "");
        }

        /// <summary>Hours as a short phrase: '&lt;1 h', '5 h', '2 days' (from 48 h); "" for anything but a number.</summary>
        private static string Hours(double h)
        {
            if (double.IsNaN(h)) return "";
            if (h < 1) return "<1 h";
            return h < 48 ? PyFmt.RoundInt(h).ToString(CultureInfo.InvariantCulture) + " h" : PyFmt.RoundInt(h / 24).ToString(CultureInfo.InvariantCulture) + " days";
        }

        private static string Hours(JToken h) => IsNum(h) ? Hours(DigestJson.PyFloat(h)) : "";

        /// <summary>Num() for a computed value (Python's format(x, "g")).</summary>
        private static string NumD(double x) => DigestJson.G6(x);

        private static string Where(JObject r)
        {
            JToken dist = DigestJson.G(r, "dist");
            string dr = CleanLabel(DigestJson.G(r, "dir"));
            var parts = new List<string>();
            if (IsNum(dist)) parts.Add(Num(dist) + " cells");
            if (dr.Length > 0) parts.Add(dr);
            return string.Join(" ", parts);
        }

        private static string StuckLine(JObject d, int n)
        {
            var parts = new List<string>();
            foreach (var s in Objs(DigestJson.G(d, "stuck"), n))
            {
                string what = CleanLabel(DigestJson.G(s, "what")), why = CleanLabel(DigestJson.G(s, "reason"));
                if (what.Length == 0) continue;
                JToken cnt = DigestJson.G(s, "count");
                string head = (IsNum(cnt) && DigestJson.PyFloat(cnt) != 1 ? Num(cnt) + " " : "") + what;
                string w = Where(s);
                parts.Add(head + (w.Length > 0 ? " " + w : "") + (why.Length > 0 ? ": " + why : ""));
            }
            return parts.Count > 0 ? "STUCK: " + string.Join("; ", parts) : "";
        }

        private static string DeadlinesLine(JObject d, int n)
        {
            var rows = new List<(double hours, string what)>();
            foreach (var r in Objs(DigestJson.G(d, "deadlines"), 50))
            {
                JToken h = DigestJson.G(r, "hours");
                string what = CleanLabel(DigestJson.G(r, "what"));
                if (IsNum(h) && what.Length > 0) rows.Add((DigestJson.PyFloat(h), what));
            }
            var sorted = rows.OrderBy(x => x.hours).ToList();           // stable, as list.sort
            if (sorted.Count == 0 || n == 0) return "";
            return "DEADLINES: " + string.Join("; ", sorted.Take(n).Select(x => x.what + " in " + Hours(x.hours)));
        }

        private static string FoodDetails(JToken f, Level lv)
        {
            var fo = f as JObject;
            if (fo == null) return "";
            var outl = new List<string>();
            foreach (var (k, loose, word) in new[] { ("meals", "meals_loose", "meals"), ("raw", "raw_loose", "raw") })
            {
                JToken v = DigestJson.G(fo, k);
                if (IsNum(v))
                {
                    JToken ls = DigestJson.G(fo, loose);
                    outl.Add(Num(v) + " " + word + (IsNum(ls) && DigestJson.PyFloat(ls) > 0 ? " (" + Num(ls) + " loose)" : ""));
                }
            }
            var rot = new List<string>();
            foreach (var r in Objs(DigestJson.G(fo, "rot_soon"), lv.Rot))
            {
                JToken days = DigestJson.G(r, "days");
                if (IsNum(days) && CleanLabel(DigestJson.G(r, "label")).Length > 0)
                    rot.Add(CleanLabel(DigestJson.G(r, "label")) + " " + Num(DigestJson.G(r, "count")) + " in " + Hours(DigestJson.PyFloat(days) * 24));
            }
            if (rot.Count > 0) outl.Add("rots soon: " + string.Join(", ", rot));
            var har = new List<string>();
            foreach (var r in Objs(DigestJson.G(fo, "harvest"), lv.Harvest))
            {
                string plant = CleanLabel(DigestJson.G(r, "plant"));
                if (plant.Length == 0) continue;
                JToken cells = DigestJson.G(r, "cells"), ready = DigestJson.G(r, "ready"), days = DigestJson.G(r, "days");
                double rd = IsNum(ready) ? DigestJson.PyFloat(ready) : 0;
                string bit = plant + " " + (IsNum(ready) ? Num(ready) : "0") + " cells ready";
                if (IsNum(cells) && DigestJson.PyFloat(cells) > rd && IsNum(days))
                    bit += ", " + NumD(DigestJson.PyFloat(cells) - rd) + " more in " + Hours(DigestJson.PyFloat(days) * 24);
                har.Add(bit);
            }
            if (har.Count > 0) outl.Add("harvest: " + string.Join("; ", har));
            var game = new List<string>();
            foreach (var r in Objs(DigestJson.G(fo, "game"), lv.Game))
                if (CleanLabel(DigestJson.G(r, "label")).Length > 0)
                    game.Add(PyStr.Strip(CleanLabel(DigestJson.G(r, "label")) + " " + Num(DigestJson.G(r, "count")) + " " + Where(r)));
            if (game.Count > 0) outl.Add("game: " + string.Join("; ", game));
            JToken hn = DigestJson.G(fo, "hunters");
            if (IsNum(hn)) outl.Add(DigestJson.PyFloat(hn) != 0 ? Num(hn) + " hunters" : "no hunters");
            return string.Join("; ", outl);
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

        // ---- the build block (vivarium phase 2) ----------------------------------------------
        /// <summary>gd.clean(x, "label"): a missing or null value is "", anything else goes through Python's str().</summary>
        private static string CleanLabel(JToken t)
            => GameData.Clean(t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined ? "" : (t.Type == JTokenType.String ? (string)t : PyJson.Str(t)), "label");

        private static string Num(JToken v)
        {
            if (v != null && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)) return DigestJson.G6(v);
            string c = CleanLabel(v);
            return c.Length > 0 ? c : "?";
        }

        private static List<string> Words(JToken items, int n)
        {
            var l = items is JArray a ? DigestJson.Take(a.ToList(), n) : new List<JToken>();
            return l.Select(CleanLabel).Where(w => w.Length > 0).ToList();
        }

        private static List<JObject> Objs(JToken items, int n)
            => items is JArray a ? DigestJson.Take(a.ToList(), n).OfType<JObject>().ToList() : new List<JObject>();

        private static string RoomLine(JObject r, int flags = 0)
        {
            string role = CleanLabel(DigestJson.G(r, "role"));
            string label = CleanLabel(DigestJson.G(r, "label"));
            if (label.Length == 0) label = role;
            var bits = new List<string>
            {
                "#" + Num(DigestJson.G(r, "id")) + " " + label
                    + (role.Length > 0 && !label.ToLowerInvariant().Contains(role.ToLowerInvariant()) ? " (" + role + ")" : ""),
                Num(DigestJson.G(r, "w")) + "x" + Num(DigestJson.G(r, "h")),
            };
            if (DigestJson.G(r, "impressiveness") != null) bits.Add("imp " + Num(DigestJson.G(r, "impressiveness")));
            foreach (var k in new[] { "bed", "door", "roofed" })
                if (DigestJson.Truthy(DigestJson.G(r, k))) bits.Add(k);
            if (flags != 0)
            {
                JToken t = DigestJson.G(r, "temp_c");
                if (IsNum(t)) bits.Add(Num(t) + "C");
                if (IsTrue(DigestJson.G(r, "mountain"))) bits.Add("under mountain");
                if (IsTrue(DigestJson.G(r, "dirty"))) bits.Add("DIRTY");
            }
            return string.Join(" ", bits);
        }

        /// <summary>The BUILD block lines; counts is the old one-line room counts, merged into the ROOMS line.</summary>
        private static List<string> BuildLines(JObject b, BuildLevel bl, string counts)
        {
            var outl = new List<string>();
            var home = DigestJson.G(b, "home") as JObject;
            string head = "BUILD: " + (home != null && home.Count > 0
                ? "home (" + Num(DigestJson.G(home, "x")) + "," + Num(DigestJson.G(home, "z")) + ")" : "home unknown");
            var miss = Words(DigestJson.G(b, "missing"), 8);
            head += "; missing: " + (miss.Count > 0 ? string.Join(", ", miss) : "nothing");
            outl.Add(head);
            var rooms = Objs(DigestJson.G(b, "rooms"), bl.Rooms);
            if (rooms.Count > 0)
                outl.Add("ROOMS" + (counts.Length > 0 ? " (" + counts + ")" : "") + ": " + string.Join("; ", rooms.Select(r => RoomLine(r, bl.Flags))));
            foreach (var x in Objs(DigestJson.G(b, "benches"), bl.Benches))
            {
                string line = "BENCH: #" + Num(DigestJson.G(x, "id")) + " " + CleanLabel(DigestJson.G(x, "label"));
                if (DigestJson.Truthy(DigestJson.G(x, "room"))) line += " (" + CleanLabel(DigestJson.G(x, "room")) + ")";
                var bills = Words(DigestJson.G(x, "bills"), bl.Bills);
                var recipes = Words(DigestJson.G(x, "recipes"), bl.Recipes);
                line += ": bills " + (bills.Count > 0 ? string.Join(", ", bills) : "none");
                if (recipes.Count > 0) line += "; recipes " + string.Join(", ", recipes);
                outl.Add(line);
            }
            var zones = Objs(DigestJson.G(b, "zones"), bl.Zones);
            if (zones.Count > 0)
            {
                var zs = new List<string>();
                foreach (var z in zones)
                {
                    string kind = CleanLabel(DigestJson.G(z, "kind"));
                    JToken pl = DigestJson.G(z, "plant"), pr = DigestJson.G(z, "preset");
                    string what = CleanLabel(DigestJson.Truthy(pl) ? pl : DigestJson.Truthy(pr) ? pr : null);
                    zs.Add(("#" + Num(DigestJson.G(z, "id")) + " " + kind + " " + what + " " + Num(DigestJson.G(z, "cells")) + " cells").Replace("  ", " "));
                }
                outl.Add("ZONES: " + string.Join("; ", zs));
            }
            var cb = Words(DigestJson.G(b, "can_build"), bl.CanBuild);
            if (cb.Count > 0) outl.Add("CAN BUILD: " + string.Join(", ", cb));
            var pls = Words(DigestJson.G(b, "plants"), bl.Plants);
            if (pls.Count > 0) outl.Add("PLANTS: " + string.Join(", ", pls));
            var stuff = Objs(DigestJson.G(b, "stuff"), bl.Stuff);
            if (stuff.Count > 0)
                outl.Add("STUFF: " + string.Join(", ", stuff.Select(s => CleanLabel(DigestJson.G(s, "stuff")) + " " + Num(DigestJson.G(s, "count")))));
            var ores = Objs(DigestJson.G(b, "ores"), bl.Ores);
            if (ores.Count > 0)
                outl.Add("ORES: " + string.Join("; ", ores.Select(o => CleanLabel(DigestJson.G(o, "ore")) + " " + Num(DigestJson.G(o, "cells")) + " cells " + Num(DigestJson.G(o, "dist")) + " away")));
            var piles = Objs(DigestJson.G(b, "loose"), bl.Loose);
            if (piles.Count > 0) outl.Add("LOOSE: " + string.Join("; ", piles.Select(Pile)));
            return outl;
        }

        // ---- the trade blocks (vivarium phase 4); the CARAVANS block is dropped (keeper: no caravans at all) ----------------
        /// <summary>d.get(k, dflt) where a present JSON null stays null (Num of it is "?").</summary>
        private static JToken GetD(JToken o, string k, JToken dflt)
        {
            var jo = o as JObject;
            if (jo == null || !jo.TryGetValue(k, out JToken t)) return dflt;
            return t;
        }

        /// <summary>A JSON null (or a missing token) as "", for gd.clean(None).</summary>
        private static object Nz(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined ? (object)"" : t;

        private static string TraderLine(JObject t, TradeLevel tl)
        {
            string first = ("- " + CleanLabel(GetD(t, "id", new JValue(""))) + " " + CleanLabel(GetD(t, "kind", new JValue(""))) + " " + GameData.Clean(Nz(GetD(t, "name", new JValue(""))), "name")).Replace("  ", " ");
            if (DigestJson.Truthy(DigestJson.G(t, "faction"))) first += " (" + CleanLabel(DigestJson.G(t, "faction")) + ")";
            var bits = new List<string> { first };
            if (DigestJson.G(t, "leaves_h") != null) bits.Add("leaves in " + Num(DigestJson.G(t, "leaves_h")) + " h");
            if (DigestJson.G(t, "silver") != null) bits.Add("silver " + Num(DigestJson.G(t, "silver")));
            bool can = !t.ContainsKey("can_trade") || DigestJson.Truthy(DigestJson.G(t, "can_trade"));
            if (can) bits.Add("can trade");
            else { string why = CleanLabel(GetD(t, "why_not", new JValue(""))); bits.Add("CANNOT trade: " + (why.Length > 0 ? why : "no")); }
            var sells = Words(DigestJson.G(t, "sells"), tl.Sells);
            var buys = Words(DigestJson.G(t, "buys"), tl.Buys);
            if (sells.Count > 0) bits.Add("sells " + string.Join(", ", sells));
            if (buys.Count > 0) bits.Add("buys " + string.Join(", ", buys));
            return string.Join(", ", bits);
        }

        private static string PlaceLine(JObject s)
        {
            string head = CleanLabel(GetD(s, "id", new JValue(""))) + " " + GameData.Clean(Nz(GetD(s, "name", new JValue(""))), "name");
            var d = new List<string>();
            foreach (var k in new[] { "faction", "relation" })
                if (DigestJson.G(s, k) != null) d.Add(CleanLabel(DigestJson.G(s, k)));
            if (DigestJson.G(s, "goodwill") != null) d.Add("goodwill " + Num(DigestJson.G(s, "goodwill")));
            var bits = new List<string> { head + (d.Count > 0 ? " (" + string.Join(", ", d) + ")" : "") };
            if (DigestJson.G(s, "days") != null) bits.Add(Num(DigestJson.G(s, "days")) + " days");
            if (DigestJson.Truthy(DigestJson.G(s, "trader"))) bits.Add(CleanLabel(DigestJson.G(s, "trader")));
            return string.Join(", ", bits);
        }

        private static string QuestLine(JObject q)
        {
            JToken what = DigestJson.Truthy(DigestJson.G(q, "what")) ? DigestJson.G(q, "what") : GetD(q, "name", new JValue(""));
            var bits = new List<string>
            {
                "- " + CleanLabel(GetD(q, "id", new JValue(""))) + " [" + CleanLabel(GetD(q, "state", new JValue(""))) + "] " + CleanLabel(GetD(q, "kind", new JValue(""))) + ": "
                    + GameData.Clean(Nz(what), "label", 80),
            };
            if (DigestJson.Truthy(DigestJson.G(q, "reward"))) bits.Add("reward " + GameData.Clean(DigestJson.G(q, "reward"), "label", 60));
            if (DigestJson.G(q, "expires_h") != null) bits.Add("expires in " + Num(DigestJson.G(q, "expires_h")) + " h");
            if (DigestJson.G(q, "days") != null) bits.Add(Num(DigestJson.G(q, "days")) + " days away");
            JToken dg = DigestJson.G(q, "danger");
            if (!(dg == null || (dg.Type == JTokenType.String && ((string)dg == "" || (string)dg == "none")))) bits.Add("danger " + CleanLabel(dg));
            if (DigestJson.Truthy(DigestJson.G(q, "can"))) bits.Add("can");
            else { string why = GameData.Clean(Nz(GetD(q, "why_not", new JValue(""))), "label", 80); bits.Add("CANNOT: " + (why.Length > 0 ? why : "no")); }
            return string.Join(", ", bits);
        }

        /// <summary>TRADE / NEEDS / STOCK / NEVER SELL / TRADERS / SETTLEMENTS from the trade block, QUESTS from their own.</summary>
        private static List<string> TradeLines(JObject d, TradeLevel tl)
        {
            var outl = new List<string>();
            var t = DigestJson.G(d, "trade") as JObject;
            if (t != null && t.Count > 0)
            {
                var head = new List<string> { "silver " + Num(DigestJson.G(t, "silver")) };
                var ng = DigestJson.G(t, "negotiator") as JObject;
                if (ng != null && DigestJson.Truthy(DigestJson.G(ng, "name")))
                {
                    JToken pb = DigestJson.G(ng, "price_bonus");
                    long pct = DigestJson.IsNumberish(pb) ? PyFmt.RoundInt(DigestJson.PyFloat(pb) * 100) : 0;
                    head.Add("negotiator " + GameData.Name(DigestJson.G(ng, "name")) + " (social " + Num(DigestJson.G(ng, "social")) + ", prices +" + pct.ToString(CultureInfo.InvariantCulture) + "%)");
                }
                else head.Add("no negotiator");
                head.Add("comms " + CleanLabel(GetD(t, "comms", new JValue("?"))));
                head.Add("beacon " + (DigestJson.Truthy(DigestJson.G(t, "beacon")) ? "yes" : "no"));
                var td = DigestJson.G(t, "today") as JObject;
                if (td != null)
                    head.Add("today " + Num(GetD(td, "deals", new JValue(0))) + " deals, " + Num(GetD(td, "deals_left", new JValue("?"))) + " left, value left " + Num(GetD(td, "value_left", new JValue("?"))));
                outl.Add("TRADE: " + string.Join("; ", head));
                var needs = Words(DigestJson.G(t, "needs"), tl.Needs);
                if (needs.Count > 0) outl.Add("NEEDS: " + string.Join("; ", needs));
                var stock = Objs(DigestJson.G(t, "stock"), tl.Stock);
                if (stock.Count > 0)
                    outl.Add("STOCK: " + string.Join(", ", stock.Select(x => CleanLabel(GetD(x, "item", new JValue(""))) + " " + Num(DigestJson.G(x, "count"))
                        + (DigestJson.Truthy(DigestJson.G(x, "surplus")) ? " (surplus " + Num(DigestJson.G(x, "surplus")) + ")" : ""))));
                var never = Words(DigestJson.G(t, "never_sell"), tl.Never);
                if (never.Count > 0) outl.Add("NEVER SELL: " + string.Join("; ", never));
                var traders = Objs(DigestJson.G(t, "traders"), tl.Traders);
                if (traders.Count > 0)
                {
                    outl.Add("TRADERS (look with trade_offer, then trade; by id):");
                    outl.AddRange(traders.Select(x => TraderLine(x, tl)));
                }
                var places = Objs(DigestJson.G(t, "settlements"), tl.Places);
                if (places.Count > 0) outl.Add("SETTLEMENTS: " + string.Join("; ", places.Select(PlaceLine)));
            }
            var qs = Objs(DigestJson.G(d, "quests"), tl.Quests);
            if (qs.Count > 0)
            {
                outl.Add("QUESTS (an offer is answered with choose):");
                outl.AddRange(qs.Select(QuestLine));
            }
            return outl;
        }

        /// <summary>OPEN DIALOGS: the windows and letters that wait for a player's choice (GET /dialogs), answered with the choose tool.</summary>
        public static List<string> DialogLines(JObject d, int? letterCap = null)
        {
            var dl = DigestJson.Items(DigestJson.G(d, "dialogs")).OfType<JObject>().Where(x => DigestJson.Truthy(DigestJson.G(x, "id"))).Take(8).ToList();
            var outl = new List<string>();
            if (dl.Count == 0) return outl;
            int cap = letterCap ?? 280;
            outl.Add("OPEN DIALOGS (answer each with choose; one left open is answered with its default after the timeout):");
            foreach (var x in dl)
            {
                string opts = string.Join("; ", DigestJson.Items(DigestJson.G(x, "options")).OfType<JObject>().Where(o => !DigestJson.Truthy(DigestJson.G(o, "disabled"))).Select(o =>
                    PyJson.Str(DigestJson.G(o, "index")) + ": " + CleanLabel(DigestJson.G(o, "label"))
                    + (DigestJson.Truthy(DigestJson.G(o, "risk")) ? " [" + CleanLabel(DigestJson.G(o, "risk")) + "]" : "")));
                JObject g = DigestJson.G(x, "growth") is JObject go && go.Count > 0 ? go : null;
                string flds = string.Join(", ", DigestJson.Items(DigestJson.G(x, "fields")).OfType<JObject>().Select(f =>
                    CleanLabel(DigestJson.Truthy(DigestJson.G(f, "label")) ? DigestJson.G(f, "label") : (f.ContainsKey("name") ? DigestJson.G(f, "name") : null))
                    + " (max " + (f.ContainsKey("max_len") ? PyJson.Str(DigestJson.G(f, "max_len")) : "40") + " chars)"));
                outl.Add("- " + PyJson.Str(x["id"]) + " (" + (x.ContainsKey("kind") ? PyJson.Str(DigestJson.G(x, "kind")) : "") + "): " + CleanLabel(DigestJson.G(x, "title"))
                    + (opts.Length > 0 && g == null ? " [options: " + opts + "]" : "")
                    + (g != null ? Growth(g) : "")
                    + (flds.Length > 0 ? " [type a name: " + flds + "; invent your own fitting names]" : "")
                    // a naming dialog's fallback stays hidden: shown, the model copied it instead of naming the colony
                    + (DigestJson.Truthy(DigestJson.G(x, "default")) && flds.Length == 0 ? " [default: " + CleanLabel(DigestJson.G(x, "default")) + "]" : ""));
                if (DigestJson.Truthy(DigestJson.G(x, "text")) && cap != 0)
                    outl.Add("    " + GameData.Clean(DigestJson.G(x, "text"), "line", cap));
            }
            return outl;
        }

        private static readonly Dictionary<string, string> StoreWords = new Dictionary<string, string>
        { ["none"] = "no stockpile takes it", ["full"] = "stockpiles full", ["far"] = "only far stockpile" };

        /// <summary>A growth moment's real choices in words: the child, the passions (and how many), the traits with a short description.</summary>
        private static string Growth(JObject g)
        {
            Func<JToken, string> c = v => GameData.Clean(DigestJson.Truthy(v) ? PyJson.Str(v) : "", "label");
            string who = c(DigestJson.G(g, "child")) + (DigestJson.Truthy(DigestJson.G(g, "age")) ? ", " + c(DigestJson.G(g, "age")) : "");
            string cur = string.Join("; ", new[]
            {
                DigestJson.Truthy(DigestJson.G(g, "skills")) ? "skills: " + c(DigestJson.G(g, "skills")) : "",
                DigestJson.Truthy(DigestJson.G(g, "traits")) ? "traits: " + c(DigestJson.G(g, "traits")) : "",
            }.Where(x => x.Length > 0));
            JToken ps = DigestJson.G(g, "passions");
            IEnumerable<JToken> pItems = ps is JValue pv && pv.Type == JTokenType.String
                ? CodePointStrings((string)pv).Select(ch => (JToken)new JValue(ch)) : DigestJson.Items(DigestJson.Truthy(ps) ? ps : null);
            string pas = string.Join(", ", pItems.Select(c));
            string tr = string.Join("; ", DigestJson.Items(DigestJson.G(g, "trait_choices")).OfType<JObject>().Select(t =>
                c(DigestJson.G(t, "label")) + (DigestJson.Truthy(DigestJson.G(t, "description")) ? " (" + CleanLabel(DigestJson.G(t, "description")) + ")" : "")));
            JToken mp = DigestJson.G(g, "max_passions");
            long maxPassions = DigestJson.PyInt(DigestJson.Truthy(mp) ? mp : new JValue(1));
            return " [child: " + who + (cur.Length > 0 ? "; " + cur : "") + "]"
                + (pas.Length > 0 ? " [pick " + maxPassions.ToString(CultureInfo.InvariantCulture) + " passion(s) of: " + pas + "]" : "")
                + (tr.Length > 0 ? " [pick 1 trait of: " + tr + "]" : "")
                + " [answer with passions and trait; option 0 takes the first choices]";
        }

        private static IEnumerable<string> CodePointStrings(string t)
        {
            for (int i = 0; i < t.Length; i++)
            {
                if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1])) { yield return t.Substring(i, 2); i++; }
                else yield return t[i].ToString();
            }
        }

        private static string Pile(JObject p)
        {
            string where = Num(DigestJson.G(p, "dist")) + " cells" + (DigestJson.Truthy(DigestJson.G(p, "dir")) ? " " + CleanLabel(DigestJson.G(p, "dir")) : "");
            JToken st = DigestJson.G(p, "store");
            string store = st != null && st.Type == JTokenType.String && StoreWords.TryGetValue((string)st, out string sw) ? sw : "";
            return CleanLabel(DigestJson.G(p, "label")) + " " + Num(DigestJson.G(p, "count")) + " " + where + (DigestJson.Truthy(DigestJson.G(p, "forbidden")) ? " (forbidden)" : "")
                + (store.Length > 0 ? " (" + store + ")" : "");
        }

        private static bool PosNum(JObject r, string k)
        {
            JToken v = DigestJson.G(r, k);
            return v != null && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) && DigestJson.PyFloat(v) > 0;
        }

        private static string ResEntry(JToken r)
        {
            var ro = r as JObject ?? new JObject();
            var extra = new List<string>();
            if (PosNum(ro, "loose")) extra.Add("+" + Num(ro["loose"]) + " loose");
            if (PosNum(ro, "forbidden")) extra.Add(Num(ro["forbidden"]) + " forbidden");
            return GameData.Clean(DigestJson.G(r, "label") ?? "", "label") + " " + DigestJson.Str(r, "count", "?") + (extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : "");
        }

        private static string Render(JObject d, Level lv, BuildLevel bl, TradeLevel tl, bool combat = true)
        {
            var outl = new List<string> { "DATE: " + Date(d) };
            string dl = DeadlinesLine(d, lv.Deadlines);
            if (dl.Length > 0) outl.Add(dl);
            JToken fd = DigestJson.G(d, "food_days");
            string fdet = FoodDetails(DigestJson.G(d, "food"), lv);
            if (fd != null)
            {
                string food = DigestJson.IsNumberish(fd) ? "FOOD: about " + DigestJson.G6(fd) + " days of food" : "FOOD: " + CleanLabel(fd);
                outl.Add(food + (fdet.Length > 0 ? "; " + fdet : ""));
            }
            else if (fdet.Length > 0) outl.Add("FOOD: " + fdet);
            var res = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "resources")), lv.Resources);
            if (res.Count > 0)
            {
                var cats = new List<KeyValuePair<string, List<string>>>();
                foreach (var r in res)
                {
                    string cat = GameData.Clean(HasKey(r, "category") ? DigestJson.G(r, "category") ?? "" : "other", "label");
                    var entry = cats.FirstOrDefault(c => c.Key == cat);
                    if (entry.Value == null) { entry = new KeyValuePair<string, List<string>>(cat, new List<string>()); cats.Add(entry); }
                    entry.Value.Add(ResEntry(r));
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
            int idle = pawns.Count(p => IsTrue(DigestJson.G(p, "idle")));
            if (idle > 0) outl.Add("IDLE: " + idle.ToString(CultureInfo.InvariantCulture) + " of " + pawns.Count.ToString(CultureInfo.InvariantCulture) + " idle");
            string stuck = StuckLine(d, lv.Stuck);
            if (stuck.Length > 0) outl.Add(stuck);
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
            if (combat && CombatSeam.ViewActive(DigestJson.G(d, "combat")))      // never shrunk away: the combat block only loses detail
                outl.Add(CombatSeam.RenderCombat(DigestJson.G(d, "combat"), lv.Combat));
            var th = DigestJson.Take(DigestJson.Items(DigestJson.G(d, "threats")), 6);
            string thText = string.Join("; ", th.Select(t =>
                DigestJson.Str(t, "count", "?") + " " + GameData.Clean(DigestJson.G(t, "label") ?? "", "label")
                + ((HasKey(t, "hostile") ? DigestJson.Truthy(DigestJson.G(t, "hostile")) : true) ? "" : " (not hostile)")));
            outl.Add("THREATS: " + (thText.Length > 0 ? thText : "none"));
            outl.AddRange(DialogLines(d, lv.Letter));
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
            string counts = rm != null && rm.Count > 0
                ? string.Join(", ", rm.Properties().Select(kv =>
                    kv.Name + " " + (kv.Value.Type == JTokenType.Boolean ? ((bool)kv.Value ? "yes" : "no") : PyJson.Str(kv.Value))))
                : "";
            var bd = DigestJson.G(d, "build") as JObject;
            var built = bd != null && bd.Count > 0 ? BuildLines(bd, bl, counts) : new List<string>();
            if (built.Count > 0)
            {
                if (!built.Any(l => l.StartsWith("ROOMS", StringComparison.Ordinal)) && counts.Length > 0) built.Insert(1, "ROOMS: " + counts);
                outl.AddRange(built);
            }
            else if (counts.Length > 0) outl.Add("ROOMS: " + counts);
            outl.AddRange(TradeLines(d, tl));
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
            digest = digest ?? new JObject();
            foreach (var st in Steps)
            {
                text = Render(digest, Levels[st[0]], BuildLevels[st[1]], TradeLevels[st[2]]);
                if (PyStr.Len(text) <= cap) return text;
            }
            if (CombatSeam.ViewActive(DigestJson.G(digest, "combat")))           // the cut takes the tail of the rest, never the fight
            {
                string fight = CombatSeam.RenderCombat(DigestJson.G(digest, "combat"), CombatSeam.DigestCaps[CombatSeam.DigestCaps.Length - 1]);
                string rest = Render(digest, Levels[Levels.Length - 1], BuildLevels[BuildLevels.Length - 1], TradeLevels[TradeLevels.Length - 1], combat: false);
                string head = PyStr.Head(rest, Math.Max(cap - PyStr.Len(fight) - 40, 0));
                int hn = head.LastIndexOf('\n');
                return (hn >= 0 ? head.Substring(0, hn) : head) + "\n" + fight + "\n(digest cut: too long)";
            }
            string cut = PyStr.Head(text, Math.Max(0, cap - 40));
            int nl = cut.LastIndexOf('\n');
            return (nl >= 0 ? cut.Substring(0, nl) : cut) + "\n(digest cut: too long)";
        }
    }
}

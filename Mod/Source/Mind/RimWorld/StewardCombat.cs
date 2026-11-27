// Port of games/rimworld/steward_combat.py: the steward's combat tools (Vivarium phase 3), squad and defend, and the combat view's text.
//
// squad orders the fighters (POST /act/squad), defend plans defences (POST /act/defend). The model names colonists and threats
// in words ("Ana", "the pirates", "group 9012"); the ids come from the combat view (GET /combat, also the digest's combat
// block). Positions are the view's own words (cover, chokepoint, door...): the mod's CoverFinder picks the cells.
//
// squad exists only while a combat view is present (and in the "combat over" cycle, for undraft and rescue); defend also when
// the digest has a build block. Optional parameters follow the builder tools: an optional enum carries "none", an optional
// number 0.
//
// Differences from Python: int(text) of a threat number is a long (a longer number is read through BigInteger only for the
// "no threat numbered" message); str.lower() is ToLowerInvariant; a view list of the wrong type is read as empty.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The combat tools for one combat view (and the digest's build block, for defend) and the body of each call.</summary>
    public sealed class CombatKit
    {
        public const string None = "none";
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since the wounded must live); " +
            "it is shown to the player beside what you did", maxLen: WhyMax, required: false, sameCall: true);   // as StewardSession's (a copy, as the Python)

        public static readonly string[] ToolNames = { "squad", "defend" };
        public static readonly string[] Ops = { "draft", "move", "hold", "fire_at_will", "focus", "retreat", "rescue", "extinguish", "flee", "undraft" };
        public static readonly string[] Kinds = { "turret", "sandbags", "barricade", "killbox", "traps", "wall" };
        public static readonly string[] Sides = { "threat", "n", "e", "s", "w" };
        public static readonly string[] AtDefend = { "chokepoint", "entrance" };
        public static readonly string[] DefaultPositions = { "cover", "chokepoint", "door", "wall_n", "wall_e", "wall_s", "wall_w", "home", "inside" };   // when a view lists none
        private static readonly Dictionary<string, int> CountMax = new Dictionary<string, int>(StringComparer.Ordinal) { ["turret"] = 4, ["traps"] = 8 };
        private static readonly string[] LengthKinds = { "sandbags", "barricade", "wall" };
        private static readonly HashSet<string> Unused = new HashSet<string>(StringComparer.Ordinal) { "", None, "n/a", "0" };
        private static readonly HashSet<string> SquadWords = new HashSet<string>(StringComparer.Ordinal) { "squad", "all", "everyone", "everybody", "fighters", "the squad" };
        private static readonly string[] Filler = { "the", "a", "an", "those", "these", "them", "group", "groups", "threat", "threats", "enemy", "enemies", "of", "all", "pawn", "id", "attackers" };

        private static readonly Regex TargetPrefix = new Regex(@"^(?:the\s+)?(?:group|threat|pawn|id)?\s*#?\s*", RegexOptions.CultureInvariant);
        private static readonly Regex Letters = new Regex("[a-z]+", RegexOptions.CultureInvariant);

        private sealed class Group
        {
            public long Id;
            public List<long> Ids = new List<long>();
            public string Text = "";
        }

        public readonly JObject View;                 // null when there is no active combat view
        public readonly bool AfterFight;
        public readonly Dictionary<string, int> Pawns;
        public readonly BuildKit Build;
        private readonly List<Group> _groups = new List<Group>();
        public readonly List<string> Positions;
        /// <summary>id (as text) -> short description, in the digest's order.</summary>
        public readonly List<KeyValuePair<string, string>> Rooms;
        public readonly List<string> Stuff;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        // ---- python helpers -------------------------------------------------------------
        /// <summary>_w(x, n): the word cleaned as a label, at most n characters, stripped (None is "").</summary>
        internal static string W(JToken x, int n = 40)
            => PyStr.Strip(GameData.Clean(x == null || x.Type == JTokenType.Null ? "" : PyJson.Str(x), "label", n));

        internal static string W(string x, int n = 40) => PyStr.Strip(GameData.Clean(x ?? "", "label", n));

        /// <summary>_int(x): int(x), or null when Python raises TypeError / ValueError.</summary>
        internal static long? IntOf(JToken x)
        {
            try { return DigestJson.PyInt(x); }
            catch (Exception e) when (e is PyTypeError || e is PyValueError) { return null; }
        }

        private static string Stem(string word)
        {
            string w = word.ToLowerInvariant();
            if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) return w.Substring(0, w.Length - 3) + "y";
            if (w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal) && w.Length > 3) return w.Substring(0, w.Length - 1);
            return w;
        }

        private static HashSet<string> Tokens(string text)
            => new HashSet<string>(Letters.Matches(text.ToLowerInvariant()).Cast<Match>().Select(m => Stem(m.Value)), StringComparer.Ordinal);

        /// <summary>view.get(k) or []: the items of a list (an object yields its keys, as iterating a dict does).</summary>
        internal static List<JToken> OrList(JToken t) => DigestJson.Truthy(t) ? DigestJson.Items(t) : new List<JToken>();

        internal static bool ViewActive(JToken view)
        {
            var o = view as JObject;
            if (o == null) return false;
            JToken a;
            if (o.TryGetValue("active", out a) && a.Type == JTokenType.Boolean && !(bool)a) return false;
            return o.Count > 0;
        }

        public CombatKit(JToken view, Dictionary<string, int> pawns = null, BuildKit build = null, bool afterFight = false)
        {
            View = ViewActive(view) ? (JObject)view : null;
            AfterFight = afterFight && View == null;       // the "combat over" cycle: squad stays for undraft and rescue
            Pawns = new Dictionary<string, int>(pawns ?? new Dictionary<string, int>(), StringComparer.Ordinal);
            Build = build != null && build.Has ? build : null;
            JObject v = View ?? new JObject();
            foreach (var gt in OrList(DigestJson.G(v, "threats")))
            {
                var g = gt as JObject;
                long? gid = g == null ? null : IntOf(DigestJson.G(g, "id"));
                if (gid.HasValue && gid.Value != 0)
                {
                    var grp = new Group { Id = gid.Value };
                    foreach (var x in OrList(DigestJson.G(g, "ids")))
                    {
                        long? i = IntOf(x);
                        if (i.HasValue && i.Value != 0) grp.Ids.Add(i.Value);
                    }
                    var words = new List<string> { W(DigestJson.G(g, "faction")), W(DigestJson.G(g, "kind")) };
                    words.AddRange(OrList(DigestJson.G(g, "labels")).Select(x => W(x)));
                    grp.Text = string.Join(" ", words);
                    _groups.Add(grp);
                }
            }
            Positions = OrList(DigestJson.G(v, "positions")).Select(x => W(x, 20)).Where(p => p.Length > 0).ToList();
            if (Positions.Count == 0) Positions = new List<string>(DefaultPositions);
            Rooms = Build != null ? new List<KeyValuePair<string, string>>(Build.Rooms) : new List<KeyValuePair<string, string>>();
            Stuff = Build != null ? new List<string>(Build.Stuff) : new List<string>();
            Tools = BuildTools();
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        // ---- the tools ------------------------------------------------------------------
        private List<ToolSpec> BuildTools()
        {
            var outl = new List<ToolSpec>();
            if (View != null || AfterFight)
            {
                var ps = new List<Param>
                {
                    new Param("op", "enum", "what to do: draft the fighters, move or retreat them to a position, hold (stand and fire, no chasing) or fire_at_will, " +
                        "focus a threat, rescue the downed, extinguish fires, flee (non-fighters indoors), undraft", choices: Ops),
                    new Param("pawns", "text", "names separated by commas, or squad (the current squad; for draft: every fighter)", maxLen: 160, required: false),
                    new Param("at", "enum", "move/retreat: where to stand", choices: new[] { None }.Concat(Positions), required: false),
                };
                if (Rooms.Count > 0)
                {
                    string roles = string.Join(", ", Rooms.Select(p => p.Value.Length > 0 ? p.Key + " " + p.Value : p.Key));
                    ps.Add(new Param("room", "enum", "at door or inside: which room (room id: " + roles + ")", choices: new[] { None }.Concat(Rooms.Select(p => p.Key)), required: false));
                }
                ps.Add(new Param("target", "text", "focus: the threat, as its group number from THREATS or words like the pirates", maxLen: 60, required: false));
                ps.Add(new Param("below", "int", "retreat: wounded fall back under this health percent (10 to 60), 0 if unused", lo: 0, hi: 90, required: false));
                ps.Add(WhyParam);
                outl.Add(new ToolSpec("squad", "Order the fighters in a fight: draft, move to cover or a door, hold, focus a threat, retreat the wounded, " +
                    "rescue the downed, put out fires, send non-fighters indoors, undraft.", ps));
            }
            if (View != null || Build != null || AfterFight)
            {
                var ds = new List<Param>
                {
                    new Param("kind", "enum", "killbox is a kill zone at the main entrance", choices: Kinds),
                    new Param("side", "enum", "threat (the enemy side) or n/e/s/w", choices: new[] { None }.Concat(Sides), required: false),
                    new Param("at", "enum", "instead of side", choices: new[] { None }.Concat(AtDefend), required: false),
                    new Param("count", "int", "turret 1-4, traps 1-8, 0 unused", lo: 0, hi: 8, required: false),
                    new Param("length", "int", "sandbags, barricade, wall 1-20, 0 unused", lo: 0, hi: 20, required: false),
                };
                if (Stuff.Count > 0) ds.Add(new Param("stuff", "enum", "material", choices: new[] { None }.Concat(Stuff), required: false));
                ds.Add(WhyParam);
                outl.Add(new ToolSpec("defend", "Plan defences (blueprints); the planner picks the cells.", ds));
            }
            return outl;
        }

        // ---- helpers --------------------------------------------------------------------
        /// <summary>v.get(k), or null when it is missing, null or one of the unused words.</summary>
        private static JToken Get(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken x) || x.Type == JTokenType.Null) return null;
            return Unused.Contains(PyStr.Strip(PyJson.Str(x)).ToLowerInvariant()) ? null : x;
        }

        /// <summary>null = the squad (or, for draft, every fighter); else the named colonists' ids.</summary>
        private List<long> PawnIds(string raw)
        {
            if (raw == null) return null;
            string low = PyStr.Strip(raw).ToLowerInvariant();
            if (Unused.Contains(low) || SquadWords.Contains(low)) return null;
            var ids = new List<long>();
            foreach (string part0 in raw.Split(','))
            {
                string part = PyStr.Strip(part0);
                if (part.Length == 0) continue;
                string hit = Pawns.Keys.FirstOrDefault(n => n.ToLowerInvariant() == part.ToLowerInvariant());
                if (hit == null) throw new PyValueError("no colonist named " + part + "; the colonists are: " + string.Join(", ", Pawns.Keys));
                if (!ids.Contains(Pawns[hit])) ids.Add(Pawns[hit]);
            }
            if (ids.Count > 30) ids = ids.GetRange(0, 30);
            return ids.Count == 0 ? null : ids;
        }

        private string GroupList(IEnumerable<Group> gs) => string.Join(", ", gs.Select(g => "#" + g.Id.ToString(CultureInfo.InvariantCulture) + " " + g.Text));

        /// <summary>A threat group id (or one of its pawn ids) from the model's words.</summary>
        public long Target(string text)
        {
            if (_groups.Count == 0) throw new PyValueError("no threats in the view to focus");
            string t = PyStr.Strip(text).ToLowerInvariant();
            string rest = PyStr.Strip(TargetPrefix.Replace(t, ""));
            if (BigInteger.TryParse(rest, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger n))
            {
                if (n >= long.MinValue && n <= long.MaxValue)
                {
                    long ln = (long)n;
                    if (_groups.Any(g => g.Id == ln || g.Ids.Contains(ln))) return ln;
                }
                throw new PyValueError("no threat numbered " + n.ToString(CultureInfo.InvariantCulture) + "; the threats are: " + GroupList(_groups));
            }
            if (t == "nearest" || t == "closest" || t == "the nearest" || t == "the closest" || t == "first") return _groups[0].Id;
            var filler = new HashSet<string>(Filler.Concat(Filler.Select(Stem)), StringComparer.Ordinal);
            var toks = Tokens(t);
            toks.ExceptWith(filler);
            if (toks.Count == 0)
            {
                if (_groups.Count == 1) return _groups[0].Id;
                throw new PyValueError("say which threat: " + GroupList(_groups));
            }
            var scored = _groups.Select(grp => (score: toks.Intersect(Tokens(grp.Text)).Count(), grp)).ToList();
            int best = scored.Max(sc => sc.score);
            var hits = scored.Where(sc => sc.score == best).Select(sc => sc.grp).ToList();
            if (best == 0) throw new PyValueError("no threat matches " + text + "; the threats are: " + GroupList(_groups));
            if (hits.Count > 1) throw new PyValueError(text + " matches more than one threat; use a number: " + GroupList(hits));
            return hits[0].Id;
        }

        // ---- the bodies -----------------------------------------------------------------
        public (string verb, JObject body) Body(string tool, JObject v)
            => tool == "squad" ? ("squad", Squad(v)) : ("defend", Defend(v));

        private JObject Squad(JObject v)
        {
            string op = PyJson.Str(v["op"]);
            var b = new JObject { ["op"] = op };
            JToken rawPawns = Get(v, "pawns");
            var ids = PawnIds(rawPawns == null ? null : PyJson.Str(rawPawns));
            if (ids != null && ids.Count > 0) b["pawns"] = new JArray(ids.Select(i => (object)i).ToArray());
            JToken at = Get(v, "at");
            if (at != null && !(at.Type == JTokenType.String && Positions.Contains((string)at)))
                throw new PyValueError("no such position; the positions are: " + string.Join(", ", Positions));
            if (op == "move" && at == null) throw new PyValueError("move needs at, one of: " + string.Join(", ", Positions));
            if ((op == "move" || op == "retreat") && at != null)
            {
                b["at"] = at.DeepClone();
                JToken room = Get(v, "room");
                if (room != null)
                {
                    if (!Rooms.Any(p => p.Key == PyJson.Str(room)))
                        throw new PyValueError("no such room; the rooms are: " + (Rooms.Count == 0 ? "none listed" : string.Join(", ", Rooms.Select(p => p.Key))));
                    string a = (string)at;
                    if (a == "door" || a == "inside") b["room"] = DigestJson.PyInt(room);
                }
            }
            if (op == "focus")
            {
                JToken t = Get(v, "target");
                if (t == null) throw new PyValueError("focus needs target: a threat number from THREATS or words like the pirates");
                b["target"] = Target(PyJson.Str(t));
            }
            if (op == "retreat")
            {
                JToken below = Get(v, "below");
                if (below != null)
                {
                    long bl = DigestJson.PyInt(below);
                    if (bl > 0)                       // the grammar writes every field: 0 = unused; over 60 would pull back the barely scratched
                        b["below"] = Math.Round(Math.Min(60, Math.Max(10, bl)) / 100.0, 2);
                }
            }
            return b;
        }

        private JObject Defend(JObject v)
        {
            string k = PyJson.Str(v["kind"]);
            var b = new JObject { ["kind"] = k };
            JToken at = Get(v, "at"), side = Get(v, "side");
            if (k == "killbox" && at != null && at.Type == JTokenType.String && (string)at == "chokepoint") at = null;   // a killbox always goes at the main entrance; the side picks which door
            if (at != null) b["at"] = at.DeepClone();
            else if (side != null) b["side"] = side.DeepClone();
            JToken cnt = Get(v, "count");
            if (CountMax.TryGetValue(k, out int max) && cnt != null)
            {
                long c = DigestJson.PyInt(cnt);
                if (!(1 <= c && c <= max)) throw new PyValueError(k + ": count is 1 to " + max.ToString(CultureInfo.InvariantCulture));
                b["count"] = c;
            }
            JToken ln = Get(v, "length");
            if (LengthKinds.Contains(k) && ln != null) b["length"] = DigestJson.PyInt(ln);
            JToken s = Get(v, "stuff");
            if (s != null)
            {
                if (!(s.Type == JTokenType.String && Stuff.Contains((string)s))) throw new PyValueError("no such material; the stock is: " + string.Join(", ", Stuff));
                b["stuff"] = s.DeepClone();
            }
            return b;
        }
    }

    /// <summary>The combat view as text (about 1.5 KB at most): threats, fighters, others, downed, fires, defences, squad.</summary>
    public static class StewardCombat
    {
        public const string SystemName = "RimWorldLLM.Mind.steward_combat.txt";
        public static readonly int[] DigestCaps = { 1500, 1000, 700, 450 };        // the digest's four detail steps ask for these

        /// <summary>The combat turn's system prompt (steward_combat.txt).</summary>
        public static string SystemPrompt() => StewardResources.Text(SystemName);

        private sealed class Level
        {
            public int Threats, Fighters, Others, Labels, Detail;
            public Level(int threats, int fighters, int others, int labels, int detail)
            { Threats = threats; Fighters = fighters; Others = others; Labels = labels; Detail = detail; }
        }

        private static readonly Level[] Levels =
        {
            new Level(6, 8, 5, 3, 2),
            new Level(4, 6, 3, 2, 1),
            new Level(3, 5, 2, 1, 0),
            new Level(2, 3, 0, 0, 0),
        };

        private static string W(JToken x, int n = 40) => CombatKit.W(x, n);
        private static string Nm(JToken x) => GameData.Name(x == null || x.Type == JTokenType.Null ? null : PyJson.Str(x));
        private static bool T(JToken x) => DigestJson.Truthy(x);
        private static List<JObject> Objs(JToken x) => CombatKit.OrList(x).OfType<JObject>().ToList();
        private static JObject Obj(JToken x) => x as JObject ?? new JObject();

        private static string Fighter(JObject f, int detail)
        {
            var bits = new List<string> { T(DigestJson.G(f, "drafted")) ? "drafted" : "not drafted" };
            JToken health = DigestJson.G(f, "health");
            bits.Add(W(T(health) ? health : new JValue("ok"), 20));
            JToken weapon = DigestJson.G(f, "weapon");
            if (T(weapon))
                bits.Add(W(weapon, 24) + (detail > 0 && DigestJson.G(f, "range") != null ? " range " + PyJson.Str(f["range"]) : ""));
            if (detail > 0)
            {
                if (T(DigestJson.G(f, "cover"))) bits.Add(W(f["cover"], 24));
                if (T(DigestJson.G(f, "mood"))) bits.Add("mood " + W(f["mood"], 12));
                if (T(DigestJson.G(f, "job"))) bits.Add(W(f["job"], 32));
            }
            if (detail > 1 && T(DigestJson.G(f, "where"))) bits.Add(W(f["where"], 36));
            return "- " + Nm(DigestJson.G(f, "name")) + ": " + string.Join(", ", bits.Where(b => b.Length > 0));
        }

        private static List<string> Render(JObject view, Level lv)
        {
            // the contract names the block "speed", which the envelope's own "speed" (a number) shadows on the wire: take either key
            JObject sp = new JObject();
            foreach (var x in new[] { DigestJson.G(view, "speed_rule"), DigestJson.G(view, "speed") })
                if (x is JObject xo) { sp = xo; break; }
            var outl = new List<string>
            {
                "COMBAT: fight under way" + (T(DigestJson.G(sp, "slowed")) && T(DigestJson.G(sp, "before"))
                    ? " (game speed was " + PyJson.Str(sp["before"]) + "x, slowed for the fight)" : ""),
            };
            var names = new Dictionary<long, string>();
            foreach (string key in new[] { "fighters", "others" })
                foreach (var f in CombatKit.OrList(DigestJson.G(view, key)))
                {
                    long? id = f is JObject ? CombatKit.IntOf(DigestJson.G(f, "id")) : null;
                    if (id.HasValue && id.Value != 0) names[id.Value] = Nm(DigestJson.G(f, "name"));
                }
            var th = Objs(DigestJson.G(view, "threats"));
            outl.Add("THREATS" + (th.Count > 1 ? " (nearest first)" : "") + ":" + (th.Count == 0 ? " none in sight" : ""));
            foreach (var g in th.Take(lv.Threats))
            {
                var labels = CombatKit.OrList(DigestJson.G(g, "labels")).Take(lv.Labels).Select(x => W(x, 28)).ToList();
                long? gid = CombatKit.IntOf(DigestJson.G(g, "id"));
                JToken faction = DigestJson.G(g, "faction");
                outl.Add("- #" + (gid.HasValue && gid.Value != 0 ? gid.Value.ToString(CultureInfo.InvariantCulture) : "?") + " " + DigestJson.Str(g, "count", "?") + " "
                    + W(DigestJson.G(g, "kind"), 14) + ", " + W(T(faction) ? faction : new JValue("wild"), 24) + ", "
                    + W(DigestJson.G(g, "where"), 40) + ", " + W(DigestJson.G(g, "heading"), 16)
                    + (labels.Count > 0 ? ": " + string.Join(", ", labels) : ""));
            }
            if (th.Count > lv.Threats) outl.Add("- and " + (th.Count - lv.Threats).ToString(CultureInfo.InvariantCulture) + " more groups");
            var fs = Objs(DigestJson.G(view, "fighters"));
            outl.Add("FIGHTERS:" + (fs.Count == 0 ? " none able to fight" : ""));
            outl.AddRange(fs.Take(lv.Fighters).Select(f => Fighter(f, lv.Detail)));
            if (fs.Count > lv.Fighters)
                outl.Add("- and " + (fs.Count - lv.Fighters).ToString(CultureInfo.InvariantCulture) + " more: "
                    + string.Join(", ", fs.Skip(lv.Fighters).Take(6).Select(f => Nm(DigestJson.G(f, "name")))));
            var ot = Objs(DigestJson.G(view, "others"));
            if (ot.Count > 0 && lv.Others > 0)
                outl.Add("NOT FIGHTING: " + string.Join(", ", ot.Take(lv.Others).Select(o =>
                        Nm(DigestJson.G(o, "name")) + " (" + W(DigestJson.G(o, "why"), 14) + ", " + (T(DigestJson.G(o, "safe")) ? "safe" : "EXPOSED") + ")"))
                    + (ot.Count > lv.Others ? " and " + (ot.Count - lv.Others).ToString(CultureInfo.InvariantCulture) + " more" : ""));
            else if (ot.Count > 0)
                outl.Add("NOT FIGHTING: " + ot.Count.ToString(CultureInfo.InvariantCulture) + " ("
                    + ot.Count(o => !T(DigestJson.G(o, "safe"))).ToString(CultureInfo.InvariantCulture) + " exposed)");
            JObject dn = Obj(DigestJson.G(view, "downed"));
            var cs = Objs(DigestJson.G(dn, "colonists"));
            if (cs.Count > 0 || T(DigestJson.G(dn, "enemies")))
            {
                string col = string.Join(", ", cs.Take(5).Select(c => Nm(DigestJson.G(c, "name")) + (T(DigestJson.G(c, "bleeding")) ? " (bleeding)" : "")
                    + (lv.Detail > 0 && T(DigestJson.G(c, "where")) ? " " + W(c["where"], 30) : "")));
                var parts = new List<string> { col.Length > 0 ? col : "no colonists" };
                if (T(DigestJson.G(dn, "enemies"))) parts.Add(PyJson.Str(dn["enemies"]) + " enemies");
                outl.Add("DOWNED: " + string.Join("; ", parts));
            }
            JObject fi = Obj(DigestJson.G(view, "fires"));
            if (T(DigestJson.G(fi, "count")))
                outl.Add("FIRES: " + PyJson.Str(fi["count"]) + " (" + DigestJson.Str(fi, "home", "0") + " in the home area)"
                    + (T(DigestJson.G(fi, "where")) ? ", " + W(fi["where"], 30) : ""));
            if (T(DigestJson.G(view, "defenses"))) outl.Add("DEFENSES: " + W(view["defenses"], 200));
            JObject sq = Obj(DigestJson.G(view, "squad"));
            if (T(DigestJson.G(sq, "members")))
            {
                var mem = CombatKit.OrList(sq["members"]).Take(10).Select(i =>
                {
                    long? id = CombatKit.IntOf(i);
                    return names.TryGetValue(id ?? 0, out string nm) ? nm : PyJson.Str(i);
                }).ToList();
                JObject rt = DigestJson.G(sq, "retreat") as JObject;
                JToken stance = DigestJson.G(sq, "stance");
                outl.Add("SQUAD: " + string.Join(", ", mem) + "; " + W(T(stance) ? stance : new JValue("fire_at_will"), 14)
                    + (T(DigestJson.G(sq, "focus")) ? "; focus #" + PyJson.Str(sq["focus"]) : "")
                    + (rt != null && rt.Count > 0 ? "; retreat point (" + DigestJson.Str(rt, "x", "None") + "," + DigestJson.Str(rt, "z", "None") + ")" : ""));
            }
            return outl;
        }

        /// <summary>The combat view as text of at most cap characters (detail shrinks level by level; the threat lines always stay).</summary>
        public static string RenderCombat(JToken view, int cap = 1500)
        {
            var vo = view as JObject;
            if (vo == null) return "";
            string text = "";
            foreach (var lv in Levels)
            {
                text = string.Join("\n", Render(vo, lv));
                if (PyStr.Len(text) <= cap) return text;
            }
            var lines = text.Split('\n').ToList();
            while (PyStr.Len(string.Join("\n", lines)) > cap && lines.Count > 3) lines.RemoveAt(lines.Count - 1);
            return PyStr.Head(string.Join("\n", lines), cap);
        }
    }
}

// Port of games/rimworld/steward_bio.py: the steward's Biotech tools (task rh-bio): mechs, genes and children.
//
// Built from the digest's `bio` block (contract A): no block, no tools, and a tool (or one of its ops) appears only when
// the digest holds the data it needs. Each call maps to one POST /act/<verb>: mechs -> mech_mode | mech_group |
// mech_gestate, genes -> genepack | xenogerm | hemogen, children -> child_schedule | feed | child_care. Optional values use
// the same `none` / 0 convention as StewardBuild. Names become ids through the digest; an unknown name is a PyValueError
// listing the known ones. Nothing here is romantic: children only get schedules, feeding and care.
//
// Differences from Python: a digest list of the wrong type is read as absent (Python would sometimes raise); str.lower() is
// ToLowerInvariant.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The Biotech tools for one digest, and the body of each call.</summary>
    public sealed class BioKit
    {
        public const string None = "none";
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because; it is shown to the player",
            maxLen: WhyMax, required: false, sameCall: true);   // a copy, as StewardBuild (the Python's too)

        public static readonly string[] ToolNames = { "mechs", "genes", "children" };
        public static readonly string[] Modes = { "work", "escort", "recharge", "shutdown" };
        public static readonly string[] Presets = { "school", "play", "default" };
        public static readonly string[] FeedModes = { "never", "childcare", "urgent" };
        public const int MaxNames = 12;
        private const int ChildAge = 13;

        private static readonly Regex TrailingParens = new Regex(@"\s*\([^)]*\)\s*$", RegexOptions.CultureInvariant);

        public readonly bool Has;
        public readonly Dictionary<string, int> Pawns;
        public readonly Dictionary<string, int> Adults = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Mechs, Children, Babies, Prisoners, Germs;
        public readonly List<string> Kinds, Packs, Carers;
        public readonly JObject Gb;
        public readonly bool Hemogen, Genes, ImplantOk, Mechanitors;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        private static List<string> Words(IEnumerable<string> items)
        {
            var outl = new List<string>();
            foreach (var x in items)
            {
                string w = PyStr.Strip(GameData.Clean(x, "bio", 40));
                if (w.Length > 0 && !outl.Contains(w)) outl.Add(w);
            }
            return outl;
        }

        /// <summary>Iterating a digest value as Python would: a list's items, a dict's keys, a string's characters, else nothing.</summary>
        private static List<JToken> Iter(JToken t)
        {
            if (t is JArray a) return a.ToList();
            if (t is JObject o) return o.Properties().Select(p => (JToken)new JValue(p.Name)).ToList();
            if (t != null && t.Type == JTokenType.String)
            {
                var l = new List<JToken>();
                string s = (string)t;
                for (int i = 0; i < s.Length; i++)
                {
                    int n = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                    l.Add(new JValue(s.Substring(i, n)));
                    i += n - 1;
                }
                return l;
            }
            return new List<JToken>();
        }

        private static List<string> WordsOf(JToken t) => Words(Iter(t).Select(x => PyJson.Str(x)));

        /// <summary>cleaned name -> id for a digest list of {name, id_key} dicts (first duplicate wins); empty names dropped.</summary>
        private static Dictionary<string, long> Named(JToken items, string idKey)
        {
            var seen = new List<KeyValuePair<string, long>>();
            foreach (var x in Iter(items))
            {
                if (!(x is JObject xo)) continue;
                try
                {
                    string name = GameData.Name(DigestJson.G(xo, "name"));
                    if (!xo.TryGetValue(idKey, out JToken id)) throw new PyKeyError(idKey);
                    long i = DigestJson.PyInt(id);
                    if (!seen.Any(p => p.Key == name)) seen.Add(new KeyValuePair<string, long>(name, i));
                }
                catch (Exception e) when (e is PyKeyError || e is PyTypeError || e is PyValueError) { }
            }
            var outd = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var p in seen) if (p.Key.Length > 0) outd[p.Key] = p.Value;
            return outd;
        }

        private static JArray ListOf(JObject b, string key) => b.TryGetValue(key, out JToken v) && v is JArray a ? a : new JArray();

        private static Param Opt(string name, string desc, IEnumerable<string> choices)
            => new Param(name, "enum", desc, choices: new[] { None }.Concat(choices), required: false);

        public BioKit(JObject digest)
        {
            var bo = DigestJson.G(digest, "bio") as JObject;
            Has = bo != null;
            JObject b = bo ?? new JObject();
            Pawns = StewardDigest.PawnNames(digest);
            // grown colonists only: feeders, carers and implant targets are never children or prisoners
            var kid = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in DigestJson.Items(DigestJson.G(digest, "pawns")))
            {
                if (!(p is JObject po)) continue;
                JToken age = DigestJson.G(po, "age");
                if (DigestJson.Str(po, "role", "colonist").ToLowerInvariant() != "colonist"
                    || (DigestJson.IsNumberish(age) && DigestJson.PyFloat(age) < ChildAge))
                    kid.Add(GameData.Name(DigestJson.G(po, "name")));
            }
            foreach (var kv in Pawns) if (!kid.Contains(kv.Key)) Adults[kv.Key] = kv.Value;
            Mechs = Named(DigestJson.G(b, "mechs"), "id").Take(MaxNames).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            Kinds = Words(ListOf(b, "can_gestate").Select(k => k is JObject ko ? DigestJson.Str(ko, "kind", "None") : PyJson.Str(k)));
            Children = Named(DigestJson.G(b, "children"), "pawn");
            Babies = Named(DigestJson.G(b, "babies"), "pawn");
            Gb = DigestJson.G(b, "genebank") as JObject ?? new JObject();
            Packs = Words(ListOf(Gb, "packs").Select(p => TrailingParens.Replace(PyJson.Str(p), "")));
            Hemogen = DigestJson.G(b, "hemogen") is JObject;
            Genes = ListOf(b, "genes").Count > 0;
            // prisoners for blood donation: a digest list if the mod gives one, else pawns whose role says prisoner
            var pri = Named(DigestJson.G(digest, "prisoners"), "id");
            var hb = DigestJson.G(b, "hemogen") as JObject ?? new JObject();
            foreach (var kv in Named(DigestJson.G(hb, "prisoners"), "id")) pri[kv.Key] = kv.Value;      // the bio block's prisoners who are not donating yet
            foreach (var p in DigestJson.Items(DigestJson.G(digest, "pawns")))
            {
                if (!(p is JObject po)) continue;
                if (DigestJson.Str(po, "role", "").ToLowerInvariant() != "prisoner") continue;
                string ids = DigestJson.Str(po, "id", "").TrimStart('-');
                if (ids.Length == 0 || !ids.All(char.IsDigit)) continue;
                pri[GameData.Name(DigestJson.G(po, "name"))] = DigestJson.PyInt(po["id"]);
            }
            Prisoners = pri;
            Germs = Named(DigestJson.G(b, "xenogerms"), "id");
            ImplantOk = DigestJson.G(b, "may_implant") is JValue mi && mi.Type == JTokenType.Boolean && (bool)mi && Germs.Count > 0;   // the player's setting, and a xenogerm to implant
            Carers = WordsOf(DigestJson.G(b, "carers") is JArray ca ? ca : null);
            Mechanitors = ListOf(b, "mechanitors").Count > 0;
            Tools = Has ? BuildTools() : new List<ToolSpec>();
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        // ---- the tools ------------------------------------------------------------------
        private List<ToolSpec> BuildTools()
        {
            var outl = new List<ToolSpec>();
            if (Mechanitors)
            {
                var ops = new List<string> { "mode", "group" };
                if (Kinds.Count > 0) ops.Add("gestate");
                var ps = new List<Param>
                {
                    new Param("op", "enum", "set a group's mode, move a mech to a group, or start gestating one", choices: ops),
                    new Param("group", "int", "group 1-12 (0 if unused)", lo: 0, hi: 12, required: false),
                    Opt("mode", "mode: the group's mode", Modes),
                };
                if (Mechs.Count > 0) ps.Add(Opt("mech", "group: which mech", Mechs.Keys));
                if (Kinds.Count > 0)
                {
                    ps.Add(Opt("kind", "gestate: which mech", Kinds));
                    ps.Add(new Param("count", "int", "gestate: 1-3 (0 if unused)", lo: 0, hi: 3, required: false));
                }
                ps.Add(WhyParam);
                outl.Add(new ToolSpec("mechs", "Command mechs.", ps));
            }
            var gops = new List<string>();
            if (PyJson.Truthy(DigestJson.G(Gb, "unstored")) || PyJson.Truthy(DigestJson.G(Gb, "genepacks"))) gops.Add("store");
            if (Packs.Count > 0) gops.Add("assemble");
            if (ImplantOk) gops.Add("implant");
            if ((Hemogen || Genes) && Prisoners.Count > 0) gops.Add("donate");
            if (gops.Count > 0 && (Genes || Gb.Count > 0 || Hemogen))
            {
                var ps = new List<Param> { new Param("op", "enum", "store genepacks, assemble or implant a xenogerm, or a prisoner donates blood", choices: gops) };
                if (gops.Contains("assemble"))
                {
                    ps.Add(new Param("genepacks", "text", "assemble: pack names, comma separated", maxLen: 160, required: false));
                    ps.Add(new Param("name", "text", "assemble: the xenogerm's name", maxLen: 40, required: false));
                }
                if (gops.Contains("implant")) ps.Add(Opt("xenogerm", "implant: which xenogerm", Germs.Keys));
                if (gops.Contains("implant") || gops.Contains("assemble"))
                    ps.Add(new Param("plan", "text", "assemble/implant: why, 10-200 chars, the player reads it", maxLen: 200, required: false));
                if (gops.Contains("implant") && Adults.Count > 0) ps.Add(Opt("who", "implant: which colonist", Adults.Keys));
                if (gops.Contains("donate")) ps.Add(Opt("prisoner", "donate: who", Prisoners.Keys));
                ps.Add(WhyParam);
                outl.Add(new ToolSpec("genes", "Genepacks and xenogerms; op donate sets a prisoner to give blood (hemogen) for hemogen-needing colonists.", ps));
            }
            var cops = new List<string>();
            if (Children.Count > 0) cops.Add("schedule");
            if (Babies.Count > 0) cops.Add("feed");
            if ((Children.Count > 0 || Babies.Count > 0) && Adults.Count > 0) cops.Add("care");
            if (cops.Count > 0)
            {
                var ps = new List<Param> { new Param("op", "enum", "set a child's day, who feeds a baby, or a colonist's Childcare priority", choices: cops) };
                var kids = Children.Keys.Concat(Babies.Keys).ToList();
                ps.Add(Opt("child", "schedule/feed: the child or baby", kids));
                if (cops.Contains("schedule")) ps.Add(Opt("preset", "schedule: the day's shape", Presets));
                if (cops.Contains("feed") || cops.Contains("care")) ps.Add(Opt("who", "feed: the feeder; care: the colonist", Adults.Keys));
                if (cops.Contains("feed")) ps.Add(Opt("feed_mode", "feed: when", FeedModes));
                if (cops.Contains("care")) ps.Add(new Param("priority", "int", "care: 1 highest to 4 lowest (0 = 1)", lo: 0, hi: 4, required: false));
                ps.Add(WhyParam);
                outl.Add(new ToolSpec("children", "Children, babies and childcare.", ps));
            }
            return outl;
        }

        // ---- the bodies -----------------------------------------------------------------
        /// <summary>x in (None, "", "none", 0) -> null.</summary>
        private static JToken Get(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken x) || x == null || x.Type == JTokenType.Null) return null;
            if (x.Type == JTokenType.String && ((string)x == "" || (string)x == None)) return null;
            if ((x.Type == JTokenType.Integer || x.Type == JTokenType.Float || x.Type == JTokenType.Boolean) && DigestJson.PyFloat(x) == 0.0) return null;
            return x;
        }

        private static string Text(JToken t) => PyJson.Str(t);

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            (string, JObject) r;
            switch (tool)
            {
                case "mechs": r = DoMechs(v); break;
                case "genes": r = DoGenes(v); break;
                case "children": r = DoChildren(v); break;
                default: throw new PyAttributeError("BioKit has no attribute _" + tool);
            }
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out JToken wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) r.Item2["why"] = why;
            return r;
        }

        private static long Pick(IDictionary<string, long> table, JToken name, string what)
        {
            string n = name == null || name.Type == JTokenType.Null || (name.Type == JTokenType.String && ((string)name == "" || (string)name == None))
                ? null : PyStr.Strip(PyJson.Str(name)).ToLowerInvariant();
            foreach (var kv in table) if (kv.Key.ToLowerInvariant() == n) return kv.Value;
            throw new PyValueError("no " + what + " named " + (name == null || name.Type == JTokenType.Null ? "None" : PyJson.Str(name)) + "; the " + what + "s are: " + string.Join(", ", table.Keys));
        }

        private static JToken Val(JObject v, string k) => v.TryGetValue(k, out JToken t) ? t : null;

        private static string Op(JObject v) { if (!v.TryGetValue("op", out JToken t)) throw new PyKeyError("op"); return PyJson.Str(t); }

        private (string, JObject) DoMechs(JObject v)
        {
            string op = Op(v);
            JToken g = Get(v, "group");
            if (op == "mode")
            {
                JToken mode = Get(v, "mode");
                if (g == null || mode == null || !(mode.Type == JTokenType.String && Modes.Contains((string)mode)))
                    throw new PyValueError("mode needs group 1-12 and mode (" + string.Join(", ", Modes) + ")");
                return ("mech_mode", new JObject { ["group"] = g.DeepClone(), ["mode"] = mode.DeepClone() });
            }
            if (op == "group")
            {
                if (g == null || Get(v, "mech") == null) throw new PyValueError("group needs mech and group 1-12");
                return ("mech_group", new JObject { ["mech"] = Pick(Mechs, Val(v, "mech"), "mech"), ["group"] = g.DeepClone() });
            }
            JToken k = Get(v, "kind");
            if (k == null || !(k.Type == JTokenType.String && Kinds.Contains((string)k))) throw new PyValueError("gestate needs kind, one of: " + string.Join(", ", Kinds));
            JToken count = Get(v, "count");
            return ("mech_gestate", new JObject { ["kind"] = k.DeepClone(), ["count"] = count != null ? count.DeepClone() : new JValue(1) });
        }

        private (string, JObject) DoGenes(JObject v)
        {
            string op = Op(v);
            if (op == "store") return ("genepack", new JObject());
            if (op == "assemble")
            {
                JToken gp = Val(v, "genepacks");
                string raw = gp != null && PyJson.Truthy(gp) ? PyJson.Str(gp) : "";
                var asked = raw.Split(',').Select(x => PyStr.Strip(x)).Where(x => x.Length > 0).ToList();
                var low = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in Packs) low[p.ToLowerInvariant()] = p;
                if (asked.Count == 0 || asked.Any(a => !low.ContainsKey(a.ToLowerInvariant())))
                    throw new PyValueError("assemble needs genepacks from: " + string.Join(", ", Packs));
                JToken nt = Val(v, "name");
                string name = PyStr.Strip(GameData.Clean(nt != null && PyJson.Truthy(nt) ? PyJson.Str(nt) : "", "name", 40));
                var packs = new JArray(asked.Select(a => (object)low[a.ToLowerInvariant()]).ToArray());
                return ("xenogerm", new JObject { ["op"] = "assemble", ["genepacks"] = packs, ["name"] = name.Length > 0 ? name : "xenogerm", ["plan"] = Plan(v) });
            }
            if (op == "implant")
            {
                long x = Pick(Germs, Val(v, "xenogerm"), "xenogerm");
                long who = Pick(Adults.ToDictionary(kv => kv.Key, kv => (long)kv.Value, StringComparer.Ordinal), Val(v, "who"), "colonist");
                return ("xenogerm", new JObject { ["op"] = "implant", ["xenogerm"] = x, ["pawn"] = who, ["plan"] = Plan(v) });
            }
            return ("hemogen", new JObject { ["op"] = "donate", ["prisoner"] = Pick(Prisoners, Val(v, "prisoner"), "prisoner") });
        }

        private static string Plan(JObject v)
        {
            JToken pt = Val(v, "plan");
            string p = PyStr.Strip(GameData.Clean(pt != null && PyJson.Truthy(pt) ? PyJson.Str(pt) : "", "plan", 200));
            if (PyStr.Len(p) < 10) throw new PyValueError("give a plan of 10 to 200 characters that the player can read");
            return p;
        }

        private (string, JObject) DoChildren(JObject v)
        {
            string op = Op(v);
            var adults = Adults.ToDictionary(kv => kv.Key, kv => (long)kv.Value, StringComparer.Ordinal);
            if (op == "care")
            {
                JToken pr = Val(v, "priority");
                return ("child_care", new JObject { ["pawn"] = Pick(adults, Val(v, "who"), "colonist"), ["priority"] = pr != null && PyJson.Truthy(pr) ? pr.DeepClone() : new JValue(1) });
            }
            if (op == "schedule")
            {
                JToken pre = Get(v, "preset");
                if (pre == null || !(pre.Type == JTokenType.String && Presets.Contains((string)pre))) throw new PyValueError("schedule needs preset: " + string.Join(", ", Presets));
                var both = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var kv in Children) both[kv.Key] = kv.Value;
                foreach (var kv in Babies) both[kv.Key] = kv.Value;
                return ("child_schedule", new JObject { ["pawn"] = Pick(both, Val(v, "child"), "child"), ["preset"] = pre.DeepClone() });
            }
            JToken fm = Get(v, "feed_mode");
            JToken mode = fm ?? new JValue("childcare");
            long baby = Pick(Babies, Val(v, "child"), "baby");
            if (Get(v, "who") == null) throw new PyValueError("feed needs who: the colonist who feeds the baby (" + string.Join(", ", Adults.Keys) + ")");
            long feeder = Pick(adults, Val(v, "who"), "colonist");
            return ("feed", new JObject { ["baby"] = baby, ["feeder"] = feeder, ["mode"] = mode.DeepClone() });
        }
    }
}

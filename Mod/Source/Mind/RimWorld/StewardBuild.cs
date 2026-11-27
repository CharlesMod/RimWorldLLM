// Port of games/rimworld/steward_build.py: the steward's builder tools (Vivarium phase 2): build, zone, designate and bill.
//
// The model states intent ("a medium bedroom", "a stove in room 12", "a potato field", "mine steel"); the mod's planners pick
// the cells. The tools are built from the digest's "build" block (docs/VIVARIUM.md, "Phase 2"): a tool, a kind or an enum value
// appears only when the digest holds what it needs, so a digest without a build block has none of them.
//
// Optional parameters: the grammar protocol always writes every parameter, so an optional enum carries the word "none" (the
// model's way to say "unused"), an optional number carries 0; the json and native protocols may simply leave them out. Both
// forms mean the same here.
//
// Each call maps to one POST /act/<verb>: build -> room | build, zone -> zone, designate -> designate (mine, chop, harvest,
// haul only: deconstruct and cancel need a place or a target, and tearing down is the player's call, so the steward does not
// get them), bill -> bill. player_asked is never sent.
//
// Differences from Python: a "home" or a digest list of the wrong type (where Python would raise AttributeError/TypeError) is
// treated as absent; str.lower() is ToLowerInvariant.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The four builder tools for one digest, and the body of each call.</summary>
    public sealed class BuildKit
    {
        public const string None = "none";
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since meals are running low); " +
            "it is shown to the player beside what you did", maxLen: WhyMax, required: false, sameCall: true);   // as StewardSession's (a copy, as the Python)

        public static readonly string[] ToolNames = { "build", "zone", "designate", "bill" };
        public static readonly string[] Purposes = { "bedroom", "barracks", "kitchen", "dining", "storage", "freezer", "hospital", "workshop", "rec" };   // BuildPure room purposes
        public static readonly string[] Sizes = { "small", "medium", "large" };
        public static readonly string[] Presets = { "food", "meds", "raw", "all" };
        public static readonly string[] BillOps = { "add", "remove", "suspend", "resume" };
        public const int MaxRooms = 12, MaxBenches = 8;
        public static readonly string[] LivingRooms = { "bedroom", "barracks", "hospital", "dining", "rec", "prison" };   // never cool these as a freezer

        public readonly bool Has;
        /// <summary>id (as text) -> short description, in the digest's order.</summary>
        public readonly List<KeyValuePair<string, string>> Rooms;
        public readonly List<KeyValuePair<string, string>> Benches;
        public readonly Dictionary<string, List<string>> Recipes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly List<string> Things, Stuff, Plants, Ores;
        /// <summary>The stockpile presets: waste (a wastepack-only stockpile) is added with a Biotech bio block.</summary>
        public readonly List<string> PresetList;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        private static Param Opt(string name, string desc, IEnumerable<string> choices)
            => new Param(name, "enum", desc, choices: new[] { None }.Concat(choices), required: false);

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

        /// <summary>Cleaned, de-duplicated words from a digest list of strings (str(x) of each item).</summary>
        private static List<string> WordsOf(IEnumerable<JToken> items)
        {
            var outl = new List<string>();
            foreach (var x in items)
            {
                string w = PyStr.Strip(GameData.Clean(PyJson.Str(x), "build", 40));
                if (w.Length > 0 && !outl.Contains(w)) outl.Add(w);
            }
            return outl;
        }

        private static bool IsZeroOrEmpty(JToken t)
            => t == null || t.Type == JTokenType.Null || (t.Type == JTokenType.String && (string)t == "")
               || (t.Type == JTokenType.Integer && Convert.ToInt64(((JValue)t).Value, CultureInfo.InvariantCulture) == 0)
               || (t.Type == JTokenType.Float && (double)t == 0.0) || (t.Type == JTokenType.Boolean && !(bool)t);

        /// <summary>id (as text) -> short description for the digest's rooms or benches.</summary>
        private static List<KeyValuePair<string, string>> Ids(JToken items, string roleKey)
        {
            var outl = new List<KeyValuePair<string, string>>();
            foreach (var x in Iter(items))
            {
                var xo = x as JObject;
                if (xo == null) continue;
                JToken id = DigestJson.G(xo, "id");
                if (IsZeroOrEmpty(id)) continue;
                string i;
                try { i = DigestJson.PyInt(id).ToString(CultureInfo.InvariantCulture); }
                catch (Exception e) when (e is PyTypeError || e is PyValueError) { continue; }
                if (outl.Any(p => p.Key == i)) continue;
                JToken r = DigestJson.G(xo, roleKey);
                if (!DigestJson.Truthy(r)) r = DigestJson.G(xo, "label");
                if (!DigestJson.Truthy(r)) r = null;
                outl.Add(new KeyValuePair<string, string>(i, PyStr.Strip(GameData.Clean(r == null ? "" : PyJson.Str(r), "build", 40))));
            }
            return outl;
        }

        public BuildKit(JObject digest)
        {
            var b = DigestJson.G(digest, "build") as JObject;
            Has = b != null;
            b = b ?? new JObject();
            Rooms = Ids(DigestJson.G(b, "rooms"), "role").Take(MaxRooms).ToList();
            Benches = Ids(DigestJson.G(b, "benches"), "label").Take(MaxBenches).ToList();
            foreach (var x in Iter(DigestJson.G(b, "benches")))
            {
                var xo = x as JObject;
                if (xo != null && Benches.Any(p => p.Key == PyJson.Str(DigestJson.G(xo, "id"))) && DigestJson.G(xo, "recipes") is JArray rec)
                    Recipes[PyJson.Str(DigestJson.G(xo, "id"))] = WordsOf(rec);
            }
            Things = WordsOf(Iter(DigestJson.G(b, "can_build")));
            Stuff = WordsOf(Iter(DigestJson.G(b, "stuff")).Select(s => s is JObject so ? (so.TryGetValue("stuff", out JToken v) ? v : null) : s));
            Plants = WordsOf(Iter(DigestJson.G(b, "plants")));
            Ores = WordsOf(Iter(DigestJson.G(b, "ores")).Select(o => o is JObject oo ? (oo.TryGetValue("ore", out JToken v) ? v : null) : o));
            PresetList = Presets.Concat(DigestJson.G(digest, "bio") is JObject ? new[] { "waste" } : new string[0]).ToList();
            Tools = Has ? BuildTools() : new List<ToolSpec>();
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        private bool HasRoom(string id) => Rooms.Any(p => p.Key == id);
        private bool HasBench(string id) => Benches.Any(p => p.Key == id);
        private string RoomRole(string id) => Rooms.Where(p => p.Key == id).Select(p => p.Value).FirstOrDefault() ?? "";

        // ---- the tools ------------------------------------------------------------------
        private Param RoomsParam(string desc)
        {
            string roles = string.Join(", ", Rooms.Select(p => p.Value.Length > 0 ? p.Key + " " + p.Value : p.Key));
            return Opt("room", desc + " (room id: " + roles + ")", Rooms.Select(p => p.Key));
        }

        private List<ToolSpec> BuildTools()
        {
            var outl = new List<ToolSpec>();
            var stuff = Stuff.Count > 0 ? new List<Param> { Opt("stuff", "material", Stuff) } : new List<Param>();
            var kinds = new List<string> { "room" };
            if (Things.Count > 0) kinds.Add("thing");
            var ps = new List<Param>
            {
                new Param("kind", "enum", "room: walls and a door; thing: one building", choices: kinds),
                Opt("purpose", "room: its use", Purposes), Opt("size", "room: how big", Sizes),
            };
            if (Things.Count > 0) ps.Add(Opt("thing", "thing: what", Things));
            if (Rooms.Count > 0 && Things.Count > 0)
                ps.Add(RoomsParam("thing: inside this existing room (a room planned this turn has no id yet), else near home"));
            ps.AddRange(stuff);
            ps.Add(WhyParam);
            outl.Add(new ToolSpec("build", "Plan a room or one building; the planner picks the cells.", ps));

            var zk = new List<string>();
            if (Plants.Count > 0) zk.Add("grow");
            zk.Add("stockpile"); zk.Add("dump");
            var zp = new List<Param> { new Param("kind", "enum", "grow: a field; stockpile: storage; dump: junk", choices: zk) };
            if (Plants.Count > 0) zp.Add(Opt("plant", "grow: the crop", Plants));
            zp.Add(Opt("preset", "stockpile: what it takes", PresetList));
            zp.Add(Opt("size", "how big", Sizes));
            if (Rooms.Count > 0) zp.Add(RoomsParam("stockpile: inside this room"));
            zp.Add(WhyParam);
            outl.Add(new ToolSpec("zone", "Mark a growing zone, stockpile or dump; the planner picks the cells.", zp));

            var dk = new List<string> { "chop", "harvest", "haul" };
            if (Ores.Count > 0) dk.Add("mine");
            var dp = new List<Param> { new Param("kind", "enum", "mine: ore rock; chop: trees in the way; harvest: wild plants; haul: loose items", choices: dk) };
            if (Ores.Count > 0) dp.Add(Opt("ore", "mine: resource", Ores));
            dp.Add(WhyParam);
            outl.Add(new ToolSpec("designate", "Order mining, chopping, harvesting or hauling near home.", dp));

            if (Benches.Count > 0)
            {
                string names = string.Join(", ", Benches.Select(p => p.Value.Length > 0 ? p.Key + " " + p.Value : p.Key));
                outl.Add(new ToolSpec("bill", "Add, remove, suspend or resume a workbench bill.", new[]
                {
                    new Param("op", "enum", "what to do", choices: BillOps),
                    new Param("bench", "enum", "the workbench id (" + names + ")", choices: Benches.Select(p => p.Key)),
                    new Param("recipe", "text", "the bench's recipe", maxLen: 60),
                    new Param("count", "int", "add: make this many, 0 if unused", lo: 0, hi: 99, required: false),
                    new Param("until", "int", "add: keep this many in stock, 0 if unused", lo: 0, hi: 999, required: false),
                    WhyParam,
                }));
            }
            return outl;
        }

        // ---- the bodies -----------------------------------------------------------------
        /// <summary>v.get(k), or null when it is missing, null, "", "none" or 0.</summary>
        private static JToken Get(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken x) || IsZeroOrEmpty(x)) return null;
            if (x.Type == JTokenType.String && (string)x == None) return null;
            return x;
        }

        private static string Text(JToken t) => PyJson.Str(t);

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            JObject b;
            string verb;
            switch (tool)
            {
                case "build": (verb, b) = DoBuild(v); break;
                case "zone": (verb, b) = DoZone(v); break;
                case "designate": (verb, b) = DoDesignate(v); break;
                case "bill": (verb, b) = DoBill(v); break;
                default: throw new PyAttributeError("BuildKit has no attribute _" + tool);
            }
            JToken wt;
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) b["why"] = why;
            return (verb, b);
        }

        private long? RoomId(JObject v)
        {
            JToken r = Get(v, "room");
            if (r == null) return null;
            if (!HasRoom(Text(r))) throw new PyValueError("no such room; the rooms are: " + string.Join(", ", Rooms.Select(p => p.Key)));
            return DigestJson.PyInt(r);
        }

        private void StuffInto(JObject v, JObject b)
        {
            JToken s = Get(v, "stuff");
            if (s == null) return;
            if (!(s.Type == JTokenType.String && Stuff.Contains((string)s))) throw new PyValueError("no such material; the stock is: " + string.Join(", ", Stuff));
            b["stuff"] = s.DeepClone();
        }

        private (string, JObject) DoBuild(JObject v)
        {
            if (Text(v["kind"]) == "room")
            {
                JToken p = Get(v, "purpose"), s = Get(v, "size");
                if (p == null || s == null)
                    throw new PyValueError("a room needs purpose (" + string.Join(", ", Purposes) + ") and size (" + string.Join(", ", Sizes) + ")");
                // thing/room are ignored for a room: the grammar writes every parameter and the 26B fills them with what it
                // means to put inside later ("cooler" for a freezer); refusing cost a step each time in the live check.
                var rb = new JObject { ["purpose"] = p.DeepClone(), ["size"] = s.DeepClone() };
                StuffInto(v, rb);
                return ("room", rb);
            }
            JToken t = Get(v, "thing");
            if (t == null || !(t.Type == JTokenType.String && Things.Contains((string)t)))
                throw new PyValueError("a thing needs thing, one of: " + string.Join(", ", Things));
            var b = new JObject { ["thing"] = t.DeepClone() };
            StuffInto(v, b);
            long? r = RoomId(v);
            if ((string)t == "cooler")
            {
                // live check: the 26B put the freezer's cooler into an existing bedroom/barracks in the same turn it planned
                // the freezer. A cooler goes into its freezer once that room is built and listed.
                string role = r.HasValue ? RoomRole(r.Value.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant() : "";
                if (!r.HasValue || LivingRooms.Any(w => role.Contains(w)))
                    throw new PyValueError("a cooler goes in the wall of a freezer or storeroom listed in ROOMS; if the freezer is "
                        + "only planned, place the cooler in a later turn once it shows in ROOMS");
            }
            if (r.HasValue) b["room"] = r.Value;
            return ("build", b);
        }

        private (string, JObject) DoZone(JObject v)
        {
            string k = Text(v["kind"]);
            var b = new JObject { ["kind"] = k };
            if (k == "grow")
            {
                JToken p = Get(v, "plant");
                if (p == null || !(p.Type == JTokenType.String && Plants.Contains((string)p)))
                    throw new PyValueError("a growing zone needs plant, one of: " + string.Join(", ", Plants));
                b["plant"] = p.DeepClone();
            }
            else if (Get(v, "plant") != null) throw new PyValueError("plant is only for a growing zone");
            JToken pre = Get(v, "preset");
            if (pre != null && k == "stockpile") b["preset"] = pre.DeepClone();
            JToken s = Get(v, "size");
            if (s != null) b["size"] = s.DeepClone();
            long? r = RoomId(v);
            if (r.HasValue)
            {
                if (k != "stockpile") throw new PyValueError("room is only for a stockpile");
                b["room"] = r.Value;
            }
            return ("zone", b);
        }

        private (string, JObject) DoDesignate(JObject v)
        {
            string k = Text(v["kind"]);
            var b = new JObject { ["kind"] = k };
            if (k == "mine")
            {
                JToken o = Get(v, "ore");
                if (o == null || !(o.Type == JTokenType.String && Ores.Contains((string)o)))
                    throw new PyValueError("mining needs ore, one of: " + string.Join(", ", Ores));
                b["ore"] = o.DeepClone();
            }
            else if (Get(v, "ore") != null) throw new PyValueError("ore is only for mining");
            return ("designate", b);
        }

        private (string, JObject) DoBill(JObject v)
        {
            string bench = Text(v["bench"]);
            if (!HasBench(bench)) throw new PyValueError("no such workbench; the benches are: " + string.Join(", ", Benches.Select(p => p.Key)));
            string recipe = Text(v["recipe"]), op = Text(v["op"]);
            var b = new JObject { ["op"] = op, ["bench"] = long.Parse(bench, CultureInfo.InvariantCulture), ["recipe"] = recipe };
            if (op == "add")
            {
                if (Recipes.TryGetValue(bench, out List<string> known) && known.Count > 0)
                {
                    string want = PyStr.Strip(recipe).ToLowerInvariant();
                    string hit = known.FirstOrDefault(r => r.ToLowerInvariant() == want);
                    if (hit == null) throw new PyValueError("bench " + bench + " cannot make " + recipe + "; its recipes are: " + string.Join(", ", known));
                    b["recipe"] = hit;
                }
                JToken count = Get(v, "count"), until = Get(v, "until");
                if (count != null && until != null) throw new PyValueError("give count or until, not both");
                if (count != null) b["count"] = count.DeepClone();
                if (until != null) b["until"] = until.DeepClone();
            }
            return ("bill", b);
        }
    }
}

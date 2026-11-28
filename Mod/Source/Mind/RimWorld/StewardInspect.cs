// The narrator's read tools about the world (RimWorldLLM tools: inspect, find, survey). All GETs, none an action, so none is in
// Acting; a routine turn offers them when the digest has pawns, a combat turn offers survey + inspect.
//
//     inspect(target)               GET /inspect?target=<text>
//     find(what, near?, item?)      GET /find?what=<kind>&near=<text>&item=<text>
//     survey(what)                  GET /survey?what=stock|threats
//
// Each answers {"text": "..."} (the route caps it near 600 chars); a refusal carries the reason as {"refused": ...}. At most
// MaxReads of the three per turn (one StewardSession per turn holds the counter).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    public sealed class InspectKit
    {
        public static readonly string[] FindKinds =
        {
            "item", "wounded", "idle", "hostiles", "fires", "corpses", "forbidden", "bed_free", "door_out", "tameable",
            "rotting", "sick", "downed", "prisoners", "animals", "guests", "building",
        };
        public static readonly string[] SurveyKinds = { "stock", "threats" };
        public static readonly string[] Names = { "inspect", "find", "survey" };
        public const int MaxReads = 4;
        public const string NoMore = "no more reads this turn; act now";
        public const int TextMax = 700;

        public readonly bool Has;
        public readonly List<ToolSpec> Tools;
        private int _reads;
        /// <summary>The reads answered or tried this turn.</summary>
        public int Reads => _reads;

        public InspectKit(JObject digest)
        {
            Has = DigestJson.Items(DigestJson.G(digest, "pawns")).Any();
            Tools = Has ? BuildTools() : new List<ToolSpec>();
        }

        private static List<ToolSpec> BuildTools() => new List<ToolSpec>
        {
            new ToolSpec("inspect", "Read details of one colonist, animal, prisoner, room, building, item or zone (not an action).",
                new[] { new Param("target", "text", "a name, room label, or #id", maxLen: 40) }),
            new ToolSpec("find", "List matching things with #ids and where they are (not an action).",
                new[]
                {
                    new Param("what", "enum", "kind of thing", choices: FindKinds),
                    new Param("near", "text", "pawn, room or #id to measure from; default home", maxLen: 40, required: false),
                    new Param("item", "text", "item or building label, for what=item or building", maxLen: 40, required: false),
                }),
            new ToolSpec("survey", "Overview of the colony (not an action): stock = resources and days of food; threats = hostiles, fires, hazards.",
                new[] { new Param("what", "enum", "stock or threats", choices: SurveyKinds) }),
        };

        private static string Opt(JObject v, string k)
        {
            string s = PyStr.Strip(v.TryGetValue(k, out JToken t) && PyJson.Truthy(t) ? PyJson.Str(t) : "");
            return s.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : s;
        }

        /// <summary>(path, query) of a read call; PyValueError for arguments that do not read.</summary>
        public (string path, List<KeyValuePair<string, string>> query) Request(string tool, JObject v)
        {
            var q = new List<KeyValuePair<string, string>>();
            switch (tool)
            {
                case "inspect":
                {
                    string target = Opt(v, "target");
                    if (target.Length == 0) throw new PyValueError("inspect needs a target: a name, a room label or #id");
                    q.Add(new KeyValuePair<string, string>("target", target));
                    return ("/inspect", q);
                }
                case "find":
                {
                    string what = Opt(v, "what").ToLowerInvariant();
                    if (Array.IndexOf(FindKinds, what) < 0) throw new PyValueError("what is one of: " + string.Join(", ", FindKinds));
                    q.Add(new KeyValuePair<string, string>("what", what));
                    string near = Opt(v, "near"), item = Opt(v, "item");
                    if (near.Length > 0) q.Add(new KeyValuePair<string, string>("near", near));
                    if (item.Length > 0) q.Add(new KeyValuePair<string, string>("item", item));
                    return ("/find", q);
                }
                case "survey":
                {
                    string what = Opt(v, "what").ToLowerInvariant();
                    if (Array.IndexOf(SurveyKinds, what) < 0) throw new PyValueError("what is stock or threats");
                    q.Add(new KeyValuePair<string, string>("what", what));
                    return ("/survey", q);
                }
                default: throw new PyValueError("unknown tool " + tool);
            }
        }

        /// <summary>Run one read: {"result": text}, {"refused": reason} or {"error": ...}. The fifth read of a turn is an error.</summary>
        public async Task<JObject> RunAsync(string tool, JObject v, IBridge bridge)
        {
            if (_reads >= MaxReads) return new JObject { ["error"] = NoMore };
            try
            {
                var (path, q) = Request(tool, v);
                _reads++;
                JObject r = await bridge.LookAsync(path, q).ConfigureAwait(false);
                string text = r != null && r.TryGetValue("text", out JToken t) && t.Type == JTokenType.String ? PyStr.Strip((string)t) : "";
                if (text.Length == 0) text = "nothing to report";
                if (PyStr.Len(text) > TextMax) text = PyStr.Head(text, TextMax - 3) + "...";
                return new JObject { ["result"] = text };
            }
            catch (PyValueError e) { return new JObject { ["error"] = e.Message }; }
            catch (BridgeError e)
            {
                if (e.Code == "refused") return new JObject { ["refused"] = e.Detail.Length > 0 ? e.Detail : e.Code };
                return new JObject { ["error"] = PyStr.Head((e.Code + ": " + e.Detail).Trim(':', ' '), 160) };
            }
        }
    }
}

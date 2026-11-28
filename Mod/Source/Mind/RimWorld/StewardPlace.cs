// The steward's place tool: furnish a room, power, climate and ship parts in one op-enum tool (POST /act/place).
// Gated by the digest's build block exactly like BuildKit: a digest without one has no place tool.
// Optional parameters carry "none" (enums) or are omitted/empty (text); both mean unused.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    public sealed class PlaceKit
    {
        public const string None = "none";
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since meals are running low); " +
            "it is shown to the player beside what you did", maxLen: WhyMax, required: false, sameCall: true);

        public readonly bool Has;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        public PlaceKit(JObject digest)
        {
            Has = DigestJson.G(digest, "build") is JObject;
            Tools = Has ? new List<ToolSpec> { Spec() } : new List<ToolSpec>();
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        private static ToolSpec Spec() => new ToolSpec("place", "Furnish a room, add power, heating, cooling or ship parts; the game picks the cells.", new[]
        {
            new Param("op", "enum", "what to do", choices: PlacePure.Ops),
            new Param("what", "text", "furnish: a building word from the build list (e.g. research bench); generator: wood|chemfuel|solar|wind|geothermal; ship: beam|reactor|engine|casket|sensor|core", maxLen: 40, required: false),
            new Param("at", "text", "a room label, 'room N', or #id", maxLen: 40, required: false),
            new Param("temp", "enum", "heater, cooler, target: the temperature", choices: new[] { None }.Concat(PlacePure.TempWords), required: false),
            WhyParam,
        });

        private static string Get(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken x) || x == null || x.Type == JTokenType.Null) return null;
            string s = PyStr.Strip(PyJson.Str(x));
            return s.Length == 0 || s == "0" || s == None ? null : s;
        }

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            if (tool != "place") throw new PyAttributeError("PlaceKit has no attribute _" + tool);
            string op = Get(v, "op") ?? "";
            string what = Get(v, "what"), at = Get(v, "at"), temp = Get(v, "temp");
            if (what != null) what = PyStr.Strip(GameData.Clean(what, "build", 40));
            if (at != null) at = PyStr.Strip(GameData.Clean(at, "build", 40));
            if (what != null && what.Length == 0) what = null;
            if (at != null && at.Length == 0) at = null;
            string lw = what == null ? null : what.ToLowerInvariant();
            switch (op)
            {
                case "furnish":
                    if (what == null || at == null) throw new PyValueError("furnish needs what (a building from the build list) and at (a room label, 'room N' or #id)");
                    break;
                case "generator":
                    if (lw == null || !PlacePure.GeneratorKinds.Contains(lw)) throw new PyValueError("a generator needs what, one of: " + string.Join(", ", PlacePure.GeneratorKinds));
                    what = lw; break;
                case "connect":
                    if (at == null) throw new PyValueError("connect needs at: a room label, 'room N' or #id of the building to wire");
                    break;
                case "heater": case "cooler":
                    if (at == null) throw new PyValueError(op + " needs at: a room label or 'room N'");
                    break;
                case "target":
                    if (at == null || temp == null) throw new PyValueError("target needs at (a room) and temp, one of: " + string.Join(", ", PlacePure.TempWords));
                    break;
                case "ship":
                    if (lw == "launch") throw new PyValueError("the keeper launches the ship");
                    if (lw == null || !PlacePure.ShipParts.Contains(lw)) throw new PyValueError("ship needs what, one of: " + string.Join(", ", PlacePure.ShipParts));
                    what = lw; break;
                case "battery": case "switch": break;
                default: throw new PyValueError("op must be one of: " + string.Join(", ", PlacePure.Ops));
            }
            var b = new JObject { ["op"] = op };
            if (what != null) b["what"] = what;
            if (at != null) b["at"] = at;
            if (temp != null) b["temp"] = temp;
            JToken wt;
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) b["why"] = why;
            return ("place", b);
        }
    }
}

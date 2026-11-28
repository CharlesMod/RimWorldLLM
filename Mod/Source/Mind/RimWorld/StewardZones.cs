// The steward's rezone tool: change an existing zone (a stockpile's priority and what it takes, the crop of a field or of the
// hydroponics basins). It exists only when the digest's ZONES lists a zone or a hydroponics basin. The model names the zone by its
// digest id ("#4") or "hydroponics", never by coordinates. One call maps to POST /act/rezone; the game side resolves the words.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The rezone tool for one digest, and the body of its call.</summary>
    public sealed class ZonesKit
    {
        public const string None = "none";
        public const int WhyMax = 80, MaxZones = 10, MaxRooms = 12;
        public const string Hydro = "hydroponics";
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because (for example: since meals are running low); " +
            "it is shown to the player beside what you did", maxLen: WhyMax, required: false, sameCall: true);

        public readonly bool Has;
        /// <summary>The zones of the digest's ZONES line: "#id" (or hydroponics) -> kind.</summary>
        public readonly List<KeyValuePair<string, string>> Zones = new List<KeyValuePair<string, string>>();
        public readonly List<string> Plants;
        /// <summary>The floor tool: the digest's room ids (with roles) and the floor words the game can build now; it needs both.</summary>
        public readonly List<KeyValuePair<string, string>> Rooms = new List<KeyValuePair<string, string>>();
        public readonly List<string> Floors = new List<string>();
        public readonly bool HasFloors;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        public ZonesKit(JObject digest)
        {
            var b = DigestJson.G(digest, "build") as JObject;
            var zs = b == null ? null : DigestJson.G(b, "zones") as JArray;
            if (zs != null)
                foreach (var z in zs.OfType<JObject>())
                {
                    string kind = PyStr.Strip(GameData.Clean(PyJson.Str(DigestJson.G(z, "kind")), "build", 40));
                    string key;
                    if (kind == Hydro) key = Hydro;
                    else
                    {
                        long id;
                        try { id = DigestJson.PyInt(DigestJson.G(z, "id")); }
                        catch (Exception e) when (e is PyTypeError || e is PyValueError) { continue; }
                        if (id <= 0) continue;
                        key = "#" + id.ToString(CultureInfo.InvariantCulture);
                    }
                    if (kind.Length == 0 || Zones.Any(p => p.Key == key)) continue;
                    Zones.Add(new KeyValuePair<string, string>(key, kind));
                }
            Zones = Zones.Take(MaxZones).ToList();
            Plants = new List<string>();
            if (b != null && DigestJson.G(b, "plants") is JArray pa)
                foreach (var x in pa)
                {
                    string w = PyStr.Strip(GameData.Clean(PyJson.Str(x), "build", 40));
                    if (w.Length > 0 && w != None && !Plants.Contains(w)) Plants.Add(w);
                }
            Has = Zones.Count > 0;
            if (b != null)
            {
                if (DigestJson.G(b, "rooms") is JArray ra)
                    foreach (var r in ra.OfType<JObject>())
                    {
                        long id;
                        try { id = DigestJson.PyInt(DigestJson.G(r, "id")); }
                        catch (Exception e) when (e is PyTypeError || e is PyValueError) { continue; }
                        string key = id.ToString(CultureInfo.InvariantCulture);
                        if (id <= 0 || Rooms.Any(p => p.Key == key)) continue;
                        JToken role = DigestJson.G(r, "role");
                        Rooms.Add(new KeyValuePair<string, string>(key, PyStr.Strip(GameData.Clean(DigestJson.Truthy(role) ? PyJson.Str(role) : "", "build", 40))));
                    }
                if (Rooms.Count > MaxRooms) Rooms.RemoveRange(MaxRooms, Rooms.Count - MaxRooms);
                if (DigestJson.G(b, "floors") is JArray fa)
                    foreach (var x in fa)
                    {
                        string w = PyStr.Strip(GameData.Clean(PyJson.Str(x), "build", 40));
                        if (w.Length > 0 && w != None && w != FloorsPure.Smooth && !Floors.Contains(w)) Floors.Add(w);
                    }
            }
            HasFloors = Rooms.Count > 0 && Floors.Count > 0;
            Tools = new List<ToolSpec>();
            if (Has) Tools.AddRange(BuildTools());
            if (HasFloors) Tools.Add(FloorTool());
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        private bool AnyKind(string k) => Zones.Any(p => p.Value == k);
        private string KindOf(string key) => Zones.Where(p => p.Key == key).Select(p => p.Value).FirstOrDefault();

        private List<ToolSpec> BuildTools()
        {
            var ps = new List<Param> { new Param("zone", "enum", "from ZONES", choices: Zones.Select(p => p.Key)) };
            bool stock = AnyKind("stockpile");
            if (stock)
            {
                ps.Add(new Param("priority", "enum", "stockpile: hauling priority", choices: new[] { None }.Concat(ZonesPure.Priorities), required: false));
                ps.Add(new Param("allow", "text", "stockpile: items to take, comma list (meals, steel)", maxLen: 80, required: false));
                ps.Add(new Param("deny", "text", "stockpile: items to refuse, comma list", maxLen: 80, required: false));
            }
            if (Plants.Count > 0 && (AnyKind("grow") || AnyKind(Hydro)))
                ps.Add(new Param("plant", "enum", "field or hydroponics: new crop", choices: new[] { None }.Concat(Plants), required: false));
            ps.Add(WhyParam);
            return new List<ToolSpec> { new ToolSpec("rezone", "Change a zone: stockpile priority and filter, or a crop.", ps) };
        }

        private ToolSpec FloorTool()
        {
            string roles = string.Join(", ", Rooms.Select(p => p.Value.Length > 0 ? p.Key + " " + p.Value : p.Key));
            var ps = new List<Param>
            {
                new Param("room", "enum", "room id: " + roles, choices: Rooms.Select(p => p.Key)),
                new Param("kind", "enum", "floor to lay, or smooth the rough stone", choices: Floors.Concat(new[] { FloorsPure.Smooth })),
                WhyParam
            };
            return new ToolSpec("floor", "Lay a floor in a room.", ps);
        }

        private static JToken Get(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken x) || x == null || x.Type == JTokenType.Null) return null;
            if (x.Type == JTokenType.String && ((string)x == "" || ((string)x).Trim().ToLowerInvariant() == None)) return null;
            return x;
        }

        private (string verb, JObject body) FloorBody(JObject v)
        {
            string room = PyStr.Strip(PyJson.Str(v["room"]));
            if (!Rooms.Any(p => p.Key == room)) throw new PyValueError("no such room; the rooms are: " + string.Join(", ", Rooms.Select(p => p.Key)));
            string kind = PyStr.Strip(GameData.Clean(PyJson.Str(v["kind"]), "build", 40));
            if (kind != FloorsPure.Smooth && !Floors.Contains(kind)) throw new PyValueError("no such floor; the floors are: " + string.Join(", ", Floors.Concat(new[] { FloorsPure.Smooth })));
            var b = new JObject { ["room"] = long.Parse(room, CultureInfo.InvariantCulture), ["kind"] = kind };
            JToken wt;
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) b["why"] = why;
            return ("floor", b);
        }

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            if (tool == "floor") return FloorBody(v);
            if (tool != "rezone") throw new PyAttributeError("ZonesKit has no attribute _" + tool);
            string zone = PyStr.Strip(PyJson.Str(v["zone"]));
            string kind = KindOf(zone);
            if (kind == null) throw new PyValueError("no such zone; the zones are: " + string.Join(", ", Zones.Select(p => p.Key)));
            var b = new JObject { ["zone"] = zone };
            foreach (var k in new[] { "priority", "allow", "deny", "plant" })
            {
                JToken t = Get(v, k);
                if (t == null) continue;
                string s = PyStr.Strip(GameData.Clean(PyJson.Str(t), "build", 200));
                if (s.Length > 0) b[k] = s;
            }
            var a = new RezoneArgs { Zone = zone, Priority = b["priority"] == null ? null : (string)b["priority"], Plant = b["plant"] == null ? null : (string)b["plant"] };
            if (b["allow"] != null) a.Allow = ZonesPure.SplitWords((string)b["allow"]);
            if (b["deny"] != null) a.Deny = ZonesPure.SplitWords((string)b["deny"]);
            string wrong = ZonesPure.WrongField(kind == Hydro ? Hydro : kind == "grow" ? "grow" : "stockpile", a);
            if (wrong != null) throw new PyValueError(wrong);
            JToken wt;
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) b["why"] = why;
            return ("rezone", b);
        }
    }
}

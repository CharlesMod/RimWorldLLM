using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The steward's DECLARED tool list: every tool the game can offer, in a fixed order, whatever this turn's digest holds, with the
    /// game-dependent enums (pawn names, rooms, beds, letters, ...) described in words. The system text and the native request carry this
    /// list, so they are byte-identical from turn to turn and the engine's prompt cache keeps the whole prefix. What is callable now goes in the
    /// turn's AVAILABLE NOW line (see AvailableNow); the grammar and the checks still use the real tools of the turn.
    ///
    /// The catalog is built from two synthetic digests, A and B, that differ in every name and id. A param whose choices differ between them is
    /// data-driven (an Open param); the rest keep their words. A test asserts that A and B give the same tools, params and descriptions.</summary>
    public static class StewardCatalog
    {
        private static readonly object Gate = new object();
        private static List<ToolSpec> _routine, _advisor;

        /// <summary>The declared tools of a routine/playing turn (advisor false) or an advisor turn, then the board tools and the look tool.</summary>
        public static IReadOnlyList<ToolSpec> Tools(bool advisor)
        {
            lock (Gate)
            {
                if (_routine == null) Build();
                return advisor ? _advisor : _routine;
            }
        }

        private static void Build()
        {
            _routine = Merge(false);
            // the advisor may use the session's tools (no choose) plus note, add_order and do_nothing; the look tool is allowed on both
            var keep = new HashSet<string>(ForVariant(0, true).Select(t => t.Name).Where(n => !StewardBoard.BoardToolNames.Contains(n) && n != VisionKit.Name), StringComparer.Ordinal);
            foreach (var n in new[] { "note", "add_order", "do_nothing", VisionKit.Name }) keep.Add(n);
            _advisor = _routine.Where(t => keep.Contains(t.Name)).ToList();
        }

        /// <summary>A tool list for one digest variant: the session tools (nothing dropped), the board tools, the look tool.</summary>
        internal static List<ToolSpec> ForVariant(int v, bool advisor)
        {
            var s = new StewardSession(Digest(v), null, advisor, false, catalog: true);
            var tools = s.Tools.ToList();
            tools.AddRange(StewardBoard.BoardTools(Board(v), advisor));
            tools.Add(VisionKit.CatalogSpec());
            return tools;
        }

        private static List<ToolSpec> Merge(bool advisor)
        {
            var a = ForVariant(0, advisor);
            var b = ForVariant(1, advisor);
            if (a.Count != b.Count || !a.Select(t => t.Name).SequenceEqual(b.Select(t => t.Name)))
                throw new InvalidOperationException("the steward catalog variants differ in their tools");
            var outl = new List<ToolSpec>();
            for (int i = 0; i < a.Count; i++)
            {
                var ps = new List<Param>();
                if (a[i].Params.Count != b[i].Params.Count) throw new InvalidOperationException("the steward catalog variants differ in the params of " + a[i].Name);
                for (int j = 0; j < a[i].Params.Count; j++)
                {
                    var pa = a[i].Params[j];
                    var pb = b[i].Params[j];
                    if (pa.Name != pb.Name || pa.Kind != pb.Kind) throw new InvalidOperationException("the steward catalog variants differ in " + a[i].Name + "." + pa.Name);
                    if (pa.Kind == "enum" && !pa.Choices.SequenceEqual(pb.Choices))
                        ps.Add(pa.AsOpen(pa.Choices.Intersect(pb.Choices)));
                    else ps.Add(pa);
                }
                outl.Add(new ToolSpec(a[i].Name, a[i].Description, ps, a[i].ResultScales, a[i].EndsTurn));
            }
            return outl;
        }

        /// <summary>The line that opens the turn's last message: the tools callable now, and the values of the data-driven params.</summary>
        public static string AvailableNow(IEnumerable<ToolSpec> callable, IReadOnlyList<ToolSpec> declared)
        {
            var calls = callable.ToList();
            var open = new List<(string Param, string Tool, List<string> Values)>();
            foreach (var t in calls)
            {
                var d = declared.FirstOrDefault(x => x.Name == t.Name);
                if (d == null) continue;
                foreach (var p in t.Params)
                {
                    var dp = d.Params.FirstOrDefault(x => x.Name == p.Name);
                    if (dp == null || !dp.Open) continue;
                    var vals = p.Choices.Where(c => !dp.Choices.Contains(c)).ToList();
                    open.Add((p.Name, t.Name, vals));
                }
            }
            var sb = new System.Text.StringBuilder("AVAILABLE NOW: " + string.Join(", ", calls.Select(t => t.Name)));
            // one entry per distinct value list; a param name shared by lists that differ carries its tool name
            var byList = new List<(List<string> Names, List<string> Values)>();
            foreach (var g in open.GroupBy(o => o.Param))
            {
                bool same = g.Select(o => string.Join("\u0001", o.Values)).Distinct().Count() == 1;
                foreach (var o in same ? g.Take(1) : g)
                {
                    string label = same ? o.Param : o.Tool + "." + o.Param;
                    var hit = byList.FirstOrDefault(x => x.Values.SequenceEqual(o.Values));
                    if (hit.Names != null) hit.Names.Add(label); else byList.Add((new List<string> { label }, o.Values));
                }
            }
            foreach (var (names, values) in byList)
                sb.Append("\n" + string.Join("/", names) + ": " + (values.Count > 0 ? string.Join(", ", values) : "none listed"));
            return sb.ToString();
        }

        // ---- the two synthetic games ------------------------------------------------------------
        private static JObject Board(int v) => new JObject
        {
            ["goals"] = new JArray(new JObject { ["id"] = v == 0 ? "g1" : "g9", ["status"] = "open", ["text"] = "x" }),
            ["orders"] = new JArray(new JObject { ["id"] = v == 0 ? "o1" : "o9", ["text"] = "x" }),
        };

        private static JObject Digest(int v)
        {
            string[] n = v == 0 ? new[] { "Ana", "Bram", "Cy", "Dee", "Kit" } : new[] { "Zed", "Yan", "Xia", "Wil", "Val" };
            int k = v == 0 ? 0 : 50;
            JObject Pawn(int id, string name, string role, int age) => new JObject { ["id"] = id + k, ["name"] = name, ["role"] = role, ["age"] = age };
            var d = new JObject
            {
                ["pawns"] = new JArray(Pawn(1, n[0], "colonist", 30), Pawn(2, n[1], "colonist", 35), Pawn(3, n[2], "colonist", 8), Pawn(4, n[3], "colonist", 1), Pawn(5, n[4], "prisoner", 40)),
                ["work_types"] = new JArray(v == 0 ? new[] { "Cooking", "Hauling", "Doctor" } : new[] { "Growing", "Mining", "Art" }),
                ["policies"] = new JObject
                {
                    ["outfit"] = new JArray(v == 0 ? "Anything" : "Worker"), ["food"] = new JArray(v == 0 ? "Fine" : "Simple"), ["drug"] = new JArray(v == 0 ? "Social" : "None"),
                },
                ["beds"] = new JArray(new JObject { ["id"] = 3301 + k, ["label"] = "bed" }),
                ["letters"] = new JArray(new JObject { ["id"] = 77 + k, ["label"] = "letter", ["choices"] = new JArray("Accept", "Reject") }),
                ["dialogs"] = new JArray(new JObject { ["id"] = (v == 0 ? "L41" : "L92") }),
                ["research"] = new JObject { ["available"] = new JArray(new JObject { ["def"] = "Batteries", ["label"] = "batteries" }) },
                ["build"] = new JObject
                {
                    ["rooms"] = new JArray(new JObject { ["id"] = 12 + k, ["role"] = v == 0 ? "kitchen" : "barracks", ["label"] = v == 0 ? "Kitchen" : "Barracks" }),
                    ["benches"] = new JArray(new JObject { ["id"] = 4410 + k, ["label"] = v == 0 ? "stove" : "smithy", ["recipes"] = new JArray("meal") }),
                    ["zones"] = new JArray(
                        new JObject { ["id"] = 3 + k, ["kind"] = "grow", ["plant"] = "rice", ["cells"] = 9 },
                        new JObject { ["id"] = 4 + k, ["kind"] = "stockpile", ["preset"] = "all", ["cells"] = 9 },
                        new JObject { ["kind"] = ZonesKit.Hydro, ["cells"] = 2 }),
                    ["can_build"] = new JArray(v == 0 ? new[] { "wall", "bed" } : new[] { "door", "table" }),
                    ["stuff"] = new JArray(new JObject { ["stuff"] = v == 0 ? "steel" : "wood", ["count"] = 9 }),
                    ["plants"] = new JArray(v == 0 ? new[] { "rice", "corn" } : new[] { "potatoes", "healroot" }),
                    ["ores"] = new JArray(new JObject { ["ore"] = v == 0 ? "steel" : "gold", ["cells"] = 3 }),
                    ["floors"] = new JArray(v == 0 ? "wood floor" : "tile"),
                    ["areas"] = new JArray(new JObject { ["name"] = v == 0 ? "Field" : "Pasture", ["cells"] = 4 }),
                    ["animals"] = new JArray(v == 0 ? "cow" : "horse"),
                    ["undoable"] = 2,
                },
                ["trade"] = new JObject { ["traders"] = new JArray(new JObject { ["id"] = (v == 0 ? "T1" : "T9"), ["name"] = v == 0 ? "Bob" : "Cat", ["can_trade"] = true }) },
                ["cmd"] = new JObject
                {
                    ["patients"] = new JArray(n[0]), ["prisoners"] = new JArray(n[4]), ["animals"] = new JArray(v == 0 ? "cow" : "horse"), ["wild"] = new JArray(v == 0 ? "deer" : "boar"),
                    ["surgery"] = true, ["social"] = true, ["ideo"] = true, ["may_execute"] = true,
                },
                ["bio"] = new JObject
                {
                    ["mechanitors"] = new JArray(new JObject { ["pawn"] = 1 + k, ["name"] = n[0] }),
                    ["mechs"] = new JArray(new JObject { ["id"] = 501 + k, ["name"] = v == 0 ? "Lifter" : "Militor", ["kind"] = "lifter" }),
                    ["can_gestate"] = new JArray(new JObject { ["kind"] = v == 0 ? "lifter" : "agrihand" }),
                    ["genes"] = new JArray(new JObject { ["pawn"] = 1 + k, ["name"] = n[0] }),
                    ["hemogen"] = new JObject { ["prisoners"] = new JArray(new JObject { ["id"] = 5 + k, ["name"] = n[4] }) },
                    ["genebank"] = new JObject { ["unstored"] = 1, ["genepacks"] = 2, ["packs"] = new JArray(v == 0 ? "robust (complexity 1)" : "fast (complexity 2)") },
                    ["xenogerms"] = new JArray(new JObject { ["id"] = 601 + k, ["name"] = v == 0 ? "Hussar" : "Pigskin" }),
                    ["may_implant"] = true,
                    ["children"] = new JArray(new JObject { ["pawn"] = 3 + k, ["name"] = n[2], ["age"] = 8 }),
                    ["babies"] = new JArray(new JObject { ["pawn"] = 4 + k, ["name"] = n[3] }),
                    ["carers"] = new JArray(n[0]),
                },
                ["combat"] = new JObject
                {
                    ["active"] = true,
                    ["positions"] = new JArray(v == 0 ? new[] { "cover", "door" } : new[] { "home", "wall_n" }),
                    ["threats"] = new JArray(new JObject { ["id"] = 9201 + k, ["faction"] = "Pirates", ["kind"] = "ranged", ["ids"] = new JArray(9201 + k) }),
                },
            };
            return d;
        }
    }
}

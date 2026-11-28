// planner-sim: the steward on a recorded early-colony digest, several cycles, with its room requests played through the real
// base planner on a recorded map grid (Verse-free). Each cycle the digest's build block is refreshed from the grid (rooms, the
// BASE line, what is missing), the steward runs one cycle (StewardTry, the real tool backend at --llm-url), and every POST
// /act/room it makes is planned, stamped on the grid (as if built) and furnished. Prints the requests, the planner's words or
// refusals, and an ASCII drawing of the base after each room; writes the same to --out.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Harness
{
    public static class PlannerSim
    {
        /// <summary>A grid file: "# comment", "origin X Z", "room X Z ROLE LABEL..." (names the room holding that cell), then the
        /// rows, north first, in BaseGrid.FromAscii's legend.</summary>
        public static BaseGrid LoadGrid(string path)
        {
            int ox = 0, oz = 0;
            var rows = new List<string>();
            var roles = new List<string[]>();
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("#") || line.Length == 0) continue;
                if (line.StartsWith("origin ")) { var p = line.Split(' '); ox = int.Parse(p[1]); oz = int.Parse(p[2]); continue; }
                if (line.StartsWith("room ")) { roles.Add(line.Split(new[] { ' ' }, 5)); continue; }
                rows.Add(line);
            }
            var g = BaseGrid.FromAscii(ox, oz, rows.ToArray());
            foreach (var r in roles)
            {
                int id = g.RoomAt(ox + int.Parse(r[1]), oz + int.Parse(r[2]));
                var room = g.Room(id);
                if (room == null) continue;
                room.Role = r[3]; room.Label = r.Length > 4 ? r[4] : "the " + r[3];
            }
            g.AddRoomPois();
            return g;
        }

        static readonly Dictionary<string, string> MissingFor = new Dictionary<string, string>
        {
            ["kitchen"] = "stove", ["freezer"] = "freezer", ["dining"] = "dining table", ["research"] = "research bench",
        };

        /// <summary>The digest with its build block refreshed from the grid.</summary>
        public static JObject Refresh(JObject digest, BaseGrid g, List<string> missing0, int colonists)
        {
            var d = (JObject)digest.DeepClone();
            var b = d["build"] as JObject ?? new JObject();
            d["build"] = b;
            b["base"] = BasePlanner.Describe(g);
            var beds = g.Pois.Where(p => p.Kind == "bed").ToList();
            var rooms = new JArray();
            foreach (var r in g.Rooms.Where(r => r.Cells >= 4))
                rooms.Add(new JObject
                {
                    ["id"] = r.Id, ["role"] = r.Role, ["label"] = r.Label, ["w"] = r.MaxX - r.MinX + 1, ["h"] = r.MaxZ - r.MinZ + 1, ["cells"] = r.Cells,
                    ["impressiveness"] = 20, ["bed"] = beds.Any(p => g.RoomAt(p.Cells[0][0], p.Cells[0][1]) == r.Id), ["door"] = true, ["roofed"] = true,
                    ["x"] = (int)r.CX, ["z"] = (int)r.CZ, ["temp_c"] = r.Role == "freezer" ? -5 : 18, ["mountain"] = false, ["dirty"] = false
                });
            b["rooms"] = rooms;
            b["home"] = new JObject { ["x"] = g.HomeX, ["z"] = g.HomeZ };
            var have = new HashSet<string>(g.Rooms.Select(r => r.Role));
            int bedrooms = g.Rooms.Count(r => r.Role == "bedroom");
            var miss = missing0.Where(m => !MissingFor.Any(kv => kv.Value == m && have.Contains(kv.Key))).ToList();
            if (bedrooms < colonists && !miss.Contains("bedrooms")) miss.Add("bedrooms (" + bedrooms + " of " + colonists + ")");
            b["missing"] = new JArray(miss);
            d["rooms"] = new JObject { ["bedrooms"] = bedrooms, ["barracks"] = g.Rooms.Count(r => r.Role == "barracks"), ["hospital"] = have.Contains("hospital"), ["freezer"] = have.Contains("freezer") };
            return d;
        }

        public static async Task<int> MainAsync(string[] argv, TextWriter o, TextWriter e)
        {
            string digest = null, grid = null, url = null, model = "", mode = "auto", outDir = null, chat = "";
            int cycles = 6;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string Next() { if (i + 1 >= argv.Length) throw new ArgumentException("expected a value after " + a); return argv[++i]; }
                switch (a)
                {
                    case "--digest": digest = Next(); break;
                    case "--grid": grid = Next(); break;
                    case "--llm-url": url = Next(); break;
                    case "--model": model = Next(); break;
                    case "--mode": mode = Next(); break;
                    case "--cycles": cycles = int.Parse(Next()); break;
                    case "--chat": chat = Next(); break;
                    case "--out": outDir = Next(); break;
                    default: e.WriteLine("planner-sim: unrecognized argument: " + a); return 2;
                }
            }
            if (digest == null || grid == null || url == null || outDir == null) { e.WriteLine("planner-sim: needs --digest, --grid, --llm-url and --out"); return 2; }
            Directory.CreateDirectory(outDir);
            var log = new StringWriter();
            Action<string> say = s => { o.WriteLine(s); log.WriteLine(s); };
            var dj = (JObject)BridgeJson.Parse(File.ReadAllText(digest));
            var g = LoadGrid(grid);
            var missing0 = ((dj["build"] as JObject)?["missing"] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
            int colonists = (dj["pawns"] as JArray)?.Count ?? 3;
            var furniture = new Dictionary<long, char>();
            int rooms = 0;
            say("== start\n" + g.ToAscii(furniture) + "\nBASE: " + BasePlanner.Describe(g));
            for (int c = 1; c <= cycles; c++)
            {
                var d = Refresh(dj, g, missing0, colonists);
                File.WriteAllText(Path.Combine(outDir, "digest-" + c + ".json"), d.ToString());
                var t0 = DateTime.UtcNow;
                var oc = await StewardTry.TryOnceAsync(d, null, chat, true, url, model, mode);
                say("\n== cycle " + c + " (" + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.0") + " s)");
                say("BASE: " + (string)d["build"]["base"]);
                if (oc.Acts.Count == 0) say("acts: none");
                foreach (var kv in oc.Acts)
                {
                    say("act " + kv.Key + " " + kv.Value.ToString(Newtonsoft.Json.Formatting.None));
                    if (kv.Key != "room") continue;
                    var body = kv.Value;
                    var req = new RoomRequest
                    {
                        Role = (string)body["purpose"], Size = (string)body["size"] ?? "medium",
                        Near = (string)body["near"], For = (string)body["for"]
                    };
                    string why;
                    var plan = BasePlanner.Plan(g, req, out why);
                    if (plan == null) { say("  refused: can't fit a " + req.Role + " into the base: " + why); continue; }
                    say("  " + plan.Words);
                    var outer = plan.Walls.Where(w => BasePlanner.OuterWallCell(g, plan.Box, w)).ToList();
                    var places = FurnitureLayout.Place(plan.Inside, plan.Door, FurnitureLayout.Items(req.Role, req.Size, 0), outer);
                    say("  furniture: " + (places.Count == 0 ? "none" : string.Join(", ", places.Select(p => p.Word))));
                    BasePlanner.Apply(g, plan, null, string.IsNullOrWhiteSpace(req.For) ? null : req.For.Trim());
                    FurnitureLayout.Draw(furniture, places);
                    rooms++;
                }
                if (oc.Chat.Count > 0) foreach (var ch in oc.Chat) say("chat " + ch.ToString(Newtonsoft.Json.Formatting.None));
                say(g.ToAscii(furniture));
            }
            say("\n== end: " + rooms + " rooms planned\nBASE: " + BasePlanner.Describe(g));
            File.WriteAllText(Path.Combine(outDir, "planner-sim.txt"), log.ToString());
            return 0;
        }
    }
}

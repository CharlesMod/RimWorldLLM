// The area tool and the undo tool: presence by digest, params, body mapping, refusals, the one-undo-a-turn guard, digest lines, size.
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class StewardAreasTests
    {
        private static readonly JObject Build = JObject.Parse(@"{
            ""home"": {""x"": 120, ""z"": 98},
            ""rooms"": [{""id"": 14, ""role"": ""kitchen"", ""label"": ""Kitchen""}, {""id"": 15, ""role"": ""dining room"", ""label"": ""dining room""}],
            ""zones"": [{""id"": 4, ""kind"": ""stockpile"", ""cells"": 20, ""preset"": ""food"", ""priority"": ""normal""}, {""kind"": ""hydroponics"", ""cells"": 4}],
            ""areas"": [{""name"": ""pen"", ""cells"": 40}, {""name"": ""Barn"", ""cells"": 12}],
            ""animals"": [""Biscuit"", ""Rex""],
            ""undoable"": 3,
            ""can_build"": [""wall"", ""door""]}");

        private static JObject Digest(JObject build)
        {
            var d = (JObject)StewardTestData.Sample.DeepClone();
            if (build == null) d.Remove("build"); else d["build"] = build.DeepClone();
            return d;
        }

        private static StewardSession Session(JObject build, IBridge bridge = null) => new StewardKit().Session(Digest(build), bridge, false);
        private static ToolSpec Tool(StewardSession s, string n) => s.Tools.First(t => t.Name == n);
        private static JObject With(params (string, JToken)[] set) { var b = (JObject)Build.DeepClone(); foreach (var (k, v) in set) b[k] = v; return b; }

        private static JObject Call(StewardSession s, string tool, object args)
        {
            var (v, err) = Tool(s, tool).CheckArgs(JObject.FromObject(args), false);
            Assert.Null(err);
            return v;
        }

        private static string Pawn => (string)StewardTestData.Sample["pawns"][0]["name"];

        [Fact]
        public void AreaGate()
        {
            Assert.Contains(Session(Build).Tools, t => t.Name == "area");
            Assert.Contains("area", Session(Build).Acting);
            Assert.Contains(Session(With(("rooms", new JArray()))).Tools, t => t.Name == "area");     // a zone is enough
            Assert.Contains(Session(With(("zones", new JArray()))).Tools, t => t.Name == "area");     // a room is enough
            Assert.DoesNotContain(Session(With(("zones", new JArray()), ("rooms", new JArray()))).Tools, t => t.Name == "area");
            Assert.DoesNotContain(Session(With(("zones", new JArray(JObject.Parse("{\"kind\":\"hydroponics\"}"))), ("rooms", new JArray()))).Tools, t => t.Name == "area");
            Assert.DoesNotContain(Session(null).Tools, t => t.Name == "area");
        }

        [Fact]
        public void AreaParamsFollowTheDigest()
        {
            var s = Session(Build);
            Assert.Equal("area(op, area, where?, who?, why?)", Tool(s, "area").Signature());
            Assert.Contains("pen, Barn", Tool(s, "area").Params.First(p => p.Name == "area").Description);
            Assert.Equal(new[] { "add", "remove", "assign" }, Tool(s, "area").Params.First(p => p.Name == "op").Choices);
        }

        [Fact]
        public async Task AreaBodyAndTheWire()
        {
            var fb = new FakeBridge();
            var s = Session(Build, FakeBridge.Client(fb));
            await s.RunAsync("area", Call(s, "area", new { op = "add", area = "PEN", where = "room 15", who = "biscuit, Rex", why = "since the horses wander" }));
            var last = fb.ActLog.Last();
            Assert.Equal("area", last.Key);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"add\",\"name\":\"pen\",\"where\":\"room 15\",\"who\":[\"Biscuit\",\"Rex\"],\"why\":\"since the horses wander\"}"), last.Value), last.Value.ToString());
            await s.RunAsync("area", Call(s, "area", new { op = "add", area = "yard", where = "#4" }));
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"add\",\"name\":\"yard\",\"where\":\"#4\"}"), fb.ActLog.Last().Value), fb.ActLog.Last().Value.ToString());
            await s.RunAsync("area", Call(s, "area", new { op = "remove", area = "home", where = "base" }));
            Assert.Equal("home", (string)fb.ActLog.Last().Value["name"]);
            await s.RunAsync("area", Call(s, "area", new { op = "assign", area = "pen", who = Pawn }));
            Assert.Equal(Pawn, (string)fb.ActLog.Last().Value["who"][0]);
            Assert.Null(fb.ActLog.Last().Value["where"]);
            await s.RunAsync("area", Call(s, "area", new { op = "assign", area = "none", who = "Rex" }));
            Assert.Equal("none", (string)fb.ActLog.Last().Value["name"]);
        }

        [Theory]
        [InlineData("add", "pen", "room 99", null, "no such room")]
        [InlineData("add", "pen", "#9", null, "no such zone")]
        [InlineData("add", "pen", "hydroponics", null, "where must be")]
        [InlineData("add", "pen", null, null, "need where")]
        [InlineData("remove", "swamp", "base", null, "no allowed area named swamp; the areas are: pen, Barn")]
        [InlineData("assign", "swamp", null, "Rex", "no allowed area named swamp")]
        [InlineData("assign", "pen", "room 14", "Rex", "assign takes who")]
        [InlineData("assign", "pen", null, null, "assign needs who")]
        [InlineData("assign", "home", null, "Rex", "home is not for pawns")]
        [InlineData("add", "none", "base", null, "none goes with assign")]
        [InlineData("assign", "pen", null, "Goat", "no pawn named Goat; colonists:")]
        [InlineData("add", "pen", "base", "Goat", "animals: Biscuit, Rex")]
        public async Task AreaRefusals(string op, string area, string where, string who, string expect)
        {
            var s = Session(Build);
            var a = new JObject { ["op"] = op, ["area"] = area };
            if (where != null) a["where"] = where;
            if (who != null) a["who"] = who;
            var (v, err) = Tool(s, "area").CheckArgs(a, false);
            Assert.Null(err);
            var fb = new FakeBridge();
            var s2 = Session(Build, FakeBridge.Client(fb));
            var o = await s2.RunAsync("area", v);
            Assert.Contains(expect, (string)o.Result["error"]);
            Assert.Empty(fb.ActLog);
        }

        [Fact]
        public async Task RemoveOnAMissingAreaOfADigestWithNoneSaysMakeOne()
        {
            var s = Session(With(("areas", new JArray())));
            var (v, err) = Tool(s, "area").CheckArgs(JObject.FromObject(new { op = "remove", area = "pen", where = "base" }), false);
            Assert.Null(err);
            Assert.Contains("make one with op add", (string)(await s.RunAsync("area", v)).Result["error"]);
        }

        [Fact]
        public void UndoGate()
        {
            Assert.Contains(Session(Build).Tools, t => t.Name == "undo");
            Assert.Contains("undo", Session(Build).Acting);
            Assert.DoesNotContain(Session(With(("undoable", 0))).Tools, t => t.Name == "undo");
            Assert.DoesNotContain(Session(With(("undoable", null))).Tools, t => t.Name == "undo");
            Assert.DoesNotContain(Session(null).Tools, t => t.Name == "undo");
            Assert.Equal("undo(n?, why?)", Tool(Session(Build), "undo").Signature());
        }

        [Fact]
        public void UndoRangeIsChecked()
        {
            var s = Session(Build);
            Assert.NotNull(Tool(s, "undo").CheckArgs(JObject.FromObject(new { n = 6 }), false).Item2);
            Assert.NotNull(Tool(s, "undo").CheckArgs(JObject.FromObject(new { n = 0 }), false).Item2);
            Assert.Null(Tool(s, "undo").CheckArgs(JObject.FromObject(new { n = 5 }), false).Item2);
        }

        [Fact]
        public async Task UndoBodyAndOneAPerTurn()
        {
            var fb = new FakeBridge();
            var s = Session(Build, FakeBridge.Client(fb));
            var o = await s.RunAsync("undo", Call(s, "undo", new { n = 2, why = "since the wall blocks the door" }));
            Assert.Equal("undo", fb.ActLog.Last().Key);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"n\":2,\"why\":\"since the wall blocks the door\"}"), fb.ActLog.Last().Value), fb.ActLog.Last().Value.ToString());
            int sent = fb.ActLog.Count;
            var again = await s.RunAsync("undo", Call(s, "undo", new { }));
            Assert.Equal(sent, fb.ActLog.Count);
            Assert.Equal("already undid this turn; move on", (string)again.Result["refused"]);
            // a fresh session is a fresh turn
            var s2 = Session(Build, FakeBridge.Client(fb));
            await s2.RunAsync("undo", Call(s2, "undo", new { }));
            Assert.Equal(sent + 1, fb.ActLog.Count);
            Assert.True(JToken.DeepEquals(JObject.Parse("{}"), fb.ActLog.Last().Value), fb.ActLog.Last().Value.ToString());
        }

        [Fact]
        public void DigestLines()
        {
            string text = StewardDigest.RenderDigest(Digest(Build), StewardDigest.Cap);
            Assert.Contains("; undoable 3", text);
            Assert.Contains("AREAS: pen 40; Barn 12; animals: Biscuit, Rex", text);
            string none = StewardDigest.RenderDigest(Digest(With(("undoable", 0), ("areas", new JArray()), ("animals", new JArray()))), StewardDigest.Cap);
            Assert.DoesNotContain("undoable", none);
            Assert.DoesNotContain("AREAS:", none);
        }

        [Fact]
        public void Size()
        {
            var s = Session(Build);
            string all = Prompt.RenderTools(s.Tools);
            foreach (var n in new[] { "area", "undo" })
            {
                string without = Prompt.RenderTools(s.Tools.Where(t => t.Name != n).ToList());
                System.Console.WriteLine(n.ToUpper() + " RenderTools chars: " + (all.Length - without.Length) + " total " + all.Length);
            }
            Assert.Contains("- area(op, area, where?, who?, why?)", all);
            // every tool of the zones part at once (rezone, floor, area, undo) on top of the build tools
            var full = Session(With(("floors", new JArray("wood floor", "concrete")), ("plants", new JArray("potatoes", "rice"))));
            Assert.True(new[] { "rezone", "floor", "area", "undo" }.All(n => full.Tools.Any(t => t.Name == n)));
            int zonesChars = Prompt.RenderTools(full.Tools).Length - Prompt.RenderTools(full.Tools.Where(t => !new[] { "rezone", "floor", "area", "undo" }.Contains(t.Name)).ToList()).Length;
            System.Console.WriteLine("ZONES PART (4 tools) chars: " + zonesChars + " total " + Prompt.RenderTools(full.Tools).Length);
            Assert.True(Prompt.RenderTools(full.Tools).Length < 11000, Prompt.RenderTools(full.Tools).Length.ToString());   // every build-family tool open at once (lead budget, tools integration)
        }
    }
}

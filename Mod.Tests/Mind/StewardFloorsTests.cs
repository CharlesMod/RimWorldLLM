// The floor tool: presence by digest (rooms AND floors), params, body mapping, refusals, digest line, size.
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
    public class StewardFloorsTests
    {
        private static readonly JObject Build = JObject.Parse(@"{
            ""home"": {""x"": 120, ""z"": 98},
            ""rooms"": [{""id"": 14, ""role"": ""kitchen"", ""label"": ""Kitchen""}, {""id"": 15, ""role"": ""dining room"", ""label"": ""dining room""}],
            ""floors"": [""wood floor"", ""concrete"", ""red carpet""],
            ""can_build"": [""wall"", ""door""]}");

        private static JObject Digest(JObject build)
        {
            var d = (JObject)StewardTestData.Sample.DeepClone();
            if (build == null) d.Remove("build"); else d["build"] = build.DeepClone();
            return d;
        }

        private static StewardSession Session(JObject build, IBridge bridge = null) => new StewardKit().Session(Digest(build), bridge, false);
        private static ToolSpec Floor(StewardSession s) => s.Tools.First(t => t.Name == "floor");
        private static JObject With(params (string, JToken)[] set) { var b = (JObject)Build.DeepClone(); foreach (var (k, v) in set) b[k] = v; return b; }

        private static JObject Call(StewardSession s, object args)
        {
            var (v, err) = Floor(s).CheckArgs(JObject.FromObject(args), false);
            Assert.Null(err);
            return v;
        }

        [Fact]
        public void OnlyWithRoomsAndFloors()
        {
            Assert.Contains(Session(Build).Tools, t => t.Name == "floor");
            Assert.Contains("floor", Session(Build).Acting);
            Assert.DoesNotContain(Session(With(("floors", new JArray()))).Tools, t => t.Name == "floor");
            Assert.DoesNotContain(Session(With(("rooms", new JArray()))).Tools, t => t.Name == "floor");
            Assert.DoesNotContain(Session(null).Tools, t => t.Name == "floor");
            Assert.DoesNotContain(Session(With(("floors", null))).Tools, t => t.Name == "floor");
            Assert.DoesNotContain(Session(With(("floors", new JArray("smooth stone")))).Tools, t => t.Name == "floor");
            Assert.DoesNotContain(Session(Build).Tools, t => t.Name == "rezone");   // no zones in this digest
        }

        [Fact]
        public void ParamsFollowTheDigest()
        {
            var s = Session(Build);
            Assert.Equal("floor(room, kind, why?)", Floor(s).Signature());
            Assert.Equal(new[] { "14", "15" }, Floor(s).Params.First(p => p.Name == "room").Choices);
            Assert.Equal(new[] { "wood floor", "concrete", "red carpet", "smooth stone" }, Floor(s).Params.First(p => p.Name == "kind").Choices);
            Assert.Contains("room id from ROOMS", Floor(s).Params.First(p => p.Name == "room").Description);
        }

        [Fact]
        public async Task BodyAndTheWire()
        {
            var fb = new FakeBridge();
            var s = Session(Build, FakeBridge.Client(fb));
            await s.RunAsync("floor", Call(s, new { room = "15", kind = "wood floor", why = "since the dining room is bare dirt" }));
            var last = fb.ActLog.Last();
            Assert.Equal("floor", last.Key);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"room\":15,\"kind\":\"wood floor\",\"why\":\"since the dining room is bare dirt\"}"), last.Value), last.Value.ToString());
            await s.RunAsync("floor", Call(s, new { room = "14", kind = "smooth stone" }));
            Assert.Equal("smooth stone", (string)fb.ActLog.Last().Value["kind"]);
        }

        [Fact]
        public void BadArgsNeverReachTheWire()
        {
            var s = Session(Build);
            var (v, err) = Floor(s).CheckArgs(JObject.FromObject(new { room = "99", kind = "wood floor" }), false);
            Assert.NotNull(err);
            (v, err) = Floor(s).CheckArgs(JObject.FromObject(new { room = "14", kind = "lava" }), false);
            Assert.NotNull(err);
        }

        [Fact]
        public void FloorsLineAndSize()
        {
            string text = StewardDigest.RenderDigest(Digest(Build), StewardDigest.Cap);
            Assert.Contains("FLOORS: wood floor, concrete, red carpet", text);
            var s = Session(Build);
            string all = Prompt.RenderTools(s.Tools);
            string without = Prompt.RenderTools(s.Tools.Where(t => t.Name != "floor").ToList());
            System.Console.WriteLine("FLOOR RenderTools chars: " + (all.Length - without.Length) + " total " + all.Length);
            Assert.Contains("- floor(room, kind, why?)", all);
            Assert.True(all.Length - without.Length < 700);
        }
    }
}

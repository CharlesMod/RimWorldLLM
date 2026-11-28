using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;
using Xunit.Abstractions;

namespace RimWorldLLM.Tests.Mind
{
    public class StewardPlaceTests
    {
        private readonly ITestOutputHelper _o;
        public StewardPlaceTests(ITestOutputHelper o) { _o = o; }

        private static readonly JObject Build = JObject.Parse(@"{""home"": {""x"": 1, ""z"": 2}, ""rooms"": [{""id"": 12, ""role"": ""bedroom"", ""label"": ""Ana's bedroom""}], ""can_build"": [""wall"", ""bed""]}");

        private static JObject Digest(JObject build)
        {
            var d = (JObject)StewardTestData.Sample.DeepClone();
            if (build == null) d.Remove("build"); else d["build"] = build.DeepClone();
            return d;
        }

        private static StewardSession Session(JObject build) => new StewardKit().Session(Digest(build), null, false);
        private static ToolSpec Tool(StewardSession s) => s.Tools.First(t => t.Name == "place");

        private static (string, JObject) Body(object args)
        {
            var s = Session(Build);
            var (v, err) = Tool(s).CheckArgs(JObject.FromObject(args), false);
            Assert.Null(err);
            return s.VerbBody("place", v);
        }

        private static void Refuses(object args, string text)
        {
            var e = Assert.Throws<PyValueError>(() => Body(args));
            Assert.Contains(text, e.Message);
        }

        [Fact]
        public void NoToolWithoutBuildBlock()
        {
            Assert.DoesNotContain(Session(null).Tools, t => t.Name == "place");
            Assert.Contains(Session(Build).Tools, t => t.Name == "place");
            Assert.True(Session(Build).Acting.Contains("place"));
        }

        [Fact]
        public void BodiesPerOp()
        {
            var (verb, b) = Body(new { op = "furnish", what = "research bench", at = "room 12", why = "since research stalls" });
            Assert.Equal("place", verb);
            Assert.Equal(@"{""op"":""furnish"",""what"":""research bench"",""at"":""room 12"",""why"":""since research stalls""}", b.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""generator"",""what"":""solar""}", Body(new { op = "generator", what = "Solar", at = "none", temp = "none" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""battery""}", Body(new { op = "battery" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""switch"",""at"":""#44""}", Body(new { op = "switch", at = "#44" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""connect"",""at"":""Kitchen""}", Body(new { op = "connect", at = "Kitchen" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""heater"",""at"":""Kitchen"",""temp"":""warm""}", Body(new { op = "heater", at = "Kitchen", temp = "warm" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""cooler"",""at"":""room 3""}", Body(new { op = "cooler", at = "room 3", temp = "none" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""target"",""at"":""room 3"",""temp"":""freezing""}", Body(new { op = "target", at = "room 3", temp = "freezing" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(@"{""op"":""ship"",""what"":""core""}", Body(new { op = "ship", what = "core" }).Item2.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void Refusals()
        {
            Refuses(new { op = "furnish", what = "bed" }, "furnish needs what");
            Refuses(new { op = "furnish", at = "room 1" }, "furnish needs what");
            Refuses(new { op = "generator" }, "generator needs what");
            Refuses(new { op = "generator", what = "nuclear" }, "wood, chemfuel");
            Refuses(new { op = "connect" }, "connect needs at");
            Refuses(new { op = "heater" }, "heater needs at");
            Refuses(new { op = "cooler" }, "cooler needs at");
            Refuses(new { op = "target", at = "room 1" }, "target needs at");
            Refuses(new { op = "target", temp = "warm" }, "target needs at");
            Refuses(new { op = "ship" }, "ship needs what");
            Refuses(new { op = "ship", what = "launch" }, "the keeper launches the ship");
        }

        [Fact]
        public void SpecRendersAndChecks()
        {
            var s = Session(Build);
            var t = Tool(s);
            Assert.Equal("place(op, what?, at?, temp?, why?)", t.Signature());
            Assert.Contains("place(", Prompt.RenderTools(s.Tools));
            Assert.Contains("place", Grammar.Build(s.Tools));
            var (_, err) = t.CheckArgs(JObject.FromObject(new { op = "nope" }), false);
            Assert.NotNull(err);
            (_, err) = t.CheckArgs(JObject.FromObject(new { op = "battery", what = new string('x', 41) }), false);
            Assert.NotNull(err);
        }

        [Fact]
        public void CharsAdded()
        {
            var with = Prompt.RenderTools(Session(Build).Tools).Length;
            var without = Prompt.RenderTools(Session(Build).Tools.Where(t => t.Name != "place").ToList()).Length;
            _o.WriteLine("PLACE CHARS " + (with - without));
            Assert.True(with < 9800);
        }
    }
}

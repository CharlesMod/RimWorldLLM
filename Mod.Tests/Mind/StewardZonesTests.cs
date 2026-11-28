// The rezone tool and the zone tool's priority: presence by digest, params, body mapping, refusals, rendering, size.
using System.Collections.Generic;
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
    public class StewardZonesTests
    {
        private static readonly JObject Build = JObject.Parse(@"{
            ""home"": {""x"": 120, ""z"": 98},
            ""rooms"": [{""id"": 14, ""role"": ""kitchen"", ""label"": ""Kitchen""}],
            ""zones"": [{""id"": 3, ""kind"": ""grow"", ""plant"": ""potatoes"", ""cells"": 64},
                        {""id"": 4, ""kind"": ""stockpile"", ""preset"": ""food"", ""priority"": ""normal"", ""cells"": 36},
                        {""kind"": ""hydroponics"", ""label"": ""hydroponics"", ""plant"": ""rice"", ""cells"": 6}],
            ""can_build"": [""wall"", ""door""], ""plants"": [""potatoes"", ""rice"", ""corn""]}");

        private static JObject Digest(JObject build)
        {
            var d = (JObject)StewardTestData.Sample.DeepClone();
            if (build == null) d.Remove("build"); else d["build"] = build.DeepClone();
            return d;
        }

        private static StewardSession Session(JObject build, IBridge bridge = null) => new StewardKit().Session(Digest(build), bridge, false);
        private static ToolSpec Rezone(StewardSession s) => s.Tools.First(t => t.Name == "rezone");
        private static JObject With(params (string, JToken)[] drop) { var b = (JObject)Build.DeepClone(); foreach (var (k, v) in drop) b[k] = v; return b; }

        private static JObject Call(StewardSession s, object args)
        {
            var (v, err) = Rezone(s).CheckArgs(JObject.FromObject(args), false);
            Assert.Null(err);
            return v;
        }

        [Fact]
        public void OnlyWhenTheDigestListsAZone()
        {
            Assert.Contains(Session(Build).Tools, t => t.Name == "rezone");
            Assert.DoesNotContain(Session(With(("zones", new JArray()))).Tools, t => t.Name == "rezone");
            Assert.DoesNotContain(Session(null).Tools, t => t.Name == "rezone");
            var onlyHydro = With(("zones", new JArray(JObject.Parse(@"{""kind"":""hydroponics"",""cells"":2}"))));
            Assert.Contains(Session(onlyHydro).Tools, t => t.Name == "rezone");
            Assert.Contains("rezone", Session(Build).Acting);
        }

        [Fact]
        public void ParamsFollowTheZones()
        {
            var s = Session(Build);
            Assert.Equal(new[] { "#3", "#4", "hydroponics" }, Rezone(s).Params.First(p => p.Name == "zone").Choices);
            Assert.Equal("rezone(zone, priority?, allow?, deny?, plant?, why?)", Rezone(s).Signature());
            Assert.Contains("critical", Rezone(s).Params.First(p => p.Name == "priority").Choices);
            var fieldsOnly = Session(With(("zones", new JArray(JObject.Parse(@"{""id"":3,""kind"":""grow"",""plant"":""rice"",""cells"":9}")))));
            Assert.Equal("rezone(zone, plant?, why?)", Rezone(fieldsOnly).Signature());
            var stockOnly = Session(With(("zones", new JArray(JObject.Parse(@"{""id"":4,""kind"":""stockpile"",""preset"":""food"",""cells"":9}")))));
            Assert.Equal("rezone(zone, priority?, allow?, deny?, why?)", Rezone(stockOnly).Signature());
        }

        [Fact]
        public async Task BodiesAndTheWire()
        {
            var fb = new FakeBridge();
            var s = Session(Build, FakeBridge.Client(fb));
            await s.RunAsync("rezone", Call(s, new { zone = "#4", priority = "important", allow = "Meals, steel", deny = "none", plant = "none", why = "since meals pile up" }));
            var last = fb.ActLog.Last();
            Assert.Equal("rezone", last.Key);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"zone\":\"#4\",\"priority\":\"important\",\"allow\":\"Meals, steel\",\"why\":\"since meals pile up\"}"), last.Value), last.Value.ToString());
            await s.RunAsync("rezone", Call(s, new { zone = "hydroponics", plant = "corn" }));
            Assert.Equal("corn", (string)fb.ActLog.Last().Value["plant"]);
            await s.RunAsync("rezone", Call(s, new { zone = "#3", plant = "rice" }));
            Assert.Equal("#3", (string)fb.ActLog.Last().Value["zone"]);
        }

        [Fact]
        public async Task RefusalsBeforeTheWire()
        {
            var fb = new FakeBridge();
            var s = Session(Build, FakeBridge.Client(fb));
            int n = fb.ActLog.Count;
            var r = await s.RunAsync("rezone", Call(s, new { zone = "#3", priority = "low" }));
            Assert.Contains("a field has no priority or filter; give plant", (string)r.Result["error"]);
            r = await s.RunAsync("rezone", Call(s, new { zone = "#4", plant = "rice" }));
            Assert.Contains("grows nothing", (string)r.Result["error"]);
            r = await s.RunAsync("rezone", Call(s, new { zone = "#4" }));
            Assert.Contains("give priority, allow or deny", (string)r.Result["error"]);
            Assert.Equal(n, fb.ActLog.Count);
        }

        [Fact]
        public void ZonesLineShowsPriorityAndHydroponics()
        {
            var d = Digest(With(("zones", JArray.Parse(@"[{""id"":3,""kind"":""grow"",""plant"":""potatoes"",""cells"":64},
                {""id"":4,""kind"":""stockpile"",""preset"":""food"",""priority"":""important"",""cells"":36},
                {""id"":5,""kind"":""stockpile"",""preset"":""meds"",""priority"":""normal"",""cells"":9},
                {""kind"":""hydroponics"",""plant"":""rice"",""cells"":6}]"))));
            string text = StewardDigest.RenderDigest(d, StewardDigest.Cap);
            Assert.Contains("ZONES: #3 grow potatoes 64 cells; #4 stockpile food important 36 cells; #5 stockpile meds 9 cells; hydroponics rice 6 basins", text);
        }

        [Fact]
        public void RenderingAndSize()
        {
            var s = Session(Build);
            string all = Prompt.RenderTools(s.Tools);
            string without = Prompt.RenderTools(s.Tools.Where(t => t.Name != "rezone").ToList());
            System.Console.WriteLine("REZONE RenderTools chars: " + (all.Length - without.Length) + " total " + all.Length);
            var z = s.Tools.First(t => t.Name == "zone");
            string zs = Prompt.RenderTools(new List<ToolSpec> { z });
            string zNo = Prompt.RenderTools(new List<ToolSpec> { new ToolSpec(z.Name, z.Description, z.Params.Where(p => p.Name != "priority").ToList(), z.ResultScales, z.EndsTurn) });
            System.Console.WriteLine("ZONEPRIO chars: " + (zs.Length - zNo.Length));
            Assert.Contains("- rezone(zone, priority?, allow?, deny?, plant?, why?)", all);
            Assert.True(all.Length - without.Length < 900);
        }
    }
}

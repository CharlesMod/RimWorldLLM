// The read tools inspect, find, survey (Mod/Source/Mind/RimWorld/StewardInspect.cs): specs, arg -> query, not acting, the per-turn limit,
// combat gating, refusal passthrough, and one routine cycle where the model reads and then acts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class StewardInspectTests
    {
        private static JObject A(object o) => JObject.FromObject(o);
        private static string Call(string t, object a) => Rig.Call(t, a);

        private static (StewardSession, FakeBridge) Live(bool pawns = true)
        {
            var fb = new FakeBridge();
            fb.Digest = (JObject)StewardTestData.Sample.DeepClone();
            if (!pawns) fb.Digest.Remove("pawns");
            return (new StewardKit().Session(fb.Digest, FakeBridge.Client(fb), false), fb);
        }

        private static JObject Checked(StewardSession s, string name, object args)
        {
            var (v, err) = s.Tools.First(t => t.Name == name).CheckArgs(A(args), false);
            Assert.Null(err);
            return v;
        }

        private static Task<Outcome> Do(StewardSession s, string name, object args) => s.RunAsync(name, Checked(s, name, args));

        [Fact]
        public void TheThreeSpecsRenderShortAndAreOfferedOnlyWithPawns()
        {
            var (s, _) = Live();
            var names = s.Tools.Select(t => t.Name).ToList();
            foreach (var n in InspectKit.Names) Assert.Contains(n, names);
            foreach (var n in InspectKit.Names)
            {
                var t = s.Tools.First(x => x.Name == n);
                Assert.True(t.Description.Length < 160, n);
                Assert.Contains("not an action", t.Description);
            }
            Assert.Equal("inspect(target)", s.Tools.First(t => t.Name == "inspect").Signature());
            Assert.Equal("find(what, near?, item?)", s.Tools.First(t => t.Name == "find").Signature());
            Assert.Equal("survey(what)", s.Tools.First(t => t.Name == "survey").Signature());
            Assert.Equal(InspectKit.FindKinds.ToList(), s.Tools.First(t => t.Name == "find").Params[0].Choices);
            Assert.Equal(new[] { "stock", "threats" }, s.Tools.First(t => t.Name == "survey").Params[0].Choices);
            Assert.Contains("find(what", Prompt.RenderTools(s.Tools));
            var (none, _) = Live(pawns: false);
            Assert.DoesNotContain(none.Tools, t => InspectKit.Names.Contains(t.Name));
        }

        [Fact]
        public void TheReadToolsAreNeverActing()
        {
            var (s, _) = Live();
            foreach (var n in InspectKit.Names)
            {
                Assert.DoesNotContain(n, StewardKit.ActingNames);
                Assert.DoesNotContain(n, s.Acting);
            }
        }

        [Fact]
        public void TheRenderedToolsGrowByAboutAThousandChars()
        {
            var (s, _) = Live();
            int all = Prompt.RenderTools(s.Tools).Length;
            int without = Prompt.RenderTools(s.Tools.Where(t => !InspectKit.Names.Contains(t.Name)).ToList()).Length;
            Console.WriteLine("INSPECT_RENDER_CHARS " + (all - without));
            Assert.InRange(all - without, 300, 1100);
        }

        [Fact]
        public async Task ArgumentsBecomeTheQuery()
        {
            var (s, fb) = Live();
            Assert.Equal("Ana: fine, no needs, at home", (string)(await Do(s, "inspect", new { target = "Ana" })).Result["result"]);
            await Do(s, "find", new { what = "item", near = "Bram", item = "steel" });
            await Do(s, "find", new { what = "wounded", near = "none", item = "none" });
            await Do(s, "survey", new { what = "stock" });
            Assert.Equal(4, fb.InspectAsks.Count);
            Assert.Equal("/inspect", fb.InspectAsks[0].Key);
            Assert.Equal(new Dictionary<string, string> { ["target"] = "Ana" }, fb.InspectAsks[0].Value);
            Assert.Equal("/find", fb.InspectAsks[1].Key);
            Assert.Equal(new Dictionary<string, string> { ["what"] = "item", ["near"] = "Bram", ["item"] = "steel" }, fb.InspectAsks[1].Value);
            Assert.Equal(new Dictionary<string, string> { ["what"] = "wounded" }, fb.InspectAsks[2].Value);
            Assert.Equal(new Dictionary<string, string> { ["what"] = "stock" }, fb.InspectAsks[3].Value);
            Assert.Empty(fb.ActLog);
        }

        [Fact]
        public async Task ACannedTextComesBackAsTheResultAndIsCapped()
        {
            var (s, fb) = Live();
            fb.InspectTexts["/survey|threats"] = "2 raiders 30 cells N of home, attacking";
            fb.InspectTexts["/inspect|Ana"] = new string('x', 900);
            Assert.Equal("2 raiders 30 cells N of home, attacking", (string)(await Do(s, "survey", new { what = "threats" })).Result["result"]);
            string big = (string)(await Do(s, "inspect", new { target = "Ana" })).Result["result"];
            Assert.True(big.Length <= InspectKit.TextMax && big.EndsWith("..."));
        }

        [Fact]
        public async Task AnUnknownTargetIsARefusalWithTheRoutesReason()
        {
            var (s, fb) = Live();
            fb.UnknownTarget = "Zed";
            var o = await Do(s, "inspect", new { target = "zed" });
            Assert.Equal("no pawn named zed; colonists: Ana, Bo", (string)o.Result["refused"]);
            Assert.False(o.Ok);
            Assert.Null(o.Result["result"]);
        }

        [Fact]
        public async Task TheFifthReadOfATurnIsAnError()
        {
            var (s, fb) = Live();
            for (int i = 0; i < 4; i++) Assert.NotNull((await Do(s, "inspect", new { target = "Ana" + i })).Result["result"]);
            var o = await Do(s, "survey", new { what = "stock" });
            Assert.Equal("no more reads this turn; act now", (string)o.Result["error"]);
            Assert.Equal(4, fb.InspectAsks.Count);
            var (fresh, _) = Live();                                        // a new session is a new turn
            Assert.NotNull((await Do(fresh, "survey", new { what = "stock" })).Result["result"]);
        }

        [Fact]
        public async Task ACombatTurnOffersSurveyAndInspectButNotFind()
        {
            Assert.Contains("inspect", StewardKit.CombatToolNames);
            Assert.Contains("survey", StewardKit.CombatToolNames);
            Assert.DoesNotContain("find", StewardKit.CombatToolNames);
            Rig.NoEnv();
            using var world = new JobsWorld();
            var r = await Rig.Boot(world, new object[] { Rig.Done });
            var w = r.w;
            w.Fb.Combat = CombatData.View("pirate_ranged");
            w.Fb.Digest = CombatData.Digest("pirate_ranged");
            w.Fb.CombatSince = w.Fb.Tick;
            await Rig.Cycle(w);
            r.fake.Script.Clear();
            r.fake.Script.AddRange(new object[] { Call("survey", new { what = "threats" }), Rig.Done });
            r.fake.Calls.Clear();
            w.Fb.Tick += 400;
            w.Real += 30;
            await Rig.Cycle(w);
            string system = Rig.System(r.fake.Calls[0]);
            Assert.Contains("survey(", system);
            Assert.Contains("inspect(", system);
            Assert.DoesNotContain("find(", system);
            Assert.Contains(w.Fb.InspectAsks, a => a.Key == "/survey");
            Assert.Contains("no threats", Rig.Json(r.fake.Calls[1]));
        }

        [Fact]
        public async Task ReadsThenAnActionInOneRoutineCycle()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Rig.Boot(world, new object[]
            {
                Call("inspect", new { target = "Ana" }), Call("find", new { what = "wounded" }), Call("draft", new { pawns = "Bram", drafted = "yes", why = "since raiders" }), Rig.Final("ok"),
            });
            world.Fb.InspectTexts["/inspect|Ana"] = "Ana: mood 62, hungry, cooking in the kitchen";
            world.Fb.InspectTexts["/find|wounded"] = "Cy (#102) bleeding, 6 cells N of home";
            Rig.Chat(w, "check on Ana and the wounded, then draft Bram");
            await Rig.Cycle(w);
            Assert.Equal(new[] { "/inspect", "/find" }, w.Fb.InspectAsks.Select(a => a.Key).ToArray());
            string later = string.Join("\n", fake.Calls.Skip(1).Select(c => Rig.Json(c)));
            Assert.Contains("Ana: mood 62, hungry, cooking in the kitchen", later);
            Assert.Contains("Cy (#102) bleeding, 6 cells N of home", later);
            Assert.Contains(w.Fb.ActLog, a => a.Key == "draft");
            var ex = (JArray)st.Stats.Trace.Last()["executed"];
            Assert.Equal("inspect", (string)ex[0][0]);
            Assert.Contains("mood 62", (string)ex[0][2]);
        }
    }
}

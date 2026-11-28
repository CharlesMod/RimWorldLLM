// NEW (the narrator can look at the screen): the vision gate, the look tool, image content parts in the loop, and that no picture bytes persist.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class VisionGateTests
    {
        private const string Engine = "/engine/v1/status";
        private static MockResponse Status(object extra) => MockResponse.Json(200, extra);

        private static (VisionProbe p, MockHandler h, double[] now) Make(Func<Seen, MockResponse> f)
        {
            var now = new[] { 1000.0 };
            var c = Mock.Client(f, out var h);
            return (new VisionProbe(c, "http://x:1/v1", "", () => now[0]), h, now);
        }

        private static MockResponse EngineWith(Seen s, params (string, object)[] extra)
        {
            var o = new JObject { ["phase"] = "ready", ["sampler"] = "structured" };
            foreach (var (k, v) in extra) o[k] = JToken.FromObject(v);
            return Status(o);
        }

        [Fact]
        public async Task EngineLookPostsTheContractBodyAndReturnsTheAnswer()
        {
            Seen seen = null;
            var c = Mock.Client(s => { seen = s; return MockResponse.Json(200, JObject.Parse(@"{""text"":"" Door blocked by a wall. "",""tokens"":{""image"":338,""prompt"":420,""completion"":9},""ms"":{""prefill"":1400,""total"":1700}}")); }, out _);
            var l = new EngineLookClient(c, "http://x:1/v1", null, "k", slot: 3);
            var o = await l.LookAsync(new LookRequest { B64 = "QUJD", Kind = "close", Label = "Picture attached: close", Question = "is the door blocked?" });
            Assert.Equal("Door blocked by a wall.", o.Answer); Assert.Null(o.Part);
            Assert.Equal("/engine/v1/look", seen.Path); Assert.Equal("POST", seen.Method);
            var b = seen.Json();
            Assert.Equal("data:image/jpeg;base64,QUJD", (string)b["images"][0]["data"]);
            Assert.Equal("Picture attached: close", (string)b["images"][0]["label"]);
            Assert.Equal("is the door blocked?", (string)b["question"]);
            Assert.Equal("high", (string)b["detail"]); Assert.Equal(3, (int)b["id_slot"]); Assert.Equal(160, (int)b["max_tokens"]);
            Assert.Equal(338, (int)l.LastStats["tokens"]["image"]);
            Assert.Equal("max", EngineLookClient.Detail("base")); Assert.Equal("max", EngineLookClient.Detail("wide"));
        }

        [Fact]
        public async Task EngineLookVisionOffOrUnreachableFallsBackToText()
        {
            var c = Mock.Client(s => MockResponse.Json(409, JObject.Parse(@"{""ok"":false,""error"":""vision_off"",""why"":""no projector""}")), out _);
            Assert.Null(await new EngineLookClient(c, "http://x:1", null).LookAsync(new LookRequest { B64 = "QUJD" }));
            var c2 = Mock.Client(s => MockResponse.Json(200, JObject.Parse(@"{""text"":""""}")), out _);
            Assert.Null(await new EngineLookClient(c2, "http://x:1", null).LookAsync(new LookRequest { B64 = "QUJD" }));
            Assert.False(await new EngineLookClient(c2, "http://x:1", null).OpenAsync());
        }

        [Fact] public async Task EngineVisionTrueOpens() { var (p, _, _) = Make(s => EngineWith(s, ("vision", true))); Assert.True(await p.OpenAsync()); }
        [Fact] public async Task EngineVisionFalseCloses() { var (p, _, _) = Make(s => EngineWith(s, ("vision", false))); Assert.False(await p.OpenAsync()); }
        [Fact] public async Task EngineVisionMissingCloses() { var (p, h, _) = Make(s => EngineWith(s)); Assert.False(await p.OpenAsync()); Assert.Single(h.All); }   // an engine is asked nothing else

        [Fact]
        public async Task OwnEndpointPropsVisionTrueOpensFalseCloses()
        {
            var (p, h, _) = Make(s => s.Path == Engine ? MockResponse.Empty(404) : Status(JObject.Parse(@"{""modalities"":{""vision"":true}}")));
            Assert.True(await p.OpenAsync());
            Assert.Equal("/props", h.All.Last().Path);
            var (q, _, _) = Make(s => s.Path == Engine ? MockResponse.Empty(404) : Status(JObject.Parse(@"{""modalities"":{""vision"":false}}")));
            Assert.False(await q.OpenAsync());
            var (r, _, _) = Make(s => s.Path == Engine ? MockResponse.Empty(404) : Status(JObject.Parse(@"{""total_slots"":1}")));
            Assert.False(await r.OpenAsync());
            var (n, _, _) = Make(s => MockResponse.Empty(404));
            Assert.False(await n.OpenAsync());
        }

        [Fact]
        public async Task UnreachableIsFalseAndNotCached()
        {
            bool up = false;
            var (p, h, _) = Make(s => up ? EngineWith(s, ("vision", true)) : throw Mock.ConnectError());
            Assert.False(await p.OpenAsync());
            Assert.False(await p.OpenAsync());
            Assert.Equal(2, p.Probes);                     // asked again: a server that was down is not remembered as "no"
            up = true;
            Assert.True(await p.OpenAsync());
        }

        [Fact]
        public async Task AnAnswerIsCachedAboutAMinute()
        {
            var (p, h, now) = Make(s => EngineWith(s, ("vision", true)));
            Assert.True(await p.OpenAsync()); Assert.True(await p.OpenAsync());
            Assert.Equal(1, p.Probes);
            now[0] += 59;
            await p.OpenAsync(); Assert.Equal(1, p.Probes);
            now[0] += 2;
            await p.OpenAsync(); Assert.Equal(2, p.Probes);
        }
    }

    public class VisionKitTests
    {
        private static readonly string B64 = "QUJDREVGR0g=";

        private static (VisionKit k, FakeBridge fb, List<int> delays) Make()
        {
            var fb = new FakeBridge();
            var delays = new List<int>();
            return (new VisionKit(FakeBridge.Client(fb), ms => { delays.Add(ms); return Task.CompletedTask; }), fb, delays);
        }

        private static JObject A(object o) => JObject.FromObject(o);
        private static bool IsErr(JToken t, string m) => t is JObject o && (string)o["error"] == m;

        [Fact]
        public async Task ReadyGivesALegendLineAndHoldsThePictureOnce()
        {
            var (k, fb, _) = Make();
            var r = await k.RunAsync(A(new { kind = "base" }));
            string text = (string)r;
            Assert.StartsWith("Picture attached: base, cells x 100-140, z 80-110 (40x30 cells), north up, 19 px per cell", text);
            Assert.DoesNotContain(B64, text);
            Assert.Equal("base", fb.VisionAsks[0]["kind"]); Assert.False(fb.VisionAsks[0].ContainsKey("target"));
            var part = k.TakeAttachment();
            Assert.Equal("data:image/jpeg;base64," + B64, (string)part["image_url"]["url"]);
            Assert.Null(k.TakeAttachment());                       // once
        }

        private sealed class PrepLook : ILookClient
        {
            public JObject Part; public string Answer; public List<string> Kinds = new List<string>(); public List<LookRequest> Asks = new List<LookRequest>();
            public Task<bool> OpenAsync() => Task.FromResult(true);
            public Task<LookOutcome> LookAsync(LookRequest r)
            {
                Kinds.Add(r.Kind + "|" + r.Mime + "|" + r.B64); Asks.Add(r);
                return Task.FromResult(Part == null && Answer == null ? null : new LookOutcome { Part = Part, Answer = Answer });
            }
        }

        [Fact]
        public async Task TheLookClientPreparesThePicture()
        {
            var fb = new FakeBridge();
            var prep = new PrepLook { Part = new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "engine:look/7" } } };
            var k = new VisionKit(FakeBridge.Client(fb), ms => Task.CompletedTask, prep);
            Assert.StartsWith("Picture attached: base", (string)await k.RunAsync(A(new { kind = "base" })));
            Assert.Equal(new[] { "base|image/jpeg|" + B64 }, prep.Kinds);
            Assert.Equal("engine:look/7", (string)k.TakeAttachment()["image_url"]["url"]);
        }

        [Fact]
        public async Task ALookClientThatCannotUseThePictureIsAnErrorAndKeepsTheAllowance()
        {
            var fb = new FakeBridge();
            var prep = new PrepLook { Part = null };
            var k = new VisionKit(FakeBridge.Client(fb), ms => Task.CompletedTask, prep);
            Assert.True(IsErr(await k.RunAsync(A(new { kind = "base" })), "the picture could not be used"));
            Assert.Null(k.TakeAttachment());
            prep.Part = ImageParts.Part("image/jpeg", B64);
            Assert.StartsWith("Picture attached", (string)await k.RunAsync(A(new { kind = "base" })));
        }

        [Fact]
        public async Task DirectLookClientIsTheDataUriPartBehindTheGate()
        {
            var c = new DirectLookClient(null);
            Assert.False(await c.OpenAsync());
            Assert.Null(await c.LookAsync(new LookRequest { B64 = "" }));
            var o = await c.LookAsync(new LookRequest { Mime = "image/png", B64 = B64, Kind = "close" });
            Assert.Null(o.Answer);
            Assert.Equal("data:image/png;base64," + B64, (string)o.Part["image_url"]["url"]);
        }

        [Fact]
        public async Task AnEngineLookAnswersInTextAndAttachesNothing()
        {
            var fb = new FakeBridge();
            var prep = new PrepLook { Answer = "A wall gap on the east side." };
            var k = new VisionKit(FakeBridge.Client(fb), ms => Task.CompletedTask, prep);
            string text = (string)await k.RunAsync(A(new { kind = "close", target = "room:12", why = "is the east wall closed?" }));
            Assert.StartsWith("Looked: close, cells x 100-140", text);
            Assert.EndsWith("What the picture shows: A wall gap on the east side.", text);
            Assert.DoesNotContain(B64, text);
            Assert.Null(k.TakeAttachment());
            Assert.Equal("is the east wall closed?", prep.Asks[0].Question);
            Assert.StartsWith("Picture attached: close", prep.Asks[0].Label);
        }

        [Fact]
        public async Task PendingIsPolledEvery250msThenReady()
        {
            var (k, fb, delays) = Make();
            fb.VisionPending = 3;
            var r = await k.RunAsync(A(new { kind = "wide" }));
            Assert.StartsWith("Picture attached: wide", (string)r);
            Assert.Equal(new[] { 250, 250, 250 }, delays);
            Assert.Equal(4, fb.VisionAsks.Count);
        }

        [Fact]
        public async Task PendingPastThreeSecondsIsCameraBusy()
        {
            var (k, fb, delays) = Make();
            fb.VisionPending = 1000;
            var r = await k.RunAsync(A(new { kind = "base" }));
            Assert.True(IsErr(r, "camera busy"));
            Assert.Equal(12, delays.Count); Assert.Equal(3000, delays.Sum());
            Assert.Null(k.TakeAttachment());
            fb.VisionPending = 0;                                  // a failed look did not use up the allowance
            Assert.StartsWith("Picture attached", (string)await k.RunAsync(A(new { kind = "base" })));
        }

        [Fact]
        public async Task LimitsOneImageAndACloseAfterBaseOrWide()
        {
            var (k, fb, _) = Make();
            await k.RunAsync(A(new { kind = "base" }));
            Assert.True(IsErr(await k.RunAsync(A(new { kind = "base" })), VisionKit.NoMore));
            Assert.True(IsErr(await k.RunAsync(A(new { kind = "wide" })), VisionKit.NoMore));
            Assert.StartsWith("Picture attached: close", (string)await k.RunAsync(A(new { kind = "close", target = "room:7" })));
            Assert.Equal("room:7", fb.VisionAsks.Last()["target"]);
            Assert.True(IsErr(await k.RunAsync(A(new { kind = "close", target = "room:8" })), VisionKit.NoMore));
            Assert.Equal(2, fb.VisionAsks.Count);                  // refused looks never reached the game
            Assert.Equal(new[] { "base", "close" }, k.Looked);

            var (k2, _, _) = Make();
            await k2.RunAsync(A(new { kind = "close", target = "cell:12,30" }));
            Assert.True(IsErr(await k2.RunAsync(A(new { kind = "close", target = "cell:1,2" })), VisionKit.NoMore));   // a first close allows no second
            Assert.True(IsErr(await k2.RunAsync(A(new { kind = "base" })), VisionKit.NoMore));
        }

        [Theory]
        [InlineData("room:3")] [InlineData("thing:1234")] [InlineData("pawn:55")] [InlineData("cell:10,20")] [InlineData("dir:northeast:40")]
        public async Task CloseTargetForms(string target)
        {
            var (k, fb, _) = Make();
            Assert.StartsWith("Picture attached: close", (string)await k.RunAsync(A(new { kind = "close", target })));
            Assert.Equal(target, fb.VisionAsks[0]["target"]);
        }

        [Theory]
        [InlineData("the kitchen")] [InlineData("room:")] [InlineData("dir:up:3")] [InlineData("")]
        public async Task BadCloseTargetsNeverReachTheGame(string target)
        {
            var (k, fb, _) = Make();
            Assert.IsType<JObject>(await k.RunAsync(A(new { kind = "close", target })));
            Assert.Empty(fb.VisionAsks);
        }

        [Fact]
        public async Task RefusalsAndErrorsAreTextNotImages()
        {
            var (k, fb, _) = Make();
            fb.VisionError = (409, "refused", "player_typing");
            var r = (JObject)await k.RunAsync(A(new { kind = "base" }));
            Assert.Contains("player_typing", (string)r["error"]);
            Assert.Null(k.TakeAttachment());
            fb.VisionError = (400, "bad_request", "bad kind");
            Assert.Contains("bad_request", (string)((JObject)await k.RunAsync(A(new { kind = "base" })))["error"]);
        }
    }

    public class VisionLoopTests
    {
        private static readonly JObject Part = ImageParts.Part("image/jpeg", "QUJDREVGR0g=");

        private static JObject Pic() => (JObject)Part.DeepClone();

        private static async Task<(LoopResult r, FakeBackend f)> Run(string protocol, bool attach, params object[] script)
        {
            var f = new FakeBackend(script);
            int n = 0;
            var o = new LoopOptions { MaxSteps = 4 };
            bool take = false;
            if (attach) o.TakeAttachment = () => { if (!take) return null; take = false; return Pic(); };
            var tools = Fx.Tools();
            var r = await Loop.RunAsync(f, tools, "You are a cook.", "Do things.",
                (name, v) => { n++; take = true; return Task.FromResult<JToken>(new JValue("Picture attached: base")); }, protocol, null, null, o);
            return (r, f);
        }

        private static string Call(string tool) => Fx.Call("do_nothing");

        [Fact]
        public async Task GrammarLastUserMessageBecomesTextPlusImage()
        {
            var (r, f) = await Run("grammar", true, Call("ping"), "{\"final\":\"ok\"}");
            Assert.Equal(2, f.Calls.Count);
            Assert.Equal(0, ImageParts.Count(f.Calls[0].Messages));
            var msgs = f.Calls[1].Messages;
            Assert.Equal(1, ImageParts.Count(msgs));
            var last = msgs.Last();
            Assert.Equal("user", (string)last["role"]);
            var parts = (JArray)last["content"];
            Assert.Equal(2, parts.Count);
            Assert.Equal("text", (string)parts[0]["type"]);
            Assert.StartsWith("Result of do_nothing: ", (string)parts[0]["text"]);
            Assert.EndsWith(Loop.NextPrompt, (string)parts[0]["text"]);
            Assert.Equal("data:image/jpeg;base64,QUJDREVGR0g=", (string)parts[1]["image_url"]["url"]);
            Assert.Equal("image_url", (string)parts[1]["type"]);
        }

        [Fact]
        public async Task NativeAddsAUserMessageAfterTheToolRows()
        {
            var tc = new Reply { ToolCalls = new List<ToolCall> { new ToolCall { Id = "a", Name = "do_nothing", Arguments = new JObject() } } };
            var (r, f) = await Run("native", true, tc, new Reply { Text = "all done" });
            var msgs = f.Calls[1].Messages;
            Assert.Equal(1, ImageParts.Count(msgs));
            Assert.Equal("tool", (string)msgs[msgs.Count - 2]["role"]);
            var last = msgs.Last();
            Assert.Equal("user", (string)last["role"]);
            var parts = (JArray)last["content"];
            Assert.Equal("The picture from look:", (string)parts[0]["text"]);
            Assert.Equal("image_url", (string)parts[1]["type"]);
            Assert.Equal(JTokenType.String, msgs[msgs.Count - 2]["content"].Type);       // the tool row stays text
        }

        [Fact]
        public async Task ASecondPictureReplacesTheFirstSoARequestCarriesOne()
        {
            var counts = new List<int>(); string third = "";
            Func<IReadOnlyList<JObject>, object> snap = m => { counts.Add(ImageParts.Count(m)); return Fx.Call("say", Fx.A("line", "hi")); };
            Func<IReadOnlyList<JObject>, object> snap2 = m => { counts.Add(ImageParts.Count(m)); third = string.Concat(m.Select(x => x.ToString())); return "{\"final\":\"ok\"}"; };
            Func<IReadOnlyList<JObject>, object> snap0 = m => { counts.Add(ImageParts.Count(m)); return Call("ping"); };
            var (r, f) = await Run("grammar", true, snap0, snap, snap2);
            Assert.Equal(new[] { 0, 1, 1 }, counts);
            Assert.Contains(ImageParts.Dropped, third);
        }

        [Fact]
        public async Task ARetryAfterARejectedReplyDoesNotDuplicateTheImage()
        {
            var (r, f) = await Run("grammar", true, Call("ping"), "not json at all", "{\"final\":\"ok\"}");
            Assert.True(f.Calls.Count >= 3);
            Assert.All(f.Calls.Skip(1), c => Assert.Equal(1, ImageParts.Count(c.Messages)));
        }

        [Fact]
        public async Task NoAttachmentMeansUnchangedBodies()
        {
            var (r1, f1) = await Run("grammar", false, Call("ping"), "{\"final\":\"ok\"}");
            var (r2, f2) = await Run("grammar", true, Call("ping"), "{\"final\":\"ok\"}");
            Assert.All(f1.Calls.SelectMany(c => c.Messages), m => Assert.Equal(JTokenType.String, m["content"].Type));
            Assert.NotEqual(JTokenType.String, f2.Calls[1].Messages.Last()["content"].Type);
            Assert.Equal(f1.Calls[0].Messages.Select(m => m.ToString()), f2.Calls[0].Messages.Select(m => m.ToString()));
        }

        [Fact]
        public async Task TheResultNeverHoldsTheBytes()
        {
            var (r, f) = await Run("grammar", true, Call("ping"), "{\"final\":\"ok\"}");
            Assert.DoesNotContain("QUJDREVGR0g=", r.ToDict().ToString());
            Assert.DoesNotContain("base64", r.ToDict().ToString());
        }

        [Fact]
        public void RedactReplacesTheDataUri()
        {
            var msg = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "x" }, Pic()) };
            string red = ImageParts.Redact(msg).ToString();
            Assert.DoesNotContain("QUJDREVGR0g=", red);
            Assert.Contains("data:image/jpeg;base64,<12 bytes>", red);
            Assert.Contains("QUJDREVGR0g=", msg.ToString());       // the original is untouched
        }
    }

    [Collection("AdultState")]
    public class VisionStewardTests
    {
        private sealed class Gate : IVisionGate { public bool Open; public int Asks; public Task<bool> OpenAsync() { Asks++; return Task.FromResult(Open); } }
        private const string B64 = "QUJDREVGR0g=";

        private static string Call(string t, object a) => Rig.Call(t, a);

        private static async Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld world, object[] script, bool open = true, bool playing = false)
        {
            var (w, st, fake) = await Rig.Boot(world, script, playing);
            w.Steward.Vision = new Gate { Open = open };
            w.Steward.VisionDelay = ms => Task.CompletedTask;
            return (w, st, fake);
        }

        private static string AllText(StewardFake fake) => string.Join("\n", fake.Calls.Select(c => Rig.Json(c)));

        [Fact]
        public async Task OfferedOnAnAdvisorChatTurnWhenTheGateIsOpen()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Rig.Final("The base looks tidy.") });
            Rig.Chat(w, "what does the base look like?");
            await Rig.Cycle(w);
            Assert.Contains("Look at a picture of the colony", Rig.System(fake.Calls[0]));
        }

        [Fact]
        public async Task NotOfferedWhenTheGateIsClosedOrTheSettingIsOffOrThereIsNoBuildBlock()
        {
            Rig.NoEnv();
            foreach (string why in new[] { "gate", "setting", "nobuild" })
            {
                using var world = new JobsWorld();
                var (w, st, fake) = await Boot(world, new object[] { Rig.Final("ok") }, open: why != "gate");
                if (why == "setting") w.Steward.VisionAllowed = () => false;
                if (why == "nobuild") ((JObject)w.Fb.Digest).Remove("build");
                Rig.Chat(w, "hello");
                await Rig.Cycle(w);
                Assert.NotEmpty(fake.Calls);
                Assert.DoesNotContain("Look at a picture", Rig.System(fake.Calls[0]));
            }
        }

        [Fact]
        public async Task TheDirectorsSettingReadsVisionAllowedAndMissingMeansAllowed()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Rig.Final("ok") });
            w.Steward.VisionAllowed = () => !JobsJson.IsFalse(w.BridgeCfg["vision_allowed"]);
            Rig.Chat(w, "hello");
            await Rig.Cycle(w);
            Assert.Contains("Look at a picture", Rig.System(fake.Calls[0]));            // missing = allowed
            w.BridgeCfg["vision_allowed"] = false;
            Rig.Chat(w, "again");
            w.Real += 60;
            await Rig.Cycle(w);
            Assert.DoesNotContain("Look at a picture", Rig.System(fake.Calls.Last()));
        }

        [Fact]
        public async Task NotOfferedOnATradeTurn()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Rig.Boot(world, new object[] { Rig.Final("done") }, true, c => c.StewardMinGapRealS = 0);
            w.Steward.Vision = new Gate { Open = true };
            var d = JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.steward_digest_trader.json"));
            d["food_days"] = 12.0;
            if (d["facts"] is JObject fx) fx["food_days"] = 12.0;
            if (d["build"] == null) d["build"] = StewardTestData.Sample["build"].DeepClone();
            w.Fb.Digest = d;
            await Rig.Cycle(w);
            Assert.Contains((string)st.Stats.Trace[0]["reason"], new[] { "trade" });
            Assert.DoesNotContain("Look at a picture", Rig.System(fake.Calls[0]));
        }

        [Fact]
        public async Task TheNextRequestCarriesOneImageAndTheNextTurnNone()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("look", new { kind = "base" }), Rig.Final("A tidy base with a gap in the north wall.") });
            Rig.Chat(w, "check the walls");
            await Rig.Cycle(w);
            Assert.Equal(2, fake.Calls.Count);
            Assert.DoesNotContain(B64, Rig.Json(new StewardFake.CallRec { Messages = fake.Calls[0].Messages.ToList() }));
            var m = fake.Calls[1].Messages;
            Assert.Equal(1, ImageParts.Count(m));
            var parts = (JArray)m.Last()["content"];
            Assert.Equal("image_url", (string)parts[1]["type"]);
            Assert.Equal("data:image/jpeg;base64," + B64, (string)parts[1]["image_url"]["url"]);
            Assert.Equal(new[] { "A tidy base with a gap in the north wall." }, Rig.Lines(w, "narrator"));
            Assert.Equal(new[] { "base" }, st.Stats.Trace.Last()["looks"].Select(x => (string)x).ToArray());

            // nothing from that picture survives: the trace, the board, the chat
            Assert.DoesNotContain(B64, string.Join("\n", st.Stats.Trace.Select(t => t.ToString())));
            Assert.DoesNotContain(B64, string.Join("\n", w.Fb.Chat.Select(t => t.ToString())));
            Assert.DoesNotContain(B64, w.Fb.GoalsBoard.ToString());

            // the next turn is a fresh loop: no image in any message
            Rig.Chat(w, "and now?");
            w.Real += 60;
            await Rig.Cycle(w);
            Assert.True(fake.Calls.Count >= 3);
            var next = fake.Calls[2].Messages;
            Assert.Equal(0, ImageParts.Count(next));
            Assert.DoesNotContain("base64", string.Join("", next.Select(x => x.ToString())));
        }

        [Fact]
        public async Task ALookIsNotAnActionSoAnActFollowsInAdvisorMode()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("look", new { kind = "wide" }), Call("set_research", new { project = "Smithing" }), Rig.Final("ok") });
            Rig.Chat(w, "look around and pick research");
            await Rig.Cycle(w);
            Assert.Equal(1, w.Fb.ActLog.Count(a => a.Key == "research"));
            var ex = (JArray)st.Stats.Trace.Last()["executed"];
            Assert.StartsWith("Picture attached: wide", (string)ex[0][2]);
        }

        [Fact]
        public async Task ASecondBaseLookIsRefusedAndGetsNoImage()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("look", new { kind = "base" }), Call("look", new { kind = "wide" }), Rig.Final("ok") });
            Rig.Chat(w, "look twice");
            await Rig.Cycle(w);
            Assert.Equal(1, w.Fb.VisionAsks.Count);
            var ex = (JArray)st.Stats.Trace.Last()["executed"];
            Assert.Contains("no more looks this turn", (string)ex[1][2]);
        }

        [Fact]
        public async Task CallLogRowsNeverCarryPictureBytes()
        {
            using var tmp = new TempDir();
            var log = new CallLog(System.IO.Path.Combine(tmp.Path, "calls.jsonl"));
            log.Record(new CallStats { Kind = "toolloop", RequestId = "r1" });
            string text = System.IO.File.ReadAllText(log.Path);
            Assert.DoesNotContain("base64", text);
            Assert.DoesNotContain("image_url", text);
        }
    }
}

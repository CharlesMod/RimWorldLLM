using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    // Ports of agent/tests/test_bridge.py and the bridge parts of test_split.py.
    public class BridgeClientTests
    {
        static (FakeBridge, BridgeClient) Mk(FakeBridge fb = null, string token = "tok")
        {
            fb = fb ?? new FakeBridge("tok");
            return (fb, FakeBridge.Client(fb, token));
        }

        static async Task<BridgeError> Fails(Task t)
        {
            return await Assert.ThrowsAsync<BridgeError>(async () => await t);
        }

        [Fact]
        public async Task EnvelopeAndAuth()
        {
            var (fb, b) = Mk();
            fb.Tick = 777; fb.Speed = 3; fb.Paused = true;
            await b.ConfigAsync();
            Assert.Equal((777, 3, true, 1), (b.Env.Game, b.Env.Speed, b.Env.Paused, b.Env.Epoch));
            Assert.Equal("RimWorldLLM", (string)(await b.HealthAsync())["mod"]);
            var (_, bad) = Mk(fb, "wrong");
            Assert.Equal("unauthorized", (await Fails(bad.ConfigAsync())).Code);
            Assert.Equal("not_found", (await Fails(b.ActorAsync(99))).Code);
        }

        [Fact]
        public async Task WriteOpIdExpiryDuplicate()
        {
            var (fb, b) = Mk(); fb.AddPawn(1, "Ana"); fb.Tick = 1000;
            await b.ConfigAsync();
            var r = await b.SayAsync(1, "Hi.", intent: "kind", expiresAt: 1500, opId: "op-1");
            Assert.Equal("kind", (string)r["applied"]["effect"]);
            var body = fb.SayLog[0];
            Assert.True((string)body["op_id"] == "op-1" && (int)body["expires_tick"] == 1500);
            Assert.Equal("duplicate", (await Fails(b.SayAsync(1, "Hi.", opId: "op-1"))).Code);
            fb.Tick = 2000;
            Assert.Equal("expired", (await Fails(b.SayAsync(1, "late", expiresAt: 1500))).Code);
            await b.SayAsync(1, "player talk never expires");   // no expires_tick sent
            Assert.True(fb.SayLog.Last()["expires_tick"] == null && !string.IsNullOrEmpty((string)fb.SayLog.Last()["op_id"]));
            fb.SayConflictFor.Add(1);
            Assert.Equal("conflict", (await Fails(b.SayAsync(1, "x"))).Code);
        }

        [Fact]
        public async Task PollerPagingAndGap()
        {
            var (fb, b) = Mk();
            var victim = new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(1, "victim") };
            for (int i = 0; i < 450; i++) fb.AddEvent("injury", victim, "e" + i, new[] { 1 });
            var p = new EventPoller(b);
            var evs = await p.PollAsync();
            Assert.Equal(450, evs.Count);
            Assert.Equal(Enumerable.Range(1, 450), evs.Select(e => (int)e["seq"]));
            Assert.True(p.Since == 450 && !p.LastGap);
            Assert.Empty(await p.PollAsync());
            fb.AddEvent("injury", victim, "new", new[] { 1 });
            Assert.Equal(new[] { "new" }, (await p.PollAsync()).Select(e => (string)e["summary"]));
            // gap: the mod only keeps the last N events
            fb.MaxKept = 10;
            fb.Events = fb.Events.Skip(fb.Events.Count - 10).ToList();
            fb.Seq += 5000;
            fb.Events.Last()["seq"] = fb.Seq;
            var p2 = new EventPoller(b, since: 3);
            await p2.PollAsync();
            Assert.True(p2.LastGap && p2.Gaps == 1);
        }

        [Fact]
        public async Task EpochChangeFlaggedAndMindRevConflict()
        {
            var (fb, b) = Mk();
            await b.ConfigAsync();
            Assert.False(b.EpochChanged);
            fb.ReloadGame();
            await b.ConfigAsync();
            Assert.True(b.EpochChanged && b.Env.Epoch == 2);
            var m = await b.GetMindAsync(1);
            Assert.True(m.Key == null && m.Value == 0);
            Assert.Equal(1, await b.PutMindAsync(1, new JObject { ["a"] = 1 }, 0));
            Assert.Equal("conflict", (await Fails(b.PutMindAsync(1, new JObject { ["a"] = 2 }, 0))).Code);
        }

        [Fact]
        public async Task Unreachable()
        {
            var b = new BridgeClient(new FuncTransport(r => throw new BridgeTransportException("no")), null, "t");
            var e = await Fails(b.ConfigAsync());
            Assert.Equal("unreachable", e.Code);
            Assert.Equal("no", e.Detail);
        }

        [Fact]
        public async Task FromConfigReadsBridgeJson()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rwl-bridgejson-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "bridge.json");
                File.WriteAllText(p, "{\"port\": 9766, \"bind\": \"127.0.0.1\", \"token\": \"abc\", \"pid\": 1, \"version\": \"2.0.0\", \"save_epoch\": 1}");
                var r = BridgeLocator.Resolve(null, null, p);
                Assert.True(r.Key == "http://127.0.0.1:9766" && r.Value == "abc");
                await Task.CompletedTask;
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- test_split.py ----
        [Fact]
        public async Task SayRefusalReasonsKeepBubbleAndRecordNothing()
        {
            // the Python used the world fixture (Director + FakeBridge + FakeLLM) but only touched its bridge and fb
            var fb = new FakeBridge("tok");
            fb.AddPawn(1, "Ana", JObject.Parse("{\"traits\": [{\"def\": \"Kind\", \"label\": \"kind\", \"degree\": 0}]}"));
            fb.AddPawn(2, "Bram", JObject.Parse("{\"traits\": [{\"def\": \"Abrasive\", \"label\": \"abrasive\", \"degree\": 0}]}"));
            fb.AddPawn(3, "Cy");
            var b = FakeBridge.Client(fb);
            var r = await b.SayAsync(1, "Cy is hoarding.", listener: 2, intent: "gossip");                 // no about
            Assert.Equal("no_about", (string)r["refused"]["effect"]);
            r = await b.SayAsync(1, "Cy is hoarding.", listener: 2, intent: "gossip", about: 3);
            Assert.True((string)r["applied"]["effect"] == "gossip" && (int)fb.SayLog.Last()["about"] == 3);
            fb.Pawns[2]["mood"]["thoughts"] = new JArray();
            r = await b.SayAsync(1, "There there.", listener: 2, intent: "comfort");
            Assert.Equal("not_negative", (string)r["refused"]["effect"]);                                  // Bram has no bad thought
            fb.Config["effects"] = "off";
            r = await b.SayAsync(1, "Well done.", listener: 2, intent: "kind");
            Assert.Equal("effects_off", (string)r["refused"]["effect"]);
        }

        [Fact]
        public void AdapterAndTransportsSatisfyTheProtocols()
        {
            // test_split.py checked that BridgeClient has every Bridge Protocol method and EventPoller has poll
            foreach (var m in new[] { "HealthAsync", "ConfigAsync", "RosterAsync", "ActorAsync", "SceneAsync", "TalkAsync", "GetMindAsync",
                                      "PutMindAsync", "SayAsync", "ThoughtAsync" })
            {
                Assert.NotNull(typeof(BridgeClient).GetMethod(m));
                Assert.NotNull(typeof(IBridge).GetMethod(m));
            }
            Assert.True(typeof(IBridge).IsAssignableFrom(typeof(BridgeClient)));
            Assert.NotNull(typeof(EventPoller).GetMethod("PollAsync"));
            Assert.NotNull(typeof(IEventSource).GetMethod("PollAsync"));
            Assert.True(typeof(IEventSource).IsAssignableFrom(typeof(EventPoller)));
        }

        [Fact]
        public async Task EveryWriteCarriesAnOpId()
        {
            // bridge-level stand-in for test_every_write_carries_an_op_id (the Director half is deferred)
            var (fb, b) = Mk(); fb.AddPawn(1, "Ana"); fb.AddPawn(2, "Bram"); fb.ArtThings.Add(5);
            await b.SayAsync(1, "a"); await b.ThoughtAsync(1, "t"); await b.SocialActionAsync(1, "avoid", 2);
            await b.ChronicleAsync(1, "c"); await b.RecapAsync(1, new[] { "l" }); await b.ArtAsync(500, "x");
            await b.PostChatAsync("note", "n"); await b.PutGoalsAsync(0); await b.ActAsync("research", new JObject { ["project"] = "p" });
            await b.PutMindAsync(1, new JObject(), 0);
            Assert.All(fb.SayLog.Concat(fb.ThoughtLog).Concat(fb.SocialLog).Concat(fb.ChronicleLog).Concat(fb.RecapLog).Concat(fb.ArtLog),
                body => Assert.False(string.IsNullOrEmpty((string)body["op_id"])));
            Assert.Equal(10, fb.SeenOps.Count);
        }

        // ---- wire shape of BridgeClient (what bridge.py sends) ----
        sealed class Rec : IBridgeTransport
        {
            public List<BridgeRequest> Sent = new List<BridgeRequest>();
            public Func<BridgeRequest, BridgeReply> Reply = r => new BridgeReply(200, "{\"ok\": true}");
            public Task<BridgeReply> SendAsync(BridgeRequest r, System.Threading.CancellationToken ct = default) { Sent.Add(r); return Task.FromResult(Reply(r)); }
            public BridgeRequest Last { get { return Sent.Last(); } }
            public JObject Body { get { return JObject.Parse(Last.JsonBody); } }
        }

        [Fact]
        public async Task HealthSendsNoAuthAndTokenOnlyWhenNonEmpty()
        {
            var t = new Rec();
            var b = new BridgeClient(t, ProtocolNames.RimWorld, "s3");
            await b.HealthAsync();
            Assert.Null(t.Last.Authorization);
            await b.ConfigAsync();
            Assert.Equal("Bearer s3", t.Last.Authorization);
            var b2 = new BridgeClient(t, ProtocolNames.RimWorld, "");
            await b2.ConfigAsync();
            Assert.Null(t.Last.Authorization);
        }

        [Fact]
        public async Task DefaultAndRimWorldProtocolNames()
        {
            var t = new Rec();
            var d = new BridgeClient(t);
            await d.RosterAsync(); Assert.Equal("/roster", t.Last.Path);
            await d.ActorAsync(5); Assert.Equal("/actor/5", t.Last.Path);
            await d.SceneAsync(5); Assert.Equal("/scene/5", t.Last.Path);
            await d.GetMindAsync(5); Assert.Equal("/mind/5", t.Last.Path);
            await d.SayAsync(5, "x", expiresAt: 9);
            Assert.True(t.Body["actor"] != null && (int)t.Body["expires_at"] == 9 && t.Body["expires_tick"] == null);
            var r = new BridgeClient(t, ProtocolNames.RimWorld);
            await r.RosterAsync(); Assert.Equal("/colony", t.Last.Path);
            await r.ActorAsync(5); Assert.Equal("/pawn/5", t.Last.Path);
            await r.SayAsync(5, "x", expiresAt: 9);
            Assert.True(t.Body["pawn"] != null && (int)t.Body["expires_tick"] == 9);
        }

        [Fact]
        public async Task SayBodyHasPythonFieldOrderAndOmitsEmptyOptionals()
        {
            var t = new Rec();
            var b = new BridgeClient(t, ProtocolNames.RimWorld);
            await b.SayAsync(3, "Hi", listener: 4, intent: "kind", conversationId: "c1", combat: true, talkId: "t1", expiresAt: 50,
                opId: "o", about: 7, vanillaRef: "v", kind: "shout");
            Assert.Equal("{\"pawn\":3,\"text\":\"Hi\",\"intent\":\"kind\",\"combat\":true,\"kind\":\"shout\",\"vanilla_ref\":\"v\",\"listener\":4,\"about\":7,\"conversation_id\":\"c1\",\"talk_id\":\"t1\",\"op_id\":\"o\",\"expires_tick\":50}", t.Last.JsonBody);
            await b.SayAsync(3, "Hi", opId: "o");
            Assert.Equal("{\"pawn\":3,\"text\":\"Hi\",\"intent\":\"none\",\"combat\":false,\"op_id\":\"o\"}", t.Last.JsonBody);
            Assert.Equal("POST", t.Last.Method); Assert.Equal("/say", t.Last.Path);
        }

        [Fact]
        public async Task OpIdIsAGuidUnlessGiven()
        {
            var t = new Rec();
            var b = new BridgeClient(t);
            await b.ThoughtAsync(1, "a"); string o1 = (string)t.Body["op_id"];
            await b.ThoughtAsync(1, "a"); string o2 = (string)t.Body["op_id"];
            Assert.True(Guid.TryParse(o1, out _) && Guid.TryParse(o2, out _) && o1 != o2);
            await b.ThoughtAsync(1, "a", opId: "mine");
            Assert.Equal("mine", (string)t.Body["op_id"]);
        }

        [Fact]
        public async Task OtherWritesAndReadsHaveTheContractShapes()
        {
            var t = new Rec();
            var b = new BridgeClient(t, ProtocolNames.RimWorld);
            await b.ThoughtAsync(2, "t", kind: "diary", surface: "s", opId: "o");
            Assert.Equal("{\"pawn\":2,\"text\":\"t\",\"kind\":\"diary\",\"surface\":\"s\",\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.SocialActionAsync(2, "seek_out", 3, "food", "o");
            Assert.Equal("{\"pawn\":2,\"action\":\"seek_out\",\"target\":3,\"topic\":\"food\",\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.SocialActionAsync(2, "avoid", 3, opId: "o");
            Assert.Equal("{\"pawn\":2,\"action\":\"avoid\",\"target\":3,\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.ChronicleAsync(4, "x", opId: "o");
            Assert.Equal("{\"day\":4,\"text\":\"x\",\"kind\":\"day\",\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.RecapAsync(2, new[] { "a", "b" }, "o");
            Assert.Equal("{\"epoch\":2,\"lines\":[\"a\",\"b\"],\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.ArtAsync(9, "hi", "o");
            Assert.Equal("{\"thing\":9,\"text\":\"hi\",\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.PostChatAsync("note", "hey", "o");
            Assert.Equal("{\"who\":\"note\",\"text\":\"hey\",\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.PutGoalsAsync(3, new JObject { ["plan"] = "p", ["log_add"] = new JArray("l") }, "o");
            Assert.Equal("PUT", t.Last.Method); Assert.Equal("/goals", t.Last.Path);
            Assert.Equal("{\"rev\":3,\"plan\":\"p\",\"log_add\":[\"l\"],\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.ActAsync("draft", new JObject { ["pawns"] = new JArray(1), ["drafted"] = true }, 77, "o");
            Assert.Equal("/act/draft", t.Last.Path);
            Assert.Equal("{\"pawns\":[1],\"drafted\":true,\"op_id\":\"o\",\"expires_tick\":77}", t.Last.JsonBody);
            await b.PutMindAsync(6, new JObject { ["k"] = 1 }, 2, "o");
            Assert.Equal("PUT", t.Last.Method); Assert.Equal("/mind/6", t.Last.Path);
            Assert.Equal("{\"rev\":2,\"mind\":{\"k\":1},\"op_id\":\"o\"}", t.Last.JsonBody);
            await b.EventsAsync(12); Assert.Equal("?since=12", t.Last.QueryString()); Assert.Null(t.Last.JsonBody);
            await b.GetChronicleAsync(); Assert.Equal("?since_day=0", t.Last.QueryString());
            await b.ChatSinceAsync(4); Assert.Equal("/chat", t.Last.Path); Assert.Equal("?since=4", t.Last.QueryString());
            await b.ActionsSinceAsync(5); Assert.Equal("/actions", t.Last.Path); Assert.Equal("?since=5", t.Last.QueryString());
            foreach (var (call, path) in new (Func<Task<JObject>>, string)[] { (b.VivariumAsync, "/vivarium"), (b.DigestAsync, "/digest"), (b.GoalsAsync, "/goals") })
            { await call(); Assert.Equal(path, t.Last.Path); Assert.Equal("GET", t.Last.Method); }
            Assert.Empty(await b.TalkAsync());
        }

        [Fact]
        public async Task ErrorMapping()
        {
            var t = new Rec();
            var b = new BridgeClient(t);
            t.Reply = r => new BridgeReply(200, "{\"ok\": false, \"error\": \"refused\", \"detail\": \"no\"}");
            var e = await Fails(b.ConfigAsync());
            Assert.True(e.Code == "refused" && e.Detail == "no" && e.Status == 200 && (string)e.Body["error"] == "refused");
            Assert.Equal("refused: no", e.Message);
            t.Reply = r => new BridgeReply(500, "{\"x\": 1}");
            e = await Fails(b.ConfigAsync());
            Assert.True(e.Code == "http_500" && e.Detail == "" && e.Message == "http_500");
            t.Reply = r => new BridgeReply(502, "<html>");
            e = await Fails(b.ConfigAsync());
            Assert.True(e.Code == "bad_reply" && e.Detail == "HTTP 502 not JSON" && e.Status == 502);
            t.Reply = r => new BridgeReply(200, "");
            Assert.Equal("bad_reply", (await Fails(b.ConfigAsync())).Code);
            t.Reply = r => new BridgeReply(200, "[1]");
            Assert.Equal("bad_reply", (await Fails(b.ConfigAsync())).Code);
            t.Reply = r => new BridgeReply(200, "{\"ok\": null}");      // only the literal false is a refusal
            Assert.NotNull(await b.ConfigAsync());
        }

        [Fact]
        public async Task TransportExceptionsAreUnreachable()
        {
            var b = new BridgeClient(new FuncTransport(r => throw new HttpRequestException("connection refused")));
            var e = await Fails(b.ConfigAsync());
            Assert.True(e.Code == "unreachable" && e.Detail == "connection refused");
            b = new BridgeClient(new FuncTransport(r => throw new BridgeTransportException("")));
            Assert.Equal("BridgeTransportException", (await Fails(b.ConfigAsync())).Detail);   // str(e) or type(e).__name__
        }

        [Fact]
        public async Task EnvelopeIsAbsorbedFromErrorsAndOnlyWithTheTimeField()
        {
            var t = new Rec();
            var b = new BridgeClient(t, ProtocolNames.RimWorld);
            t.Reply = r => new BridgeReply(404, "{\"ok\": false, \"error\": \"not_found\", \"tick\": 42, \"save_epoch\": 5, \"paused\": true, \"speed\": 0}");
            await Fails(b.ActorAsync(1));
            Assert.True(b.Env.Game == 42 && b.Env.Epoch == 5 && b.Env.Paused && b.Env.Speed == 0 && !b.EpochChanged);
            t.Reply = r => new BridgeReply(200, "{\"ok\": true, \"save_epoch\": 9}");   // no tick: not an envelope
            await b.ConfigAsync();
            Assert.True(b.Env.Epoch == 5 && !b.EpochChanged);
            t.Reply = r => new BridgeReply(200, "{\"ok\": true, \"tick\": 43, \"save_epoch\": 6}");
            await b.ConfigAsync();
            Assert.True(b.EpochChanged && b.Env.Speed == 1 && !b.Env.Paused);   // paused/speed default
            t.Reply = r => new BridgeReply(200, "{\"ok\": true, \"tick\": 44, \"save_epoch\": 6}");
            await b.ConfigAsync();
            Assert.True(b.EpochChanged);   // sticky until the director clears it
            b.EpochChanged = false;
            await b.ConfigAsync();
            Assert.False(b.EpochChanged);
        }

        [Fact]
        public async Task MindAndTalkAndChronicleParsing()
        {
            var (fb, b) = Mk(); fb.AddPawn(1, "Ana");
            fb.TalkQueue.Add(JObject.Parse("{\"id\": \"t1\"}"));
            Assert.Single(await b.TalkAsync());
            await b.PutMindAsync(1, new JObject { ["a"] = 1 }, 0);
            var m = await b.GetMindAsync(1);
            Assert.True((int)m.Key["a"] == 1 && m.Value == 1);
            await b.ChronicleAsync(3, "later"); await b.ChronicleAsync(1, "earlier");
            Assert.Equal(new[] { "later" }, (await b.GetChronicleAsync(2)).Select(e => (string)e["text"]));
        }

        [Fact]
        public void ReplyDatesStayStrings()
        {
            var j = (JObject)BridgeJson.Parse("{\"d\": \"2020-01-02T03:04:05Z\"}");
            Assert.Equal(JTokenType.String, j["d"].Type);
            Assert.Equal("2020-01-02T03:04:05Z", (string)j["d"]);
        }

        // ---- bridge.json ----
        [Fact]
        public void LocatorExplicitUrlWinsAndNeverReadsTheFile()
        {
            var r = BridgeLocator.Resolve("http://h:1", null, "/no/such/file.json");
            Assert.True(r.Key == "http://h:1" && r.Value == "");
            r = BridgeLocator.Resolve("http://h:1", "tk", "/no/such/file.json");
            Assert.Equal("tk", r.Value);
        }

        [Theory]
        [InlineData("0.0.0.0", "127.0.0.1")]
        [InlineData("::", "127.0.0.1")]
        [InlineData("192.168.1.9", "192.168.1.9")]
        [InlineData(null, "127.0.0.1")]
        public void LocatorRewritesWildcardBinds(string bind, string host)
        {
            string dir = Path.Combine(Path.GetTempPath(), "rwl-bridgejson-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "bridge.json");
                File.WriteAllText(p, new JObject { ["port"] = 9770, ["bind"] = bind, ["token"] = "ft" }.ToString());
                Assert.Equal("http://" + host + ":9770", BridgeLocator.Resolve(null, null, p).Key);
                Assert.Equal("ft", BridgeLocator.Resolve(null, null, null, p).Value);              // default path used when none given
                Assert.Equal("ov", BridgeLocator.Resolve(null, "ov", p).Value);                    // configured token beats the file's
                Assert.Equal("ft", BridgeLocator.Resolve("", "", p).Value);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void LocatorMissingFileThrows()
        {
            Assert.ThrowsAny<IOException>(() => BridgeLocator.Resolve(null, null, "/no/such/bridge.json"));
        }

        // ---- protocol presets ----
        [Fact]
        public void ProtocolPresets()
        {
            var d = ProtocolNames.Default;
            Assert.Equal(("/actor", "/scene", "/mind", "/roster", "actor", "time", "epoch", "expires_at"),
                (d.ActorPath, d.ScenePath, d.MindPath, d.RosterPath, d.ActorField, d.TimeField, d.EpochField, d.ExpiryField));
            var r = ProtocolNames.RimWorld;
            Assert.Equal(("/pawn", "/scene", "/mind", "/colony", "pawn", "tick", "save_epoch", "expires_tick"),
                (r.ActorPath, r.ScenePath, r.MindPath, r.RosterPath, r.ActorField, r.TimeField, r.EpochField, r.ExpiryField));
        }
    }
}

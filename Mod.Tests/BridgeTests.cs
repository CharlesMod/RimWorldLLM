using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    static class Net
    {
        static int _next = 24000 + Environment.ProcessId % 2000 * 10;
        public static int FreeBase() { lock (typeof(Net)) { _next += 12; return _next; } }
    }

    sealed class Harness : IDisposable
    {
        public double Now;
        public readonly OpIdStore Ids;
        public readonly ApiRouter Router;
        public BridgeServer Server;
        public string Dir = Path.Combine(Path.GetTempPath(), "hv-test-" + Guid.NewGuid().ToString("N"));
        public GameState State = new GameState { GameLoaded = true, Tick = 1000, SaveEpoch = 3, Paused = false, Speed = 1 };
        public Harness()
        {
            Ids = new OpIdStore(() => Now);
            Router = new ApiRouter(Ids);
            Router.Add("GET", "/ping", false, c => ApiResult.Success(new JObject { ["pong"] = true }));
            Router.Add("GET", "/game", true, c => ApiResult.Success());
            Router.Add("GET", "/boom", false, c => throw new InvalidOperationException("kaboom"));
            Router.Add("POST", "/w", true, c => ApiResult.Success(new JObject { ["n"] = ++Writes }));
            Router.Add("POST", "/fail", true, c => ++Fails < 2 ? ApiResult.Fail("conflict", "dead") : ApiResult.Success());
            Router.Add("GET", "/pawn/{id}", true, c => ApiResult.Success(new JObject { ["id"] = c.Args["id"] }));
        }
        public int Writes, Fails;
        public ApiResult Call(string method, string path, string body = null, GameState s = null)
        {
            string q = ""; int i = path.IndexOf('?');
            if (i >= 0) { q = path.Substring(i + 1); path = path.Substring(0, i); }
            return Router.Handle(method, path, q, body, s ?? State);
        }
        public bool StartServer(string token, int first, int last = -1)
        {
            Server = new BridgeServer(new BridgeOptions { Token = token, FirstPort = first, LastPort = last < 0 ? first + 9 : last, ConfigDir = Dir, Version = "t" }, Router);
            Server.SetState(State);
            return Server.Start();
        }
        public void Dispose() { Server?.Stop(); try { Directory.Delete(Dir, true); } catch { } }
    }

    public class EnvelopeTests
    {
        [Fact]
        public void EveryReplyCarriesTheEnvelope()
        {
            var h = new Harness();
            var s = new GameState { GameLoaded = true, Tick = 55, SaveEpoch = 7, Paused = true, Speed = 0 };
            var j = JObject.Parse(Envelope.Json(s, h.Call("GET", "/ping", null, s)));
            Assert.True((bool)j["ok"]); Assert.Equal(55, (int)j["tick"]); Assert.Equal(7, (int)j["save_epoch"]);
            Assert.True((bool)j["paused"]); Assert.Equal(0, (int)j["speed"]); Assert.True((bool)j["pong"]);
        }

        [Fact]
        public void ErrorsCarryCodeDetailAndEnvelope()
        {
            var h = new Harness();
            var r = h.Call("GET", "/nope");
            var j = Envelope.Wrap(h.State, r);
            Assert.False((bool)j["ok"]); Assert.Equal("not_found", (string)j["error"]); Assert.Equal(404, r.Status);
            Assert.Equal(3, (int)j["save_epoch"]);
        }

        [Theory]
        [InlineData("unauthorized", 401)] [InlineData("not_found", 404)] [InlineData("bad_request", 400)] [InlineData("too_large", 413)]
        [InlineData("conflict", 409)] [InlineData("expired", 410)] [InlineData("duplicate", 409)] [InlineData("no_game", 503)]
        [InlineData("busy", 503)] [InlineData("timeout", 504)] [InlineData("internal", 500)]
        public void ErrorCodeStatus(string code, int status) { Assert.Equal(status, Envelope.StatusFor(code)); }

        [Fact]
        public void NoGameInTheMenu()
        {
            var h = new Harness();
            var r = h.Call("GET", "/game", null, GameState.Menu);
            Assert.Equal("no_game", r.Error); Assert.Equal(503, r.Status);
            Assert.True(h.Call("GET", "/ping", null, GameState.Menu).Ok);
            var j = Envelope.Wrap(GameState.Menu, r);
            Assert.Equal(0, (int)j["save_epoch"]);
        }

        [Fact]
        public void HandlerExceptionIsInternal()
        {
            var r = new Harness().Call("GET", "/boom");
            Assert.Equal("internal", r.Error); Assert.Equal(500, r.Status); Assert.Contains("kaboom", r.Body.ToString());
        }

        [Fact]
        public void PathArgsAreParsed()
        {
            Assert.Equal("42", (string)new Harness().Call("GET", "/pawn/42").Body["id"]);
        }
    }

    public class WriteTests
    {
        [Fact]
        public void OpIdIsRequiredAndBodyMustBeObject()
        {
            var h = new Harness();
            Assert.Equal("bad_request", h.Call("POST", "/w", "{}").Error);
            Assert.Equal("bad_request", h.Call("POST", "/w", "[1]").Error);
            Assert.Equal("bad_request", h.Call("POST", "/w", "nope").Error);
            Assert.Equal("bad_request", h.Call("POST", "/w", null).Error);
        }

        [Fact]
        public void RepeatedOpIdIsDuplicateWithOriginalResult()
        {
            var h = new Harness();
            Assert.True(h.Call("POST", "/w", "{\"op_id\":\"a\"}").Ok);
            var r = h.Call("POST", "/w", "{\"op_id\":\"a\"}");
            Assert.Equal("duplicate", r.Error); Assert.Equal(409, r.Status);
            Assert.Equal(1, (int)r.Body["result"]["n"]);
            Assert.Equal(1, h.Writes);
        }

        [Fact]
        public void OpIdsAreForgottenAfterTenMinutes()
        {
            var h = new Harness();
            h.Call("POST", "/w", "{\"op_id\":\"a\"}");
            h.Now = 599; Assert.Equal("duplicate", h.Call("POST", "/w", "{\"op_id\":\"a\"}").Error);
            h.Now = 601; Assert.True(h.Call("POST", "/w", "{\"op_id\":\"a\"}").Ok);
            Assert.Equal(2, h.Writes);
        }

        [Fact]
        public void FailedWritesAreNotRemembered()
        {
            var h = new Harness();
            Assert.Equal("conflict", h.Call("POST", "/fail", "{\"op_id\":\"x\"}").Error);
            Assert.True(h.Call("POST", "/fail", "{\"op_id\":\"x\"}").Ok);
        }

        [Fact]
        public void ExpiredTickIsRefusedAndNotRemembered()
        {
            var h = new Harness();
            var r = h.Call("POST", "/w", "{\"op_id\":\"e\",\"expires_tick\":999}");
            Assert.Equal("expired", r.Error); Assert.Equal(410, r.Status); Assert.Equal(0, h.Writes);
            Assert.True(h.Call("POST", "/w", "{\"op_id\":\"e\",\"expires_tick\":1000}").Ok);
            Assert.True(h.Call("POST", "/w", "{\"op_id\":\"f\"}").Ok); // no expiry = never expires
            Assert.True(h.Call("POST", "/w", "{\"op_id\":\"g\",\"expires_tick\":null}").Ok);
            Assert.Equal("bad_request", h.Call("POST", "/w", "{\"op_id\":\"h\",\"expires_tick\":\"x\"}").Error);
        }
    }

    public class EventRingTests
    {
        static JObject Ev(string t) { return new JObject { ["type"] = t }; }

        [Fact]
        public void SeqIsStampedAndSinceReturnsLaterEvents()
        {
            var r = new EventRing();
            Assert.Equal(1, r.Add(Ev("a"))); r.Add(Ev("b")); r.Add(Ev("c"));
            var p = r.Since(1);
            Assert.Equal(2, p.Events.Count); Assert.Equal(2, (long)p.Events[0]["seq"]); Assert.Equal(3, p.NextSeq); Assert.False(p.Gap);
            Assert.Empty(r.Since(3).Events);
        }

        [Fact]
        public void OldCursorReportsGap()
        {
            var r = new EventRing(5);
            for (int i = 0; i < 12; i++) r.Add(Ev("e"));
            Assert.Equal(5, r.Count);
            var p = r.Since(2);
            Assert.True(p.Gap); Assert.Equal(5, p.Events.Count); Assert.Equal(8, (long)p.Events[0]["seq"]);
            Assert.False(r.Since(7).Gap); // 7 is the last evicted, so nothing is missed
        }

        [Fact]
        public void CursorFromTheFutureIsAGap()
        {
            var r = new EventRing(); r.Add(Ev("a"));
            Assert.True(r.Since(50).Gap);
        }

        [Fact]
        public void PagesAreCappedAndResumable()
        {
            var r = new EventRing(2000);
            for (int i = 0; i < 450; i++) r.Add(Ev("e"));
            var p = r.Since(0, 200);
            Assert.Equal(200, p.Events.Count); Assert.Equal(200, p.NextSeq);
            var q = r.Since(p.NextSeq, 200);
            Assert.Equal(201, (long)q.Events[0]["seq"]);
        }

        [Fact]
        public void RingKeepsTwoThousand()
        {
            var r = new EventRing();
            for (int i = 0; i < 2500; i++) r.Add(Ev("e"));
            Assert.Equal(2000, r.Count); Assert.Equal(2500, r.LastSeq);
        }

        [Fact]
        public void ClearKeepsSeqCounting()
        {
            var r = new EventRing(); r.Add(Ev("a")); r.Clear();
            Assert.Equal(2, r.Add(Ev("b")));
        }
    }

    public class ConnectionTests
    {
        static async Task<(int status, JObject body)> Get(int port, string path, string token = null, string header = null)
        {
            using (var c = new HttpClient())
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + path);
                if (header != null) req.Headers.TryAddWithoutValidation("Authorization", header);
                else if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                var resp = await c.SendAsync(req);
                return ((int)resp.StatusCode, JObject.Parse(await resp.Content.ReadAsStringAsync()));
            }
        }

        static async Task<T> WithPump<T>(BridgeServer s, Task<T> t)
        {
            while (!t.IsCompleted) { s.Pump(2); await Task.Delay(5); }
            return await t;
        }

        [Fact]
        public async Task HealthNeedsNoAuth()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("secret", Net.FreeBase()));
                var (st, j) = await Get(h.Server.Port, "/health");
                Assert.Equal(200, st); Assert.Equal("HermesVoice", (string)j["mod"]); Assert.True((bool)j["game_loaded"]);
                Assert.Equal(3, (int)j["save_epoch"]);
            }
        }

        [Fact]
        public async Task AuthIsMandatory()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("secret", Net.FreeBase()));
                int p = h.Server.Port;
                Assert.Equal(401, (await Get(p, "/ping")).status);
                Assert.Equal("unauthorized", (string)(await Get(p, "/ping", "wrong")).body["error"]);
                Assert.Equal(401, (await Get(p, "/ping", header: "secret")).status); // not a Bearer header
                var ok = await WithPump(h.Server, Get(p, "/ping", "secret"));
                Assert.Equal(200, ok.status); Assert.True((bool)ok.body["pong"]);
            }
        }

        [Fact]
        public async Task QueryTokenOnlyOnGet()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("secret", Net.FreeBase()));
                int p = h.Server.Port;
                var ok = await WithPump(h.Server, Get(p, "/ping?token=secret"));
                Assert.Equal(200, ok.status);
                using (var c = new HttpClient())
                {
                    var r = await c.PostAsync("http://127.0.0.1:" + p + "/w?token=secret", new StringContent("{\"op_id\":\"a\"}"));
                    Assert.Equal(401, (int)r.StatusCode);
                }
            }
        }

        [Fact]
        public async Task OversizedBodyIsRefused()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("secret", Net.FreeBase()));
                using (var c = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + h.Server.Port + "/w");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
                    req.Content = new StringContent(new string('x', Const.MaxBodyBytes + 10));
                    var r = await c.SendAsync(req);
                    Assert.Equal(413, (int)r.StatusCode);
                }
            }
        }

        [Fact]
        public void RefusesToStartWithoutToken()
        {
            using (var h = new Harness())
            {
                Assert.False(h.StartServer("", Net.FreeBase()));
                Assert.False(h.Server.IsRunning);
                Assert.Contains("token", h.Server.LastError);
                Assert.False(File.Exists(BridgeInfo.PathIn(h.Dir)));
                Assert.False(h.StartServer("   ", Net.FreeBase()));
            }
        }

        [Fact]
        public void PortFallsBackWhenTaken()
        {
            int b = Net.FreeBase();
            var squatter = new TcpListener(IPAddress.Loopback, b);
            squatter.Start();
            try
            {
                using (var h = new Harness())
                {
                    Assert.True(h.StartServer("t", b, b + 9));
                    Assert.Equal(b + 1, h.Server.Port);
                }
            }
            finally { squatter.Stop(); }
        }

        [Fact]
        public void FailsWhenWholeRangeIsTaken()
        {
            int b = Net.FreeBase();
            var a = new TcpListener(IPAddress.Loopback, b); a.Start();
            var c = new TcpListener(IPAddress.Loopback, b + 1); c.Start();
            try
            {
                using (var h = new Harness())
                {
                    Assert.False(h.StartServer("t", b, b + 1));
                    Assert.Contains("no free port", h.Server.LastError);
                }
            }
            finally { a.Stop(); c.Stop(); }
        }

        [Fact]
        public void DefaultsAre9765To9774()
        {
            var o = new BridgeOptions();
            Assert.Equal(9765, o.FirstPort); Assert.Equal(9774, o.LastPort);
        }

        [Fact]
        public void BridgeJsonIsWrittenOnStartAndOnEpochChange()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("tok", Net.FreeBase()));
                var j = JObject.Parse(File.ReadAllText(BridgeInfo.PathIn(h.Dir)));
                Assert.Equal(h.Server.Port, (int)j["port"]); Assert.Equal("tok", (string)j["token"]);
                Assert.Equal(3, (int)j["save_epoch"]); Assert.Equal("t", (string)j["version"]);
                Assert.Equal(Environment.ProcessId, (int)j["pid"]);
                Assert.NotNull(j["bind"]);
                h.Server.SetState(new GameState { GameLoaded = true, Tick = 5, SaveEpoch = 4 });
                Assert.Equal(4, (int)JObject.Parse(File.ReadAllText(BridgeInfo.PathIn(h.Dir)))["save_epoch"]);
                Assert.False(File.Exists(BridgeInfo.PathIn(h.Dir) + ".tmp"));
            }
        }

        [Fact]
        public void BridgeJsonPath()
        {
            Assert.Equal(Path.Combine("cfg", "HermesVoice", "bridge.json"), BridgeInfo.PathIn("cfg"));
        }

        [Fact]
        public void SavedOldPortIsMigrated()
        {
            Assert.Equal(9765, SettingsMigration.Port(8765));
            Assert.Equal(9770, SettingsMigration.Port(9770));
            Assert.Equal(9000, SettingsMigration.Port(9000));
        }

        [Fact]
        public void TokensAreRandomAndLongAndCompareConstantTime()
        {
            var a = Secure.NewToken(); var b = Secure.NewToken();
            Assert.NotEqual(a, b); Assert.True(a.Length >= 32);
            Assert.True(Secure.Equals(a, a)); Assert.False(Secure.Equals(a, b)); Assert.False(Secure.Equals(a, null));
        }
    }

    public class StateTests
    {
        [Fact]
        public void MindBookRevAndLimit()
        {
            var m = new MindBook(); int rev;
            Assert.Equal(0, (int)m.Get(1)["rev"]); Assert.Equal(JTokenType.Null, m.Get(1)["mind"].Type);
            Assert.Equal(MindBook.PutResult.Ok, m.Put(1, 0, JObject.Parse("{\"a\":1}"), out rev)); Assert.Equal(1, rev);
            Assert.Equal(MindBook.PutResult.Conflict, m.Put(1, 0, JObject.Parse("{}"), out rev)); Assert.Equal(1, rev);
            Assert.Equal(1, (int)m.Get(1)["mind"]["a"]);
            var big = new JObject { ["x"] = new string('y', MindBook.MaxMindBytes) };
            Assert.Equal(MindBook.PutResult.TooLarge, m.Put(1, 1, big, out rev));
            Assert.Equal(MindBook.PutResult.Ok, m.Put(1, 1, JValue.CreateNull(), out rev));
            Assert.Equal(JTokenType.Null, m.Get(1)["mind"].Type);
        }

        [Fact]
        public void InnerThoughtsKeepTheLastTwenty()
        {
            var m = new MindBook();
            for (int i = 0; i < 30; i++) m.AddThought(2, i, "t" + i);
            Assert.Equal(20, m.Records[2].Thoughts.Count); Assert.Equal("t10", m.Records[2].Thoughts[0].Value);
        }

        [Fact]
        public void TalkQueueRemovesWhenAnswered()
        {
            var q = new TalkQueue();
            string a = q.Add(1, "hi", "player"); q.Add(2, "yo", "mod");
            Assert.Equal(2, q.ToJson().Count);
            Assert.True(q.Remove(a)); Assert.False(q.Remove(a));
            Assert.Single(q.ToJson());
        }

        [Fact]
        public void LedgerOpensAndCloses()
        {
            var l = new Ledger();
            string id = l.NextId("raid");
            var e = l.OpenEntry("raid", "raid:" + id, 10, 0, "Pirates", null, null, id);
            Assert.Equal(id, e.Id); Assert.Same(e, l.Find("raid:" + id)); Assert.Single(l.Open);
            Assert.NotNull(l.Close("raid:" + id)); Assert.Empty(l.Open); Assert.NotEqual(id, l.NextId("raid"));
        }

        [Fact]
        public void TrendsSampleOncePerInterval()
        {
            var t = new Trends(3, 15000);
            Assert.True(t.Sample("food", 0, 9)); Assert.False(t.Sample("food", 100, 8));
            t.Sample("food", 15000, 7); t.Sample("food", 30000, 5); t.Sample("food", 45000, 3);
            Assert.Equal(new[] { 7f, 5f, 3f }, t.Get("food"));
        }

        [Fact]
        public void WitnessRuleAndPairCap()
        {
            Assert.True(Rules.Witnessed(10f, 1f, true, false));
            Assert.False(Rules.Witnessed(11f, 1f, true, true));
            Assert.False(Rules.Witnessed(5f, 1f, false, false));
            Assert.False(Rules.Witnessed(8f, 0.5f, true, true));
            var c = new PairCap(2);
            Assert.True(c.TryConsume(1, 2, 5)); Assert.True(c.TryConsume(1, 2, 5)); Assert.False(c.TryConsume(1, 2, 5));
            Assert.True(c.TryConsume(1, 2, 6));
            Assert.True(c.TryConsume(2, 1, 6));
        }

        [Fact]
        public void EffectTableMagnitudes()
        {
            Assert.Equal(0f, EffectTable.Strength("off")); Assert.Equal(0.5f, EffectTable.Strength("light")); Assert.Equal(1f, EffectTable.Strength("normal"));
            Assert.True(EffectTable.Known("comfort")); Assert.False(EffectTable.Known("zzz"));
            foreach (var i in EffectTable.Intents) if (i != "none") Assert.NotEmpty(EffectTable.For(i));
            Assert.Empty(EffectTable.For("none"));
        }
    }
}

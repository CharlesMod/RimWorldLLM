using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    // The in-process door of BridgeServer (SubmitAsync) through InProcessTransport, against a real ApiRouter.
    public class InProcessTransportTests : IDisposable
    {
        readonly Harness _h = new Harness();
        BridgeServer _s;
        int _calls;

        public void Dispose() { _s?.Stop(); _h.Dispose(); }

        BridgeServer Server(Action<BridgeOptions> tune = null, bool open = true)
        {
            var opt = new BridgeOptions { Token = "unused", GameThreadTimeout = TimeSpan.FromSeconds(5), Version = "t" };
            tune?.Invoke(opt);
            _h.Router.Add("GET", "/config", false, c => { _calls++; return ApiResult.Success(new JObject { ["since"] = c.Q("since"), ["a"] = 1 }); });
            _h.Router.Add("POST", "/thought", true, c => { _calls++; return ApiResult.Success(new JObject { ["n"] = _calls }); });
            _h.Router.Add("POST", "/say", true, c => { _calls++; return ApiResult.Success(); });
            _h.Router.Add("GET", "/events", true, c => { _calls++; return ApiResult.Success(new JObject { ["since"] = c.Q("since") }); });
            _s = new BridgeServer(opt, _h.Router);
            _s.SetState(_h.State);
            if (open) _s.EnableInProcess();
            return _s;
        }

        BridgeClient Client(BridgeServer s = null)
        {
            s = s ?? _s;
            return new BridgeClient(new InProcessTransport(s), RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames);
        }

        static async Task<T> Pumped<T>(BridgeServer s, Task<T> t)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!t.IsCompleted && DateTime.UtcNow < until) { s.Pump(2); await Task.Delay(2); }
            return await t;
        }

        static async Task<BridgeError> Fails(Task t) { return await Assert.ThrowsAsync<BridgeError>(async () => await t); }

        [Fact]
        public async Task PumpServedSuccessAbsorbsTheEnvelope()
        {
            var s = Server();
            Assert.False(s.IsRunning);   // no HTTP listener at all
            var b = Client();
            var r = await Pumped(s, b.ConfigAsync());
            Assert.Equal(1, (int)r["a"]);
            Assert.True(b.Env.Game == 1000 && b.Env.Epoch == 3 && !b.Env.Paused && b.Env.Speed == 1);
            s.SetState(new GameState { GameLoaded = true, Tick = 2500, SaveEpoch = 4, Paused = true, Speed = 3 });
            await Pumped(s, b.ConfigAsync());
            Assert.True(b.Env.Game == 2500 && b.Env.Epoch == 4 && b.Env.Paused && b.Env.Speed == 3 && b.EpochChanged);
        }

        [Fact]
        public async Task QueryStringReachesTheHandler()
        {
            var s = Server();
            var b = Client();
            Assert.Equal("17", (string)(await Pumped(s, b.EventsAsync(17)))["since"]);
        }

        [Fact]
        public async Task NothingRunsUntilTheGameThreadPumps()
        {
            var s = Server();
            var t = Client().ConfigAsync();
            await Task.Delay(100);
            Assert.False(t.IsCompleted);          // no thread blocked, nothing served
            Assert.Equal(1, s.PendingCount);
            Assert.Equal(0, _calls);
            s.Pump(2);
            await t;
            Assert.Equal(1, _calls);
        }

        [Fact]
        public async Task ContinuationsDoNotRunOnThePumpThread()
        {
            var s = Server();
            int pumpThread = -1, afterThread = -2;
            var t = Task.Run(async () =>
            {
                await new InProcessTransport(s).SendAsync(new BridgeRequest { Path = "/config" }).ConfigureAwait(false);
                afterThread = Environment.CurrentManagedThreadId;
            });
            var th = new Thread(() =>
            {
                pumpThread = Environment.CurrentManagedThreadId;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (!t.IsCompleted && DateTime.UtcNow < until) { s.Pump(2); Thread.Sleep(2); }
            });
            th.Start();
            await t;
            th.Join();
            Assert.NotEqual(pumpThread, afterThread);
        }

        [Fact]
        public async Task WriteDedupAnswers409WithTheOriginalResult()
        {
            var s = Server();
            var b = Client();
            var first = await Pumped(s, b.ThoughtAsync(1, "hm", opId: "same"));
            Assert.Equal(1, (int)first["n"]);
            var e = await Fails(Pumped(s, b.ThoughtAsync(1, "hm", opId: "same")));
            Assert.True(e.Code == "duplicate" && e.Status == 409 && (int)e.Body["result"]["n"] == 1);
            Assert.Equal(1, _calls);              // the handler ran once
        }

        [Fact]
        public async Task ExpiredWriteAnswers410AndNeverRuns()
        {
            var s = Server();
            var b = Client();
            var e = await Fails(Pumped(s, b.SayAsync(1, "late", expiresAt: 999)));   // state tick is 1000
            Assert.True(e.Code == "expired" && e.Status == 410);
            Assert.Equal(0, _calls);
            await Pumped(s, b.SayAsync(1, "in time", expiresAt: 1000));
            Assert.Equal(1, _calls);
        }

        [Fact]
        public async Task NoGameAnswers503()
        {
            var s = Server();
            s.SetState(GameState.Menu);
            var e = await Fails(Pumped(s, Client().ThoughtAsync(1, "x")));
            Assert.True(e.Code == "no_game" && e.Status == 503);
            Assert.Equal(0, _calls);
            Assert.NotNull(await Pumped(s, Client().ConfigAsync()));   // a route that needs no game still answers
        }

        [Fact]
        public async Task ClosedDoorAnswers503NoGameAndStopClosesIt()
        {
            var s = Server(open: false);
            var e = await Fails(Client().ConfigAsync());
            Assert.True(e.Code == "no_game" && e.Status == 503);
            s.EnableInProcess();
            await Pumped(s, Client().ConfigAsync());
            s.Stop();
            Assert.Equal("no_game", (await Fails(Client().ConfigAsync())).Code);
        }

        [Fact]
        public async Task StopAnswersQueuedRequests503()
        {
            var s = Server();
            var t = Client().ConfigAsync();
            await Task.Delay(30);
            s.Stop();
            var e = await Fails(t);
            Assert.True(e.Code == "no_game" && e.Status == 503);
        }

        [Fact]
        public async Task StalledGameThreadAnswersBusyAtOnce()
        {
            var s = Server(o => { o.StalledAfter = TimeSpan.FromMilliseconds(40); o.GameThreadTimeout = TimeSpan.FromSeconds(30); });
            s.Pump(2);                            // the game thread has run once...
            await Task.Delay(120);                // ...and then went quiet
            Assert.True(s.GameThreadStalled);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var e = await Fails(Client().ConfigAsync());
            Assert.True(e.Code == "busy" && e.Status == 503);
            Assert.True(sw.ElapsedMilliseconds < 2000);
            Assert.Equal(0, s.PendingCount);
            s.Pump(2);                            // frames again: served
            await Pumped(s, Client().ConfigAsync());
        }

        [Fact]
        public async Task TimeoutAbandonsTheRequest()
        {
            var s = Server(o => o.GameThreadTimeout = TimeSpan.FromMilliseconds(120));
            var e = await Fails(Client().SayAsync(1, "x"));
            Assert.True(e.Code == "timeout" && e.Status == 504);
            Assert.Equal(1, s.PendingCount);      // still queued, but abandoned
            Assert.Equal(0, s.Pump(2));           // the pump skips it: not claimed, not handled
            Assert.Equal(0, _calls);
        }

        [Fact]
        public async Task CancellationAbandonsTheRequest()
        {
            var s = Server();
            var cts = new CancellationTokenSource();
            var t = new InProcessTransport(s).SendAsync(new BridgeRequest { Method = "POST", Path = "/say", JsonBody = "{\"op_id\":\"a\"}" }, cts.Token);
            await Task.Delay(30);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await t);
            Assert.Equal(0, s.Pump(2));
            Assert.Equal(0, _calls);
        }

        [Fact]
        public async Task OversizedBodyAnswers413AndHealthNeedsNoPump()
        {
            var s = Server();
            var big = new JObject { ["op_id"] = "a", ["text"] = new string('x', Const.MaxBodyBytes) };
            var r = await new InProcessTransport(s).SendAsync(new BridgeRequest { Method = "POST", Path = "/say", JsonBody = big.ToString() });
            Assert.Equal(413, r.Status);
            Assert.Equal("too_large", (string)JObject.Parse(r.Body)["error"]);
            var h = await Client().HealthAsync();   // answered from the published state, no Pump
            Assert.Equal("RimWorldLLM", (string)h["mod"]);
            Assert.True((bool)h["game_loaded"]);
        }

        [Fact]
        public async Task TrustedNoTokenNeeded()
        {
            var s = Server();
            var r = await Pumped(s, new InProcessTransport(s).SendAsync(new BridgeRequest { Path = "/config", Authorization = "Bearer wrong" }));
            Assert.Equal(200, r.Status);
            Assert.True(s.Requests.SecondsSinceAgent(DateTime.UtcNow.Ticks) >= 0);   // the mind counts as the agent
        }

        [Fact]
        public async Task ReplyBodyIsTheSameJsonAsHttp()
        {
            var s = Server();
            var r = await Pumped(s, new InProcessTransport(s).SendAsync(new BridgeRequest { Path = "/config" }));
            Assert.Equal(Bridge.Envelope.Json(_h.State, ApiResult.Success(new JObject { ["since"] = null, ["a"] = 1 })), r.Body);
        }

        [Fact]
        public async Task HttpTransportTalksToTheSameServer()
        {
            var h = new Harness();
            try
            {
                h.Router.Add("GET", "/config", false, c => ApiResult.Success(new JObject { ["a"] = 2 }));
                Assert.True(h.StartServer("secret", Net.FreeBase()));
                string url = "http://127.0.0.1:" + h.Server.Port;
                using (var http = new HttpBridgeTransport(url))
                {
                    var b = new BridgeClient(http, RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames, "secret");
                    Assert.Equal("RimWorldLLM", (string)(await b.HealthAsync())["mod"]);
                    Assert.Equal(2, (int)(await Pumped(h.Server, b.ConfigAsync()))["a"]);
                    Assert.Equal(1000, b.Env.Game);
                    var bad = new BridgeClient(http, RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames, "wrong");
                    Assert.Equal("unauthorized", (await Fails(bad.ConfigAsync())).Code);
                }
                int port = h.Server.Port;
                h.Server.Stop();
                using (var http = new HttpBridgeTransport("http://127.0.0.1:" + port, TimeSpan.FromSeconds(2)))
                    Assert.Equal("unreachable", (await Fails(new BridgeClient(http).ConfigAsync())).Code);
            }
            finally { h.Dispose(); }
        }

        // ---- BridgeRuntime: pumping runs whether or not HTTP is on ----
        sealed class TestGame : RimWorldLLM.Bridge.IGameAdapter
        {
            public GameState State = new GameState { GameLoaded = true, Tick = 77, SaveEpoch = 2, Speed = 2 };
            public GameState BuildState() { return State; }
            public void Info(string m) { }
            public void Warn(string m) { }
            public void Error(string m) { }
        }

        [Fact]
        public async Task RuntimeServesInProcessWithoutHttpAndAcrossHttpRestarts()
        {
            var game = new TestGame();
            var rt = new BridgeRuntime(game, () => 0);
            rt.Router.Add("GET", "/config", false, c => ApiResult.Success(new JObject { ["a"] = 1 }));
            Assert.Null(rt.Server);
            rt.EnableInProcess();
            Assert.False(rt.IsRunning);
            var b = new BridgeClient(new InProcessTransport(() => rt.Server), RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames);
            async Task<JObject> Frame(Task<JObject> t)
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!t.IsCompleted && DateTime.UtcNow < until) { rt.FrameUpdate(); await Task.Delay(2); }
                return await t;
            }
            Assert.Equal(1, (int)(await Frame(b.ConfigAsync()))["a"]);
            Assert.True(b.Env.Game == 77 && b.Env.Epoch == 2 && b.Env.Speed == 2);
            // HTTP comes on, then goes off: the in-process door stays
            Assert.True(rt.Start(new BridgeOptions { Token = "t", FirstPort = Net.FreeBase(), LastPort = 65535 }));
            Assert.True(rt.IsRunning);
            await Frame(b.ConfigAsync());
            rt.Stop();
            Assert.False(rt.IsRunning);
            Assert.NotNull(rt.Server);
            Assert.Equal(1, (int)(await Frame(b.ConfigAsync()))["a"]);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>A fake of the host's own engine (loopback calls) for ShareHost.</summary>
    sealed class FakeShareApi : IShareHostApi
    {
        public bool? Share;                 // null: an engine without share mode
        public List<string> Addresses = new List<string>();
        public int PutStatus = 200;
        public List<bool> Puts = new List<bool>();
        public List<PendingPair> Waiting = new List<PendingPair>();
        public bool Unreachable;
        public List<(string key, string code, bool allow)> Answers = new List<(string, string, bool)>();
        public Func<string, string, bool, (int status, string code)> Answer = (k, c, a) => (200, "");
        public List<PairedClient> ClientList = new List<PairedClient>();
        public int ClientsStatus = 200;
        public List<string> Removed = new List<string>();
        public int PairingReads;

        static EngineResult<T> Ok<T>(T v) { return new EngineResult<T> { Ok = true, Value = v, HttpStatus = 200 }; }
        static EngineResult<T> Fail<T>(int status, string code = "") { return new EngineResult<T> { HttpStatus = status, Code = code, Error = "HTTP " + status }; }
        static EngineResult<T> Gone<T>() { return new EngineResult<T> { Unreachable = true, Error = "refused" }; }

        public Task<EngineResult<EngineStatus>> GetStatusAsync()
        {
            if (Unreachable) return Task.FromResult(Gone<EngineStatus>());
            return Task.FromResult(Ok(new EngineStatus { Phase = "ready", Share = Share, ShareAddresses = Share == null ? null : (Share == true ? Addresses.ToList() : new List<string>()) }));
        }

        public Task<EngineResult<JObject>> PutShareAsync(bool on)
        {
            Puts.Add(on);
            if (PutStatus != 200) return Task.FromResult(Fail<JObject>(PutStatus));
            if (Share != null) Share = on;
            return Task.FromResult(Ok(new JObject()));
        }

        public Task<EngineResult<PairingList>> GetPairingAsync()
        {
            PairingReads++;
            if (Unreachable) return Task.FromResult(Gone<PairingList>());
            return Task.FromResult(Ok(new PairingList { Offered = true, Requests = Waiting.ToList() }));
        }

        public Task<EngineResult<PairAnswerReply>> AnswerPairingAsync(string key, string code, bool allow)
        {
            Answers.Add((key, code, allow));
            var (st, c) = Answer(key, code, allow);
            if (st == 200)
            {
                Waiting.RemoveAll(w => w.Key == key);
                return Task.FromResult(Ok(new PairAnswerReply { Key = key, Code = code, Allowed = allow }));
            }
            return Task.FromResult(Fail<PairAnswerReply>(st, c));
        }

        public Task<EngineResult<PairedClientList>> GetClientsAsync()
        {
            if (ClientsStatus != 200) return Task.FromResult(Fail<PairedClientList>(ClientsStatus));
            return Task.FromResult(Ok(new PairedClientList { Clients = ClientList.ToList() }));
        }

        public Task<EngineResult<JObject>> RemoveClientAsync(string id)
        {
            Removed.Add(id);
            if (ClientList.RemoveAll(c => c.Id == id) == 0) return Task.FromResult(Fail<JObject>(404, "client_unknown"));
            return Task.FromResult(new EngineResult<JObject> { Ok = true, HttpStatus = 204 });
        }
    }

    public class EngineShareTests
    {
        static PendingPair P(string key, string code, string name = "RimWorld on den-pc", bool quiet = false, string from = "192.168.1.30")
        {
            return new PendingPair { Key = key, Code = code, Name = name, From = from, Quiet = quiet };
        }

        // ------------------------------------------------------------------------------------ defaults: same PC

        [Fact]
        public void DefaultIsSamePcOnly()
        {
            Assert.False(ShareDefaults.ShareEngine);
            // the mod starts its engine with no listen address: the engine's default is loopback only (ENGINE.md §7)
            var args = EngineLauncher.ServeArgs("/p.json", 77);
            foreach (var flag in new[] { "--host", "--open", "--share", "--port" }) Assert.DoesNotContain(flag, args);
            string profile = EngineLauncher.BuildProfile(123, new HostAdapterInfo("GPU", 0x10de, 8192)).ToString();
            foreach (var word in new[] { "\"host\"", "listen", "share", "0.0.0.0", "bind" }) Assert.DoesNotContain(word, profile);
            // with the toggle off the mod never asks the engine to share, whatever it is
            Assert.Null(ShareStatus.PushFor(false, null));
            Assert.Null(ShareStatus.PushFor(false, false));
        }

        [Fact]
        public void SettingsDefaultUsesShareDefaults()
        {
            string src = File.ReadAllText(Path.Combine(ModDir(), "Source", "RimWorld", "RimWorldLLMSettings.cs"));
            Assert.Contains("shareEngine = ShareDefaults.ShareEngine", src);
            Assert.Contains("Scribe_Values.Look(ref shareEngine, \"shareEngine\", ShareDefaults.ShareEngine)", src);
        }

        [Fact]
        public async Task ToggleOffNeverTouchesAnEngineWithoutShareMode()
        {
            var api = new FakeShareApi { Share = null };
            var h = new ShareHost(() => api);
            await h.ApplyAsync(false);
            Assert.Empty(api.Puts);
            Assert.Equal(ShareView.Off, h.View(false, true));
        }

        // ------------------------------------------------------------------------------------ toggle and status

        [Theory]
        [InlineData(true, null, true)]
        [InlineData(false, null, null)]
        [InlineData(true, false, true)]
        [InlineData(true, true, null)]
        [InlineData(false, true, false)]     // shared from elsewhere: the toggle is the truth
        [InlineData(false, false, null)]
        public void PushForMatchesTheToggle(bool toggle, bool? engine, bool? want)
        {
            Assert.Equal(want, ShareStatus.PushFor(toggle, engine));
        }

        [Fact]
        public async Task TurningOnPutsShareLiveAndReadsTheAddresses()
        {
            var api = new FakeShareApi { Share = false, Addresses = { "192.168.1.20:47480", "100.101.1.2:47480" } };
            var h = new ShareHost(() => api);
            await h.ApplyAsync(true);
            Assert.Equal(new[] { true }, api.Puts);
            Assert.Equal(true, h.EngineShare);
            Assert.Equal(ShareView.On, h.View(true, true));
            Assert.Equal(2, h.Addresses.Count);
            await h.ApplyAsync(true);   // already matches: nothing sent again
            Assert.Single(api.Puts);
            await h.ApplyAsync(false);
            Assert.Equal(new[] { true, false }, api.Puts);
            Assert.Equal(ShareView.Off, h.View(false, true));
        }

        [Fact]
        public async Task SharedWithNoPrivateNetworkSaysSo()
        {
            var api = new FakeShareApi { Share = false };
            var h = new ShareHost(() => api);
            await h.ApplyAsync(true);
            Assert.Equal(ShareView.OnNoNetwork, h.View(true, true));
            Assert.Equal("RWL_Share_StatusNoNetwork", ShareStatus.LineKey(h.View(true, true)));
        }

        [Theory]
        [InlineData(400)]
        [InlineData(404)]
        public async Task AnEngineWithoutShareModeIsUnsupported(int status)
        {
            var api = new FakeShareApi { Share = null, PutStatus = status };
            var h = new ShareHost(() => api);
            await h.ApplyAsync(true);
            Assert.Equal(ShareView.Unsupported, h.View(true, true));
            await h.ApplyAsync(false);
            Assert.Equal(ShareView.Off, h.View(false, true));
        }

        [Fact]
        public async Task AnEngineThatIgnoresTheFieldIsUnsupported()
        {
            var api = new FakeShareApi { Share = null, PutStatus = 200 };   // accepted, but status never says share
            var h = new ShareHost(() => api);
            await h.ApplyAsync(true);
            Assert.Equal(ShareView.Unsupported, h.View(true, true));
        }

        [Fact]
        public async Task AFailedPutIsShownAndNotUnsupported()
        {
            var api = new FakeShareApi { Share = false, PutStatus = 500 };
            var h = new ShareHost(() => api);
            await h.ApplyAsync(true);
            Assert.Equal(ShareView.Failed, h.View(true, true));
        }

        [Fact]
        public void OwnEndpointModeHasNoShare()
        {
            Assert.Equal(ShareView.NotLocal, ShareStatus.View(true, false, true, new[] { "192.168.1.20:47480" }, false, false, ""));
        }

        [Fact]
        public void AddressRowsLanFirstWithHelloUrls()
        {
            var rows = ShareStatus.Addresses(new[] { "100.101.1.2:47480", "[fd7a:115c:a1e0::101:102]:47480", "192.168.1.20:47480", "127.0.0.1:47480", "192.168.1.20:47480", "[fd12:3456::7]:47480" });
            Assert.Equal(new[] { "192.168.1.20:47480", "[fd12:3456::7]:47480", "100.101.1.2:47480", "[fd7a:115c:a1e0::101:102]:47480" }, rows.Select(r => r.HostPort));
            Assert.Equal(new[] { NetKind.Lan, NetKind.Lan, NetKind.Tailscale, NetKind.Tailscale }, rows.Select(r => r.Kind));
            Assert.Equal("http://192.168.1.20:47480/engine/v1/hello", rows[0].HelloUrl);
            Assert.Equal("http://[fd7a:115c:a1e0::101:102]:47480/engine/v1/hello", rows[3].HelloUrl);
            Assert.Empty(ShareStatus.Addresses(null));
        }

        [Fact]
        public void HelloUrlMakesAScannableQr()
        {
            var q = QrCode.Encode(ShareStatus.HelloUrl("[fd7a:115c:a1e0::101:102]:47480"));
            Assert.True(q.Size >= 21);
        }

        // ------------------------------------------------------------------------------------ the Allow/Deny book

        [Fact]
        public void ANewRequestIsPendingAndShown()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "493201") });
            var c = b.Current;
            Assert.NotNull(c);
            Assert.Equal("k1", c.Key);
            Assert.Equal("493201", c.Code);
            Assert.Equal(PairAskState.Pending, c.State);
        }

        [Fact]
        public void TheSameRequestReadTwiceIsOneEntry()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "493201"), P("k1", "493201") });
            int v = b.Version;
            b.Sync(new[] { P("k1", "493201") });
            Assert.Single(b.Snapshot());
            Assert.Equal(v, b.Version);   // nothing changed: no redraw, no second window
        }

        [Fact]
        public void TwoSourcesQueueOldestFirst()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111", "A") });
            b.Sync(new[] { P("k2", "222222", "B"), P("k1", "111111", "A") });
            Assert.Equal("k1", b.Current.Key);
            string code;
            Assert.True(b.TryBeginAnswer("k1", out code));
            b.Finish("k1", code, true, PairAnswerOutcome.Done);
            Assert.Equal("k2", b.Current.Key);
        }

        [Fact]
        public void QuietRequestsNeverOpenTheWindow()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111", quiet: true) });
            Assert.Null(b.Current);
            Assert.Single(b.Snapshot());
        }

        [Fact]
        public void QuietRequestsStillWaitInTheList()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111", quiet: true), P("k2", "222222") });
            Assert.Equal(new[] { "k1", "k2" }, b.Waiting.Select(x => x.Key));
            Assert.Equal("k2", b.Current.Key);
            string code;
            Assert.True(b.TryBeginAnswer("k1", out code));   // a mistaken Deny is undone from the settings list
            b.Finish("k1", code, true, PairAnswerOutcome.Done);
            Assert.Equal(PairAskState.Allowed, b.Get("k1").State);
            Assert.Equal(new[] { "k2" }, b.Waiting.Select(x => x.Key));
        }

        [Fact]
        public void AllowAndDeny()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111"), P("k2", "222222") });
            string c1, c2;
            Assert.True(b.TryBeginAnswer("k1", out c1));
            Assert.False(b.TryBeginAnswer("k1", out _));   // a double click sends once
            Assert.Equal(PairAskState.Answering, b.Get("k1").State);
            b.Finish("k1", c1, true, PairAnswerOutcome.Done);
            Assert.Equal(PairAskState.Allowed, b.Get("k1").State);
            Assert.True(b.TryBeginAnswer("k2", out c2));
            b.Finish("k2", c2, false, PairAnswerOutcome.Done);
            Assert.Equal(PairAskState.Denied, b.Get("k2").State);
            Assert.Null(b.Current);
        }

        [Fact]
        public void AReplacementWithANewCodeAsksToCompareAgain()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            b.Sync(new[] { P("k1", "999999") });
            var c = b.Current;
            Assert.Equal("999999", c.Code);
            Assert.True(c.CodeChanged);
            Assert.Single(b.Snapshot());
        }

        [Fact]
        public void AnAnswerForAnOldCodeGoesBackToPending()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            Assert.True(b.TryBeginAnswer("k1", out code));
            b.Sync(new[] { P("k1", "999999") });            // the source asked again while the answer was on its way
            Assert.Equal(PairAskState.Pending, b.Get("k1").State);
            b.Finish("k1", code, true, PairAnswerOutcome.Changed);
            var c = b.Current;
            Assert.Equal(PairAskState.Pending, c.State);
            Assert.Equal("999999", c.Code);
            Assert.True(c.CodeChanged);
        }

        [Fact]
        public void Answer409ReturnsToPending()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            b.TryBeginAnswer("k1", out code);
            b.Finish("k1", code, true, PairAnswerOutcome.Changed);
            Assert.Equal(PairAskState.Pending, b.Current.State);
            Assert.True(b.Current.CodeChanged);
        }

        [Fact]
        public void Answer404Expires()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            b.TryBeginAnswer("k1", out code);
            b.Finish("k1", code, true, PairAnswerOutcome.NotWaiting);
            Assert.Equal(PairAskState.Expired, b.Get("k1").State);
            Assert.Null(b.Current);
        }

        [Fact]
        public void AFailedAnswerCanBePressedAgain()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            b.TryBeginAnswer("k1", out code);
            b.Finish("k1", code, true, PairAnswerOutcome.Failed);
            Assert.True(b.Current.AnswerFailed);
            Assert.True(b.TryBeginAnswer("k1", out _));
        }

        [Fact]
        public void ARequestThatLeavesTheListExpires()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            b.Sync(new PendingPair[0]);   // withdrawn, pushed out, or answered in the tray
            Assert.Equal(PairAskState.Expired, b.Get("k1").State);
            Assert.Null(b.Current);
        }

        [Fact]
        public void AnAnswerInFlightIsNotExpiredByTheList()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            b.TryBeginAnswer("k1", out code);
            b.Sync(new PendingPair[0]);    // our own Allow took it off the list before the reply came back
            Assert.Equal(PairAskState.Answering, b.Get("k1").State);
            b.Finish("k1", code, true, PairAnswerOutcome.Done);
            Assert.Equal(PairAskState.Allowed, b.Get("k1").State);
        }

        [Fact]
        public void HostClosesTheGameMidRequest()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111"), P("k2", "222222") });
            string code;
            b.TryBeginAnswer("k2", out code);
            b.EndAll();
            Assert.All(b.Snapshot(), x => Assert.Equal(PairAskState.Expired, x.State));
            Assert.Null(b.Current);
            b.Finish("k2", code, true, PairAnswerOutcome.Done);   // a late reply changes nothing
            Assert.Equal(PairAskState.Expired, b.Get("k2").State);
        }

        [Fact]
        public void ARequestAfterAnAnswerIsANewEntry()
        {
            var b = new PairApprovals();
            b.Sync(new[] { P("k1", "111111") });
            string code;
            b.TryBeginAnswer("k1", out code);
            b.Finish("k1", code, false, PairAnswerOutcome.Done);
            b.Sync(new[] { P("k1", "333333") });
            Assert.Equal("333333", b.Current.Code);
            Assert.Equal(2, b.Snapshot().Count);
        }

        [Fact]
        public void FinishedEntriesAreCapped()
        {
            var b = new PairApprovals { KeepFinished = 3 };
            for (int i = 0; i < 10; i++)
            {
                b.Sync(new[] { P("k" + i, "10000" + i) });
                string c;
                b.TryBeginAnswer("k" + i, out c);
                b.Finish("k" + i, c, false, PairAnswerOutcome.Done);
            }
            Assert.Equal(3, b.Snapshot().Count);
        }

        [Fact]
        public void OutcomeOfMapsTheEngineReplies()
        {
            Assert.Equal(PairAnswerOutcome.Done, PairApprovals.OutcomeOf(new EngineResult<PairAnswerReply> { Ok = true, HttpStatus = 200 }));
            Assert.Equal(PairAnswerOutcome.NotWaiting, PairApprovals.OutcomeOf(new EngineResult<PairAnswerReply> { HttpStatus = 404 }));
            Assert.Equal(PairAnswerOutcome.Changed, PairApprovals.OutcomeOf(new EngineResult<PairAnswerReply> { HttpStatus = 409, Code = "pair_changed" }));
            Assert.Equal(PairAnswerOutcome.Failed, PairApprovals.OutcomeOf(new EngineResult<PairAnswerReply> { Unreachable = true }));
            Assert.Equal(PairAnswerOutcome.Failed, PairApprovals.OutcomeOf<PairAnswerReply>(null));
        }

        // ------------------------------------------------------------------------------------ host driver

        [Fact]
        public async Task AnswerSendsTheKeyAndTheComparedCode()
        {
            var api = new FakeShareApi { Waiting = { P("k1", "493201") } };
            var h = new ShareHost(() => api);
            await h.RefreshPendingAsync();
            await h.AnswerAsync("k1", true);
            Assert.Equal(("k1", "493201", true), Assert.Single(api.Answers));
            Assert.Equal(PairAskState.Allowed, h.Book.Get("k1").State);
        }

        [Fact]
        public async Task AChangedCodeIsReadAgainAndNothingIsAllowed()
        {
            var api = new FakeShareApi { Waiting = { P("k1", "111111") } };
            api.Answer = (k, c, a) => { api.Waiting[0] = P("k1", "999999"); return (409, "pair_changed"); };
            var h = new ShareHost(() => api);
            await h.RefreshPendingAsync();
            await h.AnswerAsync("k1", true);
            var c = h.Book.Current;
            Assert.Equal("999999", c.Code);
            Assert.Equal(PairAskState.Pending, c.State);
            Assert.True(c.CodeChanged);
        }

        [Fact]
        public async Task TheEngineGoneEndsTheRequests()
        {
            var api = new FakeShareApi { Waiting = { P("k1", "111111") } };
            var h = new ShareHost(() => api);
            await h.RefreshPendingAsync();
            api.Unreachable = true;
            await h.RefreshPendingAsync();
            Assert.Null(h.Book.Current);
        }

        [Fact]
        public async Task PairedPcsListAndRemove()
        {
            var api = new FakeShareApi { ClientList = { new PairedClient { Id = "c1", Name = "RimWorld on den-pc", Created = "2026-11-27T10:00:00Z" }, new PairedClient { Id = "c2", Name = "RimWorld on box" } } };
            var h = new ShareHost(() => api);
            await h.RefreshClientsAsync();
            Assert.Equal(new[] { "c1", "c2" }, h.Clients.Select(c => c.Id));
            Assert.True(await h.RemoveAsync("c1"));
            Assert.Equal(new[] { "c2" }, h.Clients.Select(c => c.Id));
            Assert.True(await h.RemoveAsync("c1"));     // already gone (404 client_unknown) counts as removed
            Assert.False(await h.RemoveAsync(""));
        }

        [Fact]
        public async Task AnEngineWithoutAClientsListSaysSo()
        {
            var api = new FakeShareApi { ClientsStatus = 404 };
            var h = new ShareHost(() => api);
            await h.RefreshClientsAsync();
            Assert.Null(h.Clients);
            Assert.Equal("unsupported", h.ClientsError);
        }

        [Fact]
        public async Task ANoEngineApiIsQuiet()
        {
            var h = new ShareHost(() => null);
            await h.ApplyAsync(true);
            await h.RefreshPendingAsync();
            await h.AnswerAsync("k", true);
            await h.RefreshClientsAsync();
            Assert.False(await h.RemoveAsync("c1"));
        }

        // ------------------------------------------------------------------------------------ events stream

        [Fact]
        public void EventLinesParse()
        {
            var e = EngineEventLine.Parse("{\"seq\":12,\"at\":\"x\",\"life\":\"L\",\"name\":\"pairing\",\"kind\":\"fact\",\"key\":\"k1\",\"body\":{\"name\":\"n\",\"code\":\"493201\"}}");
            Assert.Equal(12, e.Seq);
            Assert.Equal("pairing", e.Name);
            Assert.Equal("k1", e.Key);
            Assert.False(e.Gone);
            Assert.True(EngineEventLine.Parse("{\"seq\":13,\"name\":\"pairing\",\"key\":\"k1\",\"gone\":true}").Gone);
            Assert.True(EngineEventLine.Parse("{\"gap\":true}").Gap);
            Assert.Null(EngineEventLine.Parse("not json"));
            Assert.Null(EngineEventLine.Parse(""));
        }

        [Fact]
        public async Task TheWatchReadsTheListAtStartAndOnEachPairingEvent()
        {
            int port = FakePairEngine.FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int factsReads = 0;
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { return; }
                    var path = ctx.Request.Url.AbsolutePath;
                    if (path == "/engine/v1/facts")
                    {
                        Interlocked.Increment(ref factsReads);
                        var b = Encoding.UTF8.GetBytes("{\"life\":\"L1\",\"seq\":5,\"facts\":[]}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.OutputStream.Write(b, 0, b.Length);
                        ctx.Response.Close();
                    }
                    else if (path == "/engine/v1/events")
                    {
                        Assert.Equal("L1", ctx.Request.QueryString["life"]);
                        Assert.Equal("5", ctx.Request.QueryString["since"]);
                        ctx.Response.ContentType = "application/x-ndjson";
                        ctx.Response.SendChunked = true;
                        foreach (var line in new[] { "{\"seq\":6,\"name\":\"phase\",\"kind\":\"fact\"}", "{\"seq\":7,\"name\":\"pairing\",\"kind\":\"fact\",\"key\":\"k1\"}" })
                        {
                            var b = Encoding.UTF8.GetBytes(line + "\n");
                            ctx.Response.OutputStream.Write(b, 0, b.Length);
                            ctx.Response.OutputStream.Flush();
                        }
                        await release.Task;
                        try { ctx.Response.Close(); } catch { }
                    }
                    else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
                }
            });
            int calls = 0;
            var two = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var w = new EngineEventWatch(() => "http://127.0.0.1:" + port, () => { if (Interlocked.Increment(ref calls) == 2) two.TrySetResult(true); }) { RetryMinMs = 50 };
            w.Start();
            try
            {
                Assert.Same(two.Task, await Task.WhenAny(two.Task, Task.Delay(10000)));
                await Task.Delay(200);
                Assert.Equal(2, Volatile.Read(ref calls));   // the start read and the one pairing line; the phase line is ignored
            }
            finally
            {
                w.Stop();
                release.TrySetResult(true);
                listener.Stop();
                listener.Close();
            }
        }

        [Fact]
        public async Task TheWatchSaysWhenTheEngineIsGone()
        {
            int port = FakePairEngine.FreePort();   // nothing listens there
            var gone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int pairing = 0;
            var w = new EngineEventWatch(() => "http://127.0.0.1:" + port, () => pairing++, () => gone.TrySetResult(true)) { RetryMinMs = 50, RetryMaxMs = 100 };
            w.Start();
            try { Assert.Same(gone.Task, await Task.WhenAny(gone.Task, Task.Delay(10000))); }
            finally { w.Stop(); }
            Assert.Equal(0, pairing);
        }

        // ------------------------------------------------------------------------------------ wording

        static string ModDir([CallerFilePath] string here = "") { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "Mod")); }

        static Dictionary<string, string> Strings()
        {
            var doc = XDocument.Load(Path.Combine(ModDir(), "Languages", "English", "Keyed", "RimWorldLLM.xml"));
            return doc.Root.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        }

        [Fact]
        public void ShareWording()
        {
            var s = Strings();
            Assert.Equal("Share this PC's engine on my network", s["RWL_Share_Toggle"]);
            Assert.Equal("Any device on this network (a cafe's Wi-Fi too) can see this PC's engine name and model; only PCs you Allow can use it.", s["RWL_Share_Exposure"]);
            Assert.Contains("firewall", s["RWL_Share_ToggleTip"]);
            Assert.Contains("private", s["RWL_Share_ToggleTip"]);
            Assert.Contains("incoming connections", s["RWL_Share_ToggleTip"]);
            Assert.Equal("{0} wants to use this PC's engine.", s["RWL_PairReq_Body"]);
            Assert.Equal("Code: {0}", s["RWL_PairReq_Code"]);
            Assert.Equal("Allow", s["RWL_PairReq_Allow"]);
            Assert.Equal("Deny", s["RWL_PairReq_Deny"]);
            Assert.Equal("Use an engine on another PC on my network", s["RWL_Pair_Title"]);
            Assert.Equal("An engine on another PC on my network", s["RWL_Eng_SourcePc"]);
            Assert.Contains("removed this PC", s["RWL_Pair_StatusRevoked"]);
            foreach (ShareView v in Enum.GetValues(typeof(ShareView))) Assert.True(s.ContainsKey(ShareStatus.LineKey(v)), ShareStatus.LineKey(v));
            foreach (var k in new[] { "RWL_Share_BadgeLan", "RWL_Share_BadgeTailscale" }) Assert.True(s.ContainsKey(k), k);
        }
    }
}

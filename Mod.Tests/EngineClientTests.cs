using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>A fake engine: HttpListener on a free loopback port that records requests and answers canned JSON.</summary>
    sealed class FakeEngine : IDisposable
    {
        public readonly HttpListener Listener = new HttpListener();
        public readonly int Port;
        public readonly List<string> Log = new List<string>();                       // "METHOD path"
        public readonly List<string> Csrf = new List<string>();                      // header value per request ("" when absent)
        public readonly List<string> Bodies = new List<string>();
        public Func<string, string, (int, string)> Handler;                          // (method, pathAndQuery) -> (status, body)
        public int DelayMs;
        readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public FakeEngine(Func<string, string, (int, string)> handler)
        {
            Handler = handler;
            Port = FreePort();
            Listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            Listener.Start();
            Task.Run(Loop);
        }

        static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await Listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        void Serve(HttpListenerContext c)
        {
            string body;
            using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            string pq = c.Request.Url.PathAndQuery;
            lock (Log)
            {
                Log.Add(c.Request.HttpMethod + " " + pq);
                Csrf.Add(c.Request.Headers["X-Easy-Engine"] ?? "");
                Bodies.Add(body);
            }
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            var (status, text) = Handler(c.Request.HttpMethod, pq);
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(text);
                c.Response.StatusCode = status;
                c.Response.ContentType = "application/json";
                c.Response.OutputStream.Write(b, 0, b.Length);
                c.Response.Close();
            }
            catch { }
        }

        public EngineClient Client { get { return new EngineClient(Port); } }
        public void Dispose() { _stop.Cancel(); try { Listener.Close(); } catch { } }
    }

    public class EngineClientTests
    {
        const string StatusReady = "{\"version\":\"0.1.0\",\"port\":47480,\"mode\":\"local\",\"phase\":\"ready\",\"message\":\"\",\"progress\":null,\"model\":\"gemma4-rp\",\"backend\":\"cuda\",\"shape\":{\"slots\":3,\"ctx_per_slot\":32768,\"n_cpu_moe\":4,\"kv_type\":\"q4_0\",\"vram_mib\":13000},\"server_url\":\"http://127.0.0.1:47480\",\"tok_s\":41.5,\"agent\":{\"phase\":\"running\",\"pid\":9,\"restarts\":1,\"message\":\"\"},\"parents\":[{\"pid\":1,\"alive\":true}],\"errors\":[]}";
        const string StatusDownloading = "{\"phase\":\"downloading\",\"progress\":{\"done_bytes\":500,\"total_bytes\":1000,\"rate_bps\":250.5,\"eta_s\":2,\"step\":\"model\"},\"errors\":[]}";
        const string Plan = "{\"recommended\":\"gemma4-e4b-q4km\",\"backend\":\"cuda-linux-x64\",\"models\":[{\"id\":\"gemma4-e4b-q4km\",\"name\":\"Gemma 4 E4B\",\"size\":3000000000,\"fits\":\"yes\",\"why\":\"fits\",\"shape\":{\"slots\":3,\"ctx_per_slot\":65536,\"n_cpu_moe\":0,\"kv_type\":\"q4_0\",\"vram_mib\":6000},\"est_vram_mib\":6000,\"est_ram_mib\":500,\"installed\":false},{\"id\":\"big\",\"name\":\"Big\",\"size\":9,\"fits\":\"no\",\"why\":\"too big\",\"installed\":true,\"import_only\":true}]}";

        static (int, string) Route(string m, string pq)
        {
            if (m == "GET" && pq == "/engine/v1/status") return (200, StatusReady);
            if (m == "GET" && pq == "/engine/v1/plan") return (200, Plan);
            if (m == "GET" && pq.StartsWith("/engine/v1/hardware"))
                return (200, "{\"os\":\"linux\",\"arch\":\"amd64\",\"cpu_name\":\"X\",\"cores\":8,\"avx2\":true,\"ram_total_mib\":32000,\"ram_avail_mib\":20000,\"disk_free_mib\":100000,\"gpus\":[{\"index\":0,\"vendor\":\"nvidia\",\"name\":\"RTX 5080\",\"vram_total_mib\":16303,\"vram_used_mib\":1000}]}");
            if (m == "POST" && pq == "/engine/v1/install") return (200, "{\"ok\":true}");
            if (m == "POST" && pq == "/engine/v1/cancel") return (200, "{\"ok\":true}");
            if (m == "POST" && pq == "/engine/v1/test") return (200, "{\"light\":\"green\",\"message\":\"ok\",\"latency_ms\":120,\"model\":\"m\"}");
            if (m == "PUT" && pq == "/engine/v1/settings") return (200, "{}");
            if (m == "POST" && pq == "/engine/v1/attach") return (200, "{}");
            if (m == "GET" && pq.StartsWith("/engine/v1/logs")) return (200, "line1\nline2\n");
            return (404, "{\"error\":\"nope\"}");
        }

        [Fact]
        public async Task StatusReadyParses()
        {
            using (var f = new FakeEngine(Route))
            {
                var r = await f.Client.GetStatusAsync();
                Assert.True(r.Ok);
                Assert.Equal("ready", r.Value.Phase);
                Assert.Equal("local", r.Value.Mode);
                Assert.Equal(3, r.Value.Shape.Slots);
                Assert.Equal(32768, r.Value.Shape.CtxPerSlot);
                Assert.Equal("gemma4-rp", r.Value.Model);
                Assert.Null(r.Value.Progress);
                Assert.Equal(41.5, r.Value.TokS);
                Assert.Equal("running", r.Value.Agent.Phase);
                Assert.Equal(1, r.Value.Agent.Restarts);
                Assert.True(r.Value.Parents[0].Alive);
                Assert.Empty(r.Value.Errors);
            }
        }

        [Fact]
        public async Task StatusDownloadingHasProgress()
        {
            using (var f = new FakeEngine((m, p) => (200, StatusDownloading)))
            {
                var r = await f.Client.GetStatusAsync();
                Assert.Equal(EnginePhase.Downloading, r.Value.Phase);
                Assert.True(EnginePhase.IsBusy(r.Value.Phase));
                Assert.Equal(0.5, r.Value.Progress.Fraction, 3);
                Assert.Equal(250.5, r.Value.Progress.RateBps);
                Assert.Equal(2, r.Value.Progress.EtaSeconds);
            }
        }

        [Theory]
        [InlineData("not_installed")] [InlineData("installing")] [InlineData("starting")] [InlineData("loading")]
        [InlineData("resizing")] [InlineData("sleeping")] [InlineData("external")] [InlineData("error")]
        public async Task EveryPhaseRoundTrips(string phase)
        {
            using (var f = new FakeEngine((m, p) => (200, "{\"phase\":\"" + phase + "\"}")))
            {
                var r = await f.Client.GetStatusAsync();
                Assert.Equal(phase, r.Value.Phase);
            }
        }

        [Fact]
        public async Task PlanParses()
        {
            using (var f = new FakeEngine(Route))
            {
                var r = await f.Client.GetPlanAsync();
                Assert.True(r.Ok);
                Assert.Equal("gemma4-e4b-q4km", r.Value.Recommended);
                Assert.Equal(2, r.Value.Models.Count);
                Assert.Equal("yes", r.Value.Models[0].Fits);
                Assert.Equal(6000, r.Value.Models[0].EstVramMib);
                Assert.Equal(65536, r.Value.Models[0].Shape.CtxPerSlot);
                Assert.Equal("no", r.Value.Models[1].Fits);
                Assert.True(r.Value.Models[1].Installed);
                Assert.True(r.Value.Models[1].ImportOnly);
                Assert.False(r.Value.Models[0].ImportOnly);
            }
        }

        [Fact]
        public async Task HardwareParses()
        {
            using (var f = new FakeEngine(Route))
            {
                var r = await f.Client.GetHardwareAsync(true);
                Assert.True(r.Ok);
                Assert.Equal(32000, r.Value.RamTotalMiB);
                Assert.Equal("RTX 5080", r.Value.Gpus[0].Name);
                Assert.Equal(16303, r.Value.Gpus[0].VramTotalMiB);
                Assert.Contains("GET /engine/v1/hardware?refresh=1", f.Log);
            }
        }

        [Fact]
        public async Task InstallAndCancelFlowSendsCsrfHeaderAndBody()
        {
            using (var f = new FakeEngine(Route))
            {
                var c = f.Client;
                Assert.True((await c.InstallAsync("gemma4-e4b-q4km")).Ok);
                Assert.True((await c.CancelAsync()).Ok);
                Assert.True((await c.AttachAsync(4242)).Ok);
                Assert.True((await c.PutSettingsAsync(new EngineSettings { Mode = "external", External = new EngineExternal { BaseUrl = "http://h/v1", Model = "m", ApiKey = "k" } })).Ok);
                Assert.True((await c.TestAsync()).Ok);
                lock (f.Log)
                {
                    Assert.Equal(new[] { "POST /engine/v1/install", "POST /engine/v1/cancel", "POST /engine/v1/attach", "PUT /engine/v1/settings", "POST /engine/v1/test" }, f.Log.ToArray());
                    foreach (var h in f.Csrf) Assert.Equal("1", h);
                    Assert.Equal("gemma4-e4b-q4km", (string)JObject.Parse(f.Bodies[0])["model"]);
                    Assert.Equal(4242, (int)JObject.Parse(f.Bodies[2])["pid"]);
                    var s = JObject.Parse(f.Bodies[3]);
                    Assert.Equal("external", (string)s["mode"]);
                    Assert.Equal("http://h/v1", (string)s["external"]["base_url"]);
                    Assert.Equal("k", (string)s["external"]["api_key"]);
                    Assert.Null(s["linger_s"]);
                }
            }
        }

        [Fact]
        public async Task GetCarriesNoCsrfHeaderNeed()
        {
            using (var f = new FakeEngine(Route))
            {
                await f.Client.GetStatusAsync();
                Assert.Equal("", f.Csrf[0]);
            }
        }

        [Fact]
        public async Task TestResultParses()
        {
            using (var f = new FakeEngine(Route))
            {
                var r = await f.Client.TestAsync();
                Assert.Equal("green", r.Value.Light);
                Assert.Equal(120, r.Value.LatencyMs);
            }
        }

        [Fact]
        public async Task ConflictOnInstallReportsMessage()
        {
            using (var f = new FakeEngine((m, p) => (409, "{\"error\":\"a job is running\"}")))
            {
                var r = await f.Client.InstallAsync("x");
                Assert.False(r.Ok);
                Assert.Equal(409, r.HttpStatus);
                Assert.Equal("a job is running", r.Error);
            }
        }

        [Fact]
        public async Task LogsReturnPlainText()
        {
            using (var f = new FakeEngine(Route))
            {
                var r = await f.Client.GetLogsAsync("engine", 50);
                Assert.Equal("line1\nline2\n", r.Value);
                Assert.Contains("GET /engine/v1/logs?name=engine&tail=50", f.Log);
            }
        }

        [Fact]
        public async Task SlowEngineTimesOut()
        {
            using (var f = new FakeEngine(Route) { DelayMs = 1500 })
            {
                var c = f.Client;
                c.ShortTimeoutMs = 200;
                var r = await c.GetStatusAsync();
                Assert.False(r.Ok);
                Assert.True(r.TimedOut);
            }
        }

        [Fact]
        public async Task NothingListeningIsUnreachable()
        {
            int port;
            using (var f = new FakeEngine(Route)) port = f.Port;
            var r = await new EngineClient(port).GetStatusAsync();
            Assert.False(r.Ok);
            Assert.True(r.Unreachable || r.TimedOut);
        }

        [Fact]
        public async Task DiscoverFindsEngineOnGivenPorts()
        {
            using (var f = new FakeEngine(Route))
            {
                var c = await EngineClient.DiscoverAsync(0, 500, new[] { 1, f.Port });
                Assert.NotNull(c);
                Assert.Equal(f.Port, new Uri(c.BaseUrl).Port);
                Assert.Equal(c.BaseUrl + "/v1", c.OpenAiBase);
            }
        }

        [Fact]
        public async Task DiscoverReturnsNullWhenNoneAnswers()
        {
            Assert.Null(await EngineClient.DiscoverAsync(0, 300, new[] { 1 }));
        }

        [Fact]
        public void CandidatePortsPutLockFirstThenRange()
        {
            var l = EngineClient.CandidatePorts(47483);
            Assert.Equal(47483, l[0]);
            Assert.Equal(10, l.Count);
            Assert.Equal(47480, l[1]);
            Assert.Equal(10, EngineClient.CandidatePorts(0).Count);
        }

        [Theory]
        [InlineData("linux", "/h/.local/share/easy-engine")]
        [InlineData("darwin", "/h/Library/Application Support/EasyEngine")]
        public void DataDirPerOs(string os, string expected)
        {
            string got = EngineClient.DataDir(os, n => null, "/h");
            Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), got);
        }

        [Fact]
        public void LegacyDataDirIsTheOldName()
        {
            Assert.Equal(Path.Combine("/h", ".local", "share", "hermes-engine"), EngineClient.LegacyDataDir("linux", n => null, "/h"));
            Assert.Equal(Path.Combine("C:\\L", "HermesEngine"), EngineClient.LegacyDataDir("windows", n => n == "LOCALAPPDATA" ? "C:\\L" : null, "C:\\u"));
            Assert.Equal(Path.Combine("/h", "Library", "Application Support", "HermesEngine"), EngineClient.LegacyDataDir("darwin", n => null, "/h"));
            Assert.Equal("/old", EngineClient.LegacyDataDir("linux", n => n == "HERMES_ENGINE_HOME" ? "/old" : null, "/h"));
        }

        [Fact]
        public void LockPortPrefersTheNewDirThenTheOld()
        {
            string root = Path.Combine(Path.GetTempPath(), "rwl-lock-" + Guid.NewGuid().ToString("N"));
            try
            {
                string share = Path.Combine(root, "share");
                Func<string, string> env = n => n == "XDG_DATA_HOME" ? share : null;
                Assert.Equal(0, EngineClient.LockPort("linux", env, root));
                Directory.CreateDirectory(Path.Combine(share, "hermes-engine"));
                File.WriteAllText(Path.Combine(share, "hermes-engine", "engine.lock"), "{\"pid\":1,\"port\":47483}");
                Assert.Equal(47483, EngineClient.LockPort("linux", env, root));
                Directory.CreateDirectory(Path.Combine(share, "easy-engine"));
                File.WriteAllText(Path.Combine(share, "easy-engine", "engine.lock"), "{\"pid\":1,\"port\":47481}");
                Assert.Equal(47481, EngineClient.LockPort("linux", env, root));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void DataDirHonoursOverridesAndXdg()
        {
            Assert.Equal("/x", EngineClient.DataDir("linux", n => n == "EASY_ENGINE_HOME" ? "/x" : null, "/h"));
            Assert.Equal(Path.Combine("/xdg", "easy-engine"), EngineClient.DataDir("linux", n => n == "XDG_DATA_HOME" ? "/xdg" : null, "/h"));
            Assert.Equal(Path.Combine("C:\\L", "EasyEngine"), EngineClient.DataDir("windows", n => n == "LOCALAPPDATA" ? "C:\\L" : null, "C:\\u"));
        }

        [Fact]
        public void ReadLockPortHandlesJsonAndText()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rwl-lock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string f = Path.Combine(dir, "engine.lock");
                File.WriteAllText(f, "{\"pid\":12,\"port\":47482,\"children\":[1,2],\"started\":\"2026-11-25T10:00:00Z\"}");
                Assert.Equal(47482, EngineClient.ReadLockPort(f));
                File.WriteAllText(f, "pid=12\nport=47483\n");
                Assert.Equal(47483, EngineClient.ReadLockPort(f));
                File.WriteAllText(f, "12 47484");
                Assert.Equal(47484, EngineClient.ReadLockPort(f));
                File.WriteAllText(f, "garbage");
                Assert.Equal(0, EngineClient.ReadLockPort(f));
                Assert.Equal(0, EngineClient.ReadLockPort(Path.Combine(dir, "missing")));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}

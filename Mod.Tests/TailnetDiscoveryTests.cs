using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class TailnetDiscoveryTests
    {
        static string Fx(string name) { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "tailscale", name)); }

        static EngineHello Hello(string id, string name = "N")
        {
            return EngineHello.Parse("{\"engine\":\"easy-engine\",\"api\":1,\"engine_id\":\"" + id + "\",\"name\":\"" + name + "\",\"model_name\":\"M\",\"pairing\":true}");
        }

        [Fact]
        public void ParseRunning()
        {
            var s = TailnetStatus.Parse(Fx("status-running.json"));
            Assert.NotNull(s);
            Assert.True(s.Running);
            Assert.Equal("game-pc", s.Self.HostName);
            Assert.Equal(6, s.Peers.Count);
            Assert.Contains(s.Peers, p => p.HostName == "Someone’s Mac Air");
            var v6 = s.Peers.Single(p => p.HostName == "v6only");
            Assert.Equal("", v6.IPv4);
            Assert.StartsWith("fd7a:", v6.IPv6);
        }

        [Fact]
        public void TargetsExcludeOfflineAndSelf()
        {
            var t = TailnetStatus.Parse(Fx("status-running.json")).ProbeTargets();
            Assert.Equal(5, t.Count);
            Assert.DoesNotContain("http://100.101.1.4:47480", t);   // old-laptop offline
            Assert.DoesNotContain("http://100.101.1.1:47480", t);   // self
            Assert.Contains("http://100.101.1.2:47480", t);
            Assert.Contains("http://[fd7a:115c:a1e0::101:106]:47480", t);
        }

        [Fact]
        public void StoppedAndNeedsLogin()
        {
            foreach (var f in new[] { "status-stopped.json", "status-needslogin.json" })
            {
                var s = TailnetStatus.Parse(Fx(f));
                Assert.NotNull(s);
                Assert.False(s.Running);
                Assert.Empty(s.ProbeTargets());
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("garbage {")]
        [InlineData("[1,2]")]
        [InlineData("{}")]
        public void GarbageIsNull(string j) { Assert.Null(TailnetStatus.Parse(j)); }

        [Fact]
        public void NullPeerAndUnknownFields()
        {
            var s = TailnetStatus.Parse("{\"BackendState\":\"Running\",\"Peer\":null,\"Self\":null,\"Whatever\":{\"a\":1}}");
            Assert.NotNull(s);
            Assert.Empty(s.ProbeTargets());
        }

        [Fact]
        public void TargetsCappedAt32()
        {
            var peers = string.Join(",", Enumerable.Range(0, 50).Select(i => "\"k" + i + "\":{\"HostName\":\"h" + (100 + i) + "\",\"Online\":true,\"TailscaleIPs\":[\"100.64.0." + (i + 1) + "\"]}"));
            var s = TailnetStatus.Parse("{\"BackendState\":\"Running\",\"Peer\":{" + peers + "}}");
            var t = s.ProbeTargets();
            Assert.Equal(32, t.Count);
            Assert.Equal("http://100.64.0.1:47480", t[0]);
        }

        static Func<string, bool> Exists(params string[] files) { var set = new HashSet<string>(files); return f => set.Contains(f); }
        static Func<string, string> Env(Dictionary<string, string> d) { return k => d.ContainsKey(k) ? d[k] : null; }

        [Fact]
        public void CliFindLinuxPath()
        {
            var env = Env(new Dictionary<string, string> { ["PATH"] = "/usr/bin:/usr/local/bin" });
            Assert.Equal("/usr/local/bin/tailscale", TailscaleCli.Find("linux", env, Exists("/usr/local/bin/tailscale")));
        }

        [Fact]
        public void CliFindWindowsProgramFiles()
        {
            var env = Env(new Dictionary<string, string> { ["PATH"] = "C:\\Windows;C:\\x", ["ProgramFiles"] = "C:\\Program Files" });
            Assert.Equal("C:\\Program Files\\Tailscale\\tailscale.exe", TailscaleCli.Find("windows", env, Exists("C:\\Program Files\\Tailscale\\tailscale.exe")));
        }

        [Fact]
        public void CliFindWindowsPath()
        {
            var env = Env(new Dictionary<string, string> { ["PATH"] = "C:\\Windows;C:\\ts\\" });
            Assert.Equal("C:\\ts\\tailscale.exe", TailscaleCli.Find("windows", env, Exists("C:\\ts\\tailscale.exe")));
        }

        [Fact]
        public void CliFindMacApp()
        {
            var env = Env(new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
            Assert.Equal("/Applications/Tailscale.app/Contents/MacOS/Tailscale", TailscaleCli.Find("darwin", env, Exists("/Applications/Tailscale.app/Contents/MacOS/Tailscale")));
        }

        [Fact]
        public void CliNotInstalled()
        {
            var env = Env(new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
            Assert.Null(TailscaleCli.Find("linux", env, Exists()));
            Assert.Null(TailscaleCli.Find("windows", Env(new Dictionary<string, string>()), Exists()));
            Assert.Null(TailscaleCli.Find("darwin", env, Exists()));
        }

        [Theory]
        [InlineData("100.63.255.255", NetKind.Lan)]
        [InlineData("100.64.0.1", NetKind.Tailscale)]
        [InlineData("100.127.255.255", NetKind.Tailscale)]
        [InlineData("100.128.0.1", NetKind.Lan)]
        [InlineData("[fd7a:115c:a1e0::1]:47480", NetKind.Tailscale)]
        [InlineData("[fd12::1]:47480", NetKind.Lan)]
        [InlineData("127.0.0.1", NetKind.Loopback)]
        [InlineData("127.0.0.1:47480", NetKind.Loopback)]
        [InlineData("[::1]", NetKind.Loopback)]
        [InlineData("[::1]:47480", NetKind.Loopback)]
        [InlineData("http://100.101.1.2:47480", NetKind.Tailscale)]
        [InlineData("http://192.168.1.5:47480/v1", NetKind.Lan)]
        [InlineData("10.0.0.2", NetKind.Lan)]
        [InlineData("169.254.1.1", NetKind.Lan)]
        [InlineData("localhost:47480", NetKind.Loopback)]
        [InlineData("den-pc.tailabc123.ts.net:47480", NetKind.Tailscale)]
        [InlineData("den-pc.local", NetKind.Lan)]
        [InlineData("fd7a:115c:a1e0::5", NetKind.Tailscale)]
        [InlineData("", NetKind.Lan)]
        public void NetKindCases(string a, NetKind k) { Assert.Equal(k, NetKindOf.Of(a)); }

        const string Cli = "/usr/bin/tailscale";

        [Fact]
        public async Task FindNullRunIsEmpty()
        {
            var r = await TailnetDiscovery.FindAsync((p, a, t) => null, Cli, (root, c) => Task.FromResult(Hello("a")));
            Assert.Empty(r);
            r = await TailnetDiscovery.FindAsync((p, a, t) => Fx("status-stopped.json"), Cli, (root, c) => Task.FromResult(Hello("a")));
            Assert.Empty(r);
            r = await TailnetDiscovery.FindAsync((p, a, t) => Fx("status-running.json"), null, (root, c) => Task.FromResult(Hello("a")));
            Assert.Empty(r);
            r = await TailnetDiscovery.FindAsync((p, a, t) => throw new Exception("x"), Cli, (root, c) => Task.FromResult(Hello("a")));
            Assert.Empty(r);
        }

        [Fact]
        public async Task FindRunsStatusJsonOnceAndFindsEngines()
        {
            int runs = 0; string[] args = null;
            Func<string, string[], int, string> run = (p, a, t) => { Interlocked.Increment(ref runs); args = a; return Fx("status-running.json"); };
            Func<string, CancellationToken, Task<EngineHello>> hello = (root, c) =>
            {
                if (root.Contains("100.101.1.2")) return Task.FromResult(Hello("AAAA", "Den"));
                if (root.Contains("100.101.1.3")) throw new InvalidOperationException("boom");
                if (root.Contains("100.101.1.5")) return Task.FromResult<EngineHello>(null);
                if (root.Contains("fd7a")) return Task.FromResult(EngineHello.Parse("{\"engine\":\"other\",\"api\":1,\"engine_id\":\"z\"}"));
                return Task.FromResult<EngineHello>(null);
            };
            var r = await TailnetDiscovery.FindAsync(run, Cli, hello);
            Assert.Equal(1, runs);
            Assert.Equal(new[] { "status", "--json" }, args);
            var one = Assert.Single(r);
            Assert.Equal("aaaa", one.EngineId);
            Assert.True(one.ViaTailscale);
            Assert.False(one.ViaLan);
            Assert.True(one.Pairing);
        }

        [Fact]
        public async Task HangingHelloIsSkippedWithinTimeout()
        {
            Func<string, string[], int, string> run = (p, a, t) => Fx("status-running.json");   // 5 targets
            Func<string, CancellationToken, Task<EngineHello>> hello = async (root, c) =>
            {
                if (root.Contains("100.101.1.2")) return Hello("ok1");
                await Task.Delay(Timeout.Infinite, c);
                return null;
            };
            var sw = Stopwatch.StartNew();
            var r = await TailnetDiscovery.FindAsync(run, Cli, hello, maxParallel: 5, timeoutMs: 300);
            sw.Stop();
            Assert.Single(r);
            Assert.True(sw.ElapsedMilliseconds < 5000, "took " + sw.ElapsedMilliseconds);
        }

        [Fact]
        public async Task HelloIgnoringCancellationStillTimesOut()
        {
            Func<string, string[], int, string> run = (p, a, t) => Fx("status-running.json");
            var gate = new TaskCompletionSource<EngineHello>();
            var sw = Stopwatch.StartNew();
            var r = await TailnetDiscovery.FindAsync(run, Cli, (root, c) => gate.Task, maxParallel: 8, timeoutMs: 300);
            Assert.Empty(r);
            Assert.True(sw.ElapsedMilliseconds < 5000);
            gate.SetResult(null);
        }

        [Fact]
        public async Task ConcurrencyIsCapped()
        {
            var peers = string.Join(",", Enumerable.Range(0, 10).Select(i => "\"k" + i + "\":{\"HostName\":\"h" + i + "\",\"Online\":true,\"TailscaleIPs\":[\"100.64.0." + (i + 1) + "\"]}"));
            string json = "{\"BackendState\":\"Running\",\"Peer\":{" + peers + "}}";
            int cur = 0, max = 0;
            Func<string, CancellationToken, Task<EngineHello>> hello = async (root, c) =>
            {
                int n = Interlocked.Increment(ref cur);
                int m; while ((m = Volatile.Read(ref max)) < n && Interlocked.CompareExchange(ref max, n, m) != m) { }
                await Task.Delay(50);
                Interlocked.Decrement(ref cur);
                return Hello("e" + root.GetHashCode());
            };
            var r = await TailnetDiscovery.FindAsync((p, a, t) => json, Cli, hello, maxParallel: 3, timeoutMs: 5000);
            Assert.Equal(10, r.Count);
            Assert.InRange(max, 1, 3);
        }

        static ShareFound S(string id, string root, bool lan, bool ts, string name = "")
        {
            var s = new ShareFound { EngineId = id, Name = name, ViaLan = lan, ViaTailscale = ts };
            s.Roots.Add(root);
            return s;
        }

        [Fact]
        public void MergeByEngineIdLanFirst()
        {
            var lan = new[] { new FoundEngine { EngineId = "ABC", Name = "Den", Root = "http://192.168.1.5:47480", ModelName = "M" } };
            var tail = new[] { S("abc", "http://100.101.1.2:47480", false, true), S("def", "http://100.101.1.3:47480", false, true, "Box") };
            var m = ShareFound.Merge(lan, tail);
            Assert.Equal(2, m.Count);
            var a = m.Single(x => x.EngineId == "abc");
            Assert.Equal(new[] { "http://192.168.1.5:47480", "http://100.101.1.2:47480" }, a.Roots);
            Assert.True(a.ViaLan && a.ViaTailscale);
            Assert.Equal("Den", a.Name);
            Assert.Equal("M", a.ModelName);
            Assert.False(m.Single(x => x.EngineId == "def").ViaLan);
        }

        [Fact]
        public void MergeKeepsRowsWithoutIdByRoot()
        {
            var lan = new[] { new FoundEngine { Root = "http://10.0.0.9:47480", Name = "typed" } };
            var tail = new[] { S("", "http://10.0.0.9:47480", true, false), S("", "http://10.0.0.8:47480", true, false) };
            var m = ShareFound.Merge(lan, tail);
            Assert.Equal(2, m.Count);
            Assert.All(m, x => Assert.Equal("", x.EngineId));
        }

        [Fact]
        public void MergeEmptyAndNull()
        {
            Assert.Empty(ShareFound.Merge((IEnumerable<FoundEngine>)null, null));
        }
    }
}

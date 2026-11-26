using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HermesVoice.Bridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class GpuSenseTests
    {
        static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { GpuLine.KeyHostAndModel, "Game on {0} · {1}: {2} free for the model" },
            { GpuLine.KeyModelOnly, "{0}: {1} free for the model" },
            { GpuLine.KeyShared, "Game and model share {0}: {1} free for the model" },
            { GpuLine.KeyCpu, "No graphics card in use: the model runs on the processor" },
        };
        static string T(string k, object[] a) { return string.Format(En[k], a); }

        const string Two = "{\"gpu\":{\"adapters\":[" +
            "{\"index\":0,\"vendor\":\"intel\",\"vendor_id\":32902,\"name\":\"Intel(R) Iris(R) Xe Graphics\",\"dedicated_mib\":128,\"integrated\":true}," +
            "{\"index\":1,\"vendor\":\"nvidia\",\"vendor_id\":4318,\"name\":\"NVIDIA GeForce RTX 3050 Laptop GPU\",\"dedicated_mib\":4096}]," +
            "\"engine_adapter\":1,\"host_adapter\":0,\"host_integrated\":\"yes\",\"reserve\":{\"others_mib\":0,\"host_mib\":0,\"margin_mib\":96,\"total_mib\":96}," +
            "\"free_for_model_mib\":4000,\"others_note\":\"\"}}";

        static GpuSummary Parse(string json) { return JsonConvert.DeserializeObject<EngineStatus>(json).Gpu; }

        // Captured live from the engine on DESKTOP (Optimus laptop, 2026-11-26): the game said it
        // renders on the Iris Xe while a stand-in held 1 GB on the 3050.
        const string Desktop = "{\"gpu\": {\"adapters\": [{\"index\": 0, \"vendor\": \"nvidia\", \"vendor_id\": 4318, \"name\": \"NVIDIA GeForce RTX 3050 Laptop GPU\", \"dedicated_mib\": 3965, \"shared_mib\": 16226, \"used_mib\": 1035, \"integrated\": false, \"luid\": \"0x00000000_0x000127EF\", \"pci\": \"0000:01:00.0\", \"uuid\": \"GPU-c4ae8d0c-0952-f9a9-4b43-e71674010fff\", \"source\": \"dxgi\"}, {\"index\": 1, \"vendor\": \"intel\", \"vendor_id\": 32902, \"name\": \"Intel(R) Iris(R) Xe Graphics\", \"dedicated_mib\": 128, \"shared_mib\": 16226, \"used_mib\": 0, \"integrated\": true, \"luid\": \"0x00000000_0x0001238A\", \"source\": \"dxgi\"}], \"engine_adapter\": 0, \"host_adapter\": 1, \"host_integrated\": \"yes\", \"reserve\": {\"others_mib\": 1035, \"others_basis\": \"processes\", \"host_mib\": 0, \"host_basis\": \"other-adapter\", \"margin_mib\": 512, \"total_mib\": 4096}, \"free_for_model_mib\": 2549, \"others_note\": \"Other apps are using 1.0 GB of your graphics card\", \"processes\": [{\"pid\": 4, \"name\": \"\", \"adapter\": 0, \"dedicated_mib\": 3, \"shared_mib\": 0, \"kind\": \"\"}, {\"pid\": 12844, \"name\": \"fakehost.exe\", \"adapter\": 0, \"dedicated_mib\": 1031, \"shared_mib\": 62, \"kind\": \"graphics\"}, {\"pid\": 1616, \"name\": \"csrss.exe\", \"adapter\": 1, \"dedicated_mib\": 0, \"shared_mib\": 18, \"kind\": \"\"}], \"sources\": {\"dxgi\": \"ok\", \"nvidia-smi\": \"ok\", \"pdh\": \"ok\"}}}";

        [Fact]
        public void LiveDesktopReport()
        {
            var line = GpuLine.Format(Parse(Desktop), T);
            Assert.StartsWith("Game on Intel Iris Xe · RTX 3050: 2.5 GB free for the model", line);
            Assert.Contains("Other apps are using 1.0 GB of your graphics card", line);
        }

        [Fact]
        public void HostOnOtherAdapter()
        {
            Assert.Equal("Game on Intel Iris Xe · RTX 3050: 3.9 GB free for the model", GpuLine.Format(Parse(Two), T));
        }

        [Fact]
        public void HostUnknown()
        {
            var g = Parse(Two); g.HostAdapter = -1; g.FreeForModelMiB = 2048;
            Assert.Equal("RTX 3050: 2.0 GB free for the model", GpuLine.Format(g, T));
        }

        [Fact]
        public void HostSharesTheCard()
        {
            var g = Parse(Two); g.HostAdapter = 1;
            Assert.Equal("Game and model share RTX 3050: 3.9 GB free for the model", GpuLine.Format(g, T));
        }

        [Fact]
        public void CpuOnly()
        {
            var g = Parse(Two); g.EngineAdapter = -1; g.HostAdapter = -1;
            Assert.Equal("No graphics card in use: the model runs on the processor", GpuLine.Format(g, T));
        }

        [Fact]
        public void OthersNoteIsASecondSentence()
        {
            var g = Parse(Two); g.OthersNote = "Other apps are using 1.2 GB of your graphics card";
            Assert.Equal("Game on Intel Iris Xe · RTX 3050: 3.9 GB free for the model. Other apps are using 1.2 GB of your graphics card", GpuLine.Format(g, T));
            g.OthersNote = "Other apps use 1 GB.";
            Assert.EndsWith("model. Other apps use 1 GB.", GpuLine.Format(g, T));
        }

        [Fact]
        public void OlderEngineHasNoGpuAndNoLine()
        {
            var st = JsonConvert.DeserializeObject<EngineStatus>("{\"phase\":\"ready\"}");
            Assert.Null(st.Gpu);
            Assert.Null(GpuLine.Format(st.Gpu, T));
            var empty = JsonConvert.DeserializeObject<EngineStatus>("{\"gpu\":{}}").Gpu;
            Assert.Equal(-1, empty.EngineAdapter);
            Assert.Equal(-1, empty.HostAdapter);
        }

        [Fact]
        public void UnresolvableEngineIndexSaysNothing()
        {
            var g = Parse(Two); g.EngineAdapter = 7;
            Assert.Null(GpuLine.Format(g, T));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", "RTX 3050")]
        [InlineData("NVIDIA GeForce RTX 5080", "RTX 5080")]
        [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel Iris Xe")]
        [InlineData("Intel(R) UHD Graphics 620", "Intel UHD 620")]
        [InlineData("AMD Radeon(TM) Graphics", "Radeon")]
        [InlineData("AMD Radeon RX 6700 XT", "Radeon RX 6700 XT")]
        [InlineData("NVIDIA GeForce GTX 1650 Ti with Max-Q Design", "GTX 1650 Ti")]
        [InlineData("Graphics", "Graphics")]
        [InlineData("", "")]
        public void ShortNames(string raw, string want) { Assert.Equal(want, GpuLine.ShortName(raw)); }

        [Fact]
        public void GbOneDecimalInvariant()
        {
            Assert.Equal("0.0 GB", GpuLine.Gb(-5));
            Assert.Equal("1.5 GB", GpuLine.Gb(1536));
            var old = System.Globalization.CultureInfo.CurrentCulture;
            try { System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); Assert.Equal("1.5 GB", GpuLine.Gb(1536)); }
            finally { System.Globalization.CultureInfo.CurrentCulture = old; }
        }

        [Fact]
        public void ProfileCarriesHostPidAndAdapter()
        {
            var j = EngineLauncher.BuildProfile("/c/b.json", null, 777, new HostAdapterInfo("Intel(R) Iris(R) Xe Graphics", 0x8086, 128));
            Assert.Equal(777, (int)j["host_pid"]);
            Assert.Equal(0x8086, (int)j["host_adapter"]["vendor_id"]);
            Assert.Equal("Intel(R) Iris(R) Xe Graphics", (string)j["host_adapter"]["name"]);
            Assert.Equal(128, (int)j["host_adapter"]["memory_mib"]);
            Assert.Equal(1500, (int)j["game_reserve_mib"]);
            var bare = EngineLauncher.BuildProfile("/c/b.json", null);
            Assert.Null(bare["host_pid"]);
            Assert.Null(bare["host_adapter"]);
        }

        [Fact]
        public async Task PostHostBody()
        {
            using (var f = new FakeEngine((m, p) => (204, "")))
            {
                var r = await f.Client.PostHostAsync(55, new HostAdapterInfo("X", 4318, 4096), 1500);
                Assert.True(r.Ok);
                Assert.Contains("POST /engine/v1/host", f.Log);
                Assert.Equal("1", f.Csrf[0]);
                var b = JObject.Parse(f.Bodies[0]);
                Assert.Equal(55, (int)b["pid"]);
                Assert.Equal(1500, (int)b["reserve_mib"]);
                Assert.Equal(4318, (int)b["adapter"]["vendor_id"]);
            }
        }

        [Fact]
        public async Task PostHostOmitsUnknownAdapter()
        {
            using (var f = new FakeEngine((m, p) => (204, "")))
            {
                await f.Client.PostHostAsync(55, null, 0);
                Assert.Null(JObject.Parse(f.Bodies[0])["adapter"]);
            }
        }

        [Fact]
        public async Task LauncherPostsHostAfterAttachAndTolerates404()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hv-gpu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var f = new FakeEngine((m, p) => p.EndsWith("/host") ? (404, "{\"error\":\"nope\"}") : (200, "{}")))
                {
                    var r = await EngineLauncher.EnsureRunningAsync(new LaunchOptions
                    {
                        ModRoot = dir, BridgeJsonPath = Path.Combine(dir, "bridge.json"), Pid = 4321,
                        HostAdapter = new HostAdapterInfo("X", 1, 2),
                        Platform = new EnginePlatform("linux", "amd64"),
                        Discover = () => Task.FromResult(f.Client)
                    });
                    Assert.Equal(LaunchOutcome.Attached, r.Outcome);
                    Assert.Contains("POST /engine/v1/attach", f.Log);
                    Assert.Contains("POST /engine/v1/host", f.Log);
                    Assert.Null(r.AttachError);
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    public class AgentDataDirTests
    {
        static Func<string, string> Env(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return n => d.TryGetValue(n, out var v) ? v : null;
        }

        [Fact]
        public void ResolveCombos()
        {
            var o = AgentDataDir.Resolve("linux", Env("HERMESVOICE_DATA_DIR", "/x/mine", "XDG_DATA_HOME", "/xdg"), "/h");
            Assert.Equal("/x/mine", o.Path); Assert.True(o.Overridden);
            var xdg = AgentDataDir.Resolve("linux", Env("XDG_DATA_HOME", "/xdg"), "/h");
            Assert.Equal(Path.Combine("/xdg", "hermesvoice-agent"), xdg.Path); Assert.False(xdg.Overridden);
            Assert.Equal(Path.Combine("/h", ".local", "share", "hermesvoice-agent"), AgentDataDir.Resolve("linux", Env(), "/h").Path);
            Assert.Equal(Path.Combine("/h", ".local", "share", "hermesvoice-agent"), AgentDataDir.Resolve("darwin", Env("XDG_DATA_HOME", ""), "/h").Path);
            Assert.Equal(Path.Combine("/la", "hermesvoice-agent"), AgentDataDir.Resolve("windows", Env("LOCALAPPDATA", "/la", "XDG_DATA_HOME", "/xdg"), "/h").Path);
            Assert.Equal(Path.Combine("/h", "AppData", "Local", "hermesvoice-agent"), AgentDataDir.Resolve("windows", Env(), "/h").Path);
            Assert.Equal("/o", AgentDataDir.Resolve("windows", Env("HERMESVOICE_DATA_DIR", "/o", "LOCALAPPDATA", "/la"), "/h").Path);
        }

        sealed class Tmp : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "hv-agentdir-" + Guid.NewGuid().ToString("N"));
            public Tmp() { Directory.CreateDirectory(Dir); }
            public string Sub(params string[] p) { var d = Path.Combine(Dir, Path.Combine(p)); Directory.CreateDirectory(d); return d; }
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        static void Fill(string d) { File.WriteAllText(Path.Combine(d, "a.db"), "12345"); Directory.CreateDirectory(Path.Combine(d, "logs")); File.WriteAllText(Path.Combine(d, "logs", "l.txt"), "123"); }

        [Fact]
        public void DeletesTheRightFolder()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string dir = t.Sub("home", ".local", "share", "hermesvoice-agent"); Fill(dir);
                string sibling = t.Sub("home", ".local", "share", "other"); Fill(sibling);
                var res = AgentDataDir.Resolve("linux", Env("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), home);
                Assert.Equal(8, AgentDataDir.SizeOf(dir));
                var r = AgentDataDir.Delete(res, dir, new[] { home });
                Assert.Equal(AgentDataDir.DeleteOutcome.Deleted, r.Outcome);
                Assert.Equal(8, r.FreedBytes);
                Assert.False(Directory.Exists(dir));
                Assert.True(File.Exists(Path.Combine(sibling, "a.db")));
                Assert.Equal(AgentDataDir.DeleteOutcome.NothingThere, AgentDataDir.Delete(res, dir, new[] { home }).Outcome);
            }
        }

        [Fact]
        public void RefusesWrongLeafWithoutOverrideAndOtherPaths()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string wrong = t.Sub("home", "documents"); Fill(wrong);
                var asked = new AgentDataDir.Resolved { Path = wrong, Overridden = false };
                var r = AgentDataDir.Delete(asked, wrong, new[] { home });
                Assert.Equal(AgentDataDir.DeleteOutcome.Refused, r.Outcome);
                Assert.True(File.Exists(Path.Combine(wrong, "a.db")));
                // dir differs from the resolved one
                string right = t.Sub("home", "hermesvoice-agent"); Fill(right);
                var res = new AgentDataDir.Resolved { Path = right };
                Assert.Equal(AgentDataDir.DeleteOutcome.Refused, AgentDataDir.Delete(res, wrong, new[] { home }).Outcome);
                Assert.True(File.Exists(Path.Combine(right, "a.db")));
            }
        }

        [Fact]
        public void RefusesRootHomeAndSystemFolders()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home"); File.WriteAllText(Path.Combine(home, "keep.txt"), "k");
                foreach (var bad in new[] { "/", home, Path.GetDirectoryName(home) })
                {
                    var res = new AgentDataDir.Resolved { Path = bad, Overridden = true };
                    var r = AgentDataDir.Delete(res, bad, new[] { home });
                    Assert.Equal(AgentDataDir.DeleteOutcome.Refused, r.Outcome);
                }
                Assert.True(File.Exists(Path.Combine(home, "keep.txt")));
                Assert.NotNull(AgentDataDir.Check(new AgentDataDir.Resolved { Path = "relative/dir", Overridden = true }, "relative/dir", null));
                Assert.NotNull(AgentDataDir.Check(new AgentDataDir.Resolved { Path = "", Overridden = true }, "", null));
            }
        }

        [Fact]
        public void OverrideWithAnotherLeafIsRefusedAndUntouched()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string dir = t.Sub("mydata"); Fill(dir);
                var res = new AgentDataDir.Resolved { Path = dir, Overridden = true };
                Assert.Equal(AgentDataDir.DeleteOutcome.Refused, AgentDataDir.Delete(res, dir, new[] { home }).Outcome);
                Assert.NotEmpty(Directory.GetFileSystemEntries(dir));
            }
        }

        [Fact]
        public void OverrideNamedLikeTheAgentFolderIsEmptiedNotRemoved()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string dir = t.Sub("hermesvoice-agent"); Fill(dir);
                var res = new AgentDataDir.Resolved { Path = dir, Overridden = true };
                Assert.Equal(AgentDataDir.DeleteOutcome.Deleted, AgentDataDir.Delete(res, dir, new[] { home }).Outcome);
                Assert.True(Directory.Exists(dir));
                Assert.Empty(Directory.GetFileSystemEntries(dir));
            }
        }

        [Fact]
        public void RefusesSymlinkedFolderAndNeverFollowsInnerLinks()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string outside = t.Sub("outside"); Fill(outside);
                string link = Path.Combine(home, "hermesvoice-agent");
                Directory.CreateSymbolicLink(link, outside);
                var res = new AgentDataDir.Resolved { Path = link };
                var r = AgentDataDir.Delete(res, link, new[] { home });
                Assert.Equal(AgentDataDir.DeleteOutcome.Refused, r.Outcome);
                Assert.True(File.Exists(Path.Combine(outside, "a.db")));
                Directory.Delete(link);

                string dir = t.Sub("home", "hermesvoice-agent"); Fill(dir);
                Directory.CreateSymbolicLink(Path.Combine(dir, "esc"), outside);
                File.CreateSymbolicLink(Path.Combine(dir, "esc.txt"), Path.Combine(outside, "a.db"));
                Assert.Equal(AgentDataDir.DeleteOutcome.Deleted, AgentDataDir.Delete(res, dir, new[] { home }).Outcome);
                Assert.False(Directory.Exists(dir));
                Assert.True(File.Exists(Path.Combine(outside, "a.db")));
                Assert.True(File.Exists(Path.Combine(outside, "logs", "l.txt")));
            }
        }

        [Fact]
        public void RemovalFinishedWaitsForJobAndAgent()
        {
            Assert.False(AgentDataDir.RemovalFinished("removing", "stopped", true, 9000));
            Assert.False(AgentDataDir.RemovalFinished("not_installed", "stopped", false, 1000));   // job may not have started
            Assert.True(AgentDataDir.RemovalFinished("not_installed", "stopped", false, 3500));
            Assert.True(AgentDataDir.RemovalFinished("not_installed", "not_installed", true, 100));
            Assert.True(AgentDataDir.RemovalFinished("not_installed", null, true, 100));
            Assert.False(AgentDataDir.RemovalFinished("not_installed", "running", true, 100));
            Assert.False(AgentDataDir.RemovalFinished("not_installed", "restarting", true, 100));
        }
    }
}

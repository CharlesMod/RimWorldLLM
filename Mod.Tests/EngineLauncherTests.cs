using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class EngineLauncherTests
    {
        sealed class FakeStarter : IProcessStarter
        {
            public int Calls;
            public string Exe;
            public IList<string> Args;
            public IDictionary<string, string> Env;
            public Exception Throw;
            public void Start(string exe, IList<string> args, IDictionary<string, string> env)
            {
                Calls++;
                if (Throw != null) throw Throw;
                Exe = exe; Args = args; Env = env;
            }
        }

        sealed class Temp : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "hv-launch-" + Guid.NewGuid().ToString("N"));
            public Temp() { Directory.CreateDirectory(Dir); }
            public string Touch(params string[] rel)
            {
                string p = Path.Combine(Dir, Path.Combine(rel));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, "x");
                return p;
            }
            public void Dispose() { Directory.Delete(Dir, true); }
        }

        [Theory]
        [InlineData("linux", "amd64", "linux-amd64", "hermes-engine")]
        [InlineData("windows", "amd64", "windows-amd64", "hermes-engine.exe")]
        [InlineData("darwin", "arm64", "darwin-arm64", "hermes-engine")]
        [InlineData("darwin", "amd64", "darwin-amd64", "hermes-engine")]
        public void BinaryChoicePerPlatform(string os, string arch, string dir, string name)
        {
            var p = new EnginePlatform(os, arch);
            Assert.Equal(dir, p.Dir);
            Assert.Equal(name, p.BinaryName);
            Assert.Equal(Path.Combine("/m", "Engine", dir, name), EngineLauncher.BinaryPath("/m", p));
            Assert.Equal(os != "windows", p.IsUnix);
        }

        [Fact]
        public void DetectGivesKnownNames()
        {
            var p = EnginePlatform.Detect();
            Assert.Contains(p.Os, new[] { "linux", "windows", "darwin" });
            Assert.False(string.IsNullOrEmpty(p.Arch));
        }

        [Fact]
        public void ProfileShape()
        {
            var j = EngineLauncher.BuildProfile("/c/bridge.json", "/m/Engine/agent/a.whl");
            Assert.Equal("RimWorld", (string)j["game"]);
            Assert.Equal(3, (int)j["slots"]);
            Assert.Equal(1, (int)j["min_slots"]);
            Assert.Equal(65536, (int)j["ctx_per_slot"]["max"]);
            Assert.Equal(8192, (int)j["ctx_per_slot"]["floor"]);
            Assert.Equal(1500, (int)j["game_reserve_mib"]);
            Assert.Equal(84, (int)j["host_files_mib"]);   // calls.jsonl 4 x 20 MB + 4 MiB, counted in the engine's disk need
            var a = j["agent"];
            Assert.Equal("rimworldllm-agent", (string)a["id"]);
            Assert.Equal("/m/Engine/agent/a.whl", (string)a["wheel"]);
            Assert.Equal("3.12", (string)a["python"]);
            Assert.Equal("run", (string)a["args"][0]);
            Assert.Equal("/c/bridge.json", (string)a["env"]["RIMWORLDLLM_BRIDGE_JSON"]);
        }

        [Fact]
        public void ProfileWithoutWheelHasNoAgent()
        {
            Assert.Null(EngineLauncher.BuildProfile("/c/bridge.json", null)["agent"]);
        }

        [Fact]
        public void FindWheelPicksFileInEngineAgent()
        {
            using (var t = new Temp())
            {
                Assert.Null(EngineLauncher.FindWheel(t.Dir));
                t.Touch("Engine", "agent", "rimworldllm_agent-0.1.0-py3-none-any.whl");
                t.Touch("Engine", "agent", "notes.txt");
                Assert.EndsWith("rimworldllm_agent-0.1.0-py3-none-any.whl", EngineLauncher.FindWheel(t.Dir));
            }
        }

        [Fact]
        public void ServeArgsShape()
        {
            Assert.Equal(new[] { "serve", "--profile", "/p.json", "--parent-pid", "77" }, EngineLauncher.ServeArgs("/p.json", 77));
        }

        static LaunchOptions Opts(Temp t, FakeStarter s, Func<Task<EngineClient>> discover)
        {
            return new LaunchOptions
            {
                ModRoot = t.Dir,
                BridgeJsonPath = Path.Combine(t.Dir, "cfg", "RimWorldLLM", "bridge.json"),
                Pid = 4321,
                Platform = new EnginePlatform("linux", "amd64"),
                Starter = s,
                Discover = discover,
                SpawnWaitMs = 1000, SpawnPollMs = 100,
                Delay = ms => Task.CompletedTask
            };
        }

        [Fact]
        public async Task RunningEngineGetsAttachNotSpawn()
        {
            using (var t = new Temp())
            using (var f = new FakeEngine((m, p) => m == "GET" ? (200, "{\"phase\":\"ready\"}") : (200, "{}")))
            {
                t.Touch("Engine", "linux-amd64", "hermes-engine");
                var s = new FakeStarter();
                var r = await EngineLauncher.EnsureRunningAsync(Opts(t, s, () => Task.FromResult(f.Client)));
                Assert.Equal(LaunchOutcome.Attached, r.Outcome);
                Assert.Equal(0, s.Calls);
                Assert.Contains("POST /engine/v1/attach", f.Log);
                Assert.Equal(4321, (int)JObject.Parse(f.Bodies[f.Bodies.Count - 1])["pid"]);
                Assert.Equal("1", f.Csrf[f.Csrf.Count - 1]);
                Assert.Null(r.AttachError);
            }
        }

        [Fact]
        public async Task NoEngineSpawnsServeWithProfileAndParentPid()
        {
            using (var t = new Temp())
            using (var f = new FakeEngine((m, p) => (200, "{\"phase\":\"not_installed\"}")))
            {
                string bin = t.Touch("Engine", "linux-amd64", "hermes-engine");
                t.Touch("Engine", "agent", "a-0.1-py3-none-any.whl");
                var s = new FakeStarter();
                int polls = 0;
                var r = await EngineLauncher.EnsureRunningAsync(Opts(t, s, () => Task.FromResult(++polls >= 3 ? f.Client : null)));
                Assert.Equal(LaunchOutcome.Spawned, r.Outcome);
                Assert.Equal(1, s.Calls);
                Assert.Equal(bin, s.Exe);
                Assert.Equal("serve", s.Args[0]);
                Assert.Equal("--parent-pid", s.Args[3]);
                Assert.Equal("4321", s.Args[4]);
                Assert.True(File.Exists(s.Args[2]));
                Assert.Equal(Path.Combine(t.Dir, "cfg", "RimWorldLLM", "engine-profile.json"), s.Args[2]);
                var prof = JObject.Parse(File.ReadAllText(s.Args[2]));
                Assert.EndsWith("a-0.1-py3-none-any.whl", (string)prof["agent"]["wheel"]);
                Assert.Equal(Path.Combine(t.Dir, "cfg", "RimWorldLLM", "bridge.json"), s.Env["RIMWORLDLLM_BRIDGE_JSON"]);
                Assert.NotNull(r.Client);
            }
        }

        [Fact]
        public async Task MissingBinaryIsAClearError()
        {
            using (var t = new Temp())
            {
                var s = new FakeStarter();
                var r = await EngineLauncher.EnsureRunningAsync(Opts(t, s, () => Task.FromResult<EngineClient>(null)));
                Assert.Equal(LaunchOutcome.NoBinary, r.Outcome);
                Assert.Contains("linux-amd64", r.Error);
                Assert.Equal(0, s.Calls);
            }
        }

        [Fact]
        public async Task StarterFailureIsReported()
        {
            using (var t = new Temp())
            {
                t.Touch("Engine", "linux-amd64", "hermes-engine");
                var s = new FakeStarter { Throw = new InvalidOperationException("boom") };
                var r = await EngineLauncher.EnsureRunningAsync(Opts(t, s, () => Task.FromResult<EngineClient>(null)));
                Assert.Equal(LaunchOutcome.SpawnFailed, r.Outcome);
                Assert.Contains("boom", r.Error);
            }
        }

        [Fact]
        public async Task SpawnedButSilentLeavesNoClient()
        {
            using (var t = new Temp())
            {
                t.Touch("Engine", "linux-amd64", "hermes-engine");
                var s = new FakeStarter();
                var r = await EngineLauncher.EnsureRunningAsync(Opts(t, s, () => Task.FromResult<EngineClient>(null)));
                Assert.Equal(LaunchOutcome.Spawned, r.Outcome);
                Assert.Null(r.Client);
            }
        }
    
        // Splits a command line the way Windows programs (Go's runtime included) do: CommandLineToArgvW rules.
        static List<string> SplitWindows(string cmd)
        {
            var args = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQ = false, any = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '\\')
                {
                    int n = 0; while (i < cmd.Length && cmd[i] == '\\') { n++; i++; }
                    if (i < cmd.Length && cmd[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) cur.Append('"'); else { inQ = !inQ; any = true; } }
                    else { cur.Append('\\', n); i--; }
                    continue;
                }
                if (c == '"') { inQ = !inQ; any = true; continue; }
                if ((c == ' ' || c == '\t') && !inQ) { if (cur.Length > 0 || any) args.Add(cur.ToString()); cur.Clear(); any = false; continue; }
                cur.Append(c);
            }
            if (cur.Length > 0 || any) args.Add(cur.ToString());
            return args;
        }

        [Theory]
        [InlineData(@"C:\Users\Boss\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\RimWorldLLM\engine-profile.json")]
        [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimWorldLLM\")]
        [InlineData("say \"hi\" there")]
        [InlineData(@"a\\""b c")]
        [InlineData("/home/some one/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config")]
        [InlineData("plain")]
        [InlineData("")]
        public void Quote_RoundTripsThroughWindowsArgvRules(string arg)
        {
            var line = SystemProcessStarter.Quote(arg) + " next";
            var got = SplitWindows(line);
            Assert.Equal(new List<string> { arg, "next" }, got);
        }

        [Fact]
        public void Quote_LeavesWindowsSeparatorsSingle()
        {
            Assert.Equal("\"C:\\Program Files\\x.json\"", SystemProcessStarter.Quote(@"C:\Program Files\x.json"));
        }
}
}

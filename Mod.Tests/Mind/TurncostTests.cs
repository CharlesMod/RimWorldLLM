// The tool-loop rows in calls.jsonl, and the turncost harness command (offline: FakeLlm for the pawns, a scripted tool backend for the steward).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Harness;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class ToolCallLogTests
    {
        private static readonly IReadOnlyList<JObject> U = new[] { new JObject { ["role"] = "user", ["content"] = "hi" } };
        private static readonly object Reply = JObject.Parse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":3}," +
            "\"timings\":{\"prompt_n\":120,\"cache_n\":880,\"prompt_ms\":600.0,\"predicted_n\":7,\"predicted_ms\":140.0}}");

        private static OpenAIBackend Be(CallLog log, object body, int status = 200)
            => new OpenAIBackend("http://h", client: Mock.Client(s => MockResponse.Json(status, body)), engine: false) { CallLog = log };

        [Fact]
        public async Task AStewardCallBecomesARowWithKindSlotAndTimings()
        {
            var log = new CallLog(null);
            using (ToolCallKind.Push("steward_combat", true)) await Be(log, Reply).ChatAsync(U, slot: 3);
            var row = Assert.Single(log.Snapshot());
            Assert.Equal("steward_combat", (string)row["kind"]); Assert.Equal(3, (int)row["slot"]);
            Assert.Equal((120, 880, 7), ((long)row["prompt_n"], (long)row["cache_n"], (long)row["predicted_n"]));
            Assert.Equal(600.0, (double)row["prompt_ms"]); Assert.Equal(200.0, (double)row["raw_prefill_tps"]); Assert.Equal(1666.7, (double)row["effective_prefill_tps"]);
            Assert.True((bool)row["ok"]); Assert.NotNull(row["request_id"]); Assert.NotNull(row["turn"]);
        }

        [Fact]
        public async Task ACallWithNoSlotIsAProbeAndUsageFillsInWithoutTimings()
        {
            var log = new CallLog(null);
            var body = JObject.Parse("{\"choices\":[{\"message\":{\"content\":\"zebra-7\"}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":3}}");
            using (ToolCallKind.Push("steward", true)) await Be(log, body).ChatAsync(U);
            var row = Assert.Single(log.Snapshot());
            Assert.Equal("probe", (string)row["kind"]); Assert.Equal((50, 3, 0), ((long)row["prompt_n"], (long)row["predicted_n"], (long)row["cache_n"]));
            Assert.Null(row["turn"]);
        }

        [Fact]
        public async Task AFailedCallIsLoggedNotOk()
        {
            var log = new CallLog(null);
            await Assert.ThrowsAsync<BackendError>(() => Be(log, new JObject { ["error"] = "bad" }, 500).ChatAsync(U, slot: 1));
            var row = Assert.Single(log.Snapshot());
            Assert.False((bool)row["ok"]); Assert.Equal("HTTP 500", (string)row["error"]);
        }

        [Fact]
        public async Task TheHarnessKindReachesTheRowsAndOneLoopSharesATurn()
        {
            var log = new CallLog(null);
            var be = Be(log, Reply);
            var h = new ToolHarness(be, new List<ToolSpec>(), "grammar", null, 3) { Kind = "steward_trade" };
            try { await h.RunAsync("sys", "user", (t, v) => Task.FromResult<JToken>(null), over: o => o.Slot = 2); } catch (Exception) { }
            await h.RunAsync("sys", "user2", (t, v) => Task.FromResult<JToken>(null), over: o => o.Slot = 2).ContinueWith(_ => { });
            var rows = log.Snapshot();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal("steward_trade", (string)r["kind"]));
            Assert.Equal(2, rows.Select(r => (long)r["turn"]).Distinct().Count());
        }
    }

    public class TurncostTests
    {
        /// <summary>Scripted steward: probes answer, every turn ends with a final line; logs each call the way OpenAIBackend does.</summary>
        private sealed class LoggingScripted : IBackend
        {
            readonly CallLog _log;
            public LoggingScripted(CallLog log) { _log = log; }
            public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                         int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
            {
                int chars = messages.Sum(m => m.ToString().Length);
                _log.Record(new CallStats { Kind = ToolCallKind.For(slot), Slot = slot, Turn = slot.HasValue ? ToolCallKind.Turn : null, PromptN = chars / 4, CacheN = 100, PromptMs = 50, PredictedN = 5, PredictedMs = 20 });
                return Task.FromResult(new Reply { Text = Rig.Final("All is well.") });
            }
        }

        private static Task<int> Run(string dir, StringWriter so, StringWriter se, params string[] extra)
        {
            var args = new List<string> { "--llm-url", "http://x/v1", "--out", dir, "--mode", "grammar", "--digests", Path.Combine(dir, "d.json"), "--events", Path.Combine(AppContext.BaseDirectory, "turncost", "multiday.jsonl") };
            args.AddRange(extra);
            return Turncost.MainAsync(args.ToArray(), so, se, (cfg, log) => new LoggedLlm(new FakeLlm(), log), (cfg, log) => new LoggingScripted(log));
        }

        [Fact]
        public async Task ThreeCyclesOfflineGiveAStewardRowAndLineRows()
        {
            using var tmp = new TempDir();
            string dir = tmp.Combine("out");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "d.json"), StewardTestData.Sample.ToString());
            var so = new StringWriter(); var se = new StringWriter();
            Assert.Equal(0, await Run(dir, so, se, "--cycles", "3"));
            string table = so.ToString();
            string sample = Environment.GetEnvironmentVariable("RWL_TURNCOST_SAMPLE");      // a person can keep the offline table: set it to a file path
            if (!string.IsNullOrEmpty(sample)) File.WriteAllText(sample, table);
            Assert.Contains("steward ", table); Assert.Contains("line ", table); Assert.Contains("steward turn", table);
            var summary = JObject.Parse(File.ReadAllText(Path.Combine(dir, "summary.json")));
            Assert.True((int)summary["kinds"]["steward"]["n"] >= 3);
            Assert.True((int)summary["kinds"]["line"]["n"] > 0);
            Assert.True((int)summary["steward_turns"]["steward"]["turns"] >= 3);
            Assert.True((double)summary["kinds"]["steward"]["hit_pct"] > 0);
            var prompts = File.ReadAllLines(Path.Combine(dir, "steward-prompts.jsonl")).Select(JObject.Parse).ToList();
            Assert.Contains(prompts, p => (int)p["cycle"] == 2 && (string)p["kind"] == "steward" && ((JArray)p["messages"]).Count >= 2);
            Assert.True(File.Exists(Path.Combine(dir, "calls.jsonl")));
            Assert.StartsWith(tmp.Path, Path.GetFullPath(dir));     // everything stays in the given dir
        }

        [Fact]
        public async Task TheSameInputsGiveTheSamePrompts()
        {
            var outs = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                using var tmp = new TempDir();
                string dir = tmp.Combine("out");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "d.json"), StewardTestData.Sample.ToString());
                Assert.Equal(0, await Run(dir, new StringWriter(), new StringWriter(), "--cycles", "2", "--playing"));
                outs.Add(string.Join("\n", File.ReadAllLines(Path.Combine(dir, "steward-prompts.jsonl")).Select(l => JObject.Parse(l)).Select(p => p["cycle"] + "|" + p["step"] + "|" + p["messages"].ToString(Newtonsoft.Json.Formatting.None).Replace(dir, "D"))));
            }
            Assert.Equal(outs[0], outs[1]);
            Assert.NotEmpty(outs[0]);
        }

        [Fact]
        public void OneDigestIsMovedOnADifferentWayEachCycle()
        {
            var d = new List<JObject> { (JObject)StewardTestData.Sample.DeepClone() };
            var a = Turncost.DigestFor(d, 1); var b = Turncost.DigestFor(d, 2);
            Assert.NotEqual(a.ToString(), b.ToString());
            Assert.Equal(a.ToString(), Turncost.DigestFor(d, 1).ToString());
        }

        [Fact]
        public async Task EveryShippedDigestRunsAsAStewardDigest()
        {
            var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "turncost"), "*digest*.json");
            Assert.NotEmpty(files);
            foreach (string f in files)
            {
                var be = new StewardFake(new object[] { Rig.Final("ok") });
                var o = await RimWorldLLM.Harness.StewardTry.TryOnceAsync((JObject)BridgeJson.Parse(File.ReadAllText(f)), null, "how are we?", false, "http://unused", mode: "grammar", makeBackend: _ => be);
                Assert.True(o.Messages != null && o.Messages.Count >= 2, Path.GetFileName(f));
            }
        }

        [Fact]
        public async Task UsageErrorsExitTwo()
        {
            Assert.Equal(2, await Turncost.MainAsync(new string[0], new StringWriter(), new StringWriter()));
            Assert.Equal(2, await Turncost.MainAsync(new[] { "--llm-url", "u", "--out", "o", "--bogus" }, new StringWriter(), new StringWriter()));
        }
    }
}

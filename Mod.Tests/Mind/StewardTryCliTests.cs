// Port of test_steward_more.py::test_steward_try_cli_runs_one_cycle (the harness's steward-try command).
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class StewardTryCliTests
    {
        private sealed class Scripted : IBackend
        {
            readonly List<string> _script;
            public Scripted(params string[] s) { _script = new List<string>(s); }
            public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                         int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
            {
                if (slot == null) return Task.FromResult(new Reply { Text = Rig.Final("hi") });   // the protocol probe of auto mode
                string t = _script[0];
                if (_script.Count > 1) _script.RemoveAt(0);
                return Task.FromResult(new Reply { Text = t });
            }
        }

        [Fact]
        public async Task SetsWorkAndSaysItInTheChat()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "digest.json");
            File.WriteAllText(path, StewardTestData.Sample.ToString());
            var so = new StringWriter(); var se = new StringWriter();
            var be = new Scripted(Rig.Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1 }), Rig.Final("Ana cooks first."));
            int rc = await RimWorldLLM.Harness.StewardTry.MainAsync(
                new[] { "--digest", path, "--chat", "put Ana on cooking", "--playing", "--llm-url", "http://x/v1" }, so, se, _ => be);
            Assert.Equal(0, rc);
            var o = so.ToString();
            Assert.Contains("SYSTEM PROMPT:", o); Assert.Contains("put Ana on cooking", o);
            Assert.Contains("TOOL set_work", o); Assert.Contains("CHAT [narrator] Ana cooks first.", o);
        }

        [Fact]
        public async Task UsageErrorsExitTwo()
        {
            var so = new StringWriter(); var se = new StringWriter();
            Assert.Equal(2, await RimWorldLLM.Harness.StewardTry.MainAsync(new string[0], so, se));
            Assert.Equal(2, await RimWorldLLM.Harness.StewardTry.MainAsync(new[] { "--digest", "x", "--llm-url", "u" }, so, se));
        }

        // ---- test_steward_combat.py: steward_try --combat -------------------------------------------------------------
        [Fact]
        public async Task StewardTryCombatCycleOnEachRecordedFight()
        {
            foreach (var kind in CombatData.Kinds)
            {
                var be = new StewardFake(new object[] { Rig.Call("squad", new { op = "move", pawns = "Bram", at = "cover" }), Rig.Done });
                var o = await RimWorldLLM.Harness.StewardTry.TryOnceAsync(null, JObject.FromObject(new { plan = "Hold the line." }), "", false, "http://unused", mode: "grammar",
                                                                          combat: CombatData.View(kind), combatCycle: true, makeBackend: _ => be);
                var tr = o.Trace;
                Assert.True((string)tr["reason"] == "combat" && (bool)tr["playing"], kind);
                Assert.True(o.WallS >= 0 && (double)tr["elapsed_s"] >= 0);
                Assert.True((int)tr["system_chars"] > 0 && ((string)o.Messages[0]["content"]).Length + ((string)o.Messages[1]["content"]).Length < 16000);
                Assert.Contains("Hold the line.", (string)o.Messages[1]["content"]);
                Assert.True(o.Acts[0].Key == "squad" && JToken.DeepEquals(JObject.Parse("{\"op\":\"move\",\"pawns\":[102],\"at\":\"cover\"}"), o.Acts[0].Value));
                Assert.DoesNotContain(o.Requests, r => r.Value == "/digest");
            }
        }

        [Fact]
        public async Task StewardTryFullCombatCycleNeedsADigestAndMergesTheView()
        {
            var be = new StewardFake(new object[] { Rig.Done });
            var o = await RimWorldLLM.Harness.StewardTry.TryOnceAsync((JObject)StewardTestData.Sample.DeepClone(), null, "", true, "http://unused", mode: "grammar",
                                                                      combat: CombatData.View("mech_cluster"), makeBackend: _ => be);
            Assert.Equal("combat started", (string)o.Trace["reason"]);
            var user = (string)o.Messages[1]["content"];
            if (CombatData.DigestHasCombat) Assert.True(user.Contains("COMBAT:") && user.Contains("scythers"));      // the digest's COMBAT lines are delta3-b's
        }

        [Fact]
        public async Task StewardTryMainPrintsSizeAndSeconds()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "combat.json");
            File.WriteAllText(path, CombatData.Views["pirate_ranged"].ToString());
            var so = new StringWriter(); var se = new StringWriter();
            var be = new StewardFake(new object[] { Rig.Call("squad", new { op = "draft" }), Rig.Done });
            int rc = await RimWorldLLM.Harness.StewardTry.MainAsync(new[] { "--combat", path, "--combat-cycle", "--mode", "grammar", "--llm-url", "http://unused" }, so, se, _ => be);
            var o = so.ToString();
            Assert.True(rc == 0 && o.Contains("PROMPT:") && o.Contains("tokens") && o.Contains("ELAPSED:") && o.Contains("CYCLE: combat"), o + se);
            Assert.Contains("TOOL squad", o);
            Assert.Equal(2, await RimWorldLLM.Harness.StewardTry.MainAsync(new[] { "--combat-cycle", "--llm-url", "http://unused" }, new StringWriter(), new StringWriter()));    // needs --combat
        }
    }
}

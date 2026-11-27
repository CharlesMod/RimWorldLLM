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
    }
}

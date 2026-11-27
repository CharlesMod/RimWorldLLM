// End-to-end golden parity (docs/csmind/parity-tools.md): the Python agent's replay over recorded inputs, captured by
// ~/.local/share/rimworld-hermes-engine/csmind/tools/capture_golden.py (every LLM request and reply, every bridge write minus op_ids, in order);
// the C# replay must produce the same sequences, key order included.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using RimWorldLLM.Tests.Mind.ReplayTool;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class E2eGoldenTests
    {
        static JObject Golden(string name)
        {
            using var s = typeof(E2eGoldenTests).Assembly.GetManifestResourceStream("RimWorldLLM.Tests.e2e-" + name + ".json");
            using var r = new StreamReader(s);
            return JObject.Parse(r.ReadToEnd());
        }

        static string Cap(string s) { return s.Length <= 700 ? s : s.Substring(0, 700) + "..."; }

        /// <summary>Both texts around their first differing character.</summary>
        static string Around(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 120);
            string W(string x) { return "..." + x.Substring(Math.Min(from, x.Length), Math.Min(300, Math.Max(0, x.Length - from))); }
            return "differ at char " + i + "\n  want " + W(a) + "\n  got  " + W(b);
        }

        static string Data(string file) { return Path.Combine(AppContext.BaseDirectory, "Mind", "Data", file); }

        /// <summary>Order-sensitive text of a token (object keys in order; Newtonsoft compact form).</summary>
        static string Text(JToken t) { return t == null ? "null" : t.ToString(Formatting.None); }

        /// <summary>The first difference between two logs, or null when they are the same.</summary>
        public static string FirstDiff(string what, JArray want, List<JObject> got)
        {
            int n = Math.Min(want.Count, got.Count);
            for (int i = 0; i < n; i++)
            {
                string a = Text(want[i]), b = Text(got[i]);
                if (a != b)
                {
                    var w = (JObject)want[i]; var g = got[i];
                    foreach (var p in w.Properties())
                        if (Text(p.Value) != Text(g[p.Name])) return what + " #" + i + " field " + p.Name + ": " + Around(Text(p.Value), Text(g[p.Name]));
                    return what + " #" + i + ":\n  want " + a + "\n  got  " + b;
                }
            }
            if (want.Count != got.Count) return what + ": want " + want.Count + " entries, got " + got.Count;
            return null;
        }

        static async Task<(List<JObject> Llm, List<JObject> Bridge, List<JObject> Out)> RunAsync(string input)
        {
            using var tmp = new TempDir();
            var llm = new RecordingLlm(new FakeLlm());
            RecordingTransport tap = null;
            var cfg = new MindConfig { DataDir = tmp.Path };       // the Python capture used Config(bridge_url, bridge_token) with the defaults
            var o = await Replay.RunAsync(Data(input), cfg, llm, t => tap = new RecordingTransport(t));
            return (llm.Log, tap.Log, o);
        }

        [Theory]
        [InlineData("multiday", "multiday.jsonl")]
        [InlineData("mixed", "e2e-mixed.jsonl")]
        public Task ReplayMatchesThePythonRequestForRequest(string name, string input) { return Check(name, input); }

        // Main changed the chitchat wording of the game's own interactions (adapter.py _PASSING_KIND, the summary of a chitchat memory); the
        // "delta2" port brings it. Remove the Skip when that merges: this golden then proves it byte for byte.
        [Fact(Skip = "needs the delta2 chitchat wording (adapter.py _PASSING_KIND)")]
        public Task ChitchatReplayMatchesThePythonOnceTheNewWordingIsPorted() { return Check("chitchat", "e2e-chitchat.jsonl"); }

        static async Task Check(string name, string input)
        {
            var g = Golden(name);
            Assert.True(((JArray)g["llm"]).Count > 20 && ((JArray)g["bridge"]).Count > 20 && ((JArray)g["out"]).Count > 10);     // a golden that records nothing proves nothing
            var (llm, bridge, o) = await RunAsync(input);
            var diffs = new[]
            {
                FirstDiff("llm", (JArray)g["llm"], llm),
                FirstDiff("bridge", (JArray)g["bridge"], bridge),
                FirstDiff("out", (JArray)g["out"], o),
            }.Where(d => d != null).ToList();
            Assert.True(diffs.Count == 0, string.Join("\n", diffs));
        }
    }
}

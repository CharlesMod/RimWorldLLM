// Port of agent/tests/test_replay_multiday.py: replay over a recorded multi-day file shows reflections, diaries and chronicle entries.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.ReplayTool;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class ReplayMultidayTests
    {
        static string Data { get { return Path.Combine(System.AppContext.BaseDirectory, "Mind", "Data", "multiday.jsonl"); } }
        static string Kind(JObject i) { return (string)i["kind"]; }

        [Fact]
        public async Task ReplayShowsIdleWorkAndLeavesTheDataDirAlone()
        {
            using var tmp = new TempDir();
            string real = Path.Combine(tmp.Path, "real");
            var cfg = new MindConfig { DataDir = real, ThoughtEnabled = false };
            var o = await Replay.RunAsync(Data, cfg);
            var kinds = o.Select(Kind).ToList();
            Assert.True(new[] { "reflection", "diary", "chronicle" }.All(kinds.Contains));
            var days = new HashSet<int>(o.Where(i => Kind(i) == "diary").Select(i => (int)i["day"]));
            Assert.True(days.SetEquals(new[] { 0, 1, 2, 3 }));
            var chron = o.Where(i => Kind(i) == "chronicle").ToList();
            Assert.Contains(chron, c => (string)c["entry_kind"] == "major");
            Assert.True(chron.Count >= 3);
            Assert.Contains(o, i => !string.IsNullOrEmpty((string)i["text"]) && i["kind"] == null);       // actor lines still present
            Assert.False(Directory.Exists(real));                                                          // replay used its own temp dir
            Assert.Equal(kinds, (await Replay.RunAsync(Data, cfg)).Select(Kind).ToList());                // deterministic offline
        }

        [Fact]
        public async Task ReplayCliPrintsTheNewKinds()
        {
            using var tmp = new TempDir();
            var o = await Replay.RunAsync(Data, new MindConfig { DataDir = tmp.Path, ThoughtEnabled = false });
            string text = string.Join("\n", Replay.Format(o));
            Assert.Contains("[diary day 1]", text);
            Assert.Contains("[chronicle day", text);
            Assert.Contains("[reflection]", text);
        }
    }
}

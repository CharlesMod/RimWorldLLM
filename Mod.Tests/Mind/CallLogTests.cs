using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class CallLogTests
    {
        private static string[] Rows(string p) => File.ReadAllLines(p).Select(x => (string)JObject.Parse(x)["error"]).ToArray();

        // test_calllog_rotation.py::test_rotates_and_keeps_three
        [Fact]
        public void RotatesAndKeepsThree()
        {
            using var t = new TempDir();
            string p = t.Combine("calls.jsonl");
            var log = new CallLog(p, 1500, 3);
            for (int i = 0; i < 40; i++) log.Record(new CallStats { Kind = "line", Error = "r" + i });
            var names = Directory.GetFiles(t.Path).Select(Path.GetFileName).OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "calls.jsonl", "calls.jsonl.1", "calls.jsonl.2", "calls.jsonl.3" }, names);
            foreach (var f in Directory.GetFiles(t.Path)) Assert.True(new FileInfo(f).Length <= 1500);
            var seq = Rows(p + ".3").Concat(Rows(p + ".2")).Concat(Rows(p + ".1")).Concat(Rows(p)).ToArray();
            var nums = seq.Select(x => int.Parse(x.Substring(1))).ToArray();
            Assert.Equal(nums.OrderBy(x => x).ToArray(), nums);
            Assert.Equal(39, nums.Last());
            Assert.Equal(nums.Length, nums.Distinct().Count());
            Assert.True(nums[0] > 0);
        }

        // test_calllog_rotation.py::test_no_rotation_below_limit_and_none_path
        [Fact]
        public void NoRotationBelowLimitAndNullPath()
        {
            using var t = new TempDir();
            string p = t.Combine("calls.jsonl");
            var log = new CallLog(p);
            for (int i = 0; i < 5; i++) log.Record(new CallStats { Error = "r" + i });
            Assert.Equal(new[] { "calls.jsonl" }, Directory.GetFiles(t.Path).Select(Path.GetFileName).ToArray());
            Assert.Equal(5, Rows(p).Length);
            new CallLog(null).Record(new CallStats());
        }

        [Fact]
        public void ToDictFieldOrderAndRates()
        {
            var s = new CallStats { Kind = "line", Slot = 1, Actor = 7, PromptN = 100, CacheN = 50, PromptMs = 40, PredictedN = 5, PredictedMs = 80.5, WallS = 0.5, Ts = 12.5, Error = "é" };
            // Python: json.dumps(CallStats(...).to_dict(), ensure_ascii=False)
            Assert.Equal("{\"kind\": \"line\", \"slot\": 1, \"actor\": 7, \"prompt_n\": 100, \"cache_n\": 50, \"prompt_ms\": 40.0, \"predicted_n\": 5, \"predicted_ms\": 80.5, \"wall_s\": 0.5, \"ttft_s\": null, \"ok\": true, \"error\": \"é\", \"ts\": 12.5, \"raw_prefill_tps\": 2500.0, \"effective_prefill_tps\": 3750.0}",
                PyJson.Dumps(s.ToDict(), ensureAscii: false));
            Assert.Equal(0.0, new CallStats().RawPrefillTps);
        }

        [Fact]
        public void PyRoundIsHalfEvenOnExactValue()
        {
            Assert.Equal(0.2, PyRound.Round(0.25, 1));    // exact tie -> even
            Assert.Equal(0.3, PyRound.Round(0.35, 1));    // values from Python: round(0.35,1)=0.3, round(2.675,2)=2.67
            Assert.Equal(2.67, PyRound.Round(2.675, 2));
            Assert.Equal(0.5, PyRound.Round(0.45, 1));
            Assert.Equal(1234.5, PyRound.Round(1234.55, 1));
            Assert.Equal(-0.2, PyRound.Round(-0.25, 1));
        }
    }
}

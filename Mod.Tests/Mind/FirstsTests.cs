// Ports of the Director-free tests of agent/tests/test_firsts.py, plus recordings from the Python.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class FirstsTests
    {
        class FA : IFirstsAdapter
        {
            public (string Key, string Phrase)? FirstOf(JObject e)
            {
                return (string)e["type"] == "first_cue" ? ("snow", "seen snow") : ((string, string)?)null;
            }
        }

        [Fact]
        public void PickSpeakersIsStableAndBounded()
        {
            var fm = Enumerable.Range(1, 8).ToDictionary(i => i, i => "x");
            var a = Firsts.PickSpeakers(fm, 5);
            Assert.True(a.Count == 2 && a.SequenceEqual(Firsts.PickSpeakers(fm, 5)));
            var c = new DayCap();
            Assert.True(c.Allow(1, 3, 1)); c.Spend(1, 3);
            Assert.True(!c.Allow(1, 3, 1) && c.Allow(1, 4, 1) && c.Allow(2, 3, 1));
        }

        [Fact]
        public void DayCapForgetsOldDays()
        {
            var c = new DayCap();
            c.Spend(1, 1); c.Spend(1, 3);
            Assert.False(c.N.ContainsKey((1, 1)));
            Assert.True(c.N.ContainsKey((1, 3)));
        }

        [Fact]
        public void MarksRecordAndPickMatchPython()
        {
            var r = (JObject)MindsRec.J["firsts"];
            var fm = new Dictionary<int, MindM> { { 1, new MindM(1) }, { 2, new MindM(2) { Firsts = { "first:snow" } } }, { 3, new MindM(3) } };
            var e = JObject.Parse("{\"type\":\"first_cue\",\"witnesses\":[1,2,3,8]}");
            var mk = Firsts.Marks(fm, e, new FA());
            Assert.Equal(((JObject)r["marks"]).Properties().Select(p => p.Name + "=" + (string)p.Value), mk.Select(kv => kv.Key + "=" + kv.Value));
            Assert.Empty(Firsts.Marks(fm, JObject.Parse("{\"type\":\"x\",\"witnesses\":[1]}"), new FA()));
            Assert.Empty(Firsts.Marks(fm, e, new object()));                       // no first_of on the adapter
            Assert.Equal(((JArray)r["record"]).Select(x => (int)x), Firsts.Record(fm, e, new FA(), mk));
            foreach (var p in (JObject)r["after"])
                Assert.Equal(((JArray)p.Value).Select(x => (string)x), fm[int.Parse(p.Key)].Firsts);
            var ids = Enumerable.Range(1, 8).ToDictionary(i => i, i => "x");
            var seqs = new object[] { 5, "ab", 1.5, null, true };
            var want = (JArray)r["pick"];
            for (int i = 0; i < seqs.Length; i++)
                Assert.Equal(want[i].Select(x => (int)x), Firsts.PickSpeakers(ids, seqs[i]));
            Assert.Equal(((JArray)r["pick1"]).Select(x => (int)x), Firsts.PickSpeakers(ids, 5, 1));
            Assert.Equal(((JArray)r["pick5"]).Select(x => (int)x), Firsts.PickSpeakers(ids, 5, 5));
        }
    }
}

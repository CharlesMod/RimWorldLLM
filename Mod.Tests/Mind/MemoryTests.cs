// Ports of the world-free tests of agent/tests/test_memory_minds.py, plus recordings from the Python.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class MemoryTests
    {
        const int TicksPerDay = 60000;
        static JObject Rec { get { return MindsRec.J; } }
        static string C(JToken t) { return PyModel.Compact(t); }
        static string Dump(Memory m) { return m == null ? "null" : D(m); }
        static string D(object o) { return PyModel.Compact(PyModel.Dump(o)); }

        // Stand-in for the RimWorld adapter (wave 2): the wording of RimWorldAdapter for a victim / a bystander.
        class StubAdapter : IMemoryAdapter
        {
            public bool Wording;
            public double Importance(JObject e, int p) { return p == 1 ? 1.7 : (p == 2 ? -0.5 : 0.42); }
            public string FirstPerson(JObject e, int p) { return Wording ? (p == 1 ? "It happened to me." : "I saw it") : "I saw it " + p; }
            public bool IsPrivate(JObject e, int p) { return p == 2; }
            public bool IsPinned(JObject e, int p, List<string> f) { return p == 3; }
        }

        static JObject Ev(int seq = 1, int[] witnesses = null)
        {
            return JObject.FromObject(new Dictionary<string, object> {
                { "seq", seq }, { "game", 1000 }, { "type", "injury" }, { "summary", "Ana was shot." }, { "severity", 0.5 },
                { "actors", new[] { new { id = 1, role = "victim" }, new { id = 3, role = "attacker" } } },
                { "witnesses", witnesses ?? new[] { 1, 2 } }, { "data", new JObject() } });
        }

        [Fact]
        public void WitnessOnlyMemory()
        {
            var minds = new Dictionary<int, MindM>();
            foreach (var i in new[] { 1, 2, 3, 4 }) minds[i] = new MindM(i);
            var w = MemoryOps.RememberEvent(minds, Ev(), new StubAdapter { Wording = true });
            Assert.Equal(new[] { 1, 2 }, w.Select(x => x.Item1).OrderBy(x => x).ToArray());
            Assert.True(minds[1].Memories.Count > 0 && minds[2].Memories.Count > 0);
            Assert.True(minds[3].Memories.Count == 0 && minds[4].Memories.Count == 0);   // involved (3) but not a witness: nothing
            Assert.StartsWith("It happened to me", minds[1].Memories[0].Text);
            Assert.StartsWith("I saw it", minds[2].Memories[0].Text);
            Assert.Equal("saw", minds[1].Memories[0].Source);
            MemoryOps.RememberEvent(minds, Ev(), new StubAdapter { Wording = true });                      // idempotent per seq
            Assert.Single(minds[1].Memories);
        }

        [Fact]
        public void SelectionTop2To4AndRelevance()
        {
            var m = new MindM(1);
            long now = 10L * TicksPerDay;
            for (int i = 0; i < 10; i++) m.Memories.Add(new Memory { Id = "m" + i, Game = now - i * 1000, Text = "I saw a thing number " + i, Importance = 0.4 });
            m.Memories.Add(new Memory { Id = "rel", Game = now - 3L * TicksPerDay, Text = "Bram stole my rifle from the armory", Importance = 0.4, People = { 2 } });
            var sel = MemoryOps.SelectMemories(m, now, new HashSet<int> { 2 }, "Bram is near the armory");
            Assert.InRange(sel.Count, 2, 4);
            Assert.Contains("rel", sel.Select(x => x.Id));                                // relevance wins over a little recency
            Assert.Equal(sel.Select(x => x.Game).OrderBy(x => x).ToList(), sel.Select(x => x.Game).ToList());   // stable order
            Assert.Empty(MemoryOps.SelectMemories(new MindM(1), now, new HashSet<int>(), ""));
            var one = new MindM(1); one.Memories.Add(new Memory { Id = "a", Game = 0, Text = "x" });
            Assert.Single(MemoryOps.SelectMemories(one, now, new HashSet<int>(), ""));
        }

        [Fact]
        public void ToldMemoryLowerConfidence()
        {
            var m = new MindM(1);
            var t = MemoryOps.RememberTold(m, 2, "Bram", "Bram told me: Cy hoards food", 10);
            Assert.True(t.Source == "told" && t.Confidence < 1 && t.ToldBy == "Bram");
        }

        [Fact]
        public void MergeOldMemories()
        {
            var m = new MindM(1);
            for (int i = 0; i < 4; i++) m.Memories.Add(new Memory { Id = "m" + i, Game = i, Text = "injury " + i, Etype = "injury", Importance = 0.3 });
            Assert.Equal(4, MemoryOps.MergeOldMemories(m, 20L * TicksPerDay));
            Assert.True(m.Memories.Count == 1 && m.Memories[0].SummaryOf == 4);
        }

        // ---- recordings ----
        [Fact]
        public void KeywordsMatchPython()
        {
            var got = MemoryOps.Keywords("The Cat's  can't stop; ÉCOLE it's naïve and Ünï rock 12ab 'xx' İstanbul").OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(((JArray)Rec["keywords"]).Select(x => (string)x), got);
        }

        [Fact]
        public void ScoreSelectSortSimilarityMatchPython()
        {
            var m = MindsTests.Build();
            long now = 40 * 5000 + 3 * 60000;
            var sel = MemoryOps.SelectMemories(m, now, new HashSet<int> { 2, 3 }, "Bram stole the thing near here", 60000);
            Assert.Equal(((JArray)Rec["select"]).Select(x => (string)x), sel.Select(x => x.Id));
            var words = MemoryOps.Keywords("thing word near");
            var score = m.Memories.Take(6).Select(x => MemoryOps.ScoreMemory(m, x, now, new HashSet<int> { 2, 3 }, words, 60000)).ToList();
            Assert.Equal(((JArray)Rec["score"]).Select(x => (double)x), score);
            Assert.Equal(((JArray)Rec["weight_sorted"]).Select(x => (string)x), MemoryOps.WeightSorted(m, now).Take(8).Select(x => x.Id));
            var sim = Enumerable.Range(0, 5).Select(i => MemoryOps.Similarity(m.Memories[i], m.Memories[i + 1])).ToList();
            Assert.Equal(((JArray)Rec["sim"]).Select(x => (double)x), sim);
        }

        [Fact]
        public void UpkeepMatchesPython()
        {
            var mu = MindsTests.Build(60);
            for (int i = 0; i < mu.Memories.Count; i++)
            {
                var x = mu.Memories[i];
                x.Pinned = i % 13 == 0; x.Visibility = "public"; x.Etype = i % 4 != 0 ? "raid_start" : "injury";
                x.Text = i % 4 != 0 ? "raid hit " + (i % 3) + " the colony wall" : "I saw injury " + i;
                x.Importance = i % 17 == 0 ? 0.02 : x.Importance;
            }
            var r = MemoryOps.Upkeep(mu, 40L * 60000, 60000);
            var rec = (JObject)Rec["upkeep"];
            Assert.Equal((int)rec["r"]["merged"], r.Merged);
            Assert.Equal((int)rec["r"]["dropped"], r.Dropped);
            Assert.Equal((string)rec["mem"], C(new JArray(mu.Memories.Select(x => PyModel.Dump(x)))));
            Assert.Equal((int)rec["counter"], mu.MemCounter);
        }

        [Fact]
        public void RecordToldLineMatchesPython()
        {
            var ms = new Dictionary<int, MindM>();
            foreach (var i in new[] { 1, 2, 3, 4 }) ms[i] = new MindM(i);
            ms[1].Memories.Add(new Memory { Id = "a", Game = 10, Text = "I saw Cy steal food from the pantry", People = { 1, 5 }, Importance = 0.8, EventSeq = 7 });
            ms[1].Memories.Add(new Memory { Id = "s", Game = 11, Text = "I love Bo secretly", People = { 1, 2 }, Importance = 0.9, Visibility = "private" });
            var names = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bo" }, { 3, "Cy" }, { 4, "Di" } };
            var res = new List<string>
            {
                Dump(MemoryOps.RecordToldLine(ms, 1, 2, names, "Cy stole food from the pantry", 100, "public", 5)),
                Dump(MemoryOps.RecordToldLine(ms, 1, 3, names, "Cy stole food from the pantry", 101, "public", 5, chain: true)),
                Dump(MemoryOps.RecordToldLine(ms, 3, 4, names, "Cy stole food from the pantry", 102, "public", 5, chain: true, hopDecay: 0.7)),
                Dump(MemoryOps.RecordToldLine(ms, 4, 1, names, "Cy stole food from the pantry", 103, "public", 5, chain: true)),
                Dump(MemoryOps.RecordToldLine(ms, 4, 3, names, "Cy stole food from the pantry", 104, "public", 5, chain: true)),
                Dump(MemoryOps.RecordToldLine(ms, 1, 2, names, "I love Bo secretly", 105, "private", 2)),
                Dump(MemoryOps.RecordToldLine(ms, 1, 9, names, "x", 106, "public")),
                Dump(MemoryOps.RecordToldLine(ms, 1, 2, names, "Bo is flirting with Cy", 107, "public", 3, minorIds: new HashSet<int> { 3 })),
                Dump(MemoryOps.RecordToldLine(ms, 7, 2, names, "random gossip", 108, "public", null)),
            };
            var rec = (JObject)Rec["told"];
            var want = (JArray)rec["res"];
            for (int i = 0; i < res.Count; i++)
                Assert.Equal(C(want[i]), res[i]);
            foreach (var p in (JObject)rec["minds"])
                Assert.Equal((string)p.Value, ms[int.Parse(p.Key)].DumpJson());
            var cs = Rec["can_state"] as JArray;
            var ours = ms[1].Memories.Select(x => new[] { "Cy", "Bo", null }.Select(n => MemoryOps.CanState(x, n)).ToArray()).ToList();
            for (int i = 0; i < ours.Count; i++) Assert.Equal(((JArray)cs[i]).Select(x => (bool)x), ours[i]);
            Assert.Equal(((JArray)Rec["secret"]).Select(x => (string)x), MemoryOps.SecretTexts(ms[1], "Bo"));
        }

        [Fact]
        public void RememberEventMatchesPython()
        {
            var rec = (JObject)Rec["remember"];
            var mw = new Dictionary<int, MindM> { { 1, new MindM(1) }, { 2, new MindM(2) }, { 3, new MindM(3) } };
            var ev = JObject.Parse("{\"seq\":5,\"game\":1000,\"type\":\"injury\",\"actors\":[{\"id\":1,\"role\":\"v\"},{\"id\":3,\"role\":\"a\"}],\"witnesses\":[1,2,3,9]}");
            var w1 = MemoryOps.RememberEvent(mw, ev, new StubAdapter());
            var w2 = MemoryOps.RememberEvent(mw, ev, new StubAdapter());
            var w3 = MemoryOps.RememberEvent(mw, JObject.Parse("{\"type\":\"x\",\"actors\":[],\"witnesses\":[1]}"), new StubAdapter(), 777);
            Assert.Equal(C(rec["w1"]), C(new JArray(w1.Select(x => new JArray(x.Item1, PyModel.Dump(x.Item2))))));
            Assert.Equal((int)rec["w2"], w2.Count);
            Assert.Equal(C(rec["w3"]), C(new JArray(w3.Select(x => new JArray(x.Item1, PyModel.Dump(x.Item2))))));
            foreach (var p in (JObject)rec["minds"])
            {
                var m = mw[int.Parse(p.Key)];
                Assert.Equal(((JArray)p.Value[0]).Select(x => (string)x), m.Firsts);
                Assert.Equal((double)p.Value[1], m.UnreflectedImportance);
                Assert.Equal((int)p.Value[2], m.MemCounter);
            }
        }
    }
}

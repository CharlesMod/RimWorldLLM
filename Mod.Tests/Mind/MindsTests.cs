// Ports of agent/tests/test_memory_minds.py (the model-only tests) and the blob round-trip in test_firsts.py,
// plus recordings from the Python (MindsRec.cs) for JSON parity.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class MindsTests
    {
        const int TicksPerDay = 60000;
        static JObject Rec { get { return MindsRec.J; } }

        static string C(JToken t) { return PyModel.Compact(t); }

        // The same mind rec_c.py builds.
        public static MindM Build(int n = 40)
        {
            var m = new MindM(7, "Ana Ünï \"q\"\n") { Notes = "n\ttab" };
            m.Fixed = new FixedBio { Text = "Bio ✓", FactsHash = "abc", Extras = JObject.Parse("{\"a\":1,\"b\":[1.5,null,\"x\"],\"c\":{\"d\":2.0}}") };
            m.Voice = new VoiceCard { RegisterLine = "dry", Tics = { "hm", "well" }, Examples = { "Ana walks.", "ok", "fine" } };
            m.EvolvingBio = "evolved";
            for (int i = 0; i < 12; i++) m.AddBioDiff(i * 100, "bio " + i + " " + new string('x', 80));
            for (int i = 0; i < n; i++)
            {
                bool told = i % 5 == 0;
                m.Memories.Add(new Memory
                {
                    Id = "m" + i, Game = i * 5000, Text = "I saw thing " + i + " " + string.Concat(Enumerable.Repeat("word ", 15)),
                    Importance = 0.1 + (i % 7) / 10.0, Confidence = i % 3 != 0 ? 1.0 : 0.6, Pinned = i % 11 == 0,
                    People = new List<int> { 1, 2 + i % 3 }, Etype = "injury", EventSeq = i % 2 != 0 ? i : (long?)null,
                    ToldBy = told ? "Bo" : null, ToldById = told ? 3 : (int?)null, Source = told ? "told" : "saw",
                    Visibility = new[] { "public", "private", "told" }[i % 3],
                    ToldTo = i % 3 == 2 ? new List<string> { "Cy" } : new List<string>(),
                });
            }
            m.MemCounter = n;
            for (int i = 0; i < 9; i++) m.AddDiary(i, i * 60000L, "diary entry " + new string('y', 60));
            m.Relations["3"] = new Relation { Name = "Bo", Feeling = "wary", History = new string('h', 40), Opinion = -12, UpdatedTime = 5 };
            m.Relations["2"] = new Relation { Name = "Cy", Feeling = "warm", Opinion = 30, UpdatedTime = 9 };
            m.Relations["9"] = new Relation { Name = "Di", Opinion = null, UpdatedTime = 1 };
            for (int i = 0; i < 30; i++)
                m.Relations[(100 + i).ToString()] = new Relation { Name = "R" + i, Feeling = "f", History = new string('g', 30), Opinion = i - 15, UpdatedTime = i };
            m.FreezeRelations(4, new Dictionary<string, string> { { "3", "people say: grumpy" }, { "2", "people say: kind" }, { "9", "x" } });
            m.Firsts = new List<string> { "raid", "first:snow" };
            m.UnreflectedImportance = 1.25;
            m.LastReflectTime = 1234;
            for (int i = 0; i < 8; i++) m.Inner.Add(new InnerThought { Game = i, Text = string.Concat(Enumerable.Repeat("think ", 10)), Kind = "musing" });
            for (int i = 0; i < 5; i++) m.HabitHistory.Add(new HabitChange { Game = i, Added = "a", Removed = "b" });
            m.LastHabitGame = 4;
            for (int i = 0; i < 6; i++)
                m.Concerns.Add(new Concern { Id = "c" + i, Text = string.Concat(Enumerable.Repeat("worry ", 5)), About = { 1 }, Status = new[] { "open", "resolved", "faded" }[i % 3], Importance = 0.5 });
            for (int i = 0; i < 6; i++)
                m.Grudges.Add(new Grudge { Id = "g" + i, Against = 2, Phase = new[] { "avoid", "done", "resolve" }[i % 3], Cause = new string('c', 30) });
            return m;
        }

        // ---- ported from test_memory_minds.py ----
        [Fact]
        public void DecayHalfLifeFourDays()
        {
            var m = new MindM(1);
            var mem = new Memory { Id = "a", Game = 0, Text = "x", Importance = 0.8 };
            double w0 = m.MemoryWeight(mem, 0);
            double w4 = m.MemoryWeight(mem, 4 * TicksPerDay);
            Assert.True(Math.Abs(w4 / w0 - 0.5) < 1e-9);
            mem.Pinned = true;
            Assert.Equal(m.MemoryWeight(mem, 0), m.MemoryWeight(mem, 40 * TicksPerDay));
        }

        [Fact]
        public void BlobSizeCapTrimsDecayedFirst()
        {
            var m = new MindM(1, "Ana");
            long now = 30L * TicksPerDay;
            for (int i = 0; i < 600; i++)
                m.Memories.Add(new Memory { Id = "m" + i, Game = i * 100, Text = string.Concat(Enumerable.Repeat("I saw something happen. ", 8)), Importance = 0.3 });
            m.Memories.Add(new Memory { Id = "pin", Game = 5, Text = "My brother died.", Importance = 1.0, Pinned = true });
            m.Memories.Add(new Memory { Id = "recent", Game = now - 10, Text = "Fresh and important.", Importance = 0.9 });
            var blob = m.ToBlob(now);
            int size = Encoding.UTF8.GetByteCount(C(blob));
            Assert.True(size <= MindM.BlobLimit);
            var ids = new HashSet<string>(((JArray)blob["memories"]).Select(x => (string)x["id"]));
            Assert.True(ids.Contains("pin") && ids.Contains("recent"));         // pinned and fresh survive
            Assert.True(ids.Count < 600);                                         // decayed ones went
            Assert.True(MindM.FromBlob(blob, 1).Memories.Count > 0);
        }

        [Fact]
        public void BlobRoundtripAndBadBlob()
        {
            var m = new MindM(1, "Ana") { Wants = { "a" }, Notes = "n" };
            var m2 = MindM.FromBlob(m.ToBlob(), 1);
            Assert.True(m2.Wants.SequenceEqual(new[] { "a" }) && m2.Notes == "n");
            Assert.Equal(5, MindM.FromBlob(JObject.Parse("{\"memories\":\"garbage\"}"), 5, "Z").ActorId);
            Assert.Empty(MindM.FromBlob(null, 5).Memories);
        }

        [Fact]
        public void BioDiffHistoryAndUndo()
        {
            var m = new MindM(1);
            m.AddBioDiff(1, "first"); m.AddBioDiff(2, "second");
            Assert.True(m.EvolvingBio == "second" && m.BioHistory.Count == 2);
            Assert.True(m.UndoBio() && m.EvolvingBio == "first");
        }

        // ---- ported from test_firsts.py ----
        [Fact]
        public void BlobRoundTripKeepsFirsts()
        {
            var m = new MindM(1) { Firsts = new List<string> { "raid", "first:snow" } };
            Assert.Equal(new[] { "raid", "first:snow" }, MindM.FromBlob(m.ToBlob(), 1, "Ana").Firsts);
        }

        // ---- recordings: JSON parity with pydantic ----
        [Fact]
        public void FullMindDumpsLikeModelDumpJson()
        {
            Assert.Equal((string)Rec["full"], Build().DumpJson());
        }

        [Fact]
        public void FullMindLoadsAndReserialisesIdentically()
        {
            string json = (string)Rec["full"];
            var m = MindM.FromBlob(JObject.Parse(json), 7, "ignored");
            Assert.Equal(json, m.DumpJson());
            Assert.Equal(json, MindM.FromJson(json).DumpJson());
            Assert.Equal(new[] { "3", "2", "9", "100", "101" }.Length, 5);
            Assert.Equal(new[] { "3", "2", "9" }, m.Relations.Keys.Take(3).ToArray());   // insertion order survives the round trip
        }

        [Fact]
        public void FreezeRelationsMatchesPython()
        {
            Assert.Equal((string)Rec["freeze"], C(new JArray(Build().CRelations.Select(c => PyModel.Dump(c)))));
        }

        public static IEnumerable<object[]> Limits() { foreach (var l in new[] { 30720, 9000, 6000, 3500 }) yield return new object[] { l }; }

        [Theory, MemberData(nameof(Limits))]
        public void ToBlobTrimsLikePython(int limit)
        {
            long now = 40 * 5000 + 3 * 60000;
            var m = Build();
            Assert.Equal((string)Rec["blob_" + limit], C(m.ToBlob(now, 60000, limit)));
        }

        [Fact]
        public void FromBlobIgnoresExtrasCoercesAndDefaults()
        {
            var t = (JObject)Rec["load_extra_missing"];
            var m = MindM.FromBlob((JObject)t["in"], 5, "Z");
            Assert.Equal((string)t["out"], m.DumpJson());
            Assert.Equal(5, m.ActorId);
        }

        [Fact]
        public void FromBlobInvalidGivesFreshMind()
        {
            foreach (var t in (JArray)Rec["bad"])
                Assert.Equal((string)t["out"], MindM.FromBlob((JObject)t["in"], 5, "Z").DumpJson());
            Assert.Equal((string)Rec["empty"], MindM.FromBlob(new JObject(), 5, "Z").DumpJson());
        }

        [Fact]
        public void SpeaksThirdPersonMatchesPython()
        {
            var names = new HashSet<string> { "Tuk" };
            var want = ((JArray)Rec["speaks"]).Select(x => (bool)x).ToArray();
            var got = new[] {
                new VoiceCard { Examples = { "Tuk hunts.", "x" } }.SpeaksThirdPerson(names),
                new VoiceCard { Examples = { "Tuk, come", "Tuk's bow" } }.SpeaksThirdPerson(names),
                new VoiceCard { Examples = { "Tuk and me" }, ThirdPerson = true }.SpeaksThirdPerson(names),
                new VoiceCard { Examples = { "Tuk hunts." }, ThirdPerson = false }.SpeaksThirdPerson(names) };
            Assert.Equal(want, got);
        }

        [Fact]
        public void NextMemIdAndNewtonsoftNames()
        {
            var m = new MindM(1);
            Assert.Equal("m1", m.NextMemId());
            Assert.Equal("m2", m.NextMemId());
            var j = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new Memory { Id = "a", Game = 1, Text = "t" }));
            Assert.Equal("a", (string)j["id"]);
            Assert.NotNull(j["told_by_id"]);
        }
    }
}

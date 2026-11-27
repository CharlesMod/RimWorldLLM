// Ports of the world-free parts of agent/tests/test_upkeep_gossip.py (memory upkeep, blob size, gossip, confiding, secrets).
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class UpkeepGossipTests
    {
        const int Day = 60000;

        static Memory Mem(int i, string text, long game = 0)
        {
            return new Memory { Id = "m" + i, Game = game, Text = text };
        }

        // ---- step 2 ----
        [Fact]
        public void SimilarityUsesSharedActorsAndWords()
        {
            var a = Mem(1, "Bram snubbed me at the table"); a.People = new List<int> { 1, 2 };
            var b = Mem(2, "Bram snubbed me at supper"); b.People = new List<int> { 1, 2 };
            var c = Mem(3, "The fire spread through the barn"); c.People = new List<int> { 3 };
            Assert.True(MemoryOps.Similarity(a, b) > 0.5 && 0.5 > MemoryOps.Similarity(a, c));
        }

        [Fact]
        public void MergeBySimilarityAcrossEventTypesAndSparesPinsAndSecrets()
        {
            var m = new MindM { ActorId = 1 };
            m.MemCounter = 20;
            for (int i = 0; i < 4; i++)
            {
                var x = Mem(i, "Bram snubbed me at the table again", i);
                x.Etype = i % 2 == 1 ? "social" : "thought"; x.People = new List<int> { 1, 2 }; x.Importance = 0.3 + (double)i / 10;
                m.Memories.Add(x);
            }
            var p = Mem(10, "Bram snubbed me at the table again", 5); p.People = new List<int> { 1, 2 }; p.Visibility = "private"; m.Memories.Add(p);
            var q = Mem(11, "Bram snubbed me at the table again", 6); q.People = new List<int> { 1, 2 }; q.Pinned = true; m.Memories.Add(q);
            var r = Mem(12, "The barn burned down", 7); r.People = new List<int> { 3 }; m.Memories.Add(r);
            Assert.Equal(4, MemoryOps.MergeOldMemories(m, 20L * Day));
            var ids = m.Memories.Select(x => x.Id).ToList();
            Assert.True(new[] { "m10", "m11", "m12" }.All(ids.Contains) && !new[] { "m0", "m1", "m2", "m3" }.Any(ids.Contains));
            var s = m.Memories.First(x => x.SummaryOf != 0);
            Assert.True(s.SummaryOf == 4 && s.Importance == 0.6 * 0.8 && s.People.SequenceEqual(new[] { 1, 2 }));
            Assert.Equal(0, MemoryOps.MergeOldMemories(m, 20L * Day));          // a summary is not merged again
        }

        [Fact]
        public void RecentMemoriesAreNotMerged()
        {
            var m = new MindM { ActorId = 1 };
            for (int i = 0; i < 5; i++) { var x = Mem(i, "Bram snubbed me", 19L * Day); x.People = new List<int> { 2 }; m.Memories.Add(x); }
            Assert.Equal(0, MemoryOps.MergeOldMemories(m, 20L * Day));
        }

        [Fact]
        public void DropDecayedUnpinnedBelowFloor()
        {
            var m = new MindM { ActorId = 1 };
            var a = Mem(1, "old minor", 0); a.Importance = 0.3;
            var b = Mem(2, "old pinned", 0); b.Importance = 0.3; b.Pinned = true;
            var c = Mem(3, "fresh", 40L * Day); c.Importance = 0.3;
            var d = Mem(4, "old big", 0); d.Importance = 1.0;
            m.Memories = new List<Memory> { a, b, c, d };
            var r = MemoryOps.Upkeep(m, 40L * Day, Day, floor: 0.03);
            var ids = m.Memories.Select(x => x.Id).ToHashSet();
            Assert.True(ids.SetEquals(new[] { "m2", "m3", "m4" }) || ids.SetEquals(new[] { "m2", "m3" }));
            Assert.True(r.Dropped >= 1 && !ids.Contains("m1"));
            Assert.Contains("m2", ids);                                          // pinned survives any age
        }

        [Fact]
        public void BlobStaysUnder32kWithDiariesBioHistoryAndManyMemories()
        {
            var m = new MindM { ActorId = 1, Name = "Ana" };
            m.Fixed.Text = new string('x', 3000);
            for (int i = 0; i < 400; i++)
            {
                var x = Mem(i, string.Concat(Enumerable.Repeat("I remember something long and detailed about day " + i + ". ", 3)), i * 900);
                x.Importance = (i % 10) / 10.0; x.Pinned = i % 37 == 0; x.People = new List<int> { 2, 3 };
                m.Memories.Add(x);
            }
            for (int i = 0; i < 12; i++) m.AddBioDiff(i, string.Concat(Enumerable.Repeat("bio revision " + i + " ", 30)));
            for (int d = 0; d < 9; d++) m.AddDiary(d, d * Day, string.Concat(Enumerable.Repeat("Diary text for the day. ", 12)));
            for (int i = 0; i < 30; i++)
                m.Relations["" + i] = new Relation { Name = "P" + i, Feeling = new string('f', 80), History = new string('h', 80), UpdatedTime = i };
            Assert.True(m.Diary.Count == 7 && m.BioHistory.Count == 10);
            var blob = m.ToBlob(400 * 900, Day);
            int size = Encoding.UTF8.GetByteCount(blob.ToString(Formatting.None));
            Assert.True(size <= MindM.BlobTarget && MindM.BlobTarget <= MindM.BlobLimit);
            Assert.Contains(((Newtonsoft.Json.Linq.JArray)blob["memories"]), x => (bool)x["pinned"]);     // pins are the last to go
            Assert.Equal(m.EvolvingBio, MindM.FromBlob(blob, 1).EvolvingBio);
        }

        [Fact]
        public void BlobTrimsDiariesBeforePinnedMemories()
        {
            var m = new MindM { ActorId = 1 };
            for (int i = 0; i < 60; i++) { var x = Mem(i, string.Concat(Enumerable.Repeat("pinned thing ", 20))); x.Pinned = true; x.Importance = 0.9; m.Memories.Add(x); }
            for (int d = 0; d < 7; d++) m.AddDiary(d, d, string.Concat(Enumerable.Repeat("diary ", 100)));
            int size0 = Encoding.UTF8.GetByteCount(PyModel.Compact(PyModel.Dump(m)));
            Assert.True(size0 > 3000);   // Python also asserts size0 > BLOB_TARGET: its json.dumps default separators are larger than the compact form measured here
            var blob = m.ToBlob(0, Day, limit: size0 - 2000);   // Python used json.dumps(...) (larger) with -3000; this compact measure needs a smaller cut for the same intent
            Assert.True(((Newtonsoft.Json.Linq.JArray)blob["diary"]).Count < 7 && ((Newtonsoft.Json.Linq.JArray)blob["memories"]).Count == 60);
        }

        // ---- step 3 ----
        [Fact]
        public void GossipGivesToldByMemoryWithLowerConfidenceAndBookkeeping()
        {
            var minds = new Dictionary<int, MindM> { { 1, new MindM { ActorId = 1 } }, { 2, new MindM { ActorId = 2 } } };
            var src = Mem(1, "I saw Cy hoarding food in the shed"); src.People = new List<int> { 3 }; src.Importance = 0.6;
            minds[1].Memories.Add(src);
            var names = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" }, { 3, "Cy" } };
            var t = MemoryOps.RecordToldLine(minds, 1, 2, names, "Cy is hoarding food in the shed, you know.", 100, "public", 3);
            Assert.True(t.Source == "told" && t.ToldBy == "Ana" && t.ToldById == 1);
            Assert.True(t.Confidence == 0.5 && t.Visibility == "public" && t.People.Contains(3) && t.People.Contains(1));
            Assert.True(minds[1].Memories[0].ToldTo.SequenceEqual(new[] { "Bram" }) && minds[1].Memories[0].Visibility == "public");
            // ("(told by Ana, not sure)" in block_d: Prompt module deferred)
        }

        [Fact]
        public void ConfideMovesPrivateMemoryToToldToListener()
        {
            var minds = new Dictionary<int, MindM> { { 1, new MindM { ActorId = 1 } }, { 2, new MindM { ActorId = 2 } } };
            var sec = Mem(1, "I hid the knife under the floor"); sec.Visibility = "private"; sec.Importance = 0.7;
            var pub = Mem(2, "Public thing"); pub.Importance = 0.9;
            minds[1].Memories.Add(sec); minds[1].Memories.Add(pub);
            var names = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" } };
            var t = MemoryOps.RecordToldLine(minds, 1, 2, names, "I hid a knife somewhere, Bram.", 100, "private");
            Assert.True(sec.Visibility == "told" && sec.ToldTo.SequenceEqual(new[] { "Bram" }));
            Assert.True(t.Visibility == "private" && t.Confidence == 0.7);        // the listener now holds a secret
            Assert.True(MemoryOps.CanState(sec, "Bram") && !MemoryOps.CanState(sec, "Cy"));
            // ("[private, told to Bram]" in block_d: Prompt module deferred)
            Assert.Empty(MemoryOps.SecretTexts(minds[1], "Bram"));
            Assert.Equal(new[] { sec.Text }, MemoryOps.SecretTexts(minds[1], "Cy"));
        }

        [Fact]
        public void LeaksSecretIsVerbatimQuoteNotAHint()
        {
            string secret = "It happened to me: Ana stole the rifle from the armory at night.";
            Assert.True(Sanitize.LeaksSecret("Yes, I stole the rifle from the armory.", new[] { secret }));
            Assert.False(Sanitize.LeaksSecret("There is something about that armory I will not say.", new[] { secret }));
            Assert.True(Sanitize.LeaksSecret("secret code", new[] { "secret code" }));         // a short secret: the whole phrase
            var speaker = new HashSet<string> { "Ana" }; var known = new HashSet<string> { "Bram" }; var all = new HashSet<string> { "Ana", "Bram" };
            Assert.Equal("leaks_secret", Sanitize.SanitizeLine("I stole the rifle from the armory.", speaker, known, all, secrets: new[] { secret }).Reason);
            Assert.True(Sanitize.SanitizeLine("I know something about that armory.", speaker, known, all, secrets: new[] { secret }).Ok);
        }

        [Fact]
        public void ConfidedListenerMayHearItStatedButAThirdPartyMayNot()
        {
            var m = new MindM { ActorId = 1 };
            m.Memories.Add(new Memory { Id = "m50", Game = 10, Text = "I hid the knife under the floorboards in the barn.",
                                        Visibility = "told", ToldTo = new List<string> { "Bram" }, Importance = 0.9 });
            Assert.Empty(MemoryOps.SecretTexts(m, "Bram"));
            Assert.NotEmpty(MemoryOps.SecretTexts(m, "Cy"));
        }

        // Deferred: test_director_runs_upkeep_once_per_game_day, test_private_memory_is_hinted_never_quoted_in_a_line (Director + world fixture);
        // test_adapter_pins_deaths_betrayals_firsts_and_flags_secrets, test_block_a_carries_the_secret_rule (RimWorldAdapter, a later wave).
    }
}

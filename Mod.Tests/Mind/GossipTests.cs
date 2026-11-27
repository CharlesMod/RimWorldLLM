// Ports of the world-free parts of agent/tests/test_gossip_chain.py.
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class GossipTests
    {
        static readonly Dictionary<int, string> Names = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" }, { 3, "Cy" }, { 4, "Dee" } };

        static Dictionary<int, MindM> Minds(params int[] ids)
        {
            return ids.ToDictionary(i => i, i => new MindM { ActorId = i, Name = Names[i] });
        }

        static Memory Saw(MindM mind, string text, long seq = 7, int[] people = null, double imp = 0.8, string vis = "public")
        {
            var m = new Memory { Id = mind.NextMemId(), Game = 10, Text = text, Source = "saw", Importance = imp,
                                 People = (people ?? new[] { 3 }).ToList(), EventSeq = seq, Visibility = vis };
            mind.Memories.Add(m);
            return m;
        }

        static Memory Tell(Dictionary<int, MindM> ms, int a, int b, string line, int about = 3, double hopDecay = 0.8, ISet<int> minorIds = null)
        {
            return MemoryOps.RecordToldLine(ms, a, b, Names, line, 20, "public", about, chain: true, hopDecay: hopDecay, minorIds: minorIds);
        }

        [Fact]
        public void ChainHopsAndDecayOverTwoRetellings()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            var m2 = Tell(ms, 1, 2, "Cy hoards food.");
            Assert.Equal(new List<string> { "Ana" }, m2.Chain); Assert.Equal(1, m2.Hops); Assert.Equal(7, m2.EventSeq);
            Assert.Equal(0.5, m2.Confidence);                                   // first hop: today's value
            var m4 = Tell(ms, 2, 4, "They say Cy hoards the food.");
            Assert.Equal(new List<string> { "Ana", "Bram" }, m4.Chain); Assert.Equal(2, m4.Hops); Assert.Equal(7, m4.EventSeq);
            Assert.Equal(System.Math.Round(0.5 * 0.5 * 0.8, 3), m4.Confidence);     // base x source confidence x decay^1
            Assert.Equal("Bram told me: They say Cy hoards the food.", m4.Text);    // the retelling's own words
        }

        [Fact]
        public void HopDecayIsConfigurable()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            Tell(ms, 1, 2, "Cy hoards food.");
            var m4 = Tell(ms, 2, 4, "Cy hoards food, they say.", hopDecay: 0.5);
            Assert.Equal(System.Math.Round(0.5 * 0.5 * 0.5, 3), m4.Confidence);
        }

        [Fact]
        public void DuplicatesMergeAndKeepTheHigherConfidence()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            var first = Tell(ms, 1, 4, "Cy hoards food.");                     // Dee hears it first hand-ish (0.5)
            Tell(ms, 1, 2, "Cy hoards food.");
            var again = Tell(ms, 2, 4, "Cy hoards food indeed.");              // weaker, second-hand
            Assert.Same(first, again);
            Assert.Single(ms[4].Memories.Where(m => m.Source == "told"));
            Assert.True(first.Sources == 2 && first.Confidence == 0.5 && first.Chain.SequenceEqual(new[] { "Ana" }));
            // ("heard from several people" in block_d: Prompt module deferred)
        }

        [Fact]
        public void StrongerVersionReplacesTheWeakerOne()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            Tell(ms, 1, 2, "Cy hoards food.");
            var weak = Tell(ms, 2, 4, "Cy hoards food, so I heard.");
            Assert.Equal(2, weak.Hops);
            var direct = Tell(ms, 1, 4, "Cy hoards the food, I saw it.");
            Assert.Same(weak, direct);
            Assert.True(weak.Hops == 1 && weak.Chain.SequenceEqual(new[] { "Ana" }) && weak.Confidence == 0.5 && weak.Sources == 2);
        }

        [Fact]
        public void AStoryNeverComesBackToItsTeller()
        {
            var ms = Minds(1, 2);
            Saw(ms[1], "I saw Cy hoard the food.");
            Tell(ms, 1, 2, "Cy hoards food.");
            Assert.Null(Tell(ms, 2, 1, "Cy hoards food, Ana said."));
        }

        [Fact]
        public void ChainOffIsTodaysBehaviour()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            var a = MemoryOps.RecordToldLine(ms, 1, 2, Names, "Cy hoards food.", 20, "public", 3);
            var b = MemoryOps.RecordToldLine(ms, 2, 4, Names, "Cy hoards food.", 20, "public", 3);
            Assert.True(a.Chain.Count == 0 && a.Hops == 0 && a.EventSeq == null);
            Assert.Equal(System.Math.Round(0.5 * 1.0, 3), b.Confidence);       // told memories are not retold without the chain
        }

        [Fact]
        public void SecretsAreNeverRetoldOrHinted()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy weep in secret.", vis: "private");
            var m = Tell(ms, 1, 2, "Cy weeps in secret.");
            Assert.True(m == null || (m.Chain.SequenceEqual(new[] { "Ana" }) && ms[1].Memories[0].Visibility == "private"));
            Assert.Empty(Gossip.PassOnCandidates(ms[1], ms[4], 4, new HashSet<int> { 1, 4 }));
            // a confided (private) line stays out of chains
            var c = MemoryOps.RecordToldLine(ms, 1, 2, Names, "I weep.", 20, "private", null, chain: true);
            Assert.True(c.Chain.Count == 0 && c.Visibility == "private");
        }

        [Fact]
        public void MinorRomanticLineIsNotRecorded()
        {
            var ms = Minds(1, 2);
            Saw(ms[1], "I saw Cy hoard the food.");
            Assert.Null(Tell(ms, 1, 2, "Cy and Dee kissed in the barn.", minorIds: new HashSet<int> { 3 }));
            Assert.NotNull(Tell(ms, 1, 2, "Cy hoards food.", minorIds: new HashSet<int> { 3 }));
        }

        [Fact]
        public void PassOnOnlyUnknownPublicItemsAboutAbsentActors()
        {
            var ms = Minds(1, 2, 3);
            var a = Saw(ms[1], "I saw Cy hoard the food.", 7, new[] { 3 });
            Saw(ms[1], "I argued with Bram.", 8, new[] { 2 });                    // the listener is in it
            Saw(ms[1], "I saw Cy weep.", 9, new[] { 3 }, vis: "private");
            Saw(ms[1], "I saw Dee steal a tool.", 10, new[] { 4 }, imp: 0.9);
            var cands = Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2 });
            Assert.Equal(new long?[] { 10, 7 }, cands.Select(m => m.EventSeq).ToArray());
            Assert.Equal(7, Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2, 4 })[0].EventSeq);   // Dee is present: not absent
            Saw(ms[2], "I too saw Dee steal.", 10, new[] { 4 });                  // the listener heard it already
            Assert.Equal(new long?[] { 7 }, Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2 }).Select(m => m.EventSeq).ToArray());
            a.ToldTo.Add("Bram");
            Assert.Empty(Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2 }));
            var hint = Gossip.PassOnHint(ms[1], ms[3], 3, new HashSet<int> { 1, 3 });
            Assert.StartsWith("Something you could pass on: ", hint);
            Assert.True(hint.Length <= 125);
        }

        [Fact]
        public void PassOnSkipsRomanticTextAroundMinors()
        {
            var ms = Minds(1, 2);
            Saw(ms[1], "I saw Cy kissing Dee.", 7, new[] { 3, 4 });
            Assert.Empty(Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2 }, new[] { 3 }));
            Assert.NotEmpty(Gossip.PassOnCandidates(ms[1], ms[2], 2, new HashSet<int> { 1, 2 }, new int[0]));
        }

        [Fact]
        public void ReputationLinesFromHeardMemories()
        {
            var ms = Minds(1, 2, 4);
            Saw(ms[1], "I saw Cy hoard the food.");
            Tell(ms, 1, 2, "Cy hoards food.");
            Tell(ms, 1, 4, "Cy hoards food.");
            var reps = Gossip.Reputations(ms);
            Assert.Equal(new[] { 3 }, reps.Keys.ToArray());
            Assert.StartsWith("Cy hoards food", reps[3]); Assert.Contains("(2 people)", reps[3]);
            Assert.DoesNotContain(1, reps.Keys);                                 // the teller is not the subject
            // (the block_c "people say:" assertions: Prompt module deferred)
        }

        [Fact]
        public void ReputationLeavesOutRomanceAboutMinors()
        {
            var ms = Minds(1, 2);
            Saw(ms[1], "I saw Cy kissing Dee.", 7, new[] { 3, 4 });
            Tell(ms, 1, 2, "Cy was kissing Dee.", about: 3);
            Assert.Empty(Gossip.Reputations(ms, new[] { 3 }));
            Assert.NotEmpty(Gossip.Reputations(ms));
        }

        [Fact]
        public void StripTellerAndClip()
        {
            Assert.Equal("X y", Gossip.StripTeller("Bram told me:   X y "));
            Assert.Equal("no teller here", Gossip.StripTeller("no teller here"));
            Assert.Equal("one two", Gossip.Clip("one   two", 10));
            Assert.Equal("one two...", Gossip.Clip("one two three four", 10));
            Assert.Equal("abcdefghij...", Gossip.Clip("abcdefghijklmnop", 10));
        }

        // Deferred: test_director_wires_chain_hint_and_switches (Director world fixture; Director._common_talk/refresh_reputations).
    }
}

using System.Collections.Generic;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class LoopGuardTests
    {
        static List<(int, string)> T(params (int, string)[] t) { return new List<(int, string)>(t); }

        [Fact]
        public void SimilarityShortRepliesAreLegitimate()
        {
            foreach (var line in new[] { "Yes.", "No.", "Fine.", "Copy that.", "Okay then." })
                Assert.Equal(0.0, LoopGuard.Similarity(line, line));
            Assert.Null(LoopGuard.FindRepeat("Yes.", 2, T((1, "Yes."), (2, "Yes."))));
        }

        [Fact]
        public void VerbatimAndNearRepeatsAreCaught()
        {
            var prior = T((1, "Okay so I'm bored, like seriously nothing to do right now..."), (2, "Hm. Go feed the animals."));
            Assert.NotNull(LoopGuard.FindRepeat("Okay so I'm bored, like seriously nothing to do right now...", 1, prior));   // verbatim, own
            var h = LoopGuard.FindRepeat("Okay so I am bored, seriously, nothing to do right now.", 1, prior);
            Assert.True(h != null && h.Own);
            // parroting the other's line is also a repeat
            h = LoopGuard.FindRepeat("Hm. Go feed the animals now.", 1, prior);
            Assert.True(h != null && !h.Own);
            // a cut-down copy of the speaker's own line
            Assert.NotNull(LoopGuard.FindRepeat("I watch the perimeter.", 2, T((2, "I watch the perimeter. The muffalo is good meat."))));
        }

        [Fact]
        public void HabitOpenersDoNotMakeLinesAlike()
        {
            var prior = T((1, "Okay so I'm trying, but this rifle feels like it's gonna snap..."));
            Assert.Null(LoopGuard.FindRepeat("Okay so Biscuit brought some herbs for me to check out...", 1, prior));
            Assert.Null(LoopGuard.FindRepeat("Naturally, the hunger is a fleeting affliction in this bleak establishment.", 2,
                T((2, "Naturally, the provisions are as sparse as the prospects for this dreary existence."))));
        }

        [Fact]
        public void OwnLinesAcrossConversationsNeedAHigherBar()
        {
            var own = new List<string> { "Hm. Back hurts. Get that pan over here." };
            Assert.NotNull(LoopGuard.FindRepeat("Hm. Back hurts. Get that pan over here.", 1, T(), own));
            Assert.Null(LoopGuard.FindRepeat("Hm. Back hurts, so move the crate.", 1, T(), own));
        }

        // Not in the Python tests: pins the Hit reason, retry request text and stats dict.
        [Fact]
        public void RetryRequestAndStatsShape()
        {
            var h = new Hit("turn", "Go feed the animals.", 0.9, true);
            Assert.Equal("it repeats your own earlier line: \"Go feed the animals.\"", h.Reason);
            Assert.Equal("=== REQUEST ===\nWrite Ana's next line of speech now. One line only.\n"
                + "Your last attempt was refused because it repeats an earlier line in this conversation: \"x\". Say something new: "
                + "answer what was just said, or move the talk on. Do not repeat or rephrase any earlier line.",
                LoopGuard.RetryRequest("Ana", new Hit("turn", "x", 0.7)));
            var st = new LoopStats { Checked = 3, Caught = 1, Retried = 1, Ended = 1 };
            Assert.Equal("{\"checked\":3,\"caught\":1,\"retried\":1,\"recovered\":0,\"ended\":1}", st.ToDict().ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}

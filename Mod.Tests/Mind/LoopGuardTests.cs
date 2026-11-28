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

        [Fact]
        public void AnotherPawnsRecentLineIsARepeat()
        {
            var colony = T((2, "Turnips aren't much, but they fill the stomach."), (3, "Mind the ice on the step."));
            var h = LoopGuard.FindRepeat("Turnips are not much, but they fill the stomach.", 1, T(), null, spokenRecent: colony);
            Assert.NotNull(h);
            Assert.Equal("others", h.Kind);
            Assert.False(h.Own);
            Assert.Contains("someone else", h.Reason);
            Assert.Null(LoopGuard.FindRepeat("The roof over the freezer needs patching before winter.", 1, T(), null, spokenRecent: colony));
            // the speaker's own line in the colony list is the own check's business, not a colony hit
            Assert.Null(LoopGuard.FindRepeat("Mind the ice on the step.", 3, T(), null, spokenRecent: colony));
        }

        [Fact]
        public void AnOpeningTwoRecentLinesShareIsRefused()
        {
            var colony = T((1, "I wonder if the corn will make it."), (2, "Fine weather for it."), (3, "I wonder what Bram is cooking."),
                           (4, "Keep the door shut."));
            var h = LoopGuard.FindRepeat("I wonder about the roof over the freezer.", 5, T(), null, spokenRecent: colony);
            Assert.NotNull(h);
            Assert.Equal("opening", h.Kind);
            Assert.Contains("i wonder", h.Reason);
            Assert.Contains("different", h.Reason);
            // only one earlier line with that opening is fine, and a line under two words has no opening
            Assert.Null(LoopGuard.FindRepeat("Keep watch on the north gate tonight.", 5, T(), null, spokenRecent: colony.GetRange(0, 2)));
            Assert.Null(LoopGuard.Opening("Hm."));
            // an opening older than the last eight lines no longer counts
            var old = T((1, "I wonder if the corn will make it."), (3, "I wonder what Bram is cooking."));
            for (int i = 0; i < LoopGuard.OpeningWindow; i++) old.Add((2, "Line number " + i + " about something else entirely."));
            Assert.Null(LoopGuard.FindRepeat("I wonder about the roof.", 5, T(), null, spokenRecent: old));
        }

        [Fact]
        public void ARepeatedThoughtAcrossDaysIsFound()
        {
            var recent = new[] { "I wonder if Tobias is thinking about me too.", "The corn is coming along." };
            Assert.NotNull(LoopGuard.FindRepeatedThought("I wonder if Tobias is thinking of me too.", recent));
            Assert.Null(LoopGuard.FindRepeatedThought("My back still aches from the digging.", recent));
            Assert.Null(LoopGuard.FindRepeatedThought("Anything.", null));
        }

        [Fact]
        public void ADiaryOpeningLikeARecentOneIsFound()
        {
            var recent = new[] { "Today was quiet. We ate stew and I slept early.", "Raiders came at dawn. Two of us bled." };
            Assert.NotNull(LoopGuard.FindRepeatedDiaryOpening("Today was quiet. I mended my boots and nothing else.", recent));
            Assert.Null(LoopGuard.FindRepeatedDiaryOpening("The mortar shell took the wall. Bram carried me in.", recent));
            Assert.Equal("Raiders came at dawn.", LoopGuard.FirstSentence("Raiders came at dawn. Two of us bled."));
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

using System.Collections.Generic;
using Xunit;

namespace HermesVoice.Tests
{
    public class ThoughtPacingTests
    {
        const int PerPawn = 7200, Global = 1200;
        static ThoughtPacing.Verdict Offer(ThoughtPacing p, int pawn, int now, bool active = false, bool speech = false)
        { return p.Offer(pawn, "a thought", now, active, speech, PerPawn, Global); }

        [Fact]
        public void SurfaceUsesTheShortFieldAndNeverALongText()
        {
            Assert.Equal("short one", ThoughtPacing.Surface("short one", "a very long reflective paragraph"));
            Assert.Equal("only text", ThoughtPacing.Surface(null, "only text"));
            Assert.Null(ThoughtPacing.Surface(null, new string('x', 91)));
            Assert.Null(ThoughtPacing.Surface("", ""));
            string cut = ThoughtPacing.Surface(string.Join(" ", new string[40].Length > 0 ? System.Linq.Enumerable.Repeat("word", 40) : null), null);
            Assert.True(cut.Length <= ThoughtPacing.MaxSurfaceChars && cut.EndsWith("…"));
        }

        [Fact]
        public void OnePerPawnAtATimeAndAPerPawnGap()
        {
            var p = new ThoughtPacing();
            Assert.Equal(ThoughtPacing.Verdict.Show, Offer(p, 1, 0));
            Assert.Equal(ThoughtPacing.Verdict.Drop, Offer(p, 1, 100, active: true));
            Assert.Equal(ThoughtPacing.Verdict.Drop, Offer(p, 1, PerPawn - 1));          // too soon for this pawn
            Assert.Equal(ThoughtPacing.Verdict.Show, Offer(p, 1, PerPawn + 1));
        }

        [Fact]
        public void GlobalGapAppliesAcrossPawns()
        {
            var p = new ThoughtPacing();
            Assert.Equal(ThoughtPacing.Verdict.Show, Offer(p, 1, 0));
            Assert.Equal(ThoughtPacing.Verdict.Drop, Offer(p, 2, Global - 1));
            Assert.Equal(ThoughtPacing.Verdict.Show, Offer(p, 2, Global + 1));
        }

        [Fact]
        public void NeverOverAVisibleSpeechBalloon_WaitsThenShowsWhenItIsGone()
        {
            var p = new ThoughtPacing();
            Assert.Equal(ThoughtPacing.Verdict.Wait, Offer(p, 1, 0, speech: true));
            var ready = new List<KeyValuePair<int, string>>();
            p.Promote(10, id => true, Global, ready);
            Assert.Empty(ready);                                                          // still speaking
            p.Promote(200, id => false, Global, ready);
            Assert.Single(ready); Assert.Equal(1, ready[0].Key);
            Assert.Equal(0, p.PendingCount);
        }

        [Fact]
        public void AWaitingThoughtIsDroppedWhenStale()
        {
            var p = new ThoughtPacing();
            Offer(p, 1, 0, speech: true);
            var ready = new List<KeyValuePair<int, string>>();
            p.Promote(ThoughtPacing.StaleTicks + 1, id => false, Global, ready);
            Assert.Empty(ready); Assert.Equal(0, p.PendingCount);
        }

        [Fact]
        public void OnlyOnePendingThoughtPerPawn()
        {
            var p = new ThoughtPacing();
            Assert.Equal(ThoughtPacing.Verdict.Wait, Offer(p, 1, 0, speech: true));
            Assert.Equal(ThoughtPacing.Verdict.Drop, Offer(p, 1, 5, speech: true));
        }
    }
}

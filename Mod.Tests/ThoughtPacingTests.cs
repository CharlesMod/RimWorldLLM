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
    
        [Fact]
        public void GapsAreRealSecondsAtEverySpeed()
        {
            Assert.Equal(20 * 60, ThoughtPacing.GapTicks(20, 60f));
            Assert.Equal(20 * 180, ThoughtPacing.GapTicks(20, 180f));          // 2x
            Assert.Equal(20 * 900, ThoughtPacing.GapTicks(20, 900f));          // 4x: still 20 s of watching
            Assert.Equal(20 * 60, ThoughtPacing.GapTicks(20, 0f));             // paused or odd: never below the 1x count
            Assert.Equal(0, ThoughtPacing.GapTicks(0, 900f));
            Assert.Equal(ThoughtPacing.StaleTicks, ThoughtPacing.StaleFor(60f) );
            Assert.Equal(10 * 360, ThoughtPacing.StaleFor(360f));              // a waiting thought lives 10 s at 3x too
        }

        [Fact]
        public void ThoughtsPerRealMinuteDoNotGrowWithSpeed()
        {
            foreach (float tps in new[] { 60f, 180f, 360f, 900f })
            {
                var p = new ThoughtPacing(); int shown = 0, step = (int)(tps / 10);   // offer every 0.1 real s for 10 real minutes
                int perPawn = ThoughtPacing.GapTicks(120, tps), glob = ThoughtPacing.GapTicks(20, tps);
                for (int t = 0; t < 6000; t++)
                    if (p.Offer(t % 8, "x", t * step, false, false, perPawn, glob) == ThoughtPacing.Verdict.Show) shown++;
                Assert.InRange(shown, 20, 31);                                  // global gap 20 s: at most 30 in 10 min
            }
        }
}
}

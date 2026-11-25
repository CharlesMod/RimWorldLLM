using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class BubbleArrangeTests
    {
        static readonly BRect Bounds = new BRect(4, 4, 1000, 600);

        // Lays out balloons the way the game does: DefaultRect above each target, then Arrange.
        static (List<BRect> rects, TailInfo[] tails) Lay(params (float w, float h, float x, float y, BubbleKind k)[] bs)
        {
            var rects = bs.Select(b => BubbleLayout.DefaultRect(b.w, b.h, b.x, b.y, Bounds)).ToList();
            var tails = new TailInfo[bs.Length];
            BubbleLayout.Arrange(rects, bs.Select(b => b.x).ToList(), bs.Select(b => b.y).ToList(),
                                 bs.Select(b => b.k).ToList(), Bounds, 3f, tails);
            return (rects, tails);
        }

        static void AssertNoCrossings(List<BRect> rects, TailInfo[] tails)
        {
            for (int i = 0; i < rects.Count; i++)
                for (int j = 0; j < rects.Count; j++)
                    if (i != j) Assert.False(BubbleLayout.TailCrossesRect(tails[i], rects[j]), "tail " + i + " crosses balloon " + j);
            for (int i = 0; i < rects.Count; i++)
                for (int j = 0; j < i; j++)
                    Assert.False(rects[i].Overlaps(rects[j], 0f), "overlap " + i + "," + j);
        }

        [Fact]
        public void SegmentRectIntersection()
        {
            var r = new BRect(10, 10, 20, 20);
            Assert.True(BubbleLayout.SegmentHitsRect(0, 20, 40, 20, r));
            Assert.False(BubbleLayout.SegmentHitsRect(0, 5, 40, 5, r));
            Assert.True(BubbleLayout.SegmentHitsRect(15, 0, 15, 15, r));
            Assert.False(BubbleLayout.SegmentHitsRect(15, 0, 15, 9, r));
        }

        [Fact]
        public void TwoCloseSpeakersSitSideBySideNotStacked()
        {
            var (rects, tails) = Lay((120, 44, 300, 400, BubbleKind.Say), (120, 44, 340, 400, BubbleKind.Say));
            Assert.Equal(rects[0].Y, rects[1].Y, 2);              // same row: side by side
            AssertNoCrossings(rects, tails);
        }

        [Fact]
        public void StackedPairNeverHasATailThroughTheOtherBalloon()
        {
            // The case from the preview: the second speaker is almost under the first and the balloons are wide.
            var (rects, tails) = Lay((200, 60, 300, 400, BubbleKind.Say), (200, 60, 310, 400, BubbleKind.Say));
            AssertNoCrossings(rects, tails);
        }

        [Fact]
        public void CrowdOfSpeakersNeverCrosses()
        {
            var rnd = new Random(7);
            for (int round = 0; round < 200; round++)
            {
                int n = 2 + rnd.Next(5);
                var bs = new List<(float, float, float, float, BubbleKind)>();
                for (int i = 0; i < n; i++)
                {
                    var k = (BubbleKind)rnd.Next(0, 4);
                    bs.Add((70 + rnd.Next(170), 36 + rnd.Next(50), 150 + rnd.Next(500), 200 + rnd.Next(300), k));
                }
                var (rects, tails) = Lay(bs.ToArray());
                for (int i = 0; i < rects.Count; i++)
                    for (int j = 0; j < rects.Count; j++)
                        if (i != j) Assert.False(BubbleLayout.TailCrossesRect(tails[i], rects[j]), "round " + round + ": tail " + i + " crosses " + j);
            }
        }

        [Fact]
        public void SingleBalloonKeepsItsDefaultPlaceAndTail()
        {
            var (rects, tails) = Lay((120, 44, 300, 400, BubbleKind.Say));
            var d = BubbleLayout.DefaultRect(120, 44, 300, 400, Bounds);
            Assert.Equal(d.X, rects[0].X, 2); Assert.Equal(d.Y, rects[0].Y, 2);
            Assert.Equal(BubbleLayout.ChooseTail(BubbleKind.Say, d, 300, 400).Length, tails[0].Length, 2);
        }
    }
}

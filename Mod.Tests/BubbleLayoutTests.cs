using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class BubbleLayoutTests
    {
        // A fixed-width "font": 6 units per character.
        static readonly Func<string, float> M = s => s.Length * 6f;

        static string Join(List<string> l) { return string.Join("|", l); }

        // ------------------------------------------------------------------ variant choice
        [Theory]
        [InlineData(null, "insult", "You are a fool", false, BubbleKind.Shout)]
        [InlineData(null, "confide", "Between us", false, BubbleKind.Whisper)]
        [InlineData(null, "gossip", "Did you hear", false, BubbleKind.Whisper)]
        [InlineData(null, "kind", "Nice work", false, BubbleKind.Say)]
        [InlineData(null, "none", "Hello there", false, BubbleKind.Say)]
        [InlineData(null, null, "Hello there", false, BubbleKind.Say)]
        [InlineData(null, "none", "Stop right now!!", false, BubbleKind.Shout)]
        [InlineData(null, "none", "Get down!", true, BubbleKind.Shout)]
        [InlineData(null, "none", "Get down!", false, BubbleKind.Say)]
        [InlineData(null, "none", "WATCH OUT BEHIND YOU", false, BubbleKind.Shout)]
        [InlineData(null, "none", "OK", false, BubbleKind.Say)]
        [InlineData("thought", "insult", "Hmm", false, BubbleKind.Thought)]
        [InlineData("narrator", "none", "Later that day", false, BubbleKind.Narrator)]
        [InlineData("whisper", "insult", "psst", false, BubbleKind.Whisper)]
        [InlineData("SHOUT", "none", "hey", false, BubbleKind.Shout)]
        [InlineData("normal", "insult", "You are a fool", false, BubbleKind.Say)]   // the agent said it normally: no guessing
        [InlineData("normal", "none", "Get down!!", true, BubbleKind.Say)]
        [InlineData("normal", "none", "*hmm*", false, BubbleKind.Thought)]          // markers still work
        [InlineData("bogus", "insult", "You are a fool", false, BubbleKind.Shout)]  // unknown kind: heuristics
        [InlineData("", "confide", "Between us", false, BubbleKind.Whisper)]
        public void KindFollowsData(string kindField, string intent, string text, bool combat, BubbleKind want)
        {
            string clean;
            Assert.Equal(want, BubbleLayout.ChooseKind(kindField, intent, text, combat, out clean));
        }

        [Fact]
        public void MarkersAreStripped()
        {
            string c;
            Assert.Equal(BubbleKind.Thought, BubbleLayout.ChooseKind(null, "none", "*I miss home*", false, out c));
            Assert.Equal("I miss home", c);
            Assert.Equal(BubbleKind.Thought, BubbleLayout.ChooseKind(null, "none", "(why me)", false, out c));
            Assert.Equal("why me", c);
            Assert.Equal(BubbleKind.Narrator, BubbleLayout.ChooseKind(null, "none", "[Dusk falls]", false, out c));
            Assert.Equal("Dusk falls", c);
            Assert.Equal(BubbleKind.Say, BubbleLayout.ChooseKind(null, "none", "a (b) c", false, out c));
            Assert.Equal("a (b) c", c);
        }

        [Fact]
        public void OnlyWhisperUsesSmallText()
        {
            foreach (BubbleKind k in Enum.GetValues(typeof(BubbleKind)))
                Assert.Equal(k == BubbleKind.Whisper, BubbleLayout.SmallText(k));
        }

        // ------------------------------------------------------------------ wrapping
        [Fact]
        public void ShortTextIsOneLine()
        {
            Assert.Equal("hello there", Join(BubbleLayout.Wrap("hello there", M, 200f, 4)));
        }

        [Fact]
        public void WrapIsBalancedNotGreedy()
        {
            // Greedy at width 150 (25 chars) gives "aaaa bbbb cccc dddd eeee" (24) then "ff": an orphan.
            var lines = BubbleLayout.Wrap("aaaa bbbb cccc dddd eeee ff", M, 150f, 4);
            Assert.Equal(2, lines.Count);
            Assert.True(Math.Abs(lines[0].Length - lines[1].Length) <= 6, Join(lines));
            Assert.All(lines, l => Assert.True(M(l) <= 150f));
        }

        [Fact]
        public void WrapNeverAddsLinesAndKeepsAllWords()
        {
            string text = "the quick brown fox jumps over the lazy dog and keeps running far away";
            var lines = BubbleLayout.Wrap(text, M, 160f, 6);
            Assert.Equal(text, string.Join(" ", lines));
            int greedy = (int)Math.Ceiling(text.Length * 6f / 160f);
            Assert.True(lines.Count <= greedy + 1);
            Assert.All(lines, l => Assert.True(M(l) <= 160f));
        }

        [Fact]
        public void WrapTruncatesPastMaxLinesWithEllipsis()
        {
            var lines = BubbleLayout.Wrap(string.Join(" ", Enumerable.Repeat("word", 40)), M, 90f, 3);
            Assert.Equal(3, lines.Count);
            Assert.EndsWith("…", lines[2]);
            Assert.All(lines, l => Assert.True(M(l) <= 90f));
        }

        [Fact]
        public void LongWordIsBrokenToFit()
        {
            var lines = BubbleLayout.Wrap("supercalifragilisticexpialidocious", M, 60f, 6);
            Assert.True(lines.Count > 1);
            Assert.All(lines, l => Assert.True(M(l) <= 60f));
        }

        [Fact]
        public void EmptyTextGivesOneEmptyLine()
        {
            Assert.Equal(new[] { "" }, BubbleLayout.Wrap("   ", M, 100f, 4).ToArray());
        }

        // ------------------------------------------------------------------ size
        [Fact]
        public void SizeGrowsWithText()
        {
            float w1, h1, w2, h2;
            BubbleLayout.BalloonSize(BubbleKind.Say, 60, 16, out w1, out h1);
            BubbleLayout.BalloonSize(BubbleKind.Say, 160, 48, out w2, out h2);
            Assert.True(w2 > w1 && h2 > h1);
            // text block plus margins and padding on each side
            Assert.True(w1 >= 60 + 2 * (BubbleMetrics.Margin(BubbleKind.Say) + BubbleMetrics.PadX(BubbleKind.Say)));
        }

        [Fact]
        public void ShortLinesGetEllipseExtraWidth()
        {
            float w, h;
            BubbleLayout.BalloonSize(BubbleKind.Say, 60, 16, out w, out h);
            float plain = 60 + 2 * (BubbleMetrics.Margin(BubbleKind.Say) + BubbleMetrics.PadX(BubbleKind.Say));
            Assert.True(w > plain);
            float nw, nh;
            BubbleLayout.BalloonSize(BubbleKind.Narrator, 60, 16, out nw, out nh);
            Assert.Equal(60 + 2 * (BubbleMetrics.Margin(BubbleKind.Narrator) + BubbleMetrics.PadX(BubbleKind.Narrator)), nw, 3);
        }

        [Theory]
        [InlineData(BubbleKind.Shout)]
        [InlineData(BubbleKind.Thought)]
        [InlineData(BubbleKind.Whisper)]
        public void TiledShapesSnapToWholePeriods(BubbleKind k)
        {
            for (float tw = 20; tw < 240; tw += 17)
            {
                float w, h;
                BubbleLayout.BalloonSize(k, tw, 30, out w, out h);
                float p = BubbleMetrics.Period(k), c = 2 * BubbleMetrics.Corner;
                Assert.True(w >= c && h >= c);
                Assert.Equal(0.0, Math.IEEERemainder((w - c) / p, 1.0), 4);
                Assert.Equal(0.0, Math.IEEERemainder((h - c) / p, 1.0), 4);
            }
        }

        [Fact]
        public void CornersShrinkOnShortRoundBalloons()
        {
            Assert.Equal(BubbleMetrics.Unit, BubbleLayout.CornerScale(BubbleKind.Say, 200, 120), 4);
            Assert.True(BubbleLayout.CornerScale(BubbleKind.Say, 200, 30) < BubbleMetrics.Unit);
            Assert.Equal(BubbleMetrics.Unit, BubbleLayout.CornerScale(BubbleKind.Shout, 200, 40), 4);
        }

        // ------------------------------------------------------------------ tail
        static readonly BRect Balloon = new BRect(100, 100, 120, 40);

        [Fact]
        public void TailPointsDownWhenPawnIsBelow()
        {
            var t = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 160, 170);
            Assert.Equal(TailSide.Bottom, t.Side);
            Assert.Equal(160f, t.AnchorX, 2);
            Assert.Equal(0f, t.Angle, 1);
            Assert.True(t.Length > 14);
        }

        [Fact]
        public void TailSlantsTowardAPawnToTheSide()
        {
            var left = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 90, 180);
            Assert.Equal(TailSide.Bottom, left.Side);
            Assert.True(left.Angle > 0, "pointing down-left is a clockwise rotation");
            var right = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 240, 180);
            Assert.True(right.Angle < 0);
            Assert.True(left.AnchorX < Balloon.CenterX && right.AnchorX > Balloon.CenterX);
        }

        [Fact]
        public void TailFlipsToTheSideWhenBalloonIsPushedPast()
        {
            var rightOfPawn = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 20, 120);
            Assert.Equal(TailSide.Left, rightOfPawn.Side);
            Assert.Equal(90f, rightOfPawn.Angle, 1);
            var leftOfPawn = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 330, 120);
            Assert.Equal(TailSide.Right, leftOfPawn.Side);
            Assert.Equal(-90f, leftOfPawn.Angle, 1);
        }

        [Fact]
        public void TailFlipsToTopWhenPawnIsAbove()
        {
            var t = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 160, 40);
            Assert.Equal(TailSide.Top, t.Side);
            Assert.Equal(180f, Math.Abs(t.Angle), 1);
            Assert.True(t.DirY < 0);
        }

        [Fact]
        public void TailAngleIsClamped()
        {
            var t = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, -400, 143);   // nearly level, far left
            Assert.InRange(Math.Abs(t.Angle - (t.Side == TailSide.Left ? 90f : 0f)), 0f, BubbleMetrics.MaxTailAngle + 0.01f);
        }

        [Fact]
        public void NoTailWhenTargetIsInsideOrForNarrator()
        {
            Assert.False(BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 160, 120).Has);
            Assert.False(BubbleLayout.ChooseTail(BubbleKind.Narrator, Balloon, 160, 200).Has);
        }

        [Fact]
        public void TailAnchorStaysClearOfCorners()
        {
            var t = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 105, 180);
            Assert.True(t.AnchorX >= Balloon.X + BubbleMetrics.Corner);
            var u = BubbleLayout.ChooseTail(BubbleKind.Say, Balloon, 215, 180);
            Assert.True(u.AnchorX <= Balloon.Right - BubbleMetrics.Corner);
        }

        [Fact]
        public void ThoughtDotsFollowTheTail()
        {
            var t = BubbleLayout.ChooseTail(BubbleKind.Thought, Balloon, 160, 190);
            var xs = new float[3]; var ys = new float[3]; var d = new float[3];
            Assert.Equal(3, BubbleLayout.ThoughtDots(t, xs, ys, d));
            Assert.True(ys[0] < ys[1] && ys[1] < ys[2]);
            Assert.True(d[0] > d[1] && d[1] > d[2]);
            Assert.Equal(0, BubbleLayout.ThoughtDots(new TailInfo(), xs, ys, d));
        }

        // ------------------------------------------------------------------ placement
        [Fact]
        public void SeparateClampsToBounds()
        {
            var bounds = new BRect(4, 4, 600, 400);
            var rs = new List<BRect> { new BRect(-50, 2, 100, 30), new BRect(560, 390, 100, 30) };
            BubbleLayout.Separate(rs, bounds, 2);
            Assert.Equal(4f, rs[0].X); Assert.Equal(4f, rs[0].Y);
            Assert.Equal(bounds.Right, rs[1].Right, 3); Assert.Equal(bounds.Bottom, rs[1].Bottom, 3);
        }

        [Fact]
        public void SeparatePushesOverlappingBalloonsApart()
        {
            var bounds = new BRect(4, 4, 600, 400);
            var rs = new List<BRect> { new BRect(200, 200, 100, 40), new BRect(210, 205, 100, 40), new BRect(205, 210, 100, 40) };
            var first = rs[0];
            BubbleLayout.Separate(rs, bounds, 2);
            Assert.Equal(first.Y, rs[0].Y);
            for (int i = 0; i < rs.Count; i++)
                for (int j = 0; j < i; j++)
                    Assert.False(rs[i].Overlaps(rs[j], 0f), "overlap " + i + "," + j);
            foreach (var r in rs) { Assert.True(r.Y >= bounds.Y && r.Bottom <= bounds.Bottom); }
        }

        [Fact]
        public void DefaultRectSitsAboveThePawnOrBelowWhenTheTopIsTooClose()
        {
            var bounds = new BRect(4, 4, 600, 400);
            var above = BubbleLayout.DefaultRect(100, 40, 300, 200, bounds);
            Assert.True(above.Bottom < 200);
            Assert.Equal(300f, above.CenterX, 3);
            var near = BubbleLayout.DefaultRect(100, 40, 300, 30, bounds);
            Assert.True(near.Y > 30, "flipped below the pawn");
            var tail = BubbleLayout.ChooseTail(BubbleKind.Say, near, 300, 30);
            Assert.Equal(TailSide.Top, tail.Side);
        }

        // ------------------------------------------------------------------ motion
        [Fact]
        public void PopStartsSmallOvershootsAndSettles()
        {
            Assert.Equal(BubbleMetrics.PopFrom, BubbleLayout.PopScale(0f), 4);
            Assert.Equal(1f, BubbleLayout.PopScale(BubbleMetrics.PopSeconds), 4);
            Assert.Equal(1f, BubbleLayout.PopScale(5f), 4);
            float max = 0;
            for (float t = 0; t < BubbleMetrics.PopSeconds; t += 0.002f) max = Math.Max(max, BubbleLayout.PopScale(t));
            Assert.InRange(max, 1.005f, 1.08f);
        }

        [Fact]
        public void FadeIsFullThenRampsToZero()
        {
            Assert.Equal(1f, BubbleLayout.FadeAlpha(500));
            Assert.Equal(1f, BubbleLayout.FadeAlpha(BubbleMetrics.FadeTicks));
            Assert.Equal(0.5f, BubbleLayout.FadeAlpha(BubbleMetrics.FadeTicks / 2), 3);
            Assert.Equal(0f, BubbleLayout.FadeAlpha(0));
        }

        // ------------------------------------------------------------------ shared constants
        [Fact]
        public void CSharpMetricsMatchTheSharedJson()
        {
            string dir = Path.GetDirectoryName(ThisFile());
            var j = JObject.Parse(File.ReadAllText(Path.Combine(dir, "..", "Mod", "Textures", "src", "bubble_metrics.json")));
            Assert.Equal(BubbleMetrics.Unit, (float)j["unit"]);
            Assert.Equal(BubbleMetrics.BorderSrc, (float)j["border"]);
            Assert.Equal(BubbleMetrics.InkSrc, (float)j["ink"]);
            Assert.Equal(BubbleMetrics.RimSrc, (float)j["rim"]);
            Assert.Equal(BubbleMetrics.TailW, (float)j["tail"]["width"]);
            Assert.Equal(BubbleMetrics.TailH, (float)j["tail"]["height"]);
            Assert.Equal(BubbleMetrics.TailStubSrc, (float)j["tail"]["stub"]);
            Assert.Equal(BubbleMetrics.DotSize, (float)j["dot"]["size"]);
            var shapes = new Dictionary<BubbleKind, string>
            {
                { BubbleKind.Say, "balloon" }, { BubbleKind.Narrator, "caption" }, { BubbleKind.Shout, "burst" },
                { BubbleKind.Thought, "cloud" }, { BubbleKind.Whisper, "dashed" }
            };
            foreach (var kv in shapes)
            {
                var s = j["shapes"][kv.Value];
                Assert.Equal(BubbleMetrics.MidSrc(kv.Key), (float)s["mid"]);
                Assert.Equal(BubbleMetrics.MarginSrc(kv.Key), (float)s["margin"]);
                Assert.Equal(BubbleMetrics.Tiled(kv.Key), (bool)s["tiled"]);
            }
        }

        [Fact]
        public void EveryTextureTheCodeLoadsExists()
        {
            string dir = Path.Combine(Path.GetDirectoryName(ThisFile()), "..", "Mod", "Textures", "HermesVoice", "Bubbles");
            foreach (var shape in new[] { "balloon", "caption", "burst", "cloud", "dashed", "tail", "dot" })
                foreach (var layer in new[] { "fill", "ink", "rim" })
                    Assert.True(File.Exists(Path.Combine(dir, shape + "_" + layer + ".png")), shape + "_" + layer);
        }

        static string ThisFile([CallerFilePath] string p = "") { return p; }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorldLLM;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ChatLayoutTests
    {
        static ChatLayout.Result L(float w, float h, bool right = false, float width = 420f, int lines = 10, float lineH = 22f)
        {
            return ChatLayout.Compute(new ChatLayout.Input { ScreenW = w, ScreenH = h, Width = width, Lines = lines, LineH = lineH, Right = right });
        }

        // vanilla HUD rects in UI units for a screen
        static ChatRect Readout(float w, float h) { return new ChatRect(7, 7, 133, h - 207); }
        static ChatRect Inspect(float w, float h) { return new ChatRect(0, h - 200, 432, 165); }
        static ChatRect BottomBar(float w, float h) { return new ChatRect(0, h - 35, w, 35); }
        static ChatRect RightColumn(float w, float h) { return new ChatRect(w - 200, 0, 200, h); }
        static ChatRect ColonistBar(float w, float h) { return new ChatRect(w * 0.2f, 0, w * 0.6f, 90); }

        static void ClearOfVanilla(ChatLayout.Result r, float w, float h)
        {
            var hud = new List<ChatRect> { Readout(w, h), Inspect(w, h), BottomBar(w, h), RightColumn(w, h) };
            if (h >= 400) hud.Add(ColonistBar(w, h));   // below that one line is kept even under the colonist bar
            foreach (var v in hud)
                Assert.False(r.Whole.Overlaps(v), r.Whole + " overlaps " + v);
            Assert.True(r.Whole.X >= 0 && r.Whole.Right <= w && r.Whole.Y >= 0 && r.Whole.Bottom <= h);
        }

        [Fact]
        public void LeftAtScale1On1080p()
        {
            var r = L(1920, 1080);
            Assert.Equal(new ChatRect(148, 844, 420, 28).ToString(), r.Bar.ToString());
            Assert.Equal(new ChatRect(148, 612, 420, 230).ToString(), r.Box.ToString());
            Assert.Equal(10, r.Lines);
            ClearOfVanilla(r, 1920, 1080);
        }

        [Fact]
        public void LeftAtScale2On1080p()
        {
            var r = L(960, 540);          // 1920x1080 at UI scale 2
            Assert.Equal(new ChatRect(148, 304, 420, 28).ToString(), r.Bar.ToString());
            Assert.Equal(8, r.Lines);     // 10 wanted, 8 fit under the colonist bar
            Assert.Equal(new ChatRect(148, 116, 420, 186).ToString(), r.Box.ToString());
            ClearOfVanilla(r, 960, 540);
        }

        [Fact]
        public void RightAtScale1GoesDownToTheButtonBar()
        {
            var r = L(1920, 1080, right: true);
            Assert.Equal(new ChatRect(1292, 1009, 420, 28).ToString(), r.Bar.ToString());
            Assert.Equal(new ChatRect(1292, 777, 420, 230).ToString(), r.Box.ToString());
            ClearOfVanilla(r, 1920, 1080);
        }

        [Fact]
        public void RightAtScale2StaysAboveTheInspectPane()
        {
            var r = L(960, 540, right: true);
            Assert.Equal(332f, r.Bar.X);
            Assert.Equal(332f, r.Bar.Bottom);
            ClearOfVanilla(r, 960, 540);
        }

        [Theory]
        [InlineData(1280, 720)] [InlineData(1366, 768)] [InlineData(1920, 1080)] [InlineData(2560, 1440)] [InlineData(3840, 2160)]
        [InlineData(640, 360)] [InlineData(853, 480)] [InlineData(1280, 540)] [InlineData(1707, 960)]
        public void ClearOfVanillaAtManySizesBothSides(int w, int h)
        {
            foreach (bool right in new[] { false, true })
                foreach (float width in new[] { 260f, 420f, 640f })
                    foreach (float lh in new[] { 22f, 29f })
                    {
                        var r = L(w, h, right, width, 12, lh);
                        ClearOfVanilla(r, w, h);
                        Assert.True(r.Lines >= 1);
                        Assert.Equal(r.Box.W, r.Bar.W);                 // the input bar is the panel's width
                        Assert.Equal(r.Box.X, r.Bar.X);
                        Assert.True(r.Bar.Y - r.Box.Bottom <= 2f && r.Bar.Y >= r.Box.Bottom);   // directly under the box
                    }
        }

        [Fact]
        public void WidthClampedAndShrunkOnNarrowScreens()
        {
            Assert.Equal(ChatLayout.MinW, L(1920, 1080, width: 10).Box.W);
            Assert.Equal(ChatLayout.MaxW, L(3840, 2160, width: 5000).Box.W);
            var r = L(853, 480, width: 640);
            Assert.Equal(853 - 148 - 208, r.Box.W);
        }

        [Fact]
        public void LinesAndFont()
        {
            Assert.Equal(4, L(1920, 1080, lines: 4).Lines);
            Assert.Equal(ChatLayout.MinLines, L(1920, 1080, lines: 0).Lines);
            var med = L(1920, 1080, lines: 10, lineH: 29);
            Assert.Equal(10 * 29 + 10, med.Box.H);
            Assert.Equal(L(1920, 1080).Bar.ToString(), med.Bar.ToString());   // the bar slot does not move with the font
        }

        [Fact]
        public void VisibleNewestAtBottom()
        {
            var hs = new List<float> { 22, 44, 22, 22 };
            float s = 0;
            var v = ChatLayout.Visible(hs, 66, ref s);
            Assert.Equal(new[] { 1, 2, 3 }, v.Select(p => p.Key).ToArray());
            Assert.Equal(new[] { -22f, 22f, 44f }, v.Select(p => p.Value).ToArray());   // entry 1 cut at the top
        }

        [Fact]
        public void VisibleScrollsAndClamps()
        {
            var hs = new List<float> { 22, 22, 22, 22, 22 };
            float s = 44;
            var v = ChatLayout.Visible(hs, 44, ref s);
            Assert.Equal(new[] { 1, 2 }, v.Select(p => p.Key).ToArray());
            s = 1000;
            v = ChatLayout.Visible(hs, 44, ref s);
            Assert.Equal(66f, s);
            Assert.Equal(new[] { 0, 1 }, v.Select(p => p.Key).ToArray());
            s = -5;
            ChatLayout.Visible(hs, 44, ref s);
            Assert.Equal(0f, s);
            s = 10;
            ChatLayout.Visible(new List<float> { 22 }, 44, ref s);   // everything fits: no scroll
            Assert.Equal(0f, s);
            Assert.Empty(ChatLayout.Visible(new List<float>(), 44, ref s));
        }

        [Fact]
        public void AlphaFreshnessProgressDots()
        {
            Assert.Equal(0.85f, ChatLayout.BackgroundAlpha(0.2f, true, 0));
            Assert.Equal(0.2f, ChatLayout.BackgroundAlpha(0.2f, false, 0), 3);
            Assert.Equal(0.35f, ChatLayout.BackgroundAlpha(0.2f, false, 1), 3);
            Assert.Equal(1f, ChatLayout.Freshness(0));
            Assert.Equal(0.5f, ChatLayout.Freshness(ChatLayout.HighlightSec / 2), 3);
            Assert.Equal(0f, ChatLayout.Freshness(99));
            Assert.Equal("74%", ChatLayout.ProgressText(0.74f));
            Assert.Equal("100%", ChatLayout.ProgressText(1f));
            Assert.Equal("0%", ChatLayout.ProgressText(0f));
            Assert.Equal("", ChatLayout.ProgressText(-1f));
            Assert.Equal(0, ChatLayout.Dots(0)); Assert.Equal(1, ChatLayout.Dots(0.34)); Assert.Equal(0, ChatLayout.Dots(4.0 / 3.0 + 0.01));
        }
    }
}

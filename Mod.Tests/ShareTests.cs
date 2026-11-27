using System.Collections.Generic;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ShareTests
    {
        static ShareDoc Doc()
        {
            var d = new ShareDoc { Colony = "New Arrivals", Date = "5th of Aprimay, 5501, 14h" };
            d.Lines.Add(new ShareLine { PawnId = 1, Speaker = "Anna", Text = "  Nice day.\r\n" });
            d.Lines.Add(new ShareLine { PawnId = 1, Speaker = "Anna", Text = "I wonder if he knows.", Thought = true });
            d.Lines.Add(new ShareLine { PawnId = 2, Speaker = "Bob", Text = "Is it?" });
            return d;
        }

        [Fact]
        public void TextHasHeaderLinesAndFooter()
        {
            string t = ShareFormat.Text(Doc(), true);
            Assert.Equal("New Arrivals — 5th of Aprimay, 5501, 14h\n\nAnna: Nice day.\nAnna (thinking): I wonder if he knows.\nBob: Is it?\n\n"
                + "— RimWorldLLM: local AI colonist dialogue for RimWorld\n", t);
        }

        [Fact]
        public void FooterOffOmitsFooter()
        {
            string t = ShareFormat.Text(Doc(), false);
            Assert.DoesNotContain("RimWorldLLM", t);
            Assert.EndsWith("Bob: Is it?\n", t);
        }

        [Fact]
        public void TitleAndNarratorLines()
        {
            var d = new ShareDoc { Colony = "C", Date = "D", Title = "Chronicle" };
            d.Lines.Add(new ShareLine { Text = "A raid came." });
            Assert.Equal("C — D\nChronicle\n\nA raid came.\n", ShareFormat.Text(d, false));
        }

        [Fact]
        public void MissingColonyOrDate()
        {
            Assert.Equal("D", ShareFormat.Header(new ShareDoc { Date = "D" }));
            Assert.Equal("C", ShareFormat.Header(new ShareDoc { Colony = "C" }));
            Assert.Equal("", ShareFormat.Header(new ShareDoc()));
        }

        [Fact]
        public void SafeNameStripsPathAndSymbols()
        {
            Assert.Equal("New-Arrivals", ShareFormat.SafeName("New Arrivals"));
            Assert.Equal("a-b", ShareFormat.SafeName("../a/b?"));
            Assert.Equal("share", ShareFormat.SafeName("///"));
            Assert.Equal("share", ShareFormat.SafeName(null));
            Assert.Equal(10, ShareFormat.SafeName("abcdefghijklmnop", 10).Length);
        }

        [Fact]
        public void FileNameShape()
        {
            Assert.Equal("New-Arrivals-5th-of-Aprimay-5501-14h-1.png", ShareFormat.FileName("New Arrivals", "5th of Aprimay, 5501, 14h", 1));
        }

        [Fact]
        public void NextFileNameSkipsTaken()
        {
            var taken = new HashSet<string> { ShareFormat.FileName("C", "D", 1), ShareFormat.FileName("C", "D", 2) };
            Assert.Equal("C-D-3.png", ShareFormat.NextFileName("C", "D", taken.Contains));
            Assert.Equal("C-D-1.png", ShareFormat.NextFileName("C", "D", f => false));
        }

        static float M(string s) { return s.Length * 10f; }

        [Fact]
        public void WrapBreaksOnWords()
        {
            var w = ShareFormat.Wrap("aaa bbb ccc ddd", 70f, M);   // 7 chars
            Assert.Equal(new[] { "aaa bbb", "ccc ddd" }, w.ToArray());
        }

        [Fact]
        public void WrapSplitsLongWordsAndKeepsParagraphs()
        {
            var w = ShareFormat.Wrap("abcdefghij\nxy", 40f, M);
            Assert.Equal(new[] { "abcd", "efgh", "ij", "xy" }, w.ToArray());
            Assert.All(w, s => Assert.True(M(s) <= 40f));
        }

        [Fact]
        public void WrapEmptyIsEmptyOrBlank()
        {
            Assert.Single(ShareFormat.Wrap("", 100f, M));
        }

        [Fact]
        public void ExchangeRangeGroupsSamePairWithinGap()
        {
            int[] p = { 1, 2, 1, 3, 1, 2 };
            int[] l = { 2, 1, 2, 1, 2, 1 };
            int[] t = { 0, 100, 200, 300, 400, 10000 };
            int a, b;
            ShareFormat.ExchangeRange(p, l, t, 1, 2500, out a, out b);
            Assert.Equal(0, a); Assert.Equal(2, b);        // pawn 3 breaks the run
            ShareFormat.ExchangeRange(p, l, t, 4, 2500, out a, out b);
            Assert.Equal(4, a); Assert.Equal(4, b);        // too late for #5 and cut by #4's neighbour
            ShareFormat.ExchangeRange(p, l, t, 5, 2500, out a, out b);
            Assert.Equal(5, a); Assert.Equal(5, b);
        }

        [Fact]
        public void LayoutStacksBlocksAndPlacesFooter()
        {
            var d = Doc();
            var lay = ShareCardLayout.Compute(d, true, 600f, 20f, M);
            Assert.Equal(3, lay.Blocks.Count);
            Assert.Equal(0, lay.Dropped);
            for (int i = 1; i < lay.Blocks.Count; i++)
                Assert.True(lay.Blocks[i].Y >= lay.Blocks[i - 1].Y + lay.Blocks[i - 1].Height);
            Assert.True(lay.FooterY >= lay.Blocks[2].Y + lay.Blocks[2].Height);
            Assert.True(lay.Height > lay.FooterY + 20f);
            foreach (var b in lay.Blocks) foreach (var s in b.Wrapped) Assert.True(M(s) <= lay.TextWidth);
        }

        [Fact]
        public void LayoutWithoutFooterIsShorterAndHasNoFooterY()
        {
            var on = ShareCardLayout.Compute(Doc(), true, 600f, 20f, M);
            var off = ShareCardLayout.Compute(Doc(), false, 600f, 20f, M);
            Assert.Equal(-1f, off.FooterY);
            Assert.True(off.Height < on.Height);
        }

        [Fact]
        public void LayoutDropsLinesOverMaxHeight()
        {
            var d = new ShareDoc { Colony = "C", Date = "D" };
            for (int i = 0; i < 100; i++) d.Lines.Add(new ShareLine { PawnId = 1, Speaker = "A", Text = "line " + i });
            var lay = ShareCardLayout.Compute(d, true, 600f, 20f, M);
            Assert.True(lay.Dropped > 0);
            Assert.Equal(100, lay.Blocks.Count + lay.Dropped);
            Assert.True(lay.Height <= ShareCardLayout.MaxHeight + 1f);
        }

        [Fact]
        public void LayoutKeepsAtLeastOneBlockEvenIfHuge()
        {
            var d = new ShareDoc();
            d.Lines.Add(new ShareLine { Speaker = "A", Text = new string('x', 5000) });
            var lay = ShareCardLayout.Compute(d, true, 600f, 20f, M);
            Assert.Single(lay.Blocks);
        }
    }
}

using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class PawnTextTests
    {
        [Fact]
        public void StripRemovesMarkupAndCollapsesSpace()
        {
            Assert.Equal("Bram is brave.", PawnText.Strip("<color=#ff0000>Bram</color>  is\n <b>brave</b>."));
            Assert.Equal("", PawnText.Strip(null));
            Assert.Equal("", PawnText.Strip("  <i></i> "));
        }

        [Fact]
        public void StripHandlesNestedTags()
        {
            Assert.Equal("a b c", PawnText.Strip("<b>a <i>b <color=#fff>c</color></i></b>"));
            Assert.Equal("x", PawnText.Strip("<<b>b>x"));
        }

        [Fact]
        public void CapCutsAtLastSentenceEnd()
        {
            Assert.Equal("One two. Three four!", PawnText.Cap("One two. Three four! Five six seven eight.", 25));
        }

        [Fact]
        public void CapNoSentenceEndCutsAtWordWithEllipsis()
        {
            string t = PawnText.Cap("alpha beta gamma delta epsilon zeta", 20);
            Assert.True(t.Length <= 20);
            Assert.EndsWith("...", t);
            Assert.Equal("alpha beta gamma...", t);
        }

        [Fact]
        public void CapExactBoundaries()
        {
            string s = "Abcd efgh.";                       // 10 chars
            Assert.Equal(s, PawnText.Cap(s, 10));
            Assert.Equal("Abcd efgh.", PawnText.Cap(s + " Next one.", 10));
            Assert.Equal("Abcd...", PawnText.Cap("Abcd efghijk lmn", 9));
            Assert.Equal("", PawnText.Cap("text", 0));
        }

        [Fact]
        public void CapDoesNotCutInsideAnAbbreviationLikeDotWithoutSpace()
        {
            string t = PawnText.Cap("Version 3.5 is fine and long enough to be cut somewhere here", 30);
            Assert.True(t.Length <= 30);
            Assert.EndsWith("...", t);
        }

        [Fact]
        public void CapEmptyAndNull()
        {
            Assert.Equal("", PawnText.Cap(null, 100));
            Assert.Equal("", PawnText.Cap("   ", 100));
        }

        [Fact]
        public void FirstSentenceTakesOne()
        {
            Assert.Equal("Tough folk.", PawnText.FirstSentence("<b>Tough</b> folk. They endure.", 200));
            Assert.Equal("No end here", PawnText.FirstSentence("No end here", 200));
            Assert.True(PawnText.FirstSentence(new string('a', 50) + " " + new string('b', 50) + ".", 30).Length <= 30);
        }

        [Fact]
        public void CapListCapsCountAndDropsEmptiesAndDuplicates()
        {
            var r = PawnText.CapList(new[] { "a", "", "  ", "b", "a", "c", "d" }, 3, 10);
            Assert.Equal(new[] { "a", "b", "c" }, r.ToArray());
            Assert.Empty(PawnText.CapList(null, 3, 10));
            Assert.Equal("long...", PawnText.CapList(new[] { "long text here" }, 5, 7)[0]);
        }

        [Fact]
        public void AppearanceJoinsPartsAndCaps()
        {
            Assert.Equal("Hair: Bob. Skin: light. Wears: hat, vest, pants",
                PawnText.Appearance("Bob", "light", new[] { "hat", "vest", "pants", "boots" }));
            Assert.Equal("", PawnText.Appearance(null, "", null));
            Assert.Equal("Wears: hat", PawnText.Appearance(null, null, new[] { "hat" }));
            Assert.True(PawnText.Appearance(new string('x', 300), "s", null).Length <= PawnText.AppearanceMax);
        }
    }
}

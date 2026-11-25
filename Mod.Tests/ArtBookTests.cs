using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class ArtBookTests
    {
        [Fact]
        public void SetStoresTrimmedAndCutText()
        {
            var b = new ArtBook();
            b.Set(7, "  A statue.\n", 100);
            Assert.Equal("A statue.", b.Get(7));
            b.Set(8, new string('x', ArtBook.MaxTextChars + 40), 101);
            Assert.Equal(ArtBook.MaxTextChars, b.Get(8).Length);
        }

        [Fact]
        public void EmptyTextStoresNothingAndUnknownIsNull()
        {
            var b = new ArtBook();
            Assert.Equal(0, b.Set(1, "   ", 5));
            Assert.Null(b.Get(1));
            Assert.Null(b.Get(99));
        }

        [Fact]
        public void SetReplacesTheSameThing()
        {
            var b = new ArtBook();
            b.Set(3, "first", 1);
            b.Set(4, "other", 2);
            int n = b.Set(3, "second", 3);
            Assert.Equal(2, n);
            Assert.Equal("second", b.Get(3));
            Assert.Equal(3, b.Entries[1].Tick);
        }

        [Fact]
        public void OldestEntriesFallOutAtTheCap()
        {
            var b = new ArtBook();
            for (int i = 0; i < ArtBook.MaxEntries + 25; i++) b.Set(i, "t" + i, i);
            Assert.Equal(ArtBook.MaxEntries, b.Entries.Count);
            Assert.Null(b.Get(0));
            Assert.Null(b.Get(24));
            Assert.Equal("t25", b.Get(25));
            Assert.Equal("t" + (ArtBook.MaxEntries + 24), b.Get(ArtBook.MaxEntries + 24));
        }

        [Fact]
        public void RemoveAndClear()
        {
            var b = new ArtBook();
            b.Set(1, "a", 1); b.Set(2, "b", 1);
            Assert.True(b.Remove(1));
            Assert.False(b.Remove(1));
            b.Clear();
            Assert.Empty(b.Entries);
        }
    }
}

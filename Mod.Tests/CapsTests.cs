using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class CapsTests
    {
        [Fact]
        public void BoundedSetForgetsOldestAndKeepsCount()
        {
            var s = new BoundedSet(3);
            Assert.True(s.Add("a")); Assert.True(s.Add("b")); Assert.True(s.Add("c")); Assert.False(s.Add("a"));
            Assert.True(s.Add("d"));
            Assert.Equal(3, s.Count);
            Assert.False(s.Contains("a")); Assert.True(s.Contains("d"));
            for (int i = 0; i < 1000; i++) s.Add("x" + i);
            Assert.Equal(3, s.Count);
        }

        [Fact]
        public void BoundedMapKeepsPlaceOnUpdateAndEvictsOldest()
        {
            var m = new BoundedMap<int>(2);
            m[1] = 10; m[2] = 20; m[1] = 11; m[3] = 30;
            int v;
            Assert.False(m.TryGetValue(1, out v));
            Assert.True(m.TryGetValue(3, out v)); Assert.Equal(30, v);
            Assert.Equal(2, m.Count);
        }

        [Fact]
        public void FirstCuesSeenIsBounded()
        {
            var seen = new FirstCues.Seen();
            for (int i = 0; i < FirstCues.Seen.Cap + 500; i++) seen.First(i, "cue");
            Assert.Equal(FirstCues.Seen.Cap, seen.Count);
            Assert.False(seen.First(FirstCues.Seen.Cap + 499, "cue"));
        }

        [Fact]
        public void LedgerCapsOpenEntriesAndPrunesStuckOnes()
        {
            var l = new Ledger();
            for (int i = 0; i < Ledger.MaxOpen + 30; i++) l.OpenEntry("raid", "raid:" + i, i, null, "r");
            Assert.Equal(Ledger.MaxOpen, l.Open.Count);
            Assert.Null(l.Find("raid:0"));
            Assert.NotNull(l.Find("raid:" + (Ledger.MaxOpen + 29)));
            l.OpenEntry("quest", "quest:1", 0, null, "q");
            int n = l.PruneOlderThan(100000, 1000, "raid");
            Assert.True(n > 0);
            Assert.NotNull(l.Find("quest:1"));   // other kinds untouched
            Assert.Equal(0, l.Open.Count(e => e.Kind == "raid"));
        }

        [Fact]
        public void MindBookEvictsLeastRecentlyTouched()
        {
            var b = new MindBook();
            for (int i = 1; i <= MindBook.MaxRecords; i++) b.AddThought(i, i, "t");
            b.AddThought(1, 999, "again");   // pawn 1 is fresh again
            b.AddThought(5000, 1000, "new");
            Assert.Equal(MindBook.MaxRecords, b.Records.Count);
            Assert.True(b.Records.ContainsKey(1));
            Assert.False(b.Records.ContainsKey(2));
            Assert.True(b.Records.ContainsKey(5000));
            for (int i = 6000; i < 7000; i++) b.AddDiary(i, i, "d");
            Assert.Equal(MindBook.MaxRecords, b.Records.Count);
        }
    }
}

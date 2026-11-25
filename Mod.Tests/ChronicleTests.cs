using System.Linq;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class ChronicleTests
    {
        [Fact]
        public void ChronicleKeepsTheNewestHundredTwenty()
        {
            var c = new ChronicleBook();
            int count = 0;
            for (int i = 0; i < 150; i++) count = c.Add(i, "day", "d" + i, i * 100);
            Assert.Equal(120, count);
            Assert.Equal(120, c.Entries.Count);
            Assert.Equal("d30", c.Entries[0].Text);
            Assert.Equal("d149", c.Entries[119].Text);
        }

        [Fact]
        public void ChronicleTrimsAndCutsText()
        {
            var c = new ChronicleBook();
            c.Add(0, "day", "  hello \n", 5);
            c.Add(1, "major", new string('x', ChronicleBook.MaxTextChars + 50), 6);
            Assert.Equal("hello", c.Entries[0].Text);
            Assert.Equal(ChronicleBook.MaxTextChars, c.Entries[1].Text.Length);
            Assert.Equal(6, c.Entries[1].Tick);
        }

        [Fact]
        public void ChronicleSinceDayFiltersAndKeepsOrder()
        {
            var c = new ChronicleBook();
            c.Add(2, "day", "a", 1); c.Add(5, "major", "b", 2); c.Add(5, "day", "c", 3); c.Add(7, "day", "d", 4);
            var all = c.Since(0);
            Assert.Equal(new[] { "a", "b", "c", "d" }, all.Select(e => (string)e["text"]).ToArray());
            var from5 = c.Since(5);
            Assert.Equal(new[] { "b", "c", "d" }, from5.Select(e => (string)e["text"]).ToArray());
            Assert.Equal("major", (string)from5[0]["kind"]);
            Assert.Equal(5, (int)from5[0]["day"]);
            Assert.Equal(2, (int)from5[0]["tick"]);
            Assert.Empty(c.Since(8));
        }

        [Fact]
        public void ChronicleKnownKindsAndClear()
        {
            Assert.True(ChronicleBook.KnownKind("day")); Assert.True(ChronicleBook.KnownKind("major"));
            Assert.False(ChronicleBook.KnownKind("")); Assert.False(ChronicleBook.KnownKind("Day"));
            var c = new ChronicleBook();
            c.Add(0, "day", "a", 0);
            c.Clear();                                   // what a load does before refilling from the save
            Assert.Empty(c.Entries);
            Assert.Equal(1, c.Add(3, "day", "b", 0));
        }

        [Fact]
        public void ChronicleRebuiltFromASaveReplacesTheLiveOne()
        {
            // The game component clears the book and re-adds the saved entries on load: nothing from the later timeline survives.
            var live = new ChronicleBook();
            live.Add(1, "day", "saved", 10);
            var saved = live.Entries.Select(e => (e.Day, e.Kind, e.Text, e.Tick)).ToList();
            live.Add(2, "major", "after the save", 20);
            live.Clear();
            foreach (var e in saved) live.Add(e.Day, e.Kind, e.Text, e.Tick);
            Assert.Single(live.Entries);
            Assert.Equal("saved", live.Entries[0].Text);
        }

        [Fact]
        public void DiariesKeepTheLastSevenAndStaySeparateFromThoughts()
        {
            var m = new MindBook();
            for (int i = 0; i < 10; i++) m.AddDiary(4, i, "diary" + i);
            m.AddThought(4, 99, "a thought");
            var r = m.Records[4];
            Assert.Equal(7, r.Diaries.Count);
            Assert.Equal("diary3", r.Diaries[0].Value);
            Assert.Equal("diary9", r.Diaries[6].Value);
            Assert.Single(r.Thoughts);
            for (int i = 0; i < 30; i++) m.AddThought(4, i, "t" + i);
            Assert.Equal(7, r.Diaries.Count);            // thoughts never push diaries out
            Assert.Equal(20, r.Thoughts.Count);
        }

        [Fact]
        public void DiaryTextIsCutAndKindsAreKnown()
        {
            var m = new MindBook();
            m.AddDiary(1, 0, new string('z', MindBook.MaxDiaryChars + 10));
            Assert.Equal(MindBook.MaxDiaryChars, m.Records[1].Diaries[0].Value.Length);
            Assert.True(MindBook.KnownKind("thought")); Assert.True(MindBook.KnownKind("diary"));
            Assert.False(MindBook.KnownKind("memo"));
        }

        [Fact]
        public void ClearingTheMindBookDropsDiaries()
        {
            var m = new MindBook();
            m.AddDiary(1, 0, "x");
            m.Clear();
            Assert.Empty(m.Records);
        }
    }
}

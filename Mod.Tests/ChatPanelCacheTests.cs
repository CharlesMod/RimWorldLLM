using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ChatPanelCacheTests
    {
        static readonly Func<string, bool> All = w => true;

        class Ctx
        {
            public int Formats, Heights;
            public ChatPanelRows Rows = new ChatPanelRows();
            public ChatLog Log = new ChatLog();
            public Func<ChatEntry, string> Fmt;
            public Func<string, float> H;
            public Ctx()
            {
                Fmt = e => { Formats++; return e.Who + ": " + e.Text; };
                H = t => { Heights++; return 20f + t.Length / 40; };
            }
            public bool Sync(int width = 300, int filters = 31, Func<string, bool> shown = null)
            {
                bool r = Rows.SyncChat(Log.Entries, Log.LastSeq, filters, width, 1, shown ?? All, Fmt, H);
                Rows.MergeIfDirty();
                return r;
            }
        }

        [Fact]
        public void NothingNewMeansNoWork()
        {
            var c = new Ctx();
            for (int i = 0; i < 50; i++) c.Log.Add("narrator", "line " + i, i);
            Assert.True(c.Sync());
            int f = c.Formats, h = c.Heights;
            for (int k = 0; k < 100; k++) Assert.False(c.Sync());
            Assert.Equal(f, c.Formats); Assert.Equal(h, c.Heights);
            Assert.Equal(1, c.Rows.ChatRebuilds);
        }

        [Fact]
        public void NewLineFormatsOnlyTheNewLine()
        {
            var c = new Ctx();
            for (int i = 0; i < 50; i++) c.Log.Add("narrator", "line " + i, i);
            c.Sync();
            int f = c.Formats, h = c.Heights;
            c.Log.Add("player", "hello", 99);
            Assert.True(c.Sync());
            Assert.Equal(f + 1, c.Formats); Assert.Equal(h + 1, c.Heights);
            Assert.Equal("player: hello", c.Rows.Rows.Last().Text);
        }

        [Fact]
        public void WidthChangeRemeasuresButKeepsText()
        {
            var c = new Ctx();
            for (int i = 0; i < 30; i++) c.Log.Add("note", "x" + i, i);
            c.Sync(300);
            int f = c.Formats, h = c.Heights;
            Assert.True(c.Sync(400));
            Assert.Equal(f, c.Formats); Assert.Equal(h + 30, c.Heights);
        }

        [Fact]
        public void FiltersHideRowsAndRowsStayOldestFirst()
        {
            var c = new Ctx();
            c.Log.Add("player", "a", 1); c.Log.Add("camera", "b", 2); c.Log.Add("comment", "c", 3); c.Log.Add("narrator", "d", 4);
            Func<string, bool> noStory = w => ChatKinds.Group(w) != ChatKinds.Story;
            c.Sync(300, 15, noStory);
            Assert.Equal(new[] { "a", "d" }, c.Rows.Rows.Select(r => r.Text.Substring(r.Text.IndexOf(' ') + 1)).ToArray());
            Assert.Equal(c.Rows.Rows.Count, c.Rows.Heights.Count);
        }

        [Fact]
        public void RowsCapAndCacheStayBoundedOverALongSession()
        {
            var c = new Ctx();
            for (int i = 0; i < 5000; i++)
            {
                c.Log.Add("narrator", "line " + i, i);
                c.Sync();
            }
            Assert.True(c.Rows.Rows.Count <= ChatPanelRows.MaxRows);
            Assert.True(c.Rows.CacheCount <= 2 * ChatLog.Max + 1);
            Assert.Equal(5000, c.Formats);   // each line formatted exactly once
        }

        [Fact]
        public void LiveRowsRebuildOnlyOnVersionOrPhaseChange()
        {
            double now = 0;
            var live = new ChatLive(() => now);
            var c = new Ctx();
            c.Log.Add("narrator", "hi", 1);
            var lines = new List<LiveLine>();
            int seen = -1, makes = 0;
            Func<LiveLine, PanelRow> make = l => { makes++; return new PanelRow { Text = "live", Who = "narrator" }; };
            c.Sync();
            live.Refresh(ref seen, lines);
            c.Rows.SyncLive(seen, 0, 31, 300, 1, lines, All, make, c.H);
            Assert.Equal(0, makes);
            Assert.Single(c.Rows.Rows);

            string id = live.Begin("narrator");
            Assert.True(live.Refresh(ref seen, lines));
            Assert.True(c.Rows.SyncLive(seen, 0, 31, 300, 1, lines, All, make, c.H));
            Assert.Equal(1, makes); Assert.Equal(2, c.Rows.Rows.Count); Assert.True(c.Rows.Rows.Last().Live);
            for (int k = 0; k < 50; k++)
            {
                Assert.False(live.Refresh(ref seen, lines));
                Assert.False(c.Rows.SyncLive(seen, 0, 31, 300, 1, lines, All, make, c.H));
            }
            Assert.Equal(1, makes);
            Assert.True(c.Rows.SyncLive(seen, 1, 31, 300, 1, lines, All, make, c.H));   // dots phase moved
            Assert.Equal(2, makes);
            live.Partial(id, "abc");
            now += 1;
            Assert.True(live.Refresh(ref seen, lines));
            Assert.Equal("abc", lines[0].Text);
        }

        [Fact]
        public void LiveTimeoutBumpsTheVersionOnce()
        {
            double now = 0;
            var live = new ChatLive(() => now);
            var lines = new List<LiveLine>(); int seen = -1;
            live.Begin("narrator");
            live.Refresh(ref seen, lines);
            Assert.Single(lines);
            now += ChatLive.TimeoutSec + 1;
            Assert.True(live.Refresh(ref seen, lines));
            Assert.Empty(lines);
            Assert.False(live.Refresh(ref seen, lines));
        }

        [Fact]
        public void LayoutIsComputedOnlyWhenAnInputChanges()
        {
            var cache = new ChatLayoutCache();
            var i = new ChatLayout.Input { ScreenW = 1920, ScreenH = 1080, Width = 400, Lines = 8, LineH = 22 };
            var a = cache.Get(i);
            for (int k = 0; k < 100; k++) cache.Get(i);
            Assert.Equal(1, cache.Computes);
            i.ScreenW = 1280;
            var b = cache.Get(i);
            Assert.Equal(2, cache.Computes);
            cache.Invalidate(); cache.Get(i);
            Assert.Equal(3, cache.Computes);
        }

        [Fact]
        public void VisibleIntoMatchesVisibleAndReusesTheList()
        {
            var hs = new List<float> { 22, 44, 22, 22, 66 };
            float s1 = 10, s2 = 10;
            var a = ChatLayout.Visible(hs, 80, ref s1);
            var into = new List<KeyValuePair<int, float>> { new KeyValuePair<int, float>(9, 9) };
            ChatLayout.VisibleInto(hs, 80, ref s2, into);
            Assert.Equal(a, into); Assert.Equal(s1, s2);
        }

        [Fact]
        public void CameraAndCommentFormats()
        {
            Assert.Equal("Camera: a raid arrives", ChatRowFormat.Camera("Camera: a raid arrives", "Camera:"));
            Assert.Equal("Camera: a raid arrives", ChatRowFormat.Camera("a raid arrives", "Camera:"));
            Assert.Equal("<i>Narrator: the colony sleeps</i>", ChatRowFormat.Comment("the colony sleeps", "Narrator:"));
            Assert.Equal("<i>Narrator: hmm</i>", ChatRowFormat.Comment("Narrator: hmm", "Narrator:"));
            Assert.Equal(ChatKinds.Story, ChatKinds.Group("camera"));
            Assert.Equal(ChatKinds.Story, ChatKinds.Group("comment"));
        }
    }
}

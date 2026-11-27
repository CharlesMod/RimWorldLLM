using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ChatLiveTests
    {
        double now = 100;
        ChatLive L() { return new ChatLive(() => now); }

        // ------------------------------------------------------------ the status machine
        [Fact]
        public void ThinkingThenTypingThenFinal()
        {
            var l = L(); var log = new ChatLog();
            Assert.Null(l.Status("s1", "narrator", 0, null, "thinking"));
            var s = l.Snapshot().Single();
            Assert.Equal("narrator", s.Who); Assert.False(s.Typing); Assert.Equal("", s.Text); Assert.True(s.Progress < 0);

            Assert.True(l.Partial("s1", "Hel"));
            s = l.Snapshot().Single();
            Assert.True(s.Typing); Assert.Equal("Hel", s.Text);

            l.Partial("s1", "Hello th");
            now += 1;
            Assert.Equal("Hello th", l.Snapshot().Single().Text);

            // the final POST /chat path: line added, stream ended
            log.Add("narrator", "Hello there.", 5);
            Assert.True(l.End("s1"));
            Assert.Empty(l.Snapshot());
            Assert.Single(log.Entries);
        }

        [Fact]
        public void TypingWithoutPartialShowsEmptyTypingLine()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            Assert.Null(l.Status("s1", "narrator", 0, null, "typing"));
            var s = l.Snapshot().Single();
            Assert.True(s.Typing); Assert.Equal("", s.Text);
        }

        [Fact]
        public void ProgressShownWhileThinkingOnly()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking", 0.74f);
            Assert.Equal(0.74f, l.Snapshot().Single().Progress, 3);
            l.Status("s1", "narrator", 0, null, "thinking");          // no value: unknown again
            Assert.True(l.Snapshot().Single().Progress < 0);
            Assert.True(l.Progress("s1", 1.5f));                       // in-process, clamped
            Assert.Equal(1f, l.Snapshot().Single().Progress);
            l.Partial("s1", "a");
            Assert.True(l.Snapshot().Single().Progress < 0);
            Assert.False(l.Progress("s1", 0.5f));                     // typing: no more progress
        }

        [Fact]
        public void TimesOutWithoutUpdates()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            now += ChatLive.TimeoutSec - 1;
            Assert.Single(l.Snapshot());
            l.Partial("s1", "still going");                           // an update resets the clock
            now += ChatLive.TimeoutSec - 1;
            Assert.Single(l.Snapshot());
            now += 2;
            Assert.Empty(l.Snapshot());
            Assert.False(l.Partial("s1", "late"));                    // a timed-out stream is ended
            Assert.Null(l.Status("s1", "narrator", 0, null, "thinking"));
            Assert.Empty(l.Snapshot());
        }

        [Fact]
        public void NewStreamReplacesOldForSameSpeaker()
        {
            var l = L();
            l.Status("a", "narrator", 0, null, "thinking");
            l.Status("c1", "colonist", 7, "Ana", "thinking");
            l.Status("b", "narrator", 0, null, "thinking");
            var snap = l.Snapshot();
            Assert.Equal(new[] { "c1", "b" }, snap.Select(x => x.StreamId).ToArray());
            Assert.False(l.Partial("a", "old"));                      // the replaced stream is ended
            Assert.False(l.IsLive("a"));
            l.Status("c2", "colonist", 8, "Bo", "thinking");          // another colonist is another speaker
            Assert.Equal(3, l.Count);
        }

        [Fact]
        public void PartialAfterFinalIgnored()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            l.End("s1");
            Assert.False(l.Partial("s1", "too late"));
            Assert.Null(l.Status("s1", "narrator", 0, null, "typing"));   // accepted but ignored
            Assert.Empty(l.Snapshot());
            Assert.False(l.End("s1"));
        }

        [Fact]
        public void IdleEndsWithoutLine()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            Assert.Null(l.Status("s1", null, 0, null, "idle"));         // idle needs no speaker
            Assert.Empty(l.Snapshot());
            Assert.Null(l.Status("never", null, 0, null, "idle"));      // idle for an unknown stream is fine
        }

        [Fact]
        public void PartialIsWholeTextAndIdempotent()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            l.Partial("s1", "Hello");
            l.Partial("s1", "Hello");
            Assert.Equal("Hello", l.Snapshot().Single().Text);
        }

        [Fact]
        public void DrawnTextIsRateLimited()
        {
            var l = L();
            l.Status("s1", "narrator", 0, null, "thinking");
            l.Partial("s1", "a");
            Assert.Equal("a", l.Snapshot().Single().Text);
            l.Partial("s1", "ab");
            Assert.Equal("a", l.Snapshot().Single().Text);           // same instant: still the old text
            now += ChatLive.DrawIntervalSec / 2;
            l.Partial("s1", "abc");
            Assert.Equal("a", l.Snapshot().Single().Text);
            now += ChatLive.DrawIntervalSec;
            Assert.Equal("abc", l.Snapshot().Single().Text);         // the latest text, never a stale middle one
        }

        [Fact]
        public void InProcessBeginEndWithFinalQueuesLine()
        {
            var l = L(); var log = new ChatLog();
            string id = l.Begin("narrator");
            Assert.True(ChatLive.ValidId(id));
            Assert.True(l.Typing(id));
            Assert.True(l.Partial(id, "The raid "));
            Assert.True(l.End(id, "  The raid is over.  "));
            var s = l.Snapshot().Single();                            // kept until the game thread pumps, so nothing blinks
            Assert.Equal("The raid is over.", s.Text);
            Assert.False(l.Partial(id, "x"));
            Assert.Equal(1, l.Pump(log, 42));
            Assert.Empty(l.Snapshot());
            var e = log.Entries.Single();
            Assert.Equal("narrator", e.Who); Assert.Equal("The raid is over.", e.Text); Assert.Equal(42, e.Tick);
            Assert.Equal(0, l.Pump(log, 43));
        }

        [Fact]
        public void InProcessColonistFinalIsColonistLine()
        {
            var l = L(); var log = new ChatLog();
            string id = l.Begin("colonist", 7, "Ana");
            l.End(id, "On my way.");
            l.Pump(log, 1);
            var e = log.Entries.Single();
            Assert.Equal("colonist", e.Who); Assert.Equal(7, e.To); Assert.Equal("Ana", e.Name);
        }

        [Fact]
        public void BeginRejectsBadSpeaker()
        {
            var l = L();
            Assert.Throws<ArgumentException>(() => l.Begin("player"));
            Assert.Throws<ArgumentException>(() => l.Begin("colonist", 0));
            Assert.Throws<ArgumentException>(() => l.Begin("narrator", 5));
        }

        [Fact]
        public void CancelDropsLine()
        {
            var l = L(); var log = new ChatLog();
            string id = l.Begin("narrator");
            Assert.True(l.Cancel(id));
            Assert.Empty(l.Snapshot());
            Assert.Equal(0, l.Pump(log, 1));
        }

        [Fact]
        public void EndedMemoryIsBounded()
        {
            var l = L();
            for (int i = 0; i < ChatLive.MaxEnded + 10; i++) { l.Status("s" + i, "narrator", 0, null, "thinking"); l.End("s" + i); }
            // the oldest ended id fell out of memory; a reused id is a new stream then (documented: ids are unique per stream)
            Assert.Null(l.Status("s0", "narrator", 0, null, "thinking"));
            Assert.True(l.IsLive("s0"));
            Assert.Null(l.Status("s" + (ChatLive.MaxEnded + 9), "narrator", 0, null, "thinking"));
            Assert.False(l.IsLive("s" + (ChatLive.MaxEnded + 9)));
        }

        [Fact]
        public async System.Threading.Tasks.Task ThreadSafeUnderConcurrentWriters()
        {
            var l = new ChatLive();
            var log = new ChatLog();
            var tasks = Enumerable.Range(0, 8).Select(k => System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    string id = l.Begin("colonist", k + 1, "c" + k);
                    l.Partial(id, "x" + i);
                    l.Snapshot();
                    l.End(id, i % 2 == 0 ? "done" : null);
                }
            })).ToArray();
            await System.Threading.Tasks.Task.WhenAll(tasks);
            l.Pump(log, 1);
            Assert.Equal(0, l.Count);
            Assert.Equal(300, log.Entries.Count);   // 800 finals, log capped at ChatLog.Max
        }
    }

    public class ChatLiveRouteTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        int op;

        public ChatLiveRouteTests() { VivariumRoutes.Register(H.Router, () => V, () => null); }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "op" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        ApiResult PostRaw(string path, string json)
        {
            var j = JObject.Parse(json);
            j["op_id"] = "op" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void BadRequest(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal(400, r.Status);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }

        [Fact]
        public void FullStreamOverRoutes()
        {
            var r = Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = "s-1", progress = 0.25 });
            Assert.True(r.Ok); Assert.True((bool)r.Body["live"]); Assert.Equal("thinking", (string)r.Body["state"]);
            Assert.Equal(0.25f, V.Live.Snapshot().Single().Progress, 3);

            r = Post("/chat/status", new { who = "narrator", state = "typing", stream_id = "s-1" });
            Assert.True(r.Ok);
            r = Post("/chat/partial", new { stream_id = "s-1", text = "Wind is" });
            Assert.True(r.Ok); Assert.True((bool)r.Body["accepted"]);
            Assert.Equal("Wind is", V.Live.Snapshot().Single().Text);

            r = Post("/chat", new { who = "narrator", text = "Wind is rising.", stream_id = "s-1" });
            Assert.True(r.Ok);
            Assert.Empty(V.Live.Snapshot());
            Assert.Equal("Wind is rising.", V.Chat.Entries.Last().Text);

            r = Post("/chat/partial", new { stream_id = "s-1", text = "late" });
            Assert.True(r.Ok); Assert.False((bool)r.Body["accepted"]);
            Assert.Empty(V.Live.Snapshot());
        }

        [Fact]
        public void ColonistThinking()
        {
            var r = Post("/chat/status", new { who = "colonist", to = 12, name = "Ana", state = "thinking", stream_id = "c" });
            Assert.True(r.Ok);
            var s = V.Live.Snapshot().Single();
            Assert.Equal(12, s.To); Assert.Equal("Ana", s.Name);
            Assert.True(Post("/chat/status", new { state = "idle", stream_id = "c" }).Ok);
            Assert.Empty(V.Live.Snapshot());
        }

        [Fact]
        public void StatusValidation()
        {
            BadRequest(Post("/chat/status", new { who = "narrator", state = "thinking" }), "stream_id");
            BadRequest(Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = "has space" }), "stream_id");
            BadRequest(Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = new string('x', 65) }), "stream_id");
            BadRequest(Post("/chat/status", new { who = "narrator", state = "sleeping", stream_id = "a" }), "state");
            BadRequest(Post("/chat/status", new { who = "player", state = "thinking", stream_id = "a" }), "who");
            BadRequest(Post("/chat/status", new { who = "colonist", state = "thinking", stream_id = "a" }), "needs to");
            BadRequest(Post("/chat/status", new { who = "narrator", to = 3, state = "thinking", stream_id = "a" }), "to is only");
            BadRequest(Post("/chat/status", new { who = "colonist", to = "3", state = "thinking", stream_id = "a" }), "to must be");
            BadRequest(Post("/chat/status", new { who = "colonist", to = -1, state = "thinking", stream_id = "a" }), "to must be");
            BadRequest(Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = "a", progress = 1.5 }), "progress");
            BadRequest(Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = "a", progress = "half" }), "progress");
            Assert.Equal(0, V.Live.Count);
            Assert.True(PostRaw("/chat/status", @"{""who"":""narrator"",""state"":""thinking"",""stream_id"":""a"",""progress"":null,""to"":null}").Ok);
        }

        [Fact]
        public void PartialValidation()
        {
            BadRequest(Post("/chat/partial", new { text = "x" }), "stream_id");
            BadRequest(Post("/chat/partial", new { stream_id = "a" }), "text");
            BadRequest(Post("/chat/partial", new { stream_id = "a", text = new string('x', ChatLog.MaxText + 1) }), "at most");
            var r = Post("/chat/partial", new { stream_id = "unknown", text = "x" });
            Assert.True(r.Ok); Assert.False((bool)r.Body["accepted"]);
        }

        [Fact]
        public void ChatPostStreamIdValidation()
        {
            BadRequest(Post("/chat", new { who = "narrator", text = "x", stream_id = "bad id" }), "stream_id");
            BadRequest(Post("/chat", new { who = "narrator", text = "x", stream_id = 5 }), "stream_id");
            Assert.Empty(V.Chat.Entries);
            Assert.True(Post("/chat", new { who = "narrator", text = "x", stream_id = "never-started" }).Ok);   // unknown stream: the line still posts
            Assert.True(PostRaw("/chat", @"{""who"":""narrator"",""text"":""y"",""stream_id"":null}").Ok);
            Assert.Equal(2, V.Chat.Entries.Count);
        }

        [Fact]
        public void StatusRoutesWorkWithoutAGame()
        {
            var j = new JObject { ["who"] = "narrator", ["state"] = "thinking", ["stream_id"] = "m", ["op_id"] = "op-menu" };
            var r = H.Call("POST", "/chat/status", j.ToString(), GameState.Menu);
            Assert.True(r.Ok);
        }

        [Fact]
        public void LiveIsNotSavedButLogIs()
        {
            Post("/chat", new { who = "narrator", text = "kept" });
            Post("/chat/status", new { who = "narrator", state = "thinking", stream_id = "s" });
            var saved = V.Export();
            Assert.DoesNotContain("\"s\"", saved.ToString());
            Assert.Null(saved["live"]);
            var w = new Vivarium();
            w.Live.Status("other", "narrator", 0, null, "thinking");
            w.Import(saved);
            Assert.Equal(0, w.Live.Count);                          // a load clears live statuses
            Assert.Equal("kept", w.Chat.Entries.Single().Text);
            V.Clear();
            Assert.Equal(0, V.Live.Count);
        }
    }
}

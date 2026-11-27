// NEW (no Python counterpart): the steward's live chat line through an IChatLive sink and the streaming backend path.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    internal sealed class FakeLive : IChatLive
    {
        public readonly List<string> Log = new List<string>();
        public bool BeginThrows;
        private int _n;
        public string Begin(string who, int? pawnId, string name)
        {
            if (BeginThrows) throw new InvalidOperationException("no live");
            string id = "live" + (++_n);
            lock (Log) Log.Add("begin:" + who + ":" + (pawnId.HasValue ? pawnId.ToString() : "-") + ":" + name);
            return id;
        }
        public void Progress(string id, double p) { lock (Log) Log.Add(p < 0 ? "refresh" : "progress:" + Math.Round(p, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public void Typing(string id) { lock (Log) Log.Add("typing"); }
        public void Partial(string id, string t) { lock (Log) Log.Add("partial:" + t); }
        public void End(string id, string t) { lock (Log) Log.Add("end:" + id + ":" + t); }
        public void Cancel(string id) { lock (Log) Log.Add("cancel:" + id); }
    }

    /// <summary>A streaming tool backend: each call runs the next scripted step (stream events then the reply, or a throw).</summary>
    internal sealed class StreamFake : IStreamingBackend
    {
        public readonly List<Action<ILlmStreamSink>> Steps = new List<Action<ILlmStreamSink>>();
        public readonly List<string> Replies = new List<string>();
        public Exception Throw;
        public TimeSpan Delay = TimeSpan.Zero;
        public int Plain, Streamed;
        public List<bool> SawSink = new List<bool>();
        private int _i;

        public Task<Reply> ChatAsync(IReadOnlyList<JObject> m, string g, JArray t, string tc, int mt, double temp, double to, int? slot)
        { Plain++; return Run(null); }
        public Task<Reply> ChatAsync(IReadOnlyList<JObject> m, string g, JArray t, string tc, int mt, double temp, double to, int? slot, CallOptions o)
        { Streamed++; return Run(o?.Sink); }
        private async Task<Reply> Run(ILlmStreamSink sink)
        {
            SawSink.Add(sink != null);
            int i = _i++;
            if (sink != null && i < Steps.Count) Steps[i](sink);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay).ConfigureAwait(false);
            if (Throw != null) throw Throw;
            return new Reply { Text = Replies[Math.Min(i, Replies.Count - 1)] };
        }
    }

    [Collection("StewardEnv")]
    public class StewardStreamTests
    {
        private static async Task<(JobsWorld w, StreamFake b, FakeLive live)> Boot(string reply, bool live = true)
        {
            Rig.NoEnv();
            var w = new JobsWorld();
            var (_, st, _) = await Rig.Boot(w);
            var b = new StreamFake();
            b.Replies.Add(reply);
            w.ToolBackend = b; w.ToolMode = "grammar";
            var fl = new FakeLive();
            if (live) st.Live = fl;
            w.Fb.Playing = false;
            Rig.Chat(w, "status?");
            return (w, b, fl);
        }

        [Fact]
        public async Task FullFlowBeginProgressTypingPartialEnd()
        {
            var (w, b, live) = await Boot(Rig.Final("All quiet."));
            using (w)
            {
                b.Steps.Add(s =>
                {
                    s.OnEvent(StreamEvent.Sent());
                    s.OnEvent(StreamEvent.Progress(50, 100, 0, 1));
                    s.OnEvent(StreamEvent.FirstToken());
                    s.OnEvent(StreamEvent.Partial("{\"final\":\"All"));
                    s.OnEvent(StreamEvent.Done("{\"final\":\"All quiet.\"}", null));
                });
                await Rig.Cycle(w);
                Assert.Equal("begin:narrator:-:Narrator", live.Log[0]);
                Assert.Contains("progress:0.5", live.Log);
                Assert.Contains("typing", live.Log);
                Assert.Contains("partial:All", live.Log);
                Assert.Equal("end:live1:All quiet.", live.Log.Last());
                Assert.DoesNotContain(live.Log, l => l.StartsWith("cancel"));
                Assert.Equal(new[] { "live1" }, w.Fb.ChatStreams.ToArray());       // the line is posted with the stream id
                Assert.Equal(new[] { "All quiet." }, Rig.Lines(w, "narrator"));
                Assert.Empty(w.Fb.ChatStatus);                                       // the sink replaces POST /chat/status
                Assert.Equal(1, b.Streamed); Assert.Equal(0, b.Plain);
            }
        }

        [Fact]
        public async Task QueuedStaysThinkingWithNoProgress()
        {
            var (w, b, live) = await Boot(Rig.Final("Hi."));
            using (w)
            {
                b.Steps.Add(s => s.OnEvent(StreamEvent.Queued()));
                await Rig.Cycle(w);
                Assert.Equal("begin:narrator:-:Narrator", live.Log[0]);
                Assert.DoesNotContain(live.Log, l => l.StartsWith("progress") || l == "typing");
                Assert.Equal("end:live1:Hi.", live.Log.Last());
            }
        }

        [Fact]
        public async Task PartialsAreThrottledAndTheLastOneFlushedOnDone()
        {
            var (w, b, live) = await Boot(Rig.Final("One two three four."));
            using (w)
            {
                w.Real = 100.0;                                  // the clock stands still: only the first partial gets through
                b.Steps.Add(s =>
                {
                    s.OnEvent(StreamEvent.FirstToken());
                    s.OnEvent(StreamEvent.Partial("{\"final\":\"One"));
                    s.OnEvent(StreamEvent.Partial("{\"final\":\"One two"));
                    s.OnEvent(StreamEvent.Partial("{\"final\":\"One two three"));
                    s.OnEvent(StreamEvent.Done("{\"final\":\"One two three four.\"}", null));
                });
                await Rig.Cycle(w);
                var parts = live.Log.Where(l => l.StartsWith("partial:")).ToList();
                Assert.Equal(new[] { "partial:One", "partial:One two three" }, parts.ToArray());
            }
        }

        [Fact]
        public async Task PartialsRespectTheMinimumGapWhenTheClockMoves()
        {
            var (w, b, live) = await Boot(Rig.Final("a b c."));
            using (w)
            {
                b.Steps.Add(s =>
                {
                    s.OnEvent(StreamEvent.FirstToken());
                    w.Real = 1.0; s.OnEvent(StreamEvent.Partial("{\"final\":\"a"));
                    w.Real = 1.05; s.OnEvent(StreamEvent.Partial("{\"final\":\"a b"));       // under 1/8 s since the last: held back
                    w.Real = 1.2; s.OnEvent(StreamEvent.Partial("{\"final\":\"a b c"));
                    s.OnEvent(StreamEvent.Done("{\"final\":\"a b c.\"}", null));
                });
                await Rig.Cycle(w);
                Assert.Equal(new[] { "partial:a", "partial:a b c" }, live.Log.Where(l => l.StartsWith("partial:")).ToArray());
            }
        }

        [Fact]
        public async Task ThinkingIsRefreshedWhileTheModelWorks()
        {
            var (w, b, live) = await Boot(Rig.Final("Done waiting."));
            using (w)
            {
                var st = w.Steward;
                st.LiveRefresh = TimeSpan.FromMilliseconds(20);
                b.Delay = TimeSpan.FromMilliseconds(250);
                b.Steps.Add(s => s.OnEvent(StreamEvent.Sent()));
                await Rig.Cycle(w);
                int n; lock (live.Log) n = live.Log.Count(l => l == "refresh" || l.StartsWith("progress"));
                Assert.True(n >= 2, "refreshes: " + n);
                Assert.Equal("end:live1:Done waiting.", live.Log.Last());
            }
        }

        [Fact]
        public void TheDefaultRefreshIsWellInsideTheNinetySecondExpiry()
        {
            Assert.True(new Steward(new JobsWorld()).LiveRefresh <= TimeSpan.FromSeconds(60));
        }

        [Fact]
        public async Task YieldedEndsWithThePausedNote()
        {
            var (w, b, live) = await Boot(Rig.Final("never"));
            using (w)
            {
                b.Steps.Add(s => s.OnEvent(StreamEvent.Sent()));
                b.Throw = new BackendAway("a game is running");
                try { await Rig.Cycle(w); } catch (Exception) { }
                Assert.Equal("end:live1:" + Steward.PausedNote, live.Log.Last());
                // as in Python, a player who asked and got nothing hears the apology (posted with no stream id: the live line already ended)
                Assert.Equal(new[] { "I couldn't think that through just now. Ask me again in a moment." }, Rig.Lines(w, "narrator"));
                Assert.Equal(new string[] { null }, w.Fb.ChatStreams.ToArray());
            }
        }

        [Fact]
        public async Task NoFinalLineCancelsTheLiveLine()
        {
            var (w, b, live) = await Boot(Rig.Done);
            using (w)
            {
                b.Steps.Add(s => s.OnEvent(StreamEvent.Sent()));
                await Rig.Cycle(w);
                Assert.Equal("cancel:live1", live.Log.Last());
                Assert.DoesNotContain(live.Log, l => l.StartsWith("end:"));
            }
        }

        [Fact]
        public async Task BackendErrorCancelsTheLiveLine()
        {
            var (w, b, live) = await Boot(Rig.Done);
            using (w)
            {
                b.Throw = new InvalidOperationException("model down");
                try { await Rig.Cycle(w); } catch (Exception) { }
                Assert.Equal("cancel:live1", live.Log.Last());
            }
        }

        [Fact]
        public async Task ABeginThatThrowsIsHarmlessAndTheLineIsStillPosted()
        {
            var (w, b, live) = await Boot(Rig.Final("Fine."));
            using (w)
            {
                live.BeginThrows = true;
                b.Steps.Add(s => { s.OnEvent(StreamEvent.Sent()); s.OnEvent(StreamEvent.FirstToken()); });
                await Rig.Cycle(w);
                Assert.Equal(new[] { "Fine." }, Rig.Lines(w, "narrator"));
                Assert.Equal(new string[] { null }, w.Fb.ChatStreams.ToArray());
            }
        }

        [Fact]
        public async Task NoSinkKeepsTheOrdinaryRequestAndChatStatus()
        {
            var (w, b, live) = await Boot(Rig.Final("Plain."), live: false);
            using (w)
            {
                await Rig.Cycle(w);
                Assert.Empty(live.Log);
                Assert.Equal(0, b.Streamed); Assert.Equal(1, b.Plain);              // the non-streaming call, no CallOptions
                Assert.Equal(new[] { "thinking", "idle" }, w.Fb.ChatStatus.Select(s => (string)s["state"]).ToArray());
                Assert.Equal(new string[] { null }, w.Fb.ChatStreams.ToArray());
                Assert.Equal(new[] { "Plain." }, Rig.Lines(w, "narrator"));
            }
        }

        [Fact]
        public async Task EachLaterStepGoesBackToThinkingWithoutAFigure()
        {
            var (w, b, live) = await Boot(Rig.Final("Later."));
            using (w)
            {
                b.Replies.Clear();
                b.Replies.Add(Rig.Call("set_time", new { pause = false, why = "go" }));
                b.Replies.Add(Rig.Final("Later."));
                b.Steps.Add(s => { s.OnEvent(StreamEvent.Sent()); s.OnEvent(StreamEvent.FirstToken()); });
                b.Steps.Add(s => s.OnEvent(StreamEvent.Sent()));
                await Rig.Cycle(w);
                Assert.Equal(2, b.Streamed);
                Assert.Single(live.Log, l => l.StartsWith("begin"));                 // one live line per cycle
                int t = live.Log.IndexOf("typing");
                if (t >= 0) Assert.Equal("refresh", live.Log[t + 1]);
                Assert.Equal("end:live1:Later.", live.Log.Last());
            }
        }
    }
}

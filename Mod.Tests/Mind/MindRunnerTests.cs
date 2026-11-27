// MindRunner (core/cli.py _run as a library): starts, loads the world, never throws out, stops cleanly; the Director built by it streams chat
// through a BridgeChatLive.
using System;
using System.Threading;
using System.Threading.Tasks;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class MindRunnerTests
    {
        static MindConfig Cfg(TempDir t)
        {
            var c = new MindConfig { DataDir = t.Path, ThoughtEnabled = false };
            c.Llm.BaseUrl = "http://127.0.0.1:9";       // nothing listens: no line is ever made
            return c;
        }

        static async Task<bool> Until(Func<bool> f, int ms = 5000)
        {
            for (int i = 0; i < ms / 20; i++) { if (f()) return true; await Task.Delay(20); }
            return f();
        }

        sealed class AlwaysFails : IBridgeTransport
        {
            public int Calls;
            public Task<BridgeReply> SendAsync(BridgeRequest r, CancellationToken ct = default) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("boom"); }
        }

        [Fact]
        public async Task RunsLoadsTheWorldAndStopsCleanly()
        {
            using var t = new TempDir();
            var fb = new FakeBridge("tok");
            fb.AddPawn(1, "Ana"); fb.AddPawn(2, "Bram");
            using var r = new MindRunner(Cfg(t), new RimWorldAdapter(), fb, "tok");
            r.Start();
            Assert.True(await Until(() => r.Director != null && r.Director.Started));
            Assert.True(await Until(() => r.Director.Minds.Count == 2));
            Assert.IsType<BridgeChatLive>(r.Director.ChatLive);
            await r.StopAsync();
            Assert.False(r.Running);
            Assert.Null(r.LastError);
        }

        [Fact]
        public async Task AFailureIsLoggedOnceAndRetriedNeverThrown()
        {
            using var t = new TempDir();
            var bad = new AlwaysFails();
            using var r = new MindRunner(Cfg(t), new RimWorldAdapter(), bad, "tok", backoffS: 0.01);
            r.Start();
            Assert.True(await Until(() => r.Restarts >= 3));
            Assert.Contains("boom", r.LastError);
            Assert.True(r.Running);
            await r.StopAsync();
            Assert.False(r.Running);
        }

        [Fact]
        public void ToolBackendIsNullWhenWantsAndStewardAreOff()
        {
            using var t = new TempDir();
            var c = Cfg(t); c.Wants = false; c.Steward = false;
            Assert.Null(MindRunner.MakeToolBackend(c));
            c.Wants = true;
            using (var b = MindRunner.MakeToolBackend(c)) Assert.NotNull(b);
        }

        [Fact]
        public void WantsAreWiredOnByDefaultAtTheAgentsEngine()
        {
            using var t = new TempDir();
            var c = Cfg(t); c.Llm.BaseUrl = "http://engine:4480";
            using var b = MindRunner.MakeToolBackend(c);
            Assert.NotNull(b);
            Assert.Equal("http://engine:4480/v1", b.Base);
        }

        [Fact]
        public void StewardKeepsTheBackendWhenWantsAreOff()
        {
            using var t = new TempDir();
            var c = Cfg(t); c.Wants = false;
            using var b = MindRunner.MakeToolBackend(c);
            Assert.NotNull(b);
        }
    }
}

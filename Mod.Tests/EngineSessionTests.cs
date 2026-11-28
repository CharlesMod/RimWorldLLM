using System.Collections.Generic;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class EngineSessionTests
    {
        static (int, string) Route(string m, string p) { return (200, "{\"phase\":\"ready\",\"model\":\"m\"}"); }

        sealed class Rig : System.IDisposable
        {
            public bool Local = true;
            public int Hits;
            public readonly FakeEngine Engine;
            public readonly EngineSession Session;
            public Rig()
            {
                Engine = new FakeEngine((m, p) => { System.Threading.Interlocked.Increment(ref Hits); return Route(m, p); });
                Session = new EngineSession(() => Local, () => Task.FromResult(Engine.Client));
            }
            public void Dispose() { Engine.Dispose(); }
        }

        [Fact]
        public async Task LocalToOwnClearsClientAndStopsPolling()
        {
            using (var r = new Rig())
            {
                await r.Session.PollAsync(false);   // discovers and reads status
                Assert.NotNull(r.Session.Client);
                Assert.NotNull(r.Session.Status);
                r.Local = false;
                r.Session.SourceChanged();
                Assert.Null(r.Session.Client);
                Assert.Null(r.Session.Status);
                Assert.Null(r.Session.Plan);
                Assert.Null(r.Session.Hardware);
                int hits = r.Hits;
                await r.Session.PollAsync(false);
                await r.Session.FetchStatusAsync();
                Assert.Equal(hits, r.Hits);
                Assert.Null(r.Session.Client);
            }
        }

        [Fact]
        public async Task LocalOnlyActionsRefuseInOwnMode()
        {
            using (var r = new Rig())
            {
                await r.Session.PollAsync(false);
                r.Local = false;
                r.Session.SourceChanged();
                int hits = r.Hits;
                await r.Session.Act(c => c.InstallAsync("x"));
                Assert.Equal("engine not running", r.Session.ActionError);
                Assert.False(r.Session.Attach(r.Engine.Client));   // a late launch result is refused too
                await r.Session.RefreshHardwareAsync();
                Assert.Equal(hits, r.Hits);
                Assert.Null(r.Session.Client);
            }
        }

        [Fact]
        public async Task OwnToLocalReattaches()
        {
            using (var r = new Rig())
            {
                await r.Session.PollAsync(false);
                r.Local = false;
                r.Session.SourceChanged();
                r.Local = true;
                r.Session.SourceChanged();
                Assert.Null(r.Session.Client);
                await r.Session.PollAsync(false);   // rediscovers as at start-up
                Assert.NotNull(r.Session.Client);
                Assert.Equal("ready", r.Session.Status.Phase);
            }
        }
    }
}

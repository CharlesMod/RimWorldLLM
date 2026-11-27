// Ported from agent/tests/test_steward_chat_status.py: the steward's "Narrator is thinking" hook, POST /chat/status around the model call,
// best effort. (test_client_bodies, the BridgeClient half, is in BridgeClientTests.) With no IChatLive sink set, the Python behaviour.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class StewardChatStatusTests
    {
        private static readonly string Done = Rig.Done;
        private static string Final(string t) => Rig.Final(t);
        private static Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld w, object[] script = null, bool playing = true) => Rig.Boot(w, script, playing);
        private static Task Cycle(JobsWorld w) => Rig.Cycle(w);
        private static List<(string State, string Sid)> States(JobsWorld w) => w.Fb.ChatStatus.Select(s => ((string)s["state"], (string)s["stream_id"])).ToList();

        [Fact]
        public async Task ThinkingThenIdleThenFinal()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("All quiet.") });
            var seen = new List<List<string>>();
            fake.OnCall = _ => seen.Add(w.Fb.ChatStatus.Select(s => (string)s["state"]).ToList());
            w.Fb.Playing = false;
            Rig.Chat(w, "status?");
            await Cycle(w);
            Assert.Equal(new[] { "thinking" }, seen[0]);                      // already posted when the model is first called
            Assert.Equal("narrator", (string)w.Fb.ChatStatus[0]["who"]);
            Assert.Equal(new[] { "thinking", "idle" }, States(w).Select(s => s.State).ToArray());
            Assert.Single(States(w).Select(s => s.Sid).Distinct());
            // idle came before the narrator line: by the order of the requests
            var idx = w.Fb.Requests.Select((r, i) => (r, i)).ToList();
            int idleAt = idx.Where(x => x.r.Key == "POST" && x.r.Value == "/chat/status").Select(x => x.i).ElementAt(1);
            int lineAt = idx.First(x => x.r.Key == "POST" && x.r.Value == "/chat").i;
            Assert.True(idleAt < lineAt);
            Assert.Equal(new[] { "All quiet." }, Rig.Lines(w, "narrator"));
            // a second cycle gets its own stream id
            Rig.Chat(w, "again?");
            await Cycle(w);
            var ids = States(w).Where(s => s.State == "thinking").Select(s => s.Sid).ToList();
            Assert.True(ids.Count == 2 && ids[0] != ids[1]);
        }

        [Fact]
        public async Task IdleWhenBackendRaises()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            fake.Throw = new InvalidOperationException("model down");
            w.Fb.Playing = false;
            Rig.Chat(w, "hello?");
            try { await Cycle(w); } catch (Exception) { }
            Assert.Equal(new[] { "thinking", "idle" }, States(w).Select(s => s.State).ToArray());
        }

        [Fact]
        public async Task IdleWhenNoFinal()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            w.Fb.Playing = false;
            Rig.Chat(w, "hello?");
            await Cycle(w);
            Assert.Equal(new[] { "thinking", "idle" }, States(w).Select(s => s.State).ToArray());
        }

        [Fact]
        public async Task RejectingBridgeDoesNotBreakCycle()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("Still here.") });
            w.Fb.ChatStatusEnabled = false;
            w.Fb.Playing = false;
            Rig.Chat(w, "ping");
            await Cycle(w);
            Assert.Equal(new[] { "Still here." }, Rig.Lines(w, "narrator"));
            Assert.Empty(w.Fb.ChatStatus);
        }
    }
}

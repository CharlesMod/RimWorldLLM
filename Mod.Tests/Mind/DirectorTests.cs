// Port of agent/tests/test_director.py (13 tests) over the World fixture (Mind/Fakes/World.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindT = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class DirectorTests
    {
        static KeyValuePair<int, string>[] Who(params (int, string)[] a) { return a.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2)).ToArray(); }

        static async Task Pump(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }

        [Fact]
        public async Task EpochChangeDropsAndReloadsMinds()
        {
            using var w = new World(); await w.GoAsync();
            // the working copy gets ahead of the save
            w.D.Minds[1].Wants = new List<string> { "unsaved want" };
            w.D.Dirty.Remove(1);
            w.Fb.Minds[1] = new KeyValuePair<JObject, int>(new MindT(1, "Ana") { Wants = new List<string> { "saved want" }, Notes = "from the save" }.Dump(), 7);
            w.Fb.ReloadGame();
            await w.D.StepAsync();
            Assert.Equal(new List<string> { "saved want" }, w.D.Minds[1].Wants);
            Assert.Equal("from the save", w.D.Minds[1].Notes);
            Assert.Equal(7, w.D.Revs[1]);
            Assert.False(w.Bridge.EpochChanged);
        }

        [Fact]
        public async Task EpochZeroMainMenuDropsEverything()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Epoch = 0;
            await w.D.StepAsync();
            Assert.Empty(w.D.Minds);
        }

        [Fact]
        public async Task WitnessOnlyThroughDirectorAndMindSaved()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("injury", Who((1, "victim"), (3, "attacker")), "Ana was shot.", new[] { 1, 2 }, 0.2);
            await w.D.StepAsync(); await w.D.DrainAsync(); await w.D.StepAsync();
            Assert.Single(w.D.Minds[1].Memories);
            Assert.Single(w.D.Minds[2].Memories);
            Assert.Empty(w.D.Minds[3].Memories);
            var saved = w.Fb.Minds[1].Key;
            Assert.StartsWith("It happened to me", (string)saved["memories"][0]["text"]);
            Assert.Empty((JArray)w.Fb.Minds[3].Key["memories"]);
        }

        [Fact]
        public async Task EventSpeaksWithExpiryAndOpId()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana was shot.", new[] { 1 }, 0.6);
            await Pump(w);
            Assert.Single(w.Fb.SayLog);
            var s = w.Fb.SayLog[0];
            int exp = (int)s["expires_tick"];
            Assert.Equal(1, (int)s["pawn"]);
            Assert.InRange(exp, 1000 + 3000, 1000 + 3000 + 30);
            Assert.False(string.IsNullOrEmpty((string)s["op_id"]));
            Assert.True((bool)s["combat"]);
            Assert.False(string.IsNullOrEmpty((string)w.D.Said[0]["text"]));
        }

        [Fact]
        public async Task LowSeverityNeverReachesModel()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("level_up", Who((1, "subject")), "Ana got better at Cooking.", new[] { 1 }, 0.05);
            await Pump(w);
            Assert.Empty(w.Llm.Calls);
            Assert.Empty(w.Fb.SayLog);
        }

        [Fact]
        public async Task ExpiredLineDroppedSilently()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1 }, 1.0);
            w.Hook.Before = () => { w.Real += 30.0; return Task.CompletedTask; };      // the line took 30 s
            await Pump(w);
            Assert.Empty(w.Fb.SayLog);
            Assert.Contains(w.D.Dropped, d => d.Reason == "expired");
        }

        [Fact]
        public async Task FailedLineIsDroppedNeverShown()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["line"] = new List<string> { "I'm sorry, but I can't help with that." };
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1 }, 1.0);
            await Pump(w);
            Assert.Empty(w.Fb.SayLog);
            Assert.Contains(w.D.Dropped, d => d.Reason == "refusal");
        }

        [Fact]
        public async Task SkipIfNothingNew()
        {
            using var w = new World(); await w.GoAsync();
            for (int i = 0; i < 2; i++)
            {
                w.Fb.AddEvent("injury", Who((1, "victim")), "Ana was shot.", new[] { 1 }, 0.6);
                w.D.Leases.Quotas.LastSpoke.Clear();
                await Pump(w);
            }
            Assert.Single(w.Fb.SayLog);
            Assert.Contains((1, "nothing_new"), w.D.Dropped);
        }

        [Fact]
        public async Task PlayerTalkPreemptsAndAnswersWithTalkId()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.TalkQueue.Add(new JObject { ["id"] = "t1", ["pawn"] = 1, ["text"] = "How are you?", ["from"] = "player" });
            await Pump(w);
            Assert.Equal("t1", (string)w.Fb.SayLog[0]["talk_id"]);
            Assert.Null(w.Fb.SayLog[0]["expires_tick"]);
            Assert.Empty(w.Fb.TalkQueue);
            string prompt = string.Join("\n", w.Llm.Calls[0].Messages.Select(m => m.Text));
            Assert.Contains("The player says to you: How are you?", prompt);
        }

        [Fact]
        public async Task AmbientPauseInDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Speed = 4;
            w.Fb.AddEvent("level_up", Who((1, "subject")), "Ana got better.", new[] { 1 }, 0.9);
            await Pump(w);
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana was shot.", new[] { 1 }, 0.9);
            await Pump(w);
            Assert.Single(w.D.Said);
            Assert.True((bool)w.Fb.SayLog[0]["combat"]);
            Assert.Single(w.Fb.SayLog);
        }

        [Fact]
        public async Task PrefixDoesNotMentionUnknownPawn()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Scenes[1] = JObject.Parse("{\"room\": {\"role\": \"Kitchen\"}, \"present\": [{\"pawn\": 2, \"job\": \"idle\", \"distance\": 2, \"can_hear\": true}], \"time_of_day\": \"morning\", \"weather\": \"clear\", \"trends\": {}}");
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana was shot.", new[] { 1 }, 0.6);
            await Pump(w);
            string prefix = w.Llm.Calls[0].Messages[0].Text + "\n" + w.Llm.Calls[0].Messages[1].Text;   // the scene now leads the user message
            Assert.Contains("Bram", prefix);
            Assert.DoesNotContain("Cy", prefix);
        }

        [Fact]
        public async Task GossipCreatesToldMemoryOnlyIfApplied()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["line"] = new List<string> { "Cy is hoarding food, you know." };
            w.Llm.Script["intent"] = new List<string> { "gossip" };
            w.Fb.AddEvent("social", Who((1, "speaker"), (2, "listener")), "Ana spoke with Bram.", new[] { 1, 2 }, 0.7);
            w.D.Cfg.Quotas.PairPerDay = 9;
            await Pump(w);
            var told = w.D.Minds[2].Memories.Where(m => m.Source == "told").ToList();
            // conversation ran; if gossip was spoken by Ana the listener has a lower-confidence told memory
            if (w.D.Said.Any(s => (int)s["actor"] == 1 && (string)s["intent"] == "gossip"))
            {
                Assert.NotEmpty(told);
                Assert.True(told[0].Confidence < 1);
                Assert.Equal("Ana", told[0].ToldBy);
            }
        }

        /// <summary>Fails the first two /health calls like a bridge with no game loaded, and cancels the run on the third.</summary>
        sealed class NoGameThenOk : IBridgeTransport
        {
            readonly IBridgeTransport _inner; readonly CancellationTokenSource _stop;
            public int Tries;
            public NoGameThenOk(IBridgeTransport inner, CancellationTokenSource stop) { _inner = inner; _stop = stop; }
            public Task<BridgeReply> SendAsync(BridgeRequest r, CancellationToken ct = default)
            {
                if (r.Path == "/health")
                {
                    Tries++;
                    if (Tries < 3) throw new BridgeError("no_game", "no game is loaded", 409);
                    _stop.Cancel();                                // started: end the run loop at once
                }
                return _inner.SendAsync(r, ct);
            }
        }

        [Fact]
        public async Task RunWaitsForAGameInsteadOfExiting()
        {
            using var tmp = new TempDir();
            var fb = new RimWorldLLM.Mind.Fakes.FakeBridge("tok");
            var cts = new CancellationTokenSource();
            var flaky = new NoGameThenOk(fb, cts);
            var cfg = new MindConfig { DataDir = tmp.Path, ThoughtEnabled = false };
            var bridge = new BridgeClient(flaky, RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames, "tok");
            using var d = new Director(cfg, bridge, new FakeLlm(), new RimWorldLLM.Mind.RimWorld.RimWorldAdapter());
            await d.RunAsync(cts.Token, 0.0);
            Assert.Equal(3, flaky.Tries);
        }
    }
}

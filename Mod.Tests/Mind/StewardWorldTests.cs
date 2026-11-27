// The Python tests that run the vivarium steward under the real Director (the `world` fixture): agent/tests/test_steward.py (every boot(world)/cycle
// test), test_steward_more.py (board batching, rev conflict, idle yield, recap waits, strategy order, letters/alerts not voiced, wants off, slot pin),
// test_steward_chat_status.py (the cycle tests), test_steward_rules.py (the two world tests + the load_strategy half) and test_model_tier.py (the eight
// world tests). They run over Mind/Fakes/World.cs (a real Director over FakeBridge + FakeLlm); earlier versions of most of the cycle tests exist in
// StewardTests / StewardMoreTests / StewardChatStatusTests against the JobsTestHost stand-in, these pin the Director wiring (Director.StepAsync).
// test_steward_try is not ported: it runs the Python CLI (games/rimworld/steward_try.py), and there is no C# CLI.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>test_steward.py's boot/cycle/chat/lines helpers over the real World.</summary>
    internal static class WRig
    {
        public static readonly string Done = Rig.Done;
        public static string Call(string tool, object args) => Rig.Call(tool, args);
        public static string Final(string text) => Rig.Final(text);

        public static async Task<(World w, Steward st, StewardFake fake)> Boot(World w, object[] script = null, bool playing = true, Action<MindConfig> cfg = null)
        {
            cfg?.Invoke(w.Cfg);
            w.Cfg.Wants = false;                         // the shared tool backend is the steward's alone in these tests
            w.Fb.Digest = (JObject)StewardTestData.Sample.DeepClone();
            await w.GoAsync();
            var fake = new StewardFake(script ?? new object[] { Done });
            w.D.ToolBackend = fake; w.D.ToolMode = "grammar";
            w.Fb.Playing = false;
            await w.D.Steward.StepAsync();               // first sight: the chat cursor starts here
            w.Fb.Playing = playing;
            return (w, w.D.Steward, fake);
        }

        /// <summary>The real wiring: poll events, ingest (observe), steward.step; then drain.</summary>
        public static async Task Cycle(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }

        public static JObject Chat(World w, string text, int to = 0, string name = "") => w.Fb.AddChat("player", text, to, name);
        public static List<string> Lines(World w, string who) => w.Fb.Chat.Where(e => (string)e["who"] == who).Select(e => (string)e["text"]).ToList();
        public static string Json(StewardFake.CallRec c) => Rig.Json(c);
        public static string System(StewardFake.CallRec c) => Rig.System(c);
        public static string User(StewardFake.CallRec c) => Rig.User(c);
        public static bool Same(string json, JToken got) => Rig.Same(json, got);
        public static JObject Ev(World w, string type, string summary, double severity, JObject data = null, params (int, string)[] actors) =>
            w.Fb.AddEvent(type, actors.Select(a => new KeyValuePair<int, string>(a.Item1, a.Item2)).ToList(), summary, new List<int>(), severity, null, data);
        public static void NoEnv() => Environment.SetEnvironmentVariable(Steward.EnvStrategy, null);
    }

    [Collection("StewardEnv")]
    public class StewardWorldTests
    {
        private static readonly string Done = WRig.Done;
        private static string Call(string t, object a) => WRig.Call(t, a);
        private static string Final(string t) => WRig.Final(t);
        private static Task<(World w, Steward st, StewardFake fake)> Boot(World w, object[] script = null, bool playing = true, Action<MindConfig> cfg = null) => WRig.Boot(w, script, playing, cfg);
        private static Task Cycle(World w) => WRig.Cycle(w);
        private static JObject Chat(World w, string text, int to = 0, string name = "") => WRig.Chat(w, text, to, name);
        private static List<string> Lines(World w, string who) => WRig.Lines(w, who);
        private static List<(string State, string Sid)> States(World w) => w.Fb.ChatStatus.Select(s => ((string)s["state"], (string)s["stream_id"])).ToList();

        private static string SystemOf(StewardFake fake) => WRig.System(fake.Calls[0]);
        // ---------------------------------------------------------------- wakes
        [Fact]
        public async Task ChatWakesAtOnceAndIsAnswered()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("Cooking is already first for Ana.") });
            w.Fb.Playing = false;                    // advisor mode: chat wakes, nothing else does
            Chat(w, "who cooks?");
            await Cycle(w);
            Assert.Equal(new[] { "Cooking is already first for Ana." }, Lines(w, "narrator"));
            Assert.Contains("who cooks?", WRig.User(fake.Calls[0]));
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 1 }, st.Stats.ByReason);
        }

        [Fact]
        public async Task OldHistoryAndNamedLinesAreNotAnswered()
        {
            WRig.NoEnv();
            using var world = new World();
            world.Fb.AddChat("player", "an old line from before the agent connected");
            world.Fb.AddChat("narrator", "the narrator answered it back then");
            var (w, st, fake) = await Boot(world, playing: false);
            await Cycle(w);
            Assert.Empty(fake.Calls);                // the cursor started at the current seq
            Chat(w, "@Bram, how are you?", 2, "Bram");
            w.Fb.AddChat("narrator", "a narrator line");
            w.Fb.AddChat("colonist", "fine, thanks", 2, "Bram");
            await Cycle(w);
            Assert.Empty(fake.Calls);
            Chat(w, "hello narrator");
            await Cycle(w);
            Assert.Single(fake.Calls);
        }

        [Fact]
        public async Task ChatCursorResetsOnANewWorld()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, playing: false);
            Chat(w, "one");
            await Cycle(w);
            Assert.True(st.PendingChat.Count == 0 && st.Cursor >= 1);      // read; the steward's own answer may follow it in the log
            w.Fb.ReloadGame();
            w.Fb.Chat = new List<JObject>(); w.Fb.ChatSeq = 0;             // the loaded save has its own chat log
            await Cycle(w);                                                 // Director.step: the epoch changed, the steward is reset
            Assert.Null(st.Cursor);                                         // reset by the epoch change
            w.Fb.AddChat("player", "answered in that save");
            w.Fb.AddChat("narrator", "an answer in that save");
            await w.D.Steward.StepAsync();                                    // first sight of the new world: answered lines are history
            await w.D.DrainAsync();
            Assert.Equal(1, fake.Cycles);
            Chat(w, "after the load");
            await Cycle(w);
            Assert.Equal(2, fake.Cycles);
        }

        public static IEnumerable<object[]> EventWakeCases() => new[]
        {
            new object[] { "raid_start", "{}", 0.9 }, new object[] { "mental_break", "{}", 0.8 }, new object[] { "death", "{}", 1.0 },
            new object[] { "alert", "{\"alert\":\"Alert_X\",\"label\":\"Something\",\"critical\":true}", 0.8 },
            new object[] { "alert", "{\"alert\":\"Alert_Fire\",\"label\":\"Fire\",\"critical\":false}", 0.8 },
            new object[] { "alert", "{\"alert\":\"Alert_Starvation\",\"label\":\"Starvation\",\"critical\":false}", 0.8 },
            new object[] { "alert", "{\"alert\":\"Alert_ColonistNeedsRescue\",\"label\":\"Colonist needs rescue\",\"critical\":false}", 0.8 },
            new object[] { "letter", "{\"letter\":77,\"label\":\"Quest\",\"choices\":[\"Accept\",\"Reject\"],\"quest\":true}", 0.6 },
        };

        [Theory, MemberData(nameof(EventWakeCases))]
        public async Task EventWakeKinds(string etype, string data, double severity)
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            w.Fb.Tick += 100;
            WRig.Ev(w, etype, "a " + etype, severity, JObject.Parse(data));
            // a heartbeat is not due (the first cycle ran at the first playing step), so the wake is the event
            await Cycle(w);                          // heartbeat first (no cycle yet)
            fake.Calls.Clear();
            w.Real += 30;
            WRig.Ev(w, etype, "a " + etype + " again", severity, JObject.Parse(data));
            await Cycle(w);
            Assert.True(st.Stats.ByReason.TryGetValue("event", out int n) && n == 1, JsonConvert.SerializeObject(st.Stats.ByReason));
            Assert.Contains("Something important happened", WRig.User(fake.Calls[0]));
        }

        public static IEnumerable<object[]> OtherEventCases() => new[]
        {
            new object[] { "alert", "{\"alert\":\"Alert_LowFood\",\"label\":\"Low food\",\"critical\":false}", 0.4 },
            new object[] { "letter", "{\"letter\":80,\"label\":\"A trader\",\"choices\":[],\"quest\":false}", 0.3 },
            new object[] { "injury", "{}", 0.5 }, new object[] { "death", "{\"animal\":true}", 0.6 },
        };

        [Theory, MemberData(nameof(OtherEventCases))]
        public async Task OtherEventsOnlyQueueForTheNextCycle(string etype, string dataJson, double severity)
        {
            WRig.NoEnv();
            using var world = new World();
            var data = JObject.Parse(dataJson);
            var (w, st, fake) = await Boot(world);
            await Cycle(w);                          // the opening heartbeat cycle
            fake.Calls.Clear();
            w.Real += 30;
            WRig.Ev(w, etype, "queued " + etype, severity, data);
            await Cycle(w);
            Assert.Empty(fake.Calls);                // no wake
            w.Fb.Tick += 6 * 2500 + 10;              // the heartbeat comes; the event rides along
            w.Real += 30;
            await Cycle(w);
            string user = WRig.User(fake.Calls[0]);
            bool animal = data["animal"] != null && (bool)data["animal"];
            Assert.True(user.Contains("queued " + etype) == !animal || etype == "death");
        }

        [Fact]
        public async Task HeartbeatCountsGameTicksAndRespectsTheRealFloor()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 1 }, st.Stats.ByReason);      // the first look when it starts playing
            w.Fb.Tick += 6 * 2500 - 100;
            w.Real += 60;
            await Cycle(w);
            Assert.Equal(1, st.Stats.Cycles);                                  // not yet 6 hours
            w.Fb.Tick += 200;
            w.Real += 5;                                                       // ticks are due but the real floor (20 s) is not
            await w.D.Steward.StepAsync();
            Assert.Equal(1, st.Stats.Cycles);
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 2 }, st.Stats.ByReason);
            w.Fb.VivSettings["heartbeat_hours"] = 1.0;                         // the player changed it: 1 hour = 2500 ticks
            w.Fb.Tick += 2600;
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 3 }, st.Stats.ByReason);
        }

        [Fact]
        public async Task PausedGameHasNoHeartbeatButChatStillWakes()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            await Cycle(w);
            w.Fb.Paused = true;
            w.Fb.Tick += 10 * 2500;
            w.Real += 60;
            WRig.Ev(w, "raid_start", "raid", 0.9);
            await Cycle(w);
            Assert.Equal(1, st.Stats.Cycles);
            Chat(w, "what now?");
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 1, ["chat"] = 1 }, st.Stats.ByReason);
        }

        [Fact]
        public async Task NotPlayingOnlyChatWakes()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, playing: false);
            w.Fb.Tick += 50 * 2500;
            w.Real += 100;
            WRig.Ev(w, "raid_start", "raid", 0.9);
            await Cycle(w);
            Assert.True(fake.Calls.Count == 0 && st.Events.Count == 0 && st.Urgent.Count == 0);
            Chat(w, "hi");
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 1 }, st.Stats.ByReason);
            Assert.Contains("ADVISOR MODE", WRig.System(fake.Calls[0]));
        }

        [Fact]
        public async Task ChatIgnoresTheFloorAndOneCycleAtATime()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("first"), Final("second") });
            w.Fb.Playing = false;
            fake.Gate = new TaskCompletionSource<bool>();
            Chat(w, "one");
            await w.D.Steward.StepAsync();
            await Task.Delay(50);
            Assert.True(st.Active());
            Chat(w, "two");                                    // arrives mid-cycle
            await w.D.Steward.StepAsync();
            Assert.True(st.Stats.Cycles == 0 && st.PendingChat.Count == 1);
            fake.Gate.SetResult(true);
            await w.D.DrainAsync();
            Assert.Equal(1, st.Stats.Cycles);
            await Cycle(w);                                    // no real time passed: chat ignores the floor
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 2 }, st.Stats.ByReason);
            Assert.Contains("two", WRig.User(fake.Calls[fake.Calls.Count - 1]));
            Assert.Equal(new[] { "first", "second" }, Lines(w, "narrator"));
        }

        [Fact]
        public async Task TheNarratorsOwnPauseWakesItOnce()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            await Cycle(w);
            w.Fb.Paused = true; w.Fb.PausedByNarrator = true;
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 1, ["paused"] = 1 }, st.Stats.ByReason);
            w.Real += 30;
            await Cycle(w);
            Assert.Equal(2, st.Stats.Cycles);                  // once per pause
        }

        [Fact]
        public async Task AnOlderModWithoutVivariumKeepsTheStewardOff()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            w.Fb.VivariumSupported = false;
            await Cycle(w);
            Assert.True(st.Disabled && fake.Calls.Count == 0);
            int Viv() => w.Fb.Requests.Count(r => r.Value == "/vivarium");
            int n = Viv();
            await Cycle(w);
            Assert.Equal(n, Viv());                            // not asked again
        }

        [Fact]
        public async Task IdleStepCostsOneVivariumRead()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, playing: false);
            w.Fb.Requests.Clear();
            await w.D.Steward.StepAsync();
            Assert.Equal(new[] { new KeyValuePair<string, string>("GET", "/vivarium") }, w.Fb.Requests.ToArray());
        }

        // ---------------------------------------------------------------- tools reach the bridge
        [Fact]
        public async Task AdvisorModeDoesOneTaskAndSaysSo()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1 }),
                Call("assign_bed", new { pawn = "Eli", bed_id = "3305" }),          // a second task: refused in advisor mode
                Final("Ana cooks first now. I stopped there.") });
            w.Fb.Playing = false;
            Chat(w, "put Ana on cooking");
            await Cycle(w);
            Assert.Single(w.Fb.ActLog);
            Assert.Equal("work", w.Fb.ActLog[0].Key);
            Assert.True(WRig.Same("{\"pawn\":101,\"priorities\":{\"Cooking\":1}}", w.Fb.ActLog[0].Value));
            Assert.Contains("one task only", WRig.Json(fake.Calls[2]));
            Assert.Equal(new[] { "Ana cooks first now. I stopped there." }, Lines(w, "narrator"));
            Assert.Empty(Lines(w, "note"));                                          // the game writes the greentext line itself
            var log = (JArray)w.Fb.GoalsBoard["log"];
            Assert.StartsWith("Asked (put Ana on cooking)", (string)log[log.Count - 1]["text"]);
        }

        [Fact]
        public async Task PlayingRunsManyActionsWithResolvedIds()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1 }),
                Call("assign_bed", new { pawn = "Eli", bed_id = "3305" }),
                Call("answer_letter", new { letter_id = "77", choice = "accept" }),
                Call("draft", new { pawns = "Bram, Dara", drafted = "yes" }),
                Call("set_research", new { project = "Batteries" }),
                Call("order", new { pawn = "Bram", order = "move", x = 40, z = 61, target_id = 0, hold = "no" }),
                Call("set_policy", new { pawn = "Cy", kind = "food", name = "simple" }),
                Final("All done.") });
            await Cycle(w);
            var got = w.Fb.ActLog.ToDictionary(p => p.Key, p => p.Value);
            Assert.True(WRig.Same("{\"pawn\":101,\"priorities\":{\"Cooking\":1}}", got["work"]));
            Assert.True(WRig.Same("{\"pawn\":105,\"bed\":3305}", got["bed"]));
            Assert.True(WRig.Same("{\"letter\":77,\"choice\":\"Accept\"}", got["letter"]));
            Assert.True(WRig.Same("{\"pawns\":[102,104],\"drafted\":true}", got["draft"]));
            Assert.True(WRig.Same("{\"project\":\"Batteries\"}", got["research"]));
            Assert.True(WRig.Same("{\"pawn\":102,\"order\":\"move\",\"x\":40,\"z\":61}", got["order"]));
            Assert.True(WRig.Same("{\"pawn\":103,\"kind\":\"food\",\"name\":\"Simple\"}", got["policy"]));
            Assert.Empty(Lines(w, "note"));                                          // no automatic notes after acts
        }

        [Fact]
        public async Task OwnNoteReplacesAutomaticNotes()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("assign_bed", new { pawn = "Eli", bed_id = "3305" }),
                Call("note", new { text = "Eli has a bed now" }),
                Final("Done with beds.") });
            await Cycle(w);
            Assert.Single(w.Fb.ActLog);
            Assert.Equal("bed", w.Fb.ActLog[0].Key);
            Assert.True(WRig.Same("{\"pawn\":105,\"bed\":3305}", w.Fb.ActLog[0].Value));
            Assert.Equal(new[] { "Eli has a bed now" }, Lines(w, "note"));           // no "→ bed 3305 ..." doubled on top
        }

        [Fact]
        public async Task ScheduleAttackHoldAndTimeBodies()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_schedule", new { pawn = "Dara", from_hour = 22, to_hour = 6, assignment = "sleep" }),
                Call("order", new { pawn = "Bram", order = "attack", x = 0, z = 0, target_id = 5512, hold = "no" }),
                Call("order", new { pawn = "Bram", order = "hold", x = 0, z = 0, target_id = 0, hold = "yes" }),
                Call("set_time", new { pause = "yes", speed = "leave", emergency = "yes", why = "since raiders are at the gate" }),
                Done });
            await Cycle(w);
            var got = w.Fb.ActLog;
            Assert.Equal("schedule", got[0].Key);
            Assert.True(WRig.Same("{\"pawn\":104,\"from_hour\":22,\"to_hour\":6,\"assignment\":\"sleep\"}", got[0].Value));
            Assert.Equal("order", got[1].Key);
            Assert.True(WRig.Same("{\"pawn\":102,\"order\":\"attack\",\"target\":5512}", got[1].Value));
            Assert.Equal("order", got[2].Key);
            Assert.True(WRig.Same("{\"pawn\":102,\"order\":\"hold\",\"hold\":true}", got[2].Value));
            Assert.Equal("time", got[3].Key);
            var t = got[3].Value;
            Assert.True((bool)t["pause"] && (bool)t["emergency"] && t["speed"] == null);
            Assert.True((string)t["why"] == "since raiders are at the gate" && t["reason"] == null);
            Assert.True(w.Fb.PausedByNarrator);
        }

        [Fact]
        public async Task ARefusalGoesBackToTheModelAndIsNotCountedAsDone()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_time", new { pause = "yes", speed = "leave", emergency = "no", why = "since the colony needs rest" }),
                Final("The game would not let me pause.") });
            w.Fb.ActRefuse["time"] = "pausing needs an emergency";
            await Cycle(w);
            Assert.Contains("pausing needs an emergency", WRig.Json(fake.Calls[1]));
            Assert.True(w.Fb.ActLog.Count == 0 && st.Stats.Refused == 1 && st.Stats.Acts == 0);
            Assert.Equal(new[] { "The game would not let me pause." }, Lines(w, "narrator"));
            Assert.Equal(0, (int)w.Fb.GoalsBoard["rev"]);                            // nothing done: no board write, no log line
        }

        [Fact]
        public async Task UnknownNamesAndPoliciesComeBackAsErrors()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_policy", new { pawn = "Ana", kind = "food", name = "Gourmet" }),
                Call("set_research", new { project = "Warp drive" }),
                Call("draft", new { pawns = "Zed", drafted = "yes" }), Done });
            await Cycle(w);
            string last = WRig.Json(fake.Calls[fake.Calls.Count - 1]);
            Assert.Contains("no food policy named Gourmet", WRig.Json(fake.Calls[1]));
            Assert.Contains("no available project named Warp drive", WRig.Json(fake.Calls[2]));
            Assert.True(last.Contains("no colonist named Zed") && w.Fb.ActLog.Count == 0);
        }

        [Fact]
        public async Task TheLoopRunsOnSlotZeroWithThePlaybookAndDigest()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            await Cycle(w);
            var c = fake.Calls[0];
            Assert.Equal(0, c.Slot);
            Assert.Equal(0, w.Cfg.NarratorSlot);
            string sysm = WRig.System(c), user = WRig.User(c);
            Assert.True(sysm.Contains("## Strategy notes") && sysm.Contains("GAME DATA RULE") && sysm.Contains("set_work("));
            Assert.True(user.Contains("PEOPLE (5)") && user.Contains("letter #77") && user.Contains("YOUR GOALS BOARD"));
        }

        // ---------------------------------------------------------------- final line
        [Theory]
        [InlineData("done")]
        [InlineData("Done.")]
        [InlineData("nothing to say")]
        public async Task DoneAndEmptyFinalLinesAreSilent(string text)
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final(text) });
            await Cycle(w);
            Assert.True(Lines(w, "narrator").Count == 0 && st.Stats.Cycles == 1);
        }

        [Fact]
        public async Task ALinePairingAMinorWithRomanceIsNotPosted()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("Eli, a kiss under the stars, my darling?") });
            w.D.Ages[105] = 15.0;
            w.D.Names[105] = "Eli"; w.D.NameTokens[105] = new HashSet<string> { "eli" };
            await Cycle(w);
            Assert.Empty(Lines(w, "narrator"));
        }

        [Fact]
        public async Task ADeadBackendOnAChatGetsAPlainReply()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { "not json at all" });
            w.Fb.Playing = false;
            Chat(w, "hello?");
            await Cycle(w);
            Assert.Contains("couldn't think that through", Lines(w, "narrator")[0]);
            Chat(w, "again");
            w.D.ToolBackend = null;
            await Cycle(w);                                    // no backend: the steward is simply off
            Assert.Single(Lines(w, "narrator"));
        }

        [Fact]
        public async Task DigestUnreadableOnAChatSaysSo()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            w.Fb.Playing = false;
            w.Fb.Digest = null;
            Chat(w, "hello?");
            await Cycle(w);
            Assert.True(Lines(w, "narrator")[0].Contains("can't read the game state") && fake.Calls.Count == 0);
        }

        [Fact]
        public async Task AWordlessFinalLineOnAChatGetsAPlainReply()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final(":") });       // seen live on the 26B
            w.Fb.Playing = false;
            Chat(w, "should we take the quest?");
            await Cycle(w);
            var got = Lines(w, "narrator");
            Assert.True(got.Count == 1 && got[0] != ":" && got[0].Contains("couldn't think that through"));
        }

        [Fact]
        public async Task RecentUnansweredLinesAreCaughtUpOnFirstSight()
        {
            WRig.NoEnv();
            using var world = new World();
            world.Fb.AddChat("player", "answered earlier");
            world.Fb.AddChat("narrator", "an earlier answer");
            world.Fb.AddChat("player", "typed while the agent was restarting");
            var (w, st, fake) = await Boot(world, new object[] { Final("Here now.") }, playing: false);
            await Cycle(w);
            var narr = Lines(w, "narrator");
            Assert.Equal("Here now.", narr[narr.Count - 1]);
            Assert.Contains("typed while the agent was restarting", WRig.User(fake.Calls[0]));
        }

        [Fact]
        public async Task SwitchingOnWakesTheStewardAtOnce()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("Advice."), Final("Taking over.") }, playing: false);
            Chat(w, "advise me");
            await Cycle(w);                          // an advisor cycle sets the heartbeat clock
            Assert.Equal(1, fake.Cycles);
            w.Fb.Playing = true;                     // the player flips the switch
            await Cycle(w);
            Assert.True(fake.Cycles == 2 && st.Stats.ByReason.TryGetValue("switched on", out int n) && n == 1);
        }

        // ---------------------------------------------------------------- why on acts, no automatic notes, who is answered
        [Fact]
        public async Task WhyPassesFromToolToActBodyAndMissingWhySendsNone()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1, why = "since meals are running low" }),
                Call("set_research", new { project = "Batteries" }),
                Call("draft", new { pawns = "all", drafted = "yes", why = "since raiders are close" }),
                Final("Done.") });
            await Cycle(w);
            var got = w.Fb.ActLog.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal("since meals are running low", (string)got["work"]["why"]);
            Assert.Null(got["research"]["why"]);
            Assert.Equal("since raiders are close", (string)got["draft"]["why"]);
            Assert.Empty(Lines(w, "note"));                                          // no "→" lines after acts
            Assert.DoesNotContain(Lines(w, "narrator").Concat(Lines(w, "note")), t => t.StartsWith("→"));
        }

        [Fact]
        public async Task WhyIsInEveryActingToolAndOptional()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            var sess = st.Kit.Session(w.Fb.Digest, w.Bridge, false);
            foreach (var t in sess.Tools)
                if (StewardKit.Acting.Contains(t.Name))
                {
                    var p = t.Params.First(x => x.Name == "why");
                    Assert.True(!p.Required && p.MaxLen == 80, t.Name);
                }
        }

        [Fact]
        public async Task WhyIsCleanedAndCapped()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1, why = "since " + new string('x', 70) }), Done });
            await Cycle(w);
            string why = (string)w.Fb.ActLog[0].Value["why"];
            Assert.True(why.EnumerateRunes().Count() <= 80);
        }

        [Fact]
        public async Task OnlyPlainPlayerLinesAreAnswered()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("Heard you.") }, playing: false);
            foreach (var who in new[] { "colonist", "thought", "diary", "action", "refused", "narrator", "art" })
                w.Fb.AddChat(who, "I am not the player talking to you");
            w.Fb.AddChat("player", "Ana, how is the stew?", 101, "Ana");
            await Cycle(w);
            Assert.Empty(fake.Calls);                                // nothing woke the steward
            w.Fb.AddChat("player", "narrator, what is up?");
            await Cycle(w);
            Assert.True(fake.Calls.Count >= 1);
            string user = WRig.User(fake.Calls[0]);
            Assert.True(user.Contains("narrator, what is up?") && !user.Contains("how is the stew") && !user.Contains("not the player"));
        }

        [Fact]
        public async Task BoardEditsAreOneBatchedPutWithOneLogLine()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("add_goal", new { text = "Build a second kitchen", horizon = "long" }),
                Call("set_plan", new { text = "Cook first, then beds" }),
                Call("add_order", new { text = "never draft Eli" }),
                Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1 }), Final("Ok.") });
            await Cycle(w);
            // one PUT /goals at rev 0 (a stale rev would have been refused with a conflict and replayed: two PUTs)
            Assert.Single(w.Fb.Requests, r => r.Key == "PUT" && r.Value == "/goals");
            var b = w.Fb.GoalsBoard;
            Assert.Equal(new[] { "Build a second kitchen" }, ((JArray)b["goals"]).Select(g => (string)g["text"]).ToArray());
            Assert.Equal("Cook first, then beds", (string)b["plan"]);
            Assert.Equal(new[] { "never draft Eli" }, ((JArray)b["orders"]).Select(o => (string)o["text"]).ToArray());
            Assert.True(((JArray)b["log"]).Count == 1 && (int)b["rev"] == 1);
        }

        [Fact]
        public async Task ARevConflictRefetchesAndReplaysOnce()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            fake.Script.Clear();
            fake.Script.AddRange(new object[] { Call("add_order", new { text = "no raids please" }), Final("Noted.") });
            fake.OnCall = n =>
            {
                if (n != 1) return;                                  // the player edits the board mid-cycle
                w.Fb.GoalsBoard["rev"] = (int)w.Fb.GoalsBoard["rev"] + 1;
                w.Fb.GoalsBoard["orders"] = JArray.Parse("[{\"id\":\"o9\",\"text\":\"keep the fire going\",\"by\":\"player\"}]");
            };
            await Cycle(w);
            var b = w.Fb.GoalsBoard;
            Assert.Equal(new[] { "keep the fire going", "no raids please" }, ((JArray)b["orders"]).Select(o => (string)o["text"]).ToArray());
            Assert.True((int)b["rev"] == 2 && ((JArray)b["log"]).Count == 1);
        }

        [Fact]
        public async Task StrategyOrderEnvThenConfigThenShipped()
        {
            WRig.NoEnv();
            using var world = new World();
            string e = world.Tmp.Combine("e.txt"), c = world.Tmp.Combine("c.txt");
            File.WriteAllText(e, "ENV STRATEGY"); File.WriteAllText(c, "CFG STRATEGY");
            Environment.SetEnvironmentVariable(Steward.EnvStrategy, e);
            try
            {
                var (w, st, fake) = await Boot(world, new object[] { Done }, cfg: cf => cf.StewardStrategyPath = c);
                await Cycle(w);
                string s = SystemOf(fake);
                Assert.True(s.Contains("## Strategy notes") && s.Contains("ENV STRATEGY") && !s.Contains("CFG STRATEGY"));
            }
            finally { WRig.NoEnv(); }
        }

        [Fact]
        public async Task AnEmptyStrategyLeavesTheHeadingOut()
        {
            WRig.NoEnv();
            using var world = new World();
            string e = world.Tmp.Combine("e.txt");
            File.WriteAllText(e, "");
            var (w, st, fake) = await Boot(world, new object[] { Done }, cfg: cf => cf.StewardStrategyPath = e);
            await Cycle(w);
            Assert.DoesNotContain("## Strategy notes", SystemOf(fake));
        }

        [Fact]
        public async Task TheLoopPinsSlotZero()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            await Cycle(w);
            Assert.Equal(new int?[] { w.Cfg.NarratorSlot }, fake.Calls.Select(c => c.Slot).Distinct().ToArray());
        }

        [Fact]
        public async Task ThinkingThenIdleThenFinal()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("All quiet.") });
            var seen = new List<List<string>>();
            fake.OnCall = _ => seen.Add(w.Fb.ChatStatus.Select(s => (string)s["state"]).ToList());
            w.Fb.Playing = false;
            WRig.Chat(w, "status?");
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
            Assert.Equal(new[] { "All quiet." }, WRig.Lines(w, "narrator"));
            // a second cycle gets its own stream id
            WRig.Chat(w, "again?");
            await Cycle(w);
            var ids = States(w).Where(s => s.State == "thinking").Select(s => s.Sid).ToList();
            Assert.True(ids.Count == 2 && ids[0] != ids[1]);
        }

        [Fact]
        public async Task IdleWhenBackendRaises()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            fake.Throw = new InvalidOperationException("model down");
            w.Fb.Playing = false;
            WRig.Chat(w, "hello?");
            try { await Cycle(w); } catch (Exception) { }
            Assert.Equal(new[] { "thinking", "idle" }, States(w).Select(s => s.State).ToArray());
        }

        [Fact]
        public async Task IdleWhenNoFinal()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            w.Fb.Playing = false;
            WRig.Chat(w, "hello?");
            await Cycle(w);
            Assert.Equal(new[] { "thinking", "idle" }, States(w).Select(s => s.State).ToArray());
        }

        [Fact]
        public async Task RejectingBridgeDoesNotBreakCycle()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Final("Still here.") });
            w.Fb.ChatStatusEnabled = false;
            w.Fb.Playing = false;
            WRig.Chat(w, "ping");
            await Cycle(w);
            Assert.Equal(new[] { "Still here." }, WRig.Lines(w, "narrator"));
            Assert.Empty(w.Fb.ChatStatus);
        }

        // ---------------------------------------------------------------- test_steward_more: slot 0, recap, letters/alerts, wants
        [Fact]
        public async Task IdleJobsYieldToACycleAndComeBack()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Call("set_plan", new { text = "x" }), Final("ok") });
            fake.Gate = new TaskCompletionSource<bool>();
            var lm = w.D.Leases;
            Assert.Null(lm.NarratorOwner);
            // a running chronicle idle job (a task that never ends by itself)
            w.D.Idle.Task = new TaskCompletionSource<bool>().Task; w.D.Idle.Kind = "chronicle";
            Chat(w, "hello");
            await w.D.StepAsync();
            await Task.Delay(50);
            Assert.Equal("steward", lm.NarratorOwner);
            Assert.False(w.D.Idle.Active());                                  // the running chronicle was preempted
            Assert.Contains(w.D.Dropped, d => d.Reason == "idle_preempted:chronicle");
            Assert.False(lm.NarratorMayDoIdleWork());
            Assert.Null(await Lore.CoinAsync(w.D, "something big", 1, 1));    // lore does not start
            fake.Gate.SetResult(true);
            await w.D.DrainAsync();
            Assert.Null(lm.NarratorOwner);
            Assert.False(lm.NarratorBusy || lm.NarratorOwner != null);
        }

        // Python patches recap.run and calls Director._recap_when_free(1); the C# method is private, so the recap is spawned the way the
        // Director does it on a late connect (GET /config recap_open, in start) and its effect (the POST /recap) is what is watched.
        [Fact]
        public async Task TheRecapWaitsForTheCycle()
        {
            WRig.NoEnv();
            using var w = new World();
            w.Fb.Chronicle = new List<JObject>
            {
                JObject.Parse("{\"day\":1,\"kind\":\"day\",\"text\":\"Ana dug the first trench. Bram sulked.\",\"tick\":1}"),
                JObject.Parse("{\"day\":2,\"kind\":\"day\",\"text\":\"Raiders came and Cy was hurt. The wall held.\",\"tick\":2}"),
                JObject.Parse("{\"day\":3,\"kind\":\"major\",\"text\":\"Bram left the colony in a huff. Nobody followed.\",\"tick\":3}"),
            };
            w.Fb.Config["recap_open"] = true;
            w.Llm.Script["recap"] = new List<string> { "Ana dug the first trench.\nRaiders hit and Cy was hurt.\nBram left, and nobody knows why." };
            w.D.Leases.NarratorOwner = "steward";
            await w.GoAsync();                            // starts, and spawns the recap, which waits for the steward's cycle
            await Task.Delay(150);
            Assert.Empty(w.Fb.RecapLog);
            w.D.Leases.NarratorOwner = null;
            for (int i = 0; i < 60 && w.Fb.RecapLog.Count == 0; i++) await Task.Delay(100);     // it polls every 0.5 s
            await w.D.DrainAsync();
            Assert.Single(w.Fb.RecapLog);
        }

        [Fact]
        public async Task LettersAndAlertsAreNotVoicedOrRemembered()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            WRig.Ev(w, "letter", "A quest came", 0.6, JObject.Parse("{\"letter\":78,\"label\":\"Quest\",\"choices\":[\"Accept\"],\"quest\":true}"));
            WRig.Ev(w, "alert", "Low food", 0.4, JObject.Parse("{\"alert\":\"Alert_LowFood\",\"label\":\"Low food\",\"critical\":false}"));
            await w.D.StepAsync();
            await w.D.DrainAsync();
            Assert.True(w.D.Adapter.SilentEvents.SetEquals(new[] { "letter", "alert", "dialog" }));
            Assert.True(w.D.Adapter.Priority("letter") == Cls.Idle && w.D.Adapter.Priority("alert") == Cls.Idle);
            Assert.DoesNotContain(w.D.Chronicle.State.Recent, x => x.Text.Contains("A quest came") || x.Text.Contains("Low food"));
            Assert.True(w.D.Dropped.Count == 0 || w.D.Dropped.All(x => !x.ToString().Contains("letter")));
            Assert.Contains("Quest", WRig.Json(fake.Calls[fake.Calls.Count - 1]).Split("\"role\":\"user\"").Last());     // but the steward read them
        }

        [Fact]
        public async Task WantsOffWithABackendPresentRunsNoWants()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world);
            w.Cfg.Wants = false;
            Assert.Empty(w.D.Idle.DueWants());
        }

        // ---------------------------------------------------------------- test_steward_rules: the world tests and the load_strategy half
        [Fact]
        public async Task UserTurnHasAlertsAfterWhyYouWoke()
        {
            WRig.NoEnv();
            using var w = new World();
            w.Fb.Digest = (JObject)StewardTestData.Sample.DeepClone();
            await w.GoAsync();
            var st = w.D.Steward;
            var digest = w.Fb.Digest;
            var user = typeof(Steward).GetMethod("UserText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Func<string, string> text = alerts => (string)user.Invoke(st, new object[] { "heartbeat", new List<JObject>(), new List<string>(), JObject.Parse("{\"rev\":0}"), "DIGEST", false, alerts });
            string u = text(st.Kit.PlaybookAlerts(digest));
            Assert.True(u.IndexOf("=== WHY YOU WOKE ===") < u.IndexOf("=== PLAYBOOK ALERTS ===") && u.IndexOf("=== PLAYBOOK ALERTS ===") < u.IndexOf("=== SETTINGS ==="));
            Assert.Contains("[P1 food.emergency]", u);
            Assert.DoesNotContain("PLAYBOOK ALERTS", text(""));
        }

        [Fact]
        public async Task CycleSendsAlertsToTheModel()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await Boot(world, new object[] { Done }, playing: false);
            Chat(w, "how is the food?");
            await Cycle(w);
            string user = WRig.User(fake.Calls[0]);
            Assert.True(user.Contains("=== PLAYBOOK ALERTS ===") && user.Contains("[P1 food.emergency]"));
            Assert.True(user.IndexOf("WHY YOU WOKE") < user.IndexOf("PLAYBOOK ALERTS") && user.IndexOf("PLAYBOOK ALERTS") < user.IndexOf("SETTINGS"));
        }

        [Fact]
        public void ShippedStrategyLoadedCappedAndOverrideOrder()
        {
            WRig.NoEnv();
            using var tmp = new TempDir();
            string text = ((IStewardKit)new RimWorldAdapter().StewardKit).DefaultStrategy();
            Assert.StartsWith("You are the RimWorld colony steward", text);
            Assert.Contains("## Order of concern", text);
            var cfg = new MindConfig();
            Assert.True(cfg.StewardStrategyMaxBytes == 12288 && System.Text.Encoding.UTF8.GetByteCount(text) <= cfg.StewardStrategyMaxBytes);
            var (got, note) = Steward.LoadStrategy(cfg, text);
            Assert.True(got == text.Trim() && note == "");
            string c = tmp.Combine("c.md"), e = tmp.Combine("e.md");
            File.WriteAllText(c, "CONFIG"); File.WriteAllText(e, "ENV");
            Assert.Equal("CONFIG", Steward.LoadStrategy(new MindConfig { StewardStrategyPath = c }, text).Text);
            Environment.SetEnvironmentVariable(Steward.EnvStrategy, e);
            try { Assert.Equal("ENV", Steward.LoadStrategy(new MindConfig { StewardStrategyPath = c }, text).Text); }
            finally { WRig.NoEnv(); }
        }

        // ---------------------------------------------------------------- test_model_tier: the eight world tests
        private const string Aim = "I wish somebody would notice how tired I am";

        private static async Task<World> CloudWorld(string model, Action<MindConfig> set = null)
        {
            var w = await new World().GoAsync();
            w.Cfg.Llm.Model = model;
            w.Cfg.InnerCloudChance = 1.0;
            set?.Invoke(w.Cfg);
            return w;
        }

        // Python calls Director._inner_after_say directly; here a line the game applied reaches it through Director.SayAsync.
        private static async Task Say(World w, int aid = 1)
        {
            Assert.True(await w.D.SayAsync(aid, new Spoken("Hello.", "none", "normal", Aim, false), null, null));
        }

        private static List<JObject> Clouds(World w) => w.Fb.ThoughtLog.Where(t => (t["kind"] == null ? "thought" : (string)t["kind"]) == "thought").ToList();

        [Fact]
        public async Task CloudsOffOnSmallWhenFieldFalseAimStillStored()
        {
            using var w = await CloudWorld("gemma4-e4b-rp", c => c.InnerCloudSmall = false);
            await Say(w);
            Assert.True(Clouds(w).Count == 0 && w.D.Minds[1].Inner.Count == 1);
        }

        [Fact]
        public async Task CloudsOnSmallByDefault()
        {
            using var w = await CloudWorld("gemma4-e4b-rp");
            await Say(w);
            Assert.Single(Clouds(w));
        }

        [Fact]
        public async Task CloudsOnForLargeWhenSmallFieldFalse()
        {
            using var w = await CloudWorld("gemma4-26b", c => c.InnerCloudSmall = false);
            await Say(w);
            Assert.Single(Clouds(w));
        }

        [Fact]
        public async Task MasterSwitchStillWins()
        {
            using var w = await CloudWorld("gemma4-26b", c => c.InnerCloud = false);
            await Say(w);
            Assert.True(Clouds(w).Count == 0 && w.D.Minds[1].Inner.Count > 0);
        }

        private static void ArtEvent(World w) =>
            w.Fb.AddEvent("art_created", new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(1, "maker") }, "Ana finished a sculpture.", new List<int> { 1 }, 0.2,
                          null, JObject.Parse("{\"thing_id\":500,\"def_label\":\"sculpture\",\"quality\":\"good\",\"title\":\"Dog\",\"description\":\"d\"}"));

        private static bool ArtCalls(World w) => w.Llm.Calls.Any(c => c.Kind == "art");

        [Fact]
        public async Task ArtOffOnSmallWhenFieldFalse()
        {
            using var w = await CloudWorld("gemma4-e2b", c => c.ArtSmall = false);
            ArtEvent(w);
            await w.D.StepAsync(); await w.D.DrainAsync();
            Assert.True(w.D.Art.Jobs.Count == 0 && !ArtCalls(w));
        }

        [Fact]
        public async Task ArtOnForSmallWhenArtSmallIsSet()
        {
            using var w = await CloudWorld("gemma4-e4b", c => c.ArtSmall = true);
            ArtEvent(w);
            await w.D.StepAsync(); await w.D.DrainAsync();
            Assert.True(w.D.Art.Jobs.Count > 0 || w.Fb.ArtLog.Count > 0 || ArtCalls(w));
        }

        [Fact]
        public async Task ArtOnForLargeWhenFieldFalse()
        {
            using var w = await CloudWorld("gemma4-26b", c => c.ArtSmall = false);
            ArtEvent(w);
            await w.D.StepAsync(); await w.D.DrainAsync();
            Assert.True(w.D.Art.Jobs.Count > 0 || w.Fb.ArtLog.Count > 0 || ArtCalls(w));
        }

        [Fact]
        public async Task ArtOnForSmallByDefault()
        {
            using var w = await CloudWorld("gemma4-e4b-rp-q40");
            ArtEvent(w);
            await w.D.StepAsync(); await w.D.DrainAsync();
            Assert.True(w.D.Art.Jobs.Count > 0 || w.Fb.ArtLog.Count > 0);      // keeper 2026-11-26: art on for E4B by default
        }
    }
}

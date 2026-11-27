// Ported from agent/tests/test_steward.py: the vivarium steward's wakes, cycles, tools reaching the bridge and final line, on the JobsWorld
// test host (the Python `world` fixture + Director.step for the steward). Deferred tests are listed in the worklog / report.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>The Python tests' scripted tool backend (test_steward.Fake): each chat answers the next script item, the last one repeats.
    /// Gate blocks a call; OnCall runs after the call is recorded (the 1-based call count is passed); Throw makes every call raise.</summary>
    internal sealed class StewardFake : IBackend
    {
        public sealed class CallRec { public List<JObject> Messages; public int? Slot; public int MaxTokens; }
        public readonly List<object> Script;
        public readonly List<CallRec> Calls = new List<CallRec>();
        public TaskCompletionSource<bool> Gate;
        public Action<int> OnCall;
        public Exception Throw;

        public StewardFake(IEnumerable<object> script) { Script = script.ToList(); }

        /// <summary>Calls whose message list is just system + user (the first call of a cycle).</summary>
        public int Cycles => Calls.Count(c => c.Messages.Count == 2);

        public async Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                           int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
        {
            if (Throw != null) throw Throw;
            Calls.Add(new CallRec { Messages = messages.Select(m => (JObject)m.DeepClone()).ToList(), Slot = slot, MaxTokens = maxTokens });
            OnCall?.Invoke(Calls.Count);
            if (Gate != null) await Gate.Task.ConfigureAwait(false);
            object item;
            if (Script.Count > 1) { item = Script[0]; Script.RemoveAt(0); } else item = Script[0];
            return item is Reply r ? r : new Reply { Text = (string)item };
        }
    }

    internal static class Rig
    {
        public static readonly string Done = "{\"done\":true}";
        public static string Call(string tool, object args) => new JObject { ["tool"] = tool, ["args"] = JObject.FromObject(args) }.ToString(Formatting.None);
        public static string Final(string text) => new JObject { ["final"] = text }.ToString(Formatting.None);

        public static async Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld w, object[] script = null, bool playing = true, Action<MindConfig> cfg = null)
        {
            cfg?.Invoke(w.Cfg);
            w.Cfg.Wants = false;                         // the shared tool backend is the steward's alone in these tests
            w.Fb.Digest = (JObject)StewardTestData.Sample.DeepClone();
            await w.StartAsync();
            var fake = new StewardFake(script ?? new object[] { Done });
            w.ToolBackend = fake; w.ToolMode = "grammar";
            w.Fb.Playing = false;
            await w.Steward.StepAsync();                 // first sight: the chat cursor starts here
            w.Fb.Playing = playing;
            return (w, w.Steward, fake);
        }

        /// <summary>Director.step as the steward sees it: poll the events (observe them), and on a save-epoch change reset the steward and
        /// stop (Director.reload_minds); else steward.step. Then Director.drain.</summary>
        public static async Task Cycle(JobsWorld w)
        {
            await w.PollEventsAsync();
            if (w.Client.EpochChanged) { w.Client.EpochChanged = false; w.Steward.Reset(); }
            else await w.Steward.StepAsync();
            await w.DrainAsync();
        }

        public static JObject Chat(JobsWorld w, string text, int to = 0, string name = "") => w.Fb.AddChat("player", text, to, name);
        public static List<string> Lines(JobsWorld w, string who) => w.Fb.Chat.Where(e => (string)e["who"] == who).Select(e => (string)e["text"]).ToList();
        public static string Json(StewardFake.CallRec c) => new JArray(c.Messages).ToString(Formatting.None);
        public static string System(StewardFake.CallRec c) => (string)c.Messages[0]["content"];
        public static string User(StewardFake.CallRec c) => (string)c.Messages[1]["content"];
        public static bool Same(string json, JToken got) => StewardTestData.Same(JToken.Parse(json), got);
        public static JObject Ev(JobsWorld w, string type, string summary, double severity, JObject data = null, params (int, string)[] actors) =>
            w.Fb.AddEvent(type, actors.Select(a => new KeyValuePair<int, string>(a.Item1, a.Item2)).ToList(), summary, new List<int>(), severity, null, data);
        public static void NoEnv() => Environment.SetEnvironmentVariable(Steward.EnvStrategy, null);
    }

    [Collection("AdultState")]
    public class StewardTests
    {
        private static readonly string Done = Rig.Done;
        private static string Call(string t, object a) => Rig.Call(t, a);
        private static string Final(string t) => Rig.Final(t);
        private static Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld w, object[] script = null, bool playing = true, Action<MindConfig> cfg = null) => Rig.Boot(w, script, playing, cfg);
        private static Task Cycle(JobsWorld w) => Rig.Cycle(w);
        private static JObject Chat(JobsWorld w, string text, int to = 0, string name = "") => Rig.Chat(w, text, to, name);
        private static List<string> Lines(JobsWorld w, string who) => Rig.Lines(w, who);

        // ---------------------------------------------------------------- wakes
        [Fact]
        public async Task ChatWakesAtOnceAndIsAnswered()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("Cooking is already first for Ana.") });
            w.Fb.Playing = false;                    // advisor mode: chat wakes, nothing else does
            Chat(w, "who cooks?");
            await Cycle(w);
            Assert.Equal(new[] { "Cooking is already first for Ana." }, Lines(w, "narrator"));
            Assert.Contains("who cooks?", Rig.User(fake.Calls[0]));
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 1 }, st.Stats.ByReason);
        }

        [Fact]
        public async Task OldHistoryAndNamedLinesAreNotAnswered()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            await w.Steward.StepAsync();                                    // first sight of the new world: answered lines are history
            await w.DrainAsync();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world);
            w.Fb.Tick += 100;
            Rig.Ev(w, etype, "a " + etype, severity, JObject.Parse(data));
            // a heartbeat is not due (the first cycle ran at the first playing step), so the wake is the event
            await Cycle(w);                          // heartbeat first (no cycle yet)
            fake.Calls.Clear();
            w.Real += 30;
            Rig.Ev(w, etype, "a " + etype + " again", severity, JObject.Parse(data));
            await Cycle(w);
            Assert.True(st.Stats.ByReason.TryGetValue("event", out int n) && n == 1, JsonConvert.SerializeObject(st.Stats.ByReason));
            Assert.Contains("Something important happened", Rig.User(fake.Calls[0]));
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var data = JObject.Parse(dataJson);
            var (w, st, fake) = await Boot(world);
            await Cycle(w);                          // the opening heartbeat cycle
            fake.Calls.Clear();
            w.Real += 30;
            Rig.Ev(w, etype, "queued " + etype, severity, data);
            await Cycle(w);
            Assert.Empty(fake.Calls);                // no wake
            w.Fb.Tick += 6 * 2500 + 10;              // the heartbeat comes; the event rides along
            w.Real += 30;
            await Cycle(w);
            string user = Rig.User(fake.Calls[0]);
            bool animal = data["animal"] != null && (bool)data["animal"];
            Assert.True(user.Contains("queued " + etype) == !animal || etype == "death");
        }

        [Fact]
        public async Task HeartbeatCountsGameTicksAndRespectsTheRealFloor()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world);
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 1 }, st.Stats.ByReason);      // the first look when it starts playing
            w.Fb.Tick += 6 * 2500 - 100;
            w.Real += 60;
            await Cycle(w);
            Assert.Equal(1, st.Stats.Cycles);                                  // not yet 6 hours
            w.Fb.Tick += 200;
            w.Real += 5;                                                       // ticks are due but the real floor (20 s) is not
            await w.Steward.StepAsync();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world);
            await Cycle(w);
            w.Fb.Paused = true;
            w.Fb.Tick += 10 * 2500;
            w.Real += 60;
            Rig.Ev(w, "raid_start", "raid", 0.9);
            await Cycle(w);
            Assert.Equal(1, st.Stats.Cycles);
            Chat(w, "what now?");
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["heartbeat"] = 1, ["chat"] = 1 }, st.Stats.ByReason);
        }

        [Fact]
        public async Task NotPlayingOnlyChatWakes()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, playing: false);
            w.Fb.Tick += 50 * 2500;
            w.Real += 100;
            Rig.Ev(w, "raid_start", "raid", 0.9);
            await Cycle(w);
            Assert.True(fake.Calls.Count == 0 && st.Events.Count == 0 && st.Urgent.Count == 0);
            Chat(w, "hi");
            await Cycle(w);
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 1 }, st.Stats.ByReason);
            Assert.Contains("ADVISOR MODE", Rig.System(fake.Calls[0]));
        }

        [Fact]
        public async Task ChatIgnoresTheFloorAndOneCycleAtATime()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("first"), Final("second") });
            w.Fb.Playing = false;
            fake.Gate = new TaskCompletionSource<bool>();
            Chat(w, "one");
            await w.Steward.StepAsync();
            await Task.Delay(50);
            Assert.True(st.Active());
            Chat(w, "two");                                    // arrives mid-cycle
            await w.Steward.StepAsync();
            Assert.True(st.Stats.Cycles == 0 && st.PendingChat.Count == 1);
            fake.Gate.SetResult(true);
            await w.DrainAsync();
            Assert.Equal(1, st.Stats.Cycles);
            await Cycle(w);                                    // no real time passed: chat ignores the floor
            Assert.Equal(new Dictionary<string, int> { ["chat"] = 2 }, st.Stats.ByReason);
            Assert.Contains("two", Rig.User(fake.Calls[fake.Calls.Count - 1]));
            Assert.Equal(new[] { "first", "second" }, Lines(w, "narrator"));
        }

        [Fact]
        public async Task TheNarratorsOwnPauseWakesItOnce()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, playing: false);
            w.Fb.Requests.Clear();
            await w.Steward.StepAsync();
            Assert.Equal(new[] { new KeyValuePair<string, string>("GET", "/vivarium") }, w.Fb.Requests.ToArray());
        }

        // ---------------------------------------------------------------- tools reach the bridge
        [Fact]
        public async Task AdvisorModeDoesOneTaskAndSaysSo()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1 }),
                Call("assign_bed", new { pawn = "Eli", bed_id = "3305" }),          // a second task: refused in advisor mode
                Final("Ana cooks first now. I stopped there.") });
            w.Fb.Playing = false;
            Chat(w, "put Ana on cooking");
            await Cycle(w);
            Assert.Single(w.Fb.ActLog);
            Assert.Equal("work", w.Fb.ActLog[0].Key);
            Assert.True(Rig.Same("{\"pawn\":101,\"priorities\":{\"Cooking\":1}}", w.Fb.ActLog[0].Value));
            Assert.Contains("one task only", Rig.Json(fake.Calls[2]));
            Assert.Equal(new[] { "Ana cooks first now. I stopped there." }, Lines(w, "narrator"));
            Assert.Empty(Lines(w, "note"));                                          // the game writes the greentext line itself
            var log = (JArray)w.Fb.GoalsBoard["log"];
            Assert.StartsWith("Asked (put Ana on cooking)", (string)log[log.Count - 1]["text"]);
        }

        [Fact]
        public async Task PlayingRunsManyActionsWithResolvedIds()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Assert.True(Rig.Same("{\"pawn\":101,\"priorities\":{\"Cooking\":1}}", got["work"]));
            Assert.True(Rig.Same("{\"pawn\":105,\"bed\":3305}", got["bed"]));
            Assert.True(Rig.Same("{\"letter\":77,\"choice\":\"Accept\"}", got["letter"]));
            Assert.True(Rig.Same("{\"pawns\":[102,104],\"drafted\":true}", got["draft"]));
            Assert.True(Rig.Same("{\"project\":\"Batteries\"}", got["research"]));
            Assert.True(Rig.Same("{\"pawn\":102,\"order\":\"move\",\"x\":40,\"z\":61}", got["order"]));
            Assert.True(Rig.Same("{\"pawn\":103,\"kind\":\"food\",\"name\":\"Simple\"}", got["policy"]));
            Assert.Empty(Lines(w, "note"));                                          // no automatic notes after acts
        }

        [Fact]
        public async Task OwnNoteReplacesAutomaticNotes()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("assign_bed", new { pawn = "Eli", bed_id = "3305" }),
                Call("note", new { text = "Eli has a bed now" }),
                Final("Done with beds.") });
            await Cycle(w);
            Assert.Single(w.Fb.ActLog);
            Assert.Equal("bed", w.Fb.ActLog[0].Key);
            Assert.True(Rig.Same("{\"pawn\":105,\"bed\":3305}", w.Fb.ActLog[0].Value));
            Assert.Equal(new[] { "Eli has a bed now" }, Lines(w, "note"));           // no "→ bed 3305 ..." doubled on top
        }

        [Fact]
        public async Task ScheduleAttackHoldAndTimeBodies()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_schedule", new { pawn = "Dara", from_hour = 22, to_hour = 6, assignment = "sleep" }),
                Call("order", new { pawn = "Bram", order = "attack", x = 0, z = 0, target_id = 5512, hold = "no" }),
                Call("order", new { pawn = "Bram", order = "hold", x = 0, z = 0, target_id = 0, hold = "yes" }),
                Call("set_time", new { pause = "yes", speed = "leave", emergency = "yes", why = "since raiders are at the gate" }),
                Done });
            await Cycle(w);
            var got = w.Fb.ActLog;
            Assert.Equal("schedule", got[0].Key);
            Assert.True(Rig.Same("{\"pawn\":104,\"from_hour\":22,\"to_hour\":6,\"assignment\":\"sleep\"}", got[0].Value));
            Assert.Equal("order", got[1].Key);
            Assert.True(Rig.Same("{\"pawn\":102,\"order\":\"attack\",\"target\":5512}", got[1].Value));
            Assert.Equal("order", got[2].Key);
            Assert.True(Rig.Same("{\"pawn\":102,\"order\":\"hold\",\"hold\":true}", got[2].Value));
            Assert.Equal("time", got[3].Key);
            var t = got[3].Value;
            Assert.True((bool)t["pause"] && (bool)t["emergency"] && t["speed"] == null);
            Assert.True((string)t["why"] == "since raiders are at the gate" && t["reason"] == null);
            Assert.True(w.Fb.PausedByNarrator);
        }

        [Fact]
        public async Task ARefusalGoesBackToTheModelAndIsNotCountedAsDone()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_time", new { pause = "yes", speed = "leave", emergency = "no", why = "since the colony needs rest" }),
                Final("The game would not let me pause.") });
            w.Fb.ActRefuse["time"] = "pausing needs an emergency";
            await Cycle(w);
            Assert.Contains("pausing needs an emergency", Rig.Json(fake.Calls[1]));
            Assert.True(w.Fb.ActLog.Count == 0 && st.Stats.Refused == 1 && st.Stats.Acts == 0);
            Assert.Equal(new[] { "The game would not let me pause." }, Lines(w, "narrator"));
            Assert.Equal(0, (int)w.Fb.GoalsBoard["rev"]);                            // nothing done: no board write, no log line
        }

        [Fact]
        public async Task UnknownNamesAndPoliciesComeBackAsErrors()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] {
                Call("set_policy", new { pawn = "Ana", kind = "food", name = "Gourmet" }),
                Call("set_research", new { project = "Warp drive" }),
                Call("draft", new { pawns = "Zed", drafted = "yes" }), Done });
            await Cycle(w);
            string last = Rig.Json(fake.Calls[fake.Calls.Count - 1]);
            Assert.Contains("no food policy named Gourmet", Rig.Json(fake.Calls[1]));
            Assert.Contains("no available project named Warp drive", Rig.Json(fake.Calls[2]));
            Assert.True(last.Contains("no colonist named Zed") && w.Fb.ActLog.Count == 0);
        }

        [Fact]
        public async Task TheLoopRunsOnSlotZeroWithThePlaybookAndDigest()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            await Cycle(w);
            var c = fake.Calls[0];
            Assert.Equal(0, c.Slot);
            Assert.Equal(0, w.Cfg.NarratorSlot);
            string sysm = Rig.System(c), user = Rig.User(c);
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final(text) });
            await Cycle(w);
            Assert.True(Lines(w, "narrator").Count == 0 && st.Stats.Cycles == 1);
        }

        [Fact]
        public async Task ALinePairingAMinorWithRomanceIsNotPosted()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("Eli, a kiss under the stars, my darling?") });
            w.Ages[105] = 15;
            w.NamesD[105] = "Eli"; w.TokensD[105] = new HashSet<string> { "eli" };
            await Cycle(w);
            Assert.Empty(Lines(w, "narrator"));
        }

        [Fact]
        public async Task ADeadBackendOnAChatGetsAPlainReply()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { "not json at all" });
            w.Fb.Playing = false;
            Chat(w, "hello?");
            await Cycle(w);
            Assert.Contains("couldn't think that through", Lines(w, "narrator")[0]);
            Chat(w, "again");
            w.ToolBackend = null;
            await Cycle(w);                                    // no backend: the steward is simply off
            Assert.Single(Lines(w, "narrator"));
        }

        [Fact]
        public async Task DigestUnreadableOnAChatSaysSo()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            world.Fb.AddChat("player", "answered earlier");
            world.Fb.AddChat("narrator", "an earlier answer");
            world.Fb.AddChat("player", "typed while the agent was restarting");
            var (w, st, fake) = await Boot(world, new object[] { Final("Here now.") }, playing: false);
            await Cycle(w);
            var narr = Lines(w, "narrator");
            Assert.Equal("Here now.", narr[narr.Count - 1]);
            Assert.Contains("typed while the agent was restarting", Rig.User(fake.Calls[0]));
        }

        [Fact]
        public async Task SwitchingOnWakesTheStewardAtOnce()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            var sess = st.Kit.Session(w.Fb.Digest, w.Client, false);
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
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("set_work", new { pawn = "Ana", work = "Cooking", priority = 1, why = "since " + new string('x', 70) }), Done });
            await Cycle(w);
            string why = (string)w.Fb.ActLog[0].Value["why"];
            Assert.True(why.EnumerateRunes().Count() <= 80);
        }

        [Fact]
        public async Task OnlyPlainPlayerLinesAreAnswered()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Final("Heard you.") }, playing: false);
            foreach (var who in new[] { "colonist", "thought", "diary", "action", "refused", "narrator", "art" })
                w.Fb.AddChat(who, "I am not the player talking to you");
            w.Fb.AddChat("player", "Ana, how is the stew?", 101, "Ana");
            await Cycle(w);
            Assert.Empty(fake.Calls);                                // nothing woke the steward
            w.Fb.AddChat("player", "narrator, what is up?");
            await Cycle(w);
            Assert.True(fake.Calls.Count >= 1);
            string user = Rig.User(fake.Calls[0]);
            Assert.True(user.Contains("narrator, what is up?") && !user.Contains("how is the stew") && !user.Contains("not the player"));
        }
    }
}

// The deferred "world" tests of the jobs modules (art, dreams, lore, recap, chronicle/diary/pacing, text fill), ported from
// agent/tests/test_art.py, test_dreams.py, test_lore.py, test_recap.py, test_diary_chronicle_pacing.py and test_textfill.py over the
// World fixture (Mind/Fakes/World.cs): a real Director over a FakeBridge, a FakeLlm and the RimWorld adapter.
//
// Python monkeypatches map like this: GatedLLM subclasses are HookLlm.BeforeKind (an awaited gate); d.idle / d.leases calls that run
// outside d.step go through OnLoop (the director's loop, as asyncio ran them); the private Director members the Python tests read
// (_common_talk, _cover_allowed, _last_cover) are reached by reflection.
// One difference everywhere a Python GatedLLM counts `cancelled`: no token reaches ILlmClient, so a preempted job's call stays pending
// (detached) and its result is discarded (parity-idle.md). The tests assert the observable half (job ended, window released, nothing applied)
// and, after the gate opens, that the late result applied nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindT = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class JobsWorldTests
    {
        const int Day = 60000;
        static KeyValuePair<int, string>[] Who(params (int, string)[] a) { return a.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2)).ToArray(); }
        static async Task Step(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }
        static async Task Step2(World w) { await Step(w); await Step(w); }
        static Task OnLoop(World w, Action a) { return w.D.RunOnLoop(() => { a(); return Task.CompletedTask; }); }
        static Task<T> OnLoop<T>(World w, Func<T> f) { return w.D.RunOnLoop(() => Task.FromResult(f())); }

        static async Task WaitFor(Func<bool> cond, int ms = 3000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!cond() && DateTime.UtcNow < until) await Task.Delay(5);
            Assert.True(cond(), "timed out waiting");
        }

        static void Inject(World w, int aid, string text, long game, string visibility = "public", params int[] people)
        {
            var m = w.D.Minds[aid];
            m.Memories.Add(new Memory { Id = m.NextMemId(), Game = game, Text = text, Visibility = visibility, People = people.ToList() });
        }

        static List<FakeCall> CallsOf(World w, string kind) { return w.Llm.Calls.Where(c => c.Kind == kind).ToList(); }
        static string UserOf(FakeCall c) { return c.Messages[1].Text; }

        /// <summary>The Python GatedLLM: the listed kinds wait on a gate; `Started` fires when the first one is reached.</summary>
        sealed class Gate
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource<bool> _open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Reached;
            public Gate(World w, params string[] kinds)
            {
                w.Hook.BeforeKind = async kind =>
                {
                    if (!kinds.Contains(kind)) return;
                    Reached++;
                    Started.TrySetResult(true);
                    await _open.Task;
                };
            }
            public Task WaitStarted() { return Task.WhenAny(Started.Task, Task.Delay(3000)).ContinueWith(t => Assert.True(Started.Task.IsCompleted, "the gated call never started")); }
            public void Open() { _open.TrySetResult(true); }
        }

        // =============================================================================== test_art.py
        static readonly JObject ArtData = JObject.Parse("{\"thing_id\": 500, \"def_label\": \"large sculpture\", \"quality\": \"good\", \"title\": \"Quiet Dog\", \"description\": \"A sculpture of a dog, made by Ana.\"}");

        static JObject MakeArt(World w, int maker = 1, int thing = 500)
        {
            var d = (JObject)ArtData.DeepClone(); d["thing_id"] = thing;
            return w.Fb.AddEvent("art_created", Who((maker, "maker")), "Ana finished a large sculpture.", new[] { maker }, 0.2, data: d);
        }

        static JObject ArtEvent(World w)
        {
            return new JObject { ["actors"] = JArray.Parse("[{\"id\": 1, \"role\": \"maker\"}]"), ["data"] = ArtData.DeepClone(), ["game"] = w.Bridge.Env.Game };
        }

        [Fact]
        public async Task PreemptedArtJobIsPutBackAndNothingIsApplied()
        {
            using var w = new World(); await w.GoAsync();
            var gate = new Gate(w, "art");
            MakeArt(w);
            await w.D.StepAsync();
            await gate.WaitStarted();
            Assert.True(w.D.Idle.Active() && w.D.Idle.Kind == "art" && w.D.Leases.NarratorBusy);
            w.Fb.AddEvent("injury", Who((2, "victim")), "Bram was shot.", new[] { 2, 3 }, 0.9);
            await w.D.StepAsync();
            await WaitFor(() => !w.D.Idle.Active() && !w.D.Leases.NarratorBusy);
            Assert.Equal(new[] { 500 }, w.D.Art.Jobs.Select(j => j.Thing).ToArray());
            Assert.Empty(w.Fb.ArtLog);
            // the detached call finishes later: it applies nothing
            gate.Open();
            await w.D.DrainAsync(); await Task.Delay(50);
            Assert.Empty(w.Fb.ArtLog);
        }

        [Fact]
        public async Task APendingArtJobCountsAsIdleWorkDueAndCanStarve()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            await OnLoop(w, () =>
            {
                d.Idle.DueSince = null;
                Assert.False(d.Idle.AnythingDue());
                d.Art.Note(ArtEvent(w), d.Bridge.Env.Game);
                Assert.True(d.Idle.AnythingDue());
                Assert.False(d.Idle.Starving());                                       // just became due
                d.Bridge.Env.Game += (int)(d.Cfg.IdleStarvingFraction * d.Day) + 1;
                Assert.True(d.Idle.Starving());                                        // waited a share of a day: promoted
                d.Cfg.ArtEnabled = false;
                Assert.False(d.Idle.AnythingDue());                                    // off: never counts
                d.Cfg.ArtEnabled = true;
                d.Bridge.Env.Game += d.Day;                                            // expired jobs do not keep idle work "due"
                Assert.False(d.Idle.AnythingDue());
            });
        }

        [Fact]
        public async Task StarvedArtYieldsToReactiveWorkButNotToAmbientChatter()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            await OnLoop(w, () =>
            {
                d.Art.Note(ArtEvent(w), d.Bridge.Env.Game);
                d.Idle.DueSince = d.Bridge.Env.Game - d.Day;                          // long starved
                Assert.True(d.Idle.Starving());
                d.Leases.Queue.Add(d.Leases.MakeItem(1, "x", d.Now(), cls: Cls.Ambient));
                Assert.False(d.Idle.ActorsWantWindows(true));                         // starving art may run beside ambient talk
                d.Leases.Queue.Add(d.Leases.MakeItem(1, "x", d.Now(), cls: Cls.Reactive));
                Assert.True(d.Idle.ActorsWantWindows(true));                          // reactive and urgent still go first
            });
        }

        // =============================================================================== test_dreams.py
        const string DreamText = "I am chasing a berry bush through the kitchen";

        static async Task<World> DreamReady(double chance = 1.0)
        {
            var w = new World(); await w.GoAsync();
            w.D.Cfg.DreamChance = chance;
            w.Llm.Script["dream"] = new List<string> { DreamText };
            Inject(w, 1, "I found a ripe berry bush.", 900);
            return w;
        }

        [Fact]
        public async Task DreamIsPreemptedByActorWork()
        {
            using var w = await DreamReady();
            w.Fb.Pawns[1]["asleep"] = true;
            w.Fb.AddEvent("sleep_start", Who((1, "sleeper")), "Ana fell asleep.", new[] { 1 }, 0.1);
            w.Fb.AddEvent("injury", Who((2, "victim")), "Bram was shot.", new[] { 2 }, 0.9);
            await Step(w);
            // actor work ran; no dream beside it
            Assert.Empty(w.Fb.ThoughtLog.Where(t => (string)t["kind"] == "dream" && (int)t["pawn"] == 2));
        }

        // =============================================================================== test_lore.py
        const string Death = "Bram died of his wounds in the evening, and the others buried him without speeches.";

        static async Task LoreStep(World w) { w.Real += 30.0; await w.D.StepAsync(); await w.D.DrainAsync(); }

        static T Priv<T>(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            return (T)m.Invoke(o, args);
        }

        [Fact]
        public async Task LoreCoinedAfterAMajorEventStoredCachedAndReusedThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["chronicle"] = new List<string> { Death };
            w.Llm.Script["lore"] = new List<string> { "The Hollow Funeral: Bram's burial without speeches" };
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1, 3 }, 1.0);
            await LoreStep(w); await LoreStep(w);
            var calls = CallsOf(w, "lore");
            Assert.Single(calls);
            Assert.Equal(w.Cfg.NarratorSlot, calls[0].Slot);
            Assert.Equal(new[] { "The Hollow Funeral: Bram's burial without speeches" }, w.D.Lore.Lines().ToArray());
            var stored = w.Fb.Chronicle.Where(e => (string)e["kind"] == "lore").ToList();
            Assert.NotEmpty(stored);
            Assert.Equal("The Hollow Funeral: Bram's burial without speeches", (string)stored[0]["text"]);
            Assert.Single(w.Fb.Chronicle.Where(e => (string)e["kind"] == "major"));
            // a later chronicle prompt carries the names, with data rule wrapping
            w.Llm.Script["chronicle"] = new List<string> { Death };
            w.D.Chronicle.Note(w.D.Bridge.Env.Game, "Cy was hurt.", 1.0, true);
            w.Llm.Script["lore"] = new List<string> { "none" };
            await LoreStep(w); await LoreStep(w);
            string user = UserOf(CallsOf(w, "chronicle").Last());
            Assert.Contains("SHARED NAMES", user);
            Assert.Contains("The Hollow Funeral", user);
            // block D gets the matching names
            var sc = new Scene { Where = "the hall" };
            var funeral = Priv<List<string>>(w.D, "CommonTalk", 1, "the funeral of Bram", sc, new HashSet<int> { 1 }, (int?)null);
            Assert.StartsWith("Colony names: The Hollow Funeral", funeral[0]);
            Assert.Empty(Priv<List<string>>(w.D, "CommonTalk", 1, "supper", sc, new HashSet<int> { 1 }, (int?)null));
        }

        [Fact]
        public async Task LoreSurvivesInTheSaveAndReloadsThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 2, \"kind\": \"lore\", \"text\": \"Bram's Folly: the half-built tower\", \"tick\": 5}"));
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 3, \"kind\": \"day\", \"text\": \"A quiet day passed.\", \"tick\": 6}"));
            w.Fb.ReloadGame();
            await w.D.StepAsync();                              // the epoch changed: lore is read back from the mod
            Assert.Equal(new[] { "Bram's Folly: the half-built tower" }, w.D.Lore.Lines().ToArray());
        }

        [Fact]
        public async Task RecapIgnoresLoreEntriesThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 0, \"kind\": \"day\", \"text\": \"Ana dug the first trench.\", \"tick\": 1}"));
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 1, \"kind\": \"lore\", \"text\": \"Bram's Folly: the half-built tower\", \"tick\": 2}"));
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 2, \"kind\": \"lore\", \"text\": \"The Long Winter: the hungry months\", \"tick\": 3}"));
            w.Llm.Script["recap"] = new List<string> { "Ana dug the first trench.\nThe days went by.\nWork continued." };
            w.Fb.ReloadGame();
            await Step(w);
            Assert.Empty(CallsOf(w, "recap")); Assert.Empty(w.Fb.RecapLog);          // one real entry: no recap
            w.Fb.Chronicle.Add(JObject.Parse("{\"day\": 3, \"kind\": \"day\", \"text\": \"Cy was hurt by raiders.\", \"tick\": 4}"));
            w.Fb.ReloadGame();
            await Step(w);
            string user = UserOf(CallsOf(w, "recap")[0]);
            Assert.Contains("Ana dug", user); Assert.Contains("Cy was hurt", user);
            Assert.DoesNotContain("Folly", user); Assert.DoesNotContain("Winter", user);
        }

        // =============================================================================== test_recap.py (the Director's trigger)
        static readonly string[] RecapEntries =
        {
            "{\"day\": 1, \"kind\": \"day\", \"text\": \"Ana dug the first trench. Bram sulked.\", \"tick\": 1}",
            "{\"day\": 2, \"kind\": \"day\", \"text\": \"Raiders came and Cy was hurt. The wall held.\", \"tick\": 2}",
            "{\"day\": 3, \"kind\": \"major\", \"text\": \"Bram left the colony in a huff. Nobody followed.\", \"tick\": 3}",
        };
        const string RecapGood = "Ana dug the first trench.\nRaiders hit and Cy was hurt.\nBram left, and nobody knows why.";

        static void SetChronicle(World w) { w.Fb.Chronicle = RecapEntries.Select(JObject.Parse).ToList(); }

        static async Task<World> RecapLoad(World w)
        {
            await w.GoAsync();
            SetChronicle(w);
            w.Llm.Script["recap"] = new List<string> { RecapGood };
            w.Fb.ReloadGame();
            await w.D.StepAsync(); await w.D.DrainAsync();
            return w;
        }

        [Fact]
        public async Task RecapEpochChangeOneCallOnePostAndNoRepeatThroughTheDirector()
        {
            using var w = await RecapLoad(new World());
            await Step(w);                                          // same epoch again: nothing new
            Assert.Single(CallsOf(w, "recap"));
            Assert.Equal(w.Cfg.NarratorSlot, CallsOf(w, "recap")[0].Slot);
            Assert.Single(w.Fb.RecapLog);
            var body = w.Fb.RecapLog[0];
            Assert.Equal(w.Fb.Epoch, (int)body["epoch"]);
            Assert.Equal(RecapGood.Split('\n'), body["lines"].Select(x => (string)x).ToArray());
        }

        [Fact]
        public async Task RecapSecondLoadGetsItsOwnRecapThroughTheDirector()
        {
            using var w = await RecapLoad(new World());
            w.Llm.Script["recap"] = new List<string> { RecapGood };
            w.Fb.ReloadGame();
            await Step(w);
            Assert.Equal(new[] { 2, 3 }, w.Fb.RecapLog.Select(b => (int)b["epoch"]).ToArray());
        }

        [Fact]
        public async Task RecapOffByTheModsSettingThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Config["recap"] = false;
            SetChronicle(w);
            w.Fb.ReloadGame();
            await Step(w);
            Assert.Empty(CallsOf(w, "recap")); Assert.Empty(w.Fb.RecapLog);
        }

        [Fact]
        public async Task RecapOffByTheLocalSwitchThroughTheDirector()
        {
            using var w = new World(c => c.RecapEnabled = false);
            await RecapLoad(w);
            Assert.Empty(CallsOf(w, "recap")); Assert.Empty(w.Fb.RecapLog);
        }

        [Fact]
        public async Task LateConnectRecapsWhenTheModSaysOpenThroughTheDirector()
        {
            using var w = new World();
            SetChronicle(w);
            w.Llm.Script["recap"] = new List<string> { RecapGood };
            w.Fb.Config["recap_open"] = true;
            await w.GoAsync();
            await w.D.DrainAsync();
            Assert.Single(CallsOf(w, "recap")); Assert.Single(w.Fb.RecapLog);
            Assert.Equal(w.Fb.Epoch, (int)w.Fb.RecapLog[0]["epoch"]);
        }

        [Fact]
        public async Task LateConnectDoesNothingWhenClosedOrAlreadyTriedThroughTheDirector()
        {
            using var w = new World();
            SetChronicle(w);
            w.Llm.Script["recap"] = new List<string> { RecapGood };
            await w.GoAsync();                                       // recap_open absent: false
            await w.D.DrainAsync();
            Assert.Empty(w.Fb.RecapLog);
            w.Fb.Config["recap_open"] = true;
            w.D.RecapEpoch = w.Fb.Epoch;                             // already attempted for this epoch
            Assert.False(Recap.LateDue(w.D));
            w.D.RecapEpoch = 0;
            await w.D.RefreshConfigAsync();
            Assert.True(Recap.LateDue(w.D));
            w.D.Cfg.RecapEnabled = false;
            Assert.False(Recap.LateDue(w.D));
        }

        /// <summary>Not in Python (its twin is test_steward_more.test_recap_waits_for_the_cycle): the recap waits while a steward cycle owns slot 0.</summary>
        [Fact]
        public async Task RecapWaitsWhileTheNarratorSlotIsOwned()
        {
            using var w = new World(); await w.GoAsync();
            SetChronicle(w);
            w.Llm.Script["recap"] = new List<string> { RecapGood };
            w.D.Leases.NarratorOwner = "hold";                    // not "steward": a reload resets that one (Steward.Reset, as in Python)
            w.Fb.ReloadGame();
            await w.D.StepAsync();
            await Task.Delay(700);
            Assert.Empty(CallsOf(w, "recap"));
            w.D.Leases.NarratorOwner = null;
            await WaitFor(() => w.Fb.RecapLog.Count == 1, 4000);
            await w.D.DrainAsync();
            Assert.Single(CallsOf(w, "recap"));
        }

        // =============================================================================== test_diary_chronicle_pacing.py
        const string Diary = "Today I kept my head down and got the stew finished before dark.";
        const string Chron = "Ana was shot and the colonists patched her up. Nobody slept well, and the food ran lower.";

        [Fact]
        public async Task DiaryOncePerActorPerDayLateInTheDayAndSentAsADiaryThought()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["diary"] = new List<string> { Diary, Diary.Replace("stew", "soup"), Diary };
            Inject(w, 1, "I saw Bram eat alone.", 5000);
            w.Fb.Tick = (int)(0.3 * Day);
            await Step(w);
            Assert.Empty(w.D.Minds[1].Diary);                                   // too early in the day
            w.Fb.Tick = (int)(0.7 * Day);
            await Step(w);
            Assert.Equal(new[] { 0 }, w.D.Minds[1].Diary.Select(e => e.Day).ToArray());
            Assert.True(w.Fb.ThoughtLog.Count > 0 && (string)w.Fb.ThoughtLog[0]["kind"] == "diary" && (int)w.Fb.ThoughtLog[0]["pawn"] == 1);
            Assert.True((string)w.Fb.ThoughtLog[0]["text"] == Diary && !string.IsNullOrEmpty((string)w.Fb.ThoughtLog[0]["op_id"]));
            int n = w.Fb.ThoughtLog.Count;
            await Step(w); await Step(w);
            Assert.Equal(n, w.Fb.ThoughtLog.Count);                             // once per day
            Assert.Empty(w.D.Minds[2].Diary);                                   // nothing happened to Bram's memory
        }

        [Fact]
        public async Task ADiaryOpeningLikeARecentOneIsDropped()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Minds[1].AddDiary(0, 100, "Today I kept my head down and got the stew finished. Nothing else happened.");
            w.D.Minds[1].LastDiaryDay = -1;
            w.Llm.Script["diary"] = new List<string> { "Today I kept my head down and got the soup finished. Nobody ate alone again." };
            Inject(w, 1, "I mended the east wall.", 5000);
            w.Fb.Tick = (int)(0.7 * Day);
            await Step(w);
            Assert.Contains((1, "diary:repeat"), w.D.Dropped);
            Assert.Single(w.D.Minds[1].Diary);
        }

        [Fact]
        public async Task DiaryLeadsWithRealEventsAndLeavesRoutineTalkOut()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Minds[1].Memories.Add(new Memory { Id = "s1", Game = 4000, Etype = "social", Importance = 0.9, People = new List<int> { 2 },
                Text = "I was part of it: Ana and Bram had a passing exchange while going about their day; what was said wasn't noted." });
            w.D.Minds[1].Memories.Add(new Memory { Id = "s2", Game = 4500, Etype = "conversation", Importance = 0.9, Text = "I talked with Bram. He started it." });
            Inject(w, 1, "I saw the east wall burn down.", 5000);
            w.Llm.Script["diary"] = new List<string> { Diary };
            w.Fb.Tick = (int)(0.7 * Day);
            await Step(w);
            var c = w.Llm.Calls.First(x => x.Kind == "diary" && x.Actor == 1);
            string u = c.Messages[1].Text;
            Assert.Contains("east wall burn down", u);
            Assert.DoesNotContain("passing exchange", u);
            Assert.DoesNotContain("I talked with Bram", u);
        }

        [Fact]
        public async Task AllRoutineTalkBecomesOnePlainLineInTheDiaryPrompt()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Minds[1].Memories.Add(new Memory { Id = "s1", Game = 4000, Etype = "social", Text = "I was part of it: Ana and Bram had a passing exchange." });
            w.D.Minds[1].Memories.Add(new Memory { Id = "s2", Game = 4500, Etype = "conversation", Text = "I talked with Bram. He started it." });
            w.Llm.Script["diary"] = new List<string> { Diary };
            w.Fb.Tick = (int)(0.7 * Day);
            await Step(w);
            string u = w.Llm.Calls.First(x => x.Kind == "diary" && x.Actor == 1).Messages[1].Text;
            Assert.Contains("Nothing out of the ordinary happened", u);
            Assert.DoesNotContain("passing exchange", u);
        }

        [Fact]
        public async Task ADiaryThatIsAboutTheChatIsDropped()
        {
            using var w = new World(); await w.GoAsync();
            Inject(w, 1, "I mended a wall.", 5000);
            w.Llm.Script["diary"] = new List<string> { "Today I had a long conversation. Nothing else happened at all." };
            w.Fb.Tick = (int)(0.7 * Day);
            await Step(w);
            Assert.Contains((1, "diary:meta_talk"), w.D.Dropped);
            Assert.Empty(w.D.Minds[1].Diary);
        }

        [Fact]
        public async Task DiaryKeepsLastSevenAndIsSavedInTheBlob()
        {
            using var w = new World(); await w.GoAsync();
            for (int day = 0; day < 9; day++)
            {
                Inject(w, 1, "I did chore number " + day + ".", day * Day + 100);
                w.Fb.Tick = day * Day + (int)(0.8 * Day);
                w.Llm.Script["diary"] = new List<string> { "Day " + day + " went by. I kept my head down and got the stew finished before dark." };   // distinct openings: a repeated one is dropped
                await Step(w);
            }
            await Step(w);
            Assert.Equal(Enumerable.Range(2, 7).ToArray(), w.D.Minds[1].Diary.Select(e => e.Day).ToArray());
            var blob = w.Fb.Minds[1].Key;
            Assert.Equal(7, ((JArray)blob["diary"]).Count);
            Assert.Equal(8, (int)blob["last_diary_day"]);
        }

        [Fact]
        public async Task DiaryForYesterdayIsWrittenEarlyNextDayAndBadTextIsDropped()
        {
            using var w = new World(); await w.GoAsync();
            Inject(w, 1, "I mended a wall.", 100);
            w.Fb.Tick = Day + 500;                                              // early day 1: yesterday's entry
            w.Llm.Script["diary"] = new List<string> { "As an AI language model I have no day to write about.", Diary };
            await Step(w);
            Assert.Empty(w.D.Minds[1].Diary);
            Assert.Contains((1, "diary:refusal"), w.D.Dropped);
            w.Real += 100;
            await Step(w);
            Assert.Equal(new[] { 0 }, w.D.Minds[1].Diary.Select(e => e.Day).ToArray());
        }

        [Fact]
        public async Task DiaryCanBeTurnedOff()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.DiaryEnabled = false;
            Inject(w, 1, "I mended a wall.", 100);
            w.Fb.Tick = (int)(0.9 * Day);
            await Step(w);
            Assert.Empty(w.D.Minds[1].Diary);
            Assert.Empty(CallsOf(w, "diary"));
        }

        [Fact]
        public async Task ChronicleOneParagraphPerDaySkipsWhenNothingNew()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["chronicle"] = new List<string> { Chron };
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana was shot.", new[] { 1 }, 0.6);
            await Step(w);
            Assert.DoesNotContain(w.D.Idle.Log, x => (string)x["kind"] == "chronicle");      // same day: wait
            w.Fb.Tick = Day + 100;
            await Step(w);
            var ch = w.D.Idle.Log.Where(x => (string)x["kind"] == "chronicle").ToList();
            Assert.Single(ch);
            Assert.Equal(0, (int)ch[0]["day"]); Assert.Equal(Chron, (string)ch[0]["text"]);
            var calls = CallsOf(w, "chronicle");
            Assert.Equal(0, calls[0].Slot);                                                  // slot 0, the narrator
            Assert.Contains("Ana was shot.", UserOf(calls[0]));
            w.Fb.Tick = 2 * Day + 100;
            await Step(w); await Step(w);
            Assert.Single(CallsOf(w, "chronicle"));                                          // nothing new: skipped
            string md = File.ReadAllText(Path.Combine(w.Cfg.DataDir, "chronicle.md"));
            Assert.Contains("## Day 1", md); Assert.Contains(Chron, md);
            var st = JObject.Parse(File.ReadAllText(Path.Combine(w.Cfg.DataDir, "chronicle.json")));
            Assert.Equal(Chron, (string)st["recent"][0]["text"]);
            Assert.Empty((JArray)st["pending"]);
        }

        [Fact]
        public async Task ChronicleOnDeathAtOnceAndNeverProposesEvents()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["chronicle"] = new List<string> { Death };
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1, 3 }, 1.0);
            await Step(w); await Step(w);                                                    // the actors speak first, then the narrator
            var ch = w.D.Idle.Log.Where(x => (string)x["kind"] == "chronicle").ToList();
            Assert.Single(ch);
            Assert.Equal("major", (string)ch[0]["entry_kind"]);
            string sys = CallsOf(w, "chronicle")[0].Messages[0].Text;
            Assert.Contains("never", sys); Assert.Contains("propose", sys);
            Assert.True(w.Fb.ChronicleLog.Count > 0 && (string)w.Fb.ChronicleLog[0]["kind"] == "major");   // stored through the bridge, not as speech
            Assert.Empty(w.Fb.SayLog.Where(b => b["pawn"] != null && (int)b["pawn"] == 0));
        }

        [Fact]
        public async Task ChronicleContextStaysUnder6000TokensAndRollsIntoASummary()
        {
            using var w = new World(); await w.GoAsync();
            string lng = string.Join(" ", Enumerable.Repeat("The colonists endured the cold and the hunger and the long shifts at the field", 3)) + ".";
            string summary = string.Concat(Enumerable.Repeat("Earlier the colonists endured cold, hunger and long shifts. ", 4));
            for (int day = 0; day < 12; day++)
            {
                for (int k = 0; k < 25; k++)
                {
                    w.Fb.Tick = day * Day + 100 + k;
                    w.Fb.AddEvent("injury", Who((1, "victim")), "Ana took wound " + k + " on day " + day + ": " + new string('x', 80), new[] { 1 }, 0.6);
                }
                w.Fb.Tick = (day + 1) * Day + 5;
                w.Llm.Script["chronicle"] = new List<string> { lng };
                w.Llm.Script["chronicle_summary"] = new List<string> { summary };
                await Step(w); await Step(w);
            }
            var calls = CallsOf(w, "chronicle");
            Assert.True(calls.Count >= 10);
            Assert.True(calls.Max(c => Chronicle.MessagesTokens(c.Messages)) <= 6000);
            var st = w.D.Chronicle.State;
            Assert.True(st.Recent.Count <= 3 && !string.IsNullOrEmpty(st.Summary) && w.Llm.Calls.Any(c => c.Kind == "chronicle_summary"));
            Assert.True(st.Summary.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length <= 400);
            Assert.Contains("Earlier the colonists", UserOf(calls.Last()));                   // the summary feeds the next entry
        }

        [Fact]
        public async Task ChronicleEntryCanGoToTheBridgeWhenEnabled()
        {
            using var w = new World(); await w.GoAsync();
            Assert.True(w.D.Cfg.ChronicleBridge);                                             // on by default
            w.Llm.Script["chronicle"] = new List<string> { Death };
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1 }, 1.0);
            await Step(w); await Step(w);
            Assert.True(w.Fb.ChronicleLog.Count > 0 && (int)w.Fb.ChronicleLog[0]["day"] == 0 && (string)w.Fb.ChronicleLog[0]["kind"] == "major");
            var got = await w.D.Bridge.GetChronicleAsync(0);                                  // GET /chronicle?since_day
            Assert.Equal(new[] { (0, "major") }, got.Select(e => ((int)e["day"], (string)e["kind"])).ToArray());
            Assert.Contains("Bram", (string)got[0]["text"]);
            Assert.Empty(await w.D.Bridge.GetChronicleAsync(1));
        }

        // ---- step 6: pacing
        [Fact]
        public async Task IdleJobIsCancelledTheMomentAnActorWindowIsWanted()
        {
            using var w = new World();
            var gate = new Gate(w, "reflect", "diary", "chronicle");
            await w.GoAsync();
            var mind = w.D.Minds[1];
            mind.UnreflectedImportance = 9.0;
            await w.D.StepAsync();
            await gate.WaitStarted();
            Assert.True(w.D.Idle.Active() && w.D.Leases.Leases.Count > 0 && w.D.Leases.WindowsIdle());
            w.Fb.AddEvent("injury", Who((2, "victim")), "Bram was shot.", new[] { 2, 3 }, 0.9);
            await w.D.StepAsync();                                                           // the event is queued: idle yields now
            await WaitFor(() => !w.D.Idle.Active());
            Assert.Equal(1, gate.Reached);
            Assert.Contains((1, "idle_preempted:reflect"), w.D.Dropped);
            await w.D.DrainAsync();
            Assert.True(mind.EvolvingBio == "" && mind.UnreflectedImportance == 9.0);       // nothing was applied
            Assert.NotEmpty(w.Fb.SayLog);                                                    // the actor window spoke instead
            gate.Open();                                                                     // the detached call finishes: still nothing applied
            await w.D.DrainAsync(); await Task.Delay(50);
            Assert.True(mind.EvolvingBio == "" && mind.UnreflectedImportance == 9.0);
        }

        [Fact]
        public async Task IdleWorkNeverStartsBesideLiveWork()
        {
            using var w = new World();
            var gate = new Gate(w, "reflect", "diary", "chronicle");
            await w.GoAsync();
            var d = w.D;
            d.Minds[1].UnreflectedImportance = 9.0;
            Inject(w, 2, "I mended a wall.", 100);
            w.Fb.Tick = (int)(0.9 * Day);
            // a live conversation lease and a queued item: no idle job may start
            Lease lease = await OnLoop(w, () => d.Leases.Acquire(d.Leases.MakeItem(3, "social", d.Now(), cls: Cls.Social), d.Now()));
            await d.StepAsync();
            Assert.False(d.Idle.Active());
            Assert.Empty(w.Llm.Calls);
            await OnLoop(w, () =>
            {
                d.Leases.Release(lease);
                d.Leases.Queue.Add(d.Leases.MakeItem(3, "injury", d.Now()));
                Assert.False(d.Leases.NarratorMayDoIdleWork());
                d.Idle.RunOnce();
                Assert.False(d.Idle.Active());
                d.Leases.Queue.Clear();
            });
            await d.StepAsync();
            await gate.WaitStarted();
            Assert.True(d.Idle.Active());
            Assert.Equal(1, d.TaskCount);                                                    // one idle job at a time
            gate.Open();
            await d.DrainAsync();
        }

        [Fact]
        public async Task ChronicleJobYieldsToAPlayerTalkRequest()
        {
            using var w = new World();
            var gate = new Gate(w, "reflect", "diary", "chronicle");
            await w.GoAsync();
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1 }, 1.0);
            w.D.Cfg.Quotas.ActorPerDay = 99;
            // keep the actors quiet this step so only the narrator has work
            await w.D.RunOnLoop(async () =>
            {
                await w.D.IngestAsync(await w.D.Events.PollAsync());
                w.D.Leases.Queue.Clear();
                w.D.Leases.NarratorBusy = false;
                w.D.Idle.RunOnce();
            });
            await gate.WaitStarted();
            Assert.True(w.D.Idle.Kind == "chronicle" && w.D.Leases.NarratorBusy);
            w.Fb.TalkQueue.Add(new JObject { ["id"] = "t1", ["pawn"] = 1, ["text"] = "Hello?", ["from"] = "player" });
            await w.D.StepAsync();
            await WaitFor(() => !w.D.Leases.NarratorBusy);
            Assert.Equal(1, gate.Reached);
            await w.D.DrainAsync();
            Assert.NotEmpty(w.D.Chronicle.State.Pending);                                    // the notes wait for the next idle window
            Assert.Contains(w.Fb.SayLog, b => b["talk_id"] != null && (string)b["talk_id"] == "t1");
            gate.Open();
            await w.D.DrainAsync();
        }

        [Fact]
        public async Task ChronicleBridgeOffOrOldModIsHarmless()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.ChronicleSupported = false;                                                 // an older mod: 404 on /chronicle
            w.Llm.Script["chronicle"] = new List<string> { Death };
            w.Fb.AddEvent("death", Who((2, "victim")), "Bram died.", new[] { 1 }, 1.0);
            await Step(w); await Step(w);
            Assert.NotEmpty(w.D.Idle.Log.Where(x => (string)x["kind"] == "chronicle"));
            Assert.Empty(w.Fb.ChronicleLog);
        }

        [Fact]
        public async Task ChronicleClientMatchesTheContractCapsAndErrors()
        {
            using var w = new World(); await w.GoAsync();
            var r = await w.D.Bridge.ChronicleAsync(3, new string('x', 3000), "day");
            Assert.True((bool)r["stored"]); Assert.Equal(1, (int)r["count"]);
            Assert.Equal(2000, ((string)w.Fb.Chronicle[0]["text"]).Length);
            for (int i = 0; i < 130; i++) await w.D.Bridge.ChronicleAsync(4 + i, "t", "day");
            Assert.Equal(120, w.Fb.Chronicle.Count);
            await Assert.ThrowsAsync<BridgeError>(() => w.D.Bridge.ChronicleAsync(-1, "t", "day"));
        }

        // =============================================================================== test_textfill.py (idle wiring)
        const string Good = "Rook carved this in the rain, mostly to keep away from the kitchen.";

        static JObject TextItem(string id = "art:T1", string kind = "art")
        {
            return new JObject
            {
                ["id"] = id, ["kind"] = kind, ["prio"] = 3,
                ["fields"] = kind == "art" ? new JArray("title", "text") : new JArray("text"),
                ["min_len"] = 20, ["max_len"] = 200, ["title_max"] = 30, ["tokens"] = new JArray(), ["about"] = "an artwork's description",
                ["example"] = "A plain statue.", ["example_title"] = "Statue",
                ["context"] = JObject.Parse("{\"maker\": {\"name\": \"Rook\", \"mood\": \"low (31%)\", \"traits\": [\"Pyromaniac\"]}, \"material\": \"marble\", \"colony\": {\"weather\": \"rain\", \"recent\": [\"Raid by pirates\"]}}"),
            };
        }

        static async Task<World> TextWorld(IEnumerable<JObject> items, string script = null)
        {
            var w = new World(); await w.GoAsync();
            w.Fb.TextSupported = true;
            w.Fb.TextItems = items.ToList();
            if (script != null) w.Llm.Script["textfill"] = new List<string> { script };
            return w;
        }

        static string TextReply(string id, string text, string title) { return new JArray(new JObject { ["id"] = id, ["text"] = text, ["title"] = title }).ToString(Newtonsoft.Json.Formatting.None); }

        static JObject TextArtEvent(World w)
        {
            return JObject.Parse("{\"seq\": 1, \"type\": \"art_created\", \"severity\": 0.3, \"actors\": [{\"id\": 1, \"role\": \"maker\"}], \"data\": {\"thing_id\": 500, \"def_label\": \"statue\", \"quality\": \"good\"}}")
                .Also(o => o["game"] = w.D.Bridge.Env.Game);
        }

        [Fact]
        public async Task ArtIdleJobSkippedWhenTextQueueLive()
        {
            using var w = new World(); await w.GoAsync();
            await OnLoop(w, () =>
            {
                w.D.Art.Note(TextArtEvent(w), w.D.Bridge.Env.Game);
                Assert.True(w.D.Idle.AnythingDue());
                w.D.TextQueueLive = true;
                Assert.False(w.D.Idle.StartArt()); Assert.NotEmpty(w.D.Art.Jobs);
                Assert.False(w.D.Idle.AnythingDue());
                w.D.TextQueueLive = false;
                Assert.True(w.D.Idle.StartArt());
                w.D.Idle.Preempt();
            });
        }

        [Fact]
        public async Task IdleRunsTextFillLastWhenIdle()
        {
            using var w = await TextWorld(new[] { TextItem("a") }, TextReply("a", Good, "Smoke"));
            string kind = null; Lease lease = null;                                          // read on the loop: the job may finish at once (Python's task starts later)
            await OnLoop(w, () => { w.D.Idle.RunOnce(); kind = w.D.Idle.Kind; lease = w.D.Idle.Lease; });
            Assert.Equal("textfill", kind);
            Assert.True(lease.ActorId == -2 && lease.Cls == Cls.Idle);
            await w.D.DrainAsync();
            Assert.Equal(new[] { "a" }, w.Fb.TextFillLog.Select(b => (string)b["id"]).ToArray());
            Assert.True(w.D.Idle.TextNext >= w.D.Clock() + 10);
            await OnLoop(w, () => w.D.Idle.RunOnce());                                       // paced: not again within the gap
            Assert.False(w.D.Idle.Active());
        }

        [Fact]
        public async Task IdleTextFillYieldsToOtherJobsAndToRealWork()
        {
            using var w = await TextWorld(new[] { TextItem("a") }, TextReply("a", Good, "Smoke"));
            await OnLoop(w, () =>
            {
                w.D.Art.Note(TextArtEvent(w), w.D.Bridge.Env.Game);
                w.D.TextQueueLive = false;
                w.D.Idle.RunOnce();
                Assert.Equal("art", w.D.Idle.Kind);                                          // an earlier idle job goes first
                w.D.Idle.Preempt();
                w.D.Idle.TextNext = 0.0;
                w.D.Idle.RunOnce();
                var item = w.D.Leases.MakeItem(1, "reaction", w.D.Now(), cls: Cls.Reactive);
                w.D.Leases.Submit(item, w.D.Now());
                w.D.Idle.PreemptIfWanted();
                Assert.False(w.D.Idle.Active());
                Assert.Empty(w.Fb.TextFillLog);
                w.D.Idle.TextNext = 0.0;
                w.D.Idle.RunOnce();                                                          // real work is queued: nothing starts
                Assert.False(w.D.Idle.Active());
            });
        }

        [Fact]
        public async Task IdleTextFillHonoursBothFlags()
        {
            using var w = await TextWorld(new[] { TextItem("a") });
            await OnLoop(w, () =>
            {
                w.D.Cfg.TextFill = false;
                w.D.Idle.RunOnce();
                Assert.False(w.D.Idle.Active());
                w.D.Cfg.TextFill = true;
                w.D.BridgeCfg["text_fill"] = false;
                w.D.Idle.RunOnce();
                Assert.False(w.D.Idle.Active()); Assert.Empty(w.Llm.Calls);
                w.D.BridgeCfg.Remove("text_fill");
                w.D.Idle.RunOnce();
                Assert.Equal("textfill", w.D.Idle.Kind);
                w.D.Idle.Preempt();
            });
        }

        [Fact]
        public async Task IdleTextFill404BacksOffFiveMinutes()
        {
            using var w = await TextWorld(new[] { TextItem("a") });
            w.Fb.TextSupported = false;
            await OnLoop(w, () => w.D.Idle.RunOnce());
            await w.D.DrainAsync();
            Assert.True(w.D.Idle.TextNext >= w.D.Clock() + 300);
            Assert.Empty(w.Llm.Calls);
        }
    }

    static class JObjectAlso
    {
        public static JObject Also(this JObject o, Action<JObject> f) { f(o); return o; }
    }
}

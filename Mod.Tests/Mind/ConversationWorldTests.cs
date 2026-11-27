// Ports of the Director-driven tests of agent/tests/test_group.py (17), test_conversation.py (2), test_loopguard.py (4) and
// test_firsts.py (7) over the World fixture (Mind/Fakes/World.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class ConversationWorldTests
    {
        static readonly string[] LINES =
        {
            "Remember the night the wolves came to the gate.", "I do, and Bram held that door alone.",
            "Somebody pass the salt, this stew needs help.", "We built this place out of splinters and stubbornness.",
            "The first winter nearly broke every last one of us.", "Still here though, and still arguing over supper.",
            "Cy burned the porridge again, I swear it.", "Best porridge I ever pushed away politely.",
        };

        static async Task Step(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }
        static void Four(World w) { w.Fb.AddPawn(4, "Dee"); w.D.Cfg.GroupTurns = new List<int> { 5, 4, 2, 0 }; }
        static List<string> Lines() { return new List<string>(LINES); }
        static List<FakeCall> LineCalls(World w) { return w.Llm.Calls.Where(c => c.Kind == "line").ToList(); }
        static bool Conv(JObject s) { return ConvOf(s) != null; }
        static List<JObject> ConvSaid(World w) { return w.D.Said.Where(Conv).ToList(); }
        static string Msg(FakeCall c, int i) { return c.Messages[i].Text; }
        static KeyValuePair<int, string>[] Who(params (int, string)[] a) { return a.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2)).ToArray(); }
        static string ConvOf(JObject s) { var c = s["conv"]; string v = c == null || c.Type == JTokenType.Null ? null : (string)c; return string.IsNullOrEmpty(v) ? null : v; }   // a JValue holding a null string has Type String

        static JObject Gather(World w, string kind, int[] ids, int? lead = null, string ledger = "gathering:1", double severity = 0.45,
                              Action<JObject> extra = null)
        {
            var actors = ids.Select(i => new KeyValuePair<int, string>(i, lead == i ? "lead" : "participant")).ToArray();
            var d = new JObject { ["phase"] = "start", ["kind"] = kind, ["key"] = "k" + (w.Fb.Seq + 1), ["label"] = "x" };
            if (lead != null) d["lead"] = lead.Value;
            if (extra != null) extra(d);
            return w.Fb.AddEvent("gathering", actors, kind + " begins", ids, severity, ledger, d);
        }

        // ---------------------------------------------------------------- test_group.py

        [Fact]
        public async Task MealSceneRotatesWindowsAndBlockFCarriesEveryTurn()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            Gather(w, "meal", new[] { 1, 2, 3, 4 }, extra: d => d["room"] = "Kitchen");
            await Step(w);
            var said = ConvSaid(w);
            Assert.Equal(5, said.Count);
            Assert.Single(said.Select(ConvOf).Distinct());
            Assert.All(said, s => Assert.Equal("none", (string)s["intent"]));
            var speakers = said.Select(s => (int)s["actor"]).ToList();
            for (int i = 1; i < speakers.Count; i++) Assert.NotEqual(speakers[i - 1], speakers[i]);
            Assert.Equal(4, speakers.Distinct().Count());
            Assert.True(w.D.Group.Rotations >= 2);                                 // four speakers on two windows
            var last = Msg(LineCalls(w).Last(), 1);
            Assert.All(LINES.Take(4), t => Assert.Contains(t, last));              // block F: all earlier turns
            Assert.DoesNotContain(w.Llm.Calls, c => c.Kind == "intent");           // no intent or aim call in a group
            Assert.Empty(w.D.Leases.Leases);
            Assert.Equal(1, w.D.Group.Ends["turn_cap"]);
            Assert.NotEmpty(w.Fb.SayLog);
            Assert.All(w.Fb.SayLog.Where(s => s["listener"] != null && s["listener"].Type != JTokenType.Null),
                       s => Assert.NotEqual((int)s["pawn"], (int)s["listener"]));
        }

        [Fact]
        public async Task EachParticipantKeepsOneMemory()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            Gather(w, "meal", new[] { 1, 2, 3, 4 });
            await Step(w);
            foreach (int i in new[] { 1, 2, 3, 4 })
            {
                var mem = w.D.Minds[i].Memories.Where(m => m.Etype == "gathering" && m.Text.StartsWith("I was at")).ToList();
                Assert.Single(mem);
                Assert.Equal(0.3, mem[0].Importance);
            }
            Assert.DoesNotContain(w.D.Minds[1].Memories, m => m.Text == "meal begins");      // the bare event is not a memory
        }

        [Fact]
        public async Task SpeedCapsMealTurnsAnd4xHasNoMealScene()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Fb.Speed = 3;
            w.Llm.Script["line"] = Lines();
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            Assert.Equal(2, ConvSaid(w).Count);
            w.Fb.Speed = 4;
            w.D.Group.Seen.Clear();
            int n = w.D.Said.Count;
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            Assert.Equal(n, w.D.Said.Count);
        }

        [Fact]
        public async Task StoryPickUsesChroniclePeopleAndIsNotRepeated()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            var d = w.D;
            long game = d.Bridge.Env.Game;
            d.Chronicle.Note(game, "Ana and Bram held the gate against the wolves all night long.", 0.6, false);
            var got = Group.PickStory(d, new[] { 1, 2, 3 }, game);
            Assert.NotNull(got);
            Assert.Contains("wolves", got.Value.Text);
            Assert.True(got.Value.Opener == 1 || got.Value.Opener == 2);
            d.Group.Told[got.Value.Text] = game;
            Assert.Null(Group.PickStory(d, new[] { 1, 2, 3 }, game));
            Assert.Null(Group.PickStory(d, new[] { 3, 4 }, game));
        }

        [Fact]
        public async Task MealOpenerRetellsTheStoryInThePrompt()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.D.Chronicle.Note(w.D.Bridge.Env.Game, "Ana and Bram held the gate against the wolves all night long.", 0.6, false);
            w.Llm.Script["line"] = Lines();
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            var first = LineCalls(w)[0];
            Assert.Contains("wolves", Msg(first, 0));
            Assert.Contains("Bring it up", Msg(first, 1));
            int a = (int)w.D.Said[0]["actor"];
            Assert.True(a == 1 || a == 2);
        }

        [Fact]
        public async Task FuneralLeadEulogyWithTokenCapAndOldEventIsSilent()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.D.BridgeCfg["events"] = new JObject { ["gathering"] = true };
            w.Fb.AddEvent("funeral", new KeyValuePair<int, string>[0], "A funeral is starting.", new[] { 1, 2, 3 }, 0.5, "funeral:9",
                          new JObject { ["phase"] = "start" });
            Gather(w, "funeral", new[] { 1, 2, 3 }, lead: 3, severity: 0.6, ledger: "gathering:2", extra: d => d["deceased_name"] = "Old Tam");
            w.Llm.Script["line"] = new List<string> { "Tam kept us fed through the worst of the winter.", "He taught me to mend a fence.", "Rest easy now, old man." };
            await Step(w);
            var ls = LineCalls(w);
            Assert.Equal(3, ls[0].Actor == 3 ? ls.Count : -1);
            Assert.Contains("Old Tam", Msg(ls[0], 0));
            Assert.Contains("eulogy", Msg(ls[0], 1));
            var said = ConvSaid(w);
            Assert.Equal(3, (int)said[0]["actor"]);
            Assert.DoesNotContain(w.D.Said, s => !Conv(s));                       // the old funeral event spoke no second line
            Assert.Contains(w.D.Chronicle.State.Pending, n => n.Text.Contains("funeral") && n.Text.Contains("said"));
        }

        [Fact]
        public async Task FuneralEventStillReactsWhenGatheringsAreOff()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.Gatherings = false;
            w.Fb.AddEvent("funeral", new KeyValuePair<int, string>[0], "A funeral is starting.", new[] { 1, 2 }, 0.5, "funeral:9",
                          new JObject { ["phase"] = "start" });
            Gather(w, "funeral", new[] { 1, 2, 3 }, lead: 3, severity: 0.6);
            await Step(w);
            Assert.Empty(ConvSaid(w));
            Assert.NotEmpty(w.D.Said);                                         // today's behaviour for the funeral
        }

        [Fact]
        public async Task WeddingAdultsSayVowsInOrder()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = new List<string> { "I choose you, today and every morning after.", "And I choose you, with all my stubborn heart.",
                                                      "Long may you two argue happily." };
            Gather(w, "wedding", new[] { 1, 2, 3, 4 }, severity: 0.6, extra: d => d["couple"] = new JArray(2, 1));
            await Step(w);
            var said = ConvSaid(w);
            Assert.Equal(new[] { 2, 1 }, said.Take(2).Select(s => (int)s["actor"]).ToArray());
            Assert.Equal(3, said.Count);
            Assert.Contains("vow", Msg(LineCalls(w)[0], 1));
        }

        [Fact]
        public async Task WeddingWithAMinorOrUnknownAgeHasNoScene()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Fb.Pawns[2]["age"] = 17;
            w.D.Ages.Remove(2);
            Gather(w, "wedding", new[] { 1, 2, 3 }, severity: 0.6, extra: d => d["couple"] = new JArray(1, 2));
            await Step(w);
            Assert.Empty(LineCalls(w));
            Assert.Empty(w.D.Said);
            Assert.Equal(1, w.D.Gate.SkippedEvents);
            w.Fb.Pawns[2]["age"] = 30;
            w.D.Ages.Remove(2);
            w.Fb.Pawns[1].Remove("age");
            w.D.Ages.Remove(1);
            Gather(w, "wedding", new[] { 1, 2, 3 }, severity: 0.6, extra: d => d["couple"] = new JArray(1, 2));
            await Step(w);
            Assert.Empty(LineCalls(w));
        }

        [Fact]
        public async Task WeddingMinorGuestDoesNotSpeakAndNoMissingCouple()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Fb.Pawns[4]["age"] = 12;
            w.D.Ages.Remove(4);
            w.Llm.Script["line"] = new List<string> { "I choose you, today and every morning after.", "And I choose you, with all my stubborn heart.",
                                                      "Long may you two argue happily." };
            Gather(w, "wedding", new[] { 1, 2, 3, 4 }, severity: 0.6, extra: d => d["couple"] = new JArray(1, 2));
            await Step(w);
            Assert.DoesNotContain(4, w.D.Said.Select(s => (int)s["actor"]));
            w.D.Group.Seen.Clear();
            int n = w.D.Said.Count;
            Gather(w, "wedding", new[] { 1, 2, 3 }, severity: 0.6);                 // no couple named
            await Step(w);
            Assert.Equal(n, w.D.Said.Count);
        }

        [Fact]
        public async Task MinorAtAMealCarriesTheMinorRule()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Fb.Pawns[3]["age"] = 14;
            w.D.Ages.Remove(3);
            w.Llm.Script["line"] = Lines();
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            Assert.NotEmpty(w.D.Said);
            Assert.All(LineCalls(w), c => Assert.Contains(AgeGate.MinorRule, Msg(c, 0)));
        }

        [Fact]
        public async Task SpeechLeaderThenListeners()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = new List<string> { "We keep faith with the work of our own hands.", "Well said, truly.", "I needed that today." };
            Gather(w, "speech", new[] { 1, 2, 3 }, lead: 1, severity: 0.6, extra: d => d["ideo"] = "The Work Precept");
            await Step(w);
            Assert.Equal(1, (int)ConvSaid(w)[0]["actor"]);
            Assert.Contains("Work Precept", Msg(LineCalls(w)[0], 0));
        }

        [Fact]
        public async Task SwitchesOffMeansNoSceneAndNoMemory()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.D.Cfg.Gatherings = false;
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            Assert.Empty(LineCalls(w));
            Assert.Empty(w.D.Said);
            Assert.DoesNotContain(new[] { 1, 2, 3 }.SelectMany(i => w.D.Minds[i].Memories), m => m.Etype == "gathering");
            w.D.Cfg.Gatherings = true;
            w.D.Cfg.MealTalk = false;
            Gather(w, "meal", new[] { 1, 2, 3 });
            await Step(w);
            Assert.Empty(LineCalls(w));
            w.D.Cfg.MealTalk = true;
            w.D.BridgeCfg["events"] = new JObject { ["gathering"] = false };
            Gather(w, "party", new[] { 1, 2, 3 });
            await Step(w);
            Assert.Empty(LineCalls(w));
        }

        [Fact]
        public async Task GatheringEndStopsASceneAndUrgentWorkPreempts()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            var e = Gather(w, "party", new[] { 1, 2, 3, 4 });
            string key = (string)e["data"]["key"];
            w.D.Group.Over.Add(key);
            await Step(w);
            Assert.Empty(ConvSaid(w));
            Assert.Equal(1, w.D.Group.Ends["over"]);
        }

        [Fact]
        public async Task BudgetIsSpentPerLine()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            double before = w.D.Leases.Budget.Tokens;
            Gather(w, "meal", new[] { 1, 2, 3, 4 });
            await Step(w);
            int n = ConvSaid(w).Count;
            Assert.True(n > 0 && w.D.Leases.Budget.Tokens <= before - n + 1.0);
        }

        [Fact]
        public async Task SceneEndsWhenTheOthersAreApart()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            foreach (int i in new[] { 1, 2, 3, 4 })
                w.Fb.Scenes[i] = JObject.Parse("{\"room\": {\"role\": \"Kitchen\", \"impressiveness\": 30, \"clean\": \"clean\", \"temp\": 20}, " +
                                               "\"present\": [{\"pawn\": " + i + ", \"job\": \"Wander\", \"distance\": 3, \"can_hear\": false}]}");
            Gather(w, "meal", new[] { 1, 2, 3, 4 });
            await Step(w);
            Assert.Single(ConvSaid(w));
            Assert.Equal(1, w.D.Group.Ends["apart"]);
        }

        [Fact]
        public async Task UrgentWorkPreemptsTheScene()
        {
            using var w = new World(); await w.GoAsync(); Four(w);
            w.Llm.Script["line"] = Lines();
            w.Hook.Before = () => { foreach (var l in w.D.Leases.Leases.Values) l.Preempted = true; return Task.CompletedTask; };
            Gather(w, "meal", new[] { 1, 2, 3, 4 });
            await Step(w);
            Assert.Empty(ConvSaid(w));
            Assert.Equal(1, w.D.Group.Ends["preempted"]);
        }

        // ---------------------------------------------------------------- test_conversation.py

        [Fact]
        public async Task DirectorConversationEndWritesMemoryAndRelation()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["line"] = new List<string> { "Where did you put the medicine?", "I put it in the freezer.", "That is a stupid place for it.",
                                                      "Then move it yourself.", "I will. Later.", "Do it today.", "Fine." };
            w.Fb.AddEvent("social", Who((1, "speaker"), (2, "listener")), "Ana spoke with Bram.", new[] { 1, 2 }, 0.7);
            await w.D.StepAsync(); await w.D.DrainAsync(); await w.D.StepAsync();
            await w.D.DrainAsync();   // the Python test's last step leaves its idle text-fill task unstarted (asyncio.run ends); here it runs on the loop, so let it finish before the lease check
            var convIds = w.D.Said.Select(ConvOf).Where(c => c != null).Distinct().ToList();
            Assert.Single(convIds);
            int nSay = w.Fb.SayLog.Count;
            Assert.InRange(nSay, 2, 6);
            Assert.All(w.Fb.SayLog, s => Assert.Equal("c-1", (string)s["conversation_id"]));
            var speakers = w.Fb.SayLog.Select(s => (int)s["pawn"]).ToList();
            Assert.Equal(new[] { 1, 2 }, speakers.Take(2).ToArray());                 // opener obliged a reply (it was a question)
            foreach (var (pid, other) in new[] { (1, 2), (2, 1) })
            {
                var mind = w.D.Minds[pid];
                Assert.Single(mind.Memories.Where(m => m.Etype == "conversation"));   // one memory per pawn
                Assert.True(mind.Relations.ContainsKey(other.ToString()) && !string.IsNullOrEmpty(mind.Relations[other.ToString()].Feeling));
            }
            Assert.Empty(w.D.Leases.Leases);                                          // both leases ended
            var slots = new Dictionary<int, int?>();                                   // two windows: each pawn's lines came from its own slot
            foreach (var c in w.Llm.Calls.Where(c => c.Kind == "line")) slots[c.Actor.Value] = c.Slot;
            Assert.NotEqual(slots[1], slots[2]);
        }

        [Fact]
        public async Task ConversationEndsWhenApart()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["line"] = new List<string> { "Where did you put the medicine?", "In the freezer, Ana.", "Good.", "ok" };
            w.Fb.AddEvent("social", Who((1, "speaker"), (2, "listener")), "Ana spoke with Bram.", new[] { 1, 2 }, 0.7);
            w.Fb.OnRequest = req =>
            {
                if (req.Path == "/scene/1" && w.Fb.SayLog.Count >= 1)
                    w.Fb.Scenes[1] = JObject.Parse("{\"room\": {\"role\": \"Kitchen\"}, \"present\": [], \"time_of_day\": \"m\", \"weather\": \"c\", \"trends\": {}}");
            };
            await w.D.StepAsync(); await w.D.DrainAsync();
            Assert.Single(w.Fb.SayLog);                                               // they parted after the opener
        }

        // ---------------------------------------------------------------- test_loopguard.py

        static void StartConv(World w)
        {
            w.D.Rng.RandomOverride = () => 0.0;
            w.Fb.AddEvent("social", Who((1, "speaker"), (2, "listener")), "Ana spoke with Bram.", new[] { 1, 2 }, 0.7);
            w.D.Cfg.Quotas.PairPerDay = 9;
        }

        [Fact]
        public async Task LoopingFakeIsRetriedThenEndedWithoutARepeat()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.RepeatRetries = 1;
            w.Llm.Script["line"] = new List<string> { "Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                                                      "Where did you put the spare medicine for the leg?", "Where did you put the spare medicine for that leg?" };
            StartConv(w);
            await Step(w);
            var spoken = w.D.Said.Select(s => (string)s["text"]).ToList();
            Assert.Equal(new[] { "Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf." }, spoken);
            Assert.Equal(spoken.Count, spoken.Distinct().Count());                    // never a repeat on screen
            Assert.Contains((1, "repeat"), w.D.Dropped);
            var st = w.D.Loop;
            Assert.Equal((1, 1, 0, 1), (st.Caught, st.Retried, st.Recovered, st.Ended));
            Assert.Equal(3, st.Checked);
            Assert.Equal(new[] { false, false, true, true }, st.Trace.Select(t => (bool)t["refused"]).ToArray());
            string retryPrompt = string.Join("\n", LineCalls(w)[3].Messages.Select(m => m.Text));
            Assert.Contains("refused because it repeats your own earlier line", retryPrompt);
        }

        [Fact]
        public async Task RetryThatIsCleanContinuesTheConversation()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.RepeatRetries = 1;
            w.Llm.Script["line"] = new List<string> { "Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                                                      "Hm. In the cold room, second shelf.", "Thanks, I will fetch it before the bleeding gets worse.",
                                                      "Mind the ice on the step as you go past the freezer." };
            StartConv(w);
            await Step(w);
            var spoken = w.D.Said.Select(s => (string)s["text"]).ToList();
            Assert.Equal(new[] { "Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                                 "Thanks, I will fetch it before the bleeding gets worse." }, spoken.Take(3).ToArray());
            var st = w.D.Loop;
            Assert.True(st.Caught >= 1 && st.Recovered >= 1 && st.Retried == st.Caught);
            Assert.Equal(new[] { (0, false), (0, false), (0, true), (1, false) },
                         st.Trace.Take(4).Select(t => ((int)t["attempt"], (bool)t["refused"])).ToArray());
            Assert.Equal(spoken.Count, spoken.Distinct().Count());
        }

        [Fact]
        public async Task GuardCanBeTurnedOffAndRetriesZeroEndsAtOnce()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.RepeatRetries = 0;
            w.Llm.Script["line"] = new List<string> { "Where did you put the spare medicine for the leg?", "Hm. In the cold room, second shelf.",
                                                      "Where did you put the spare medicine for the leg?" };
            StartConv(w);
            await Step(w);
            Assert.Equal(2, w.D.Said.Count);
            Assert.Equal(1, w.D.Loop.Ended);
            Assert.Equal(0, w.D.Loop.Retried);
        }

        [Fact]
        public async Task OwnRecentLinesCarryAcrossConversations()
        {
            using var w = new World(); await w.GoAsync();
            w.D.RememberOwn(1, "Where did you put the spare medicine for the leg?");
            w.Llm.Script["line"] = new List<string> { "Where did you put the spare medicine for the leg?", "Hm. Cold room, second shelf, friend.",
                                                      "Fetch it quickly then, before the leg gets worse.", "Mind the ice on the step as you go past the freezer." };
            StartConv(w);
            await Step(w);
            Assert.True(w.D.Loop.Caught >= 1);
            Assert.Equal("own", (string)w.D.Loop.Trace[0]["kind"]);
            Assert.True((bool)w.D.Loop.Trace[0]["refused"]);
        }

        // ---------------------------------------------------------------- test_firsts.py

        static JObject Cue(World w, string what = "snow", int[] witnesses = null, string summary = "It began to snow.")
        {
            return w.Fb.AddEvent("first_cue", new KeyValuePair<int, string>[0], summary, witnesses ?? new[] { 1, 2, 3 }, 0.4, null,
                                 new JObject { ["cue"] = what });
        }

        [Fact]
        public async Task FirstSnowIsRecordedForEveryoneAndVoicedByAtMostTwo()
        {
            using var w = new World(); await w.GoAsync();
            Cue(w);
            await Step(w);
            foreach (int i in new[] { 1, 2, 3 }) Assert.Contains("first:snow", w.D.Minds[i].Firsts);
            var calls = LineCalls(w);
            Assert.InRange(calls.Count, 1, Firsts.MaxSpeakersPerEvent);
            Assert.Contains("The first time you have ever seen snow fall: It began to snow.", Msg(calls[0], 0));
            foreach (int i in new[] { 1, 2, 3 }) Assert.Contains(w.D.Minds[i].Memories, m => m.Etype == "first_cue" && m.Pinned);
            Assert.Equal(calls.Count, w.Fb.SayLog.Count);
        }

        [Fact]
        public async Task SecondOccurrenceIsSilentAndWritesNothing()
        {
            using var w = new World(); await w.GoAsync();
            Cue(w);
            await Step(w);
            int nCalls = w.Llm.Calls.Count;
            var nMem = new[] { 1, 2, 3 }.ToDictionary(i => i, i => w.D.Minds[i].Memories.Count);
            w.Fb.Tick += 5000;
            Cue(w, summary: "Snow fell again.");
            await Step(w);
            Assert.Equal(nCalls, w.Llm.Calls.Count);
            Assert.Equal(nMem, new[] { 1, 2, 3 }.ToDictionary(i => i, i => w.D.Minds[i].Memories.Count));
        }

        [Fact]
        public async Task FirstsPerActorPerDayCap()
        {
            using var w = new World(); await w.GoAsync();
            w.Cfg.FirstsPerDay = 1;
            foreach (var what in new[] { "snow", "eclipse", "aurora" })
            {
                Cue(w, what, new[] { 1 }, "A " + what + ".");
                w.Fb.Tick += 400;
                await Step(w);
            }
            foreach (var f in new[] { "first:snow", "first:eclipse", "first:aurora" }) Assert.Contains(f, w.D.Minds[1].Firsts);   // recorded even when not voiced
            Assert.Single(LineCalls(w));                                                                                  // one first-time line that day
        }

        [Fact]
        public async Task RecordedEvenWhenTheLineIsDropped()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Fail = true;
            Cue(w, "corpse", new[] { 1 }, "Ana saw a corpse.");
            await Step(w);
            Assert.Contains("first:corpse", w.D.Minds[1].Firsts);
            Assert.Empty(w.Fb.SayLog);
        }

        [Fact]
        public async Task SwitchOffIsTodaysBehaviour()
        {
            using var w = new World(c => c.Firsts = false); await w.GoAsync();
            Cue(w);
            w.Fb.AddEvent("raid_start", new KeyValuePair<int, string>[0], "A raid.", new[] { 1 }, 0.8, null, new JObject());
            await Step(w);
            Assert.DoesNotContain(w.D.Minds[1].Firsts, x => x.StartsWith("first:"));
            Assert.DoesNotContain("first_cue", w.D.Minds[1].Firsts);                 // the signal event never reached the memory
            Assert.DoesNotContain(LineCalls(w), c => Msg(c, 0).Contains("first time"));
        }

        [Fact]
        public async Task ExistingEventTypesGetTheFirstTimeTriggerOnce()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("raid_start", new KeyValuePair<int, string>[0], "Raid by pirates.", new[] { 1 }, 0.8, null, new JObject());
            await Step(w);
            var first = LineCalls(w).Where(c => Msg(c, 0).Contains("first time you have ever seen a raid")).ToList();
            w.Fb.Tick += 70000;
            w.Fb.AddEvent("raid_start", new KeyValuePair<int, string>[0], "Another raid.", new[] { 1 }, 0.8, null, new JObject());
            await Step(w);
            var again = LineCalls(w).Where(c => Msg(c, 0).Contains("Another raid")).ToList();
            Assert.NotEmpty(first);
            Assert.Contains("first:raid", w.D.Minds[1].Firsts);
            Assert.NotEmpty(again);
            Assert.DoesNotContain(again, c => Msg(c, 0).Contains("first time"));
        }

        [Fact]
        public async Task AnimalDeathIsNotAColonistFirst()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddEvent("death", Who((3, "victim")), "A muffalo died.", new[] { 1 }, 1.0, null, new JObject { ["animal"] = true });
            await Step(w);
            Assert.DoesNotContain("first:colonist_death", w.D.Minds[1].Firsts);
        }
    }
}

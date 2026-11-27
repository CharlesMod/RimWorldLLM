// World-fixture ports (a real Director over a FakeBridge + FakeLlm; Mind/Fakes/World.cs) of the Python tests the earlier inner/idle/text/contract
// ports had to defer: test_concerns (prefix), test_grudges (arrival chat through the Director), test_wants / test_wants_wiring (idle window),
// test_gossip_chain, test_upkeep_gossip, test_reflection.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindT = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class MindWorldTests
    {
        const long DAY = 60000;

        static KeyValuePair<int, string>[] Who(params (int, string)[] a) { return a.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2)).ToArray(); }

        static async Task Step(World w, int ticks = 0, double? real = null)
        {
            w.Fb.Tick += ticks;
            w.Real += real ?? ticks / 60.0;
            await w.D.StepAsync(); await w.D.DrainAsync();
        }
        static async Task Pump(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }

        static void Love(World w, int holder = 1, int other = 2, int opinion = 60)
        {
            w.D.Minds[holder].Relations[other.ToString()] = new Relation { Name = w.D.Names[other], Opinion = opinion };
        }
        static JObject Hurt(World w, int victim = 2, double severity = 0.6, params int[] witnesses)
        {
            return w.Fb.AddEvent("injury", Who((victim, "victim")), "Bram is badly hurt.", witnesses.Length > 0 ? witnesses : new[] { 1, 2 }, severity);
        }
        static Lease FreshLease() { return new Lease(1, 1, Cls.Social, "x", new Now(1, 0.0), 0.0); }
        static async Task<string> Prefix(World w, Lease lease)
        {
            await w.D.RunOnLoop(async () => { await w.D.PrepareAsync(1, "Ana looks around.", null, lease); });
            return (string)lease.Data["prefix"];
        }

        // ---- test_concerns ------------------------------------------------------------------
        [Fact]
        public async Task PrefixCarriesTheWorry()
        {
            using var w = new World(); await w.GoAsync();
            Love(w);
            ConcernOps.AddConcern(w.D.Minds[1], "I worry about Bram.", new[] { 2 }, "hurt", "healed:2", 0.6, 1, 2);
            string p = await Prefix(w, FreshLease());
            Assert.Contains("Worries: I worry about Bram.", p);
        }

        [Fact]
        public async Task SwitchOffMeansNoBirthAndNoPrefixLine()
        {
            using var w = new World(c => c.Concerns = false); await w.GoAsync();
            Love(w);
            Hurt(w); await Pump(w);
            string p = await Prefix(w, FreshLease());
            Assert.Empty(w.D.Minds[1].Concerns);
            Assert.DoesNotContain("Worries", p);
            Assert.DoesNotContain(w.D.Minds[1].Inner, t => t.Kind == "concern");
        }

        // ---- test_grudges -------------------------------------------------------------------
        static JObject InsultEv(World w, int a, int b, string kind = "Insult")
        {
            return w.Fb.AddEvent("social", Who((a, "initiator"), (b, "recipient")), w.D.Names[a] + " -> " + w.D.Names[b] + ": " + kind + ".", new[] { a, b }, 0.5,
                data: JObject.Parse("{\"interaction\": \"" + kind + "\", \"interaction_label\": \"" + kind.ToLowerInvariant() + "\"}"));
        }

        [Fact]
        public async Task ArrivalChatIsToldWhatItIsAboutAndKindIntentsMakeThemUpThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            InsultEv(w, 1, 2);
            await Step(w);
            await Step(w, (int)(0.8 * DAY));                // Ana walks over to apologise
            w.Llm.Script["intent"] = new List<string> { "kind normal", "comfort normal", "kind normal", "none normal" };
            w.Fb.AddEvent("social", Who((1, "initiator"), (2, "recipient")), "Ana -> Bram: Chitchat.", new[] { 1, 2 }, 0.1,
                data: JObject.Parse("{\"interaction\": \"Chitchat\"}"));
            await Step(w);
            string flat = string.Join(" ", w.Llm.Calls.Where(c => c.Kind == "line").SelectMany(c => c.Messages.Select(m => m.Text)));
            Assert.Contains("has come to apologise to", flat); Assert.Contains("The quarrel: Ana insulted Bram", flat);
            var g = w.D.Minds[2].Grudges.Last();
            Assert.Equal("done", g.Phase); Assert.Equal("made_up", g.Ended);
            Assert.Contains(w.D.Minds[1].Memories, m => m.Etype == "makeup"); Assert.Contains(w.D.Minds[2].Memories, m => m.Etype == "makeup");
            Assert.Contains(w.D.Chronicle.State.Pending, n => n.Text.Contains("made up") && !n.Major);
            var guilt = w.D.Minds[1].Concerns.Where(c => c.Kind == "guilt").ToList();
            Assert.NotEmpty(guilt); Assert.Equal("resolved", guilt[0].Status);
            Assert.Equal(1, w.D.Grudges.MadeUp);
        }

        // ---- test_wants / test_wants_wiring --------------------------------------------------
        static string Call(string tool, params (string, object)[] a)
        {
            var args = new JObject();
            foreach (var (k, v) in a) args[k] = JToken.FromObject(v);
            return JsonConvert.SerializeObject(new JObject { ["tool"] = tool, ["args"] = args });
        }
        static readonly string DONE = "{\"done\": true}";

        static void AllWeigh(World w, double v = 2.0) { w.D.Weights.Clear(); foreach (int i in w.D.Minds.Keys) w.D.Weights[i] = v; }

        [Fact]
        public async Task IdleWindowSchedulesWantsWithPacing()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            Assert.Empty(d.Idle.DueWants());                    // off with no tool backend
            d.ToolBackend = new FakeBackend(Call("do_nothing"), DONE); d.ToolMode = "grammar";
            w.Fb.Tick += 100000;
            w.Bridge.Env.Game = w.Fb.Tick;
            d.BridgeCfg["talk_frequency"] = 1.0;
            AllWeigh(w);
            var due = d.Idle.DueWants();
            d.BridgeCfg["talk_frequency"] = 0;
            var off = d.Idle.DueWants();
            d.BridgeCfg["talk_frequency"] = 1.0;
            d.WantsLast.Clear(); foreach (int i in d.Minds.Keys) d.WantsLast[i] = w.Bridge.Env.Game;
            var gapped = d.Idle.DueWants();
            d.WantsLast.Clear();
            w.Bridge.Env.Speed = d.Leases.PauseAmbientAtSpeed;     // ambient pause (4x by default since the speed work)
            var fast = d.Idle.DueWants();
            Assert.Equal(due.OrderBy(x => x).ToList(), due); Assert.Equal(3, due.Count);
            Assert.Empty(off); Assert.Empty(gapped); Assert.Empty(fast);
        }

        [Fact]
        public async Task DirectorRunsAWantsWindowEndToEnd()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.ToolBackend = new FakeBackend(Call("seek_out", ("target", "Bram"), ("topic", "the bread")), DONE); d.ToolMode = "grammar";
            w.Fb.Tick += 100000;
            w.Bridge.Env.Game = w.Fb.Tick;
            AllWeigh(w);
            await Pump(w);
            Assert.True(w.Fb.SocialLog.Count >= 1 && d.Wants.Runs >= 1);
        }

        [Fact]
        public async Task WantsOffViaBridgeConfig()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.ToolBackend = new FakeBackend(Call("do_nothing"), DONE); d.ToolMode = "grammar";
            w.Fb.Tick += 100000;
            w.Bridge.Env.Game = w.Fb.Tick;
            AllWeigh(w);
            d.BridgeCfg["wants_enabled"] = true;
            var on = d.Idle.DueWants();
            d.BridgeCfg["wants_enabled"] = false;
            var off = d.Idle.DueWants();
            Assert.NotEmpty(on); Assert.Empty(off);
        }

        [Fact]
        public async Task SeekOutReachesTheFakeWithEverythingTheModNeedsThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.ToolBackend = new FakeBackend(Call("seek_out", ("target", "Bram"), ("topic", "the bread")), DONE); d.ToolMode = "grammar";
            w.Fb.Tick += 100000;
            w.Bridge.Env.Game = w.Fb.Tick;
            AllWeigh(w);
            await Pump(w);
            var b = w.Fb.SocialLog[0];
            Assert.Contains((int)b["pawn"], w.Fb.Pawns.Keys); Assert.Contains((int)b["target"], w.Fb.Pawns.Keys);
            Assert.Equal("seek_out", (string)b["action"]);
            Assert.False(string.IsNullOrEmpty((string)b["topic"])); Assert.False(string.IsNullOrEmpty((string)b["op_id"]));
        }

        // ---- test_gossip_chain ---------------------------------------------------------------
        static List<string> CommonTalk(World w, int aid, string trigger, Scene sc, ISet<int> present, int? convWith)
        {
            var mi = typeof(Director).GetMethod("CommonTalk", BindingFlags.Instance | BindingFlags.NonPublic);
            return (List<string>)mi.Invoke(w.D, new object[] { aid, trigger, sc, present, convWith });
        }

        [Fact]
        public async Task DirectorWiresChainHintAndSwitches()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.Minds[1].Memories.Add(new Memory { Id = d.Minds[1].NextMemId(), Game = 10, Text = "I saw Cy hoard the food.", Source = "saw", Importance = 0.8,
                                                 People = new List<int> { 3 }, EventSeq = 7, Visibility = "public" });
            var sc = new Scene { Where = "the hall", Present = new List<string> { "Bram (idle)" } };
            var pres = new HashSet<int> { 1, 2 };
            var hint = CommonTalk(w, 1, "supper", sc, pres, 2);
            Assert.Contains(hint, x => x.StartsWith("Something you could pass on"));
            Assert.DoesNotContain(CommonTalk(w, 1, "supper", sc, pres, null), x => x.StartsWith("Something"));   // not outside a conversation
            d.Cfg.GossipChain = false;
            Assert.Empty(CommonTalk(w, 1, "supper", sc, pres, 2));
            d.Cfg.GossipChain = true;
            d.Cfg.Reputation = false;
            d.RefreshReputations();
            Assert.Empty(d.Reputations);
        }

        // ---- test_upkeep_gossip --------------------------------------------------------------
        [Fact]
        public async Task DirectorRunsUpkeepOncePerGameDay()
        {
            using var w = new World(); await w.GoAsync();
            var m = w.D.Minds[1];
            for (int i = 0; i < 4; i++)
                m.Memories.Add(new Memory { Id = "m" + i, Game = i, Text = "Bram snubbed me at the table again", People = new List<int> { 2 }, Importance = 0.5 });
            w.Fb.Tick = (int)(10 * DAY);
            await Pump(w);
            Assert.True(m.Memories.Count == 1 && m.Memories[0].SummaryOf == 4);
            m.Memories.Add(new Memory { Id = "m9", Game = 0, Text = "x y z", Importance = 0.01 });
            await Pump(w);
            Assert.Contains(m.Memories, x => x.Id == "m9");                 // not again the same day
        }

        [Fact]
        public async Task PrivateMemoryIsHintedNeverQuotedInALine()
        {
            using var w = new World(); await w.GoAsync();
            var secret = JObject.Parse("{\"secret\": true}");
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana stole the rifle from the armory at night.", new[] { 1 }, 0.6, data: secret);
            w.Llm.Script["line"] = new List<string> { "Yes, I stole the rifle from the armory at night.", "There is something about that armory I am not telling you." };
            await Pump(w);
            Assert.Equal("private", w.D.Minds[1].Memories[0].Visibility);
            Assert.Contains("[private]", w.Llm.Calls.First(c => c.Kind == "line").Messages[0].Text);
            Assert.Contains((1, "leaks_secret"), w.D.Dropped); Assert.Empty(w.Fb.SayLog);       // the quote was dropped, never shown
            w.Fb.Tick += 6000;
            w.Fb.AddEvent("injury", Who((1, "victim")), "Ana stole the rifle from the armory at night.", new[] { 1 }, 0.62, data: JObject.Parse("{\"secret\": true}"));
            await Pump(w);
            Assert.NotEmpty(w.Fb.SayLog);
            string said = (string)w.Fb.SayLog.Last()["text"];
            Assert.Contains("armory", said); Assert.DoesNotContain("stole", said);
        }

        [Fact]
        public void AdapterPinsDeathsBetrayalsFirstsAndFlagsSecrets()
        {
            var ad = new RimWorldLLM.Mind.RimWorld.RimWorldAdapter();
            var ev = JObject.Parse("{\"seq\": 1, \"game\": 5, \"type\": \"death\", \"actors\": [{\"id\": 2, \"role\": \"victim\"}], \"witnesses\": [1], \"summary\": \"Bram died.\", \"severity\": 0.2, \"data\": {}}");
            var minds = new Dictionary<int, MindT> { { 1, new MindT(1) } };
            MemoryOps.RememberEvent(minds, ev, ad);
            Assert.True(minds[1].Memories[0].Pinned);
            var be = (JObject)ev.DeepClone();
            be["seq"] = 2; be["type"] = "social"; be["summary"] = "Cy sold Ana out."; be["data"] = JObject.Parse("{\"betrayal\": true}");
            be["actors"] = JArray.Parse("[{\"id\": 3, \"role\": \"speaker\"}]");
            MemoryOps.RememberEvent(minds, be, ad);
            Assert.True(minds[1].Memories[1].Pinned);
            var sec = (JObject)ev.DeepClone();
            sec["seq"] = 3; sec["type"] = "injury"; sec["severity"] = 0.3; sec["data"] = JObject.Parse("{\"secret\": true}"); sec["summary"] = "Ana hid the knife.";
            MemoryOps.RememberEvent(minds, sec, ad);
            Assert.Equal("private", minds[1].Memories[2].Visibility);
        }

        [Fact]
        public async Task ConfidedListenerMayHearItStatedButAThirdPartyMayNotInTheWorld()
        {
            using var w = new World(); await w.GoAsync();
            var m = w.D.Minds[1];
            m.Memories.Add(new Memory { Id = "m50", Game = 10, Text = "I hid the knife under the floorboards in the barn.", Visibility = "told",
                                        ToldTo = new List<string> { "Bram" }, Importance = 0.9 });
            Assert.Empty(MemoryOps.SecretTexts(m, "Bram")); Assert.NotEmpty(MemoryOps.SecretTexts(m, "Cy"));
        }

        // ---- test_reflection -----------------------------------------------------------------
        static string Result(string bio = "I am harder than I was, and tired of the cold.", string[] wants = null, JArray rels = null, JArray merge = null)
        {
            return JsonConvert.SerializeObject(new JObject { ["bio"] = bio, ["wants"] = new JArray(wants ?? new[] { "Get warm" }),
                ["relations"] = rels ?? new JArray(), ["merge"] = merge ?? new JArray() });
        }

        [Fact]
        public async Task PrefixBytesUnchangedUntilAReflectionRuns()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D; var mind = d.Minds[1];
            mind.Memories.Add(new Memory { Id = "m1", Game = 500, Text = "I burned the stew.", Importance = 0.5, People = new List<int> { 1 } });
            await Prefix(w, FreshLease());
            var l0 = FreshLease(); string p0 = await Prefix(w, l0); string c0 = PromptBlocks.BlockC(mind);
            // what ordinary play does: conversations rewrite relation lines, the game moves opinions
            mind.Relations["2"] = new Relation { Name = "Bram", Feeling = "irritated", History = "he laughed at me", Opinion = -40 };
            mind.UnreflectedImportance += 2.0;
            w.Fb.Pawns[1]["relations"] = JArray.Parse("[{\"pawn\": 2, \"opinion\": -60}]");
            var l1 = FreshLease(); string p1 = await Prefix(w, l1);
            Assert.Equal(p0, p1); Assert.Equal(c0, PromptBlocks.BlockC(mind));
            // a reflection runs: only now does block C change, and only block C
            w.Llm.Script["reflect"] = new List<string> { Result(bio: "I have stopped trusting Bram.", wants: new[] { "Avoid Bram" },
                rels: JArray.Parse("[{\"who\": \"Bram\", \"feeling\": \"I do not trust him\", \"history\": \"he laughed at me\"}]")) };
            mind.UnreflectedImportance = 5.0;
            await Pump(w);
            var l2 = FreshLease(); string p2 = await Prefix(w, l2); string c2 = PromptBlocks.BlockC(mind);
            Assert.NotEqual(c0, c2);
            Assert.Contains("stopped trusting Bram", c2); Assert.Contains("Avoid Bram", c2); Assert.Contains("do not trust him", c2);
            Assert.Equal(p0.Replace(c0, c2), p2);
        }

        [Fact]
        public async Task ReflectionRunsInAnIdleWindowWithASchemaAndIsSaved()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["reflect"] = new List<string> { Result() };
            w.D.Minds[1].UnreflectedImportance = 9.0;
            await Pump(w);
            await Pump(w);                                   // the next step saves the dirty mind
            var calls = w.Llm.Calls.Where(c => c.Kind == "reflect").ToList();
            Assert.Single(calls);
            Assert.True(JToken.DeepEquals(ConcernOps.ReflectSchema(Reflection.SchemaFor(w.Cfg.Habits), true), calls[0].JsonSchema));
            Assert.Contains(calls[0].Slot, new int?[] { 1, 2 });
            Assert.StartsWith("I am harder", w.D.Minds[1].EvolvingBio);
            Assert.Equal("reflection", (string)w.D.Idle.Log[0]["kind"]); Assert.Equal(1, (int)w.D.Idle.Log[0]["actor"]);
            var blob = w.Fb.Minds[1].Key;
            Assert.StartsWith("I am harder", (string)blob["evolving_bio"]); Assert.Equal(1, (int)blob["bio_history"][0]["rev"]);
        }

        [Fact]
        public async Task ReflectionByIntervalAndFailureBacksOff()
        {
            using var w = new World(); await w.GoAsync();
            var m = w.D.Minds[2];
            m.UnreflectedImportance = 0.5;
            await Pump(w);
            Assert.DoesNotContain(w.Llm.Calls, c => c.Kind == "reflect");            // too soon
            w.Fb.Tick = (int)(4 * DAY);
            w.Llm.Script["reflect"] = new List<string> { "not json" };
            await Pump(w);
            Assert.Equal(1, w.Llm.Calls.Count(c => c.Kind == "reflect"));
            Assert.Equal(0.5, m.UnreflectedImportance); Assert.Contains((2, "reflect_failed"), w.D.Dropped);
            await Pump(w);                                                         // backed off: no hammering
            Assert.Equal(1, w.Llm.Calls.Count(c => c.Kind == "reflect"));
            w.Real += 100;
            await Pump(w);
            Assert.Equal(2, w.Llm.Calls.Count(c => c.Kind == "reflect"));
            Assert.Equal(0, m.UnreflectedImportance); Assert.True(m.LastReflectTime >= 4 * DAY);
        }

        [Fact]
        public async Task UngroundedBioIsRejectedThroughTheDirector()
        {
            using var w = new World(); await w.GoAsync();
            w.Llm.Script["reflect"] = new List<string> { Result(bio: "I have grown close to Zorbax Quillfeather.") };
            w.D.Minds[1].UnreflectedImportance = 9.0;
            await Pump(w);
            Assert.Equal("", w.D.Minds[1].EvolvingBio); Assert.Equal(9.0, w.D.Minds[1].UnreflectedImportance);
        }

        [Fact]
        public async Task PlayerNotesWinOnConflictAndFeedBlockC()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.Minds[1].EvolvingBio = "mine";
            d.Dirty.Add(1);
            int rev = w.Fb.Minds.TryGetValue(1, out var cur) ? cur.Value : 0;
            // the player edits notes in the mod: blob rev moves on, notes_edited_by_player set
            var theirs = d.Minds[1].ToBlob(0, (int)DAY);
            theirs["notes"] = "Never forgives the raid."; theirs["notes_rev"] = 3; theirs["notes_edited_by_player"] = true; theirs["evolving_bio"] = "stale";
            w.Fb.Minds[1] = new KeyValuePair<JObject, int>(theirs, rev + 5);
            await d.RunOnLoop(() => d.SaveDirtyAsync());
            var saved = w.Fb.Minds[1].Key;
            Assert.Equal("Never forgives the raid.", (string)saved["notes"]); Assert.Equal(3, (int)saved["notes_rev"]);
            Assert.True((bool)saved["notes_edited_by_player"]); Assert.Equal("mine", (string)saved["evolving_bio"]);        // ours merged
            Assert.Equal("Never forgives the raid.", d.Minds[1].Notes); Assert.DoesNotContain(1, d.Dirty);
            Assert.Contains("Never forgives the raid.", PromptBlocks.BlockC(d.Minds[1]));
            // without the flag the agent's blob is written as before
            var theirs2 = (JObject)saved.DeepClone();
            theirs2["notes"] = "agent junk"; theirs2["notes_edited_by_player"] = false;
            w.Fb.Minds[1] = new KeyValuePair<JObject, int>(theirs2, w.Fb.Minds[1].Value + 1);
            d.Minds[1].Notes = "ours"; d.Dirty.Add(1);
            await d.RunOnLoop(() => d.SaveDirtyAsync());
            Assert.Equal("ours", (string)w.Fb.Minds[1].Key["notes"]);
        }

        [Fact]
        public async Task PlayerVoiceEditWinsOnConflict()
        {
            using var w = new World(); await w.GoAsync();
            var d = w.D;
            d.Dirty.Add(2);
            int rev = w.Fb.Minds.TryGetValue(2, out var cur) ? cur.Value : 0;
            var theirs = d.Minds[2].ToBlob(0, (int)DAY);
            var v = (JObject)theirs["voice"]; v["register_line"] = "Mumbles in rhyme."; v["edited_by_player"] = true;
            w.Fb.Minds[2] = new KeyValuePair<JObject, int>(theirs, rev + 2);
            d.Minds[2].Wants = new List<string> { "ours" };
            await d.RunOnLoop(() => d.SaveDirtyAsync());
            var saved = w.Fb.Minds[2].Key;
            Assert.Equal("Mumbles in rhyme.", (string)saved["voice"]["register_line"]); Assert.True((bool)saved["voice"]["edited_by_player"]);
            Assert.Equal("ours", (string)saved["wants"][0]); Assert.True(d.Minds[2].Voice.EditedByPlayer);
        }
    }
}

// Ported from agent/tests/test_steward_more.py: the goals board round trip, slot-0 yielding, strategy loading, letters/alerts not voiced.
// Already ported elsewhere (StewardToolsTests): test_sample_digest_is_small_and_the_cap_holds, test_digest_text_is_cleaned.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("StewardEnv")]
    public class StewardMoreTests
    {
        private static readonly string Done = Rig.Done;
        private static string Call(string t, object a) => Rig.Call(t, a);
        private static string Final(string t) => Rig.Final(t);
        private static Task<(JobsWorld w, Steward st, StewardFake fake)> Boot(JobsWorld w, object[] script = null, bool playing = true, Action<MindConfig> cfg = null) => Rig.Boot(w, script, playing, cfg);
        private static Task Cycle(JobsWorld w) => Rig.Cycle(w);

        // ---------------------------------------------------------------- goals board
        [Fact]
        public async Task BoardEditsAreOneBatchedPutWithOneLogLine()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            Rig.NoEnv();
            using var world = new JobsWorld();
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

        // ---------------------------------------------------------------- slot 0
        // The Python test runs a real chronicle idle task and the Director's preemption; here the StubIdle stands for it (the real
        // idle runner is deferred): the steward preempts a running chronicle job at once and holds slot 0 until the cycle ends.
        [Fact]
        public async Task IdleJobsYieldToACycleAndComeBack()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Call("set_plan", new { text = "x" }), Final("ok") });
            fake.Gate = new TaskCompletionSource<bool>();
            var lm = w.Leases;
            Assert.Null(lm.NarratorOwner);
            w.IdleD.IsActive = true; w.IdleD.KindName = "chronicle";
            Rig.Chat(w, "hello");
            await w.StepAsync();
            await Task.Delay(50);
            Assert.Equal("steward", lm.NarratorOwner);
            Assert.Equal(new[] { "steward" }, w.IdleD.Preempted.ToArray());        // the running chronicle was preempted
            Assert.False(lm.NarratorMayDoIdleWork());
            Assert.Null(await Lore.CoinAsync(w, "something big", 1, 1));           // lore does not start
            Assert.Empty(w.Fake.Calls);
            fake.Gate.SetResult(true);
            await w.DrainAsync();
            Assert.Null(lm.NarratorOwner);
            Assert.False(lm.NarratorBusy || lm.NarratorOwner != null);
        }

        // ---------------------------------------------------------------- strategy
        private static string SystemOf(StewardFake fake) => Rig.System(fake.Calls[0]);

        [Fact]
        public async Task StrategyOrderEnvThenConfigThenShipped()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
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
            finally { Rig.NoEnv(); }
        }

        [Fact]
        public void StrategyConfigThenShipped()
        {
            Rig.NoEnv();
            using var tmp = new TempDir();
            string c = tmp.Combine("c.txt");
            File.WriteAllText(c, "CFG STRATEGY");
            Assert.Equal(("CFG STRATEGY", ""), Steward.LoadStrategy(new MindConfig { StewardStrategyPath = c }, "SHIPPED"));
            Assert.Equal(("SHIPPED", ""), Steward.LoadStrategy(new MindConfig(), "SHIPPED"));
        }

        [Fact]
        public void StrategyMissingOrEmptyIsLeftOutAndLongIsCut()
        {
            Rig.NoEnv();
            using var tmp = new TempDir();
            var (text, note) = Steward.LoadStrategy(new MindConfig { StewardStrategyPath = tmp.Combine("none.txt") }, "SHIPPED");
            Assert.True(text == "" && note.Contains("left out"));
            File.WriteAllText(tmp.Combine("e.txt"), "  \n");
            Assert.Equal("", Steward.LoadStrategy(new MindConfig { StewardStrategyPath = tmp.Combine("e.txt") }, "S").Text);
            string big = tmp.Combine("b.txt");
            File.WriteAllText(big, string.Join("\n", Enumerable.Range(0, 400).Select(i => "line " + i + " " + new string('x', 50))));
            (text, note) = Steward.LoadStrategy(new MindConfig { StewardStrategyPath = big }, "S");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 12288 && note.Contains("cut to 12288") && text.EndsWith(new string('x', 50)));
        }

        [Fact]
        public async Task AnEmptyStrategyLeavesTheHeadingOut()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            string e = world.Tmp.Combine("e.txt");
            File.WriteAllText(e, "");
            var (w, st, fake) = await Boot(world, new object[] { Done }, cfg: cf => cf.StewardStrategyPath = e);
            await Cycle(w);
            Assert.DoesNotContain("## Strategy notes", SystemOf(fake));
        }

        // ---------------------------------------------------------------- letters / alerts, wants, slot
        // The Python test also checks, through Director.ingest, that the two kinds never reach the chronicle or the dropped log:
        // that half is deferred with the Director. What the host can check: the adapter marks them silent and idle-class, and the
        // steward still reads them (the digest carries the quest letter).
        [Fact]
        public async Task LettersAndAlertsAreNotVoicedOrRemembered()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world);
            Rig.Ev(w, "letter", "A quest came", 0.6, JObject.Parse("{\"letter\":78,\"label\":\"Quest\",\"choices\":[\"Accept\"],\"quest\":true}"));
            Rig.Ev(w, "alert", "Low food", 0.4, JObject.Parse("{\"alert\":\"Alert_LowFood\",\"label\":\"Low food\",\"critical\":false}"));
            await w.StepAsync();
            await w.DrainAsync();
            Assert.True(w.Adapter.SilentEvents.SetEquals(new[] { "letter", "alert" }));
            Assert.True(w.Adapter.Priority("letter") == Cls.Idle && w.Adapter.Priority("alert") == Cls.Idle);
            Assert.DoesNotContain(w.Chronicle.State.Recent, x => x.Text.Contains("A quest came") || x.Text.Contains("Low food"));
            Assert.True(w.Dropped.Count == 0 || w.Dropped.All(x => !x.ToString().Contains("letter")));
            Assert.Contains("Quest", Rig.Json(fake.Calls[fake.Calls.Count - 1]).Split("\"role\":\"user\"").Last());     // but the steward read them
        }

        [Fact]
        public async Task TheLoopPinsSlotZero()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, st, fake) = await Boot(world, new object[] { Done });
            await Cycle(w);
            Assert.Equal(new int?[] { w.Cfg.NarratorSlot }, fake.Calls.Select(c => c.Slot).Distinct().ToArray());
        }
    }
}

// Port of agent/tests/test_commentary.py: the documentary voice (core/commentary.py, games/rimworld/moments.py, comment_try).
// test_comment_try_main_prints runs the Python argparse CLI and is not ported (the tools worker's wrapper); the printing format is
// covered by CommentTry.Format below.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class CommentaryTests
    {
        private const int D = 60000;

        private static JObject Ev(string t, int game, int[] actors = null, JObject data = null, string summary = "")
        {
            return new JObject
            {
                ["seq"] = game, ["game"] = game, ["type"] = t, ["map"] = 0,
                ["actors"] = new JArray((actors ?? new int[0]).Select(a => new JObject { ["id"] = a, ["role"] = "subject" })),
                ["summary"] = summary.Length > 0 ? summary : t + " happened", ["severity"] = 0.6, ["witnesses"] = new JArray(),
                ["data"] = data ?? new JObject(),
            };
        }

        private static List<string> Comments(World w) => w.Fb.Chat.Where(e => (string)e["who"] == "comment").Select(e => (string)e["text"]).ToList();

        private static async Task<World> Boot(World world, bool playing = true, string settings = "{}", string line = "The colony endures another raid, as colonies do.")
        {
            var w = await world.GoAsync();
            w.Llm.Script["narrator"] = Enumerable.Repeat(line, 20).ToList();
            w.D.Steward.Vv = JObject.Parse("{\"playing\": " + (playing ? "true" : "false") + ", \"settings\": " + settings + "}");
            return w;
        }

        private static async Task Tick(World w, int game, double real)
        {
            w.Real = real;
            w.D.Bridge.Env.Game = game;
            w.D.Commentary.RunOnce();
            await w.D.DrainAsync();
        }

        [Fact]
        public async Task MomentsPickedAndIgnored()
        {
            using var world = new World();
            var w = await Boot(world);
            var c = w.D.Commentary;
            var got = new HashSet<string>();
            foreach (var e in new[]
            {
                Ev("raid_start", 100), Ev("death", 200, new[] { 2 }), Ev("birth", 300, data: JObject.Parse("{\"pawn\": {\"id\": 9}}")),
                Ev("gathering", 400, data: JObject.Parse("{\"kind\": \"wedding\"}")), Ev("masterwork", 500, data: JObject.Parse("{\"thing\": \"x\", \"quality\": \"masterwork\"}")),
                Ev("ship_part", 600, data: JObject.Parse("{\"thing\": \"r\"}")), Ev("colony_lost", 700), Ev("funeral", 800),
                Ev("mental_break", 900, new[] { 1 }, JObject.Parse("{\"state\": \"SocialFighting\"}")),
            })
            {
                c.Note(e);
                got.UnionWith(c.Pending.Select(m => m.Kind));
            }
            Assert.Equal(new HashSet<string> { "raid_start", "death", "birth", "wedding", "masterwork", "ship_part", "colony_lost", "funeral", "social_fight" }, got);
            c.Pending.Clear();
            foreach (var e in new[]
            {
                Ev("tend", 1000), Ev("gathering", 1100, data: JObject.Parse("{\"kind\": \"meeting\"}")), Ev("letter", 1200, summary: "Trade caravan arrives"), Ev("social", 1300),
            }) c.Note(e);
            Assert.Empty(c.Pending);
            c.Note(Ev("letter", 5000, summary: "A wanderer joins the colony"));
            c.Note(Ev("letter", 9000, summary: "Quest accepted: Bandit camp"));
            Assert.Equal(new HashSet<string> { "joiner", "quest" }, c.Pending.Select(m => m.Kind).ToHashSet());
            Assert.All(c.Pending, m => Assert.True(m.Weight >= Commentary.MinWeight));
        }

        [Fact]
        public async Task DedupeWindow()
        {
            using var world = new World();
            var w = await Boot(world);
            var c = w.D.Commentary;
            c.Note(Ev("death", 1000, new[] { 2 }));
            c.Note(Ev("death", 1000 + Commentary.DedupeGame - 1, new[] { 2 }));
            c.Note(Ev("death", 1000, new[] { 3 }));                       // another pawn: a new moment
            Assert.Equal(2, c.Pending.Count);
            c.Note(Ev("death", 1000 + Commentary.DedupeGame, new[] { 2 }));
            Assert.Equal(3, c.Pending.Count);
        }

        [Fact]
        public async Task PostsACommentLine()
        {
            using var world = new World();
            var w = await Boot(world);
            w.D.Commentary.Note(Ev("raid_start", 100, summary: "Pirates attack from the east."));
            await Tick(w, 100, 1000.0);
            Assert.Equal(new[] { "The colony endures another raid, as colonies do." }, Comments(w));
            var call = w.Llm.Calls.Last(x => x.Kind == "narrator");
            Assert.True(call.Slot == w.Cfg.NarratorSlot && call.Messages[1].Text.Contains("Pirates attack"));
        }

        [Fact]
        public async Task RateLimitsAndUrgentBypass()
        {
            using var world = new World();
            var w = await Boot(world);
            var c = w.D.Commentary;
            c.Note(Ev("funeral", 100, new[] { 1 }));
            await Tick(w, 100, 1000.0);
            Assert.Single(Comments(w));
            c.Note(Ev("ship_part", 5000, data: JObject.Parse("{\"thing\": \"a\"}")));      // normal: needs 180 s real AND 15000 game
            await Tick(w, 5000, 1100.0);
            Assert.Single(Comments(w));
            await Tick(w, 5000, 1300.0);                                   // real gap met, game gap not
            Assert.Single(Comments(w));
            c.Note(Ev("death", 5100, new[] { 2 }));                        // urgent: skips both gaps, but not the 30 s floor
            await Tick(w, 5100, 1301.0);
            Assert.True(Comments(w).Count == 2 && c.Log.Last().Kind == "death");
            await Tick(w, 5200, 1600.0);                                   // the ship part still waits for the game gap
            Assert.Equal(2, Comments(w).Count);
            await Tick(w, 5100 + 15000, 1900.0);
            Assert.Equal(3, Comments(w).Count);
        }

        [Fact]
        public async Task UrgentFloorReal()
        {
            using var world = new World();
            var w = await Boot(world);
            var c = w.D.Commentary;
            c.Note(Ev("death", 100, new[] { 1 }));
            await Tick(w, 100, 1000.0);
            c.Note(Ev("death", 200, new[] { 2 }));
            await Tick(w, 200, 1010.0);
            Assert.Single(Comments(w));
            await Tick(w, 200, 1031.0);
            Assert.Equal(2, Comments(w).Count);
        }

        [Fact]
        public async Task OnlyWhenPlayingAndEnabled()
        {
            using var world = new World();
            var w = await Boot(world, playing: false);
            w.D.Commentary.Note(Ev("death", 100, new[] { 2 }));
            await Tick(w, 100, 1000.0);
            Assert.Empty(Comments(w));
            w.D.Steward.Vv = JObject.Parse("{\"playing\": true, \"settings\": {\"commentary\": false}}");
            await Tick(w, 100, 1100.0);
            Assert.Empty(Comments(w));
            w.D.Steward.Vv = new JObject();                                // no vivarium (older mod)
            await Tick(w, 100, 1200.0);
            Assert.Empty(Comments(w));
            w.D.Steward.Vv = JObject.Parse("{\"playing\": true, \"settings\": {\"commentary\": true}}");
            w.Cfg.Commentary = false;
            await Tick(w, 100, 1300.0);
            Assert.Empty(Comments(w));
            w.Cfg.Commentary = true;
            await Tick(w, 100, 1400.0);
            Assert.Single(Comments(w));
        }

        [Fact]
        public async Task WaitsForTheNarratorSlot()
        {
            using var world = new World();
            var w = await Boot(world);
            w.D.Commentary.Note(Ev("death", 100, new[] { 2 }));
            w.D.Leases.NarratorOwner = "steward";
            await Tick(w, 100, 1000.0);
            Assert.Empty(Comments(w));
            w.D.Leases.NarratorOwner = null;
            w.D.Leases.NarratorBusy = true;                                // another slot-0 job
            await Tick(w, 100, 1001.0);
            Assert.Empty(Comments(w));
            w.D.Leases.NarratorBusy = false;
            await Tick(w, 100, 1002.0);
            Assert.Single(Comments(w));
        }

        [Fact]
        public async Task StewardPreemptsARunningComment()
        {
            using var world = new World();
            var w = await Boot(world);
            var gate = new TaskCompletionSource<bool>();
            w.Hook.Before = () => gate.Task;
            w.D.Commentary.Note(Ev("death", 100, new[] { 2 }));
            w.Real = 1000.0; w.D.Bridge.Env.Game = 100;
            w.D.Commentary.RunOnce();
            for (int i = 0; i < 200 && !w.D.Leases.NarratorBusy; i++) await Task.Delay(5);
            Assert.True(w.D.Commentary.Active() && w.D.Leases.NarratorBusy);
            w.D.Leases.NarratorOwner = "steward";                          // a steward cycle starts
            w.D.Commentary.PreemptIfWanted();
            gate.SetResult(true);
            await w.D.DrainAsync();
            Assert.True(Comments(w).Count == 0 && !w.D.Leases.NarratorBusy && !w.D.Commentary.Active());
            Assert.Contains((-1, "commentary_preempted"), w.D.Dropped);
        }

        [Fact]
        public async Task RecapOncePerDay()
        {
            using var world = new World();
            var w = await Boot(world, line: "A day of small mercies.");
            var c = w.D.Commentary;
            c.Note(Ev("death", D + 100, new[] { 2 }, summary: "Bram died."));
            c.Note(Ev("birth", D + 5000, data: JObject.Parse("{\"pawn\": {\"id\": 9}}"), summary: "A baby was born."));
            await Tick(w, D + 5100, 1000.0);                               // the death speaks
            await Tick(w, D + 6000, 1200.0);                               // the birth (urgent) speaks
            int n = Comments(w).Count;
            await Tick(w, D + 9000, 1400.0);
            Assert.Equal(n, Comments(w).Count);                            // same day: no recap
            await Tick(w, 2 * D + 10, 1500.0);                             // the day turned
            Assert.Equal(1, c.Log.Count(e => e.Mode == "recap"));
            var recap = w.Llm.Calls.Last(x => x.Kind == "narrator");
            Assert.True(recap.Messages[1].Text.Contains("Bram died.") && recap.Messages[1].Text.Contains("day 2"));
            await Tick(w, 2 * D + 20, 1700.0);
            await Tick(w, 3 * D + 20, 1900.0);                             // day 3 had no moments: nothing
            Assert.Equal(1, c.Log.Count(e => e.Mode == "recap"));
        }

        [Fact]
        public async Task RecapOff()
        {
            using var world = new World();
            var w = await Boot(world);
            w.Cfg.CommentaryRecap = false;
            w.D.Commentary.Note(Ev("funeral", 100, new[] { 1 }));
            await Tick(w, 100, 1000.0);
            await Tick(w, D + 5, 1500.0);
            Assert.Equal(new[] { "moment" }, w.D.Commentary.Log.Select(e => e.Mode).ToArray());
        }

        [Fact]
        public async Task SanitizeAndAgeGateDrop()
        {
            using var world = new World();
            var w = await Boot(world, line: "I am sorry, as an AI language model I cannot comment on that.");
            w.D.Commentary.Note(Ev("death", 100, new[] { 2 }));
            await Tick(w, 100, 1000.0);
            Assert.True(Comments(w).Count == 0 && w.D.Commentary.Drops.Count > 0);
            w.Llm.Script["narrator"] = new List<string> { "Bram kissed Ana passionately in the moonlight, naked." };
            w.D.Names[1] = "Ana"; w.D.NameTokens[1] = new HashSet<string> { "Ana" }; w.D.Ages[1] = 12;
            w.D.Names[2] = "Bram"; w.D.NameTokens[2] = new HashSet<string> { "Bram" }; w.D.Ages[2] = 30;
            w.D.Commentary.Note(Ev("gathering", 70000, new[] { 1, 2 }, JObject.Parse("{\"kind\": \"wedding\"}")));
            await Tick(w, 70000, 2000.0);
            Assert.Empty(Comments(w));
            Assert.Contains(w.D.Dropped, r => r.Reason.StartsWith("commentary:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task OldModWithoutCommentStopsAsking()
        {
            using var world = new World();
            var w = await Boot(world);
            w.Fb.RefuseComment = true;
            w.D.Commentary.Note(Ev("death", 100, new[] { 2 }));
            await Tick(w, 100, 1000.0);
            Assert.True(w.D.Commentary.Unsupported && !w.D.Commentary.Enabled());
        }

        [Fact]
        public async Task DirectorWiresEventsIntoCommentary()
        {
            using var world = new World();
            var w = await Boot(world);
            await w.D.IngestAsync(new List<JObject> { Ev("death", 100, new[] { 2 }, summary: "Bram died.") });
            Assert.Equal(new[] { "death" }, w.D.Commentary.Pending.Select(m => m.Kind).ToArray());
        }

        private static List<JObject> Sample()
        {
            var asm = typeof(StewardTestData).Assembly;
            using (var s = asm.GetManifestResourceStream("RimWorldLLM.Tests.comment_events_sample.json"))
            using (var r = new StreamReader(s)) return ((JArray)JToken.Parse(r.ReadToEnd())).Cast<JObject>().ToList();
        }

        [Fact]
        public async Task CommentTryOnSample()
        {
            var llm = new FakeLlm();
            llm.Script["narrator"] = Enumerable.Range(0, 10).Select(i => "The colony faces moment number " + i + " with quiet dignity.").ToList();
            var rows = await CommentTry.TryEventsAsync(Sample(), llm);
            var lines = new Dictionary<string, string>();
            foreach (var r in rows) lines[r.Kind] = r.Line;
            foreach (var k in new[] { "raid_start", "death", "birth", "gathering", "masterwork", "ship_part", "recap" })
                Assert.False(string.IsNullOrEmpty(lines[k]), k);
            Assert.Null(lines["tend"]);
            Assert.Equal(1, rows.Count(r => r.Mode == "recap"));
            Assert.Equal("[tend] tend happened\n    -> (no comment)".Split('\n')[1], CommentTry.Format(new CommentTryRow { Event = "x", Kind = "tend" }).Split('\n')[1]);
        }
    }
}

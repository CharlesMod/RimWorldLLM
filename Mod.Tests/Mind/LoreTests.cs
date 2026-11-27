// Ported from agent/tests/test_lore.py and the lore tests of agent/tests/test_creative_ground.py (see ArtTests.cs for TaCtx/TaGolden).
// Deferred (need the Director's step/chronicle writer): the chronicle prompt carrying "SHARED NAMES" and Director._common_talk block D (second
// half of test_coined_after_a_major_event_stored_cached_and_reused), the chronicle prompt part of test_lore_off_is_todays_behaviour,
// and the epoch-change trigger of test_lore_survives_in_the_save_and_reloads (Lore.LoadAsync itself is tested).
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class LoreTests
    {
        private const string Death = "Bram died of his wounds in the evening, and the others buried him without speeches.";

        [Fact]
        public void ParseEntry()
        {
            var p = Lore.ParseEntry("The Long Winter: the months the food ran short.");
            Assert.NotNull(p); Assert.Equal("The Long Winter", p.Value.Name); Assert.Equal("the months the food ran short", p.Value.What);
            Assert.Null(Lore.ParseEntry("none")); Assert.Null(Lore.ParseEntry("Winter: cold"));          // a name is 2-5 words
            Assert.Null(Lore.ParseEntry("no colon here at all")); Assert.Null(Lore.ParseEntry("One Two Three Four Five Six: x"));
        }

        [Fact]
        public void LoreBookCapsDedupesAndMatchesTheScene()
        {
            var b = new LoreBook(3);
            for (int i = 0; i < 5; i++) b.Add("Name Number" + i, "thing" + i, i);
            Assert.Equal(new[] { "Name Number2", "Name Number3", "Name Number4" }, b.Entries.Select(e => e.Name).ToArray());
            b.Add("name number3", "again", 9);
            Assert.Equal(3, b.Entries.Count); Assert.Equal("again", b.Entries[b.Entries.Count - 1].What);
            b.Add("The Old Mill", "where the first raid hit", 10);
            Assert.Equal(new[] { "The Old Mill" }, b.Relevant("they met at the mill by the east gate").Select(e => e.Name).ToArray());
            Assert.Empty(b.Relevant("nothing alike here")); Assert.Equal("", b.NamesLine("nothing alike here"));
            string line = b.NamesLine("the mill raid", label: "Colony names");
            Assert.StartsWith("Colony names: The Old Mill", line);
        }

        [Fact]
        public async Task CoinedAfterAMajorEventStoredAndCached()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fake.Script["lore"] = new List<string> { "The Hollow Funeral: Bram's burial without speeches" };
            var e = await Lore.CoinAsync(w, Death, 5, 0);
            Assert.NotNull(e);
            var calls = c.Calls("lore");
            Assert.Single(calls); Assert.Equal(w.Cfg.NarratorSlot, calls[0].Slot);
            Assert.Equal(new[] { "The Hollow Funeral: Bram's burial without speeches" }, w.LoreD.Lines().ToArray());
            var stored = w.Fb.Chronicle.Where(x => (string)x["kind"] == "lore").ToList();
            Assert.NotEmpty(stored); Assert.Equal("The Hollow Funeral: Bram's burial without speeches", (string)stored[0]["text"]);
        }

        [Fact]
        public async Task NoneStoresNothing()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fake.Script["lore"] = new List<string> { "none" };
            Assert.Null(await Lore.CoinAsync(w, Death, 5, 0));
            Assert.Single(c.Calls("lore")); Assert.Empty(w.LoreD.Entries);
            Assert.DoesNotContain(w.Fb.Chronicle, x => (string)x["kind"] == "lore");
        }

        [Fact]
        public async Task LoreOffIsTodaysBehaviour()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.Lore = false;
            w.Fake.Script["lore"] = new List<string> { "The Hollow Funeral: a burial" };
            Assert.Null(await Lore.CoinAsync(w, Death, 5, 0));
            Assert.Empty(c.Calls("lore")); Assert.Empty(w.LoreD.Entries);
        }

        [Fact]
        public async Task UnusableLoreIsDroppedMinorsAndRomance()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Ages[1] = 12;                                    // Ana is a minor
            foreach (var bad in new[] { "I'm sorry, I can't do that: refusing", "Cy And Ana: their lovers tryst in the barn", "Just a plain sentence." })
            {
                w.Fake.Script["lore"] = new List<string> { bad };
                Assert.Null(await Lore.CoinAsync(w, Death, 5, 0));
            }
            Assert.Empty(w.LoreD.Entries);
        }

        [Fact]
        public async Task LoreSurvivesInTheSaveAndReloads()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fb.Chronicle.Add(new JObject { ["day"] = 2, ["kind"] = "lore", ["text"] = "Bram's Folly: the half-built tower", ["tick"] = 5 });
            w.Fb.Chronicle.Add(new JObject { ["day"] = 3, ["kind"] = "day", ["text"] = "A quiet day passed.", ["tick"] = 6 });
            w.Fb.ReloadGame();
            await Lore.LoadAsync(w);                           // the Director does this when the epoch changed
            Assert.Equal(new[] { "Bram's Folly: the half-built tower" }, w.LoreD.Lines().ToArray());
        }

        [Fact]
        public async Task RecapIgnoresLoreEntries()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fb.Chronicle.Add(new JObject { ["day"] = 0, ["kind"] = "day", ["text"] = "Ana dug the first trench.", ["tick"] = 1 });
            w.Fb.Chronicle.Add(new JObject { ["day"] = 1, ["kind"] = "lore", ["text"] = "Bram's Folly: the half-built tower", ["tick"] = 2 });
            w.Fb.Chronicle.Add(new JObject { ["day"] = 2, ["kind"] = "lore", ["text"] = "The Long Winter: the hungry months", ["tick"] = 3 });
            w.Fake.Script["recap"] = new List<string> { "Ana dug the first trench.\nThe days went by.\nWork continued." };
            w.Fb.ReloadGame();
            await w.Client.ConfigAsync();                      // the envelope carries the new epoch
            await Recap.RunAsync(w, w.Bridge.Env.Epoch);
            Assert.Empty(c.Calls("recap")); Assert.Empty(w.Fb.RecapLog);          // one real entry: no recap
            w.Fb.Chronicle.Add(new JObject { ["day"] = 3, ["kind"] = "day", ["text"] = "Cy was hurt by raiders.", ["tick"] = 4 });
            w.Fb.ReloadGame();
            await w.Client.ConfigAsync();
            await Recap.RunAsync(w, w.Bridge.Env.Epoch);
            string user = c.Msgs("recap", 0)[1].Text;
            Assert.Contains("Ana dug", user); Assert.Contains("Cy was hurt", user); Assert.DoesNotContain("Folly", user); Assert.DoesNotContain("Winter", user);
        }

        [Fact]
        public void LoreAndRecapPromptsAskForNicknamesAndBigEvents()
        {
            var a = new RimWorldAdapter();
            Assert.Contains("never a label", a.LoreRequest);
            Assert.Contains("biggest events", a.RecapRequest); Assert.Contains("never small", a.RecapRequest);
        }

        [Fact]
        public async Task ANameThatOnlyRepeatsItselfIsDropped()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fake.Script["lore"] = new List<string> { "Yard with: Yard with" };
            Assert.Null(await Lore.CoinAsync(w, Death, 5, 0));
            Assert.Empty(w.LoreD.Entries);
        }

        // ---- test_creative_ground.py: lore ---------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task LoreReferentsAndPrompt()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            string entry = "Ana killed the wolves in the east barracks. Bram buried Old Tam near the hollow.";
            var refs = Grounding.Referents(new[] { entry }, w.NamesD.Values.ToList());
            Assert.Contains("Ana", refs); Assert.Contains("Bram", refs); Assert.DoesNotContain(refs, r => r == "barracks");
            var a = w.Adapter;
            var off = Lore.Messages(a, entry, new string[0]);
            var on = Lore.Messages(a, entry, new string[0], refs);
            Assert.DoesNotContain("REFERENTS", off[1].Text); Assert.Contains(a.LoreRequest, off[1].Text);
            Assert.Contains("<game_data kind=\"referents\">", on[1].Text); Assert.Contains("one vivid moment", on[1].Text);
            foreach (var m in new[] { off, on }) { Assert.Contains("MINORS", m[0].Text); Assert.Contains("GAME DATA RULE", m[0].Text); }
        }

        [Fact]
        public async Task LoreCoinGoesThroughComposeOnceFlagsOff()
        {
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fake.Script["lore"] = new List<string> { "The Hollow Burial: where Tam was laid to rest" };
            var e = await Lore.CoinAsync(w, "Ana buried Old Tam near the hollow.", 5, 0);
            Assert.NotNull(e); Assert.Equal("The Hollow Burial", e.Name);
            Assert.Single(c.Calls("lore"));
            w.Cfg.CreativeGround = true;
            w.Fake.Script["lore"] = new List<string> { "The Wolf Wall: where Ana held the line" };
            var e2 = await Lore.CoinAsync(w, "Ana held the east wall against the wolves.", 6, 0);
            string user = c.Msgs("lore")[1].Text;
            Assert.NotNull(e2); Assert.Contains("REFERENTS", user); Assert.Contains("Ana", user);
        }

        // ---- goldens ---------------------------------------------------------------------------------------------------------------------------------

        private static void AssertEntry(JToken expected, LoreEntry e)
        {
            Assert.NotNull(e); Assert.Equal((string)expected[0], e.Name); Assert.Equal((string)expected[1], e.What);
        }

        [Fact]
        public async Task GoldenCoinOffThenGround()
        {
            var g = TaGolden.Load("lore");
            using var c = await TaCtx.Go(); var w = c.W;
            w.Fake.Script["lore"] = new List<string> { "The Hollow Burial: where Tam was laid to rest" };
            var e = await Lore.CoinAsync(w, "Ana buried Old Tam near the hollow.", 5, 0);
            TaGolden.AssertMessages(g["lore_off"]["messages"], c.Msgs("lore")); AssertEntry(g["lore_off"]["result"], e);
            w.Cfg.CreativeGround = true;
            w.ChronicleD.State.Recent.Add(new ChronicleEntry { Day = 0, Game = 3, Text = "Bram buried Old Tam near the hollow." });
            w.Fake.Script["lore"] = new List<string> { "The Wolf Wall: where Ana held the line" };
            var e2 = await Lore.CoinAsync(w, "Ana held the east wall against the wolves.", 6, 0);
            TaGolden.AssertMessages(g["lore_ground"]["messages"], c.Msgs("lore")); AssertEntry(g["lore_ground"]["result"], e2);
            Assert.Equal((string)g["lore_ground"]["chronicle_post"], (string)w.Fb.Chronicle[w.Fb.Chronicle.Count - 1]["text"]);

            string entry = "Ana killed the wolves in the east barracks. Bram buried Old Tam near the hollow.";
            TaGolden.AssertMessages(g["lore_messages_plain"], Lore.Messages(w.Adapter, entry, new[] { "The Old Mill: where the first raid hit", "Quiet Rock: a stone" }));
            TaGolden.AssertMessages(g["lore_messages_refs"], Lore.Messages(w.Adapter, entry, new string[0], new[] { "Ana", "Bram", "hollow" }));
            TaGolden.AssertMessages(g["lore_messages_norefs_empty"], Lore.Messages(w.Adapter, entry, new string[0], new string[0]));
        }

        [Fact]
        public async Task GoldenCoinWithPlan()
        {
            var g = TaGolden.Load("lore")["lore_plan"];
            using var c = await TaCtx.Go(); var w = c.W;
            w.Cfg.CreativePlan = true;
            w.Fake.Script["lore"] = new List<string> { "Hollow Burial; Tam", "The Hollow Burial: where Tam was laid to rest" };
            var e = await Lore.CoinAsync(w, "Ana buried Old Tam near the hollow.", 5, 0);
            var calls = c.Calls("lore");
            Assert.Equal(((JArray)g["calls"]).Count, calls.Count);
            for (int i = 0; i < calls.Count; i++) TaGolden.AssertMessages(g["calls"][i], calls[i].Messages);
            AssertEntry(g["result"], e);
        }

        [Fact]
        public void GoldenNamesLine()
        {
            var g = TaGolden.Load("lore")["lore_names_line"];
            var b = new LoreBook(12);
            b.Add("The Old Mill", "where the first raid hit", 10);
            b.Add("Bram's Folly", "the half-built tower", 5);
            Assert.Equal((string)g[0], b.NamesLine("they met at the mill by the east gate"));
            Assert.Equal((string)g[1], b.NamesLine("the tower raid", label: "Colony names"));
            Assert.Equal((string)g[2], b.NamesLine("nothing here"));
        }
    }
}

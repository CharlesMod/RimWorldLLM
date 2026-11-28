// Prefix-stable steward prompts: the system text and the declared tools are byte-identical from turn to turn, whatever the digest holds;
// what is callable now rides in the last user message (AVAILABLE NOW) and in the grammar.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;
using Xunit.Abstractions;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class StewardPrefixTests
    {
        private readonly ITestOutputHelper _out;
        public StewardPrefixTests(ITestOutputHelper o) { _out = o; }

        private static JObject Load(string name) => JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests." + name + ".json"));

        /// <summary>The sample with other pawns, other rooms and ids, no beds or letters, and a new letter and dialog.</summary>
        private static JObject Mutated()
        {
            var d = StewardTestData.Clone(StewardTestData.Sample);
            string[] names = { "Zed", "Yan", "Xia", "Wil", "Val" };
            int i = 0;
            foreach (var p in (JArray)d["pawns"]) { p["name"] = names[i % names.Length]; p["id"] = 900 + i; i++; }
            d["beds"] = new JArray();
            d["letters"] = new JArray(new JObject { ["id"] = 5, ["label"] = "Offer", ["text"] = "x", ["choices"] = new JArray("Yes", "No") });
            var rooms = (JArray)d["build"]["rooms"];
            foreach (var r in rooms) { r["id"] = (int)r["id"] + 70; r["label"] = "Annex " + r["id"]; }
            d["work_types"] = new JArray("Cooking", "Mining");
            d["goals_dummy"] = 1;
            return d;
        }

        public static IEnumerable<object[]> Pairs()
        {
            yield return new object[] { "sample", "settlement" };
            yield return new object[] { "sample", "trader" };
            yield return new object[] { "settlement", "orbital" };
            yield return new object[] { "sample", "mutated" };
            yield return new object[] { "mutated", "trader" };
        }

        private static JObject Digest(string n) => n == "mutated" ? Mutated() : Load("steward_digest_" + n);

        private static async Task<(StewardFake.CallRec First, StewardFake.CallRec Second)> Run(string a, string b, string mode, bool playing, object[] script = null)
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, _, fake) = await Rig.Boot(world, script ?? new object[] { Rig.Done }, playing: playing);
            w.ToolMode = mode;
            async Task One(string dig, string chat)
            {
                w.Fb.Digest = Digest(dig);
                int before = fake.Cycles;
                for (int i = 0; i < 4 && fake.Cycles == before; i++)
                {
                    w.Real += 30; w.Fb.Tick += 6 * 2500 + 10;
                    if (!playing) Rig.Chat(w, chat + i);
                    await Rig.Cycle(w);
                }
            }
            await One(a, "how are things? ");
            await One(b, "and now? ");
            var firsts = fake.Calls.Where(c => c.Messages.Count == 2).ToList();
            Assert.True(firsts.Count >= 2, "two cycles ran: calls " + fake.Calls.Count + " " + string.Join(",", fake.Calls.Select(c => c.Messages.Count)));
            return (firsts[0], firsts[1]);
        }

        private static string Prompt(StewardFake.CallRec c) => (c.Tools == null ? "" : c.Tools.ToString(Newtonsoft.Json.Formatting.None) + "\n") + Rig.System(c) + "\n" + Rig.User(c);

        private static int Common(string x, string y)
        {
            int n = Math.Min(x.Length, y.Length), i = 0;
            while (i < n && x[i] == y[i]) i++;
            return i;
        }

        [Theory]
        [MemberData(nameof(Pairs))]
        public async Task GrammarPathSystemIsByteIdentical(string a, string b)
        {
            foreach (bool playing in new[] { true, false })
            {
                var (c1, c2) = await Run(a, b, "grammar", playing);
                Assert.Equal(Rig.System(c1), Rig.System(c2));
                string p1 = Prompt(c1), p2 = Prompt(c2);
                int common = Common(p1, p2);
                int head = (Rig.System(c1) + "\n").Length;
                Assert.True(common >= head, "the shared prefix covers the system text and the tool block");
                Assert.True(common >= 0.7 * p1.Length, "shared " + common + " of " + p1.Length);
                Assert.StartsWith("AVAILABLE NOW: ", Rig.User(c1));
                Assert.StartsWith("AVAILABLE NOW: ", Rig.User(c2));
                _out.WriteLine($"{a}->{b} playing={playing}: system {Rig.System(c1).Length} chars, user {Rig.User(c1).Length}, shared {common}/{p1.Length}");
            }
        }

        [Theory]
        [MemberData(nameof(Pairs))]
        public async Task NativePathSystemAndToolsAreByteIdentical(string a, string b)
        {
            foreach (bool playing in new[] { true, false })
            {
                var (c1, c2) = await Run(a, b, "native", playing);
                Assert.Equal(Rig.System(c1), Rig.System(c2));
                Assert.NotNull(c1.Tools);
                Assert.Equal(c1.Tools.ToString(), c2.Tools.ToString());
                string p1 = Prompt(c1), p2 = Prompt(c2);
                int common = Common(p1, p2);
                int head = c1.Tools.ToString(Newtonsoft.Json.Formatting.None).Length + 1 + (Rig.System(c1) + "\n").Length;
                Assert.True(common >= head);
                Assert.True(common >= 0.7 * p1.Length, "shared " + common + " of " + p1.Length);
                Assert.DoesNotContain("one of: \"Ana\"", c1.Tools.ToString());
                _out.WriteLine($"{a}->{b} native playing={playing}: system {Rig.System(c1).Length}, tools {c1.Tools.ToString(Newtonsoft.Json.Formatting.None).Length}, shared {common}/{p1.Length}");
            }
        }

        [Fact]
        public void CatalogVariantsAgreeAndHoldNoData()
        {
            foreach (bool adv in new[] { false, true })
            {
                var tools = StewardCatalog.Tools(adv);
                string text = Prompt_.Render(tools);
                foreach (var w in new[] { "Ana", "Zed", "3301", "campfire", "Hussar", "Pirates" })
                    Assert.DoesNotContain("\"" + w + "\"", text);
                var names = tools.Select(t => t.Name).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
                foreach (var n in new[] { "set_work", "assign_bed", "build", "zone", "trade", "mechs", "squad", "defend", "job", "inspect", "look", "do_nothing" })
                    Assert.Contains(n, names);
                Assert.Equal(!adv, names.Contains("choose"));
                _out.WriteLine((adv ? "advisor" : "routine") + " declared: " + tools.Count + " tools, " + text.Length + " chars");
            }
        }

        private static class Prompt_ { public static string Render(IReadOnlyList<ToolSpec> t) => RimWorldLLM.Mind.ToolLoop.Prompt.RenderTools(t); }

        [Fact]
        public async Task GrammarExcludesUnavailableToolsAndWrongValues()
        {
            var (c1, c2) = await Run("sample", "mutated", "grammar", true);
            Assert.NotNull(c1.Grammar);
            // the sample has beds and no zones ... the mutated digest has no beds: assign_bed is not callable there
            Assert.Contains("assign_bed", c1.Grammar);
            Assert.DoesNotContain("assign_bed", c2.Grammar);
            Assert.Contains("\\\"Ana\\\"", c1.Grammar.Replace("\"Ana\"", "\\\"Ana\\\""));
            Assert.DoesNotContain("Ana", c2.Grammar);
            Assert.Contains("Zed", c2.Grammar);
            var user = Rig.User(c2);
            Assert.DoesNotContain("assign_bed", user.Split('\n')[0]);
            Assert.Contains("answer_letter", user.Split('\n')[0]);
        }

        [Fact]
        public async Task ADeclaredButUnavailableToolIsRejectedWithTheCallableNames()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var d = Mutated();
            var (w, _, fake) = await Rig.Boot(world, new object[] { Rig.Call("assign_bed", new { pawn = "Zed", bed_id = "3301" }), Rig.Done });
            w.Fb.Digest = d;
            w.Real += 30; w.Fb.Tick += 6 * 2500 + 10;
            await Rig.Cycle(w);
            var second = fake.Calls.First(c => c.Messages.Count > 2);
            string last = (string)second.Messages.Last()["content"];
            Assert.Contains("assign_bed", last);
            Assert.Contains("not available now", last);
            Assert.Contains("set_work", last);
        }

        [Fact]
        public void AvailableNowNamesCallableToolsAndTheirValues()
        {
            var declared = StewardCatalog.Tools(false);
            var s = new StewardSession(Mutated(), null, false);
            string line = StewardCatalog.AvailableNow(s.Tools, declared);
            _out.WriteLine(line);
            Assert.StartsWith("AVAILABLE NOW: set_time, set_work", line);
            Assert.Contains("pawn", line);
            Assert.Contains("Zed", line);
            Assert.DoesNotContain("assign_bed", line);
        }
    
        [Fact]
        public async Task CombatTurnSystemIsByteIdenticalAcrossFights()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, _, fake) = await Rig.Boot(world, new object[] { Rig.Done }, true);
            foreach (var kind in new[] { "pirate_ranged", "tribal_melee", "mech_cluster" })
            {
                w.Fb.Combat = CombatData.View(kind); w.Fb.Digest = CombatData.Digest(kind); w.Fb.CombatSince = w.Fb.Tick;
                w.Real += 60; w.Fb.Tick += 600;
                await Rig.Cycle(w);
                w.Real += 60; w.Fb.Tick += 600;
                await Rig.Cycle(w);
                w.Fb.Combat = Newtonsoft.Json.Linq.JObject.FromObject(new { active = false }); w.Real += 60; w.Fb.Tick += 600;
                await Rig.Cycle(w);
            }
            // the "combat started" turn is a full cycle (same system as routine); the small turns after it are the combat turns
            var turns = fake.Calls.Where(c => Rig.User(c).StartsWith("AVAILABLE NOW: ") && Rig.System(c).Length < 20000).ToList();
            Assert.True(turns.Count >= 2, "combat turns " + turns.Count + " of " + fake.Calls.Count);
            _out.WriteLine("combat system chars " + Rig.System(turns[0]).Length);
            foreach (var t in turns) Assert.Equal(Rig.System(turns[0]), Rig.System(t));
        }

        [Fact]
        public async Task TradeTurnSystemIsByteIdenticalAcrossTraders()
        {
            Rig.NoEnv();
            using var world = new JobsWorld();
            var (w, _, fake) = await Rig.Boot(world, new object[] { Rig.Done }, true);
            foreach (var n in new[] { "trader", "orbital" })
            {
                var d = Load("steward_digest_" + n); d["food_days"] = 12; if (d["facts"] is Newtonsoft.Json.Linq.JObject f) f["food_days"] = 12;
                w.Fb.Digest = d; w.Real += 60; w.Fb.Tick += 6 * 2500 + 10;
                await Rig.Cycle(w);
            }
            var turns = fake.Calls.Where(c => Rig.User(c).StartsWith("AVAILABLE NOW: ") && Rig.User(c).Contains("=== THE TRADE ===")).ToList();
            Assert.True(turns.Count >= 2, "trade turns " + turns.Count);
            _out.WriteLine("trade system chars " + Rig.System(turns[0]).Length);
            foreach (var t in turns) Assert.Equal(Rig.System(turns[0]), Rig.System(t));
        }
}
}

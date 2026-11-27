// World-fixture ports (a real Director over a FakeBridge and a FakeLlm) of the Python tests that were deferred while the Director was missing:
// test_bystanders, test_inner_monologue, test_delivery_thought, test_vanilla_social, test_habits (Director tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class InnerWorldTests
    {
        static readonly RimWorldAdapter AD = new RimWorldAdapter();

        static KeyValuePair<int, string>[] Who(params (int, string)[] a) { return a.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2)).ToArray(); }
        static async Task Step(World w) { await w.D.StepAsync(); await w.D.DrainAsync(); }

        // ---------------- test_bystanders ----------------

        static void ThreeWindows(World w)
        {
            if (w.D.Leases.ActorSlots.Count == 2) w.D.Leases.ActorSlots.Add(3);     // the two talkers use two windows; the bystander waits for a third
        }

        static JObject Insult(World w, int[] witnesses = null, double sev = 0.5, string interaction = "Insult")
        {
            w.D.Cfg.Quotas.PairPerDay = 9;
            ThreeWindows(w);
            return w.Fb.AddEvent("social", Who((1, "initiator"), (2, "recipient")), "Ana -> Bram: insult.", witnesses ?? new[] { 3 }, sev,
                null, new JObject { ["interaction"] = interaction, ["interaction_label"] = "insult" });
        }

        static List<FakeCall> ReactionCalls(World w) { return w.Llm.Calls.Where(c => c.Kind == "reaction").ToList(); }
        static List<JObject> Clouds(World w, int aid) { return w.Fb.ThoughtLog.Where(t => t["pawn"] != null && (int)t["pawn"] == aid && !string.IsNullOrEmpty((string)t["surface"])).ToList(); }

        [Fact]
        public async Task VanillaInsultMakesOneBystanderCloudInAFreeWindow()
        {
            using var w = new World(); await w.GoAsync();
            Insult(w, new[] { 3 });
            await Step(w);
            var calls = ReactionCalls(w);
            Assert.True(calls.Count == 1 && calls[0].Actor == 3 && new int?[] { 1, 2, 3 }.Contains(calls[0].Slot));
            var cl = Clouds(w, 3);
            Assert.Single(cl);
            Assert.Equal("Not again, you two.", (string)cl[0]["text"]);
            Assert.Equal((string)cl[0]["text"], (string)cl[0]["surface"]);
            Assert.Equal(new List<string> { "Not again, you two." }, w.D.Minds[3].Inner.Where(t => t.Kind == "reaction").Select(t => t.Text).ToList());
            Assert.Empty(Clouds(w, 1)); Assert.Empty(Clouds(w, 2));                          // the parties show nothing
        }

        [Fact]
        public async Task OneReactionPerArgumentAndAPerPawnGap()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.AddPawn(4, "Dee");
            w.Fb.AddPawn(5, "Eli");
            await w.D.LoadActorAsync(4); await w.D.LoadActorAsync(5);
            Insult(w, new[] { 3, 4, 5 });
            await Step(w);
            int n1 = ReactionCalls(w).Count;
            w.Fb.Tick += 50;
            Insult(w, new[] { 3, 4, 5 });                                                    // another one right after
            await Step(w);
            int n2 = ReactionCalls(w).Count;
            Assert.True(n1 == 1 && n2 == 2);                  // one per argument, and the second picks someone not in a gap
            Assert.Equal(2, ReactionCalls(w).Select(c => c.Actor).Distinct().Count());
        }

        [Fact]
        public async Task NotAWitnessNotAPartyAndNotAnArgument()
        {
            using var w = new World(); await w.GoAsync();
            Insult(w, new[] { 1, 2 });                                                       // only the parties heard it
            w.Fb.AddEvent("social", Who((1, "initiator"), (2, "recipient")), "Ana chatted.", new[] { 3 }, 0.5, null, new JObject { ["interaction"] = "Chitchat" });
            await Step(w);
            Assert.Empty(ReactionCalls(w)); Assert.Empty(w.Fb.ThoughtLog);
        }

        [Fact]
        public async Task TemplateWhenWindowsAreBusyAndNothingAtDevSpeed()
        {
            using var w = new World(); await w.GoAsync();
            Insult(w, new[] { 3 });
            w.D.Leases.ActorSlots.Clear();                                                   // no actor window at all
            await Step(w);
            int busyCalls = ReactionCalls(w).Count;
            var busyClouds = Clouds(w, 3).Select(t => (string)t["text"]).ToList();
            w.Fb.Speed = 4;
            w.Fb.Tick += 100000;
            w.D.Leases.ActorSlots.Clear(); w.D.Leases.ActorSlots.AddRange(new[] { 1, 2, 3 });
            Insult(w, new[] { 3 });
            await Step(w);
            Assert.True(busyCalls == 0 && busyClouds.Count > 0 && AD.BystanderTemplates.Contains(busyClouds[0]));
            Assert.Single(Clouds(w, 3));                                                     // speed 4: ambient pause, no second cloud
        }

        [Fact]
        public async Task AppliedSayInsultUsesTheScenePresentList()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Pawns[1]["relations"] = new JArray();
            w.D.Minds[3].Relations["1"] = new Relation { Name = "Ana", Opinion = -10 };
            w.Fb.Scenes[1] = JObject.Parse("{\"room\": {\"role\": \"Kitchen\"}, \"time_of_day\": \"morning\", \"weather\": \"clear\", \"present\": [{\"pawn\": 2, \"job\": \"idle\", \"can_hear\": true}, {\"pawn\": 3, \"job\": \"idle\", \"can_hear\": true}]}");
            ThreeWindows(w);
            w.Llm.Script["intent"] = new List<string> { "insult normal I want him gone" };
            w.Fb.AddEvent("social", Who((1, "speaker"), (2, "listener")), "Ana spoke with Bram.", new[] { 1, 2 }, 0.7);
            w.D.Cfg.Quotas.PairPerDay = 9;
            await Step(w);
            w.D.Leases.Budget.Tokens = 5.0;                   // the talk spent the voice budget; the cloud waited behind it
            await Step(w);
            Assert.Contains(w.Fb.SayLog, s => (string)s["intent"] == "insult");
            Assert.Single(Clouds(w, 3));
            Assert.Equal(3, ReactionCalls(w)[0].Actor);
        }

        [Fact]
        public async Task AgeGateMinorRuleInPromptAndRomanticReactionDropped()
        {
            using var w = new World(); await w.GoAsync();
            w.Fb.Pawns[2]["age"] = 12;
            w.D.Ages[2] = 12.0;
            w.Llm.Script["reaction"] = new List<string> { "Ugh, kiss already, you two." };
            Insult(w, new[] { 3 });
            await Step(w);
            var c = ReactionCalls(w)[0];
            Assert.Contains("minor", c.Messages[0].Text.ToLowerInvariant());
            Assert.Empty(Clouds(w, 3)); Assert.Equal(1, w.D.Bystand.Dropped);
        }

        [Fact]
        public async Task SwitchOffAndLlmFailure()
        {
            using (var w = new World(c => c.Bystanders = false))
            {
                await w.GoAsync();
                Insult(w, new[] { 3 });
                await Step(w);
                Assert.Empty(ReactionCalls(w)); Assert.Empty(w.Fb.ThoughtLog);
            }
            using (var w2 = new World())
            {
                w2.Llm.Fail = true;
                await w2.GoAsync();
                Insult(w2, new[] { 3 });
                await Step(w2);
                Assert.Empty(Clouds(w2, 3)); Assert.Equal(1, w2.D.Bystand.Dropped);
            }
        }
    }
}

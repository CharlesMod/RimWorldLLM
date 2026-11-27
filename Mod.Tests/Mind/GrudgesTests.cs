// Port of agent/tests/test_grudges.py against InnerHost. The Python drove the Director's step(); here Step() feeds events to
// GrudgeEngine.OnEvent, advances the clocks and calls Advance(), then drains the spawned work. The talk itself (line prompts, the
// Conversation machinery) is the Director's: the arrival tests are ported as ReunionFor/NoteFor/OnConversationDone on a hand-made Conversation,
// and the "told what it is about" prompt assertion is deferred (docs/csmind/parity-inner.md).
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class GrudgesTests
    {
        const long DAY = 60000;

        static async Task Step(InnerHost h, JObject ev = null, int ticks = 0, double? real = null)
        {
            h.Fb.Tick += ticks;
            if (!h.Fb.Paused) h.Real += real ?? ticks / 60.0;       // the play clock does not run while paused
            h.Sync();
            if (ev != null) h.Grudges.OnEvent(h.Adapter.NormalizeEvent(ev));
            h.Grudges.Advance();
            await h.Drain();
        }
        static JObject Insult(InnerHost h, int a, int b, string kind = "Insult")
        {
            return h.Fb.AddEvent("social", new[] { new KeyValuePair<int, string>(a, "initiator"), new KeyValuePair<int, string>(b, "recipient") },
                h.Names[a] + " -> " + h.Names[b] + ": " + kind + ".", new[] { a, b }, 0.5,
                data: JObject.Parse("{\"interaction\": \"" + kind + "\", \"interaction_label\": \"" + kind.ToLowerInvariant() + "\"}"));
        }
        static List<(int Pawn, string Action, int Target, string Topic)> Actions(InnerHost h, string action = null)
        {
            return h.Fb.SocialLog.Where(b => action == null || (string)b["action"] == action)
                .Select(b => ((int)b["pawn"], (string)b["action"], (int)b["target"], b["topic"] == null ? null : (string)b["topic"])).ToList();
        }
        static Grudge G(InnerHost h, int holder) { return h.Minds[holder].Grudges.LastOrDefault(); }
        static List<Concern> Open(InnerHost h, int id) { return ConcernOps.OpenConcerns(h.Minds[id]); }

        [Fact]
        public void DecideIsDeterministicFromTraitsAndOpinion()
        {
            Assert.Equal(("offender", "apologise"), GrudgeEngine.Decide(-50, "", "kind", true, 10));
            Assert.Equal(("holder", "confront"), GrudgeEngine.Decide(-50, "", "abrasive", true, 10));
            Assert.Equal(("holder", "make up"), GrudgeEngine.Decide(-50, "kind", "abrasive", false, 10));
            Assert.Equal(("holder", "make up"), GrudgeEngine.Decide(20, "", "", false, 10));
            Assert.Equal(("holder", "confront"), GrudgeEngine.Decide(-20, "", "", false, 10));
            Assert.Equal(("holder", "confront"), GrudgeEngine.Decide(40, "abrasive", "", false, 10));
            Assert.Equal("made_up", GrudgeEngine.OutcomeOf(new[] { "none", "kind" }));
            Assert.Equal("escalated", GrudgeEngine.OutcomeOf(new[] { "kind", "insult" }));
            Assert.Equal("neutral", GrudgeEngine.OutcomeOf(new[] { "none" }));
        }

        [Fact]
        public async Task AnInsultEventStartsAGrudgeAndAvoidsDirectly()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            var g = G(h, 2);
            Assert.Equal(1, g.Against); Assert.Equal("avoid", g.Phase); Assert.Contains("Ana insulted Bram", g.Cause);
            var a = Actions(h);
            Assert.Single(a); Assert.Equal((2, "avoid", 1), (a[0].Pawn, a[0].Action, a[0].Target)); Assert.True(string.IsNullOrEmpty(a[0].Topic));
            Assert.Null(G(h, 1));
        }

        [Fact]
        public async Task ASlightCountsTooButChitchatDoesNot()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 3, 1, "Slight"));
            await Step(h, Insult(h, 3, 2, "Chitchat"));
            Assert.NotNull(G(h, 1)); Assert.Empty(h.Minds[2].Grudges);
        }

        [Fact]
        public async Task AnAppliedInsultLineStartsOneButARefusedOrOtherIntentDoesNot()
        {
            using var h = new InnerHost();
            h.Grudges.OnSay(1, 2, "insult", true);          // d._say applies the line, then tells the engine
            h.Grudges.OnSay(3, 1, "kind", true);
            h.Grudges.OnSay(3, 1, "insult", false);         // a refused one
            await Step(h);
            Assert.NotNull(G(h, 2)); Assert.Equal(1, G(h, 2).Against); Assert.Empty(h.Minds[1].Grudges); Assert.Empty(h.Minds[3].Grudges);
        }

        [Fact]
        public async Task PhasesAvoidThenCooldownThenSeekOutWithGuiltApology()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            var g1 = Open(h, 1);
            await Step(h, null, (int)(0.3 * DAY));
            string ph1 = G(h, 2).Phase;
            int before = Actions(h, "seek_out").Count;
            await Step(h, null, (int)(0.6 * DAY));
            Assert.Single(g1); Assert.Equal("guilt", g1[0].Kind); Assert.Equal(new[] { 2 }, g1[0].About);
            Assert.Equal("cooldown", ph1); Assert.Equal(0, before);
            Assert.Equal("resolve", G(h, 2).Phase);
            var s = Actions(h, "seek_out");
            Assert.Single(s); Assert.Equal((1, "seek_out", 2, "apologise"), (s[0].Pawn, s[0].Action, s[0].Target, s[0].Topic));
        }

        [Fact]
        public async Task ConfrontWhenOpinionIsLowAndNobodyIsKind()
        {
            using var h = new InnerHost();
            h.Fb.Pawns[1]["traits"] = new JArray();
            await Step(h, Insult(h, 3, 2));
            h.Fb.Pawns[2]["relations"] = JArray.Parse("[{\"pawn\": 3, \"opinion\": -40}]");
            await Step(h, null, (int)(0.8 * DAY));
            var s = Actions(h, "seek_out");
            Assert.Single(s); Assert.Equal((2, "seek_out", 3, "confront"), (s[0].Pawn, s[0].Action, s[0].Target, s[0].Topic));
        }

        [Fact]
        public async Task MakeUpWhenTheHolderIsKind()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 2, 1));
            await Step(h, null, (int)(0.8 * DAY));
            var s = Actions(h, "seek_out");
            Assert.Single(s); Assert.Equal((1, "seek_out", 2, "make up"), (s[0].Pawn, s[0].Action, s[0].Target, s[0].Topic));
            Assert.Empty(Open(h, 2));
        }

        // the talk: the Director starts the Conversation for the arrival event; here a hand-made one with the Reunion tag it would carry
        static Conversation Talk(InnerHost h, Reunion r, params string[] intents)
        {
            var conv = new Conversation("c1", r.Mover, r.Other, h.Names.ToDictionary(k => k.Key, k => k.Value)) { Reunion = r.Holder + "|" + r.Grudge };
            for (int i = 0; i < intents.Length; i++) conv.Add(i % 2 == 0 ? r.Mover : r.Other, "line " + i, intents[i]);
            return conv;
        }
        static JObject Arrive(InnerHost h, int mover, int target)
        {
            return h.Fb.AddEvent("social", new[] { new KeyValuePair<int, string>(mover, "initiator"), new KeyValuePair<int, string>(target, "recipient") },
                h.Names[mover] + " -> " + h.Names[target] + ": Chitchat.", new[] { mover, target }, 0.1, data: JObject.Parse("{\"interaction\": \"Chitchat\"}"));
        }

        [Fact]
        public async Task ArrivalChatIsToldWhatItIsAboutAndKindIntentsMakeThemUp()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            await Step(h, null, (int)(0.8 * DAY));
            var arrive = h.Adapter.NormalizeEvent(Arrive(h, 1, 2));
            var r = h.Grudges.ReunionFor(arrive);
            Assert.NotNull(r);
            string note = h.Grudges.NoteFor(r);
            Assert.Contains("has come to apologise to", note); Assert.Contains("The quarrel: Ana insulted Bram", note);
            h.Grudges.OnConversationDone(Talk(h, r, "kind", "comfort", "kind", "none"), h.Bridge.Env.Game);
            var g = G(h, 2);
            Assert.Equal("done", g.Phase); Assert.Equal("made_up", g.Ended);
            Assert.Contains(h.Minds[1].Memories, m => m.Etype == "makeup"); Assert.Contains(h.Minds[2].Memories, m => m.Etype == "makeup");
            Assert.Contains(h.Chronicle.State.Pending, n => n.Text.Contains("made up") && !n.Major);
            var guilt = h.Minds[1].Concerns.Where(c => c.Kind == "guilt").ToList();
            Assert.NotEmpty(guilt); Assert.Equal("resolved", guilt[0].Status);
            Assert.Equal(1, h.Grudges.MadeUp);
        }

        [Fact]
        public async Task AnInsultInTheTalkEscalatesOnceThenFades()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            await Step(h, null, (int)(0.8 * DAY));
            var r = h.Grudges.ReunionFor(h.Adapter.NormalizeEvent(Arrive(h, 1, 2)));
            h.Grudges.OnConversationDone(Talk(h, r, "insult", "insult", "insult", "none"), h.Bridge.Env.Game);
            await h.Drain();
            var g = G(h, 2);
            Assert.Equal(("avoid", 1), (g.Phase, g.Renewals));
            await Step(h, null, (int)(0.8 * DAY));
            var r2 = h.Grudges.ReunionFor(h.Adapter.NormalizeEvent(Arrive(h, h.Grudges.Pending.Values.First().Mover, h.Grudges.Pending.Values.First().Other)));
            Assert.NotNull(r2);
            h.Grudges.OnConversationDone(Talk(h, r2, "insult", "insult", "insult", "none"), h.Bridge.Env.Game);
            await h.Drain();
            g = G(h, 2);
            Assert.Equal("done", g.Phase); Assert.Equal("faded", g.Ended);
            Assert.Equal(2, h.Grudges.Escalated); Assert.Equal(0, h.Grudges.MadeUp);
            Assert.True(Actions(h, "avoid").Count >= 2);
        }

        [Fact]
        public async Task ANeutralTalkRetriesThenFadesAfterMaxAttempts()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            await Step(h, null, (int)(0.8 * DAY));
            for (int i = 0; i < 5; i++) await Step(h, null, (int)(0.3 * DAY));
            var g = G(h, 2);
            Assert.Equal("done", g.Phase); Assert.Equal("faded", g.Ended); Assert.Equal(h.Cfg.GrudgeMaxAttempts, Actions(h, "seek_out").Count);
        }

        [Fact]
        public async Task RefusedSeekOutRetriesLater()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            h.Fb.SocialSupported = false;
            await Step(h, null, (int)(0.8 * DAY));
            int a = G(h, 2).Attempts;
            h.Fb.SocialSupported = true;
            await Step(h, null, (int)(0.3 * DAY));
            Assert.Equal(1, a); Assert.Single(Actions(h, "seek_out"));
        }

        [Fact]
        public async Task NoSocialActionsWithoutWantsOrWithEffectsOff()
        {
            foreach (var kv in new[] { ("wants_enabled", (JToken)false), ("effects", (JToken)"off") })
            {
                using var h = new InnerHost();
                h.BridgeCfg[kv.Item1] = kv.Item2;
                await Step(h, Insult(h, 1, 2));
                await Step(h, null, (int)(0.8 * DAY));
                Assert.Empty(h.Fb.SocialLog); Assert.Empty(h.Minds[2].Grudges);
            }
        }

        [Fact]
        public async Task SwitchOffIsTodaysBehaviour()
        {
            using var h = new InnerHost();
            h.Cfg.Grudges = false;
            await Step(h, Insult(h, 1, 2));
            await Step(h, null, (int)(0.8 * DAY));
            Assert.Empty(h.Fb.SocialLog); Assert.Empty(h.Minds[2].Grudges);
        }

        [Fact]
        public void MinorsAreSafeTheTopicsAreFixedWordsAndNothingRomantic()
        {
            foreach (var a in new[] { GrudgeEngine.Apologise, GrudgeEngine.MakeUpApproach, GrudgeEngine.Confront })
                Assert.Contains(a, new[] { "apologise", "make up", "confront" });
            Assert.False(AgeGate.Violation(GrudgeEngine.GuiltText.Replace("{who}", "Bram"), true, new string[0]));
            var e = JObject.Parse("{\"type\": \"social\", \"actors\": [{\"id\": 1, \"role\": \"initiator\"}, {\"id\": 2, \"role\": \"recipient\"}], \"data\": {\"interaction\": \"RomanceAttempt\"}}");
            Assert.Null(new RimWorldLLM.Mind.RimWorld.RimWorldAdapter().SocialOffence(e));
        }

        [Fact]
        public void SocialFightNamingItsTargetIsAnOffence()
        {
            var ad = new RimWorldLLM.Mind.RimWorld.RimWorldAdapter();
            var e = JObject.Parse("{\"type\": \"mental_break\", \"actors\": [{\"id\": 1, \"role\": \"subject\"}], \"data\": {\"state\": \"SocialFighting\", \"target\": 2}}");
            var off = ad.SocialOffence(e);
            Assert.Equal((1, 2, "picked a fight with"), (off.Value.Offender, off.Value.Victim, off.Value.Verb));
            ((JObject)e["data"]).Remove("target");
            Assert.Null(ad.SocialOffence(e));
        }

        [Fact]
        public async Task ASecondInsultWhileHoldingAGrudgeDoesNotStack()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            await Step(h, Insult(h, 1, 2));
            Assert.Single(h.Minds[2].Grudges); Assert.Single(Actions(h, "avoid"));
        }

        [Fact]
        public async Task FinishedGrudgesAreDroppedAfterADay()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            for (int i = 0; i < 6; i++) await Step(h, null, (int)(0.3 * DAY));
            await Step(h, null, (int)(1.5 * DAY));
            h.Grudges.Upkeep();
            Assert.Empty(h.Minds[2].Grudges);
        }

        [Fact]
        public async Task RealTimeFloorHoldsPhasesAtHighSpeed()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            await Step(h, null, (int)(0.3 * DAY), 20);
            string phFast = G(h, 2).Phase;
            await Step(h, null, 0, 110);
            string phAvoidDone = G(h, 2).Phase;
            await Step(h, null, (int)(0.6 * DAY), 30);
            int early = Actions(h, "seek_out").Count;
            await Step(h, null, 0, 150);
            Assert.Equal("avoid", phFast); Assert.Equal("cooldown", phAvoidDone); Assert.Equal(0, early); Assert.Single(Actions(h, "seek_out"));
        }

        [Fact]
        public async Task RealTimeFloorDoesNotCountAPause()
        {
            using var h = new InnerHost();
            await Step(h, Insult(h, 1, 2));
            h.Fb.Paused = true;
            await Step(h, null, (int)(0.3 * DAY), 500);
            string paused = G(h, 2).Phase;
            h.Fb.Paused = false;
            await Step(h, null, 0, 1);
            await Step(h, null, 0, 125);
            Assert.Equal("avoid", paused); Assert.Equal("cooldown", G(h, 2).Phase);
        }
    }
}

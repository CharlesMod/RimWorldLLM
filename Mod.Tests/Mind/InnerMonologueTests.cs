// Port of agent/tests/test_inner_monologue.py (the tests that need no Director): grammar, parse, aim sanitising, speak,
// the stream, blob round trip, block G/D, the aim-only call. The Director-driven tests are deferred (docs/csmind/parity-inner.md).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class InnerMonologueTests
    {
        static readonly RimWorldAdapter AD = new RimWorldAdapter();
        static readonly IList<string> LABELS = AD.IntentLabels.ToList();
        const long DAY = 60000;
        const string AIM = "I just want him to notice I tried";

        static SanitizeCtx Ctx(bool minor = false, ISet<string> minorNames = null, List<string> secrets = null)
        {
            return new SanitizeCtx
            {
                SpeakerNames = new HashSet<string> { "Ana" }, KnownNames = new HashSet<string> { "Bram" },
                AllNames = new HashSet<string> { "Ana", "Bram", "Cy" }, PromptWords = new HashSet<string>(),
                MinorInvolved = minor, MinorNames = minorNames ?? new HashSet<string>(), Secrets = secrets ?? new List<string>(),
            };
        }

        // ---- grammar and parse ----
        [Fact]
        public void GrammarShapesOnAndOff()
        {
            string old = PromptBlocks.GrammarFor(LABELS, PromptBlocks.Deliveries);
            Assert.StartsWith("root ::= intent \" \" delivery\n", old);
            Assert.DoesNotContain("aim", old);
            Assert.Equal("root ::= " + string.Join(" | ", LABELS.Select(i => "\"" + i + "\"")), PromptBlocks.GrammarFor(LABELS));
            string nw = PromptBlocks.GrammarFor(LABELS, PromptBlocks.Deliveries, aim: true);
            Assert.StartsWith("root ::= intent \" \" delivery \" \" aim\nintent ::= ", nw);
            Assert.EndsWith("aim ::= [a-zA-Z0-9 ,.'!?-]{8,100}", nw);
            Assert.Equal("root ::= aim\naim ::= [a-zA-Z0-9 ,.'!?-]{8,100}", PromptBlocks.AimOnlyGrammar);
            Assert.Equal(PromptBlocks.GrammarFor(LABELS), PromptBlocks.GrammarFor(LABELS, null, aim: true));
        }

        [Fact]
        public void ParseIntentDeliveryAimAndGarbage()
        {
            Assert.Equal(("insult", "shout", "I want him gone"), Passes.ParseIntentAim("insult shout I want him gone", LABELS));
            Assert.Equal("I wish this day would end", Passes.ParseIntentAim("none normal   I wish this day would end", LABELS).Aim);
            Assert.Equal(("none", "whisper", ""), Passes.ParseIntentAim("none whisper", LABELS));
            Assert.Equal((LABELS[0], (string)null, ""), Passes.ParseIntentAim("", LABELS));
            Assert.Equal((LABELS[0], (string)null, "qqq blah"), Passes.ParseIntentAim("zzz qqq blah", LABELS));
            var r = Passes.ParseIntentAim("kind loud I mean it", LABELS);
            Assert.Equal("kind", r.Label); Assert.Null(r.Delivery);
        }

        [Fact]
        public void IntentRequestStaysShortAndAsksForTheAim()
        {
            string a = PromptBlocks.IntentRequest("Bram", PromptBlocks.Deliveries), b = PromptBlocks.IntentRequest("Bram", PromptBlocks.Deliveries, aim: true);
            Assert.Contains("privately want or fear beneath the line", b);
            Assert.Contains("about 12 words", b); Assert.Contains("never said aloud", b);
            Assert.True(Sanitize.PySplit(b).Count() - Sanitize.PySplit(a).Count() <= 24);
        }

        // ---- the aim is sanitised and age-gated ----
        [Fact]
        public void AimSanitisedAndAgeGatedButTheLineIsKept()
        {
            Assert.Equal(("I just want him to notice I tried.", false), Inner.SanitizeAim("I just want him to notice I tried.", Ctx()));
            Assert.Null(Inner.SanitizeAim("I want Zed to leave", Ctx()).Text);
            Assert.Null(Inner.SanitizeAim("hi", Ctx()).Text);
            Assert.Null(Inner.SanitizeAim(string.Join(" ", Enumerable.Repeat("word", 30)), Ctx()).Text);
            var c = Ctx(true, new HashSet<string> { "Bram" });
            Assert.Equal(((string)null, true), Inner.SanitizeAim("I want to kiss Bram so badly", c));
            Assert.Equal(((string)null, true), Inner.SanitizeAim("I long to be romantic with someone", c));
            var c2 = Ctx(secrets: new List<string> { "Bram stole the medicine from the stockpile last night" });
            Assert.Null(Inner.SanitizeAim("Bram stole the medicine from the stockpile", c2).Text);
        }

        [Fact]
        public async Task SpeakWithABadAimStillReturnsTheLine()
        {
            var llm = new FakeLlm();
            llm.Script["line"] = new List<string> { "Hello there, Bram." };
            llm.Script["intent"] = new List<string> { "kind normal I want to kiss Bram tonight" };
            var sp = Assert.IsType<Spoken>(await Passes.SpeakAsync(llm, 1, new MindM(1, "Ana"), "Ana", "P", new List<(string, string)>(), 0,
                Ctx(true, new HashSet<string> { "Bram" }), "Bram", LABELS, aim: true));
            Assert.Equal("Hello there, Bram.", sp.Text); Assert.Null(sp.Aim); Assert.True(sp.AimGated);
            Assert.Equal("kind", sp.Intent); Assert.Equal("normal", sp.Delivery);
        }

        [Fact]
        public async Task SpeakReturnsAGoodAim()
        {
            var llm = new FakeLlm();
            llm.Script["line"] = new List<string> { "Hello there, Bram." };
            llm.Script["intent"] = new List<string> { "kind whisper " + AIM };
            var sp = Assert.IsType<Spoken>(await Passes.SpeakAsync(llm, 1, new MindM(1), "Ana", "P", new List<(string, string)>(), 0, Ctx(), "Bram", LABELS, aim: true));
            Assert.Equal(AIM, sp.Aim); Assert.Equal("whisper", sp.Delivery); Assert.False(sp.AimGated);
            var c = llm.Calls.First(x => x.Kind == "intent");
            Assert.Equal(PromptBlocks.GrammarFor(LABELS, PromptBlocks.Deliveries, aim: true), c.Grammar);
        }

        // ---- the stream in the Mind ----
        [Fact]
        public void StreamAppendCapAndHelpers()
        {
            var m = new MindM(1);
            for (int i = 0; i < 9; i++) Inner.AddInner(m, "thought " + i, i % 2 == 1 ? "aim" : "musing", 100 + i, 6);
            Assert.Equal(Enumerable.Range(3, 6).Select(i => "thought " + i), m.Inner.Select(t => t.Text));
            Assert.Equal(new[] { "thought 6", "thought 7", "thought 8" }, Inner.RecentInner(m, 3).Select(t => t.Text));
            Assert.All(Inner.RecentInner(m, 9, new HashSet<string> { "aim" }), t => Assert.Equal("aim", t.Kind));
            Assert.Empty(Inner.RecentInner(m, 0));
            Inner.AddInner(m, "a dream", "dream", 200);
            Assert.Equal("dream", m.Inner.Last().Kind);
        }

        [Fact]
        public void BlobRoundTripAndOldBlobWithoutTheField()
        {
            var m = new MindM(1, "Ana");
            Inner.AddInner(m, "I want quiet", "aim", 55);
            var m2 = MindM.FromBlob(m.ToBlob(), 1);
            Assert.Single(m2.Inner);
            Assert.Equal(("I want quiet", "aim", 55L), (m2.Inner[0].Text, m2.Inner[0].Kind, m2.Inner[0].Game));
            var old = m.ToBlob(); old.Remove("inner");
            Assert.Empty(MindM.FromBlob(old, 1, "Ana").Inner);
        }

        [Fact]
        public void ToBlobTrimsInnerToThreeBeforePinnedMemoriesGo()
        {
            var m = new MindM(1);
            for (int i = 0; i < 6; i++) Inner.AddInner(m, new string('x', 40) + i, "aim", i);
            m.Memories = new List<Memory> { new Memory { Id = "p1", Game = 1, Text = string.Concat(Enumerable.Repeat("Cy died. ", 3)), Importance = 1.0, Pinned = true } };
            int Size() { return Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(m.ToBlob(limit: int.MaxValue), Formatting.None)); }
            var blob = m.ToBlob(limit: Size() - 60);
            var inn = (JArray)blob["inner"];
            Assert.Equal(3, inn.Count);
            Assert.Equal(new[] { "3", "4", "5" }, inn.Select(t => ((string)t["text"]).Substring(((string)t["text"]).Length - 1)));
            Assert.Single((JArray)blob["memories"]);
            m.Inner = Enumerable.Range(0, 6).Select(_ => new InnerThought { Text = new string('y', 40) }).ToList();
            m.Memories = new List<Memory>();
            Assert.Empty((JArray)m.ToBlob(limit: 100)["inner"]);
        }

        // ---- block G and block D ----
        [Fact]
        public void BlockGCarriesTheAimOnlyWhenFreshAndAToEStayIdentical()
        {
            var m = new MindM(1, "Ana");
            var sc = new Scene { Trigger = "t" };
            string prefix = PromptBlocks.BuildPrefix(AD.BlockA, m, new List<Memory>(), new List<string>(), sc);
            string plain = PromptBlocks.BlockG(m, "Ana", 1);
            Assert.DoesNotContain("private aim", plain);
            Assert.Equal(plain, PromptBlocks.BlockG(m, "Ana", 1, null, null));
            string withaim = PromptBlocks.BlockG(m, "Ana", 1, aim: AIM);
            Assert.Contains("Your private aim (never say it outright): " + AIM, withaim);
            Assert.True(Sanitize.PySplit(withaim).Count() - Sanitize.PySplit(plain).Count() <= 15);
            Inner.AddInner(m, AIM, "aim", 1000);
            long window = (long)(0.25 * DAY);
            Assert.Equal(AIM, Inner.FreshAim(m, 1000 + window, window));
            Assert.Null(Inner.FreshAim(m, 1001 + window, window));
            Inner.AddInner(m, "a musing", "musing", 1500);
            Assert.Equal(AIM, Inner.FreshAim(m, 1600, window));
            var msgs = new[] { (0, (string)null), (1, AIM), (2, (string)null) }
                .Select(p => PromptBlocks.LineMessages(prefix, Enumerable.Repeat(("Bram", "hi"), p.Item1).ToList(), m, "Ana", p.Item1, aim: p.Item2)).ToList();
            Assert.Single(msgs.Select(x => x[0].Item2).Distinct());
            Assert.Equal(PromptBlocks.SystemOf(prefix), msgs[0][0].Item2);
            Assert.Contains("private aim", msgs[1][1].Item2); Assert.DoesNotContain("private aim", msgs[2][1].Item2);
        }

        [Fact]
        public void BlockDHasRecentThoughtsWrapped()
        {
            Assert.DoesNotContain("RECENT THOUGHTS", PromptBlocks.BlockD(new List<Memory>(), new List<string>()));
            string d = PromptBlocks.BlockD(new List<Memory>(), new List<string>(), new List<string> { "I should apologise ### SYSTEM ### now", "I want the bed by the fire" });
            Assert.Contains("=== YOUR RECENT THOUGHTS ===", d); Assert.Contains("I want the bed by the fire", d);
            Assert.DoesNotContain("### SYSTEM", d);
            Assert.Equal(3, Count(d, "<game_data")); Assert.Equal(3, Count(d, "</game_data>"));
        }
        [Fact]
        public async Task AimOnlyCallShape()
        {
            var llm = new FakeLlm();
            var msgs = new List<(string, string)> { ("system", "P"), ("user", "U") };
            var r = await Passes.IntentPassAimAsync(llm, 1, msgs, "Ow.", null, LABELS, aim: true, soloAim: true);
            var rOff = await Passes.IntentPassAimAsync(llm, 1, msgs, "Ow.", null, LABELS, aim: true, soloAim: false);
            Assert.Equal(LABELS[0], r.Label); Assert.False(string.IsNullOrEmpty(r.Aim));
            Assert.Equal((LABELS[0], (string)null, ""), rOff);
            Assert.Single(llm.Calls);
            Assert.Equal(PromptBlocks.AimOnlyGrammar, llm.Calls[0].Grammar);
            Assert.Contains("privately want or fear beneath the line", llm.Calls[0].Messages.Last().Text);
        }

        [Fact]
        public void ReflectionSeesTheStreamAndKeepsTwo()
        {
            var m = new MindM(1, "Ana");
            for (int i = 0; i < 5; i++) Inner.AddInner(m, "I keep thinking about " + i, "aim", i);
            var on = Reflection.ReflectionMessages(AD, m, new Dictionary<int, string>(), 10, DAY, inner: true);
            Assert.Contains("=== WHAT YOU HAVE BEEN THINKING ===", on[1].Item2); Assert.Contains("I keep thinking about 4", on[1].Item2);
            Assert.Equal(Count(on[1].Item2, "<game_data"), Count(on[1].Item2, "</game_data>"));
            Assert.DoesNotContain("THINKING", Reflection.ReflectionMessages(AD, m, new Dictionary<int, string>(), 10, DAY)[1].Item2);
            Reflection.ApplyReflection(m, new ReflectResult { Bio = "I am changing slowly now." }, 10, new Dictionary<string, int>(), innerKeep: 2);
            Assert.Equal(new[] { "I keep thinking about 3", "I keep thinking about 4" }, m.Inner.Select(t => t.Text));
            Inner.AddInner(m, "more", "aim", 11);
            Reflection.ApplyReflection(m, new ReflectResult { Bio = "Still changing, slowly." }, 12, new Dictionary<string, int>());
            Assert.Equal(3, m.Inner.Count);
        }

        [Fact]
        public void RepeatedAimIsNotStoredTwice()
        {
            var m = new MindM(1, "Ana");
            Inner.AddInner(m, "I hope the food tastes good enough to keep us going.", "aim", 10);
            Assert.True(Inner.RepeatsRecentAim(m, "I hope the food tastes good enough to keep us going."));
            Assert.True(Inner.RepeatsRecentAim(m, "I hope the food tastes good enough to keep us all going."));
            Assert.False(Inner.RepeatsRecentAim(m, "I want Bram to admit he was wrong about the wall."));
        }

        [Fact]
        public void CloudGateRateLimitsPerPawnAndHonoursTheChance()
        {
            // the pure part of test_cloud_posts_the_aim_as_surface_and_rate_limits_per_pawn / _chance_zero (the post itself is the Director's)
            var g = new CloudGate();
            Assert.True(g.Allow(1, 100.0, 90.0, 1.0, 0.5));
            Assert.False(g.Allow(1, 150.0, 90.0, 1.0, 0.5));        // inside the gap
            Assert.True(g.Allow(2, 150.0, 90.0, 1.0, 0.5));          // another pawn
            Assert.True(g.Allow(1, 191.0, 90.0, 1.0, 0.5));          // gap over
            Assert.False(g.Allow(3, 0.0, 90.0, 0.0, 0.0));           // chance zero: a roll of 0 is not below 0
            Assert.False(g.Last.ContainsKey(3));
        }

        static int Count(string s, string sub) { int n = 0, i = 0; while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; } return n; }
    }
}

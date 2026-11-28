// The speech work: a voice card with no code-written examples or topic tics, the two context sections, vanilla interactions as intent plus tone,
// and the length rules (1-3 sentences when the moment earns it).
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindT = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class SpeechContextTests
    {
        private static readonly RimWorldAdapter Ad = new RimWorldAdapter();

        private static JObject Vanilla(string interaction, string text, JArray outcome = null)
        {
            var fb = new FakeBridge();
            var data = new JObject { ["interaction"] = interaction, ["interaction_label"] = interaction.ToLowerInvariant(), ["vanilla"] = true, ["ref"] = "v1", ["tier"] = "high", ["vanilla_text"] = text };
            if (outcome != null) data["outcome"] = outcome;
            return fb.AddEvent("social", new[] { new KeyValuePair<int, string>(1, "initiator"), new KeyValuePair<int, string>(2, "recipient") },
                "Ana -> Bram: " + interaction + ".", new[] { 1, 2 }, 0.5, data: data);
        }

        // ---- the voice card
        [Theory]
        [InlineData("greedy"), InlineData("gourmand"), InlineData("industrious"), InlineData("bookworm"), InlineData("nightowl"), InlineData("cannibal"), InlineData("bloodlust"), InlineData("jealous"), InlineData("creative")]
        public void TheVoiceCardHoldsNoCodeWrittenExamplesOrTopicTics(string trait)
        {
            var p = FakeBridge.DefaultPawn(1, "Ana", JObject.Parse(@"{""traits"":[{""def"":""" + trait + @""",""label"":""" + trait + @"""}]}"));
            var vc = Builders.BuildVoiceCard(p);
            Assert.Empty(vc.Examples);
            Assert.NotEmpty(vc.Tics);
            var legacy = new[] { "Mentions what things cost", "Talks about the next job", "Mentions meals", "Quotes numbers", "Mentions sleep", "Compares things to art", "Compares rooms", "Talks about the kill", "Calls the dead 'meat'" };
            Assert.DoesNotContain(vc.Tics, t => legacy.Contains(t));
            Assert.True(vc.RegisterLine.StartsWith("Comes across as") || vc.RegisterLine == "Plain and brief.", vc.RegisterLine);
            Assert.DoesNotContain("kitchen terms", vc.RegisterLine);
        }

        [Fact]
        public void ASavedMindWithLegacyTicsAndRegisterIsFilteredOnTheWayIntoThePrompt()
        {
            var m = new MindT(1, "Ana");
            m.Voice.RegisterLine = "Speaks warm, talks in kitchen terms, counts everything.";
            m.Voice.Tics = new List<string> { "Mentions meals", "Laughs it off" };
            m.Voice.Examples = new List<string> { "Pass the salt, friend." };
            string b = PromptBlocks.BlockB(m);
            Assert.Contains("Register: Comes across as warm.", b);
            Assert.Contains("Habits: Laughs it off", b);
            Assert.DoesNotContain("Mentions meals", b);
            Assert.DoesNotContain("Example lines", b);                       // the model/player did not write them
            Assert.DoesNotContain("Sound like", PromptBlocks.BlockG(m, "Ana"));
        }

        [Fact]
        public void ExamplesTheModelOrThePlayerWroteStillReachThePrompt()
        {
            var m = new MindT(1, "Ana"); m.Voice.Examples = new List<string> { "Pass the salt, friend." };
            m.Voice.LlmExamples = true;
            Assert.Contains("Example lines:\n- Pass the salt, friend.", PromptBlocks.BlockB(m));
            Assert.Contains("Sound like: \"Pass the salt, friend.\"", PromptBlocks.BlockG(m, "Ana"));
            m.Voice.LlmExamples = false; m.Voice.EditedByPlayer = true;
            Assert.Contains("Example lines:", PromptBlocks.BlockB(m));
        }

        [Fact]
        public void AHabitComesUpAtMostOnceInThreeLinesByTheCounter()
        {
            var m = new MindT(1, "Ana"); m.Voice.Tics = new List<string> { "Laughs it off" };
            var shown = Enumerable.Range(0, 9).Select(n => PromptBlocks.BlockG(m, "Ana", n).Contains("Habits (this line): Laughs it off")).ToList();
            Assert.Equal(3, shown.Count(x => x));
            for (int i = 0; i + 1 < shown.Count; i++) Assert.False(shown[i] && shown[i + 1]);
        }

        // ---- the two sections
        [Fact]
        public void StandsOutIsAFramedListAndEmptyWithoutFacts()
        {
            Assert.Equal("", PromptBlocks.StandsOut(new string[0]));
            string s = PromptBlocks.StandsOut(new[] { "The room is filthy.", "It is night." });
            Assert.Contains("=== WHAT STANDS OUT RIGHT NOW ===", s);
            Assert.Contains("- The room is filthy.", s); Assert.Contains("- It is night.", s);
        }

        [Fact]
        public void TheListenerSectionHoldsWhoTheyAreAndWhatWasLastSaid()
        {
            string s = PromptBlocks.ListenerSection("Bram", new[] { "Doing: hauling steel", "You and them: friend, you like them" }, new[] { "Ana: Morning.", "Bram: Hm." });
            Assert.Contains("=== WHO YOU ARE TALKING TO ===", s);
            Assert.Contains("Name: Bram", s); Assert.Contains("Doing: hauling steel", s);
            Assert.Contains("- Ana: Morning.", s); Assert.Contains("- Bram: Hm.", s);
            Assert.Equal("", PromptBlocks.ListenerSection("", new string[0], new string[0]));
        }

        [Fact]
        public void TheListenerCardSaysRelationAndOpinionInWordsNeverNumbers()
        {
            var me = JObject.Parse(@"{'id':1,'name':{'first':'Ana'},'relations':[{'pawn':2,'kind':'friend','opinion':65}]}");
            var them = JObject.Parse(@"{'id':2,'name':{'first':'Bram'},'job':'hauling steel','health':{'conditions':[{'label':'Cut','severity':0.6}],'pain':0.5},'relations':[{'pawn':1,'kind':'friend','opinion':-30}]}");
            string card = string.Join("\n", SalienceFacts.ListenerCard(me, them, 2));
            Assert.Contains("hauling steel", card);
            Assert.Contains("friend", card);
            Assert.Contains("you are close to them", card);
            Assert.Contains("they dislike you", card);                       // what they think of you
            Assert.DoesNotMatch(@"\d", card);
        }

        [Fact]
        public void ThePairKeepsTheLastLinesBetweenTwoPeopleNewestLastAndForgetsOldOnes()
        {
            var fb = new FakeBridge("tok");
            using var tmp = new TempDir();
            var bridge = new BridgeClient(fb, RimWorldAdapter.WireNames, "tok");
            using var d = new Director(new MindConfig { DataDir = tmp.Path, ThoughtEnabled = false }, bridge, new FakeLlm(), new RimWorldAdapter());
            d.RememberPair(1, 2, "Morning.", 1000); d.RememberPair(2, 1, "Hm.", 1100); d.RememberPair(1, 2, "Soup?", 1200); d.RememberPair(2, 1, "Sure.", 1300);
            var l = d.PairLines(2, 1, 1400);
            Assert.True(l.Count >= 2 && l.Count <= 3);
            Assert.Equal((2, "Sure."), l.Last());                            // newest last, either order of the pair
            d.RememberPair(1, 2, "", 1500);                                  // nothing said, nothing kept
            Assert.Equal((2, "Sure."), d.PairLines(1, 2, 1500).Last());
            Assert.Empty(d.PairLines(1, 2, 1000000));                        // far later: forgotten
            Assert.Empty(d.PairLines(1, 3, 1400));                           // another pair
        }

        // ---- vanilla interactions
        [Theory]
        [InlineData("Chitchat"), InlineData("DeepTalk")]
        public void ChitchatAndDeepTalkCarryNoTopicFromTheGameLog(string key)
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla(key, "Ana chatted with Bram about granite.")));
            string req = Ad.VanillaRequest(info, "Ana", "Bram");
            Assert.DoesNotContain("granite", req);
            Assert.DoesNotContain("The game's own log", req);
            Assert.Contains("Tone:", req);
            string trig = Ad.VanillaTrigger(info, "Ana", "Bram");
            Assert.DoesNotContain("granite", trig);
            Assert.Contains("Bram", trig);
        }

        [Fact]
        public void InsultAndSlightKeepTheLogTopicOnlyIfItFits()
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla("Insult", "Ana insulted Bram's cooking.")));
            string req = Ad.VanillaRequest(info, "Ana", "Bram");
            Assert.Contains("Ana insulted Bram's cooking.", req);
            Assert.Contains("only if it fits", req);
            Assert.Contains("Tone: cutting", req);
        }

        [Fact]
        public void TheOutcomeTheGameCollectedIsStatedInTheRequest()
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla("Insult", "Ana insulted Bram.", new JArray("Insulted")))); 
            Assert.Contains("How it landed", Ad.VanillaRequest(info, "Ana", "Bram"));
            Assert.Contains("Insulted", Ad.VanillaRequest(info, "Ana", "Bram"));
            var none = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla("Insult", "Ana insulted Bram.")));
            Assert.DoesNotContain("How it landed", Ad.VanillaRequest(none, "Ana", "Bram"));
        }

        // ---- length
        [Fact]
        public void TheLengthRulesAreSixtyWordsAndNinetySixTokensWithAHintByTheMoment()
        {
            Assert.Equal(60, Sanitize.MaxWords);
            Assert.Equal(96, new LlmConfig().LineMaxTokens);
            Assert.Contains("a few words or one short sentence", PromptBlocks.LengthHint(false));
            Assert.Contains("three short sentences", PromptBlocks.LengthHint(true));
            Assert.Contains("a few words", PromptBlocks.LineRequest("Ana", false));
            Assert.Contains("three short sentences", PromptBlocks.LineRequest("Ana", true));
            Assert.EndsWith("One line only.", PromptBlocks.LineRequest("Ana", true));
            Assert.Contains("up to three short sentences", Ad.BlockA);
            Assert.Contains("Never more than 60 words", Ad.BlockA);
            Assert.DoesNotContain("Never more than 35", Ad.BlockA);
        }

        [Fact]
        public void ALongLineUnderSixtyWordsPassesAndOverSixtyIsDropped()
        {
            var names = new HashSet<string>();
            var ok = Sanitize.SanitizeLine(string.Join(" ", Enumerable.Repeat("word", 55)), new HashSet<string> { "Ana" }, new HashSet<string> { "Bram" }, new HashSet<string> { "Ana", "Bram" }, names);
            Assert.NotNull(ok.Text);
            var bad = Sanitize.SanitizeLine(string.Join(" ", Enumerable.Repeat("word", 65)), new HashSet<string> { "Ana" }, new HashSet<string> { "Bram" }, new HashSet<string> { "Ana", "Bram" }, names);
            Assert.Equal("too_long", bad.Reason);
        }

        [Theory]
        [InlineData("death", 0.2, true)]
        [InlineData("raid_start", 0.2, true)]
        [InlineData("injury", 0.1, true)]
        [InlineData("weather", 0.2, false)]
        [InlineData("weather", 0.8, true)]
        [InlineData("work", 0.3, false)]
        public void OnlyAMomentThatMattersEarnsALongerLine(string type, double severity, bool expect)
        {
            var ev = new JObject { ["type"] = type, ["severity"] = severity, ["data"] = new JObject() };
            Assert.Equal(expect, Ad.EarnsLength(ev));
        }

        [Fact]
        public void ADeepTalkEarnsLengthButChitchatDoesNot()
        {
            Assert.True(Ad.EarnsLength(Ad.NormalizeEvent(Vanilla("DeepTalk", "x"))));
            Assert.False(Ad.EarnsLength(Ad.NormalizeEvent(Vanilla("Chitchat", "x"))));
        }
    }
}

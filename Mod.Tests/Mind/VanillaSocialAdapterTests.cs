// Ports of the adapter-only parts of agent/tests/test_vanilla_social.py (the changed and new tests that need no Director:
// the wording of the vanilla request, the no-meta rule in the prompts, the chitchat memory text, drops_meta).
// The ones that run the director (insult line, tiers, dropped lines, meta thought/vanilla line dropped) are deferred, see
// docs/csmind/parity-delta.md.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class VanillaSocialAdapterTests
    {
        private static readonly RimWorldAdapter Ad = new RimWorldAdapter();

        private static JObject Vanilla(string interaction = "Insult", string label = "insult", string text = "Ana insulted Bram's cooking.", string tier = "high", string refId = "v1-1")
        {
            var fb = new FakeBridge();
            return fb.AddEvent("social", new[] { new KeyValuePair<int, string>(1, "initiator"), new KeyValuePair<int, string>(2, "recipient") },
                "Ana -> Bram: " + label + ".", new[] { 1, 2 }, 0.5,
                data: new JObject { ["interaction"] = interaction, ["interaction_label"] = label, ["vanilla"] = true, ["ref"] = refId, ["tier"] = tier, ["vanilla_text"] = text });
        }

        [Theory]
        [InlineData("KindWords", "kind words")]
        [InlineData("Slight", "slights")]
        [InlineData("DeepTalk", "personal")]
        [InlineData("Chitchat", "passing exchange")]
        public void PromptFitsEachKind(string key, string word)
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla(key, key.ToLowerInvariant())));
            Assert.Contains(word, Ad.VanillaRequest(info, "Ana", "Bram"));
        }

        [Fact]
        public void UnknownInteractionUsesItsLabel()
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla("ModdedThing", "stare awkwardly")));
            Assert.Contains("stare awkwardly", Ad.VanillaRequest(info, "Ana", "Bram"));
        }

        [Fact]
        public void ChitchatRequestDescribesTheSituationNotTheName()
        {
            var info = Ad.VanillaSocial(Ad.NormalizeEvent(Vanilla("Chitchat", "chitchat", "Ana chatted with Bram.", "idle")));
            string req = Ad.VanillaRequest(info, "Ana", "Bram").ToLowerInvariant();
            Assert.True(!req.Contains("small talk") && !req.Contains("chitchat") && !req.Contains("small-talk"));
            Assert.Contains("whatever is around", req);
            Assert.Contains("don't remark on the conversation or the act of talking itself", req);
        }

        [Fact]
        public void TheNoMetaRuleIsInTheSpeechThoughtDiaryAndReactionPrompts()
        {
            string rule = "Don't remark on the conversation or the act of talking itself; react to the people and what's happening.";
            foreach (var text in new[] { Ad.BlockA, Ad.ThoughtRequest, Ad.DiaryRequest, Ad.ReactionRequest }) Assert.Contains(rule, text);
        }

        [Fact]
        public void ChitchatMemoryTextDoesNotNameTheInteraction()
        {
            var fb = new FakeBridge();
            var e = Ad.NormalizeEvent(fb.AddEvent("social", new[] { new KeyValuePair<int, string>(1, "initiator"), new KeyValuePair<int, string>(2, "recipient") },
                "Ana -> Bram: Chitchat.", new[] { 1, 2 }, 0.1, data: new JObject { ["interaction"] = "Chitchat", ["interaction_label"] = "Chitchat" }));
            string t = Ad.FirstPerson(e, 1);
            Assert.True(!t.ToLowerInvariant().Contains("chitchat") && t.Contains("Ana and Bram"));
            Assert.Contains("the work, the food, the weather", t);      // what it was about, so the thought has a subject other than the talking
        }

        [Theory]
        [InlineData("I wish people would cut this chitchat.")]
        [InlineData("Enough small talk already.")]
        [InlineData("All this talk, talk, talk.")]
        [InlineData("Why does everyone chat so much? Enough chatter.")]
        public void DropsMetaOnlyWithAChitchatScene(string text)
        {
            var chat = new[] { "I was part of it: Ana and Bram had a passing exchange." };
            Assert.True(Ad.DropsMeta(text, chat), text);
            Assert.False(Ad.DropsMeta(text, new[] { "I was part of it: Ana -> Bram: Insult." }), text);      // an insult scene keeps it
            Assert.False(Ad.DropsMeta(text, new string[0]), text);
            Assert.False(Ad.DropsMeta("The stew smells burnt again.", chat));
        }
    }
}

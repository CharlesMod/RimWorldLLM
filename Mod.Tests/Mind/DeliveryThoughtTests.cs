// Port of the test_delivery_thought.py tests that need no Director (parse + grammar); the world tests are deferred.
using System.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class DeliveryThoughtTests
    {
        static readonly System.Collections.Generic.IList<string> LABELS = new RimWorldAdapter().IntentLabels.ToList();

        [Fact]
        public void IntentAndDeliveryParseFromOneCompletion()
        {
            Assert.Equal("insult", Passes.ParseIntent("insult shout", LABELS)); Assert.Equal("shout", Passes.ParseDelivery("insult shout"));
            Assert.Equal("whisper", Passes.ParseDelivery("none whisper"));
            Assert.Null(Passes.ParseDelivery("kind")); Assert.Null(Passes.ParseDelivery("kind loud"));
            Assert.Equal("none", Passes.ParseIntent("none normal", LABELS));
        }

        [Fact]
        public void GrammarIsOneConstrainedCompletionAndOldFormIsKept()
        {
            string g = PromptBlocks.GrammarFor(LABELS, PromptBlocks.Deliveries);
            Assert.StartsWith("root ::= intent \" \" delivery", g); Assert.Contains("\"shout\"", g); Assert.Contains("\"insult\"", g);
            Assert.Equal("root ::= " + string.Join(" | ", LABELS.Select(i => "\"" + i + "\"")), PromptBlocks.GrammarFor(LABELS));
        }
    }
}

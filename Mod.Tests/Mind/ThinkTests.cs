using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class ThinkTests
    {
        // expected values recorded from CPython llm.strip_thought (no Python test exists for it directly)
        [Theory]
        [InlineData("<|channel>thought\nx<channel|>Hi<think>a\nb</think> there", "Hi there")]
        [InlineData("<think>unclosed", "<think>unclosed")]
        [InlineData("a<|channel>b", "a<|channel>b")]
        public void StripThoughtMatchesPython(string raw, string want) { Assert.Equal(want, Think.StripThought(raw)); }
    }
}

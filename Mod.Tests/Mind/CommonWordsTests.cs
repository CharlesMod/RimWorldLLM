using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class CommonWordsTests
    {
        [Fact]
        public void SetMatchesPython()
        {
            Assert.Equal(947, CommonWords.CommonSentenceStart.Count);    // len() recorded from CPython
            foreach (var w in new[] { "it", "it's", "stance", "dinner", "rice", "quiet", "that" }) Assert.Contains(w, CommonWords.CommonSentenceStart);
            Assert.DoesNotContain("zed", CommonWords.CommonSentenceStart);
        }
    }
}

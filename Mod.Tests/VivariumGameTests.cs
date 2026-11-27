using System.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VivariumGameTests
    {
        [Fact]
        public void HourRangeWrapsPastMidnight()
        {
            Assert.Equal(new[] { 22, 23, 0, 1, 2, 3, 4, 5 }, VivariumHandsPure.HourRange(22, 6));
            Assert.Equal(new[] { 8, 9, 10 }, VivariumHandsPure.HourRange(8, 11));
            Assert.Equal(24, VivariumHandsPure.HourRange(0, 24).Count);
            Assert.Equal(new[] { 23 }, VivariumHandsPure.HourRange(23, 24));
        }

        [Fact]
        public void HourRangeRejectsBadInput()
        {
            Assert.Empty(VivariumHandsPure.HourRange(5, 5));
            Assert.Empty(VivariumHandsPure.HourRange(-1, 5));
            Assert.Empty(VivariumHandsPure.HourRange(24, 5));
            Assert.Empty(VivariumHandsPure.HourRange(3, 25));
        }

        [Fact]
        public void AtLinesSplitNameAndText()
        {
            string n, r;
            Assert.True(ChatParse.TryAt("@Ana  how are you?", out n, out r));
            Assert.Equal("Ana", n); Assert.Equal("how are you?", r);
            Assert.True(ChatParse.TryAt("  @bo", out n, out r));
            Assert.Equal("bo", n); Assert.Equal("", r);
            Assert.False(ChatParse.TryAt("hello @Ana", out n, out r));
            Assert.False(ChatParse.TryAt("@", out n, out r));
            Assert.False(ChatParse.TryAt(null, out n, out r));
        }

        [Fact]
        public void NameMatchIsCaseInsensitivePrefixAndNeverAmbiguous()
        {
            var names = new[] { "Ana", "Anders", "Bo" };
            Assert.Equal(0, ChatParse.Match("ana", names));      // exact wins over the prefix of Anders
            Assert.Equal(2, ChatParse.Match("b", names));
            Assert.Equal(-1, ChatParse.Match("An", names));      // ambiguous
            Assert.Equal(-1, ChatParse.Match("Zed", names));
            Assert.Equal(-1, ChatParse.Match("", names));
        }
    }
}

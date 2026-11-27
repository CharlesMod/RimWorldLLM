using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ChatKindsTests
    {
        [Theory]
        [InlineData("player", "talk")]
        [InlineData("colonist", "talk")]
        [InlineData("thought", "thoughts")]
        [InlineData("dream", "thoughts")]
        [InlineData("diary", "thoughts")]
        [InlineData("narrator", "narrator")]
        [InlineData("note", "narrator")]
        [InlineData("whatever", "narrator")]
        [InlineData("action", "actions")]
        [InlineData("refused", "actions")]
        [InlineData("chronicle", "story")]
        [InlineData("recap", "story")]
        [InlineData("art", "story")]
        [InlineData("camera", "story")]
        [InlineData("comment", "story")]
        public void GroupOfKind(string who, string group) { Assert.Equal(group, ChatKinds.Group(who)); }

        [Fact]
        public void ListenerRoundTripsAndIsOmittedWhenEmpty()
        {
            var log = new ChatLog();
            var a = log.Add("colonist", "Pass the salt.", 5, 1, "Ana", "Bram");
            var b = log.Add("thought", "Cold again.", 6, 1, "Ana");
            Assert.Equal("Bram", (string)a.ToJson()["listener"]);
            Assert.Null(b.ToJson()["listener"]);
            var back = new ChatLog();
            back.Import(log.Export());
            Assert.Equal("Bram", back.Entries[0].Listener);
            Assert.Equal("", back.Entries[1].Listener);
        }
    }
}

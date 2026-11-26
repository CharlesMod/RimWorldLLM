using System.Linq;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class ArtTextTests
    {
        [Fact] public void MaterialStripsTagsAndNull()
        {
            Assert.Equal("steel", ArtText.Material("<color=red>steel</color>"));
            Assert.Equal("", ArtText.Material(null));
        }

        [Fact] public void TaleNullWhenEmpty()
        {
            Assert.Null(ArtText.Tale(null, "  ", 3, new[] { "Ann" }));
        }

        [Fact] public void TaleShapesFields()
        {
            var t = ArtText.Tale("Raid", "<b>Ann</b> fought.", 12, new[] { "Ann", "", "Bob", "Cy", "Di" });
            Assert.Equal("Raid", (string)t["def"]);
            Assert.Equal("Ann fought.", (string)t["summary"]);
            Assert.Equal(12, (int)t["day"]);
            Assert.Equal(3, t["pawns"].Count());
            Assert.Equal("Cy", (string)t["pawns"][2]);
        }

        [Fact] public void TaleCapsAndDropsDay()
        {
            var t = ArtText.Tale("x", new string('a', 500), -1, null);
            Assert.True(((string)t["summary"]).Length <= ArtText.SummaryMax);
            Assert.Null(t["day"]);
            Assert.Empty(t["pawns"]);
        }
    }
}

using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class FirstCuesTests
    {
        [Theory]
        [InlineData("ObservedLayingCorpse", "corpse")]
        [InlineData("ObservedLayingRottingCorpse", "corpse")]
        [InlineData("AteWithoutTable", "no_table")]
        [InlineData("Catharsis", null)]
        public void ThoughtCues(string def, string cue) { Assert.Equal(cue, FirstCues.ThoughtCue(def)); }

        [Theory]
        [InlineData("Eclipse", "eclipse")]
        [InlineData("Aurora", "aurora")]
        [InlineData("SolarFlare", "solar_flare")]
        [InlineData("HeatWave", null)]
        public void ConditionCues(string def, string cue) { Assert.Equal(cue, FirstCues.ConditionCue(def)); }

        [Fact]
        public void WeatherAndRoom()
        {
            Assert.Equal("snow", FirstCues.WeatherCue("SnowGentle"));
            Assert.Equal("snow", FirstCues.WeatherCue("SnowHard"));
            Assert.Null(FirstCues.WeatherCue("Rain"));
            Assert.Null(FirstCues.RoomCue(64.9f));
            Assert.Equal("beautiful_room", FirstCues.RoomCue(65f));
        }

        [Fact]
        public void SummariesExistForEveryCue()
        {
            foreach (var c in new[] { "snow", "eclipse", "aurora", "solar_flare", "corpse", "beautiful_room", "no_table" })
                Assert.False(string.IsNullOrEmpty(FirstCues.Summary(c, "Ann")));
            Assert.Null(FirstCues.Summary("nope", "Ann"));
        }

        [Fact]
        public void SeenReportsOnce()
        {
            var s = new FirstCues.Seen();
            Assert.True(s.First(3, "snow"));
            Assert.False(s.First(3, "snow"));
            Assert.True(s.First(4, "snow"));
            s.Clear();
            Assert.True(s.First(3, "snow"));
        }
    }
}

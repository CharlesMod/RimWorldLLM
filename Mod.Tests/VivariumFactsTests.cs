using RimWorldLLM;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VivariumFactsTests
    {
        [Theory]
        [InlineData("TemperateForest", "temperate_forest")]
        [InlineData("BorealForest", "boreal_forest")]
        [InlineData("IceSheet", "ice_sheet")]
        [InlineData("SeaIce", "sea_ice")]
        [InlineData("ToxicFallout", "toxic_fallout")]
        [InlineData("Tundra", "tundra")]
        [InlineData("Temperate Forest", "temperate_forest")]
        [InlineData("cold_snap", "cold_snap")]
        [InlineData("PsychicDrone", "psychic_drone")]
        [InlineData("GU_Thing", "gu_thing")]
        [InlineData("XMLThing", "xml_thing")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void SnakeCase(string input, string expected) { Assert.Equal(expected, FactsPure.SnakeCase(input)); }

        [Theory]
        [InlineData("Spring", "spring")]
        [InlineData("Summer", "summer")]
        [InlineData("Fall", "fall")]
        [InlineData("Winter", "winter")]
        [InlineData("PermanentSummer", "summer")]
        [InlineData("PermanentWinter", "winter")]
        [InlineData("Undefined", null)]
        [InlineData(null, null)]
        public void SeasonKey(string input, string expected) { Assert.Equal(expected, FactsPure.SeasonKey(input)); }

        [Theory]
        [InlineData("Cassandra", "cassandra")]
        [InlineData("Phoebe", "phoebe")]
        [InlineData("Randy", "randy")]
        [InlineData("Tynan", "other")]
        [InlineData(null, "other")]
        public void StorytellerKey(string input, string expected) { Assert.Equal(expected, FactsPure.StorytellerKey(input)); }
    }
}

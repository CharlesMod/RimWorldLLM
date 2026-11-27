using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VivariumTradeKeysTests
    {
        [Theory]
        [InlineData("T4321", 'T', 4321)]
        [InlineData("p5521", 'P', 5521)]
        [InlineData(" C88 ", 'C', 88)]
        public void ParsesKeys(string s, char k, int n)
        {
            Assert.True(TradeKeys.TryParse(s, out var kind, out var num));
            Assert.Equal(k, kind); Assert.Equal(n, num);
        }

        [Theory]
        [InlineData("")] [InlineData("T")] [InlineData("T-4")] [InlineData("12")] [InlineData("Tx1")]
        public void RejectsBadKeys(string s) { Assert.False(TradeKeys.TryParse(s, out _, out _)); }

        [Fact]
        public void TrailingNumber()
        {
            Assert.Equal(31, TradeKeys.TrailingNumber("PassingShip_31"));
            Assert.Equal(-1, TradeKeys.TrailingNumber("PassingShip_"));
            Assert.Equal(-1, TradeKeys.TrailingNumber(null));
            Assert.Equal("S31", TradeKeys.Make('S', 31));
        }

        [Theory]
        [InlineData("Pemmican", true, false, false, "pemmican")]
        [InlineData("MealSurvivalPack", true, true, false, "survival")]
        [InlineData("Kibble", false, false, true, "kibble")]
        [InlineData("MealFine", true, true, false, "meal")]
        [InlineData("RawPotatoes", true, false, false, "raw")]
        [InlineData("Steel", false, false, false, null)]
        public void FoodKind(string def, bool edible, bool meal, bool kibble, string want)
        {
            Assert.Equal(want, FoodKinds.Classify(def, edible, meal, kibble, false));
        }
    }
}

using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class NeverSellListTests
    {
        [Fact]
        public void SplitsTrimsAndDropsEmpties()
        {
            Assert.Equal(new[] { "Mortar shell", "Neutroamine", "Gold" }, NeverSellList.Parse(" Mortar shell, Neutroamine ,, ,Gold,"));
        }

        [Fact]
        public void NullOrBlankGivesEmpty()
        {
            Assert.Empty(NeverSellList.Parse(null));
            Assert.Empty(NeverSellList.Parse(""));
            Assert.Empty(NeverSellList.Parse(" , "));
        }
    }
}

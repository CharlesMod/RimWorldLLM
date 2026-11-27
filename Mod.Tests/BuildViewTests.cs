using System.Collections.Generic;
using System.Linq;
using RimWorldLLM;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class BuildViewTests
    {
        static MissingInput Full() { return new MissingInput { Colonists = 5, HasStove = true, HasFoodStockpile = true, HasFreezer = true, CanBuildCooler = true, HasDiningTable = true, HasGrowZone = true, HasResearchBench = true }; }

        [Fact]
        public void NothingMissingWhenEquipped() { Assert.Empty(BuildView.Missing(Full())); }

        [Fact]
        public void NoColonistsNoList()
        {
            var i = new MissingInput();
            Assert.Empty(BuildView.Missing(i));
            Assert.Empty(BuildView.Missing(null));
        }

        [Fact]
        public void BuildOrder()
        {
            var i = new MissingInput { Colonists = 5, ColonistsWithoutRoofedRoom = 5, ColonistsWithoutBed = 5, CanBuildCooler = true };
            Assert.Equal(new[] { "shelter", "beds for 5 colonists", "stove", "food stockpile", "freezer", "dining table", "growing zone", "research bench" }, BuildView.Missing(i));
        }

        [Fact]
        public void PartialShelterNamesCount()
        {
            var i = Full(); i.ColonistsWithoutRoofedRoom = 1;
            Assert.Equal(new[] { "shelter for 1 colonist" }, BuildView.Missing(i));
        }

        [Fact]
        public void FreeBedsOffsetNeed()
        {
            var i = Full(); i.ColonistsWithoutBed = 3; i.BedsFree = 1;
            Assert.Equal(new[] { "beds for 2 colonists" }, BuildView.Missing(i));
            i.BedsFree = 3;
            Assert.Empty(BuildView.Missing(i));
            i.BedsFree = 0; i.ColonistsWithoutBed = 1;
            Assert.Equal(new[] { "beds for 1 colonist" }, BuildView.Missing(i));
        }

        [Fact]
        public void FreezerOnlyWhenCoolerBuildable()
        {
            var i = Full(); i.HasFreezer = false; i.CanBuildCooler = false;
            Assert.Empty(BuildView.Missing(i));
            i.CanBuildCooler = true;
            Assert.Equal(new[] { "freezer" }, BuildView.Missing(i));
        }

        [Fact]
        public void CapHolds()
        {
            Assert.Equal(2, BuildView.Cap(new[] { 1, 2, 3 }, 2).Count);
            Assert.Empty(BuildView.Cap<int>(null, 3));
            Assert.Empty(BuildView.Cap(new[] { 1 }, 0));
        }

        [Fact]
        public void HomeCenterIsMean()
        {
            Assert.Null(BuildView.HomeCenter(new List<int[]>()));
            Assert.Equal(new[] { 11, 20 }, BuildView.HomeCenter(new List<int[]> { new[] { 10, 18 }, new[] { 12, 22 } }));
        }

        [Fact]
        public void OresGroupedNearestFirstAndCapped()
        {
            var cells = new List<OreCell>
            {
                new OreCell("steel", 118, 98), new OreCell("steel", 120, 116), new OreCell("steel", 121, 99),
                new OreCell("components", 160, 98), new OreCell("gold", 300, 300), new OreCell("", 121, 98),
            };
            var g = BuildView.GroupOres(cells, 120, 98);
            Assert.Equal(new[] { "steel", "components" }, g.Select(x => x.Ore));
            Assert.Equal(3, g[0].Cells); Assert.Equal(1, g[0].Dist);
            Assert.Equal(1, g[1].Cells); Assert.Equal(40, g[1].Dist);
        }

        [Fact]
        public void OreCapAndEmpty()
        {
            var cells = Enumerable.Range(0, 20).Select(n => new OreCell("ore" + n, 100 + n, 100)).ToList();
            Assert.Equal(6, BuildView.GroupOres(cells, 100, 100).Count);
            Assert.Empty(BuildView.GroupOres(null, 0, 0));
        }
    }
}

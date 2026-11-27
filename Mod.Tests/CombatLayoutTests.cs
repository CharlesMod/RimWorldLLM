using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class CombatLayoutTests
    {
        static bool Has(List<int[]> l, int x, int z) { return l.Any(c => c[0] == x && c[1] == z); }

        [Fact]
        public void KillboxNorthGeometry()
        {
            var l = DefenseLayout.Killbox(10, 10, "n");
            Assert.Equal(10, l.Walls.Count);
            for (int z = 11; z <= 15; z++) { Assert.True(Has(l.Walls, 9, z)); Assert.True(Has(l.Walls, 11, z)); }
            Assert.Equal(2, l.Traps.Count); Assert.True(Has(l.Traps, 10, 12)); Assert.True(Has(l.Traps, 10, 14));
            Assert.Equal(7, l.Sandbags.Count);
            Assert.All(l.Sandbags, c => Assert.Equal(4, c[1]));
            Assert.Equal(Enumerable.Range(7, 7), l.Sandbags.Select(c => c[0]).OrderBy(x => x));
            Assert.Equal(7, l.Shooters.Count);
            Assert.All(l.Shooters, c => Assert.Equal(3, c[1]));
            Assert.Equal(10, l.Shooters[0][0]); // middle first
            Assert.Equal(2, l.Turrets.Count); Assert.True(Has(l.Turrets, 6, 4)); Assert.True(Has(l.Turrets, 14, 4));
        }

        [Theory]
        [InlineData("n", 0, 1)]
        [InlineData("e", 1, 0)]
        [InlineData("s", 0, -1)]
        [InlineData("w", -1, 0)]
        public void KillboxFacesOutwardInEveryDirection(string dir, int ox, int oz)
        {
            var l = DefenseLayout.Killbox(0, 0, dir);
            // outside cells lie out along outward, inside cells (sandbags, shooters) lie the other way
            Assert.All(l.Walls, c => Assert.True(c[0] * ox + c[1] * oz >= 1 && c[0] * ox + c[1] * oz <= 5));
            Assert.All(l.Traps, c => { Assert.Equal(0, c[0] * oz == 0 && c[1] * ox == 0 ? 0 : 1); Assert.True(c[0] * ox + c[1] * oz >= 2); });
            Assert.All(l.Sandbags, c => Assert.Equal(-6, c[0] * ox + c[1] * oz));
            Assert.All(l.Shooters, c => Assert.Equal(-7, c[0] * ox + c[1] * oz));
            Assert.All(l.Turrets, c => Assert.Equal(-6, c[0] * ox + c[1] * oz));
            // the sandbag line is 7 wide centred on the door's axis; turrets one past each end
            int along(int[] c) { return System.Math.Abs(c[0] * oz) + System.Math.Abs(c[1] * ox) == 0 ? 0 : (c[0] != 0 && oz != 0 ? c[0] : c[1]); }
            Assert.Equal(new[] { -3, -2, -1, 0, 1, 2, 3 }, l.Sandbags.Select(along).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { -4, 4 }, l.Turrets.Select(along).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void KillboxHonoursSizesAndTrapCap()
        {
            var l = DefenseLayout.Killbox(0, 0, "n", funnel: 8, depth: 3, width: 5, maxTraps: 2);
            Assert.Equal(16, l.Walls.Count); Assert.Equal(2, l.Traps.Count);
            Assert.Equal(5, l.Sandbags.Count); Assert.All(l.Sandbags, c => Assert.Equal(-4, c[1]));
            Assert.Empty(DefenseLayout.Killbox(0, 0, "x").Walls);
        }

        [Fact]
        public void LineAcrossIsPerpendicularAndCentred()
        {
            var ns = DefenseLayout.LineAcross(10, 10, "n", 5);
            Assert.Equal(5, ns.Count); Assert.All(ns, c => Assert.Equal(10, c[1]));
            Assert.Equal(new[] { 8, 9, 10, 11, 12 }, ns.Select(c => c[0]).OrderBy(x => x).ToArray());
            var ew = DefenseLayout.LineAcross(10, 10, "e", 3);
            Assert.All(ew, c => Assert.Equal(10, c[0]));
            Assert.Equal(new[] { 9, 10, 11 }, ew.Select(c => c[1]).OrderBy(x => x).ToArray());
            Assert.Empty(DefenseLayout.LineAcross(0, 0, "n", 0));
        }

        [Fact]
        public void TrapsOnPathSkipsDoorsAndHonoursSkipAndEvery()
        {
            var g = CombatGrid.FromAscii("..........");
            g.Set(5, 0, CellFlag.Door | CellFlag.Walkable);
            var path = Enumerable.Range(0, 10).Select(x => new[] { x, 0 }).ToList();
            var t = DefenseLayout.TrapsOnPath(g, path, 10);
            Assert.Equal(new[] { 3, 7, 9 }, t.Select(c => c[0]).ToArray());
            Assert.Equal(new[] { 3, 7 }, DefenseLayout.TrapsOnPath(g, path, 2).Select(c => c[0]).ToArray());
        }

        [Fact]
        public void BuildableFilterDropsWallsDoorsFogAndOutside()
        {
            var g = CombatGrid.FromAscii("#D.f", "....");
            int dropped;
            var kept = DefenseLayout.Buildable(g, new List<int[]> { new[] { 0, 1 }, new[] { 1, 1 }, new[] { 2, 1 }, new[] { 3, 1 }, new[] { 0, 0 }, new[] { 9, 9 } }, out dropped);
            Assert.Equal(4, dropped);
            Assert.Equal(2, kept.Count); Assert.True(Has(kept, 2, 1)); Assert.True(Has(kept, 0, 0));
        }

        [Fact]
        public void TurretSpotsSeeThePathAreSpacedAndOutdoors()
        {
            var rows = Enumerable.Repeat("...............", 15).ToArray();
            var g = CombatGrid.FromAscii(rows);
            var path = Enumerable.Range(10, 5).Select(z => new[] { 7, z }).ToList();
            var spots = DefenseLayout.TurretSpots(g, 7, 5, 3, path, 14, 3);
            Assert.Equal(3, spots.Count);
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.True(BuildHelpers.Dist2(spots[i][0], spots[i][1], spots[j][0], spots[j][1]) >= 9);
            var indoors = CombatGrid.FromAscii(Enumerable.Repeat("rrrrrrrrrrrrrrr", 15).ToArray());
            Assert.Empty(DefenseLayout.TurretSpots(indoors, 7, 5, 3, path, 14, 3));
            // a wall hides the path
            var walled = CombatGrid.FromAscii(Enumerable.Range(0, 15).Select(r => r == 6 ? "###############" : "...............").ToArray());
            Assert.Empty(DefenseLayout.TurretSpots(walled, 7, 3, 2, path, 14, 3));
        }
    }
}

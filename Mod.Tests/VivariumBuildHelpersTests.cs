using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VivariumBuildHelpersTests
    {
        static KeyValuePair<string, int> K(string s, int n) { return new KeyValuePair<string, int>(s, n); }

        [Fact]
        public void MeanRoundsAndHandlesEmpty()
        {
            int x, z;
            Assert.False(BuildHelpers.Mean(new List<int[]>(), out x, out z));
            Assert.True(BuildHelpers.Mean(new[] { new[] { 0, 0 }, new[] { 3, 5 } }, out x, out z));
            Assert.Equal(2, x); Assert.Equal(3, z); // 1.5 and 2.5 round up
        }

        [Fact]
        public void NearestFirstOrdersAndCaps()
        {
            var cells = new[] { new[] { 10, 10 }, new[] { 1, 1 }, new[] { 2, 2 }, new[] { 0, 0 } };
            var r = BuildHelpers.NearestFirst(cells, 0, 0, 3);
            Assert.Equal(3, r.Count);
            Assert.Equal(new[] { 0, 0 }, r[0]); Assert.Equal(new[] { 1, 1 }, r[1]); Assert.Equal(new[] { 2, 2 }, r[2]);
            Assert.Empty(BuildHelpers.NearestFirst(cells, 0, 0, 0));
        }

        [Fact]
        public void RotationOrderSouthFirstUnlessGiven()
        {
            Assert.Equal(new[] { 2, 0, 1, 3 }, BuildHelpers.RotOrder(-1));
            Assert.Equal(new[] { 1 }, BuildHelpers.RotOrder(1));
        }

        [Fact]
        public void PickStuffRankedPrefersBlocksThenWoodThenSteel()
        {
            var l = new List<KeyValuePair<string, int>> { K("Steel", 900), K("WoodLog", 400), K("BlocksGranite", 100) };
            Assert.Equal("BlocksGranite", BuildHelpers.PickStuff(l, 75, true));
            Assert.Equal("WoodLog", BuildHelpers.PickStuff(l, 150, true));
            Assert.Equal("Steel", BuildHelpers.PickStuff(l, 500, true));
        }

        [Fact]
        public void PickStuffUnrankedAndShortStock()
        {
            var l = new List<KeyValuePair<string, int>> { K("Steel", 90), K("WoodLog", 400), K("Gold", 0) };
            Assert.Equal("WoodLog", BuildHelpers.PickStuff(l, 10, false));
            Assert.Equal("WoodLog", BuildHelpers.PickStuff(l, 1000, true));   // none enough: most plentiful
            Assert.Null(BuildHelpers.PickStuff(new List<KeyValuePair<string, int>> { K("Gold", 0) }, 5, true));
            Assert.Null(BuildHelpers.PickStuff(new List<KeyValuePair<string, int>>(), 5, false));
        }

        [Fact]
        public void UnknownListsClosestWords()
        {
            string s = BuildHelpers.Unknown("thing", "stoev", new[] { "wall", "stove", "door", "bed" }, 2);
            Assert.StartsWith("no thing 'stoev' (try: stove", s);
            Assert.Equal("no plant 'x'", BuildHelpers.Unknown("plant", "x", new string[0]));
        }

        [Fact]
        public void WallScorePrefersWallsAndKeepsDoorClear()
        {
            var room = new CellBox(0, 0, 7, 7);               // inside 1..5
            Assert.Equal(2, BuildHelpers.WallScore(1, 3, room, 3, 0));
            Assert.Equal(0, BuildHelpers.WallScore(3, 3, room, 3, 0));
            Assert.Equal(-1, BuildHelpers.WallScore(3, 1, room, 3, 0));   // next to door
            Assert.Equal(-1, BuildHelpers.WallScore(0, 3, room, 3, 0));   // wall cell, not inside
        }

        [Fact]
        public void CircleAndWhereCellsClipToMap()
        {
            var c = BuildHelpers.Circle(0, 0, 2, 100, 100);
            Assert.Contains(c, p => p[0] == 0 && p[1] == 0);
            Assert.DoesNotContain(c, p => p[0] < 0 || p[1] < 0);
            Assert.DoesNotContain(c, p => p[0] == 2 && p[1] == 2);
            var w = new Where { Box = new CellBox(98, 98, 5, 5) };
            Assert.Equal(4, BuildHelpers.WhereCells(w, 100, 100).Count);
            Assert.Null(BuildHelpers.WhereCells(new Where(), 100, 100));
            Assert.NotNull(BuildHelpers.WhereCells(new Where { X = 5, Z = 5, Radius = 1 }, 100, 100));
        }

        [Fact]
        public void MarkBuilderGroupsBlueprintsByDef()
        {
            var b = new BuildHelpers.MarkBuilder(7);
            b.Blueprint("Wall", 1, 1, 10); b.Blueprint("Wall", 2, 1, 11); b.Blueprint("Door", 3, 1, 12);
            Assert.Equal(2, b.Marks.Count);
            var w = b.Marks.First(m => m.Def == "Wall");
            Assert.Equal(7, w.Map); Assert.Equal(2, w.CellCount); Assert.Equal(new[] { 10, 11 }, w.Things.ToArray());
            b.Designation("Mine", 0, 5, 5); b.Designation("Mine", 0, 6, 5); b.Designation("Chop", 99, 0, 0);
            Assert.Equal(2, b.Marks.First(m => m.Def == "Mine").CellCount);
            Assert.Equal(new[] { 99 }, b.Marks.First(m => m.Def == "Chop").Things.ToArray());
            Assert.Equal(3, b.CountOf("blueprint"));
        }

        [Fact]
        public void NotesReadPlainly()
        {
            Assert.Equal("placed a 5x5 bedroom (15 granite walls, a door) at (118,101)",
                BuildHelpers.RoomNote("bedroom", new CellBox(118, 101, 5, 5), 15, "granite", true));
            Assert.Equal("cancelled 15 blueprints, removed 1 zone, 3 already built (left)",
                BuildHelpers.UndoNote(15, 0, 1, 0, 0, 0, 3, 0));
            Assert.Equal("nothing left to undo", BuildHelpers.UndoNote(0, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal("skipped 3 (2 beds, 1 grave)", BuildHelpers.SkipNote(new Dictionary<string, int> { ["beds"] = 2, ["grave"] = 1 }));
            Assert.Equal("", BuildHelpers.SkipNote(new Dictionary<string, int>()));
            Assert.Equal("count 5", BuildHelpers.BillMode("count", 5, 0));
            Assert.Equal("until 15", BuildHelpers.BillMode("until", 0, 15));
            Assert.Equal("forever", BuildHelpers.BillMode("forever", 0, 0));
        }

        [Fact]
        public void SoilScoreRejectsPoorCells()
        {
            Assert.Equal(-1, BuildHelpers.SoilScore(new[] { 1.0, 0.5 }, 0.7));
            Assert.Equal(1.2, BuildHelpers.SoilScore(new[] { 1.0, 1.4 }, 0.7), 5);
            Assert.Equal(-1, BuildHelpers.SoilScore(new double[0], 0.7));
        }

        [Fact]
        public void DumpRingAndCandidates()
        {
            Assert.False(BuildHelpers.InDumpRing(5, 0, 0, 0));
            Assert.True(BuildHelpers.InDumpRing(20, 0, 0, 0));
            Assert.False(BuildHelpers.InDumpRing(40, 0, 0, 0));
            foreach (var c in BuildHelpers.DumpCandidates(100, 100, 4, 4).Take(20))
                Assert.True(BuildHelpers.InDumpRing(c[0] + 2, c[1] + 2, 100, 100));
        }

        [Fact]
        public void FindByNameOrLabelAndCaps()
        {
            var l = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("CookMealSimple", "cook simple meal"), new KeyValuePair<string, string>("Make_Pemmican", "make pemmican") };
            Assert.Equal(0, BuildHelpers.FindByNameOrLabel(l, "cookmealsimple"));
            Assert.Equal(1, BuildHelpers.FindByNameOrLabel(l, "Make Pemmican".Replace("Make Pemmican", "make pemmican")));
            Assert.Equal(-1, BuildHelpers.FindByNameOrLabel(l, "nope"));
            Assert.Equal(200, BuildHelpers.StockpileCap(500));
            Assert.Equal(30, BuildHelpers.StockpileCap(30));
            Assert.Equal(0, BuildHelpers.StockpileCap(-4));
        }

        [Fact]
        public void PlaceMarksFilterByMapAndPairs()
        {
            var m = new NarratorMark { Kind = "blueprint", Map = 2 }; m.AddCell(1, 2); m.AddCell(3, 4);
            Assert.Single(BuildHelpers.OnMap(new[] { m, new NarratorMark { Kind = "zone", Map = 3 } }, 2));
            var pairs = BuildHelpers.CellPairs(m);
            Assert.Equal(2, pairs.Count); Assert.Equal(new[] { 3, 4 }, pairs[1]);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;
using Xunit.Abstractions;

namespace RimWorldLLM.Tests
{
    public class BasePlannerTests
    {
        readonly ITestOutputHelper _out;
        public BasePlannerTests(ITestOutputHelper o) { _out = o; }

        // An early colony: one shelter (barracks) with a south door, a stockpile and a bench outside, a river west.
        static BaseGrid Early()
        {
            var g = BaseGrid.FromAscii(0, 0,
                "~~~.............................",
                "~~~.............................",
                "~~~.............................",
                "~~~.............................",
                "~~~.............................",
                "~~~.............................",
                "~~~.............................",
                "~~~.........#######.............",
                "~~~.........#B.B.B#.............",
                "~~~.........#B.B.B#.............",
                "~~~.........#.....#.............",
                "~~~.........#.....#.............",
                "~~~.........###+###.............",
                "~~~.............................",
                "~~~.........SSS..T..............",
                "~~~.........SSS....@............",
                "~~~.........................,,,,",
                "~~~.........................,,,,",
                "~~~.........................,,,,",
                "~~~.............................",
                "~~~.............................",
                "~~~.............................");
            g.Rooms[0].Role = "barracks"; g.Rooms[0].Label = "the barracks";
            return g;
        }

        static string Draw(BaseGrid g, RoomPlan p, IEnumerable<FurniturePlace> f = null)
        {
            var o = new Dictionary<long, char>();
            if (p != null)
            {
                foreach (var c in p.Walls) o[BaseGrid.Key(c[0], c[1])] = 'w';
                o[BaseGrid.Key(p.Door[0], p.Door[1])] = 'd';
            }
            if (f != null) FurnitureLayout.Draw(o, f);
            return g.ToAscii(o);
        }

        // ------------------------------------------------------------ guard
        [Fact]
        public void GuardRefusesAWallAcrossTheOnlyEntrance()
        {
            var g = Early();
            // a wall segment right in front of the door, closing it off: the case the keeper hit first
            var line = new List<int[]>();
            for (int x = 14; x <= 16; x++) line.Add(new[] { x, 8 });
            line.Add(new[] { 14, 9 }); line.Add(new[] { 16, 9 });
            // seal the pocket: walls left and right of the doorway pocket
            string why = ReachGuard.Check(g, line);
            Assert.NotNull(why);
            Assert.Contains("entrance", why);
        }

        [Fact]
        public void GuardRefusesAWallOverTheDoor()
        {
            var g = Early();
            string why = ReachGuard.Check(g, new[] { new[] { 15, 9 } });
            Assert.Contains("would block the entrance", why);
        }

        [Fact]
        public void GuardAllowsAWallThatLeavesAPathAround()
        {
            var g = Early();
            var line = new List<int[]>();
            for (int x = 10; x <= 20; x++) line.Add(new[] { x, 4 });   // a line south of the base: walk around it
            Assert.Null(ReachGuard.Check(g, line));
        }

        [Fact]
        public void GuardRefusesWallingInABedAndNamesIt()
        {
            var g = BaseGrid.FromAscii(0, 0,
                "..........",
                "..........",
                "...B......",
                "..........",
                "..........");
            var ring = new List<int[]>();
            for (int x = 2; x <= 4; x++) { ring.Add(new[] { x, 1 }); ring.Add(new[] { x, 3 }); }
            ring.Add(new[] { 2, 2 }); ring.Add(new[] { 4, 2 });
            string why = ReachGuard.Check(g, ring);
            Assert.Contains("would cut off bed 1", why);
        }

        [Fact]
        public void GuardRefusesClosingADoorBetweenRooms()
        {
            // two rooms in a row: the inner (bed) room is reached only through the outer one
            var g = BaseGrid.FromAscii(0, 0,
                "...........",
                ".#########.",
                ".#B..+...#.",
                ".#...#...#.",
                ".#####+###.",
                "...........");
            Assert.Equal(2, g.Rooms.Count);
            // a wall inside the outer room right in front of the inner door
            string why = ReachGuard.Check(g, new[] { new[] { 6, 3 } });
            Assert.NotNull(why);
            Assert.Contains("bed 1", why);
            // a wall elsewhere in the outer room leaves the path
            Assert.Null(ReachGuard.Check(g, new[] { new[] { 8, 3 } }));
        }

        [Fact]
        public void GuardKeepsTheDoorstepClearButAllowsALineFurtherOut()
        {
            var g = BaseGrid.FromAscii(0, 0,
                "..........",
                ".######...",
                ".#B..#....",
                ".#...#....",
                ".##+##....",
                "..........",
                "..........");
            // a wall line two cells out from the door: a path goes round its ends
            Assert.Null(ReachGuard.Check(g, new[] { new[] { 1, 0 }, new[] { 2, 0 }, new[] { 3, 0 }, new[] { 4, 0 }, new[] { 5, 0 } }));
            // a line on the doorstep, from the wall to the wall: the entrance is blocked
            string why = ReachGuard.Check(g, new[] { new[] { 1, 1 }, new[] { 2, 1 }, new[] { 3, 1 }, new[] { 4, 1 }, new[] { 5, 1 } });
            Assert.Contains("would block the entrance", why);
            // a single cell on the doorstep
            Assert.Contains("entrance", ReachGuard.Check(g, new[] { new[] { 3, 1 } }));
        }

        [Fact]
        public void GuardEmptyPlanIsFine()
        {
            Assert.Null(ReachGuard.Check(Early(), new List<int[]>()));
        }

        // ------------------------------------------------------------ planner
        [Fact]
        public void PlannerAttachesABedroomToTheBarracks()
        {
            var g = Early();
            string why;
            var p = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom", For = "Bram" }, out why);
            _out.WriteLine(why ?? p.Words);
            _out.WriteLine(Draw(g, p));
            Assert.NotNull(p);
            Assert.Contains(g.Rooms[0].Id, p.SharedWith);
            Assert.Contains("for Bram", p.Words);
            Assert.Contains("sharing the barracks'", p.Words);
            Assert.Equal(4 + 2, System.Math.Min(p.Box.W, p.Box.H));
        }

        [Fact]
        public void FiveRoomsMakeACoherentBase()
        {
            var g = Early();
            foreach (var role in new[] { "bedroom", "bedroom", "kitchen", "freezer", "dining", "research" })
            {
                string why;
                var p = BasePlanner.Plan(g, new RoomRequest { Role = role }, out why);
                Assert.True(p != null, role + ": " + why);
                _out.WriteLine(p.Words);
                Assert.Null(ReachGuard.Check(g, p.Walls, new[] { p.Door }));
                BasePlanner.Apply(g, p);
            }
            _out.WriteLine(g.ToAscii());
            _out.WriteLine(BasePlanner.Describe(g));
            Assert.Equal(7, g.Rooms.Count);
        }

        [Fact]
        public void FreezerGoesNextToTheKitchenWithAnOutsideWall()
        {
            var g = Early();
            string why;
            var k = BasePlanner.Plan(g, new RoomRequest { Role = "kitchen" }, out why);
            Assert.NotNull(k);
            BasePlanner.Apply(g, k);
            var f = BasePlanner.Plan(g, new RoomRequest { Role = "freezer" }, out why);
            _out.WriteLine(f.Words);
            _out.WriteLine(Draw(g, f));
            var kitchen = g.Rooms.First(r => r.Role == "kitchen");
            Assert.Contains(kitchen.Id, f.SharedWith);
            Assert.Contains("sharing the kitchen's", f.Words);
            Assert.Contains(f.Walls, c => BasePlanner.OuterWallCell(g, f.Box, c));
        }

        [Fact]
        public void NearNamesTheRoomItGoesBy()
        {
            var g = Early();
            string why;
            var a = BasePlanner.Plan(g, new RoomRequest { Role = "workshop" }, out why);
            BasePlanner.Apply(g, a, "the workshop");
            var b = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom", Near = "workshop", For = "Cy" }, out why);
            _out.WriteLine(b.Words);
            Assert.Contains(g.Rooms.First(r => r.Role == "workshop").Id, b.SharedWith);
            BasePlanner.Apply(g, b, null, "Cy");
            var d = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom", Near = "Cy's bedroom" }, out why);
            Assert.Contains("sharing Cy's bedroom's", d.Words);
            var c = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom", Near = "the moon" }, out why);
            Assert.Contains("no the moon found", c.Words);
        }

        [Fact]
        public void PlannerKeepsOffWaterMarshAndFertileSoilAndKeepsDoorstepsClear()
        {
            var g = BaseGrid.FromAscii(0, 0,
                "~~~~~~~~~~~~~~~~~~~~",
                "~~~~~~~~~~~~~~~~~~~~",
                "mmmmmmmm,,,,,,,,,,,,",
                "mmmmmmmm,,,,,,,,,,,,",
                "........,,,,,,,,,,,,",
                "........,,,,,,,,,,,,",
                ".#####..............",
                ".#B..#..............",
                ".#...#..............",
                ".##+##..............",
                "....................",
                "....................",
                "....................",
                "....................",
                "....................");
            string why;
            var p = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom" }, out why);
            Assert.NotNull(p);
            _out.WriteLine(Draw(g, p));
            foreach (var c in BuildPure.BoxCells(p.Box))
            {
                Assert.False(g.Has(c[0], c[1], BaseFlag.Water | BaseFlag.Marsh));
                Assert.False(g.Has(c[0], c[1], BaseFlag.Fertile), "covers soil at " + c[0] + "," + c[1]);
            }
            // the doorstep of the existing door (3,6) stays clear
            Assert.False(p.Box.Contains(3, 5));
        }

        [Fact]
        public void NoSiteSaysWhy()
        {
            var g = BaseGrid.FromAscii(0, 0,
                "~~~~~~~~",
                "~......~",
                "~..@...~",
                "~......~",
                "~~~~~~~~");
            string why;
            Assert.Null(BasePlanner.Plan(g, new RoomRequest { Role = "storage", Size = "large" }, out why));
            Assert.Contains("no open ground", why);
        }

        [Fact]
        public void PlannerNeverPicksASpotTheGuardRefuses()
        {
            // a narrow pass between rock and water: a room in it would seal the colonist in
            var g = BaseGrid.FromAscii(0, 0,
                "RRRRRRRRRRRRRRRRRRRR",
                "R..................R",
                "R..@...............R",
                "R..................R",
                "R..................R",
                "R..................R",
                "R..................R",
                "R..................R",
                "RRRRRRRR...RRRRRRRRR",
                "~~~~~~~~...~~~~~~~~~");
            string why;
            var p = BasePlanner.Plan(g, new RoomRequest { Role = "bedroom" }, out why);
            Assert.NotNull(p);
            _out.WriteLine(Draw(g, p));
            Assert.Null(ReachGuard.Check(g, p.Walls, new[] { p.Door }));
        }

        // ------------------------------------------------------------ furniture
        [Fact]
        public void BedroomBedIsAgainstAWallAndNeverBlocksTheDoor()
        {
            var inside = new CellBox(1, 1, 4, 5);
            var door = new[] { 3, 0 };   // south wall
            var l = FurnitureLayout.Place(inside, door, FurnitureLayout.Items("bedroom", "medium", 0));
            _out.WriteLine(string.Join(" ", l));
            var bed = l.First(p => p.Word == "bed");
            Assert.Equal(1, bed.Rect.W); Assert.Equal(2, bed.Rect.H);
            Assert.Equal(inside.MaxZ, bed.Rect.MaxZ);    // far wall: head against the north wall
            Assert.Equal(0, bed.Rect.MaxZ == inside.MaxZ ? 0 : 1);
            foreach (var p in l) Assert.False(p.Rect.Contains(3, 1), p.Word + " blocks the door");
            Assert.Equal(3, l.Count);
        }

        [Fact]
        public void BarracksFitsItsBedsAndKitchenGetsStoveAndButcher()
        {
            int w, h;
            BasePlanner.Inside("barracks", "medium", 0, out w, out h);
            var l = FurnitureLayout.Place(new CellBox(1, 1, w, h), new[] { 1 + w / 2, 0 }, FurnitureLayout.Items("barracks", "medium", 0));
            Assert.Equal(6, l.Count(p => p.Word == "bed"));
            var k = FurnitureLayout.Place(new CellBox(1, 1, 5, 5), new[] { 3, 0 }, FurnitureLayout.Items("kitchen", "medium", 0));
            Assert.Contains(k, p => p.Word == "stove");
            Assert.Contains(k, p => p.Word == "butcher table");
        }

        [Fact]
        public void FreezerCoolerGoesInAnOuterWallFacingIn()
        {
            var inside = new CellBox(1, 1, 5, 5);
            var outer = new List<int[]> { new[] { 6, 3 }, new[] { 3, 6 } };   // east wall and north wall cells with open air outside
            var l = FurnitureLayout.Place(inside, new[] { 3, 0 }, FurnitureLayout.Items("freezer", "medium", 0), outer);
            var c = Assert.Single(l);
            Assert.Equal("cooler", c.Word);
            Assert.True((c.Rect.X == 6 && c.Rot == 1) || (c.Rect.Z == 6 && c.Rot == 0));
            Assert.Empty(FurnitureLayout.Place(inside, new[] { 3, 0 }, FurnitureLayout.Items("freezer", "medium", 0), new List<int[]>()));
        }

        [Fact]
        public void DiningTableGetsChairsFacingIt()
        {
            var l = FurnitureLayout.Place(new CellBox(1, 1, 6, 6), new[] { 4, 0 }, FurnitureLayout.Items("dining", "medium", 0));
            var t = l.First(p => p.Word == "table");
            var chairs = l.Where(p => p.Word == "chair").ToList();
            Assert.Equal(4, chairs.Count);
            foreach (var ch in chairs)
            {
                var st = BaseGrid.Dirs[ch.Rot];
                Assert.True(t.Rect.Contains(ch.Rect.X + st[0], ch.Rect.Z + st[1]), "chair " + ch + " faces the table");
            }
        }

        // ------------------------------------------------------------ BASE line
        [Fact]
        public void BaseLineDescribesOutlineEntranceGrowthAndNextSpots()
        {
            var g = Early();
            string s = BasePlanner.Describe(g);
            _out.WriteLine(s);
            Assert.StartsWith("one room 7x6, 1 room, entrance south", s);
            Assert.Contains("room to grow:", s);
            Assert.Contains("west 9 (water)", s);
            Assert.Contains("next: room for 3+ more bedrooms", s);
            Assert.True(s.Length < 260, s.Length.ToString());
            var empty = BaseGrid.FromAscii(0, 0, "....", "..@.", "....");
            Assert.StartsWith("no rooms yet; home at", BasePlanner.Describe(empty));
        }

        [Fact]
        public void PlanningOnAFullSizeWindowIsQuick()
        {
            var rows = new List<string>();
            for (int z = 0; z < 170; z++) rows.Add(new string('.', 170));
            var g = BaseGrid.FromAscii(0, 0, rows.ToArray());
            g.HomeX = 85; g.HomeZ = 85;
            for (int i = 0; i < 6; i++)
            {
                string why;
                var p = BasePlanner.Plan(g, new RoomRequest { Role = i % 2 == 0 ? "bedroom" : "storage" }, out why);
                Assert.NotNull(p);
                BasePlanner.Apply(g, p);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string line = BasePlanner.Describe(g);
            var p2 = BasePlanner.Plan(g, new RoomRequest { Role = "dining" }, out string _);
            sw.Stop();
            _out.WriteLine(line + "  [" + sw.ElapsedMilliseconds + " ms]");
            Assert.NotNull(p2);
            Assert.True(sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + " ms");
        }
    }
}

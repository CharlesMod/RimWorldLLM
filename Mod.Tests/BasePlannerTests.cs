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
    }
}

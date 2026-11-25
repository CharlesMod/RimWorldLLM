using System.Collections.Generic;
using System.Linq;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class MealDetectorTests
    {
        static List<MealSample> Room(int room, int count, bool eating = true, int firstId = 1)
        {
            return Enumerable.Range(firstId, count).Select(i => new MealSample(i, room, eating)).ToList();
        }

        static (List<MealGroup> starts, List<MealGroup> ends) Run(MealDetector d, IEnumerable<MealSample> s, int tick)
        {
            List<MealGroup> st, en;
            d.Observe(s, tick, out st, out en);
            return (st, en);
        }

        [Fact]
        public void ThreeEatingInOneRoomStartsAMeal()
        {
            var d = new MealDetector();
            var r = Run(d, Room(5, 3), 100);
            Assert.Single(r.starts);
            Assert.Equal(5, r.starts[0].RoomId);
            Assert.Equal(new[] { 1, 2, 3 }, r.starts[0].Pawns);
            Assert.Equal("meal:room5", r.starts[0].Key);
            Assert.Empty(r.ends);
        }

        [Fact]
        public void TwoEatingIsNotEnough()
        {
            var d = new MealDetector();
            Assert.Empty(Run(d, Room(5, 2), 100).starts);
        }

        [Fact]
        public void PawnsInDifferentRoomsDoNotAddUp()
        {
            var d = new MealDetector();
            var s = Room(1, 2).Concat(Room(2, 2, true, 10)).ToList();
            Assert.Empty(Run(d, s, 100).starts);
            var s2 = Room(1, 3).Concat(Room(2, 2, true, 10)).ToList();
            var r = Run(d, s2, 220);
            Assert.Single(r.starts);
            Assert.Equal(1, r.starts[0].RoomId);
        }

        [Fact]
        public void NotEatingIsIgnored()
        {
            var d = new MealDetector();
            var s = Room(5, 2).Concat(Room(5, 3, false, 10)).ToList();
            Assert.Empty(Run(d, s, 100).starts);
        }

        [Fact]
        public void OpenMealDoesNotRestart()
        {
            var d = new MealDetector();
            Run(d, Room(5, 3), 100);
            var r = Run(d, Room(5, 4), 220);
            Assert.Empty(r.starts);
            Assert.Empty(r.ends);
        }

        [Fact]
        public void EndsWhenOnlyOneLeft()
        {
            var d = new MealDetector();
            Run(d, Room(5, 3), 100);
            var r = Run(d, Room(5, 1), 220);
            Assert.Empty(r.starts);
            Assert.Single(r.ends);
            Assert.Equal(new[] { 1, 2, 3 }, r.ends[0].Pawns);
        }

        [Fact]
        public void EndsWhenEveryoneLeftTheRoom()
        {
            var d = new MealDetector();
            Run(d, Room(5, 3), 100);
            Assert.Single(Run(d, new List<MealSample>(), 220).ends);
        }

        [Fact]
        public void TimeoutEndsALongMeal()
        {
            var d = new MealDetector();
            Run(d, Room(5, 3), 100);
            var r = Run(d, Room(5, 3), 100 + MealDetector.MaxMealTicks);
            Assert.Single(r.ends);
            Assert.Empty(r.starts);   // still on cooldown, so no immediate restart
        }

        [Fact]
        public void CooldownBlocksARestartInTheSameRoomOnly()
        {
            var d = new MealDetector(1000);
            Run(d, Room(5, 3), 0);
            Run(d, new List<MealSample>(), 120);                          // meal ends
            Assert.Empty(Run(d, Room(5, 3), 500).starts);                 // same room, cooling down
            Assert.Single(Run(d, Room(6, 3), 500).starts);                // another room is fine
            Run(d, new List<MealSample>(), 620);
            Assert.Single(Run(d, Room(5, 3), 1100).starts);               // past the cooldown
        }

        [Fact]
        public void MealsGroupedPerRoomAreIndependent()
        {
            var d = new MealDetector();
            var s = Room(1, 3).Concat(Room(2, 3, true, 10)).ToList();
            var r = Run(d, s, 100);
            Assert.Equal(2, r.starts.Count);
            var r2 = Run(d, Room(1, 3), 220);
            Assert.Single(r2.ends);
            Assert.Equal(2, r2.ends[0].RoomId);
        }
    }
}

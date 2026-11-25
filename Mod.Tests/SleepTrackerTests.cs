using System.Collections.Generic;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class SleepTrackerTests
    {
        [Fact]
        public void SignalsOnTheFallingAsleepEdgeOnly()
        {
            var t = new SleepTracker(1000);
            Assert.False(t.Observe(1, false, 0));
            Assert.True(t.Observe(1, true, 100));
            Assert.False(t.Observe(1, true, 220));      // still asleep
        }

        [Fact]
        public void CooldownBlocksASecondNap()
        {
            var t = new SleepTracker(1000);
            t.Observe(1, false, 0);
            Assert.True(t.Observe(1, true, 100));
            t.Observe(1, false, 300);
            Assert.False(t.Observe(1, true, 500));      // 400 ticks after the last signal
            t.Observe(1, false, 600);
            Assert.True(t.Observe(1, true, 1200));      // past the cooldown
        }

        [Fact]
        public void FirstSightingAsleepNeverSignals()
        {
            var t = new SleepTracker(1000);
            Assert.False(t.Observe(7, true, 50));
            t.Observe(7, false, 60);
            Assert.True(t.Observe(7, true, 70));
        }

        [Fact]
        public void PawnsAreIndependentAndRetainForgets()
        {
            var t = new SleepTracker(1000);
            t.Observe(1, false, 0); t.Observe(2, false, 0);
            Assert.True(t.Observe(1, true, 10));
            Assert.True(t.Observe(2, true, 10));
            t.Retain(new HashSet<int> { 2 });
            Assert.False(t.Observe(1, true, 20));       // forgotten: first sighting again
            t.Observe(1, false, 30);
            Assert.True(t.Observe(1, true, 40));        // and its cooldown is forgotten too
        }

        [Fact]
        public void DreamIsAKnownThoughtKind()
        {
            Assert.True(MindBook.KnownKind("dream"));
            Assert.False(MindBook.KnownKind("nap"));
        }
    }
}

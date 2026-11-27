using System.Collections.Generic;
using System.Linq;
using RimWorldLLM;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class NarratorFoodTests
    {
        [Fact]
        public void RotGroupsByLabelKeepsSoonestAndDropsSafe()
        {
            var g = NarratorFood.GroupRot(new[]
            {
                new FoodStack("raw potatoes", 20, 0.8), new FoodStack("raw potatoes", 10, 1.5), new FoodStack("raw potatoes", 50, 3.0),
                new FoodStack("meat", 5, 0.2), new FoodStack("rice", 9, 2.0), new FoodStack("corn", 0, 0.1)
            });
            Assert.Equal(new[] { "meat", "raw potatoes" }, g.Select(x => x.Label).ToArray());
            Assert.Equal(30, g[1].Count); Assert.Equal(0.8, g[1].Days);
        }

        [Fact]
        public void RotCapsAndSorts()
        {
            var st = Enumerable.Range(1, 6).Select(i => new FoodStack("f" + i, 1, 1.9 - i * 0.2)).ToList();
            var g = NarratorFood.GroupRot(st);
            Assert.Equal(3, g.Count); Assert.Equal("f6", g[0].Label);
        }

        [Fact]
        public void HarvestCountsReadyAndSoonest()
        {
            var cells = new List<CropCell>();
            for (int i = 0; i < 12; i++) cells.Add(new CropCell("rice", true, 0));
            cells.Add(new CropCell("rice", false, 3.2)); cells.Add(new CropCell("rice", false, 1.5));
            cells.Add(new CropCell("corn", true, 0));
            var g = NarratorFood.GroupHarvest(cells);
            Assert.Equal("rice", g[0].Plant); Assert.Equal(14, g[0].Cells); Assert.Equal(12, g[0].Ready); Assert.Equal(1.5, g[0].Days);
            Assert.Null(g[1].Days);
        }

        [Fact]
        public void GameNearestFirstWithDirection()
        {
            var a = new[] { new GameAnimal("deer", -30, 0), new GameAnimal("deer", -50, 0), new GameAnimal("boar", 0, 10) };
            var g = NarratorFood.GroupGame(a, 0, 0);
            Assert.Equal("boar", g[0].Label); Assert.Equal("N", g[0].Dir); Assert.Equal(10, g[0].Dist);
            Assert.Equal(2, g[1].Count); Assert.Equal(30, g[1].Dist); Assert.Equal("W", g[1].Dir);
        }
    }

    public class NarratorClockTests
    {
        [Fact]
        public void ImmunityWinsWhenItGetsThereFirst()
        {
            var r = NarratorClock.ImmunityRace(0.5, 1.0, 0.3, 0.2);   // immune in 12 h, dead in 84 h
            Assert.True(r.Key); Assert.Equal(12.0, r.Value, 6);
        }

        [Fact]
        public void ImmunityLosesWhenSeverityGetsThereFirst()
        {
            var r = NarratorClock.ImmunityRace(0.1, 0.2, 0.6, 0.8);   // immune in 108 h, dead in 12 h
            Assert.False(r.Key); Assert.Equal(12.0, r.Value, 6);
        }

        [Fact]
        public void FadingIllnessIsWinning()
        {
            var r = NarratorClock.ImmunityRace(0.2, 0.5, 0.4, -0.1);
            Assert.True(r.Key); Assert.Equal(38.4, r.Value, 6);
        }

        [Fact]
        public void NothingMovingIsInfinite()
        {
            var r = NarratorClock.ImmunityRace(0.2, 0, 0.4, 0);
            Assert.True(r.Key); Assert.True(double.IsInfinity(r.Value));
        }

        static readonly double[] Temperate = { -8, -5, 2, 8, 14, 20, 22, 20, 14, 8, 2, -4 };

        [Fact]
        public void DaysToColdCountsRestOfCurrentTwelfthThenFives()
        {
            // Twelfth index 9 (mean 8), day 2 of 5: 3 days left, then index 10 (2 C), then 11 (-4 C): 3 + 5 = 8
            Assert.Equal(8.0, NarratorClock.DaysToCold(Temperate, 9, 2.0));
        }

        [Fact]
        public void DaysToColdWrapsTheYearAndIsZeroWhenAlreadyCold()
        {
            Assert.Equal(0.0, NarratorClock.DaysToCold(Temperate, 0, 1.0));
            var only0 = new double[] { -3, 5, 6, 7, 8, 9, 10, 11, 12, 10, 8, 6 };
            Assert.Equal(5.0 + 5 * 7, NarratorClock.DaysToCold(only0, 4, 0.0));   // index 4 -> 0 is 8 steps: 5 left + 7 twelfths
        }

        [Fact]
        public void DaysToColdNullWhenNeverCold()
        {
            Assert.Null(NarratorClock.DaysToCold(new double[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 12, 10, 8 }, 3, 0));
            Assert.Null(NarratorClock.DaysToCold(new double[3], 0, 0));
        }

        [Fact]
        public void DaysToWarmFromWinter()
        {
            // index 0 (-8), day 4.5: 0.5 left; 1 (-5): +5, 2 (2): +5, 3 (8 C warm) => 0.5 + 10 = 10.5
            Assert.Equal(10.5, NarratorClock.DaysToWarm(Temperate, 0, 4.5));
            Assert.Equal(0.0, NarratorClock.DaysToWarm(Temperate, 5, 1.0));
            Assert.True(NarratorClock.TooColdToGrow(Temperate, 2));
            Assert.False(NarratorClock.TooColdToGrow(Temperate, 3));
        }

        [Fact]
        public void SortCapSortsAndDropsBad()
        {
            var l = NarratorClock.SortCap(new[]
            {
                new Deadline("a", 9, "rot"), new Deadline("b", 2, "bleed"), new Deadline("", 1, "x"),
                new Deadline("c", double.PositiveInfinity, "x"), new Deadline("d", 5, "winter"), new Deadline("e", 6, "winter"),
                new Deadline("f", 7, "winter"), new Deadline("g", 8, "winter"), new Deadline("h", 10, "winter")
            });
            Assert.Equal(6, l.Count); Assert.Equal("b", l[0].What); Assert.Equal("a", l[5].What);
        }
    }
}

namespace RimWorldLLM.Tests
{
    public class NarratorExtrasTests
    {
        [Theory]
        [InlineData(0.02, "extreme")]
        [InlineData(0.15, "major")]
        [InlineData(0.30, "minor")]
        [InlineData(0.37, "near")]
        [InlineData(0.60, null)]
        public void BreakRiskByThreshold(double mood, string want)
        { Assert.Equal(want, NarratorExtras.BreakRisk(mood, 0.35, 0.20, 0.05)); }

        [Fact]
        public void MoodWhySumsSameLabelAndTakesWorstTwo()
        {
            var w = NarratorExtras.MoodWhy(new[]
            {
                new MoodThought("ate raw food", -4), new MoodThought("ate raw food", -4), new MoodThought("slept outside", -5),
                new MoodThought("dirty room", -3), new MoodThought("nice room", 6), new MoodThought("tiny", -0.2)
            });
            Assert.Equal(new[] { "ate raw food", "slept outside" }, w.ToArray());
            Assert.Empty(NarratorExtras.MoodWhy(null));
        }

        [Fact]
        public void DirtyBelowThreshold() { Assert.True(NarratorExtras.IsDirty(-2)); Assert.False(NarratorExtras.IsDirty(-1)); }
    }
}

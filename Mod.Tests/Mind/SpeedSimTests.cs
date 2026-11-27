// Port of tests/test_speed_sim.py: the whole agent against a modelled game at 1x..4x on a virtual clock. Bounds, not exact numbers: they hold the
// design goals in docs/SPEED.md (voiced at 3x, never stale, nothing lost from memory, safe at 4x). The harness's SpeedSim (tools/MindHarness) runs it.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Harness;
using Xunit;
using static RimWorldLLM.Harness.SpeedSim;

namespace RimWorldLLM.Tests.Mind
{
    public class SpeedSimTests
    {
        static readonly Dictionary<(int, double), Metrics> Cache = new Dictionary<(int, double), Metrics>();
        static Metrics Day_(int speed, double floor = 5.0)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue((speed, floor), out var m)) Cache[(speed, floor)] = m = Simulate(speed, attachFloorS: floor);
                return m;
            }
        }

        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
        public void NothingLostOrUnsafeAtAnySpeed(int speed)
        {
            var m = Day_(speed);
            Assert.True(m.Crashed == "", m.Crashed);
            Assert.True(m.EventsLost == 0 && m.RingGaps == 0);                  // every event reached the agent, the ring never overran
            Assert.True(m.MemoriesMissed == 0 && m.MemoriesExpected > 0);       // a witnessed event is always remembered
            Assert.Equal(0, m.EffectsForDropped);                               // no gameplay effect for a line that was dropped
            Assert.True(m.QueueMax <= 16);                                      // bounded queue
            Assert.Equal(0, m.SayRefused);
            Assert.True(m.LateShare <= 0.10);                                   // lines that arrive too late are rare, shed in queue instead
            Assert.True(m.IdleDays.TryGetValue("reflection", out var r) && r.Count > 0 && m.IdleDays.TryGetValue("chronicle", out var c) && c.Count > 0);   // idle work still runs
        }

        [Fact]
        public void X3IsVoicedAboutLikeX1PerRealMinute()
        {
            var a = Day_(1); var c = Day_(3);
            Assert.True(c.LinesPerRealMin >= 0.9 * a.LinesPerRealMin);
            Assert.InRange(c.LinesPerRealMin, 2.0, 6.0);                        // a ceiling too: the budget, not a flood
            Assert.True(c.Lines >= 12);
            Assert.Equal(0, c.ConvsCut);
        }

        [Fact]
        public void LinesAreNeverStaleInGameTime()
        {
            foreach (var (sp, bound) in new[] { (1, 1500), (2, 3000), (3, 3200), (4, 4200) })
            {
                var m = Day_(sp);
                Assert.True(m.DelayGameP90 <= bound, "speed " + sp + " p90 " + m.DelayGameP90);
            }
            Assert.True(Day_(3).DelayRealP90 <= 15.0);                          // real seconds from event to line, at 3x
        }

        [Fact]
        public void X4MayBeQuietButNotSilent()
        {
            var m = Day_(4);
            Assert.InRange(m.Lines, 3, 40);
            Assert.True(m.LinesPerRealMin <= 6.0);
        }

        [Fact]
        public void HigherSpeedDoesLessIdleWorkPerRealTimeButNeverNone()
        {
            foreach (int sp in new[] { 1, 2, 3, 4 })
            {
                var m = Day_(sp);
                int I(string k) => m.Idle.TryGetValue(k, out int v) ? v : 0;
                Assert.True(I("diary") >= 1 && I("chronicle") >= 1);
                Assert.True(I("reflection") >= 4);                              // at least one a pawn (4 pawns) over the run
            }
        }

        [Fact]
        public void RaidBurstAtSpeed3()
        {
            var evs = ColonyDay(5, 4, raidAt: 20000, raidSize: 60);
            var m = Simulate(3, events: evs, seed: 5);
            Assert.True(m.Crashed == "", m.Crashed);
            Assert.True(m.QueueMax <= 16);
            Assert.True(m.EventsLost == 0 && m.MemoriesMissed == 0 && m.EffectsForDropped == 0);
            Assert.True(m.LateShare <= 0.10);
        }

        [Fact]
        public void PauseUnpauseAndSpeedChanges()
        {
            var sched = new Schedule(new[] { (0.0, 1, false), (120.0, 3, false), (240.0, 3, true), (300.0, 2, false), (360.0, 4, false), (400.0, 1, false), (500.0, 3, false) });
            var m = Simulate(sched, seed: 4);
            Assert.True(m.Crashed == "", m.Crashed);
            Assert.True(m.QueueMax <= 16);
            Assert.True(m.EventsLost == 0 && m.MemoriesMissed == 0 && m.EffectsForDropped == 0);
            Assert.True(m.LateShare <= 0.10 && m.Lines >= 8);
        }

        static List<SimEvent> SparseVanilla(int n = 6, int gapTicks = 8000)
        {
            var pairs = new[] { (1, 2), (3, 4), (2, 3), (4, 1), (1, 3), (2, 4) };
            return Enumerable.Range(0, n).Select(i => new SimEvent(2000 + i * gapTicks, "social", new[] { (pairs[i].Item1, "initiator"), (pairs[i].Item2, "recipient") },
                new[] { pairs[i].Item1, pairs[i].Item2 }, 0.5, "Pawn" + pairs[i].Item1 + " and Pawn" + pairs[i].Item2 + " spoke #" + i,
                new JObject { ["vanilla"] = true, ["ref"] = "s" + i, ["tier"] = "high", ["interaction"] = "Insult", ["vanilla_text"] = "A sentence the game wrote.", ["window_ticks"] = 417 })).ToList();
        }

        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)]
        public void VanillaHighLinesThatAreSentAttach(int speed)
        {
            var m = Simulate(speed, events: SparseVanilla(), days: 1.0, attachFloorS: 5.0, pawns: 4);
            int sent = m.SaidByKind.TryGetValue("vanilla", out int v) ? v : 0;
            Assert.True(m.VanillaHigh == 6 && sent >= 3, "speed " + speed + " " + string.Join(",", m.DropReasons));     // the rest are "nothing new" for that pawn
            Assert.True(m.VanillaHighAttached >= sent - 1, "speed " + speed + " attached " + m.VanillaHighAttached + " sent " + sent);
        }

        [Fact]
        public void X4PausesChatterIncludingVanillaVoicing()
        {
            var m = Simulate(4, events: SparseVanilla(), days: 1.0, attachFloorS: 5.0, pawns: 4);
            Assert.True(m.Crashed == "", m.Crashed);
            Assert.True(m.Lines == 0 && m.MemoriesMissed == 0);                 // quiet by design, memories still written
        }

        [Fact]
        public void RealTimeFloorKeepsAttachesUnderLoad()
        {
            // a busy colony at 3x: the ticks-only window (417 ticks = 1.2 s) loses lines the real floor keeps
            int old = new[] { 3, 5, 6 }.Sum(sd => Simulate(3, attachFloorS: 0.0, seed: sd).VanillaHighAttached);
            int nw = new[] { 3, 5, 6 }.Sum(sd => Simulate(3, attachFloorS: 5.0, seed: sd).VanillaHighAttached);
            Assert.True(nw > old, "new " + nw + " old " + old);
        }

        [Fact]
        public void CloudGapsAreRealSeconds()
        {
            var th = Enumerable.Range(0, 400).Select(i => new Thought { Real = i * 0.5, Tick = i * 450, Pawn = i % 6, Tps = 900.0 }).ToList();   // 4x, a thought every 0.5 s
            int old = CloudsShown(th, false);
            int nw = CloudsShown(th, true);
            double realMin = 400 * 0.5 / 60;
            Assert.True(old / realMin > 20 && nw / realMin <= 3.2);              // 20 s global gap = at most 3 a real minute
        }

        [Fact]
        public void SocialFightChainCannotFloodTheQueue()
        {
            // the high tier skips the 60 s social cooldown: a chain of 60 insults between two pawns in 20 s of 3x play
            var chain = Enumerable.Range(0, 60).Select(i => new SimEvent(3000 + i * 30, "social", new[] { (new[] { 1, 2 }[i % 2], "initiator"), (new[] { 2, 1 }[i % 2], "recipient") },
                new[] { 1, 2, 3 }, 0.5, "fight #" + i,
                new JObject { ["vanilla"] = true, ["ref"] = "f" + i, ["tier"] = "high", ["interaction"] = "Insult", ["vanilla_text"] = "A sentence the game wrote.", ["window_ticks"] = 417 })).ToList();
            var m = Simulate(3, events: chain, days: 0.2, pawns: 3);
            Assert.True(m.Crashed == "", m.Crashed);
            Assert.True(m.QueueMax <= 16);
            Assert.True(m.Lines <= 8);                                           // voiced a few times, not sixty
            Assert.True(m.MemoriesMissed == 0 && m.EventsLost == 0);
        }
    }
}

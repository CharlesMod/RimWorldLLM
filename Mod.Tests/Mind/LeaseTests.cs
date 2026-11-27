using System.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class LeaseTests
    {
        // The RimWorld adapter's priority (games/rimworld/adapter.py _EVENT_CLASS); the adapter itself is another worker's.
        static Cls ClassOf(string t)
        {
            switch (t)
            {
                case "player_talk": return Cls.Player;
                case "death": case "mental_break": case "raid_start": return Cls.Urgent;
                case "injury": case "tend": case "first_cue": return Cls.Reactive;
                case "social": return Cls.Social;
                case "letter": case "alert": return Cls.Idle;
                default: return Cls.Ambient;
            }
        }

        static LeaseManager Mgr(int actorPerDay = 12, int pairPerDay = 4, long minGapGame = 1500, double minGapRealS = 20.0)
        {
            return new LeaseManager(new[] { 1, 2 }, 0, new QuotaTracker(actorPerDay: actorPerDay, pairPerDay: pairPerDay,
                minGapGame: minGapGame, minGapRealS: minGapRealS), priority: ClassOf);
        }

        [Fact]
        public void FirstFreeWindowAndClasses()
        {
            var m = Mgr(); var n = new Now(100, 0.0);
            var a = m.Acquire(m.MakeItem(10, "injury", n), n);
            var b = m.Acquire(m.MakeItem(11, "social", n), n);
            Assert.Equal((1, 2), (a.Slot, b.Slot));
            m.Release(a);
            var c = m.Acquire(m.MakeItem(12, "social", n), n);
            Assert.Equal(1, c.Slot);          // first free, no affinity
            Assert.True(ClassOf("death") == ClassOf("raid_start") && ClassOf("raid_start") == ClassOf("mental_break") && ClassOf("mental_break") == Cls.Urgent);
            Assert.True(ClassOf("tend") == Cls.Reactive && ClassOf("level_up") == Cls.Ambient);
            Assert.True(ClassOf("player_talk") == Cls.Player && Cls.Player > Cls.Urgent && Cls.Urgent > Cls.Reactive
                && Cls.Reactive > Cls.Social && Cls.Social > Cls.Ambient);
        }

        [Fact]
        public void PreemptionOnlyByHigherClass()
        {
            var m = Mgr(); var n = new Now(100, 0.0);
            var a = m.Acquire(m.MakeItem(10, "injury", n), n);
            var b = m.Acquire(m.MakeItem(11, "social", n), n);
            Assert.Null(m.Acquire(m.MakeItem(12, "social", n), n));          // equal to the lowest: no preempt
            Assert.Null(m.Acquire(m.MakeItem(12, "level_up", n), n));        // lower: no preempt
            var c = m.Acquire(m.MakeItem(12, "death", n), n);
            Assert.True(c != null && b.Preempted && b.Ended && !a.Preempted);   // lowest class goes first
            Assert.Equal(b.Slot, c.Slot);
            var d = m.Acquire(m.MakeItem(13, "player_talk", n), n);
            Assert.True(d != null && a.Preempted && !c.Preempted);      // player beats injury, urgent kept
            Assert.Null(m.Acquire(m.MakeItem(14, "death", n), n));      // nothing below urgent left
        }

        [Fact]
        public void PawnAlreadyInhabited()
        {
            var m = Mgr(); var n = new Now(100, 0.0);
            m.Acquire(m.MakeItem(10, "social", n), n);
            Assert.Null(m.Acquire(m.MakeItem(10, "death", n), n));
        }

        [Fact]
        public void ExpiryByClassDualClock()
        {
            var m = Mgr(); var n = new Now(1000, 10.0);
            var urgent = m.MakeItem(1, "death", n); var social = m.MakeItem(2, "social", n);
            var inj = m.MakeItem(3, "injury", n); var player = m.MakeItem(4, "player_talk", n);
            Assert.True(LeaseManager.RequestExpiry[Cls.Urgent].RealS == 5.0 && LeaseManager.RequestExpiry[Cls.Social].RealS == 20.0);
            Assert.Equal(1000 + 3000, inj.ExpiresAt);
            Assert.True(player.ExpiresAt == null && player.ExpiresReal == null);
            Assert.True(!urgent.Expired(new Now(1100, 14.9)) && urgent.Expired(new Now(1100, 15.1)));      // real clock
            Assert.True(urgent.Expired(new Now(1700, 11.0)));                                              // tick clock (paused wall)
            Assert.True(!social.Expired(new Now(1100, 29.9)) && social.Expired(new Now(1100, 30.1)));
            Assert.True(inj.Expired(new Now(4000, 11.0)) && !inj.Expired(new Now(3999, 11.0)));
            Assert.False(player.Expired(new Now(1000000000L, 1000000)));
            m.Queue.AddRange(new[] { urgent, social, player });
            var dead = m.Expire(new Now(1100, 16.0));
            Assert.True(dead.Select(d => d.Kind).SequenceEqual(new[] { "death" }) && m.Queue.Count == 2);
            Assert.Null(m.Acquire(urgent, new Now(1100, 16.0)));                  // expired items never lease
        }

        [Fact]
        public void ExpiryFloorScalesTheGameWindowWithSpeed()
        {
            var m = Mgr();
            var slow = new Now(1000, 0.0, 60.0); var fast = new Now(1000, 0.0, 900.0);
            var u1 = m.MakeItem(1, "death", slow); var u4 = m.MakeItem(1, "death", fast);
            Assert.True(u1.ExpiresAt == 1600 && u4.ExpiresAt == 1000 + 2700);
            Assert.True(!u4.Expired(new Now(3600, 2.9, 900.0)) && u4.Expired(new Now(3700, 2.9, 900.0)));
            Assert.True(u4.Expired(new Now(1100, 5.1, 900.0)));                                  // the real cap still ends it
            // speed rises after the request was made: the floor is re-read at the new rate
            var s = m.MakeItem(2, "social", new Now(1000, 0.0, 60.0));
            Assert.True(s.Expired(new Now(2300, 1.0, 60.0)) && !s.Expired(new Now(2300, 1.0, 900.0)));
        }

        [Fact]
        public void QueueIsBoundedAndTheMostImportantWins()
        {
            var m = Mgr(); var n = new Now(1000, 0.0);
            for (int i = 0; i < LeaseManager.MaxQueue; i++)
                Assert.Equal("queued", m.Submit(m.MakeItem(100 + i, "thought", n, severity: 0.3 + i / 100.0), n));
            Assert.Equal("drop:queue_full", m.Submit(m.MakeItem(1, "thought", n, severity: 0.1), n));
            Assert.Equal("queued", m.Submit(m.MakeItem(2, "death", n, severity: 1.0), n));
            Assert.True(m.Queue.Count == LeaseManager.MaxQueue && m.Queue.All(q => q.ActorId != 100));   // the weakest made room
        }

        [Fact]
        public void VoiceBudgetGatesChatterButNeverUrgent()
        {
            var m = new LeaseManager(new[] { 1, 2 }, 0, new QuotaTracker(), priority: ClassOf, budget: new VoiceBudget(perMin: 6, burst: 2));
            var n = new Now(1000, 0.0);
            m.Budget.Spend(n, 2);                                       // the colony has just spoken twice
            m.Submit(m.MakeItem(1, "social", n), n); m.Submit(m.MakeItem(2, "death", n), n);
            var started = m.PopRunnable(n);
            Assert.True(started.Select(s => s.Item1.Kind).SequenceEqual(new[] { "death" }));   // urgent bypasses the gate, the social line waits
            var later = new Now(1000, 10.0);                            // 10 s at 6 a minute is one token
            Assert.True(m.PopRunnable(later).Select(s => s.Item1.Kind).SequenceEqual(new[] { "social" }));
        }

        [Fact]
        public void LeaseIdleExpiryByClass()
        {
            var m = Mgr(); var n = new Now(0, 0.0);
            var l1 = m.Acquire(m.MakeItem(1, "level_up", n), n);
            var l2 = m.Acquire(m.MakeItem(2, "player_talk", n), n);
            var o = m.ExpireLeases(new Now(0, 20.0));
            Assert.True(o.Count == 1 && ReferenceEquals(o[0], l1) && m.Leases.Values.Contains(l2));
        }

        [Fact]
        public void QuotasPairPawnAndBypass()
        {
            var m = Mgr(actorPerDay: 2, pairPerDay: 1, minGapGame: 0);
            var n = new Now(100, 0.0);
            m.Quotas.Record(1, 2, n);
            Assert.Equal("drop:pair_quota", m.Submit(m.MakeItem(1, "social", n, listener: 2), n));
            Assert.Equal("drop:pair_quota", m.Submit(m.MakeItem(2, "social", n, listener: 1), n));   // unordered pair
            m.Quotas.Record(1, 3, n);
            Assert.Equal("drop:actor_quota", m.Submit(m.MakeItem(1, "level_up", n), n));
            Assert.Equal("queued", m.Submit(m.MakeItem(1, "death", n), n));                          // urgent bypasses
            Assert.Equal("queued", m.Submit(m.MakeItem(1, "social", new Now(100 + 60000, 0)), new Now(100 + 60000, 0)));  // next day
        }

        [Fact]
        public void GapNeedsBothClocksTooSoon()
        {
            var m = Mgr(minGapGame: 1500, minGapRealS: 20);
            m.Quotas.Record(1, null, new Now(100, 0.0));
            Assert.Equal((false, "gap"), m.Quotas.Allowed(1, null, Cls.Ambient, new Now(200, 5.0)));
            Assert.True(m.Quotas.Allowed(1, null, Cls.Ambient, new Now(200, 25.0)).Item1);      // real seconds passed
            Assert.True(m.Quotas.Allowed(1, null, Cls.Ambient, new Now(2000, 5.0)).Item1);      // ticks passed
        }

        [Fact]
        public void AmbientPauseAtSpeed()
        {
            var m = new LeaseManager(new[] { 1, 2 }, 0, new QuotaTracker(), pauseAmbientAtSpeed: 3, priority: ClassOf);
            var n = new Now(0, 0.0);
            Assert.StartsWith("drop", m.Submit(m.MakeItem(1, "level_up", n), n, speed: 3));
            Assert.StartsWith("drop", m.Submit(m.MakeItem(1, "social", n), n, speed: 4));
            Assert.Equal("queued", m.Submit(m.MakeItem(1, "level_up", n), n, speed: 2));
            Assert.Equal("queued", m.Submit(m.MakeItem(2, "death", n), n, speed: 4));      // urgent still runs
            Assert.Equal("queued", m.Submit(m.MakeItem(3, "injury", n), n, speed: 4));
        }

        [Fact]
        public void DuplicateRequestsMerge()
        {
            var m = Mgr(); var n = new Now(0, 0.0);
            Assert.Equal("queued", m.Submit(m.MakeItem(1, "injury", n), n));
            Assert.StartsWith("merged", m.Submit(m.MakeItem(1, "injury", n), n));
            Assert.Single(m.Queue);
        }

        [Fact]
        public void SkipIfNothingNew()
        {
            var m = Mgr();
            Assert.False(m.NothingNew(1, "sig"));
            m.MarkSpoke(1, "sig");
            Assert.True(m.NothingNew(1, "sig") && !m.NothingNew(1, "other"));
        }

        [Fact]
        public void NarratorIdleOnlyAndCoverage()
        {
            var m = Mgr(); var n = new Now(0, 0.0);
            Assert.True(m.NarratorMayDoIdleWork());
            var l1 = m.Acquire(m.MakeItem(1, "social", n), n);
            Assert.False(m.NarratorMayDoIdleWork());            // a window is busy
            m.Release(l1);
            Assert.True(m.NarratorMayDoIdleWork());
            m.NarratorBusy = true;
            Assert.False(m.NarratorMayDoIdleWork());
            m.NarratorBusy = false;
            m.Acquire(m.MakeItem(1, "player_talk", n), n); m.Acquire(m.MakeItem(2, "death", n), n);
            Assert.True(m.NarratorShouldCover(m.MakeItem(3, "injury", n), n));      // both busy, nothing preemptable
            Assert.False(m.NarratorShouldCover(m.MakeItem(4, "player_talk", n), n));   // can preempt the death? no: equal/lower
            m.Queue.Add(m.MakeItem(5, "level_up", n));
            Assert.False(m.NarratorMayDoIdleWork());
        }
    }
}

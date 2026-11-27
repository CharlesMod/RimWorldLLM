using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class WatchDirectorTests
    {
        static WatchEvent Ev(string kind, int key = 1, string name = "Ana", string detail = null, int x = 10, int z = 10, double sev = 1)
        {
            return new WatchEvent { Kind = kind, Key = key, TargetId = key, MapId = 0, X = x, Z = z, Name = name, Detail = detail, Severity = sev };
        }

        [Fact]
        public void KindWeightsOrder()
        {
            Assert.True(WatchKinds.Weight(WatchKinds.Raid) > WatchKinds.Weight(WatchKinds.Birth));
            Assert.True(WatchKinds.Weight(WatchKinds.Birth) > WatchKinds.Weight(WatchKinds.Combat));
            Assert.True(WatchKinds.Weight(WatchKinds.Combat) > WatchKinds.Weight(WatchKinds.Masterwork));
            Assert.True(WatchKinds.Weight(WatchKinds.Art) > WatchKinds.Weight(WatchKinds.Wander));
            Assert.Equal(0, WatchKinds.Weight("nonsense"));
        }

        [Fact]
        public void EmptyDirectorStays()
        {
            Assert.Null(new WatchDirector().Next(0));
        }

        [Fact]
        public void PicksHighestInterest()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Art, 1), 0);
            d.Offer(Ev(WatchKinds.Birth, 2, "Bram"), 0);
            Assert.Equal(WatchKinds.Birth, d.Next(0).Kind);
        }

        [Fact]
        public void InterestDecaysAndExpires()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Art, 1), 0);       // 4 -> below 0.8 after ~46 s
            Assert.Null(d.Next(50));
            var d2 = new WatchDirector();
            d2.Offer(Ev(WatchKinds.Raid, 1), 0);
            Assert.Null(d2.Next(61));                 // expired
        }

        [Fact]
        public void VarietyPenalisesSameSubject()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1, "Ana"), 0);
            Assert.Equal(1, d.Next(0).Key);
            d.Offer(Ev(WatchKinds.MentalBreak, 1, "Ana"), 20);   // same pawn again
            d.Offer(Ev(WatchKinds.SocialFight, 2, "Bram"), 20);  // other pawn, lower base
            Assert.Equal(2, d.Next(20).Key);
        }

        [Fact]
        public void VarietyPenalisesSameKind()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1), 0);
            d.Next(0);
            d.Offer(Ev(WatchKinds.Combat, 2), 9);
            d.Offer(Ev(WatchKinds.Party, 3, "Cy"), 9);
            Assert.Equal(WatchKinds.Party, d.Next(9).Kind);      // 8*0.6=4.8 < 7
        }

        [Fact]
        public void DwellHoldsTheShot()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Birth, 1), 0);
            var s = d.Next(0);
            Assert.Equal(12, s.Dwell);
            d.Offer(Ev(WatchKinds.Party, 2), 1);
            Assert.Null(d.Next(5));
            Assert.Equal(WatchKinds.Party, d.Next(13).Kind);
        }

        [Fact]
        public void BigEventsDwellLonger()
        {
            Assert.True(WatchKinds.Dwell(WatchKinds.Raid) > WatchKinds.Dwell(WatchKinds.Wander));
            Assert.True(WatchKinds.Dwell(WatchKinds.Wedding) > WatchKinds.Dwell(WatchKinds.Art));
        }

        [Fact]
        public void UrgentCutsInAfterMinimumButNotBefore()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Party, 1), 0);
            d.Next(0);
            d.Offer(Ev(WatchKinds.Raid, 2, "x", "east"), 1);
            Assert.Null(d.Next(1));                               // too soon
            var s = d.Next(4);
            Assert.NotNull(s); Assert.True(s.Cut); Assert.Equal(WatchKinds.Raid, s.Kind);
        }

        [Fact]
        public void NonUrgentNeverCutsIntoAnEvent()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1), 0);
            d.Next(0);
            d.Offer(Ev(WatchKinds.Birth, 2), 1);
            Assert.Null(d.Next(4));
        }

        [Fact]
        public void AnyEventCutsIntoFillerAfterTheMinimum()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Wander, 1), 0);
            d.Next(0);
            d.Offer(Ev(WatchKinds.Birth, 2), 1);
            Assert.Null(d.Next(2));                   // not before CutInMinSec
            var s = d.Next(4);
            Assert.True(s.Cut); Assert.Equal(WatchKinds.Birth, s.Kind);
        }

        [Fact]
        public void PlayerTouchPausesThenResumes()
        {
            var d = new WatchDirector();
            d.PlayerTouched(0);
            d.Offer(Ev(WatchKinds.Raid, 1), 0);
            Assert.Null(d.Next(10));
            Assert.True(d.IsPaused(19));
            Assert.NotNull(d.Next(21));
        }

        [Fact]
        public void PauseLengthIsConfigurable()
        {
            var d = new WatchDirector { PlayerPauseSec = 5 };
            d.PlayerTouched(0);
            d.Offer(Ev(WatchKinds.Raid, 1), 0);
            Assert.NotNull(d.Next(6));
        }

        [Fact]
        public void DuplicateOffersMerge()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1), 0);
            d.Offer(Ev(WatchKinds.Combat, 1), 3);
            Assert.Equal(1, d.PendingCount);
        }

        [Fact]
        public void CaptionTexts()
        {
            Assert.Equal("Camera: a raid arrives from the east", WatchKinds.Caption(WatchKinds.Raid, null, "east"));
            Assert.Equal("Camera: Ana gives birth", WatchKinds.Caption(WatchKinds.Birth, "Ana", null));
            Assert.Equal("Camera: Bram's funeral", WatchKinds.Caption(WatchKinds.Funeral, null, "Bram"));
            Assert.Equal("Camera: Ana has died", WatchKinds.Caption(WatchKinds.Death, "Ana", null));
            Assert.Null(WatchKinds.Caption(WatchKinds.Wander, "Ana", null));
        }

        [Fact]
        public void DirectionCompass()
        {
            Assert.Equal("east", WatchKinds.Direction(10, 0));
            Assert.Equal("north", WatchKinds.Direction(0, 10));
            Assert.Equal("southwest", WatchKinds.Direction(-5, -5));
            Assert.Equal("the centre", WatchKinds.Direction(0, 0));
        }

        [Fact]
        public void EventMapping()
        {
            Assert.Equal(WatchKinds.SocialFight, WatchKinds.FromEvent("mental_break", "SocialFighting", null, null));
            Assert.Equal(WatchKinds.MentalBreak, WatchKinds.FromEvent("mental_break", "Wander_Sad", null, null));
            Assert.Equal(WatchKinds.Wedding, WatchKinds.FromEvent("gathering", null, "wedding", "start"));
            Assert.Equal(WatchKinds.Party, WatchKinds.FromEvent("gathering", null, "party", "start"));
            Assert.Null(WatchKinds.FromEvent("gathering", null, "party", "end"));
            Assert.Null(WatchKinds.FromEvent("gathering", null, "meal", "start"));
            Assert.Equal(WatchKinds.Combat, WatchKinds.FromEvent("injury", null, null, null));
            Assert.Null(WatchKinds.FromEvent("funeral", null, null, "end"));
            Assert.Null(WatchKinds.FromEvent("sleep_start", null, null, null));
        }

        [Fact]
        public void CaptionSuppressedInsideGapAndForFiller()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1, "Ana"), 0);
            Assert.NotNull(d.Next(0).Caption);
            d.Offer(Ev(WatchKinds.Party, 2), 9);     // after dwell(8), gap 12 not yet over
            var s = d.Next(9);
            Assert.Equal(WatchKinds.Party, s.Kind);
            Assert.Null(s.Caption);
            d.Offer(Ev(WatchKinds.Wander, 3), 30);
            var w = d.Next(30);
            Assert.Equal(WatchKinds.Wander, w.Kind);
            Assert.Null(w.Caption);
        }

        [Fact]
        public void UrgentHasShorterCaptionGap()
        {
            var d = new WatchDirector();
            d.Offer(Ev(WatchKinds.Combat, 1, "Ana"), 0);
            d.Next(0);
            d.Offer(Ev(WatchKinds.Death, 2, "Bram"), 5);
            var s = d.Next(5);
            Assert.True(s.Cut);
            Assert.Equal("Camera: Bram has died", s.Caption);
        }
    }
}

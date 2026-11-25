using System.Collections.Generic;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class DirectorFocusTests
    {
        [Fact]
        public void IdleMeansNoFocus()
        {
            var d = new DirectorFocus();
            Assert.Equal(-1, d.Pick(0));
            Assert.Equal(-1, d.Pick(100));
        }

        [Fact]
        public void SignalWeightsOrderByIntent()
        {
            Assert.True(DirectorFocus.Weight(FocusSignal.SpeechHigh) > DirectorFocus.Weight(FocusSignal.SpeechMedium));
            Assert.True(DirectorFocus.Weight(FocusSignal.SpeechMedium) > DirectorFocus.Weight(FocusSignal.SpeechPlain));
            Assert.True(DirectorFocus.Weight(FocusSignal.SpeechPlain) > DirectorFocus.Weight(FocusSignal.Thought));
            Assert.True(DirectorFocus.Weight(FocusSignal.Death) > DirectorFocus.Weight(FocusSignal.Injury));
        }

        [Fact]
        public void FirstInterestingPawnBecomesFocus()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);
            d.Signal(2, FocusSignal.SpeechHigh, 0);
            Assert.Equal(2, d.Pick(0));
        }

        [Fact]
        public void ScoresAddAndDecayByHalfLife()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechHigh, 0);
            d.Signal(1, FocusSignal.SpeechHigh, 0);
            Assert.Equal(16.0, d.ScoreOf(1, 0), 6);
            Assert.Equal(8.0, d.ScoreOf(1, DirectorFocus.HalfLifeSec), 6);
            Assert.Equal(4.0, d.ScoreOf(1, 2 * DirectorFocus.HalfLifeSec), 6);
        }

        [Fact]
        public void ScoreIsCapped()
        {
            var d = new DirectorFocus();
            for (int i = 0; i < 100; i++) d.Signal(1, FocusSignal.SpeechHigh, 0);
            Assert.Equal(DirectorFocus.MaxScore, d.ScoreOf(1, 0), 6);
        }

        [Fact]
        public void DecaysToIdleAndLetsGoAfterTheDwell()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);          // 3
            Assert.Equal(1, d.Pick(0));
            // Score falls under MinScore after ~3 * 8 / ... ; the dwell still holds at 5 s.
            Assert.Equal(1, d.Pick(5));
            Assert.Equal(-1, d.Pick(60));
            Assert.Equal(-1, d.Current);
            Assert.Equal(0, d.Count);
        }

        [Fact]
        public void DwellBlocksASwitchEvenForAStrongRival()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);
            Assert.Equal(1, d.Pick(0));
            d.Signal(2, FocusSignal.SpeechHigh, 1);           // 8 > 3 * 1.5, but only 1 s in
            Assert.Equal(1, d.Pick(1));
            Assert.Equal(1, d.Pick(DirectorFocus.MinDwellSec - 0.1));
        }

        [Fact]
        public void SwitchesAfterDwellOnlyWhenRivalBeatsTheMargin()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechMedium, 0);          // 5
            Assert.Equal(1, d.Pick(0));
            double t = DirectorFocus.MinDwellSec;
            double cur = d.ScoreOf(1, t);                       // 5 * 0.5^(6/8) ~ 2.97
            // A rival just under cur * margin does not win ...
            d.Signal(2, FocusSignal.SpeechPlain, t);            // 3: 3 < 2.97 * 1.5 = 4.46
            Assert.Equal(1, d.Pick(t));
            // ... one over the margin does.
            d.Signal(2, FocusSignal.SpeechPlain, t);            // 6 > 4.46
            Assert.Equal(2, d.Pick(t));
        }

        [Fact]
        public void SwitchResetsTheDwell()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);
            d.Pick(0);
            double t = DirectorFocus.MinDwellSec + 1;
            d.Signal(2, FocusSignal.SpeechHigh, t);
            Assert.Equal(2, d.Pick(t));
            d.Signal(3, FocusSignal.SpeechHigh, t + 1); d.Signal(3, FocusSignal.SpeechHigh, t + 1);
            Assert.Equal(2, d.Pick(t + 1));                     // dwell restarted at the switch
            Assert.Equal(3, d.Pick(t + DirectorFocus.MinDwellSec));
        }

        [Theory]
        [InlineData(FocusSignal.Death)]
        [InlineData(FocusSignal.MentalBreak)]
        [InlineData(FocusSignal.RaidStart)]
        public void UrgentSignalsCutInAtOnce(FocusSignal sig)
        {
            Assert.True(DirectorFocus.IsUrgent(sig));
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechHigh, 0);
            d.Signal(1, FocusSignal.SpeechHigh, 0);
            Assert.Equal(1, d.Pick(0));
            d.Signal(2, sig, 0.5);                              // inside the dwell, rival is stronger anyway
            Assert.Equal(2, d.Pick(0.5));
        }

        [Fact]
        public void UrgentCutsInEvenWhenWeakerThanTheFocus()
        {
            var d = new DirectorFocus();
            for (int i = 0; i < 10; i++) d.Signal(1, FocusSignal.SpeechHigh, 0);   // 40 (cap)
            Assert.Equal(1, d.Pick(0));
            d.Signal(2, FocusSignal.MentalBreak, 0.1);          // 15 < 40
            Assert.Equal(2, d.Pick(0.1));
            // It happens once: the focus then holds by the usual rules.
            Assert.Equal(2, d.Pick(0.2));
        }

        [Fact]
        public void NonUrgentInjuryDoesNotCutIn()
        {
            Assert.False(DirectorFocus.IsUrgent(FocusSignal.Injury));
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);
            d.Pick(0);
            d.Signal(2, FocusSignal.Injury, 1);
            Assert.Equal(1, d.Pick(1));
        }

        [Fact]
        public void SeverityScalesEventsButNotSpeech()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.Injury, 0, 2.0);
            d.Signal(2, FocusSignal.Injury, 0, 1.0);
            d.Signal(3, FocusSignal.SpeechPlain, 0, 2.0);
            Assert.Equal(12.0, d.ScoreOf(1, 0), 6);
            Assert.Equal(6.0, d.ScoreOf(2, 0), 6);
            Assert.Equal(3.0, d.ScoreOf(3, 0), 6);
        }

        [Fact]
        public void ConversationGivesBothPawnsABonusWhileItLasts()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.Thought, 0);                // 1
            d.Conversation(1, 2, 0, 10);
            Assert.Equal(1.0 + DirectorFocus.ConversationBonus, d.ScoreOf(1, 0), 6);
            Assert.Equal(DirectorFocus.ConversationBonus, d.ScoreOf(2, 0), 6);
            Assert.Equal(1, d.Pick(0));                          // tie broken by lower id, then 1 is higher anyway
            Assert.True(d.ScoreOf(2, 11) < DirectorFocus.MinScore);
        }

        [Fact]
        public void TiesBreakDeterministicallyByLowerId()
        {
            var d = new DirectorFocus();
            d.Signal(7, FocusSignal.SpeechPlain, 0);
            d.Signal(3, FocusSignal.SpeechPlain, 0);
            Assert.Equal(3, d.Pick(0));
        }

        [Fact]
        public void RemovedFocusEndsAtOnceAndNextPickIsFree()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechHigh, 0);
            d.Signal(2, FocusSignal.SpeechPlain, 0);
            Assert.Equal(1, d.Pick(0));
            d.Remove(1);                                         // died: no dwell wait
            Assert.Equal(-1, d.Current);
            Assert.Equal(2, d.Pick(0.1));
        }

        [Fact]
        public void RemovedRivalIsNeverPicked()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.SpeechPlain, 0);
            d.Pick(0);
            d.Signal(2, FocusSignal.SpeechHigh, 1);
            d.Remove(2);
            Assert.Equal(1, d.Pick(20));                         // dwell over, 2 is gone
            Assert.Equal(-1, d.Pick(100));
        }

        [Fact]
        public void CopyIdsAndClear()
        {
            var d = new DirectorFocus();
            d.Signal(1, FocusSignal.Thought, 0); d.Signal(2, FocusSignal.Thought, 0);
            var ids = new List<int>();
            d.CopyIds(ids);
            ids.Sort();
            Assert.Equal(new[] { 1, 2 }, ids);
            d.Clear();
            Assert.Equal(0, d.Count);
            Assert.Equal(-1, d.Pick(0));
        }

        [Fact]
        public void DeterministicWithAFakeClock()
        {
            int[] Run()
            {
                var d = new DirectorFocus();
                var seq = new List<int>();
                for (int t = 0; t < 60; t++)
                {
                    if (t % 7 == 0) d.Signal(t % 3, FocusSignal.SpeechMedium, t);
                    if (t == 30) d.Signal(9, FocusSignal.RaidStart, t);
                    seq.Add(d.Pick(t));
                }
                return seq.ToArray();
            }
            Assert.Equal(Run(), Run());
        }
    }
}

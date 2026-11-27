using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class AutosaveRotationTests
    {
        static AutosaveRotation.Input In(int now, int last, float days = 1f)
        {
            return new AutosaveRotation.Input { Enabled = true, NarratorPlays = true, GameAutosaveOn = false, NowTick = now, LastSaveTick = last, EveryDays = days };
        }

        [Fact]
        public void NamesAreOursOnlyWhenTheyMatch()
        {
            Assert.True(AutosaveRotation.IsOurs("RWL-Narrator-1"));
            Assert.True(AutosaveRotation.IsOurs("RWL-Narrator-20"));
            Assert.False(AutosaveRotation.IsOurs("RWL-Narrator-0"));
            Assert.False(AutosaveRotation.IsOurs("RWL-Narrator-21"));
            Assert.False(AutosaveRotation.IsOurs("Autosave-1"));
            Assert.False(AutosaveRotation.IsOurs("RWL-Narrator-1x"));
            Assert.False(AutosaveRotation.IsOurs(null));
            Assert.False(AutosaveRotation.IsOurs("My colony"));
        }

        [Fact]
        public void NextSlotFillsThenRotatesOldest()
        {
            var t = new Dictionary<int, DateTime>();
            Func<int, DateTime?> f = i => t.ContainsKey(i) ? t[i] : (DateTime?)null;
            var d0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var order = new List<int>();
            for (int n = 0; n < 8; n++)
            {
                int s = AutosaveRotation.NextSlot(3, f);
                order.Add(s); t[s] = d0.AddHours(n);
            }
            Assert.Equal(new[] { 1, 2, 3, 1, 2, 3, 1, 2 }, order);
        }

        [Fact]
        public void NextSlotKeepIsClampedAndSurvivesARestart()
        {
            Assert.Equal(1, AutosaveRotation.NextSlot(0, i => null));
            // a gap (slot 2 missing) is filled first
            Assert.Equal(2, AutosaveRotation.NextSlot(5, i => i == 2 ? (DateTime?)null : DateTime.UtcNow));
            Assert.InRange(AutosaveRotation.NextSlot(999, i => DateTime.UtcNow), 1, AutosaveRotation.MaxKeep);
        }

        [Fact]
        public void DecideRespectsTheGamesOwnAutosaveAndTheSwitch()
        {
            var i = In(100000, 0);
            Assert.Equal(AutosaveRotation.Decision.Save, AutosaveRotation.Decide(i));
            i.GameAutosaveOn = true;
            Assert.Equal(AutosaveRotation.Decision.GameHandles, AutosaveRotation.Decide(i));
            i = In(100000, 0); i.Enabled = false;
            Assert.Equal(AutosaveRotation.Decision.Off, AutosaveRotation.Decide(i));
            i = In(100000, 0); i.NarratorPlays = false;
            Assert.Equal(AutosaveRotation.Decision.Off, AutosaveRotation.Decide(i));
        }

        [Fact]
        public void DecideWaitsForTheIntervalAndHoldsForBusy()
        {
            Assert.Equal(AutosaveRotation.Decision.NotDue, AutosaveRotation.Decide(In(59999, 0)));
            Assert.Equal(AutosaveRotation.Decision.Save, AutosaveRotation.Decide(In(60000, 0)));
            var i = In(70000, 0); i.HardHold = true;
            Assert.Equal(AutosaveRotation.Decision.Hold, AutosaveRotation.Decide(i));
            i = In(70000, 0); i.SoftHold = true;
            Assert.Equal(AutosaveRotation.Decision.Hold, AutosaveRotation.Decide(i));
            i = In(60000 + AutosaveRotation.SoftGraceTicks, 0); i.SoftHold = true;
            Assert.Equal(AutosaveRotation.Decision.Save, AutosaveRotation.Decide(i));   // a long raid cannot stop saving for ever
            i.HardHold = true;
            Assert.Equal(AutosaveRotation.Decision.Hold, AutosaveRotation.Decide(i));   // a modal never lets one through
        }
    }

    public class ColonyRestartTests
    {
        static ColonyRestart.Input In(double t, RestartMode m = RestartMode.Ask, bool lost = true, bool playing = true, bool same = true)
        {
            return new ColonyRestart.Input { Lost = lost, Mode = m, NarratorPlays = playing, SameWorldPossible = same, NowSec = t };
        }

        [Fact]
        public void LostIsAnnouncedOnce()
        {
            var r = new ColonyRestart();
            Assert.Equal(RestartAct.None, r.Step(In(0, lost: false)));
            Assert.Equal(RestartAct.EmitColonyLost, r.Step(In(1)));
            Assert.Equal(RestartAct.ShowChoice, r.Step(In(2)));
            Assert.Equal(RestartAct.None, r.Step(In(3)));
            Assert.Equal(RestartAct.None, r.Step(In(4)));
        }

        [Fact]
        public void OffOrNotPlayingDoesNothingAfterTheEvent()
        {
            var r = new ColonyRestart();
            Assert.Equal(RestartAct.EmitColonyLost, r.Step(In(0, RestartMode.Off)));
            Assert.Equal(RestartAct.None, r.Step(In(1, RestartMode.Off)));
            Assert.Equal(ColonyRestart.State.Done, r.Current);
            r = new ColonyRestart();
            r.Step(In(0, RestartMode.Auto, playing: false));
            Assert.Equal(RestartAct.None, r.Step(In(1, RestartMode.Auto, playing: false)));
        }

        [Fact]
        public void AskSameWorldNeedsNoSaveAndWaitsForColonists()
        {
            var r = new ColonyRestart();
            r.Step(In(0)); r.Step(In(1));
            Assert.Equal(RestartAct.StartSameWorld, r.Choose(RestartChoice.SameWorld, In(2)));
            Assert.Equal(RestartAct.None, r.Step(In(10)));
            var back = In(11, lost: false); back.Resumed = true;
            r.Step(back);
            Assert.Equal(ColonyRestart.State.Launched, r.Current);
        }

        [Fact]
        public void AskNewWorldSavesFirstThenLaunches()
        {
            var r = new ColonyRestart();
            r.Step(In(0)); r.Step(In(1));
            Assert.Equal(RestartAct.SaveLostWorld, r.Choose(RestartChoice.NewWorld, In(2)));
            Assert.Equal(RestartAct.None, r.Step(In(3)));   // no launch before the save is reported
            Assert.Equal(RestartAct.StartNewWorld, r.SaveResult(true, In(4)));
            Assert.True(r.SaveDone);
        }

        [Fact]
        public void FailedSaveCancelsTheLeave()
        {
            var r = new ColonyRestart();
            r.Step(In(0)); r.Step(In(1));
            r.Choose(RestartChoice.NewWorld, In(2));
            Assert.Equal(RestartAct.NoteSaveFailed, r.SaveResult(false, In(3)));
            Assert.Equal(RestartAct.None, r.Step(In(4)));
            Assert.False(r.SaveDone);
        }

        [Fact]
        public void StopAndDismissEnd()
        {
            var r = new ColonyRestart();
            r.Step(In(0)); r.Step(In(1));
            Assert.Equal(RestartAct.NoteStopped, r.Choose(RestartChoice.Stop, In(2)));
            var d = new ColonyRestart();
            d.Step(In(0)); d.Step(In(1)); d.Dismissed();
            Assert.Equal(ColonyRestart.State.Done, d.Current);
        }

        [Fact]
        public void SameWorldImpossibleBecomesNewWorld()
        {
            var r = new ColonyRestart();
            r.Step(In(0, same: false)); r.Step(In(1, same: false));
            Assert.Equal(RestartAct.SaveLostWorld, r.Choose(RestartChoice.SameWorld, In(2, same: false)));
        }

        [Fact]
        public void AutoWaitsThenPicksSameWorldAndFallsBackToNewWorld()
        {
            var r = new ColonyRestart();
            Assert.Equal(RestartAct.EmitColonyLost, r.Step(In(0, RestartMode.Auto)));
            Assert.Equal(RestartAct.NoteAutoDelay, r.Step(In(1, RestartMode.Auto)));
            Assert.Equal(RestartAct.None, r.Step(In(5, RestartMode.Auto)));
            Assert.Equal(RestartAct.StartSameWorld, r.Step(In(1 + ColonyRestart.AutoDelaySec, RestartMode.Auto)));
            // nobody answered the vanilla dialog: new world, saved first
            double t = 1 + ColonyRestart.AutoDelaySec + ColonyRestart.SameWorldWaitSec;
            Assert.Equal(RestartAct.None, r.Step(In(t - 1, RestartMode.Auto)));
            Assert.Equal(RestartAct.SaveLostWorld, r.Step(In(t, RestartMode.Auto)));
            Assert.Equal(RestartAct.StartNewWorld, r.SaveResult(true, In(t + 1, RestartMode.Auto)));
        }

        [Fact]
        public void AutoWithoutSameWorldGoesStraightToSaveAndNewWorld()
        {
            var r = new ColonyRestart();
            r.Step(In(0, RestartMode.Auto, same: false)); r.Step(In(0, RestartMode.Auto, same: false));
            Assert.Equal(RestartAct.SaveLostWorld, r.Step(In(ColonyRestart.AutoDelaySec, RestartMode.Auto, same: false)));
        }

        [Fact]
        public void AutoCancelsIfColonistsReturnDuringTheDelay()
        {
            var r = new ColonyRestart();
            r.Step(In(0, RestartMode.Auto)); r.Step(In(0, RestartMode.Auto));
            Assert.Equal(RestartAct.None, r.Step(In(5, RestartMode.Auto, lost: false)));
            Assert.Equal(ColonyRestart.State.Idle, r.Current);
        }

        [Fact]
        public void LostSaveNameNeverReusesAnExistingName()
        {
            var have = new List<string> { "Haven-lost-1", "haven-LOST-2", "Other" };
            Assert.Equal("Haven-lost-3", ColonyRestart.LostSaveName("Haven", have));
            Assert.Equal("Colony-lost-1", ColonyRestart.LostSaveName("   ", have));
            Assert.Equal("Badname-lost-1", ColonyRestart.LostSaveName("Bad/na\\me", new string[0]));
            Assert.DoesNotContain('/', ColonyRestart.LostSaveName("a/b\\c:d", have));
        }

        [Fact]
        public void HandoffIsOneShotAndHoldsUntilTheNewGame()
        {
            RestartHandoff.Clear();
            RestartHandoff.Set(true, "Haven fell.", newWorld: true);
            bool p; string s;
            Assert.False(RestartHandoff.Take(out p, out s));            // still in the old world / main menu
            Assert.True(RestartHandoff.TakeNewWorldRequest());
            Assert.False(RestartHandoff.TakeNewWorldRequest());
            Assert.True(RestartHandoff.Take(out p, out s));
            Assert.True(p); Assert.Equal("Haven fell.", s);
            Assert.False(RestartHandoff.Take(out p, out s));
            Assert.StartsWith("The story continues.", RestartHandoff.ContinueText("Haven fell."));
        }

        [Fact]
        public void SummaryIsBounded()
        {
            var lines = new List<string>();
            for (int i = 0; i < 50; i++) lines.Add(new string('x', 100));
            Assert.True(RestartHandoff.BuildSummary("Haven", 12, lines).Length < 700);
            Assert.StartsWith("Haven fell after 1 day.", RestartHandoff.BuildSummary("Haven", 1, null));
        }
    }
}

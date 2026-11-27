using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class SceneFinderTests
    {
        static ScenePawn P(int id, string name, int x, int z, string job = null, int room = -1, string roomLabel = null)
        {
            return new ScenePawn { Id = id, Name = name, X = x, Z = z, Job = job, RoomId = room, RoomLabel = roomLabel };
        }

        [Fact]
        public void SharedMealInARoomIsOneGroupScene()
        {
            var pawns = new List<ScenePawn>
            {
                P(1, "Ana", 10, 10, "Ingest", 5, "barracks"), P(2, "Bram", 12, 10, "Ingest", 5, "barracks"),
                P(3, "Cy", 14, 11, "Ingest", 5, "barracks"), P(4, "Dara", 40, 40, "Ingest", 9, "kitchen")
            };
            var s = SceneFinder.Find(pawns, 19);
            var meal = s.Single(c => c.Kind == SceneKinds.Meal);
            Assert.Equal("Camera: dinner in the barracks: Ana, Bram and Cy", meal.Caption);
            Assert.Equal(new[] { 1, 2, 3 }, meal.Ids.ToArray());
            Assert.Equal(ShotStyle.Group, meal.Style);
            Assert.Equal("scene_meal:r5", meal.Key);
        }

        [Fact]
        public void LoneEaterIsNoMealScene()
        {
            Assert.DoesNotContain(SceneFinder.Find(new List<ScenePawn> { P(1, "Ana", 10, 10, "Ingest", 5) }, 12), c => c.Kind == SceneKinds.Meal);
        }

        [Fact]
        public void MealWordFollowsTheClock()
        {
            Assert.Equal("breakfast", SceneFinder.MealWord(7));
            Assert.Equal("lunch", SceneFinder.MealWord(12));
            Assert.Equal("dinner", SceneFinder.MealWord(19));
            Assert.Equal("a meal", SceneFinder.MealWord(3));
        }

        [Fact]
        public void OutdoorPawnsClusterByDistance()
        {
            var g = SceneFinder.Cluster(new[] { P(1, "A", 0, 0), P(2, "B", 5, 0), P(3, "C", 12, 0), P(4, "D", 60, 60) }, 8);
            Assert.Equal(2, g.Count);
            Assert.Equal(3, g[0].Count);   // chained: 0-5-12
        }

        [Fact]
        public void LoneAwakePawnAtNight()
        {
            var pawns = new List<ScenePawn> { P(1, "Dara", 10, 10, "Wait_Wander"), new ScenePawn { Id = 2, Name = "Ana", X = 12, Z = 10, Awake = false, InBed = true } };
            var s = SceneFinder.Find(pawns, 2);
            Assert.Equal("Camera: Dara can't sleep", s.Single(c => c.Kind == SceneKinds.Night).Caption);
            Assert.DoesNotContain(SceneFinder.Find(pawns, 14), c => c.Kind == SceneKinds.Night);   // daytime
            pawns.Add(P(3, "Bram", 13, 11, "Cook"));                                              // someone else awake near
            Assert.DoesNotContain(SceneFinder.Find(pawns, 2), c => c.Kind == SceneKinds.Night && c.Ids.Contains(1));
        }

        [Fact]
        public void NightWorkerIsWorkingNotSleepless()
        {
            var s = SceneFinder.Find(new List<ScenePawn> { new ScenePawn { Id = 1, Name = "Dara", Job = "DoBill", WorkType = "Smithing" } }, 1);
            Assert.Equal("Camera: Dara works through the night", s.Single().Caption);
        }

        [Fact]
        public void DoctorTendingPatient()
        {
            var pawns = new List<ScenePawn> { new ScenePawn { Id = 1, Name = "Ana", Job = "TendPatient", TargetId = 2, X = 5, Z = 5 }, new ScenePawn { Id = 2, Name = "Bram", InBed = true, X = 6, Z = 5 } };
            var m = SceneFinder.Find(pawns, 12).Single(c => c.Kind == SceneKinds.Medical);
            Assert.Equal("Camera: Ana tends to Bram", m.Caption);
            Assert.Equal(2, m.Ids.Count);
        }

        [Fact]
        public void RomanceNeedsTwoAdultsAwakeAndClose()
        {
            var a = new ScenePawn { Id = 1, Name = "Ana", X = 10, Z = 10, Partners = new[] { 2 }, AgeYears = 30 };
            var b = new ScenePawn { Id = 2, Name = "Bram", X = 12, Z = 10, Partners = new[] { 1 }, AgeYears = 29 };
            Assert.Equal("Camera: Ana and Bram spend time together", SceneFinder.Find(new List<ScenePawn> { a, b }, 15).Single(c => c.Kind == SceneKinds.Romance).Caption);
            b.AgeYears = 17;   // the age gate
            Assert.DoesNotContain(SceneFinder.Find(new List<ScenePawn> { a, b }, 15), c => c.Kind == SceneKinds.Romance);
            b.AgeYears = 29; b.InBed = true;
            Assert.DoesNotContain(SceneFinder.Find(new List<ScenePawn> { a, b }, 15), c => c.Kind == SceneKinds.Romance);
            b.InBed = false; b.Job = "Lovin";
            Assert.DoesNotContain(SceneFinder.Find(new List<ScenePawn> { a, b }, 15), c => c.Kind == SceneKinds.Romance);
            b.Job = null; b.X = 40;
            Assert.DoesNotContain(SceneFinder.Find(new List<ScenePawn> { a, b }, 15), c => c.Kind == SceneKinds.Romance);
        }

        [Fact]
        public void RecreationAndWorkTogether()
        {
            var pawns = new List<ScenePawn>
            {
                new ScenePawn { Id = 1, Name = "Ana", JoyKind = "Gaming_Cerebral", Job = "Play_Chess", RoomId = 3, RoomLabel = "rec room" },
                new ScenePawn { Id = 2, Name = "Bram", JoyKind = "Gaming_Cerebral", Job = "Play_Chess", RoomId = 3, RoomLabel = "rec room" },
                new ScenePawn { Id = 3, Name = "Cy", WorkType = "Growing", Job = "Sow", X = 50, Z = 50 },
                new ScenePawn { Id = 4, Name = "Dara", WorkType = "PlantCutting", Job = "Harvest", X = 55, Z = 52 },
            };
            var s = SceneFinder.Find(pawns, 15);
            Assert.Equal("Camera: a board game in the rec room: Ana and Bram", s.Single(c => c.Kind == SceneKinds.Recreation).Caption);
            Assert.Equal("Camera: Cy and Dara work the fields", s.Single(c => c.Kind == SceneKinds.Work).Caption);
        }

        [Fact]
        public void ChildrenPrisonersAndTaming()
        {
            var pawns = new List<ScenePawn>
            {
                new ScenePawn { Id = 1, Name = "Tess", Child = true, AgeYears = 8, Job = "Lessontaking", X = 0, Z = 0 },
                new ScenePawn { Id = 2, Name = "Ana", Job = "PrisonerAttemptRecruit", TargetId = 3, X = 30, Z = 30 },
                new ScenePawn { Id = 3, Name = "Vik", Prisoner = true, X = 31, Z = 30 },
                new ScenePawn { Id = 4, Name = "Bram", Job = "Tame", TargetName = "muffalo", X = 60, Z = 60 },
            };
            var s = SceneFinder.Find(pawns, 10);
            Assert.Equal("Camera: Tess has a lesson", s.Single(c => c.Kind == SceneKinds.Child).Caption);
            Assert.Equal("Camera: Ana talks with the prisoner Vik", s.Single(c => c.Kind == SceneKinds.Prisoner).Caption);
            Assert.Equal("Camera: Bram tames the muffalo", s.Single(c => c.Kind == SceneKinds.Taming).Caption);
        }

        [Fact]
        public void DisabledGroupsAreLeftOutAndAPawnShowsOnce()
        {
            var pawns = new List<ScenePawn>
            {
                new ScenePawn { Id = 1, Name = "Ana", Job = "Ingest", RoomId = 1, Partners = new[] { 2 } },
                new ScenePawn { Id = 2, Name = "Bram", Job = "Ingest", RoomId = 1, Partners = new[] { 1 } },
            };
            var all = SceneFinder.Find(pawns, 12);
            Assert.Single(all);                                  // the meal claims both; no second romance scene
            Assert.Equal(SceneKinds.Meal, all[0].Kind);
            var noMeals = SceneFinder.Find(pawns, 12, new HashSet<string> { SceneKinds.GroupRomance });
            Assert.Equal(SceneKinds.Romance, noMeals.Single().Kind);
            Assert.Empty(SceneFinder.Find(pawns, 12, new HashSet<string>()));
        }

        [Fact]
        public void ScenesRankByWeightAndGroupSize()
        {
            var pawns = new List<ScenePawn>();
            for (int i = 1; i <= 4; i++) pawns.Add(new ScenePawn { Id = i, Name = "M" + i, Job = "Ingest", RoomId = 1 });
            pawns.Add(new ScenePawn { Id = 10, Name = "W1", WorkType = "Mining", Job = "Mine", X = 90, Z = 90 });
            pawns.Add(new ScenePawn { Id = 11, Name = "W2", WorkType = "Mining", Job = "Mine", X = 91, Z = 90 });
            var s = SceneFinder.Find(pawns, 12);
            Assert.Equal(SceneKinds.Meal, s[0].Kind);
            Assert.True(s[0].Score > s[1].Score);
            Assert.True(WatchKinds.Weight(SceneKinds.Medical) > WatchKinds.Weight(SceneKinds.Work));
            Assert.True(WatchKinds.Weight(WatchKinds.Art) > WatchKinds.Weight(SceneKinds.Medical));   // events outrank scenes
        }

        [Fact]
        public void NamesList()
        {
            Assert.Equal("Ana", SceneFinder.Names(new[] { "Ana" }));
            Assert.Equal("Ana and Bram", SceneFinder.Names(new[] { "Ana", "Bram" }));
            Assert.Equal("A, B, C and 3 more", SceneFinder.Names(new[] { "A", "B", "C", "D", "E", "F" }));
        }
    }

    public class DirectorFramingTests
    {
        [Fact]
        public void ZoomTargetsPerKind()
        {
            Assert.Equal(DirectorZoom.Level.Close, DirectorZoom.ForKind(SceneKinds.Meal));
            Assert.Equal(DirectorZoom.Level.Close, DirectorZoom.ForKind(WatchKinds.Death));
            Assert.Equal(DirectorZoom.Level.Close, DirectorZoom.ForKind(WatchKinds.Art));
            Assert.Equal(DirectorZoom.Level.Mid, DirectorZoom.ForKind(SceneKinds.Work));
            Assert.Equal(DirectorZoom.Level.Wide, DirectorZoom.ForKind(WatchKinds.Raid));
            Assert.Equal(DirectorZoom.Level.Wide, DirectorZoom.ForKind(WatchKinds.ShipPart));
            Assert.True(DirectorZoom.CloseSize < DirectorZoom.MidSize && DirectorZoom.MidSize < DirectorZoom.WideSize);
        }

        [Fact]
        public void ZoomRespectsPlayerLimitsAndWidensForGroups()
        {
            Assert.Equal(15, DirectorZoom.Target(SceneKinds.Meal, -1, 15, 60));       // close 13 clamped up to the player's min
            Assert.Equal(30, DirectorZoom.Target(WatchKinds.Raid, -1, 11, 30));       // wide 36 clamped to the player's max
            Assert.Equal(20, DirectorZoom.Target(SceneKinds.Meal, 20, 11, 60));       // the group needs more room
        }

        [Fact]
        public void FitCoversTheBoxOnAWideScreen()
        {
            double s = DirectorZoom.Fit(-32, -5, 32, 5, 0, 0, 16.0 / 9.0, 1.0, 0);
            Assert.Equal(18, s, 3);                                                  // 32 / (16/9)
            Assert.Equal(10, DirectorZoom.Fit(-1, -10, 1, 10, 0, 0, 16.0 / 9.0, 1.0, 0), 3);
        }

        static List<FightPoint> Pts(params double[] xz)
        {
            var l = new List<FightPoint>();
            for (int i = 0; i + 1 < xz.Length; i += 2) l.Add(new FightPoint(i, xz[i], xz[i + 1]));
            return l;
        }

        [Fact]
        public void BattleCentresOnTheFrontLine()
        {
            var cols = Pts(0, 0, 4, 0);          // centroid (2,0)
            var hos = Pts(20, 0, 22, 0, 24, 0);  // centroid (22,0)
            var f = BattleFrame.Compute(cols, hos, 16.0 / 9.0, 11, 60);
            Assert.NotNull(f);
            Assert.Equal(12, f.X, 3); Assert.Equal(0, f.Z, 3);
            Assert.Equal(2, f.Colonists); Assert.Equal(3, f.Hostiles);
            Assert.InRange(f.Size, DirectorZoom.BattleMin, DirectorZoom.BattleMax);
        }

        [Fact]
        public void FarAwayFightersAreNotEngaged()
        {
            Assert.Null(BattleFrame.Compute(Pts(0, 0), Pts(100, 100), 1.7, 11, 60));
            var f = BattleFrame.Compute(Pts(0, 0, 200, 200), Pts(10, 0), 1.7, 11, 60);
            Assert.Equal(1, f.Colonists);                     // the colonist far away is not in the frame
            Assert.Equal(5, f.X, 3);
        }

        [Fact]
        public void BattleSizeIsClamped()
        {
            var f = BattleFrame.Compute(Pts(0, 0), Pts(1, 0), 1.7, 11, 60);
            Assert.Equal(DirectorZoom.BattleMin, f.Size);
            var g = BattleFrame.Compute(Pts(0, 0, 0, 27, 0, 54, 0, 81), Pts(0, 13, 0, 40, 0, 67, 0, 95), 1.7, 11, 40);
            Assert.Equal(40, g.Size);                         // the player's max beats the battle max
        }

        [Fact]
        public void FollowCamHoldsStillInsideTheDeadZone()
        {
            var f = new FollowCam();
            double x = 0, z = 0;
            for (int i = 0; i < 60; i++) f.Step(ref x, ref z, 2.0, 1.0, 1 / 60.0);   // 2.2 cells away < StartCells
            Assert.Equal(0, x); Assert.Equal(0, z);
            Assert.False(f.Moving);
        }

        [Fact]
        public void FollowCamEasesInWithoutOvershootAndSettles()
        {
            var f = new FollowCam();
            double x = 0, z = 0, prev = 0, maxStep = 0;
            for (int i = 0; i < 300; i++)
            {
                f.Step(ref x, ref z, 10, 0, 1 / 60.0);
                Assert.True(x <= 10 + 1e-9);                         // never past the target
                Assert.True(x >= prev - 1e-9);                       // never backwards
                maxStep = Math.Max(maxStep, x - prev); prev = x;
            }
            Assert.InRange(x, 9.5, 10.0);
            Assert.False(f.Moving);
            Assert.True(maxStep < 0.5);                              // no jump in a single 60 fps frame
        }

        [Fact]
        public void FollowCamTracksAFastPawnAt3xWithoutJitter()
        {
            // A pawn walking at 3x speed (~14 cells/s) eastward; the camera keeps it in view and moves monotonically.
            var f = new FollowCam();
            double x = 0, z = 0, px = 0, prev = 0;
            int reversals = 0;
            for (int i = 0; i < 600; i++)
            {
                px += 14.0 / 60.0;
                f.Step(ref x, ref z, px, 0, 1 / 60.0);
                if (x < prev - 1e-9) reversals++;
                prev = x;
                if (i > 120) Assert.InRange(px - x, 0, 12);          // the pawn stays well inside a close view
            }
            Assert.Equal(0, reversals);
        }

        [Fact]
        public void FollowCamCutsWhenFar()
        {
            var f = new FollowCam();
            Assert.True(f.ShouldCut(0, 0, 100, 0));
            Assert.False(f.ShouldCut(0, 0, 30, 0));
        }

        [Fact]
        public void ZoomEasesToTarget()
        {
            var f = new FollowCam();
            double s = 36;
            for (int i = 0; i < 300; i++) { double n = f.StepZoom(s, 13, 1 / 60.0); Assert.True(n <= s + 1e-9 && n >= 13 - 1e-9); s = n; }
            Assert.Equal(13, s, 2);
        }
    }

    public class BattleDirectorTests
    {
        static BattleFrameResult F(double x = 10, double z = 10, double size = 20) { return new BattleFrameResult { X = x, Z = z, Size = size, Colonists = 2, Hostiles = 3 }; }

        [Fact]
        public void HoldsTheFightThenClosesAndGoesIdle()
        {
            var b = new BattleDirector();
            Assert.Null(b.Shot(0));
            b.Update(0, F(), "the raiders", 3, 2);
            var s = b.Shot(0);
            Assert.Equal(WatchKinds.Battle, s.Kind);
            Assert.Equal("Camera: the fight begins", s.Caption);
            Assert.Null(b.Shot(1).Caption);                          // caption once
            for (int t = 1; t < 120; t++) b.Update(t, F(10 + t * 0.1), "the raiders", 3, 2);
            Assert.True(b.Fighting);                                 // held as long as it lasts
            b.Update(120, null, "the raiders", 2, 2);
            Assert.True(b.Fighting);                                 // grace
            b.Update(120 + BattleDirector.EndGraceSec, null, "the raiders", 2, 2);
            var c = b.Shot(120 + BattleDirector.EndGraceSec);
            Assert.Equal(WatchKinds.BattleEnd, c.Kind);
            Assert.Equal("Camera: the raiders flee", c.Caption);
            b.Update(120 + BattleDirector.EndGraceSec + BattleDirector.ClosingSec, null, "the raiders", 0, 2);
            Assert.False(b.Active);
            Assert.Null(b.Shot(200));
        }

        [Fact]
        public void ClosingCaptions()
        {
            Assert.Equal("Camera: the raiders flee", BattleDirector.ClosingCaption("the raiders", 2, 3));
            Assert.Equal("Camera: the animals are beaten", BattleDirector.ClosingCaption("the animals", 0, 3));
            Assert.Equal("Camera: no one is left standing", BattleDirector.ClosingCaption("the raiders", 4, 0));
        }

        [Fact]
        public void DownedColonistCloseUpIsShortAndRare()
        {
            var b = new BattleDirector();
            b.Update(0, F(), "the raiders", 3, 2);
            b.Shot(0);
            b.Downed(7, "Cy", 1);
            var d = b.Shot(1);
            Assert.Equal(WatchKinds.Downed, d.Kind);
            Assert.Equal(7, d.TargetId);
            Assert.Equal("Camera: Cy is down", d.Caption);
            Assert.Equal(WatchKinds.Battle, b.Shot(1 + BattleDirector.DownedSec).Kind);   // back to the fight
            b.Downed(8, "Dara", 3);                                                      // inside the gap: ignored
            Assert.Equal(WatchKinds.Battle, b.Shot(6).Kind);
        }

        [Fact]
        public void DownedOutsideAFightIsIgnored()
        {
            var b = new BattleDirector();
            b.Downed(1, "Ana", 0);
            Assert.Null(b.Shot(0));
        }
    }

    public class DirectorPacingTests
    {
        [Fact]
        public void HoldsPerKindAndPace()
        {
            Assert.Equal(20, DirectorPacing.Hold(WatchKinds.Wander, WatchPace.Calm));
            Assert.Equal(15, DirectorPacing.Hold(WatchKinds.Thread, WatchPace.Normal));
            Assert.Equal(10, DirectorPacing.Hold(WatchKinds.Wander, WatchPace.Lively));
            Assert.Equal(15, DirectorPacing.Hold(SceneKinds.Meal, WatchPace.Calm));
            Assert.Equal(8, DirectorPacing.Hold(SceneKinds.Meal, WatchPace.Lively));
            Assert.Equal(12, DirectorPacing.Hold(WatchKinds.Raid, WatchPace.Normal));
            Assert.True(double.IsPositiveInfinity(DirectorPacing.Hold(WatchKinds.Battle, WatchPace.Lively)));
            Assert.True(DirectorPacing.NoRepeatSec(WatchPace.Calm) > DirectorPacing.NoRepeatSec(WatchPace.Lively));
            Assert.Equal(WatchPace.Calm, DirectorPacing.FromInt(-3));
            Assert.Equal(WatchPace.Lively, DirectorPacing.FromInt(9));
        }

        [Fact]
        public void RepeatGuardBlocksUnlessUrgent()
        {
            var g = new RepeatGuard();
            g.Shown("p:1", 0);
            Assert.True(g.Blocked("p:1", 100, 240, false));
            Assert.False(g.Blocked("p:1", 100, 240, true));
            Assert.False(g.Blocked("p:1", 241, 240, false));
            Assert.False(g.Blocked(null, 0, 240, false));
            Assert.Null(DirectorPacing.BlockKey(WatchKinds.Wander, 3, null));
            Assert.Equal("scene_meal:r1", DirectorPacing.BlockKey(SceneKinds.Meal, -1, "scene_meal:r1"));
            Assert.Equal("p:3", DirectorPacing.BlockKey(WatchKinds.Thread, 3, null));
        }

        static WatchEvent Scene(string key, string kind = SceneKinds.Meal, string cap = "Camera: dinner")
        {
            return new WatchEvent { Kind = kind, SceneKey = key, Caption = cap, Style = ShotStyle.Group, Ids = new List<int> { 1, 2 } };
        }

        [Fact]
        public void SameSceneIsNotRepeatedInsideTheWindow()
        {
            var d = new WatchDirector();
            d.Offer(Scene("scene_meal:r1"), 0);
            Assert.Equal(SceneKinds.Meal, d.Next(0).Kind);
            d.Offer(Scene("scene_meal:r1"), 20);
            Assert.Null(d.Next(20));                                     // blocked for NoRepeatSec(normal) = 240
            d.Offer(Scene("scene_work:x", SceneKinds.Work, "Camera: work"), 20);
            Assert.Equal(SceneKinds.Work, d.Next(20).Kind);              // a different scene is fine
            d.Offer(Scene("scene_meal:r1"), 250);
            Assert.Equal(SceneKinds.Meal, d.Next(250).Kind);             // after the window
        }

        [Fact]
        public void UrgentEventsIgnoreTheNoRepeatWindow()
        {
            var d = new WatchDirector();
            d.Offer(new WatchEvent { Kind = WatchKinds.MentalBreak, Key = 1, TargetId = 1, Name = "Ana" }, 0);
            Assert.NotNull(d.Next(0));
            d.Offer(new WatchEvent { Kind = WatchKinds.Death, Key = 1, TargetId = 1, Name = "Ana" }, 100);
            Assert.Equal(WatchKinds.Death, d.Next(100).Kind);
        }

        [Fact]
        public void EventsBeatScenesEvenWhenScenesScoreHigher()
        {
            var d = new WatchDirector();
            d.Offer(Scene("scene_medical:1", SceneKinds.Medical, "Camera: Ana tends to Bram"), 0);
            d.Offer(new WatchEvent { Kind = WatchKinds.Art, Key = 5, Name = "Cy", Severity = 0.5 }, 0);   // 4*0.5=2 < 3.5
            Assert.Equal(WatchKinds.Art, d.Next(0).Kind);
        }

        [Fact]
        public void SceneCaptionIsUsedAndShotCarriesItsGroup()
        {
            var d = new WatchDirector();
            d.Offer(Scene("scene_meal:r1", SceneKinds.Meal, "Camera: dinner in the barracks: Ana and Bram"), 0);
            var s = d.Next(0);
            Assert.Equal("Camera: dinner in the barracks: Ana and Bram", s.Caption);
            Assert.Equal(ShotStyle.Group, s.Style);
            Assert.Equal(2, s.Ids.Count);
            Assert.Equal(11, s.Dwell);
            Assert.False(d.ShotOver(10));
            Assert.True(d.ShotOver(11));
        }

        [Fact]
        public void DifferentScenesOnOneMapDoNotMerge()
        {
            var d = new WatchDirector();
            d.Offer(Scene("scene_meal:r1"), 0);
            d.Offer(Scene("scene_meal:r2"), 0);
            Assert.Equal(2, d.PendingCount);
        }

        [Fact]
        public void PaceChangesTheHold()
        {
            var d = new WatchDirector { Pace = WatchPace.Lively };
            d.Offer(new WatchEvent { Kind = WatchKinds.Wander, Key = 1 }, 0);
            Assert.Equal(10, d.Next(0).Dwell);
        }

        [Fact]
        public void TryCaptionSharesTheLimit()
        {
            var d = new WatchDirector();
            Assert.True(d.TryCaption("Camera: the fight begins", true, 0));
            Assert.False(d.TryCaption("Camera: Cy is down", true, 2));
            Assert.True(d.TryCaption("Camera: Cy is down", true, 5));
            Assert.False(d.TryCaption("Camera: a scene", false, 10));   // normal gap 12 s
        }
    }

    public class StoryThreadTests
    {
        const int H = StoryThreads.TicksPerHour;

        [Fact]
        public void BoostDecaysOverTheThread()
        {
            var t = new StoryThreads();
            t.Start(1, "Bram", ThreadReasons.Breakup, 0);               // 6 h
            Assert.Equal(StoryThreads.MaxBoost, t.Boost(1, 0), 6);
            Assert.Equal(2.0, t.Boost(1, 3 * H), 6);                     // halfway
            Assert.Equal(1.0, t.Boost(1, 6 * H), 6);
            Assert.Equal(1.0, t.Boost(2, 0), 6);                         // no thread
            Assert.True(t.Has(1, 5 * H)); Assert.False(t.Has(1, 6 * H));
        }

        [Fact]
        public void CaptionsAndRelabel()
        {
            var t = new StoryThreads();
            t.Start(1, "Bram", ThreadReasons.Loss("Ana"), 0);
            Assert.Equal("Camera: Bram, after losing Ana", t.Caption(1, 10));
            t.Relabel(ThreadReasons.LossPrefix, ThreadReasons.Funeral, 2 * H);
            Assert.Equal("Camera: Bram, after the funeral", t.Caption(1, 2 * H));
            Assert.Equal(StoryThreads.MaxBoost, t.Boost(1, 2 * H), 6);  // restarted
            Assert.Null(t.Caption(1, 11 * H));
        }

        [Fact]
        public void ActiveIsStrongestFirstAndCapped()
        {
            var t = new StoryThreads();
            t.Start(1, "A", ThreadReasons.Recovered, 0);                 // 4 h
            t.Start(2, "B", ThreadReasons.Arrival, 2 * H);               // 6 h, fresher
            Assert.Equal(new[] { 2, 1 }, t.Active(2 * H).ToArray());
            for (int i = 10; i < 30; i++) t.Start(i, "x", ThreadReasons.Growth, 3 * H);
            Assert.Equal(StoryThreads.MaxThreads, t.Count);
            t.Prune(100 * H);
            Assert.Equal(0, t.Count);
        }

        [Fact]
        public void ThreadPawnsScoreHigherInTheDirector()
        {
            var t = new StoryThreads();
            t.Start(2, "Bram", ThreadReasons.Funeral, 0);
            var d = new WatchDirector { SubjectBoost = id => t.Boost(id, 0) };
            d.Offer(new WatchEvent { Kind = SceneKinds.Meal, SceneKey = "scene_meal:r1", Ids = new List<int> { 1, 3 }, Caption = "Camera: lunch" }, 0);
            d.Offer(new WatchEvent { Kind = WatchKinds.Thread, Key = 2, TargetId = 2, Caption = t.Caption(2, 0) }, 0);
            var s = d.Next(0);
            Assert.Equal(WatchKinds.Thread, s.Kind);                     // 2.5 x 3 beats 3.0
            Assert.Equal("Camera: Bram, after the funeral", s.Caption);
        }
    }
}

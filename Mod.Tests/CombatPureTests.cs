using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class CombatPureTests
    {
        static List<int[]> Cells(params int[][] c) { return c.ToList(); }

        static HostileInfo H(int id, string faction, string kind, int x, int z, bool ranged = false, bool downed = false, int px = -1, int pz = -1)
        {
            return new HostileInfo { Id = id, Faction = faction, Kind = kind, X = x, Z = z, Ranged = ranged, Downed = downed, PrevX = px, PrevZ = pz };
        }

        [Fact]
        public void FromAsciiReadsRowsNorthFirstAndFlags()
        {
            var g = CombatGrid.FromAscii("#s", "D.");
            Assert.Equal(2, g.W); Assert.Equal(2, g.H);
            Assert.False(g.Sight(0, 1)); Assert.Equal(0.75f, g.Cover[g.Idx(0, 1)]);
            Assert.True(g.Sight(1, 1)); Assert.False(g.Walk(1, 1)); Assert.Equal(0.55f, g.Cover[g.Idx(1, 1)]);
            Assert.True(g.Walk(0, 0)); Assert.True((g.F(0, 0) & CellFlag.Door) != 0);
            Assert.True(g.Walk(1, 0)); Assert.False(g.Walk(5, 5));
        }

        [Fact]
        public void LineOfFireBlockedByWallNotBySandbag()
        {
            Assert.False(LineOfFire.Clear(CombatGrid.FromAscii("..#.."), 0, 0, 4, 0));
            Assert.True(LineOfFire.Clear(CombatGrid.FromAscii("..s.."), 0, 0, 4, 0));
            Assert.True(LineOfFire.Clear(CombatGrid.FromAscii("#...#"), 0, 0, 4, 0)); // endpoints do not block
            var line = LineOfFire.Line(0, 0, 3, 0).ToList();
            Assert.Equal(4, line.Count); Assert.Equal(3, line[3][0]);
        }

        [Fact]
        public void CoverFromFacesTheShooter()
        {
            var g = CombatGrid.FromAscii(".....", "..#..", ".....", ".....", ".....");
            // wall at (2,3); cell (2,2)
            Assert.Equal(0.75f, CoverCalc.CoverFrom(g, 2, 2, 2, 4));
            Assert.Equal(0f, CoverCalc.CoverFrom(g, 2, 2, 2, 0));
            Assert.Equal(0f, CoverCalc.CoverFrom(g, 2, 2, 2, 3)); // point blank
        }

        static CombatGrid WallGrid()
        {
            var rows = new string[12];
            for (int z = 11; z >= 0; z--) rows[11 - z] = z == 6 ? "..###########.." : "...............";
            return CombatGrid.FromAscii(rows);
        }

        [Fact]
        public void CoverFinderPicksCellsBehindTheWallFacingTheThreat()
        {
            var g = WallGrid();
            var spots = CoverFinder.Find(g, 7, 3, 4, Cells(new[] { 7, 11 }), Cells(), 20, 3);
            Assert.Equal(3, spots.Count);
            Assert.All(spots, s => { Assert.Equal(5, s.Z); Assert.True(s.Cover > 0); });
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.True(System.Math.Abs(spots[i].X - spots[j].X) > 1 || spots[i].Z != spots[j].Z);
        }

        [Fact]
        public void CoverFinderNeverTakesOpenGroundWhenCoverExistsAndReturnsFewer()
        {
            var g = WallGrid();
            var spots = CoverFinder.Find(g, 7, 3, 4, Cells(new[] { 7, 11 }), Cells(), 20, 30);
            Assert.True(spots.Count >= 3 && spots.Count < 30);
            Assert.All(spots, s => Assert.Equal(5, s.Z));
            var open = CoverFinder.Find(g, 7, 3, 4, Cells(new[] { 7, 11 }), Cells(), 20, 30, true);
            Assert.True(open.Count > spots.Count);
            Assert.Equal(5, open[0].Z); // covered ones come first
        }

        [Fact]
        public void CoverFinderPrefersCellsThatSeeTheApproach()
        {
            // wall (blocks sight) on the west half, sandbags (sight passes, less cover) on the east half of z=6
            var rows = new string[12];
            for (int z = 11; z >= 0; z--) rows[11 - z] = z == 6 ? "...####ssss...." : "...............";
            var g = CombatGrid.FromAscii(rows);
            var path = Cells(new[] { 9, 11 }, new[] { 9, 10 }, new[] { 9, 9 }, new[] { 9, 8 }, new[] { 9, 7 });
            var spots = CoverFinder.Find(g, 7, 3, 4, Cells(new[] { 9, 11 }), path, 20, 1);
            Assert.Single(spots);
            Assert.True(spots[0].SeesApproach);
            Assert.Equal(5, spots[0].Z);
            Assert.True(spots[0].X >= 6);
        }

        [Fact]
        public void CoverFinderKeepsOffDoorsFogAndThreats()
        {
            var g = CombatGrid.FromAscii("....", ".D..", "f...");
            var spots = CoverFinder.Find(g, 1, 1, 3, Cells(new[] { 3, 2 }), Cells(), 10, 20, true);
            Assert.DoesNotContain(spots, s => s.X == 1 && s.Z == 1);
            Assert.DoesNotContain(spots, s => s.X == 0 && s.Z == 0);
            Assert.DoesNotContain(spots, s => System.Math.Abs(s.X - 3) <= 1 && s.Z >= 1 && s.X >= 2 && s.X == 3 && s.Z == 2);
        }

        static CombatGrid BaseWithGap()
        {
            return CombatGrid.FromAscii(".........", ".........", "####.####", ".........", ".........");
        }

        [Fact]
        public void ApproachPathAndChokepointOnWalledBase()
        {
            var g = BaseWithGap();
            var path = Approach.Path(g, 4, 4, 4, 0);
            Assert.NotNull(path);
            Assert.Equal(5, path.Count);
            Assert.Equal(4, path[0][0]); Assert.Equal(0, path[4][1]);
            int w;
            var c = Approach.Chokepoint(g, path, 1, out w);
            Assert.Equal(2, c[1]); Assert.Equal(1, w);
            Assert.Null(Approach.Chokepoint(g, path, 9, out w));
        }

        [Fact]
        public void ApproachPathUnreachableAndThroughDoor()
        {
            var sealedOff = CombatGrid.FromAscii("..", "##", "..");
            Assert.Null(Approach.Path(sealedOff, 0, 2, 0, 0));
            var door = CombatGrid.FromAscii("..", "D#", "..");
            Assert.NotNull(Approach.Path(door, 0, 2, 0, 0));
            Assert.Null(Approach.Path(door, 1, 1, 0, 0)); // start inside a wall
        }

        [Fact]
        public void CompassWordsAndKeys()
        {
            Assert.Equal("north", Approach.Compass(0, 0, 0, 5));
            Assert.Equal("north", Approach.Compass(0, 0, 1, 10));
            Assert.Equal("north-east", Approach.Compass(0, 0, 5, 5));
            Assert.Equal("east", Approach.Compass(0, 0, 5, 0));
            Assert.Equal("south-east", Approach.Compass(0, 0, 4, -4));
            Assert.Equal("south", Approach.Compass(0, 0, 0, -1));
            Assert.Equal("south-west", Approach.Compass(0, 0, -5, -5));
            Assert.Equal("west", Approach.Compass(0, 0, -5, 0));
            Assert.Equal("north-west", Approach.Compass(0, 0, -5, 5));
            Assert.Equal("here", Approach.Compass(3, 3, 3, 3));
            Assert.Equal("ne", Approach.CompassKey(0, 0, 5, 5));
            Assert.Equal("w", Approach.CompassKey(0, 0, -9, 1));
        }

        [Fact]
        public void KindOfPrecedence()
        {
            Assert.Equal("infestation", CombatWords.KindOf(new HostileInfo { Insect = true, Ranged = true }));
            Assert.Equal("manhunter", CombatWords.KindOf(new HostileInfo { Manhunter = true, Animal = true }));
            Assert.Equal("siege", CombatWords.KindOf(new HostileInfo { Siege = true, Ranged = true }));
            Assert.Equal("mech", CombatWords.KindOf(new HostileInfo { Mech = true }));
            Assert.Equal("ranged", CombatWords.KindOf(new HostileInfo { Ranged = true }));
            Assert.Equal("melee", CombatWords.KindOf(new HostileInfo()));
        }

        [Fact]
        public void GroupSplitsFactionsAndLinksSingleLink()
        {
            var hs = new List<HostileInfo> { H(1, "A", "Pirate gunner", 30, 0, true), H(2, "B", "Tribal", 31, 0), H(3, "A", "Pirate gunner", 40, 0, true), H(4, "A", "Pirate gunner", 50, 0, true) };
            var g = CombatWords.Group(hs, 0, 0);
            Assert.Equal(2, g.Count);
            var a = g.First(x => x.Faction == "A");
            Assert.Equal(3, a.Count); Assert.Equal(1, a.Id); Assert.Equal(new[] { 1, 3, 4 }, a.Ids.ToArray());
            Assert.Equal("ranged", a.Kind);
            Assert.Equal("3 pirate gunners", a.Labels[0]);
            // a gap over 12 cells splits a faction
            var far = CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 0, 0), H(2, "A", "x", 30, 0) }, 0, 0);
            Assert.Equal(2, far.Count);
        }

        [Fact]
        public void GroupKindMajorityAndNullFaction()
        {
            var mech = CombatWords.Group(new List<HostileInfo> {
                new HostileInfo { Id = 1, Mech = true, X = 5, Z = 5 }, new HostileInfo { Id = 2, Mech = true, X = 6, Z = 5 },
                new HostileInfo { Id = 3, Mech = true, X = 7, Z = 5 }, new HostileInfo { Id = 4, Ranged = true, X = 8, Z = 5 } }, 0, 0);
            Assert.Single(mech); Assert.Equal("mech", mech[0].Kind); Assert.Equal("wild", mech[0].Faction);
            var mixed = CombatWords.Group(new List<HostileInfo> {
                H(1, "A", "a", 5, 5, true), H(2, "A", "b", 6, 5, true), H(3, "A", "c", 7, 5), H(4, "A", "d", 8, 5) }, 0, 0);
            Assert.Equal("mixed", mixed[0].Kind);
        }

        [Fact]
        public void GroupHeadingAndWhere()
        {
            Assert.Equal("toward home", CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 30, 0, false, false, 40, 0) }, 0, 0)[0].Heading);
            Assert.Equal("away", CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 30, 0, false, false, 20, 0) }, 0, 0)[0].Heading);
            Assert.Equal("holding", CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 30, 0, false, false, 29, 0) }, 0, 0)[0].Heading);
            Assert.Equal("holding", CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 30, 0) }, 0, 0)[0].Heading);
            var ne = CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 30, 30) }, 0, 0)[0];
            Assert.Equal("north-east, 42 cells from home", ne.Where); Assert.Equal(42, ne.Dist);
        }

        [Fact]
        public void GroupOrdersByDistanceAndSkipsDowned()
        {
            var g = CombatWords.Group(new List<HostileInfo> { H(1, "A", "x", 90, 0), H(2, "B", "y", 20, 0), H(3, "C", "z", 5, 0, false, true) }, 0, 0);
            Assert.Equal(new[] { 2, 1 }, g.Select(x => x.Id).ToArray());
            Assert.Empty(CombatWords.Group(new List<HostileInfo> { H(3, "C", "z", 5, 0, false, true) }, 0, 0));
        }

        [Fact]
        public void DefenseWords()
        {
            Assert.Equal("no walls, no turrets: open ground", CombatWords.Defenses(new DefenseCounts()));
            var d = new DefenseCounts { Turrets = 3, TurretsPowered = 2, Traps = 6, Sandbags = 14, Enclosed = true, Doors = 3, ExteriorDoors = 2 };
            d.Chokepoints.Add("north gap, 2 wide");
            Assert.Equal("3 turrets (2 powered); 6 traps; 14 sandbags; base walled with 2 doors; chokepoint: north gap, 2 wide", CombatWords.Defenses(d));
            Assert.Equal("1 turret (0 powered, 1 no ammo)", CombatWords.Defenses(new DefenseCounts { Turrets = 1, TurretsNoAmmo = 1 }));
            Assert.Equal("walls with 3 gaps", CombatWords.Defenses(new DefenseCounts { WallCells = 40, PerimeterGaps = 3 }));
        }

        [Fact]
        public void MoodRiskAndHealthWords()
        {
            Assert.Equal("", CombatWords.MoodRisk(0.9f, 0.4f, 0.2f, false));
            Assert.Equal("minor", CombatWords.MoodRisk(0.3f, 0.4f, 0.2f, false));
            Assert.Equal("major", CombatWords.MoodRisk(0.1f, 0.4f, 0.2f, false));
            Assert.Equal("breaking", CombatWords.MoodRisk(0.9f, 0.4f, 0.2f, true));
            Assert.Equal("ok", CombatWords.Health(0.8f, false));
            Assert.Equal("hurt", CombatWords.Health(0.6f, false));
            Assert.Equal("badly hurt, bleeding", CombatWords.Health(0.3f, true));
            Assert.Equal("critical", CombatWords.Health(0.1f, false));
        }

        // ---- squad ----
        static SquadMember M(int id, float health = 1f, int x = 0, int z = 0, int target = 0, bool busy = false, bool fleeing = false, bool bleeding = false)
        {
            return new SquadMember { Id = id, Health = health, X = x, Z = z, TargetId = target, Busy = busy, Fleeing = fleeing, Bleeding = bleeding, Drafted = true };
        }

        static SquadState S() { var s = new SquadState(); s.Members.AddRange(new[] { 1, 2 }); return s; }

        [Fact]
        public void DecideRetreatsOnceAndOnlyWithARetreatPoint()
        {
            var s = S(); s.RetreatX = 5; s.RetreatZ = 6;
            var o = SquadTactics.Decide(s, new[] { M(1, 0.3f), M(2, 0.9f) }, new List<HostileInfo>(), new List<ThreatGroup>(), null);
            Assert.Single(o); Assert.Equal("retreat", o[0].Kind); Assert.Equal(1, o[0].Pawn); Assert.Equal(5, o[0].X);
            Assert.Contains("badly hurt", o[0].Note);
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.3f, fleeing: true) }, new List<HostileInfo>(), new List<ThreatGroup>(), null));
            Assert.Single(SquadTactics.Decide(s, new[] { M(1, 0.55f, bleeding: true) }, new List<HostileInfo>(), new List<ThreatGroup>(), null));
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.55f) }, new List<HostileInfo>(), new List<ThreatGroup>(), null));
            Assert.Empty(SquadTactics.Decide(S(), new[] { M(1, 0.1f) }, new List<HostileInfo>(), new List<ThreatGroup>(), null)); // no retreat point
        }

        [Fact]
        public void DecideFocusPawnSkipsBusyAndRepeats()
        {
            var s = S(); s.FocusPawn = 9;
            var hs = new List<HostileInfo> { H(9, "A", "x", 10, 3) };
            var o = SquadTactics.Decide(s, new[] { M(1), M(2, target: 9) }, hs, new List<ThreatGroup>(), null);
            Assert.Single(o); Assert.Equal("attack", o[0].Kind); Assert.Equal(9, o[0].Target); Assert.Equal(10, o[0].X);
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, busy: true), M(2, target: 9) }, hs, new List<ThreatGroup>(), null));
        }

        [Fact]
        public void DecideFocusGroupPicksNearestAndFallsBackFromDownedPawn()
        {
            var s = S(); s.FocusGroup = 5;
            var hs = new List<HostileInfo> { H(5, "A", "x", 10, 0), H(6, "A", "x", 30, 0) };
            var grp = new List<ThreatGroup> { new ThreatGroup { Id = 5, Ids = new List<int> { 5, 6 } } };
            var o = SquadTactics.Decide(s, new[] { M(1, x: 0), M(2, x: 35) }, hs, grp, null);
            Assert.Equal(2, o.Count);
            Assert.Equal(5, o.First(x => x.Pawn == 1).Target); Assert.Equal(6, o.First(x => x.Pawn == 2).Target);
            s.FocusPawn = 7; // not in hostiles: group applies
            Assert.Equal(2, SquadTactics.Decide(s, new[] { M(1), M(2) }, hs, grp, null).Count);
            hs[0].Downed = true;
            var o2 = SquadTactics.Decide(s, new[] { M(1) }, hs, grp, null);
            Assert.Equal(6, o2[0].Target);
        }

        [Fact]
        public void DecideAutoRescueOneRescuerPerDowned()
        {
            var s = S();
            var downed = new List<int[]> { new[] { 50, 3, 3 } };
            var o = SquadTactics.Decide(s, new[] { M(1, 0.9f, 0, 0), M(2, 0.9f, 1, 1) }, new List<HostileInfo>(), new List<ThreatGroup>(), downed);
            Assert.Single(o); Assert.Equal("rescue", o[0].Kind); Assert.Equal(2, o[0].Pawn); Assert.Equal(50, o[0].Target);
            // already being rescued, too far, or only hurt members
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.9f, 0, 0), M(2, 0.9f, 1, 1, target: 50) }, new List<HostileInfo>(), new List<ThreatGroup>(), downed));
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.9f, 40, 40) }, new List<HostileInfo>(), new List<ThreatGroup>(), downed));
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.5f, 0, 0) }, new List<HostileInfo>(), new List<ThreatGroup>(), downed));
            s.AutoRescue = false;
            Assert.Empty(SquadTactics.Decide(s, new[] { M(1, 0.9f) }, new List<HostileInfo>(), new List<ThreatGroup>(), downed));
        }

        [Fact]
        public void DecideSkipsUndraftedAndDowned()
        {
            var s = S(); s.FocusPawn = 9;
            var hs = new List<HostileInfo> { H(9, "A", "x", 10, 3) };
            var a = M(1); a.Drafted = false; var b = M(2); b.Downed = true;
            Assert.Empty(SquadTactics.Decide(s, new[] { a, b }, hs, new List<ThreatGroup>(), null));
        }

        [Fact]
        public void SquadStateJsonRoundTrip()
        {
            var s = new SquadState { Stance = "hold", FocusGroup = 7, FocusPawn = 8, RetreatX = 11, RetreatZ = 12, RetreatBelow = 0.45f, AutoRescue = false, SinceTick = 999 };
            s.Members.AddRange(new[] { 101, 102 });
            var back = SquadState.FromJson(JObject.Parse(s.ToJson().ToString()));
            Assert.Equal(new[] { 101, 102 }, back.Members.ToArray());
            Assert.Equal("hold", back.Stance); Assert.Equal(7, back.FocusGroup); Assert.Equal(8, back.FocusPawn);
            Assert.Equal(11, back.RetreatX); Assert.Equal(12, back.RetreatZ); Assert.Equal(0.45f, back.RetreatBelow);
            Assert.False(back.AutoRescue); Assert.Equal(999, back.SinceTick); Assert.True(back.Active);
            var def = SquadState.FromJson(null);
            Assert.False(def.Active); Assert.Equal(-1, def.RetreatX); Assert.True(def.AutoRescue); Assert.Equal("fire_at_will", def.Stance);
        }
    }
}

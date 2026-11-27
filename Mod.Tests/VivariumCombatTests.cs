using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>Phase 3: GET /combat, /act/squad and /act/defend shapes and refusals, the fight's speed rule, the combat status in
    /// GET /vivarium, caps and undo for defenses, the saved squad, and the controller's focus memory.</summary>
    public class VivariumCombatTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        IVivariumHands CurrentHands;
        int op;

        public VivariumCombatTests()
        {
            CurrentHands = Hands;
            VivariumRoutes.Register(H.Router, () => V, () => CurrentHands);
        }

        ApiResult Post(string path, object body)
        {
            var j = body is string s ? JObject.Parse(s) : JObject.FromObject(body);
            j["op_id"] = "op" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void Bad(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }
        static void Refused(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }
        static NarratorMark Bp(int n, string def = "Sandbags")
        {
            var m = new NarratorMark { Kind = "blueprint", Map = 0, Def = def };
            for (int i = 0; i < n; i++) m.AddCell(10 + i, 20);
            return m;
        }

        // ------------------------------------------------------------ GET /combat and /vivarium
        [Fact]
        public void CombatRouteAddsSquadAndSpeedRule()
        {
            V.Combat.Squad.Members.AddRange(new[] { 101, 102 });
            V.Combat.SpeedBefore = 3;
            var r = H.Call("GET", "/combat", null);
            Assert.True(r.Ok);
            Assert.Same(V.Combat, Hands.LastState);
            Assert.Equal(new[] { 101, 102 }, r.Body["squad"]["members"].Select(t => (int)t).ToArray());
            Assert.True((bool)r.Body["speed_rule"]["slowed"]);
            Assert.Equal(3, (int)r.Body["speed_rule"]["before"]);
            Assert.True((bool)r.Body["active"]);
        }

        [Fact]
        public void CombatRouteNeedsAGame()
        {
            CurrentHands = null;
            Assert.Equal("no_game", H.Call("GET", "/combat", null).Error);
            CurrentHands = Hands; Hands.CombatJson = null;
            Assert.Equal("no_game", H.Call("GET", "/combat", null).Error);
        }

        [Fact]
        public void VivariumShowsCombatAndTheSetting()
        {
            V.Combat.Update(true, 5, 900);
            var r = H.Call("GET", "/vivarium", null);
            Assert.True((bool)r.Body["combat"]["active"]);
            Assert.Equal(5, (int)r.Body["combat"]["hostiles"]);
            Assert.Equal(900, (int)r.Body["combat"]["since_tick"]);
            Assert.True((bool)r.Body["settings"]["slow_for_combat"]);
        }

        [Fact]
        public void CombatStateUpdateFlips()
        {
            var c = new CombatState();
            Assert.True(c.Update(true, 3, 100));
            Assert.False(c.Update(true, 4, 350));
            Assert.Equal(100, c.SinceTick);
            Assert.Equal(4, c.Hostiles);
            Assert.True(c.Update(false, 0, 600));
            Assert.Equal(-1, c.SinceTick);
            Assert.False(c.Update(false, -2, 850));
            Assert.Equal(0, c.Hostiles);
        }

        // ------------------------------------------------------------ /act/squad
        [Theory]
        [InlineData("{\"op\":\"charge\"}", "op must be one of")]
        [InlineData("{\"op\":\"move\"}", "move needs at")]
        [InlineData("{\"op\":\"move\",\"at\":\"roof\"}", "at must be one of")]
        [InlineData("{\"op\":\"move\",\"at\":\"cover\",\"x\":3,\"z\":4}", "not both")]
        [InlineData("{\"op\":\"move\",\"x\":3}", "both x and z")]
        [InlineData("{\"op\":\"focus\"}", "focus needs target")]
        [InlineData("{\"op\":\"draft\",\"pawns\":[0]}", "positive integer ids")]
        [InlineData("{\"op\":\"draft\",\"pawns\":\"Ana\"}", "array of pawn ids")]
        [InlineData("{\"op\":\"retreat\",\"below\":0.95}", "below must be 0.1..0.9")]
        [InlineData("{\"op\":\"retreat\",\"below\":\"half\"}", "below must be a number")]
        [InlineData("{\"op\":\"move\",\"at\":\"cover\",\"room\":12}", "room goes with at door or at inside")]
        public void SquadShapes(string body, string why)
        {
            Bad(Post("/act/squad", body), why);
            Assert.Empty(Hands.Calls);
            Assert.Equal(0, V.Chat.LastSeq);
        }

        [Fact]
        public void SquadPassesArgsAndState()
        {
            Assert.True(Post("/act/squad", new { op = "move", at = "chokepoint", pawns = new[] { 101, 102, 101 }, why = "hold them at the gap" }).Ok);
            var a = (SquadArgs)Hands.LastArgs;
            Assert.Equal("move", a.Op); Assert.Equal("chokepoint", a.At);
            Assert.Equal(new List<int> { 101, 102 }, a.Pawns);
            Assert.Same(V.Combat, Hands.LastState);
            Assert.Equal("did it, since hold them at the gap", V.Chat.Entries.Last().Text);
            Assert.Equal("action", V.Chat.Entries.Last().Who);
        }

        [Fact]
        public void RetreatDefaultsInsideAndTakesBelow()
        {
            Assert.True(Post("/act/squad", new { op = "retreat", below = 0.3 }).Ok);
            var a = (SquadArgs)Hands.LastArgs;
            Assert.Equal("inside", a.At);
            Assert.Equal(0.3f, a.Below, 3);
        }

        [Fact]
        public void SquadRefusalIsAGreyLine()
        {
            Hands.Next = HandResult.No("no hostiles on the map to focus");
            Refused(Post("/act/squad", new { op = "focus", target = 9012 }), "no hostiles");
            Assert.Equal("refused", V.Chat.Entries.Last().Who);
            Assert.Equal("couldn't focus the squad's fire: no hostiles on the map to focus", V.Chat.Entries.Last().Text);
            Assert.Equal(0, V.Actions.LastSeq);
        }

        [Fact]
        public void SquadOrdersPlaceNothingAndCostNothing()
        {
            int before = V.Budget.Left().Blueprints;
            var r = Post("/act/squad", new { op = "draft" });
            Assert.True(r.Ok);
            Assert.Null(r.Body["undo_seq"]);
            Assert.Equal(before, V.Budget.Left().Blueprints);
            Assert.Equal(1, V.Actions.LastSeq);
        }

        // ------------------------------------------------------------ /act/defend
        [Theory]
        [InlineData("{\"kind\":\"moat\"}", "kind must be one of")]
        [InlineData("{\"kind\":\"turret\",\"count\":5}", "count must be 1..4")]
        [InlineData("{\"kind\":\"traps\",\"count\":9}", "count must be 1..8")]
        [InlineData("{\"kind\":\"sandbags\",\"length\":21}", "length must be 1..20")]
        [InlineData("{\"kind\":\"sandbags\",\"side\":\"up\"}", "side must be one of")]
        [InlineData("{\"kind\":\"sandbags\",\"at\":\"gate\"}", "at must be one of")]
        [InlineData("{\"kind\":\"wall\",\"at\":\"entrance\",\"x\":1,\"z\":1}", "not both")]
        [InlineData("{\"kind\":\"killbox\",\"at\":\"chokepoint\"}", "main entrance")]
        public void DefendShapes(string body, string why)
        {
            Bad(Post("/act/defend", body), why);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void DefendDefaults()
        {
            Assert.True(Post("/act/defend", new { kind = "turret" }).Ok);
            var a = (DefendArgs)Hands.LastArgs;
            Assert.Equal(1, a.Count); Assert.Equal("threat", a.Side); Assert.Equal(7, a.Length);
            Assert.True(Post("/act/defend", new { kind = "traps", side = "n" }).Ok);
            a = (DefendArgs)Hands.LastArgs;
            Assert.Equal(4, a.Count); Assert.Equal("n", a.Side);
        }

        [Fact]
        public void DefendChargesCapsAndGoesInTheLedger()
        {
            Hands.Next = HandResult.Placed("planned 7 sandbags", new List<NarratorMark> { Bp(7) });
            var r = Post("/act/defend", new { kind = "sandbags", side = "e", why = "they come from the east" });
            Assert.True(r.Ok);
            Assert.Equal(1, (int)r.Body["undo_seq"]);
            Assert.Equal(50 - 7, (int)r.Body["left"]["blueprints"]);
            Assert.Equal(1, V.Ledger.Count);
            // undo takes it back like any phase 2 placement
            Assert.True(Post("/vivarium/undo", new { }).Ok);
            Assert.Equal(7, Hands.LastUndo[0].CellCount);
        }

        [Fact]
        public void DefendRefusedWhenTheCapIsUsed()
        {
            Hands.Next = HandResult.Placed("planned lots", new List<NarratorMark> { Bp(50) });
            Assert.True(Post("/act/defend", new { kind = "wall" }).Ok);
            Hands.Calls.Clear();
            Refused(Post("/act/defend", new { kind = "sandbags" }), "cap on blueprints");
            Assert.Empty(Hands.Calls);
        }

        // ------------------------------------------------------------ the fight's speed rule
        [Fact]
        public void CombatSlowNeedsTheSettingAndRemembers()
        {
            V.Settings.SlowForCombat = false;
            Refused(Post("/act/time", new { speed = 1, combat = true }), "slow the game for fights");
            V.Settings.SlowForCombat = true;
            Hands.Next = HandResult.Done("slowed", new JObject { ["before"] = 3 });
            Assert.True(Post("/act/time", new { speed = 1, combat = true }).Ok);
            Assert.Equal("combat_speed 1 False", Hands.Calls.Last());
            Assert.Equal(3, V.Combat.SpeedBefore);
            // a second slow does not overwrite the memory with 1x
            Hands.Next = HandResult.Done("slowed", new JObject { ["before"] = 1 });
            Assert.True(Post("/act/time", new { speed = 1, combat = true }).Ok);
            Assert.Equal(3, V.Combat.SpeedBefore);
        }

        [Fact]
        public void ControlsSpeedAlsoAllowsTheSlow()
        {
            V.Settings.SlowForCombat = false; V.Settings.ControlsSpeed = true;
            Assert.True(Post("/act/time", new { speed = 1, combat = true }).Ok);
        }

        [Fact]
        public void RestoreUsesTheMemoryOnce()
        {
            Refused(Post("/act/time", new { combat = true, restore = true }), "did not slow the game");
            V.Combat.SpeedBefore = 4;
            Hands.Next = HandResult.Done("put back");
            Assert.True(Post("/act/time", new { combat = true, restore = true }).Ok);
            Assert.Equal("combat_speed 4 True", Hands.Calls.Last());
            Assert.Equal(0, V.Combat.SpeedBefore);
        }

        [Fact]
        public void RestoreRefusedByTheGameKeepsNothing()
        {
            V.Combat.SpeedBefore = 2;
            Hands.Next = HandResult.No("the player changed the speed during the fight; leaving it");
            Refused(Post("/act/time", new { combat = true, restore = true }), "player changed");
            Assert.Equal(2, V.Combat.SpeedBefore);   // the agent stops asking; a later explicit speed clears it
            V.Settings.ControlsSpeed = true;
            Hands.Next = HandResult.Done("set");
            Assert.True(Post("/act/time", new { speed = 2 }).Ok);
            Assert.Equal(0, V.Combat.SpeedBefore);
        }

        [Theory]
        [InlineData("{\"combat\":true,\"speed\":3}", "only slows to speed 1")]
        [InlineData("{\"combat\":true,\"pause\":true}", "not pause")]
        [InlineData("{\"combat\":true,\"restore\":true,\"speed\":1}", "not both")]
        [InlineData("{\"restore\":true}", "restore goes with combat")]
        [InlineData("{\"combat\":\"yes\",\"speed\":1}", "combat must be true or false")]
        public void CombatTimeShapes(string body, string why)
        {
            Bad(Post("/act/time", body), why);
        }

        // ------------------------------------------------------------ saved state
        [Fact]
        public void SquadAndSpeedSurviveTheSave()
        {
            V.Combat.Squad.Members.AddRange(new[] { 7, 8 });
            V.Combat.Squad.Stance = "hold";
            V.Combat.Squad.Fell.Add(8);
            V.Combat.Squad.FocusGroup = 900; V.Combat.Squad.FocusIds.AddRange(new[] { 900, 901 });
            V.Combat.SpeedBefore = 3; V.Combat.LastSide = "ne";
            V.Combat.Update(true, 4, 10);
            var back = new Vivarium();
            back.Import(JObject.Parse(V.Export().ToString()));
            Assert.Equal(new List<int> { 7, 8 }, back.Combat.Squad.Members);
            Assert.Equal("hold", back.Combat.Squad.Stance);
            Assert.Equal(new List<int> { 8 }, back.Combat.Squad.Fell);
            Assert.Equal(new List<int> { 900, 901 }, back.Combat.Squad.FocusIds);
            Assert.Equal(3, back.Combat.SpeedBefore);
            Assert.Equal("ne", back.Combat.LastSide);
            Assert.False(back.Combat.Active);   // the fight itself is re-read from the map
        }

        [Fact]
        public void DamagedCombatSaveIsTolerated()
        {
            var v = new Vivarium();
            v.Import(JObject.Parse("{\"combat\":{\"squad\":\"oops\",\"speed_before\":9}}"));
            Assert.Empty(v.Combat.Squad.Members);
            Assert.Equal(0, v.Combat.SpeedBefore);
            v.Import(JObject.Parse("{}"));
            Assert.Null(v.Combat.LastSide);
        }

        // ------------------------------------------------------------ words and the focus memory
        [Fact]
        public void TextHelpers()
        {
            Assert.Equal("behind the north wall", CombatText.PositionWords("wall_n", 0));
            Assert.Equal("inside that room's door", CombatText.PositionWords("door", 12));
            Assert.Equal("n", CombatText.Cardinal("ne"));
            Assert.Equal("e", CombatText.Cardinal("se"));
            Assert.Equal("s", CombatText.Cardinal("sw"));
            Assert.Equal("w", CombatText.Cardinal("nw"));
            Assert.Null(CombatText.Cardinal("here"));
            Assert.Equal("Ana", CombatText.Names(new[] { "Ana" }));
            Assert.Equal("Ana and Bram", CombatText.Names(new[] { "Ana", "Bram" }));
            Assert.Equal("Ana, Bram and Cy", CombatText.Names(new[] { "Ana", "Bram", "Cy" }));
            Assert.Equal("A, B, C, D and 2 more", CombatText.Names(new[] { "A", "B", "C", "D", "E", "F" }));
            Assert.Equal("move the squad to the chokepoint", CombatText.SquadWhat("move", "chokepoint", 0));
            Assert.Equal("plan a killbox", CombatText.DefendWhat("killbox"));
        }

        [Fact]
        public void FocusFollowsTheGroupWhenItsIdMoves()
        {
            var s = new SquadState { FocusGroup = 10 };
            s.FocusIds.AddRange(new[] { 10, 11, 12 });
            var groups = new List<ThreatGroup>
            {
                new ThreatGroup { Id = 5, Ids = new List<int> { 5, 6 } },
                new ThreatGroup { Id = 11, Ids = new List<int> { 11, 12 } }   // 10 died: the group is now #11
            };
            var g = SquadTactics.FocusedGroup(s, groups);
            Assert.Equal(11, g.Id);
            Assert.Equal(11, s.FocusGroup);
            Assert.Null(SquadTactics.FocusedGroup(new SquadState { FocusGroup = 99 }, groups));
        }

        [Fact]
        public void FellMembersAreNotOrderedAgain()
        {
            var s = new SquadState { RetreatX = 5, RetreatZ = 5 };
            s.Members.Add(1);
            var m = new SquadMember { Id = 1, Health = 0.2f, Drafted = true, X = 20, Z = 20 };
            var first = SquadTactics.Decide(s, new[] { m }, new List<HostileInfo>(), new List<ThreatGroup>(), null);
            Assert.Equal("retreat", first.Single().Kind);
            s.Fell.Add(1); m.Fleeing = s.Fell.Contains(1);
            Assert.Empty(SquadTactics.Decide(s, new[] { m }, new List<HostileInfo>(), new List<ThreatGroup>(), null));
        }
    }
}

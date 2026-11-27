using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>Phase 2: the builder routes' shape checks and refusals, the per-cycle caps, the undo ledger, footprint math.</summary>
    public class VivariumBuildTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        IVivariumHands CurrentHands;
        int op;

        public VivariumBuildTests()
        {
            CurrentHands = Hands;
            VivariumRoutes.Register(H.Router, () => V, () => CurrentHands);
        }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
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
        static NarratorMark Bp(int n, string def = "Wall")
        {
            var m = new NarratorMark { Kind = "blueprint", Map = 0, Def = def };
            for (int i = 0; i < n; i++) m.AddCell(10 + i, 20);
            return m;
        }

        // ------------------------------------------------------------ shapes and refusals (no game touched)
        [Theory]
        [InlineData("{\"kind\":\"dig\"}", "kind must be one of")]
        [InlineData("{\"kind\":\"mine\",\"x\":3}", "both x and z")]
        [InlineData("{\"kind\":\"mine\",\"w\":3,\"h\":3}", "south-west corner")]
        [InlineData("{\"kind\":\"chop\",\"radius\":5}", "radius needs a centre")]
        [InlineData("{\"kind\":\"chop\",\"ore\":\"steel\"}", "ore is only for kind mine")]
        [InlineData("{\"kind\":\"deconstruct\"}", "deconstruct needs a box")]
        [InlineData("{\"kind\":\"cancel\"}", "cancel needs a box")]
        [InlineData("{\"kind\":\"mine\",\"max\":500}", "max must be 1..200")]
        [InlineData("{\"kind\":\"mine\",\"x\":-1,\"z\":2}", "x must be 0..1000")]
        public void DesignateShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "d" + (++op);
            Bad(H.Call("POST", "/act/designate", j.ToString()), why);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void DesignateArgsReachTheHand()
        {
            Assert.True(Post("/act/designate", new { kind = "MINE", ore = "steel", max = 12 }).Ok);
            var a = (DesignateArgs)Hands.LastArgs;
            Assert.Equal("mine", a.Kind); Assert.Equal("steel", a.Ore); Assert.Equal(12, a.Max); Assert.False(a.PlayerAsked);
            Assert.True(Post("/act/designate", new { kind = "chop", x = 5, z = 6, w = 3, h = 2 }).Ok);
            a = (DesignateArgs)Hands.LastArgs;
            Assert.Equal(3, a.At.Box.W); Assert.Equal(6, a.At.Box.Z); Assert.Equal(40, a.Max);
            Assert.True(Post("/act/designate", new { kind = "haul", x = 5, z = 6, radius = 4 }).Ok);
            Assert.Equal(4, ((DesignateArgs)Hands.LastArgs).At.Radius);
        }

        [Fact]
        public void PlayerAskedNeedsARecentPlayerLine()
        {
            Refused(Post("/act/designate", new { kind = "deconstruct", target = 9, player_asked = true }), "said nothing in the chat");
            V.Chat.Add("player", "tear down that shack", H.State.Tick - 100, to: 7);   // to a colonist: does not count
            Refused(Post("/act/designate", new { kind = "deconstruct", target = 9, player_asked = true }), "said nothing");
            V.Chat.Add("player", "tear down that shack", H.State.Tick - VivariumRoutes.AskedTicks - 1);   // too old
            Refused(Post("/act/designate", new { kind = "deconstruct", target = 9, player_asked = true }), "said nothing");
            V.Chat.Add("player", "tear down that shack", H.State.Tick - 10);
            Assert.True(Post("/act/designate", new { kind = "deconstruct", target = 9, player_asked = true }).Ok);
            Assert.True(((DesignateArgs)Hands.LastArgs).PlayerAsked);
        }

        [Theory]
        [InlineData("{}", "thing is required")]
        [InlineData("{\"thing\":\"stove\",\"rot\":\"up\"}", "rot must be")]
        [InlineData("{\"thing\":\"stove\",\"rot\":7}", "rot must be")]
        [InlineData("{\"thing\":\"stove\",\"x\":3,\"z\":4,\"room\":2}", "not both")]
        [InlineData("{\"thing\":\"stove\",\"stuff\":5}", "stuff must be a string")]
        public void BuildShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "b" + (++op);
            Bad(H.Call("POST", "/act/build", j.ToString()), why);
        }

        [Fact]
        public void BuildArgsAndRot()
        {
            Assert.True(Post("/act/build", new { thing = "stove", stuff = "steel", rot = "east", room = 12 }).Ok);
            var a = (BuildArgs)Hands.LastArgs;
            Assert.Equal("stove", a.Thing); Assert.Equal("steel", a.Stuff); Assert.Equal(1, a.Rot); Assert.Equal(12, a.At.Room);
            Assert.True(Post("/act/build", new { thing = "wall", x = 1, z = 2, rot = 3 }).Ok);
            a = (BuildArgs)Hands.LastArgs;
            Assert.Equal(3, a.Rot); Assert.Equal(1, a.At.X); Assert.Equal(-1, ((BuildArgs)new BuildArgs()).Rot);
        }

        [Theory]
        [InlineData("{\"purpose\":\"castle\"}", "purpose must be one of")]
        [InlineData("{\"purpose\":\"bedroom\",\"w\":2,\"h\":5}", "w must be 3..11")]
        [InlineData("{\"purpose\":\"bedroom\",\"w\":5}", "both w and h")]
        [InlineData("{\"purpose\":\"bedroom\",\"size\":\"huge\"}", "size must be one of")]
        [InlineData("{\"purpose\":\"bedroom\",\"door\":\"up\"}", "door must be one of")]
        [InlineData("{\"purpose\":\"bedroom\",\"room\":3}", "not a room id")]
        public void RoomShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "r" + (++op);
            Bad(H.Call("POST", "/act/room", j.ToString()), why);
        }

        [Fact]
        public void RoomSizeFromPurpose()
        {
            Assert.True(Post("/act/room", new { purpose = "bedroom" }).Ok);
            var a = (RoomArgs)Hands.LastArgs;
            Assert.Equal(5, a.W); Assert.Equal(5, a.H); Assert.Equal("auto", a.Door);
            Assert.True(Post("/act/room", new { purpose = "storage", size = "large", door = "n", stuff = "granite" }).Ok);
            a = (RoomArgs)Hands.LastArgs;
            Assert.Equal(10, a.W); Assert.Equal(9, a.H); Assert.Equal("n", a.Door); Assert.Equal("granite", a.Stuff);
        }

        [Theory]
        [InlineData("{\"kind\":\"grow\"}", "needs a plant")]
        [InlineData("{\"kind\":\"stockpile\",\"plant\":\"rice\"}", "only for kind grow")]
        [InlineData("{\"kind\":\"stockpile\",\"preset\":\"guns\"}", "preset must be one of")]
        [InlineData("{\"kind\":\"stockpile\",\"priority\":\"top\"}", "priority must be one of")]
        [InlineData("{\"kind\":\"stockpile\",\"w\":21,\"h\":2}", "w must be 1..20")]
        [InlineData("{\"kind\":\"stockpile\",\"x\":1,\"z\":1,\"room\":3}", "not both")]
        public void ZoneShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "z" + (++op);
            Bad(H.Call("POST", "/act/zone", j.ToString()), why);
        }

        [Theory]
        [InlineData("{\"kind\":\"home\",\"op\":\"add\"}", "give the box")]
        [InlineData("{\"kind\":\"allowed\",\"op\":\"add\",\"x\":1,\"z\":1,\"w\":2,\"h\":2}", "needs its name")]
        [InlineData("{\"kind\":\"home\",\"op\":\"add\",\"x\":1,\"z\":1,\"w\":2,\"h\":2,\"assign\":[1]}", "only for allowed")]
        [InlineData("{\"kind\":\"allowed\",\"name\":\"in\",\"op\":\"add\",\"x\":1,\"z\":1,\"w\":2,\"h\":2,\"assign\":[0]}", "positive integer")]
        [InlineData("{\"kind\":\"home\",\"op\":\"grow\",\"x\":1,\"z\":1,\"w\":2,\"h\":2}", "op must be one of")]
        public void AreaShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "a" + (++op);
            Bad(H.Call("POST", "/act/area", j.ToString()), why);
        }

        [Theory]
        [InlineData("{\"op\":\"add\",\"bench\":4}", "needs a recipe")]
        [InlineData("{\"op\":\"remove\",\"bench\":4}", "index")]
        [InlineData("{\"op\":\"add\",\"bench\":4,\"recipe\":\"x\",\"count\":2,\"until\":3}", "not both")]
        [InlineData("{\"op\":\"suspend\",\"bench\":4,\"index\":1,\"count\":2}", "only for add")]
        [InlineData("{\"op\":\"add\",\"recipe\":\"x\"}", "bench is required")]
        [InlineData("{\"op\":\"add\",\"bench\":4,\"recipe\":\"x\",\"radius\":1}", "radius must be 3..999")]
        public void BillShapes(string body, string why)
        {
            var j = JObject.Parse(body); j["op_id"] = "l" + (++op);
            Bad(H.Call("POST", "/act/bill", j.ToString()), why);
        }

        [Fact]
        public void HandRefusalAndNoGame()
        {
            Hands.Next = HandResult.No("no thing 'stoev' (try: stove, wall)");
            Refused(Post("/act/build", new { thing = "stoev" }), "stoev");
            Assert.Equal(0, V.Ledger.Count);
            CurrentHands = null;
            var r = Post("/act/room", new { purpose = "bedroom" });
            Assert.Equal("no_game", r.Error);
            Assert.Equal("no_game", H.Call("GET", "/benches").Error);
        }

        // ------------------------------------------------------------ caps
        [Fact]
        public void PlacingChargesTheCycleAndRefusesPastTheCap()
        {
            Hands.Next = HandResult.Placed("placed 40 walls", new List<NarratorMark> { Bp(40) });
            var r = Post("/act/build", new { thing = "wall" });
            Assert.True(r.Ok);
            Assert.Equal(10, (int)r.Body["left"]["blueprints"]);
            Assert.Equal(1, (int)r.Body["undo_seq"]);
            Assert.Equal(50, Hands.LastLeft.Blueprints);
            // a medium bedroom (5x5) needs 16 blueprints: refused before the hand
            Hands.Calls.Clear();
            Refused(Post("/act/room", new { purpose = "bedroom" }), "only 10 are left");
            Assert.Empty(Hands.Calls);
            Hands.Next = HandResult.Placed("placed 10", new List<NarratorMark> { Bp(10) });
            Assert.True(Post("/act/build", new { thing = "wall" }).Ok);
            Refused(Post("/act/build", new { thing = "wall" }), "cap on blueprints");
            // a new steward cycle refills
            Assert.True(H.Call("POST", "/chat/status", new JObject { ["op_id"] = "s1", ["who"] = "narrator", ["state"] = "thinking", ["stream_id"] = "x1" }.ToString()).Ok);
            Assert.Equal(50, V.Budget.Blueprints);
        }

        [Fact]
        public void ColonistStatusDoesNotRefillAndAnHourDoes()
        {
            V.Budget.NewCycle(H.State.Tick);
            V.Budget.Charge(new List<NarratorMark> { Bp(50) });
            H.Call("POST", "/chat/status", new JObject { ["op_id"] = "s2", ["who"] = "Ana", ["to"] = 5, ["state"] = "thinking", ["stream_id"] = "x2" }.ToString());
            Assert.Equal(0, V.Budget.Blueprints);
            Refused(Post("/act/build", new { thing = "wall" }), "used up");
            H.State.Tick += BuildBudget.CycleTicks;
            Assert.True(Post("/act/build", new { thing = "wall" }).Ok);
        }

        [Fact]
        public void ZoneAndAreaCellCaps()
        {
            V.Budget.NewCycle(H.State.Tick);
            var m = new NarratorMark { Kind = "zone", Id = 3 };
            for (int i = 0; i < 190; i++) m.AddCell(i, 0);
            V.Budget.Charge(new List<NarratorMark> { m });
            Refused(Post("/act/zone", new { kind = "grow", plant = "rice" }), "only 10 are left");   // medium grow = 8x8
            Refused(Post("/act/area", new { kind = "home", op = "add", x = 1, z = 1, w = 4, h = 4 }), "16 cells");
            Assert.True(Post("/act/zone", new { kind = "stockpile", room = 4 }).Ok);   // a room's floor: the hand caps it
            Assert.Equal(10, Hands.LastLeft.Cells);
        }

        [Fact]
        public void BillAndDesignationCaps()
        {
            V.Budget.NewCycle(H.State.Tick);
            var bills = new List<NarratorMark>();
            for (int i = 0; i < 4; i++) bills.Add(new NarratorMark { Kind = "bill", Id = 4, Sub = i + 1 });
            V.Budget.Charge(bills);
            Refused(Post("/act/bill", new { op = "add", bench = 4, recipe = "simple meal" }), "cap on bills");
            Assert.True(Post("/act/bill", new { op = "suspend", bench = 4, index = 1 }).Ok);
            var d = new NarratorMark { Kind = "designation", Def = "Mine" };
            for (int i = 0; i < 150; i++) d.Things.Add(i + 1);
            V.Budget.Charge(new List<NarratorMark> { d });
            Refused(Post("/act/designate", new { kind = "mine", ore = "steel" }), "cap on designations");
            Assert.True(Post("/act/designate", new { kind = "cancel", target = 3 }).Ok);   // cancelling is never capped
        }

        [Fact]
        public void BudgetCosts()
        {
            var z = new NarratorMark { Kind = "zone_cells" }; z.AddCell(1, 1); z.AddCell(1, 2);
            var a = new NarratorMark { Kind = "area_cells" }; a.AddCell(1, 1);
            var d = new NarratorMark { Kind = "designation" }; d.Things.Add(5); d.AddCell(2, 2);
            var ms = new List<NarratorMark> { Bp(3), z, a, d, new NarratorMark { Kind = "bill" } };
            Assert.Equal(3, BuildBudget.CostBlueprints(ms));
            Assert.Equal(3, BuildBudget.CostCells(ms));
            Assert.Equal(2, BuildBudget.CostDesignations(ms));
            Assert.Equal(1, BuildBudget.CostBills(ms));
            var b = new BuildBudget(); b.NewCycle(0); b.Charge(ms);
            Assert.Equal(47, b.Blueprints); Assert.Equal(197, b.Cells); Assert.Equal(148, b.Designations); Assert.Equal(3, b.Bills);
            var left = b.Left(); left.Blueprints = 0;
            Assert.Equal(47, b.Blueprints);   // a copy
        }

        // ------------------------------------------------------------ undo
        [Fact]
        public void UndoTakesBackTheNewestFirstAndLogs()
        {
            Hands.Next = HandResult.Placed("placed a room", new List<NarratorMark> { Bp(15), Bp(1, "Door") });
            Assert.True(Post("/act/room", new { purpose = "bedroom" }).Ok);
            Hands.Next = HandResult.Placed("made a field", new List<NarratorMark> { new NarratorMark { Kind = "zone", Id = 9 } });
            Assert.True(Post("/act/zone", new { kind = "grow", plant = "rice", size = "small" }).Ok);
            Hands.Next = HandResult.Done("set Cooking 1 for Ana");
            Assert.True(Post("/act/work", new { pawn = 1, priorities = new { Cooking = 1 } }).Ok);   // places nothing: not in the ledger
            Assert.Equal(2, V.Ledger.Count);
            Assert.Equal(2, (int)H.Call("GET", "/vivarium").Body["undoable"]);

            var r = Post("/vivarium/undo", new { });
            Assert.True(r.Ok);
            Assert.Equal("zone", Hands.LastUndo.Single().Kind);
            Assert.Contains("made a field", r.Body["undone"].ToString());
            Assert.Equal(1, (int)r.Body["undoable"]);
            r = Post("/vivarium/undo", new { n = 5 });
            Assert.True(r.Ok);
            Assert.Equal(2, Hands.LastUndo.Count);
            Assert.Equal(0, V.Ledger.Count);
            Refused(Post("/vivarium/undo", new { }), "nothing that can be undone");
            var acts = H.Call("GET", "/actions").Body.ToString();
            Assert.Contains("undid 1 narrator action", acts);
            Assert.Contains("\"player\"", acts);
        }

        [Fact]
        public void UndoThatFailsKeepsTheLedger()
        {
            V.Ledger.Add("build", "placed a wall", 1, new List<NarratorMark> { Bp(1) });
            Hands.UndoNext = HandResult.No("the game refused: x");
            Refused(Post("/vivarium/undo", new { }), "the game refused");
            Assert.Equal(1, V.Ledger.Count);
            Bad(Post("/vivarium/undo", new { n = 21 }), "n must be 1..20");
            var g = H.Call("GET", "/vivarium/undo");
            Assert.Equal(1, (int)g.Body["count"]);
            Assert.Equal("placed a wall", (string)g.Body["newest_first"][0]["note"]);
        }

        [Fact]
        public void UndoLastHelperForChatAndSettings()
        {
            V.Ledger.Add("zone", "made a dump", 1, new List<NarratorMark> { new NarratorMark { Kind = "zone", Id = 2 } });
            var r = VivariumRoutes.UndoLast(V, Hands, 1, 50, "player");
            Assert.True(r.Ok);
            Assert.StartsWith("undid 1 narrator action", r.Note);
            Assert.False(VivariumRoutes.UndoLast(V, Hands, 1, 51, "player").Ok);
        }

        [Fact]
        public void LedgerSavesAndCaps()
        {
            for (int i = 0; i < NarratorLedger.Max + 5; i++)
                V.Ledger.Add("build", "n" + i, i, new List<NarratorMark> { Bp(2) });
            Assert.Equal(NarratorLedger.Max, V.Ledger.Count);
            Assert.Null(V.Ledger.Add("build", "nothing", 1, new List<NarratorMark>()));
            var saved = V.Export();
            var v2 = new Vivarium();
            v2.Import(JObject.Parse(saved.ToString()));
            Assert.Equal(NarratorLedger.Max, v2.Ledger.Count);
            var last = v2.Ledger.Entries.Last();
            Assert.Equal("n" + (NarratorLedger.Max + 4), last.Note);
            Assert.Equal(new List<int> { 10, 20, 11, 20 }, last.Marks[0].Cells);
            Assert.Equal("Wall", last.Marks[0].Def);
            var e = v2.Ledger.Add("zone", "z", 1, new List<NarratorMark> { new NarratorMark { Kind = "zone", Id = 1 } });
            Assert.Equal(NarratorLedger.Max + 6, e.Seq);   // seq continues after a load
            v2.Clear();
            Assert.Equal(0, v2.Ledger.Count);
        }

        [Fact]
        public void MarkJsonRoundTripDropsJunk()
        {
            var m = NarratorMark.FromJson(JObject.Parse("{\"kind\":\"bill\",\"map\":2,\"id\":44,\"sub\":7,\"cells\":[1,2,3],\"things\":[5,\"x\"]}"));
            Assert.Equal(new List<int> { 1, 2 }, m.Cells);
            Assert.Equal(new List<int> { 5 }, m.Things);
            Assert.Equal(44, m.Id); Assert.Equal(7, m.Sub);
            Assert.Null(NarratorMark.FromJson(JObject.Parse("{\"map\":2}")));
        }

        // ------------------------------------------------------------ footprint math
        [Fact]
        public void RoomWallsAreThePerimeterMinusTheDoor()
        {
            var b = new CellBox(10, 20, 5, 4);
            int[] door;
            var walls = BuildPure.RoomWalls(b, "s", out door);
            Assert.Equal(new[] { 12, 20 }, door);
            Assert.Equal(2 * (5 + 4) - 4 - 1, walls.Count);
            Assert.DoesNotContain(walls, c => c[0] == 12 && c[1] == 20);
            Assert.Equal(walls.Count, walls.Select(c => c[0] + "," + c[1]).Distinct().Count());
            Assert.All(walls, c => Assert.True(c[0] == 10 || c[0] == 14 || c[1] == 20 || c[1] == 23));
            Assert.Equal(new[] { 10, 20 }, walls[0]);
            BuildPure.RoomWalls(b, "n", out door); Assert.Equal(new[] { 12, 23 }, door);
            BuildPure.RoomWalls(b, "e", out door); Assert.Equal(new[] { 14, 22 }, door);
            BuildPure.RoomWalls(b, "w", out door); Assert.Equal(new[] { 10, 22 }, door);
            var inside = BuildPure.RoomInside(b);
            Assert.Equal(3 * 2, inside.Count);
            Assert.All(inside, c => Assert.True(c[0] > 10 && c[0] < 14 && c[1] > 20 && c[1] < 23));
        }

        [Fact]
        public void SmallestRoomAndCornersNeverHoldTheDoor()
        {
            var b = new CellBox(0, 0, 3, 3);
            foreach (var s in new[] { "n", "e", "s", "w" })
            {
                int[] door;
                var walls = BuildPure.RoomWalls(b, s, out door);
                Assert.Equal(7, walls.Count);
                Assert.False((door[0] == 0 || door[0] == 2) && (door[1] == 0 || door[1] == 2));
            }
            Assert.Single(BuildPure.RoomInside(b));
            Assert.Throws<System.ArgumentException>(() => { int[] d; BuildPure.RoomWalls(new CellBox(0, 0, 2, 5), "s", out d); });
        }

        [Theory]
        [InlineData(0, 30, "n")]
        [InlineData(0, -30, "s")]
        [InlineData(40, 2, "e")]
        [InlineData(-40, 2, "w")]
        [InlineData(0, 0, "s")]   // the centre itself
        public void DoorFacesHome(int dx, int dz, string side)
        {
            var b = new CellBox(0, 0, 5, 5);   // centre (2, 2)
            Assert.Equal(side, BuildPure.DoorToward(b, 2 + dx, 2 + dz));
        }

        [Fact]
        public void SizesAndSites()
        {
            int w, h;
            BuildPure.RoomSize("bedroom", "small", out w, out h); Assert.Equal(4, w); Assert.Equal(4, h);
            BuildPure.RoomSize("storage", "large", out w, out h); Assert.Equal(10, w); Assert.Equal(9, h);
            BuildPure.RoomSize("rec", "large", out w, out h); Assert.True(w <= BuildPure.MaxRoom && h <= BuildPure.MaxRoom);
            foreach (var p in BuildPure.Purposes) foreach (var s in BuildPure.Sizes)
            {
                BuildPure.RoomSize(p, s, out w, out h);
                Assert.InRange(2 * (w + h) - 4, 8, new BuildBudget().MaxBlueprints);   // every planner room fits one cycle
            }
            BuildPure.ZoneSize("grow", "large", out w, out h); Assert.Equal(121, w * h);
            Assert.True(w * h <= new BuildBudget().MaxCells);
            var c = BuildPure.SiteCandidates(50, 50, 5, 5, 2).ToList();
            Assert.Equal(25, c.Count);                     // 1 + 8 + 16
            Assert.Equal(new[] { 48, 48 }, c[0]);
            Assert.Equal(c.Count, c.Select(x => x[0] + "," + x[1]).Distinct().Count());
            Assert.Equal(9, BuildPure.BoxCells(new CellBox(1, 1, 3, 3)).Count);
        }

        [Fact]
        public void WordLists()
        {
            Assert.Equal("FueledStove", BuildPure.ThingDefs("stove")[0]);   // the no-power version first
            Assert.Equal("Wall", BuildPure.ThingDefs("walls")[0]);
            Assert.Equal("Wall", BuildPure.ThingDefs("WALL")[0]);
            Assert.Equal("MyModBench", BuildPure.ThingDefs("MyModBench")[0]);
            Assert.Empty(BuildPure.ThingDefs("  "));
            Assert.Equal("BlocksGranite", BuildPure.StuffDef("granite blocks"));
            Assert.Equal("WoodLog", BuildPure.StuffDef("Wood"));
            Assert.Equal("Plant_Potato", BuildPure.PlantDef("potatoes"));
            Assert.Equal(0, BuildPure.Rot("north")); Assert.Equal(3, BuildPure.Rot("W"));
            Assert.Equal(-1, BuildPure.Rot(null)); Assert.Equal(-2, BuildPure.Rot("up"));
            Assert.Equal("a door", BuildPure.Count(1, "door", "doors"));
            Assert.Equal("3 doors", BuildPure.Count(3, "door", "doors"));
        }
    }
}

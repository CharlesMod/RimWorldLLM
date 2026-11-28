using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>POST /act/place shapes, refusals and greentext, plus the pure place algorithms.</summary>
    [Collection("PlacePureStatic")]
    public class PlaceRouteTests : IDisposable
    {
        sealed class FakePlace : IPlaceHands
        {
            public PlaceArgs Last; public BuildBudget LastLeft; public int Calls;
            public HandResult Next = HandResult.Done("did it");
            public HandResult Place(PlaceArgs a, BuildBudget left) { Calls++; Last = a; LastLeft = left; return Next; }
        }

        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        readonly FakePlace Place = new FakePlace();
        readonly Func<IPlaceHands> Saved;
        int op;

        public PlaceRouteTests()
        {
            Saved = PlacePure.Hands;
            PlacePure.Hands = () => Place;
            VivariumRoutes.Register(H.Router, () => V, () => Hands);
        }
        public void Dispose() { PlacePure.Hands = Saved; }

        ApiResult Post(object body)
        {
            var j = body is string s ? JObject.Parse(s) : JObject.FromObject(body);
            j["op_id"] = "op" + (++op);
            return H.Call("POST", "/act/place", j.ToString());
        }
        static void Bad(ApiResult r, string contains)
        {
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }

        [Theory]
        [InlineData("{\"op\":\"furnish\",\"what\":\"research bench\",\"at\":\"lab\"}", "furnish")]
        [InlineData("{\"op\":\"generator\",\"what\":\"solar\"}", "generator")]
        [InlineData("{\"op\":\"battery\"}", "battery")]
        [InlineData("{\"op\":\"connect\",\"at\":\"kitchen\"}", "connect")]
        [InlineData("{\"op\":\"switch\"}", "switch")]
        [InlineData("{\"op\":\"heater\",\"at\":\"bedroom\"}", "heater")]
        [InlineData("{\"op\":\"cooler\",\"at\":\"freezer\"}", "cooler")]
        [InlineData("{\"op\":\"target\",\"at\":\"freezer\",\"temp\":\"Freezing\"}", "target")]
        [InlineData("{\"op\":\"ship\",\"what\":\"engine\"}", "ship")]
        public void EachOpParses(string body, string op)
        {
            Assert.True(Post(body).Ok);
            Assert.Equal(op, Place.Last.Op);
            Assert.Equal(1, Place.Calls);
        }

        [Theory]
        [InlineData("{\"op\":\"dance\"}", "op must be one of")]
        [InlineData("{\"op\":\"furnish\",\"at\":\"lab\"}", "furnish needs what")]
        [InlineData("{\"op\":\"furnish\",\"what\":\"bed\"}", "furnish needs what")]
        [InlineData("{\"op\":\"generator\"}", "generator needs what")]
        [InlineData("{\"op\":\"generator\",\"what\":\"coal\"}", "generator needs what")]
        [InlineData("{\"op\":\"connect\"}", "connect needs at")]
        [InlineData("{\"op\":\"heater\"}", "heater needs at")]
        [InlineData("{\"op\":\"cooler\"}", "cooler needs at")]
        [InlineData("{\"op\":\"target\",\"at\":\"lab\"}", "target needs at")]
        [InlineData("{\"op\":\"target\",\"temp\":\"warm\"}", "target needs at")]
        [InlineData("{\"op\":\"target\",\"at\":\"lab\",\"temp\":\"toasty\"}", "temp must be one of")]
        [InlineData("{\"op\":\"ship\"}", "ship needs what")]
        [InlineData("{\"op\":\"ship\",\"what\":\"wing\"}", "ship needs what")]
        [InlineData("{\"op\":\"ship\",\"what\":\"launch\"}", "the keeper launches the ship; the narrator cannot launch")]
        public void ShapeRefusals(string body, string why)
        {
            Bad(Post(body), why);
            Assert.Equal(0, Place.Calls);
        }

        [Fact]
        public void TempBecomesDegrees()
        {
            Assert.True(Post(new { op = "target", at = "freezer", temp = "freezing" }).Ok);
            Assert.Equal(-10, Place.Last.Degrees);
            Assert.Equal("freezing", Place.Last.Temp);
        }

        [Fact]
        public void GreentextUsesTheWhatLine()
        {
            Place.Next = HandResult.Done("put a research bench in the lab");
            Assert.True(Post(new { op = "furnish", what = "research bench", at = "lab", why = "research is slow" }).Ok);
            Assert.Equal("put a research bench in the lab, since research is slow", V.Chat.Entries.Last().Text);
            Place.Next = HandResult.No("no room called lab");
            Assert.False(Post(new { op = "furnish", what = "research bench", at = "lab" }).Ok);
            Assert.Equal("couldn't furnish the lab with a research bench: no room called lab", V.Chat.Entries.Last().Text);
            Assert.False(Post(new { op = "connect", at = "kitchen" }).Ok);
            Assert.Equal("couldn't run power to the kitchen: no room called lab", V.Chat.Entries.Last().Text);
            Assert.False(Post(new { op = "target", at = "freezer", temp = "freezing" }).Ok);
            Assert.Equal("couldn't set the freezer to freezing: no room called lab", V.Chat.Entries.Last().Text);
        }

        [Fact]
        public void NoHandsIsARefusalNotACrash()
        {
            PlacePure.Hands = null;
            var r = Post(new { op = "battery" });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Contains("not ready", (string)r.Body["detail"]);
            PlacePure.Hands = () => null;
            Assert.Equal("refused", Post(new { op = "battery" }).Error);
        }

        [Fact]
        public void RefusedWhenNoBlueprintsLeft()
        {
            var m = new NarratorMark { Kind = "blueprint", Map = 0, Def = "Battery" };
            for (int i = 0; i < 50; i++) m.AddCell(i, 0);
            Place.Next = HandResult.Placed("placed", new List<NarratorMark> { m });
            Assert.True(Post(new { op = "battery" }).Ok);
            Place.Calls = 0;
            var r = Post(new { op = "battery" });
            Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains("cap on blueprints", (string)r.Body["detail"]);
            Assert.Equal(0, Place.Calls);
        }

        [Fact]
        public void WhatTexts()
        {
            Assert.Equal("build a wood generator", PlacePure.WhatText(new PlaceArgs { Op = "generator", What = "wood" }));
            Assert.Equal("build an ship beam".Replace("an ship", "a ship"), PlacePure.WhatText(new PlaceArgs { Op = "ship", What = "beam" }));
            Assert.Equal("furnish room 12 with a bed", PlacePure.WhatText(new PlaceArgs { Op = "furnish", What = "bed", At = "room 12" }));
            Assert.Equal("run power to building 77", PlacePure.WhatText(new PlaceArgs { Op = "connect", At = "#77" }));
            Assert.Equal("put a cooler in the freezer", PlacePure.WhatText(new PlaceArgs { Op = "cooler", At = "freezer" }));
        }

        // ------------------------------------------------------------ ConduitRoute
        static Func<int, int, int> Open(params int[][] special)
        {
            var d = special.ToDictionary(s => BaseGrid.Key(s[0], s[1]), s => s[2]);
            return (x, z) => { int c; return d.TryGetValue(BaseGrid.Key(x, z), out c) ? c : (x < -5 || x > 20 || z < -5 || z > 20 ? -1 : 1); };
        }
        static ISet<long> Goal(int x, int z) { return new HashSet<long> { BaseGrid.Key(x, z) }; }
        static IList<int[]> Start(int x, int z) { return new List<int[]> { new[] { x, z } }; }

        [Fact]
        public void ShortestRoute()
        {
            var r = ConduitRoute.Find(Start(0, 0), Goal(4, 0), Open(), 20);
            Assert.NotNull(r);
            Assert.Equal(5, r.NewCells);
            Assert.Equal(new[] { 0, 0 }, r.Cells[0]);
            Assert.Equal(new[] { 4, 0 }, r.Cells[4]);
            for (int i = 1; i < r.Cells.Count; i++) Assert.Equal(1, Math.Abs(r.Cells[i][0] - r.Cells[i - 1][0]) + Math.Abs(r.Cells[i][1] - r.Cells[i - 1][1]));
        }

        [Fact]
        public void IsDeterministic()
        {
            var a = ConduitRoute.Find(Start(0, 0), Goal(3, 3), Open(), 20);
            var b = ConduitRoute.Find(Start(0, 0), Goal(3, 3), Open(), 20);
            Assert.Equal(a.Cells.Select(c => c[0] + "," + c[1]), b.Cells.Select(c => c[0] + "," + c[1]));
            Assert.Equal(7, a.NewCells);
        }

        [Fact]
        public void AvoidsADoorwayWhenTheDetourIsCheap()
        {
            // doorway at (2,0) costs 6; going round through z=1 costs 2 extra cells
            var r = ConduitRoute.Find(Start(0, 0), Goal(4, 0), Open(new[] { 2, 0, 6 }), 20);
            Assert.DoesNotContain(r.Cells, c => c[0] == 2 && c[1] == 0);
            Assert.Equal(7, r.NewCells);
        }

        [Fact]
        public void TakesTheDoorwayWhenThereIsNoWayAround()
        {
            // a wall at x=2 from z=-5..20, one doorway at (2,0)
            var special = new List<int[]>();
            for (int z = -5; z <= 20; z++) special.Add(new[] { 2, z, z == 0 ? 6 : -1 });
            var r = ConduitRoute.Find(Start(0, 0), Goal(4, 0), Open(special.ToArray()), 20);
            Assert.NotNull(r);
            Assert.Contains(r.Cells, c => c[0] == 2 && c[1] == 0);
        }

        [Fact]
        public void ReusesExistingConduitForFree()
        {
            // conduit already at (1..3, 0); goal (4,0)
            var r = ConduitRoute.Find(Start(0, 0), Goal(4, 0), Open(new[] { 1, 0, 0 }, new[] { 2, 0, 0 }, new[] { 3, 0, 0 }), 20);
            Assert.Equal(2, r.NewCells);
            Assert.Equal(new[] { 0, 0 }, r.Cells[0]);
            Assert.Equal(new[] { 4, 0 }, r.Cells[1]);
        }

        [Fact]
        public void AGoalOnExistingConduitIsNotListed()
        {
            var r = ConduitRoute.Find(Start(0, 0), Goal(2, 0), Open(new[] { 2, 0, 0 }), 20);
            Assert.Equal(2, r.NewCells);
        }

        [Fact]
        public void TooFarIsNull()
        {
            Assert.Null(ConduitRoute.Find(Start(0, 0), Goal(10, 0), Open(), 5));
            Assert.NotNull(ConduitRoute.Find(Start(0, 0), Goal(10, 0), Open(), 11));
        }

        [Fact]
        public void UnreachableIsNull()
        {
            var ring = new List<int[]>();
            foreach (var d in new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } }) ring.Add(new[] { d[0], d[1], -1 });
            Assert.Null(ConduitRoute.Find(Start(0, 0), Goal(8, 8), Open(ring.ToArray()), 50));
        }

        [Fact]
        public void AlreadyAtTheGoalOrAdjacent()
        {
            var r = ConduitRoute.Find(Start(3, 3), Goal(3, 3), Open(), 5);
            Assert.Empty(r.Cells); Assert.Equal(0, r.NewCells);
            r = ConduitRoute.Find(Start(3, 3), Goal(4, 3), Open(), 5);
            Assert.Equal(2, r.NewCells);
            r = ConduitRoute.Find(Start(3, 3), Goal(4, 3), Open(new[] { 4, 3, 0 }), 5);
            Assert.Equal(1, r.NewCells);
        }

        [Fact]
        public void PicksTheNearestOfSeveralStartsAndGoals()
        {
            var starts = new List<int[]> { new[] { 0, 0 }, new[] { 9, 9 } };
            var goals = new HashSet<long> { BaseGrid.Key(10, 9), BaseGrid.Key(-9, -9) };
            var r = ConduitRoute.Find(starts, goals, Open(), 30);
            Assert.Equal(2, r.NewCells);
        }

        // ------------------------------------------------------------ words
        [Fact]
        public void PowerSummary()
        {
            Assert.Equal("power: 1200 W made, 800 W used, 3 batteries holding 900 Wh", PowerWords.Summary(1200, 800, 900, 3));
            Assert.Equal("power: no power made yet, 0 W used, no batteries", PowerWords.Summary(0, 0, 0, 0));
            Assert.Contains("1 battery holding", PowerWords.Summary(100, 50, 400.4, 1));
        }

        [Fact]
        public void ParseAtForms()
        {
            Assert.Equal(Tuple.Create("id", "123"), PlaceWords.ParseAt("#123"));
            Assert.Equal(Tuple.Create("room", "12"), PlaceWords.ParseAt("room 12"));
            Assert.Equal(Tuple.Create("room", "12"), PlaceWords.ParseAt(" Room #12 "));
            Assert.Equal(Tuple.Create("label", "the lab"), PlaceWords.ParseAt("the lab"));
            Assert.Equal(Tuple.Create("label", "room service"), PlaceWords.ParseAt("room service"));
            Assert.Equal(Tuple.Create("label", "#abc"), PlaceWords.ParseAt("#abc"));
        }

        [Fact]
        public void NoRoomMessage()
        {
            Assert.Equal("no room called lab; rooms: kitchen, freezer", PlaceWords.NoRoomMessage("lab", new[] { "kitchen", "freezer" }));
            Assert.Equal("no room called lab; there are no rooms yet", PlaceWords.NoRoomMessage("lab", new string[0]));
        }

        [Fact]
        public void DefNamesAndShip()
        {
            foreach (var k in PlacePure.GeneratorKinds) Assert.NotNull(PlaceWords.GeneratorDef(k));
            Assert.Equal("WindTurbine", PlaceWords.GeneratorDef("wind"));
            Assert.Null(PlaceWords.GeneratorDef("coal"));
            foreach (var p in PlacePure.ShipParts) { Assert.StartsWith("Ship_", ShipLayout.DefName(p)); Assert.Contains("research", ShipLayout.Hint(p)); Assert.Contains(p, ShipLayout.Order); }
            Assert.Equal(PlacePure.ShipParts.Length, ShipLayout.Order.Length);
            Assert.Equal("Ship_CryptosleepCasket", ShipLayout.DefName("casket"));
            Assert.Null(ShipLayout.DefName("wing"));
        }
    }
}

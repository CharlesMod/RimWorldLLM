using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    sealed class FloorFakeHands : FakeHands, IFloorHands
    {
        public FloorArgs Last;
        public BuildBudget LeftSeen;
        public HandResult FloorNext = HandResult.Done("laid wood floor in the dining room (12 cells)");
        public HandResult Floor(FloorArgs a, BuildBudget left) { Calls.Add("floor"); Last = a; LeftSeen = left; return FloorNext; }
    }

    public class VivariumFloorsTests
    {
        static FloorInfo F(string def, string label, string research, params (string, string, int)[] cost)
        {
            var f = new FloorInfo { Def = def, Label = label, Research = research };
            foreach (var c in cost) f.Cost.Add(new FloorCost(c.Item1, c.Item2, c.Item3));
            return f;
        }

        static readonly FloorInfo[] Floors =
        {
            F("StrawMatting", "straw matting", null, ("Hay", "hay", 4)),
            F("WoodPlankFloor", "wood floor", null, ("WoodLog", "wood", 3)),
            F("Concrete", "concrete", null, ("Steel", "steel", 1)),
            F("MetalTile", "steel tile", null, ("Steel", "steel", 3)),
            F("SterileTile", "sterile tile", "SterileMaterials", ("Steel", "steel", 3), ("Silver", "silver", 12)),
            F("TileGranite", "granite tile", null, ("BlocksGranite", "granite blocks", 4)),
            F("FlagstoneGranite", "granite flagstone", null, ("BlocksGranite", "granite blocks", 1)),
            F("CarpetRed", "carpet (red)", null, ("Cloth", "cloth", 7)),
            F("CarpetBlue", "carpet (blue)", null, ("Cloth", "cloth", 7)),
        };

        [Theory]
        [InlineData("wood floor", "WoodPlankFloor")]
        [InlineData("Wooden Floor", "WoodPlankFloor")]
        [InlineData("  wood  ", "WoodPlankFloor")]
        [InlineData("straw", "StrawMatting")]
        [InlineData("metal tile", "MetalTile")]
        [InlineData("concrete floor", "Concrete")]
        [InlineData("red carpet", "CarpetRed")]
        [InlineData("granite flagstone", "FlagstoneGranite")]
        [InlineData("flagstone", "FlagstoneGranite")]
        public void WordsResolve(string word, string def)
        {
            FloorInfo f; string r;
            Assert.True(FloorsPure.Resolve(word, Floors, out f, out r), r);
            Assert.Equal(def, f.Def);
        }

        [Fact]
        public void WordsFromLabels()
        {
            Assert.Equal("red carpet", FloorsPure.Word("CarpetRed", "carpet (red)"));
            Assert.Equal("wood floor", FloorsPure.Word("WoodPlankFloor", "Wood  Floor"));
            Assert.Equal("Concrete".ToLowerInvariant(), FloorsPure.Word("Concrete", null));
            Assert.True(FloorsPure.IsSmooth("Smooth Stone")); Assert.True(FloorsPure.IsSmooth("smooth")); Assert.False(FloorsPure.IsSmooth("wood floor"));
        }

        [Fact]
        public void Refusals()
        {
            FloorInfo f; string r;
            Assert.False(FloorsPure.Resolve("sterile tile", Floors, out f, out r)); Assert.Equal("sterile tile needs research SterileMaterials first", r);
            Assert.False(FloorsPure.Resolve("lava", Floors, out f, out r));
            Assert.Equal("no floor called 'lava'; try: straw matting, wood floor, concrete, steel tile, granite tile, granite flagstone, red carpet, blue carpet, smooth stone", r);
            Assert.False(FloorsPure.Resolve("granite", Floors, out f, out r)); Assert.Equal("'granite' fits several floors: granite tile, granite flagstone", r);
            Assert.False(FloorsPure.Resolve("carpet", Floors, out f, out r)); Assert.Contains("red carpet, blue carpet", r);
            Assert.Equal("only 5 blueprints left this cycle; try a smaller room or wait", FloorsPure.Budget(12, 5));
            Assert.Null(FloorsPure.Budget(5, 5));
            Assert.Equal("only 3 designations left this cycle; try a smaller room or wait", FloorsPure.Budget(4, 3, "designations"));
            var have = new Dictionary<string, int> { ["Steel"] = 10 };
            System.Func<string, int> stock = d => have.TryGetValue(d, out int n) ? n : 0;
            Assert.Equal("not enough steel in stock: 4 cells need 12, there is 10", FloorsPure.Stock(Floors[3], 4, stock));
            Assert.Null(FloorsPure.Stock(Floors[3], 3, stock));
            Assert.Equal("not enough cloth in stock: 1 cell needs 7, there is 0", FloorsPure.Stock(Floors[7], 1, stock));
        }

        [Fact]
        public void DigestWordsAreResearchedStockedFirstAndFewCarpets()
        {
            var have = new Dictionary<string, int> { ["WoodLog"] = 50, ["Steel"] = 5, ["Cloth"] = 100 };
            System.Func<string, int> stock = d => have.TryGetValue(d, out int n) ? n : 0;
            var w = FloorsPure.DigestWords(Floors, stock, 8);
            Assert.DoesNotContain("sterile tile", w);
            Assert.Equal("wood floor", w[0]);   // stocked first, in game order: straw (no hay) goes after
            Assert.Equal(new[] { "wood floor", "concrete", "steel tile", "red carpet", "blue carpet", "straw matting", "granite tile", "granite flagstone" }, w);
            Assert.Equal(2, FloorsPure.DigestWords(Floors, stock, 2).Count);
        }

        [Fact]
        public void Notes()
        {
            Assert.Equal("laid wood floor in the dining room (12 cells)", FloorsPure.Note("wood floor", FloorsPure.RoomName("Dining room"), 12, 0));
            Assert.Equal("laid concrete in Ana's bedroom (1 cell, 2 skipped)", FloorsPure.Note("concrete", FloorsPure.RoomName("Ana's bedroom"), 1, 2));
            Assert.Equal("marked the kitchen's rough stone floor for smoothing (9 cells)", FloorsPure.SmoothNote("the kitchen", 9, 0));
            Assert.Equal("the kitchen already has concrete everywhere", FloorsPure.AlreadyNote("concrete", "the kitchen"));
        }

        [Fact]
        public void FloorMarksCostBlueprints()
        {
            var m = new NarratorMark { Kind = "floor", Map = 1, Def = "WoodPlankFloor" };
            m.AddCell(1, 2); m.AddCell(3, 4); m.AddCell(5, 6);
            Assert.Equal(3, BuildBudget.CostBlueprints(new[] { m }));
            Assert.Equal(0, BuildBudget.CostCells(new[] { m }));
            Assert.Equal("floor", NarratorMark.FromJson(m.ToJson()).Kind);
        }

        // ---------------------------------------------------------------- the route
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FloorFakeHands Hands = new FloorFakeHands();
        IVivariumHands Current;
        int op;

        public VivariumFloorsTests() { Current = Hands; VivariumRoutes.Register(H.Router, () => V, () => Current); }

        ApiResult Post(object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "fop" + (++op);
            return H.Call("POST", "/act/floor", j.ToString());
        }

        [Fact]
        public void FloorParsesAndPostsGreentext()
        {
            var r = Post(new { room = 12, kind = "wood floor", why = "since the dining room is dirt" });
            Assert.True(r.Ok, r.Error + " " + r.Body);
            Assert.Equal(12, Hands.Last.Room); Assert.Equal("wood floor", Hands.Last.Kind);
            Assert.Equal(50, Hands.LeftSeen.Blueprints);
            Assert.Equal("laid wood floor in the dining room (12 cells)", (string)r.Body["done"]);
            Assert.Contains("since the dining room is dirt", V.Chat.Since(0).ToString());
        }

        [Fact]
        public void FloorShapeErrors()
        {
            Assert.Equal("bad_request", Post(new { kind = "wood floor" }).Error);
            Assert.Equal("bad_request", Post(new { room = 0, kind = "wood floor" }).Error);
            Assert.Equal("bad_request", Post(new { room = 12 }).Error);
            Assert.DoesNotContain("floor", Hands.Calls);
        }

        [Fact]
        public void FloorRefusalAndMissingHands()
        {
            Hands.FloorNext = HandResult.No("sterile tile needs research SterileMaterials first", "lay a floor");
            var r = Post(new { room = 12, kind = "sterile tile" });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains("couldn't lay a floor: sterile tile needs research", V.Chat.Since(0).ToString());
            Current = new FakeHands();
            r = Post(new { room = 12, kind = "wood floor" });
            Assert.False(r.Ok); Assert.Contains("floor hands are not available", (string)r.Body["detail"]);
        }

        [Fact]
        public void FloorMarksChargeBlueprintsAndUndoLedger()
        {
            var m = new NarratorMark { Kind = "floor", Map = 1, Def = "WoodPlankFloor" };
            for (int i = 0; i < 12; i++) m.AddCell(i, 0);
            Hands.FloorNext = HandResult.Placed("laid wood floor in the dining room (12 cells)", new List<NarratorMark> { m });
            var r = Post(new { room = 12, kind = "wood floor" });
            Assert.True(r.Ok);
            Assert.Equal(1, V.Ledger.Count); Assert.NotNull(r.Body["undo_seq"]);
            Assert.Equal(38, V.Budget.Left().Blueprints);
            Assert.Equal(200, V.Budget.Left().Cells);
        }
    }
}

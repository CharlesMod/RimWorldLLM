using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>A fake hands object that takes the area route's word form and keeps the numeric one working.</summary>
    sealed class AreaFakeHands : FakeHands, IAreaHands
    {
        public AreaWordArgs Last;
        public BuildBudget LeftSeen;
        public HandResult WordsNext = HandResult.Done("added the kitchen to the pen area");
        public HandResult AreaWords(AreaWordArgs a, BuildBudget left) { Calls.Add("areawords"); Last = a; LeftSeen = left; return WordsNext; }
    }

    public class VivariumAreasTests
    {
        // ---------------------------------------------------------------- AreasPure
        [Theory]
        [InlineData("room 12", "room", 12)]
        [InlineData("Room 3", "room", 3)]
        [InlineData("12", "room", 12)]
        [InlineData("#4", "zone", 4)]
        [InlineData("zone 4", "zone", 4)]
        [InlineData("zone #7", "zone", 7)]
        [InlineData(" base ", "base", 0)]
        public void WhereWordsParse(string s, string kind, int id)
        {
            AreaWhere w; string r;
            Assert.True(AreasPure.ParseWhere(s, out w, out r), r);
            Assert.Equal(kind, w.Kind); Assert.Equal(id, w.Id);
        }

        [Theory]
        [InlineData("")]
        [InlineData("room")]
        [InlineData("#")]
        [InlineData("room -1")]
        [InlineData("room 0")]
        [InlineData("the barn")]
        [InlineData("12 5")]
        public void WhereWordsRefuse(string s)
        {
            AreaWhere w; string r;
            Assert.False(AreasPure.ParseWhere(s, out w, out r));
            Assert.Contains("where must be", r);
        }

        [Fact]
        public void RoomBoxIsPaddedByOne()
        {
            var b = AreasPure.BoxFromRoom(10, 20, 14, 23, 250, 250);
            Assert.Equal(9, b.X); Assert.Equal(19, b.Z); Assert.Equal(7, b.W); Assert.Equal(6, b.H);
        }

        [Fact]
        public void BoxIsClampedToTheMap()
        {
            var b = AreasPure.BoxFromRoom(0, 0, 3, 3, 250, 250);
            Assert.Equal(0, b.X); Assert.Equal(0, b.Z); Assert.Equal(5, b.W); Assert.Equal(5, b.H);
            b = AreasPure.BoxFromRoom(246, 246, 249, 249, 250, 250);
            Assert.Equal(245, b.X); Assert.Equal(5, b.W); Assert.Equal(249, b.MaxX);
            Assert.Null(AreasPure.Fit(300, 300, 310, 310, 250, 250));
            Assert.Null(AreasPure.Fit(-10, -10, -2, -2, 250, 250));
        }

        [Fact]
        public void BoxIsCappedAt60Centred()
        {
            var b = AreasPure.Fit(0, 0, 199, 99, 250, 250);
            Assert.Equal(60, b.W); Assert.Equal(60, b.H);
            Assert.Equal(70, b.X); Assert.Equal(20, b.Z);
            Assert.True(b.Area <= 3600);
        }

        [Fact]
        public void CellsBoxIsTheBoundingBoxPadded()
        {
            var cells = new List<int[]> { new[] { 50, 50 }, new[] { 52, 55 }, new[] { 51, 60 } };
            var b = AreasPure.BoxFromCells(cells, 0, 250, 250);
            Assert.Equal(50, b.X); Assert.Equal(50, b.Z); Assert.Equal(3, b.W); Assert.Equal(11, b.H);
            b = AreasPure.BoxFromCells(cells, AreasPure.BasePad, 250, 250);
            Assert.Equal(44, b.X); Assert.Equal(44, b.Z); Assert.Equal(15, b.W); Assert.Equal(23, b.H);
            Assert.Null(AreasPure.BoxFromCells(new List<int[]>(), 1, 250, 250));
        }

        [Fact]
        public void BoxAroundAPoint()
        {
            var b = AreasPure.BoxAround(100, 100, 6, 250, 250);
            Assert.Equal(94, b.X); Assert.Equal(13, b.W);
        }

        [Fact]
        public void NamesSplitAndResolve()
        {
            Assert.Equal(new[] { "Biscuit", "Rex", "Ana" }, AreasPure.SplitNames("Biscuit, Rex and Ana, biscuit"));
            var pool = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Ana", 1), new KeyValuePair<string, int>("Biscuit", 77) };
            List<int> ids; List<string> names; string r;
            Assert.True(AreasPure.ResolveWho(new[] { "biscuit", "ANA" }, pool, out ids, out names, out r));
            Assert.Equal(new[] { 77, 1 }, ids); Assert.Equal(new[] { "Biscuit", "Ana" }, names);
            Assert.False(AreasPure.ResolveWho(new[] { "Rex" }, pool, out ids, out names, out r));
            Assert.Equal("no pawn named Rex; they are: Ana, Biscuit", r);
        }

        [Fact]
        public void RestrictMarkRoundTrips()
        {
            var e = AreasPure.EncodeRestrict(new[] { new KeyValuePair<int, string>(5, "pen"), new KeyValuePair<int, string>(9, "") });
            Assert.Equal("5:pen|9:", e);
            var d = AreasPure.DecodeRestrict(e);
            Assert.Equal(2, d.Count); Assert.Equal(5, d[0].Key); Assert.Equal("pen", d[0].Value); Assert.Equal("", d[1].Value);
            var m = new NarratorMark { Kind = AreasPure.RestrictKind, Map = 1, Def = "pen", Extra = e };
            var back = NarratorMark.FromJson(m.ToJson());
            Assert.Equal(e, back.Extra);
            Assert.Equal(0, BuildBudget.CostCells(new[] { m }));
        }

        [Fact]
        public void Notes()
        {
            Assert.Equal("added the kitchen to the pen area", AreasPure.AddNote("the kitchen", "pen", 40));
            Assert.Equal("the pen area already covers the kitchen", AreasPure.AddNote("the kitchen", "pen", 0));
            Assert.Equal("took zone #4 out of the pen area", AreasPure.RemoveNote("zone #4", "pen"));
            Assert.Equal("restricted Biscuit, Rex to the pen area", AreasPure.RestrictNote(new[] { "Biscuit", "Rex" }, "pen"));
            Assert.Equal("let Biscuit roam free", AreasPure.ReleaseNote(new[] { "Biscuit" }));
            Assert.Contains("name a room or a zone", AreasPure.CapHint("that would change 900 cells but only 200 are left this cycle", new AreaWhere { Kind = AreaWhere.Base }));
            Assert.DoesNotContain("name a room", AreasPure.CapHint("that would change 900 cells but only 200 are left this cycle", new AreaWhere { Kind = AreaWhere.Room, Id = 1 }));
        }

        // ---------------------------------------------------------------- /act/area
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly AreaFakeHands Hands = new AreaFakeHands();
        IVivariumHands Current;
        int op;

        public VivariumAreasTests() { Current = Hands; VivariumRoutes.Register(H.Router, () => V, () => Current); }

        ApiResult Post(string verb, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "aop" + (++op);
            return H.Call("POST", "/act/" + verb, j.ToString());
        }

        [Fact]
        public void WordWhereReachesTheWordHands()
        {
            var r = Post("area", new { op = "add", name = "pen", where = "room 12", who = new[] { "Biscuit", "Rex" }, why = "since the horses wander" });
            Assert.True(r.Ok, r.Error + " " + r.Body);
            Assert.Equal(new[] { "areawords" }, Hands.Calls.ToArray());
            Assert.Equal("add", Hands.Last.Op); Assert.Equal("pen", Hands.Last.Name); Assert.False(Hands.Last.Home);
            Assert.Equal("room", Hands.Last.Where.Kind); Assert.Equal(12, Hands.Last.Where.Id);
            Assert.Equal(new[] { "Biscuit", "Rex" }, Hands.Last.Who.ToArray());
            Assert.Equal(200, Hands.LeftSeen.Cells);
            Assert.Equal("added the kitchen to the pen area", (string)r.Body["done"]);
            Assert.Contains("since the horses wander", V.Chat.Since(0).ToString());
        }

        [Fact]
        public void ZoneBaseAndHomeWords()
        {
            Assert.True(Post("area", new { op = "add", name = "pen", where = "#4" }).Ok);
            Assert.Equal("zone", Hands.Last.Where.Kind); Assert.Equal(4, Hands.Last.Where.Id);
            Assert.True(Post("area", new { op = "remove", name = "Home", where = "base" }).Ok);
            Assert.Equal("base", Hands.Last.Where.Kind); Assert.True(Hands.Last.Home);
        }

        [Fact]
        public void AssignAndRelease()
        {
            Assert.True(Post("area", new { op = "assign", name = "pen", who = "Biscuit, Rex" }).Ok);
            Assert.Equal("assign", Hands.Last.Op); Assert.Null(Hands.Last.Where); Assert.Equal(2, Hands.Last.Who.Count); Assert.False(Hands.Last.Release);
            Assert.True(Post("area", new { op = "assign", name = "none", who = new[] { "Biscuit" } }).Ok);
            Assert.True(Hands.Last.Release);
        }

        [Fact]
        public void WordShapeErrors()
        {
            Assert.Equal("bad_request", Post("area", new { op = "add", name = "pen", where = "the barn" }).Error);
            Assert.Equal("bad_request", Post("area", new { op = "add", name = "pen", who = "Rex" }).Error);          // add needs where
            Assert.Equal("bad_request", Post("area", new { op = "assign", name = "pen" }).Error);                    // assign needs who
            Assert.Equal("bad_request", Post("area", new { op = "assign", name = "home", who = "Rex" }).Error);
            Assert.Equal("bad_request", Post("area", new { op = "assign", name = "pen", where = "room 1", who = "Rex" }).Error);
            Assert.Equal("bad_request", Post("area", new { op = "remove", name = "none", where = "room 1" }).Error);
            Assert.Equal("bad_request", Post("area", new { op = "add", name = "pen", where = "room 1", who = new string[13].Select((_, i) => "p" + i).ToArray() }).Error);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void WordRefusalAndMissingHands()
        {
            Hands.WordsNext = HandResult.No("no pawn named Rex; they are: Ana", "restrict Rex");
            var r = Post("area", new { op = "assign", name = "pen", who = "Rex" });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Contains("couldn't restrict Rex: no pawn named Rex", V.Chat.Since(0).ToString());
            Current = new FakeHands();
            r = Post("area", new { op = "add", name = "pen", where = "base" });
            Assert.False(r.Ok); Assert.Contains("area hands are not available", (string)r.Body["detail"]);
        }

        [Fact]
        public void WordMarksUndoThroughTheLedger()
        {
            var m = new NarratorMark { Kind = AreasPure.RestrictKind, Map = 1, Def = "pen", Extra = "7:" };
            Hands.WordsNext = HandResult.Placed("restricted Biscuit to the pen area", new List<NarratorMark> { m });
            var r = Post("area", new { op = "assign", name = "pen", who = "Biscuit" });
            Assert.True(r.Ok);
            Assert.Equal(1, V.Ledger.Count);
            Assert.Equal(200, V.Budget.Left().Cells);
        }

        [Fact]
        public void NumericRouteStillWorks()
        {
            var r = Post("area", new { kind = "allowed", op = "add", name = "pen", x = 10, z = 10, w = 4, h = 4, assign = new[] { 5 } });
            Assert.True(r.Ok, r.Error + " " + r.Body);
            Assert.Equal(new[] { "area" }, Hands.Calls.ToArray());
            Assert.Equal(new[] { 5 }, ((AreaArgs)Hands.LastArgs).Assign.ToArray());
        }

        // ---------------------------------------------------------------- /act/undo
        [Fact]
        public void UndoTakesBackThroughTheLedgerWithoutNewMarks()
        {
            var mk = new NarratorMark { Kind = "blueprint", Map = 1, Def = "Wall" }; mk.AddCell(1, 1);
            V.Ledger.Add("build", "built a wall", 1, new List<NarratorMark> { mk });
            V.Ledger.Add("build", "built a door", 2, new List<NarratorMark> { mk });
            var r = Post("undo", new { n = 2, why = "since I misplaced them" });
            Assert.True(r.Ok, r.Error + " " + r.Body);
            Assert.Equal("undid 2 narrator actions: cancelled it", (string)r.Body["done"]);
            Assert.Equal(0, V.Ledger.Count);
            Assert.Null(r.Body["undo_seq"]);
            Assert.Equal(0, (int)r.Body["undoable"]);
            Assert.Equal(2, ((JArray)r.Body["undone"]).Count);
            Assert.Contains("undo 2", Hands.Calls);
            Assert.Equal(1, ((JArray)V.Actions.Since(0)["actions"]).Count(a => (string)a["verb"] == "undo"));
            Assert.Contains("since I misplaced them", V.Chat.Since(0).ToString());
        }

        [Fact]
        public void UndoDefaultsToOneAndCapsAtFive()
        {
            var mk = new NarratorMark { Kind = "blueprint", Map = 1, Def = "Wall" }; mk.AddCell(1, 1);
            for (int i = 0; i < 3; i++) V.Ledger.Add("build", "built " + i, i, new List<NarratorMark> { mk });
            Assert.True(Post("undo", new { }).Ok);
            Assert.Equal(2, V.Ledger.Count);
            Assert.Equal("bad_request", Post("undo", new { n = 6 }).Error);
            Assert.Equal("bad_request", Post("undo", new { n = 0 }).Error);
            Assert.Equal(2, V.Ledger.Count);
        }

        [Fact]
        public void UndoWithNothingToUndoRefuses()
        {
            var r = Post("undo", new { });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Contains("nothing that can be undone", (string)r.Body["detail"]);
        }

        [Fact]
        public void UndoRefusedByTheGameKeepsTheLedger()
        {
            var mk = new NarratorMark { Kind = "blueprint", Map = 1, Def = "Wall" }; mk.AddCell(1, 1);
            V.Ledger.Add("build", "built a wall", 1, new List<NarratorMark> { mk });
            Hands.UndoNext = HandResult.No("the game is busy");
            var r = Post("undo", new { });
            Assert.False(r.Ok);
            Assert.Equal(1, V.Ledger.Count);
        }
    }
}

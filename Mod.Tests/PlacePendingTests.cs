using System.Collections.Generic;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class PlacePendingTests
    {
        static PendingTarget P(int x, int z, string def, int deg, int tick) { return new PendingTarget { Map = 1, X = x, Z = z, Def = def, Degrees = deg, Tick = tick }; }

        [Fact]
        public void MergeReplacesTheSameCellAndDef()
        {
            var l = new List<PendingTarget>();
            PlacePending.Merge(l, P(3, 4, "Cooler", -10, 100));
            PlacePending.Merge(l, P(5, 4, "Cooler", 3, 100));
            PlacePending.Merge(l, P(3, 4, "Cooler", 15, 200));
            Assert.Equal(2, l.Count);
            Assert.Equal(15, l[1].Degrees);
            Assert.Equal(5, l[0].X);
        }

        [Fact]
        public void MergeCapsTheList()
        {
            var l = new List<PendingTarget>();
            for (int i = 0; i < PlacePending.MaxEntries + 5; i++) PlacePending.Merge(l, P(i, 0, "Heater", 21, 0));
            Assert.Equal(PlacePending.MaxEntries, l.Count);
            Assert.Equal(5, l[0].X);
        }

        [Fact]
        public void DecideAppliesDropsAndKeeps()
        {
            var e = P(1, 1, "Heater", 21, 1000);
            Assert.Equal(PendingVerdict.Apply, PlacePending.Decide(e, 5000, true, false));
            Assert.Equal(PendingVerdict.Keep, PlacePending.Decide(e, 5000, false, true));
            Assert.Equal(PendingVerdict.Drop, PlacePending.Decide(e, 5000, false, false));
            Assert.Equal(PendingVerdict.Drop, PlacePending.Decide(e, 1000 + PlacePending.MaxAge + 1, false, true));
            Assert.Equal(PendingVerdict.Apply, PlacePending.Decide(e, 1000 + PlacePending.MaxAge + 1, true, false));
        }

        [Fact]
        public void DueEvery250Ticks()
        {
            Assert.True(PlacePending.Due(0));
            Assert.True(PlacePending.Due(500));
            Assert.False(PlacePending.Due(501));
        }

        [Fact]
        public void DefaultDegrees()
        {
            Assert.Equal(-10, PlacePending.DefaultDegrees("cooler", null, "freezer", "storeroom"));
            Assert.Equal(-10, PlacePending.DefaultDegrees("cooler", null, "storage", "freezer"));
            Assert.Null(PlacePending.DefaultDegrees("cooler", null, "kitchen", "kitchen"));
            Assert.Null(PlacePending.DefaultDegrees("heater", null, "freezer", "freezer"));
            Assert.Equal(25, PlacePending.DefaultDegrees("heater", 25, "lab", "research"));
            Assert.Equal(3, PlacePending.DefaultDegrees("cooler", 3, "freezer", "freezer"));
        }

        [Fact]
        public void Words()
        {
            Assert.Equal("target -10 C will be set once it is built", PlacePending.PendingNote(-10));
            Assert.Equal("set 2 heater/cooler in the freezer to -10 C", PlacePending.TargetNote(2, 0, "freezer", -10));
            Assert.Equal("set 1 heater/cooler in the lab to 21 C and queued 2 more to 21 C once built", PlacePending.TargetNote(1, 2, "lab", 21));
            Assert.Equal("no heater or cooler in the lab; place one with place cooler first", PlacePending.NoClimateMessage("lab"));
            Assert.Equal("the lab has no free spot for a 3x1 research bench; build a larger room or remove something", PlacePending.NoSpotMessage("lab", 3, 1, "research bench", null));
            Assert.StartsWith("the lab has no free spot", PlacePending.NoSpotMessage("lab", 3, 1, "research bench", "it overlaps"));
            Assert.True(PlacePending.IsNoSpot("no free spot for a X in that room: y"));
            Assert.False(PlacePending.IsNoSpot("can't put a X"));
            Assert.Equal("y", PlacePending.Detail("no free spot for a X: y"));
            Assert.Equal("Ana's bedroom", PlacePending.The("Ana's bedroom"));
            Assert.Equal("the route needs 12 conduits, only 5 blueprints left this cycle; ask again next cycle", PlacePending.OverCapMessage(12, 5));
            Assert.Equal("1 conduit", PlacePending.Conduits(1));
        }

        [Fact]
        public void MatchRoomOrder()
        {
            var labels = new[] { "Ana's bedroom", "storeroom", "research" };
            var roles = new[] { "bedroom", "freezer", "research" };
            Assert.Equal(1, PlacePending.MatchRoom(labels, roles, "the freezer"));
            Assert.Equal(2, PlacePending.MatchRoom(labels, roles, "research"));
            Assert.Equal(0, PlacePending.MatchRoom(labels, roles, "ana"));
            Assert.Equal(-1, PlacePending.MatchRoom(labels, roles, "kitchen"));
            Assert.Equal(-1, PlacePending.MatchRoom(labels, roles, ""));
        }

        [Fact]
        public void ShipEdges()
        {
            var a = new ShipRect(0, 0, 2, 6);
            Assert.Equal(4, ShipPlan.SharedEdge(a, new ShipRect(2, 0, 3, 4)));
            Assert.Equal(0, ShipPlan.SharedEdge(a, new ShipRect(2, 6, 2, 2)));   // corner only
            Assert.Equal(0, ShipPlan.SharedEdge(a, new ShipRect(1, 1, 2, 2)));   // overlap
            Assert.Equal(2, ShipPlan.SharedEdge(a, new ShipRect(0, 6, 2, 2)));
        }

        [Fact]
        public void ShipScoreEngineGoesFarSide()
        {
            var ex = new List<KeyValuePair<string, ShipRect>>
            {
                new KeyValuePair<string, ShipRect>("beam", new ShipRect(10, 10, 2, 6)),
                new KeyValuePair<string, ShipRect>("core", new ShipRect(12, 10, 2, 2)),
            };
            var near = new ShipRect(7, 10, 3, 4);          // touches the beam on the west, close to the group centre
            var far = new ShipRect(10, 16, 3, 4);          // sits on the beam's end, farther from the centre
            Assert.NotNull(ShipPlan.Score("engine", near, ex));
            Assert.Null(ShipPlan.Score("engine", new ShipRect(30, 30, 3, 4), ex));
            Assert.True(ShipPlan.Score("engine", far, ex) > ShipPlan.Score("engine", near, ex));
            var casketBeam = new ShipRect(9, 12, 1, 2);
            var casketCore = new ShipRect(13, 12, 1, 2);
            Assert.True(ShipPlan.Score("casket", casketBeam, ex) > ShipPlan.Score("casket", casketCore, ex));
        }

        [Fact]
        public void PartOfDef()
        {
            Assert.Equal("beam", ShipPlan.PartOfDef("Ship_Beam"));
            Assert.Equal("casket", ShipPlan.PartOfDef("Ship_CryptosleepCasket"));
            Assert.Null(ShipPlan.PartOfDef("Wall"));
        }
    }
}

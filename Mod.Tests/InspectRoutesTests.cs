using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class InspectRoutesTests
    {
        sealed class FakeInspect : IInspectHands
        {
            public string LastTarget, LastWhat, LastNear, LastItem, LastSurvey;
            public string Refuse; public int Calls;
            JObject R(string t) { Calls++; return Refuse != null ? new JObject { ["refused"] = Refuse } : new JObject { ["text"] = t, ["total"] = 1 }; }
            public JObject Inspect(string target) { LastTarget = target; return R("inspected " + target); }
            public JObject Find(string what, string near, string item) { LastWhat = what; LastNear = near; LastItem = item; return R("found " + what); }
            public JObject Survey(string what) { LastSurvey = what; return R("survey " + what); }
        }

        readonly Harness H = new Harness();
        readonly FakeInspect Hands = new FakeInspect();
        IInspectHands Cur;

        public InspectRoutesTests() { Cur = Hands; VivariumRoutesInspect.Register(H.Router, () => Cur); }

        static void Err(ApiResult r, string code, string contains)
        {
            Assert.False(r.Ok); Assert.Equal(code, r.Error);
            Assert.Contains(contains, (string)r.Body["detail"]);
        }

        [Fact]
        public void InspectPassesTarget()
        {
            var r = H.Call("GET", "/inspect?target=" + System.Uri.EscapeDataString("the kitchen"));
            Assert.True(r.Ok);
            Assert.Equal("the kitchen", Hands.LastTarget);
            Assert.Equal("inspected the kitchen", (string)r.Body["text"]);
        }

        [Fact]
        public void InspectNeedsTargetAndLimitsLength()
        {
            Err(H.Call("GET", "/inspect"), "bad_request", "target is required");
            Err(H.Call("GET", "/inspect?target=%20%20"), "bad_request", "target is required");
            Err(H.Call("GET", "/inspect?target=" + new string('a', 61)), "bad_request", "60");
            Assert.True(H.Call("GET", "/inspect?target=" + new string('a', 60)).Ok);
        }

        [Fact]
        public void FindParamsAndNormalisedWord()
        {
            var r = H.Call("GET", "/find?what=Bed_Free&near=Mara&item=steel");
            Assert.True(r.Ok);
            Assert.Equal("bed_free", Hands.LastWhat); Assert.Equal("Mara", Hands.LastNear); Assert.Equal("steel", Hands.LastItem);
            Assert.True(H.Call("GET", "/find?what=wounded").Ok);
            Assert.Equal("", Hands.LastNear); Assert.Equal("", Hands.LastItem);
        }

        [Fact]
        public void FindBadWhatListsValidWords()
        {
            var r = H.Call("GET", "/find?what=caravans");
            Err(r, "bad_request", "what must be one of: item, wounded");
            Assert.Contains("door_out", (string)r.Body["detail"]);
            Err(H.Call("GET", "/find"), "bad_request", "what must be one of");
            Assert.Equal(0, Hands.Calls);
        }

        [Fact]
        public void FindLengthLimits()
        {
            Err(H.Call("GET", "/find?what=item&near=" + new string('a', 41)), "bad_request", "near");
            Err(H.Call("GET", "/find?what=item&item=" + new string('a', 41)), "bad_request", "item");
            Assert.True(H.Call("GET", "/find?what=item&item=" + new string('a', 40)).Ok);
        }

        [Fact]
        public void SurveyStockAndThreatsOnly()
        {
            Assert.True(H.Call("GET", "/survey?what=stock").Ok); Assert.Equal("stock", Hands.LastSurvey);
            Assert.True(H.Call("GET", "/survey?what=Threats").Ok); Assert.Equal("threats", Hands.LastSurvey);
            Err(H.Call("GET", "/survey?what=moods"), "bad_request", "stock or threats");
            Err(H.Call("GET", "/survey"), "bad_request", "stock or threats");
        }

        [Fact]
        public void RefusedBecomesRefused409()
        {
            Hands.Refuse = "no pawn named Bob; colonists: A, B";
            foreach (var p in new[] { "/inspect?target=Bob", "/find?what=idle", "/survey?what=stock" })
            {
                var r = H.Call("GET", p);
                Err(r, "refused", "no pawn named Bob"); Assert.Equal(409, r.Status);
            }
        }

        [Fact]
        public void NullHandsOrNullResultIsNoGame()
        {
            Cur = null;
            foreach (var p in new[] { "/inspect?target=Bob", "/find?what=idle", "/survey?what=threats" })
                Err(H.Call("GET", p), "no_game", "no game");
            Cur = new NullHands();
            Err(H.Call("GET", "/inspect?target=Bob"), "no_game", "no game");
        }

        sealed class NullHands : IInspectHands
        {
            public JObject Inspect(string t) { return null; }
            public JObject Find(string w, string n, string i) { return null; }
            public JObject Survey(string w) { return null; }
        }

        [Fact]
        public void NoGameLoadedBlockedByRouter()
        {
            var r = H.Call("GET", "/inspect?target=x", null, new GameState { GameLoaded = false });
            Assert.False(r.Ok); Assert.Equal("no_game", r.Error);
            Assert.Equal(0, Hands.Calls);
        }
    }
}

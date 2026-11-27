using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class PairingApplyTests
    {
        const string Id = "9f2c4e1a0b3d5f6e7a8b9c0d1e2f3a4b";
        const string Tok = "tok-SECRET-abc";

        static PairOutcome Paired(string root = "http://192.168.1.20:47480", string model = "gemma4-rp-maxxed-q3kxl", string token = Tok)
        {
            return new PairOutcome
            {
                Root = root, ModelId = model, EngineName = "den-pc", ModelName = "Gemma 4 26B RP",
                Engine = new PairedEngine { EngineId = Id, Token = token, ClientId = "c1", Name = "den-pc", Address = root },
            };
        }

        [Fact]
        public void OutcomeFillsTheOwnEndpointAndStoresTheTokenByEngineId()
        {
            var f = new EndpointFields { Source = "local", BaseUrl = "", Model = "", ApiKey = "" };
            var st = new PairStore();
            PairingApply.Outcome(f, Paired(), st);
            Assert.Equal("own", f.Source);
            Assert.Equal("http://192.168.1.20:47480/v1", f.BaseUrl);
            Assert.Equal("gemma4-rp-maxxed-q3kxl", f.Model);
            Assert.Equal(Tok, f.ApiKey);
            Assert.Equal(Id, f.EngineId);
            Assert.Equal(Tok, st.Get(Id).Token);
            Assert.Equal("http://192.168.1.20:47480", st.Get(Id).Address);
        }

        [Fact]
        public void OutcomeWithoutAModelKeepsTheModelOfTheSameEngineAndClearsAnother()
        {
            var f = new EndpointFields { Source = "own", Model = "old", EngineId = Id };
            PairingApply.Outcome(f, Paired(model: ""), new PairStore());
            Assert.Equal("old", f.Model);
            var g = new EndpointFields { Source = "own", Model = "gpt-4o", EngineId = "" };
            PairingApply.Outcome(g, Paired(model: ""), new PairStore());
            Assert.Equal("", g.Model);
        }

        [Fact]
        public void AnOpenEngineGetsNoKey()
        {
            var f = new EndpointFields { ApiKey = "stale" };
            var st = new PairStore();
            PairingApply.Outcome(f, Paired(root: "http://127.0.0.1:47480", token: ""), st);
            Assert.Equal("", f.ApiKey);
            Assert.Equal("http://127.0.0.1:47480/v1", f.BaseUrl);
            Assert.False(st.HasToken(Id));
            Assert.False(PairingApply.Typed(f, st));   // the store knows its address
        }

        static (EndpointFields, PairStore) PairedState()
        {
            var f = new EndpointFields();
            var st = new PairStore();
            PairingApply.Outcome(f, Paired(), st);
            return (f, st);
        }

        [Fact]
        public void ReconnectFollowsAMovedEngine()
        {
            var (f, st) = PairedState();
            var r = new ReconnectResult { Found = true, Moved = true, Root = "http://192.168.1.33:47480", Auth = EngineAuth.Paired, ModelId = "gemma4-e4b" };
            Assert.True(PairingApply.Reconnect(f, r, st));
            Assert.Equal("http://192.168.1.33:47480/v1", f.BaseUrl);
            Assert.Equal("http://192.168.1.33:47480", st.Get(Id).Address);
            Assert.Equal("gemma4-e4b", f.Model);
            Assert.Equal(Tok, f.ApiKey);
        }

        [Theory]
        [InlineData(EngineAuth.TokenRevoked)]
        [InlineData(EngineAuth.TokenInvalid)]
        public void ReconnectDropsATokenTheEngineNoLongerAccepts(EngineAuth a)
        {
            var (f, st) = PairedState();
            Assert.True(PairingApply.Reconnect(f, new ReconnectResult { Found = true, Auth = a, Root = "http://192.168.1.20:47480" }, st));
            Assert.Equal("", f.ApiKey);
            Assert.False(st.HasToken(Id));
            Assert.Equal(Id, f.EngineId);          // still that engine: "Pair again" offers it
        }

        [Fact]
        public void ReconnectThatFoundNothingChangesNothing()
        {
            var (f, st) = PairedState();
            Assert.False(PairingApply.Reconnect(f, new ReconnectResult { Found = false }, st));
            Assert.Equal(Tok, f.ApiKey);
        }

        [Fact]
        public void A401CodeDropsThePairedToken_ButNeverATypedKey()
        {
            var (f, st) = PairedState();
            Assert.True(PairingApply.Auth(f, EngineAuth.TokenRevoked, st));
            Assert.Equal("", f.ApiKey);
            Assert.False(st.HasToken(Id));

            var typed = new EndpointFields { Source = "own", BaseUrl = "https://api.example.com/v1", ApiKey = "sk-typed", EngineId = "" };
            Assert.False(PairingApply.Auth(typed, EngineAuth.TokenRevoked, st));
            Assert.Equal("sk-typed", typed.ApiKey);
            var (f2, st2) = PairedState();
            Assert.False(PairingApply.Auth(f2, EngineAuth.TokenRequired, st2));
            Assert.Equal(Tok, f2.ApiKey);
        }

        [Fact]
        public void TypingAnotherUrlLetsGoOfThePairedEngine()
        {
            var (f, st) = PairedState();
            Assert.False(PairingApply.Typed(f, st));
            f.BaseUrl = "http://192.168.1.20:47480/v1/";
            Assert.False(PairingApply.Typed(f, st));
            f.BaseUrl = "http://localhost:11434/v1";
            Assert.True(PairingApply.Typed(f, st));
            Assert.Equal("", f.EngineId);
            Assert.True(st.HasToken(Id));          // the token stays stored for later
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class EndpointPairingTests
    {
        const string Id = "9f2c4e1a0b3d5f6e7a8b9c0d1e2f3a4b";
        const string Master = "MASTER-secret-123";

        [Theory]
        [InlineData("http://sprinter:47480/v1", "http://sprinter:47480")]
        [InlineData("http://sprinter:47480/v1/", "http://sprinter:47480")]
        [InlineData("http://sprinter:47480", "http://sprinter:47480")]
        [InlineData("http://sprinter:47480/", "http://sprinter:47480")]
        [InlineData("sprinter:47480/v1", "http://sprinter:47480")]
        [InlineData("http://sprinter/v1", "http://sprinter:47480")]
        [InlineData("sprinter", "http://sprinter:47480")]
        [InlineData("http://10.0.0.5:9000/V1//", "http://10.0.0.5:9000")]
        [InlineData("", null)]
        [InlineData("   ", null)]
        public void RootFromBaseUrl(string url, string root)
        {
            Assert.Equal(root, EndpointPairing.RootOf(url));
        }

        static EngineHello Hello(string engine = "easy-engine", bool tokenRequired = true, bool? paired = null, bool revoked = false)
        {
            return new EngineHello { Name = "den-pc", Engine = engine, Api = 1, EngineId = Id, TokenRequired = tokenRequired, Paired = paired, Revoked = revoked };
        }

        [Fact]
        public void DecisionAdoptVersusPair()
        {
            Assert.Equal(EndpointPairKind.Adopt, EndpointPairing.Decide(Hello(paired: true), true, false));
            Assert.Equal(EndpointPairKind.Adopt, EndpointPairing.Decide(Hello(tokenRequired: false), false, false));
            Assert.Equal(EndpointPairKind.Adopt, EndpointPairing.Decide(Hello(paired: false), true, true));   // master: status 200 is proof
            Assert.Equal(EndpointPairKind.Pair, EndpointPairing.Decide(Hello(paired: false), true, false));
            Assert.Equal(EndpointPairKind.Pair, EndpointPairing.Decide(Hello(paired: false, revoked: true), true, false));
            Assert.Equal(EndpointPairKind.Pair, EndpointPairing.Decide(Hello(), false, false));
        }

        [Fact]
        public void NotAnEngineChangesNothing()
        {
            Assert.Equal(EndpointPairKind.NotAnEngine, EndpointPairing.Decide(null, true, true));
            Assert.Equal(EndpointPairKind.NotAnEngine, EndpointPairing.Decide(Hello(engine: "llama.cpp"), true, true));
        }

        [Fact]
        public void OfferedOnlyForAnUnpairedEndpoint()
        {
            var st = new PairStore();
            Assert.False(EndpointPairing.ShouldOffer("", "", st));
            Assert.True(EndpointPairing.ShouldOffer("http://sprinter:47480/v1", "", st));
            Assert.True(EndpointPairing.ShouldOffer("http://sprinter:47480/v1", Id, st));   // id but no token
            st.Put(new PairedEngine { EngineId = Id, Token = "t", Name = "x", Address = "http://sprinter:47480" });
            Assert.False(EndpointPairing.ShouldOffer("http://sprinter:47480/v1", Id, st));
        }

        static Func<PReq, Task<PReply>> Engine(bool helloShowsPaired, bool statusOk, bool isEngine = true)
        {
            return r =>
            {
                if (r.Method == "GET" && r.Path == "/engine/v1/hello")
                {
                    if (!isEngine) return Task.FromResult(PReply.Json(404, "{}"));
                    bool good = r.Auth == "Bearer " + Master;
                    string extra = good && helloShowsPaired ? ",\"paired\":true,\"client_id\":\"master\"" : (r.Auth != "" ? ",\"paired\":false" : "");
                    return Task.FromResult(PReply.Json(200, "{\"name\":\"den-pc\",\"engine\":\"easy-engine\",\"api\":1,\"engine_id\":\"" + Id + "\",\"pairing\":true,\"token_required\":true" + extra + "}"));
                }
                if (r.Method == "GET" && r.Path == "/engine/v1/status")
                    return Task.FromResult(statusOk && r.Auth == "Bearer " + Master ? PReply.Json(200, "{\"phase\":\"ready\"}") : PReply.Json(401, "{\"code\":\"token_invalid\"}"));
                return Task.FromResult(PReply.Json(404, "{}"));
            };
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]    // hello says paired:false for the master token; status proves it
        public async Task MasterKeyIsAdoptedWithoutAPairingRound(bool helloShowsPaired, bool statusOk)
        {
            using (var f = new FakePairEngine(Engine(helloShowsPaired, statusOk)))
            {
                var p = await EndpointPairing.ProbeAsync(new PairingApi(), f.Root + "/v1", Master);
                Assert.Equal(EndpointPairKind.Adopt, p.Kind);
                Assert.Equal(f.Root, p.Root);
                Assert.Equal(0, f.Count("POST", "/engine/v1/pair"));
                var fields = new EndpointFields { Source = "own", BaseUrl = f.Root + "/v1", Model = "m", ApiKey = Master };
                var st = new PairStore();
                EndpointPairing.Adopt(fields, p, st);
                Assert.Equal(Id, fields.EngineId);
                Assert.Equal(Master, fields.ApiKey);
                Assert.Equal(f.Root + "/v1", fields.BaseUrl);
                Assert.Equal("m", fields.Model);
                Assert.Equal(Master, st.Get(Id).Token);
                Assert.False(EndpointPairing.ShouldOffer(fields.BaseUrl, fields.EngineId, st));
                Assert.DoesNotContain(Master, p.ToString());
                Assert.DoesNotContain(Master, st.Get(Id).ToString());
            }
        }

        [Fact]
        public async Task WrongKeyStartsPairing()
        {
            using (var f = new FakePairEngine(Engine(true, true)))
            {
                var p = await EndpointPairing.ProbeAsync(new PairingApi(), f.Root + "/v1", "some-other-key");
                Assert.Equal(EndpointPairKind.Pair, p.Kind);
                var fields = new EndpointFields { Source = "own", ApiKey = "some-other-key" };
                EndpointPairing.Adopt(fields, p, new PairStore());   // not an Adopt result: changes nothing
                Assert.Equal("", fields.EngineId);
                var none = await EndpointPairing.ProbeAsync(new PairingApi(), f.Root + "/v1", "");
                Assert.Equal(EndpointPairKind.Pair, none.Kind);
            }
        }

        [Fact]
        public async Task NotAnEngineAndUnreachableChangeNothing()
        {
            using (var f = new FakePairEngine(Engine(true, true, isEngine: false)))
            {
                var p = await EndpointPairing.ProbeAsync(new PairingApi(), f.Root + "/v1", Master);
                Assert.Equal(EndpointPairKind.NotAnEngine, p.Kind);
                var fields = new EndpointFields { Source = "own", BaseUrl = f.Root + "/v1", ApiKey = Master };
                var st = new PairStore();
                EndpointPairing.Adopt(fields, p, st);
                Assert.Equal("", fields.EngineId);
                Assert.Empty(st.Items);
            }
            var dead = await EndpointPairing.ProbeAsync(new PairingApi { HelloTimeoutMs = 1000 }, "http://127.0.0.1:" + FakePairEngine.FreePort() + "/v1", Master);
            Assert.Equal(EndpointPairKind.NotAnEngine, dead.Kind);
        }

        [Fact]
        public async Task TheKeyIsNotInAnyLogLine()
        {
            using (var f = new FakePairEngine(Engine(false, true)))
            {
                var p = await EndpointPairing.ProbeAsync(new PairingApi(), f.Root + "/v1", Master);
                var fields = new EndpointFields { Source = "own", ApiKey = Master };
                var st = new PairStore();
                EndpointPairing.Adopt(fields, p, st);
                var text = new List<string> { p.ToString(), p.Hello.ToString() ?? "", st.Get(Id).ToString() };
                foreach (var t in text) Assert.DoesNotContain(Master, t);
                // the Pair path goes through PairingSession, whose log lines are covered by EnginePairingTests.NoSecrets
            }
        }
    }
}

// test_sampler.py (the toolloop-backend cases are in ToolLoopBackendTests.cs)
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class SamplerTests
    {
        public static readonly string[] Fields = { "temperature", "top_k", "top_p", "min_p", "repeat_penalty", "dry_multiplier" };

        public static HttpClient Server(bool engine, List<Seen> seen)
            => Mock.Client(s =>
            {
                if (s.Method == "GET")
                {
                    if (engine && s.Path == "/engine/v1/status")
                        return MockResponse.Json(200, new JObject { ["phase"] = "ready", ["sampler"] = new JObject { ["policy"] = "enforce" } });
                    return MockResponse.Json(404, new JObject());
                }
                lock (seen) seen.Add(s);
                if (s.Path.EndsWith("/completion")) return MockResponse.Json(200, new JObject { ["content"] = "ok" });
                return MockResponse.Json(200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = "ok" } }) });
            });

        private static readonly LlmMessage[] U = { ("user", "u") };

        [Fact]
        public void ConfigDefaultsAreNone()
        {
            var c = new LlmConfig();
            Assert.Null(c.Temperature); Assert.Null(c.TopK); Assert.Null(c.TopP); Assert.Null(c.MinP); Assert.Null(c.RepeatPenalty); Assert.Null(c.Engine);
            Assert.False(c.LineDry);
        }

        [Theory]
        [InlineData("line", "rp")] [InlineData("diary", "rp")] [InlineData("narrator", "rp")] [InlineData("chronicle", "rp")]
        [InlineData("voice_card", "rp")] [InlineData("intent", "structured")] [InlineData("reflect", "structured")]
        public async Task EngineGetsHeaderAndNoSamplerFields(string kind, string want)
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Model = "google/gemma-4-E4B-it" }, client: Server(true, seen));
            await llm.CompleteAsync(U, 1, kind, temperature: 0.7);
            Assert.Equal(want, seen[0].Headers["x-easy-sampler"]);
            var body = seen[0].Json();
            Assert.DoesNotContain(Fields, k => body.ContainsKey(k));   // not even the per-call temperature: the engine decides
        }

        [Fact]
        public void SchemaOrGrammarMeansStructured()
        {
            Assert.Equal("structured", LlmWire.SamplerProfile("whatever", grammar: "root ::= x"));
            Assert.Equal("structured", LlmWire.SamplerProfile("whatever", jsonSchema: new JObject { ["type"] = "object" }));
            Assert.Equal("rp", LlmWire.SamplerProfile("line"));
        }

        [Fact]
        public async Task NativeModeAlsoSendsHeader()
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Mode = "native" }, client: Server(true, seen));
            await llm.CompleteAsync(U, 0, "intent", 4, new string[0], "root ::= \"a\"");
            Assert.Equal("/completion", seen[0].Path);
            Assert.Equal("structured", seen[0].Headers["x-easy-sampler"]);
            Assert.False(seen[0].Json().ContainsKey("temperature"));
        }

        [Fact]
        public async Task ForcedConfigValuesStillSent()
        {
            var seen = new List<Seen>();
            var cfg = new LlmConfig { BaseUrl = "http://h", Temperature = 0.5, MinP = 0.1 };
            await new HttpLlm(cfg, client: Server(true, seen)).CompleteAsync(U, 1);
            var body = seen[0].Json();
            Assert.Equal(0.5, (double)body["temperature"]); Assert.Equal(0.1, (double)body["min_p"]); Assert.False(body.ContainsKey("top_k"));
        }

        [Theory]
        [InlineData("google/gemma-4-E4B-it")] [InlineData("gemma4:e2b")] [InlineData("unsloth/gemma-4-26B-A4B-it-GGUF")] [InlineData("gemma4-rp-maxxed-Q3_K_XL")]
        public async Task NonEngineKnownGemmaGetsOfficialValues(string model)
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Model = model }, client: Server(false, seen));
            await llm.CompleteAsync(U, 1, "line", temperature: 0.9);
            var body = seen[0].Json();
            Assert.False(seen[0].Headers.ContainsKey("x-easy-sampler"));
            Assert.Equal((1.0, 64L, 0.95), ((double)body["temperature"], (long)body["top_k"], (double)body["top_p"]));
            Assert.False(body.ContainsKey("min_p")); Assert.False(body.ContainsKey("dry_multiplier"));
        }

        [Theory]
        [InlineData("")] [InlineData("gpt-4o")] [InlineData("llama-3.1-8b")] [InlineData("gemma-3-27b")] [InlineData("gemma-2-9b")]
        public async Task NonEngineUnknownModelGetsNothing(string model)
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Model = model }, client: Server(false, seen));
            await llm.CompleteAsync(U, 1, "line", temperature: 0.9);
            Assert.False(seen[0].Headers.ContainsKey("x-easy-sampler"));
            Assert.DoesNotContain(Fields, k => seen[0].Json().ContainsKey(k));
        }

        [Fact]
        public async Task DetectionIsCachedAndConfigOverridesIt()
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h" }, client: Server(true, seen));
            await llm.CompleteAsync(U, 1);
            await llm.CompleteAsync(U, 1);
            Assert.True(llm.Engine);
            Assert.Equal(2, seen.Count);
            var seen2 = new List<Seen>();
            llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false, Model = "gemma-4-E4B-it" }, client: Server(true, seen2));
            await llm.CompleteAsync(U, 1);
            Assert.False(seen2[0].Headers.ContainsKey("x-easy-sampler"));
            Assert.Equal(64L, (long)seen2[0].Json()["top_k"]);
        }

        [Fact]
        public async Task UnreachableProbeIsNotCached()
        {
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h" }, client: Mock.Client(s => throw Mock.ConnectError()));
            await Assert.ThrowsAsync<LlmError>(() => llm.CompleteAsync(U, 1));
            Assert.Null(llm.Engine);
        }

        [Fact]
        public void IsKnownGemma4() => Assert.True(LlmWire.IsKnownGemma4("google/gemma-4-E4B-it") && !LlmWire.IsKnownGemma4("gemma-3-27b"));

        [Fact]
        public async Task NamedSamplerSendsItsProfile()
        {
            var seen = new List<Seen>();
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h" }, client: Server(true, seen));
            Assert.Null(SamplerRoute.Route(llm, ""));
            var named = (HttpLlm)SamplerRoute.Route(llm, "aim");
            Assert.Same(named, SamplerRoute.Route(named, "aim"));
            Assert.NotSame(named, SamplerRoute.Route(named, "other"));
            await named.CompleteAsync(U, 1, "intent");
            Assert.Equal("aim", seen[0].Headers["x-easy-sampler"]);
            Assert.True(llm.Engine);            // one detection, shared
            Assert.Null(SamplerRoute.Route(new Fakes.NullClient(), "aim"));
        }
    }
}

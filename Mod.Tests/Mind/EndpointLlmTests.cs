// test_endpoint_llm.py, test_yielded.py (HttpLlm half; the backend half is in ToolLoopBackendTests.cs), test_model_tier.py (non-world tests)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class EndpointLlmTests
    {
        private static Func<Seen, MockResponse> OkServer(List<(string, string)> seen = null, string[] models = null)
        {
            models ??= new[] { "m1" };
            return s =>
            {
                seen?.Add((s.Method, s.Path));
                if (s.Path.EndsWith("/models")) return MockResponse.Json(200, new JObject { ["data"] = new JArray(models.Select(m => new JObject { ["id"] = m })) });
                return MockResponse.Json(200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = "Hi there" } }) });
            };
        }

        [Fact]
        public void NormaliseBase()
        {
            Assert.Equal("http://localhost:8080/v1", LlmWire.NormaliseBase("localhost:8080"));
            Assert.Equal("http://h:1/v1", LlmWire.NormaliseBase("http://h:1/v1/"));
            Assert.Equal("http://h:1/v1", LlmWire.NormaliseBase("http://h:1/v1/chat/completions"));
            Assert.Equal("http://h:1/v1", LlmWire.NormaliseBase("http://h:1/"));
            Assert.Equal("http://HTTP://x/v1", LlmWire.NormaliseBase("HTTP://x"));     // Python's case-sensitive scheme test, kept
            Assert.Equal("http://h/v1", LlmWire.NormaliseBase("  http://h/models "));
        }

        [Fact]
        public async Task EndpointGreenAndTimed()
        {
            var seen = new List<(string, string)>();
            var r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h:1" }, Mock.Client(OkServer(seen)));
            Assert.True(r.Ok); Assert.NotNull(r.CompletionMs); Assert.Equal(new[] { "m1" }, r.Models);
            Assert.Contains(("GET", "/v1/models"), seen); Assert.Contains(("POST", "/v1/chat/completions"), seen);
            Assert.StartsWith("ok: 1 model(s), 8-token completion in ", r.Cause);
        }

        [Fact]
        public async Task Endpoint404RetriesOnceWithoutV1()
        {
            var seen = new List<string>();
            var r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h:1/v1" }, Mock.Client(s =>
            {
                seen.Add(s.Path);
                return s.Path.StartsWith("/v1") ? MockResponse.Json(404, new JObject()) : OkServer()(s);
            }));
            Assert.True(r.Ok); Assert.Equal("/v1/models", seen[0]); Assert.Equal("/models", seen[1]); Assert.Equal("http://h:1", r.BaseUrl);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h:1" }, Mock.Client(s => MockResponse.Json(404, new JObject())));
            Assert.Equal("red", r.Status); Assert.Contains("404", r.Cause);
        }

        [Fact]
        public async Task Endpoint503IsAmberAndCausesOneLine()
        {
            var r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" }, Mock.Client(s => MockResponse.Json(503, new JObject())));
            Assert.Equal("amber", r.Status); Assert.Contains("loading", r.Cause); Assert.DoesNotContain("\n", r.Cause);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" }, Mock.Client(s => MockResponse.Json(401, new JObject())));
            Assert.Equal("red", r.Status); Assert.Contains("API key", r.Cause);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" }, Mock.Client(s => throw Mock.ConnectError("x")));
            Assert.Equal("red", r.Status); Assert.Contains("refused", r.Cause);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" }, Mock.Client(s => s.Path.EndsWith("models") ? OkServer()(s) : MockResponse.Json(503, new JObject())));
            Assert.Equal("amber", r.Status);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h", Model = "nope" }, Mock.Client(OkServer()));
            Assert.Equal("red", r.Status); Assert.Contains("nope", r.Cause);
        }

        [Fact]
        public async Task EndpointOtherCauses()
        {
            var r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" },
                Mock.Client(s => new MockResponse { Status = 400, Body = System.Text.Encoding.UTF8.GetBytes("  bad  thing  ") }));
            Assert.Equal("GET /models: 400, the server rejected the request (bad  thing)", r.Cause);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" }, Mock.Client(s => MockResponse.Empty(500)));
            Assert.Equal("GET /models: HTTP 500", r.Cause);
            r = await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h" },
                Mock.Client(s => s.Path.EndsWith("models") ? OkServer()(s) : MockResponse.Json(200, new JObject { ["nope"] = 1 })));
            Assert.Equal("red", r.Status); Assert.Equal("completion: reply was not an OpenAI chat completion", r.Cause);
            // the completion body (golden captured from httpx): model comes from the first served model
            Seen last = null;
            await EndpointCheck.TestEndpointAsync(new LlmConfig { BaseUrl = "http://h", ApiKey = "k" }, Mock.Client(s => { if (s.Method == "POST") last = s; return OkServer()(s); }));
            Assert.Equal("{\"messages\":[{\"role\":\"user\",\"content\":\"Say hi.\"}],\"max_tokens\":8,\"temperature\":0,\"chat_template_kwargs\":{\"enable_thinking\":false},\"model\":\"m1\"}", last.BodyText);
            Assert.Equal("Bearer k", last.Headers["authorization"]);
        }

        [Fact]
        public async Task CallLogHasCacheStatsAndEffectivePrefill()
        {
            using var t = new TempDir();
            var http = Mock.Client(s =>
            {
                var body = s.Json();
                Assert.Equal(1L, (long)body["id_slot"]); Assert.True((bool)body["cache_prompt"]);
                Assert.False((bool)body["chat_template_kwargs"]["enable_thinking"]);
                return MockResponse.Json(200, new JObject
                {
                    ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = "Eat." } }),
                    ["timings"] = new JObject { ["prompt_n"] = 154, ["cache_n"] = 1721, ["prompt_ms"] = 260.0, ["predicted_n"] = 3, ["predicted_ms"] = 40.0 },
                });
            });
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, new CallLog(t.Combine("calls.jsonl")), http);
            var c = await llm.CompleteAsync(new LlmMessage[] { ("system", "s"), ("user", "u") }, 1, "line", actor: 5);
            Assert.Equal("Eat.", c.Text);
            var row = JObject.Parse(File.ReadAllLines(t.Combine("calls.jsonl"))[0]);
            Assert.Equal(154L, (long)row["prompt_n"]); Assert.Equal(1721L, (long)row["cache_n"]);
            Assert.Equal(260.0, (double)row["prompt_ms"]); Assert.Equal(40.0, (double)row["predicted_ms"]);
            Assert.Equal(154 / 0.26, (double)row["raw_prefill_tps"], 0);
            Assert.Equal(PyRound.Round((154 + 1721) / 0.26, 1), (double)row["effective_prefill_tps"]);
            Assert.Equal("line", (string)row["kind"]); Assert.Equal(1L, (long)row["slot"]); Assert.Equal(5L, (long)row["actor"]);
        }

        [Fact]
        public async Task UsageFallbackAndFailureRecords()
        {
            var log = new CallLog(null);
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, log, Mock.Client(s => MockResponse.Json(200, new JObject
            {
                ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = null } }),
                ["usage"] = new JObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 3 },
            })));
            var c = await llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, null);
            Assert.Equal("", c.Text); Assert.Equal((12L, 3L, 0L), (c.Stats.PromptN, c.Stats.PredictedN, c.Stats.CacheN));
            var bad = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, log, Mock.Client(s => MockResponse.Json(500, new JObject())));
            var e = await Assert.ThrowsAsync<LlmError>(() => bad.CompleteAsync(new LlmMessage[] { ("user", "u") }, null));
            Assert.Equal((500, "HTTP 500"), (e.Status, e.Cause));
            var down = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, log, Mock.Client(s => throw Mock.ConnectError()));
            e = await Assert.ThrowsAsync<LlmError>(() => down.CompleteAsync(new LlmMessage[] { ("user", "u") }, null));
            Assert.Equal("server unreachable (ConnectError)", e.Cause);
            var rows = log.Snapshot();
            Assert.Equal(3, rows.Count);
            Assert.Equal("HTTP 500", (string)rows[1]["error"]); Assert.False((bool)rows[1]["ok"]); Assert.Equal("ConnectError", (string)rows[2]["error"]);
            // a malformed reply raises an uncaught error and writes no call-log row (Python quirk kept)
            var weird = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, log, Mock.Client(s => MockResponse.Json(200, new JObject { ["choices"] = new JArray() })));
            await Assert.ThrowsAsync<RimWorldLLM.Mind.Py.PyIndexError>(() => weird.CompleteAsync(new LlmMessage[] { ("user", "u") }, null));
            Assert.Equal(3, log.Snapshot().Count);
            var notJson = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false }, log, Mock.Client(s => new MockResponse { Body = System.Text.Encoding.UTF8.GetBytes("<html>") }));
            e = await Assert.ThrowsAsync<LlmError>(() => notJson.CompleteAsync(new LlmMessage[] { ("user", "u") }, null));
            Assert.Equal("reply was not JSON", e.Cause);
        }

        [Fact]
        public async Task TimeoutIsReadTimeout()
        {
            var log = new CallLog(null);
            var http = new HttpClient(new MockHandlerSlow()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h", Engine = false, TimeoutS = 0.05 }, log, http);
            var e = await Assert.ThrowsAsync<LlmError>(() => llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, null));
            Assert.Equal("server unreachable (ReadTimeout)", e.Cause);
        }

        private sealed class MockHandlerSlow : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                await Task.Delay(5000, ct);
                return new HttpResponseMessage();
            }
        }

        [Fact]
        public async Task NativeModePayload()
        {
            Seen seen = null;
            var http = Mock.Client(s => { seen = s; return MockResponse.Json(200, new JObject { ["content"] = "Eat.", ["timings"] = new JObject { ["prompt_n"] = 5, ["cache_n"] = 100, ["prompt_ms"] = 50 } }); });
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Mode = "native", Engine = false }, client: http);
            var c = await llm.CompleteAsync(new LlmMessage[] { ("system", "S"), ("user", "U"), ("assistant", "line"), ("user", "label") }, 2, "intent", 4, new string[0], "root ::= \"none\"");
            Assert.Equal("http://h:1/completion", seen.Url);
            var b = seen.Json();
            Assert.Equal(2L, (long)b["id_slot"]); Assert.True(!string.IsNullOrEmpty((string)b["grammar"])); Assert.Equal(4L, (long)b["n_predict"]);
            Assert.Equal("<|turn>system\nS<turn|>\n<|turn>user\nU<turn|>\n<|turn>model\n<|channel>thought\n<channel|>line<turn|>\n<|turn>user\nlabel<turn|>\n<|turn>model\n<|channel>thought\n<channel|>", (string)b["prompt"]);
            Assert.Equal((5L, 100L, 50.0), (c.Stats.PromptN, c.Stats.CacheN, c.Stats.PromptMs));
        }

        [Fact]
        public void GemmaPromptPrefixSharedBetweenLineAndIntentPass()
        {
            var baseM = new LlmMessage[] { ("system", "S"), ("user", "U") };
            string line = LlmWire.GemmaPrompt(baseM);
            string intent = LlmWire.GemmaPrompt(baseM.Concat(new LlmMessage[] { ("assistant", "x"), ("user", "l") }).ToArray());
            string tail = "<|turn>model\n<|channel>thought\n<channel|>";
            Assert.EndsWith(tail, line);
            Assert.StartsWith(line.Substring(0, line.Length - tail.Length), intent);
            Assert.Equal("", LlmWire.GemmaPrompt(new LlmMessage[0]));
            Assert.EndsWith("<turn|>\n", LlmWire.GemmaPrompt(new LlmMessage[] { ("user", "u"), ("assistant", "a") }));
        }

        // ---- test_yielded.py

        private static readonly JObject Yielded = new JObject { ["error"] = new JObject { ["code"] = "yielded", ["type"] = "unavailable_error", ["message"] = "Paused while RimWorld runs" } };
        private static readonly JObject Loading = new JObject { ["error"] = new JObject { ["code"] = 503, ["type"] = "unavailable_error", ["message"] = "Loading model" } };
        private static readonly JObject OkBody = new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = "Hi there" } }) };

        public sealed class YServer
        {
            public int Status = 503; public JObject Body = Yielded; public string Retry = "7"; public int N;
            public MockResponse Handle(Seen s)
            {
                N++;
                if (Status == 200) return MockResponse.Json(200, OkBody);
                return MockResponse.Json(Status, Body, Retry);
            }
        }

        private static HttpLlm Llm(YServer s, List<string> log = null, Func<double> clock = null)
            => new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false }, client: Mock.Client(s.Handle), log: log == null ? null : (lv, n, m) => log.Add(m), clock: clock);

        private static Task<Completion> Call(HttpLlm c) => c.CompleteAsync(new LlmMessage[] { ("user", "hi") }, 0);

        [Fact]
        public async Task YieldedRaisesAwayWithRetryAfter()
        {
            var e = await Assert.ThrowsAsync<LlmAway>(() => Call(Llm(new YServer())));
            Assert.Equal(7.0, e.RetryAfter); Assert.IsAssignableFrom<LlmError>(e); Assert.Contains("RimWorld", e.AwayMessage);
            Assert.Equal((503, "yielded"), (e.Status, e.Cause));
        }

        [Theory]
        [InlineData("600", 30.0)] [InlineData(null, 5.0)] [InlineData("junk", 5.0)] [InlineData("-3", 0.0)] [InlineData(" 2_5 ", 25.0)]
        [InlineData("inf", 30.0)] [InlineData("1e1", 10.0)] [InlineData("", 5.0)]
        public async Task RetryAfterCappedAndDefaulted(string hdr, double want)
        {
            var e = await Assert.ThrowsAsync<LlmAway>(() => Call(Llm(new YServer { Retry = hdr })));
            Assert.Equal(want, e.RetryAfter);
        }

        [Fact]
        public async Task NanRetryAfterRaisesInTheBackoffLikePython()
        {
            // round(nan) raises ValueError inside went_away: no call-log row, the gate never blocks
            var log = new CallLog(null);
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false }, log, Mock.Client(new YServer { Retry = "nan" }.Handle));
            await Assert.ThrowsAsync<RimWorldLLM.Mind.Py.PyValueError>(() => Call(llm));
            Assert.Empty(log.Snapshot());
        }

        [Fact]
        public async Task BackoffSkipsNetworkAndLogsOnce()
        {
            var s = new YServer(); var lines = new List<string>(); var c = Llm(s, lines);
            for (int i = 0; i < 5; i++) { try { await Call(c); } catch (LlmAway) { } }
            Assert.Equal(1, s.N);
            Assert.Single(lines, m => m.Contains("paused"));
            Assert.Equal("model paused on the server: Paused while RimWorld runs; retrying in 7s", lines[0]);
            Assert.Single(c.CallLog.Snapshot());
        }

        [Fact]
        public async Task ComesBackAfterBackoff()
        {
            var s = new YServer(); var lines = new List<string>(); var c = Llm(s, lines);
            try { await Call(c); } catch (LlmAway) { }
            c.Gate.Until = 0.0001;          // the window has passed
            s.Status = 200;
            Assert.Equal("Hi there", (await Call(c)).Text);
            Assert.Contains(lines, m => m.Contains("back"));
        }

        [Fact]
        public async Task GateUsesInjectedClock()
        {
            double now = 100; var s = new YServer { Retry = "10" }; var c = Llm(s, clock: () => now);
            await Assert.ThrowsAsync<LlmAway>(() => Call(c));
            now = 105;
            var e = await Assert.ThrowsAsync<LlmAway>(() => Call(c));
            Assert.Equal(5.0, e.RetryAfter); Assert.Equal(1, s.N);
            now = 111; s.Status = 200;
            Assert.Equal("Hi there", (await Call(c)).Text);
        }

        [Fact]
        public async Task Plain503IsPlainLlmError()
        {
            var s = new YServer { Body = Loading };
            for (int i = 0; i < 2; i++)
            {
                var e = await Assert.ThrowsAsync<LlmError>(() => Call(Llm(s)));
                Assert.IsNotType<LlmAway>(e); Assert.Equal(503, e.Status);
            }
        }

        [Fact]
        public void ParseYieldedShapes()
        {
            Assert.Null(LlmWire.ParseYielded(500, "{}", null));
            Assert.Null(LlmWire.ParseYielded(503, "not json", null));
            Assert.Null(LlmWire.ParseYielded(503, "[]", null));
            Assert.Null(LlmWire.ParseYielded(503, "{\"error\": \"yielded\"}", null));
            Assert.Null(LlmWire.ParseYielded(503, "{\"error\": {\"code\": true}}", null));
            Assert.Equal("paused", LlmWire.ParseYielded(503, "{\"error\": {\"code\": \"yielded\"}}", null).Message);
            Assert.Equal("5", LlmWire.ParseYielded(503, "{\"error\": {\"code\": \"yielded\", \"message\": 5}}", null).Message);
            Assert.Equal("paused", LlmWire.ParseYielded(503, "{\"error\": {\"code\": \"yielded\", \"message\": \"\"}}", null).Message);
        }

        // ---- test_model_tier.py (the world-fixture tests are deferred, see docs/csmind/parity-llm.md)

        private static readonly string[] Pats = { "e2b", "e4b" };

        [Theory]
        [InlineData("gemma4-e4b-rp-q40", "full", "small")] [InlineData("Gemma-4-E2B-it", "", "small")] [InlineData("gemma4-26b-a4b-q4", "full", "large")]
        [InlineData("gemma4-rp-maxxed-q3kxl", "full", "large")] [InlineData("", "", "large")] [InlineData("gemma4-rp-maxxed-q3kxl", "starter", "small")]
        public void Classify(string model, string staging, string want) => Assert.Equal(want, ModelTier.Classify(model, Pats, staging));

        private static (HttpClient http, List<string> seen) Engine(string model, string staging = "full", bool ok = true, string sizeClass = null)
        {
            var seen = new List<string>();
            var http = Mock.Client(s =>
            {
                seen.Add(s.Url);
                if (s.Path.EndsWith("/engine/v1/status"))
                {
                    if (!ok) return MockResponse.Empty(500);
                    var d = new JObject { ["phase"] = "ready", ["sampler"] = new JObject(), ["model"] = model, ["model_tier"] = staging };
                    if (sizeClass != null) d["model_size_class"] = sizeClass;
                    return MockResponse.Json(200, d);
                }
                return MockResponse.Empty(404);
            });
            return (http, seen);
        }

        private static (MindConfig cfg, ModelTier tier, HttpLlm llm, double[] now, List<string> seen) Make(string model, string staging = "full", bool ok = true, string sizeClass = null)
        {
            var cfg = new MindConfig(new MindEnv(new Dictionary<string, string>(), false, "/home/u")) { Llm = new LlmConfig { BaseUrl = "http://h" } };
            var now = new[] { 0.0 };
            var (http, seen) = Engine(model, staging, ok, sizeClass);
            return (cfg, new ModelTier(cfg, () => now[0]), new HttpLlm(cfg.Llm, client: http), now, seen);
        }

        [Fact]
        public async Task EngineReadAndCache()
        {
            var (cfg, t, llm, now, seen) = Make("gemma4-e4b-rp-q40");
            Assert.False(t.Small);                      // unknown until read
            await t.RefreshAsync(llm);
            Assert.True(t.Small); Assert.Equal("gemma4-e4b-rp-q40", t.Model);
            int n = seen.Count;
            await t.RefreshAsync(llm); now[0] = 59;
            await t.RefreshAsync(llm);
            Assert.Equal(n, seen.Count);                // cached for 60 s
            now[0] = 61;
            Assert.True(t.Due());
            await t.RefreshAsync(llm);
            Assert.Equal(n + 1, seen.Count);
        }

        [Fact]
        public async Task EngineLargeStarterAndFailureKeepsLast()
        {
            var (_, t, llm, _, _) = Make("gemma4-rp-maxxed-q3kxl");
            await t.RefreshAsync(llm); Assert.False(t.Small);
            (_, t, llm, _, _) = Make("gemma4-rp-maxxed-q3kxl", "starter");
            await t.RefreshAsync(llm); Assert.True(t.Small);
            (_, t, llm, _, _) = Make("gemma4-e4b", ok: false);
            await t.RefreshAsync(llm); Assert.False(t.Small);     // no answer = large
        }

        [Fact]
        public async Task PlainEndpointUsesCfgModel()
        {
            var cfg = new MindConfig(new MindEnv(new Dictionary<string, string>(), false, "/home/u")) { Llm = new LlmConfig { BaseUrl = "http://h", Model = "gemma-4-E4B-it", Engine = false } };
            var t = new ModelTier(cfg, () => 0.0);
            await t.RefreshAsync(new HttpLlm(cfg.Llm, client: Mock.Client(s => MockResponse.Empty(404))));
            Assert.True(t.Small);
            await new ModelTier(cfg, () => 0.0).RefreshAsync(new Fakes.NullClient());   // a client with no engine probe is a plain endpoint
        }

        [Theory]
        [InlineData("custom:Qwen3-4B", "small", "small")] [InlineData("custom:my-e4b-merge-12b", "large", "large")] [InlineData("custom:my-e4b-merge", "", "small")]
        [InlineData("custom:Qwen3-4B", "", "large")] [InlineData("custom:my-e4b-merge", "weird", "small")]
        public void ClassifySizeClass(string model, string sc, string want)
        {
            Assert.Equal(want, ModelTier.Classify(model, Pats, "full", sc));
            Assert.Equal("small", ModelTier.Classify(model, Pats, "starter", "large"));
        }

        [Theory]
        [InlineData("custom:Qwen3-4B", "small", true)] [InlineData("custom:gemma-e4b-merge-12b", "large", false)] [InlineData("custom:gemma-e4b-merge", null, true)]
        public async Task EngineSizeClassRead(string model, string sc, bool want)
        {
            var (_, t, llm, _, _) = Make(model, sizeClass: sc);
            await t.RefreshAsync(llm);
            Assert.Equal(want, t.Small);
        }

        [Fact]
        public void FeatureOn()
        {
            var (_, t, _, _, _) = Make("x");
            Assert.True(ModelTier.FeatureOn(t, true, false)); Assert.False(ModelTier.FeatureOn(t, false, true));
        }
    }
}

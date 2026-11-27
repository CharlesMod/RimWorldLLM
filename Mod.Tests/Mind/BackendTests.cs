// OpenAIBackend tests from agent/tests/test_sampler.py and test_yielded.py, plus request-byte and reply-shape checks.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class BackendTests
    {
        private static readonly JObject[] U = { new JObject { ["role"] = "user", ["content"] = "u" } };

        [Fact]
        public async Task ToolloopBackendStructuredHeaderAndNoTemperature()
        {
            var seen = new List<Seen>();
            var be = new OpenAIBackend("http://h", model: "google/gemma-4-E4B-it", client: SamplerTests.Server(true, seen));
            var r = await be.ChatAsync(U, temperature: 0.2);
            Assert.Equal("ok", r.Text);
            Assert.Equal("structured", seen[0].Headers["x-easy-sampler"]);
            Assert.False(seen[0].Json().ContainsKey("temperature"));
        }

        [Fact]
        public async Task ToolloopBackendNonEngine()
        {
            var seen = new List<Seen>();
            var be = new OpenAIBackend("http://h", model: "gemma-4-E4B-it", client: SamplerTests.Server(false, seen));
            await be.ChatAsync(U, temperature: 0.2);
            Assert.False(seen[0].Headers.ContainsKey("x-easy-sampler"));
            var b = seen[0].Json();
            Assert.Equal((1.0, 64, 0.95), ((double)b["temperature"], (int)b["top_k"], (double)b["top_p"]));
            seen.Clear();
            be = new OpenAIBackend("http://h", model: "some-model", client: SamplerTests.Server(false, seen));
            await be.ChatAsync(U, temperature: 0.2);
            Assert.False(seen[0].Json().ContainsKey("temperature"));
            seen.Clear();
            be = new OpenAIBackend("http://h", model: "some-model", client: SamplerTests.Server(false, seen), honorTemperature: true);
            await be.ChatAsync(U, temperature: 0.2);
            Assert.Equal(0.2, (double)seen[0].Json()["temperature"]);
        }

        [Fact]
        public async Task RequestBytesMatchPython()
        {
            // captured from httpx 0.28.1: OpenAIBackend("http://h:1/", model="m", api_key="k", engine=False).chat(..., grammar, tools, slot=2)
            var seen = new List<Seen>();
            var be = new OpenAIBackend("http://h:1/", model: "m", apiKey: "k", client: SamplerTests.Server(false, seen), engine: false, extra: new JObject { ["repeat_last_n"] = 64 });
            var tools = new JArray(new JObject { ["type"] = "function" });
            await be.ChatAsync(new[] { new JObject { ["role"] = "user", ["content"] = "héllo" } }, grammar: "root ::= \"a\"", tools: tools, maxTokens: 9, slot: 2);
            Assert.Equal("{\"messages\":[{\"role\":\"user\",\"content\":\"héllo\"}],\"max_tokens\":9,\"cache_prompt\":true,\"chat_template_kwargs\":{\"enable_thinking\":false},\"repeat_last_n\":64,\"model\":\"m\",\"id_slot\":2,\"grammar\":\"root ::= \\\"a\\\"\",\"tools\":[{\"type\":\"function\"}],\"tool_choice\":\"auto\"}", seen[0].BodyText);
            Assert.Equal("Bearer k", seen[0].Headers["Authorization"]);
            Assert.Equal("/v1/chat/completions", seen[0].Path);
        }

        private static HttpClientHolder Replying(object body, int status = 200) => new HttpClientHolder(body, status);
        private sealed class HttpClientHolder
        {
            public System.Net.Http.HttpClient Client;
            public HttpClientHolder(object body, int status) { Client = Mock.Client(s => MockResponse.Json(status, body)); }
        }

        [Fact]
        public async Task ReplyParsing()
        {
            var body = JObject.Parse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"hi\",\"reasoning_content\":\"why\",\"tool_calls\":[{\"id\":\"a\",\"function\":{\"name\":\"say\",\"arguments\":\"{}\"}},{\"function\":{\"name\":\"x\"}}]}}]}");
            var r = await new OpenAIBackend("http://h", client: Replying(body).Client, engine: false).ChatAsync(U);
            Assert.Equal(("hi", "stop", "why"), (r.Text, r.Finish, r.Reasoning));
            Assert.Equal(new[] { "a|say", "call_1|x" }, r.ToolCalls.Select(c => c.Id + "|" + c.Name).ToArray());
            Assert.Equal("{}", (string)r.ToolCalls[0].Arguments); Assert.Null(r.ToolCalls[1].Arguments);
            r = await new OpenAIBackend("http://h", client: Replying(JObject.Parse("{\"choices\":[{\"message\":{\"content\":null}}]}")).Client, engine: false).ChatAsync(U);
            Assert.Equal("", r.Text); Assert.Null(r.Finish);
            r = await new OpenAIBackend("http://h", client: Replying(JObject.Parse("{\"choices\":[{}]}")).Client, engine: false).ChatAsync(U);
            Assert.Equal("", r.Text);
        }

        [Theory]
        [InlineData("{}")] [InlineData("{\"choices\":[]}")] [InlineData("[]")] [InlineData("\"x\"")] [InlineData("{\"choices\":5}")]
        public async Task MalformedReplyIsABackendError(string json)
        {
            var e = await Assert.ThrowsAsync<BackendError>(() => new OpenAIBackend("http://h", client: Replying(JToken.Parse(json)).Client, engine: false).ChatAsync(U));
            Assert.Equal("reply was not a chat completion", e.Message);
        }

        [Fact]
        public async Task NonDictChoiceEscapesAsAnAttributeErrorLikePython()
        {
            // "choices": ["x"] -> "x".get(...) is an AttributeError in Python, not caught by the backend; the loop reports it
            await Assert.ThrowsAsync<PyAttributeError>(() => new OpenAIBackend("http://h", client: Replying(JObject.Parse("{\"choices\":[\"x\"]}")).Client, engine: false).ChatAsync(U));
        }

        [Fact]
        public async Task HttpErrorAndUnreachable()
        {
            var e = await Assert.ThrowsAsync<BackendError>(() => new OpenAIBackend("http://h", client: Replying(new JObject { ["error"] = "bad" }, 500).Client, engine: false).ChatAsync(U));
            Assert.Equal("HTTP 500: {\"error\":\"bad\"}", e.Message);
            var down = Mock.Client(s => throw Mock.ConnectError());
            e = await Assert.ThrowsAsync<BackendError>(() => new OpenAIBackend("http://h", client: down, engine: false).ChatAsync(U));
            Assert.Equal("unreachable (ConnectError)", e.Message);
        }

        // ---- test_yielded
        private static readonly JObject Yielded = new JObject { ["error"] = new JObject { ["code"] = "yielded", ["type"] = "unavailable_error", ["message"] = "Paused while RimWorld runs" } };
        private static readonly JObject Loading = new JObject { ["error"] = new JObject { ["code"] = 503, ["type"] = "unavailable_error", ["message"] = "Loading model" } };

        [Fact]
        public async Task BackendYieldedAwayAndBackoff()
        {
            int n = 0;
            var lines = new List<string>();
            var be = new OpenAIBackend("http://h:1", engine: false, client: Mock.Client(s => { n++; return MockResponse.Json(503, Yielded, "7"); }), log: (lv, nm, m) => lines.Add(m));
            for (int i = 0; i < 3; i++)
            {
                var e = await Assert.ThrowsAsync<BackendAway>(() => be.ChatAsync(new[] { new JObject { ["role"] = "user", ["content"] = "x" } }));
                Assert.True(e.RetryAfter > 6 && e.RetryAfter <= 7); Assert.IsAssignableFrom<BackendError>(e);
            }
            Assert.Equal(1, n);
            Assert.Single(lines, m => m.Contains("paused"));
        }

        [Fact]
        public async Task BackendPlain503StaysBackendError()
        {
            var be = new OpenAIBackend("http://h:1", engine: false, client: Mock.Client(s => MockResponse.Json(503, Loading, "7")));
            var e = await Assert.ThrowsAsync<BackendError>(() => be.ChatAsync(U));
            Assert.IsNotType<BackendAway>(e);
        }
    }
}

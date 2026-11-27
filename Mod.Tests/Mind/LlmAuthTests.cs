// Job B (pairing): a 401 from the mind's own /v1 calls reaches LlmAuth.OnUnauthorized (the mod points it at EngineAuthStatus.Note).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("AdultState")]
    public class LlmAuthTests
    {
        private const string Marker = "token_revoked_llmauthtest";

        private static (List<(int, string)> seen, Action<int, string> prev) Hook()
        {
            var seen = new List<(int, string)>();
            var prev = LlmAuth.OnUnauthorized;
            LlmAuth.OnUnauthorized = (s, b) => { if (b.Contains(Marker)) lock (seen) seen.Add((s, b)); };
            return (seen, prev);
        }

        [Fact]
        public async Task HttpLlmNonStreamed401IsReported()
        {
            var (seen, prev) = Hook();
            try
            {
                var http = Mock.Client(s => s.Method == "GET" ? MockResponse.Json(404, new JObject()) : MockResponse.Json(401, new JObject { ["code"] = Marker }));
                var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false, ApiKey = "k", TimeoutS = 5 }, new CallLog(null), http);
                await Record.ExceptionAsync(() => llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, 1, "line", 8, null, null, null, null, null, null));
                Assert.Single(seen);
                Assert.Equal(401, seen[0].Item1);
            }
            finally { LlmAuth.OnUnauthorized = prev; }
        }

        [Fact]
        public async Task HttpLlmStreamed401IsReported()
        {
            var (seen, prev) = Hook();
            try
            {
                var h = new StreamHandler(s => s.Method == "GET" ? StreamResp.Json(404, "{}") : StreamResp.Json(401, "{\"code\":\"" + Marker + "\"}"));
                var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false, TimeoutS = 5 }, new CallLog(null), new HttpClient(h));
                var opt = new CallOptions { Sink = new StreamSink(e => { }) };
                await Record.ExceptionAsync(() => llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, 1, "line", 8, null, null, null, null, null, opt));
                Assert.Single(seen);
            }
            finally { LlmAuth.OnUnauthorized = prev; }
        }

        [Fact]
        public async Task OpenAIBackend401IsReportedAndOtherStatusesAreNot()
        {
            var (seen, prev) = Hook();
            try
            {
                int status = 401;
                var http = Mock.Client(s => s.Method == "GET" ? MockResponse.Json(404, new JObject()) : MockResponse.Json(status, new JObject { ["code"] = Marker }));
                var be = new OpenAIBackend("http://h", model: "m", apiKey: "k", client: http, engine: false);
                var u = new[] { new JObject { ["role"] = "user", ["content"] = "u" } };
                await Record.ExceptionAsync(() => be.ChatAsync(u));
                Assert.Single(seen);
                status = 500;
                await Record.ExceptionAsync(() => be.ChatAsync(u));
                Assert.Single(seen);
            }
            finally { LlmAuth.OnUnauthorized = prev; }
        }

        [Fact]
        public async Task ACallbackThatThrowsDoesNotBreakTheCall()
        {
            var prev = LlmAuth.OnUnauthorized;
            LlmAuth.OnUnauthorized = (s, b) => throw new InvalidOperationException("x");
            try
            {
                var http = Mock.Client(s => s.Method == "GET" ? MockResponse.Json(404, new JObject()) : MockResponse.Json(401, new JObject()));
                var be = new OpenAIBackend("http://h", model: "m", client: http, engine: false);
                var ex = await Record.ExceptionAsync(() => be.ChatAsync(new[] { new JObject { ["role"] = "user", ["content"] = "u" } }));
                Assert.IsNotType<InvalidOperationException>(ex);
            }
            finally { LlmAuth.OnUnauthorized = prev; }
        }
    }
}

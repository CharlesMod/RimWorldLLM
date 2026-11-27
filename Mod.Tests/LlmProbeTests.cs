using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Authentication;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class LlmProbeTests
    {
        [Theory]
        [InlineData("localhost:4480", "http://localhost:4480")]
        [InlineData("  http://localhost:4480/v1/  ", "http://localhost:4480/v1")]
        [InlineData("http://h:1/v1/chat/completions", "http://h:1/v1")]
        [InlineData("https://api.example.com/v1/models", "https://api.example.com/v1")]
        [InlineData("http://h:1/v1?x=1#f", "http://h:1/v1")]
        [InlineData("http://h:1///", "http://h:1")]
        public void NormalizesTheBase(string input, string expected) { Assert.Equal(expected, LlmProbe.NormalizeBase(input)); }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ftp://h/v1")]
        [InlineData("http://")]
        public void RejectsUnusableUrls(string input) { Assert.Null(LlmProbe.NormalizeBase(input)); }

        [Fact]
        public void TogglesV1() { Assert.Equal("http://h/v1", LlmProbe.ToggleV1("http://h")); Assert.Equal("http://h", LlmProbe.ToggleV1("http://h/v1")); }

        static HttpOutcome Ok(string body, long ms = 100) { return new HttpOutcome { Status = 200, Body = body, Ms = ms }; }
        static HttpOutcome St(int status, string body = "") { return new HttpOutcome { Status = status, Body = body }; }
        const string Models = "{\"data\":[{\"id\":\"gemma\"},{\"id\":\"other\"}]}";
        const string Chat = "{\"choices\":[{\"message\":{\"content\":\"ready\"}}]}";

        [Fact]
        public void ClassifiesModels()
        {
            Assert.Null(LlmProbe.ClassifyModels(Ok(Models), "gemma"));
            Assert.Null(LlmProbe.ClassifyModels(Ok(Models), ""));
            Assert.Equal(ProbeCode.ModelNotFound, LlmProbe.ClassifyModels(Ok(Models), "nope").Code);
            Assert.Equal(ProbeCode.KeyRejected, LlmProbe.ClassifyModels(St(401), "m").Code);
            Assert.Equal(ProbeCode.WrongBase, LlmProbe.ClassifyModels(St(404), "m").Code);
            var l = LlmProbe.ClassifyModels(St(503), "m");
            Assert.Equal(ProbeLight.Amber, l.Light); Assert.Equal(ProbeCode.Loading, l.Code);
            Assert.Equal(ProbeCode.NotJson, LlmProbe.ClassifyModels(Ok("<html>"), "m").Code);
            Assert.Equal(ProbeCode.NotJson, LlmProbe.ClassifyModels(Ok("{\"x\":1}"), "m").Code);
            Assert.Equal(ProbeCode.HttpError, LlmProbe.ClassifyModels(St(500), "m").Code);
        }

        [Fact]
        public void ClassifiesTransportErrors()
        {
            Assert.Equal(Transport.Refused, LlmProbe.ClassifyException(new HttpRequestExceptionLike(new SocketException((int)SocketError.ConnectionRefused))));
            Assert.Equal(Transport.Tls, LlmProbe.ClassifyException(new Exception("outer", new AuthenticationException("bad cert"))));
            Assert.Equal(Transport.Timeout, LlmProbe.ClassifyException(new System.Threading.Tasks.TaskCanceledException()));
            Assert.Equal(Transport.Other, LlmProbe.ClassifyException(new Exception("weird")));
            Assert.Equal(ProbeCode.NothingListening, LlmProbe.ClassifyModels(new HttpOutcome { Error = new SocketException((int)SocketError.ConnectionRefused) }, "m").Code);
        }
        sealed class HttpRequestExceptionLike : Exception { public HttpRequestExceptionLike(Exception inner) : base("send failed", inner) { } }

        [Fact]
        public void ClassifiesChat()
        {
            Assert.Null(LlmProbe.ClassifyChat(Ok(Chat), "m"));
            Assert.Equal(ProbeCode.Slow, LlmProbe.ClassifyChat(Ok(Chat, 9000), "m").Code);
            Assert.Equal(ProbeLight.Amber, LlmProbe.ClassifyChat(Ok(Chat, 9000), "m").Light);
            Assert.Equal(ProbeCode.ReasoningText, LlmProbe.ClassifyChat(Ok("{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"hmm\"}}]}"), "m").Code);
            Assert.Equal(ProbeCode.ReasoningText, LlmProbe.ClassifyChat(Ok("{\"choices\":[{\"message\":{\"content\":\"<think>x\"}}]}"), "m").Code);
            Assert.Equal(ProbeCode.KeyRejected, LlmProbe.ClassifyChat(St(403), "m").Code);
            Assert.Equal(ProbeCode.ModelNotFound, LlmProbe.ClassifyChat(St(404, "{\"error\":{\"message\":\"The model `x` does not exist\"}}"), "x").Code);
            Assert.Equal(ProbeCode.Loading, LlmProbe.ClassifyChat(St(503), "m").Code);
            Assert.Equal(ProbeCode.NotJson, LlmProbe.ClassifyChat(Ok("nope"), "m").Code);
            Assert.Equal(ProbeCode.Timeout, LlmProbe.ClassifyChat(new HttpOutcome { Error = new TimeoutException() }, "m").Code);
        }

        sealed class Fake
        {
            public readonly List<string> Calls = new List<string>();
            public Dictionary<string, HttpOutcome> Replies = new Dictionary<string, HttpOutcome>();
            public HttpOutcome Do(string method, string url, string body, string key, int timeout)
            {
                Calls.Add(method + " " + url);
                HttpOutcome o;
                return Replies.TryGetValue(method + " " + url, out o) ? o : St(404);
            }
        }

        [Fact]
        public void RunGreenPath()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/v1/models"] = Ok(Models);
            f.Replies["POST http://h:1/v1/chat/completions"] = Ok(Chat);
            var r = LlmProbe.Run("h:1/v1", "gemma", "k", f.Do);
            Assert.Equal(ProbeLight.Green, r.Light); Assert.Equal(ProbeCode.Ok, r.Code); Assert.Equal("http://h:1/v1", r.EffectiveBase);
        }

        [Fact]
        public void RunRetriesWithV1AddedOn404()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/v1/models"] = Ok(Models);
            f.Replies["POST http://h:1/v1/chat/completions"] = Ok(Chat);
            var r = LlmProbe.Run("http://h:1", "gemma", "", f.Do);
            Assert.Equal(new[] { "GET http://h:1/models", "GET http://h:1/v1/models", "POST http://h:1/v1/chat/completions" }, f.Calls.ToArray());
            Assert.Equal(ProbeCode.OkFixedBase, r.Code); Assert.Equal("http://h:1/v1", r.Arg);
        }

        [Fact]
        public void RunRetriesWithV1RemovedOn404()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/models"] = Ok(Models);
            f.Replies["POST http://h:1/chat/completions"] = Ok(Chat);
            var r = LlmProbe.Run("http://h:1/v1", "gemma", "", f.Do);
            Assert.Equal(ProbeCode.OkFixedBase, r.Code); Assert.Equal("http://h:1", r.Arg);
        }

        [Fact]
        public void RunWrongBaseSuggestsV1()
        {
            var r = LlmProbe.Run("http://h:1", "m", "", new Fake().Do);
            Assert.Equal(ProbeLight.Red, r.Light); Assert.Equal(ProbeCode.WrongBase, r.Code); Assert.Equal("http://h:1/v1", r.Arg);
        }

        const string YieldedBody = "{\"error\":{\"code\":\"yielded\",\"type\":\"unavailable_error\",\"message\":\"Paused while RimWorld runs\"}}";
        const string LoadingBody = "{\"error\":{\"code\":503,\"message\":\"Loading model\"}}";

        [Fact]
        public void YieldedIs503Paused_PlainIsLoading()
        {
            var y = new HttpOutcome { Status = 503, Body = YieldedBody };
            var l = new HttpOutcome { Status = 503, Body = LoadingBody };
            Assert.Equal(ProbeCode.Paused, LlmProbe.ClassifyModels(y, "m").Code);
            Assert.Equal(ProbeLight.Amber, LlmProbe.ClassifyModels(y, "m").Light);
            Assert.Equal(ProbeCode.Paused, LlmProbe.ClassifyChat(y, "m").Code);
            Assert.Equal(ProbeCode.Loading, LlmProbe.ClassifyModels(l, "m").Code);
            Assert.Equal(ProbeCode.Loading, LlmProbe.ClassifyChat(l, "m").Code);
            Assert.Equal(ProbeCode.Loading, LlmProbe.ClassifyChat(St(503), "m").Code);
        }

        [Fact]
        public void RunYieldedOnModelsAndHealthIsPaused()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/v1/models"] = new HttpOutcome { Status = 503, Body = YieldedBody };
            Assert.Equal(ProbeCode.Paused, LlmProbe.Run("http://h:1/v1", "m", "", f.Do).Code);
            var g = new Fake();
            g.Replies["GET http://h:1/health"] = new HttpOutcome { Status = 503, Body = YieldedBody };
            var r = LlmProbe.Run("http://h:1/v1", "m", "", g.Do);
            Assert.Equal(ProbeLight.Amber, r.Light); Assert.Equal(ProbeCode.Paused, r.Code);
        }

        [Fact]
        public void RunHealth503IsAmberLoading()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/health"] = St(503);
            var r = LlmProbe.Run("http://h:1/v1", "m", "", f.Do);
            Assert.Equal(ProbeLight.Amber, r.Light); Assert.Equal(ProbeCode.Loading, r.Code);
        }

        [Fact]
        public void RunStopsOnRedStepsAndSkipsChat()
        {
            var f = new Fake();
            f.Replies["GET http://h:1/v1/models"] = St(401);
            Assert.Equal(ProbeCode.KeyRejected, LlmProbe.Run("http://h:1/v1", "m", "bad", f.Do).Code);
            Assert.DoesNotContain(f.Calls, c => c.StartsWith("POST"));
            Assert.Equal(ProbeCode.NoUrl, LlmProbe.Run(" ", "m", "", f.Do).Code);
            Assert.Equal(ProbeCode.BadUrl, LlmProbe.Run("ftp://x", "m", "", f.Do).Code);
        }

        [Fact]
        public void ChatBodyAsksForEightTokensWithThinkingOff()
        {
            var j = JObject.Parse(LlmProbe.ChatBody("gemma"));
            Assert.Equal(8, (int)j["max_tokens"]);
            Assert.False((bool)j["chat_template_kwargs"]["enable_thinking"]);
            Assert.Equal("gemma", (string)j["model"]);
        }

        [Fact]
        public void RunAgainstARealLocalServerReportsNothingListening()
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var r = LlmProbe.Run("http://127.0.0.1:" + port + "/v1", "m", "", LlmProbe.HttpSend);
            Assert.Equal(ProbeCode.NothingListening, r.Code);
        }
    }

    public class RequestLogTests
    {
        [Fact]
        public void KeepsTheNewestAndTracksTheAgent()
        {
            var l = new RequestLog(3);
            Assert.Equal(-1, l.SecondsSinceAgent(DateTime.UtcNow.Ticks));
            l.Add("GET", "/health", 200, 1, false);
            Assert.Equal(-1, l.SecondsSinceAgent(DateTime.UtcNow.Ticks));
            for (int i = 0; i < 4; i++) l.Add("GET", "/p" + i, 200, 2, i == 1);
            var r = l.Recent(10);
            Assert.Equal(3, r.Count); Assert.Equal("/p3", r[0].Path);
            Assert.True(l.SecondsSinceAgent(DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 5) >= 5);
        }

        [Fact]
        public void BridgeInfoCarriesTheLlmBlockOnlyWhenGiven()
        {
            Assert.Null(BridgeInfo.Build(1, "b", "t", 2, "v", 3)["llm"]);
            var j = BridgeInfo.Build(1, "b", "t", 2, "v", 3, new JObject { ["base_url"] = "u", ["api_key"] = "k" });
            Assert.Equal("k", (string)j["llm"]["api_key"]);
        }
    }
}

namespace HermesVoice.Tests
{
    public class ServerRequestLogTests
    {
        [Fact]
        public void ServerLogsRequestsAndMarksAuthenticatedOnes()
        {
            using (var h = new Harness())
            {
                Assert.True(h.StartServer("tok", Net.FreeBase()));
                var c = new System.Net.Http.HttpClient();
                var url = "http://127.0.0.1:" + h.Server.Port;
                c.GetAsync(url + "/health").Result.Dispose();
                Assert.Equal(-1, h.Server.Requests.SecondsSinceAgent(DateTime.UtcNow.Ticks));
                c.GetAsync(url + "/ping").Result.Dispose();                       // 401
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url + "/ping");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer tok");
                var t = c.SendAsync(req);
                // answered by the game thread
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!t.IsCompleted && sw.ElapsedMilliseconds < 5000) { h.Server.Pump(); System.Threading.Thread.Sleep(5); }
                Assert.Equal(200, (int)t.Result.StatusCode);
                System.Threading.Thread.Sleep(50);
                var recent = h.Server.Requests.Recent(10);
                Assert.Equal(3, recent.Count);
                Assert.Equal(200, recent[0].Status); Assert.True(recent[0].Authenticated);
                Assert.Equal(401, recent[1].Status); Assert.False(recent[1].Authenticated);
                Assert.True(h.Server.Requests.SecondsSinceAgent(DateTime.UtcNow.Ticks) >= 0);
            }
        }
    }
}

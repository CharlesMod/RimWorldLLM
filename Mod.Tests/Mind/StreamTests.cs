using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>A canned streamed answer: status, headers, body chunks delivered one per read, then end (or hang until cancelled).</summary>
    public sealed class StreamResp
    {
        public int Status = 200;
        public string ContentType = "text/event-stream";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public List<byte[]> Chunks = new List<byte[]>();
        public bool HangAtEnd;
        public static StreamResp Sse(string text, int split = 0)
        {
            var r = new StreamResp();
            byte[] b = new UTF8Encoding(false).GetBytes(text);
            if (split <= 0) r.Chunks.Add(b);
            else for (int i = 0; i < b.Length; i += split) r.Chunks.Add(b.Skip(i).Take(split).ToArray());
            return r;
        }
        public static StreamResp Json(int status, string json, string retryAfter = null, string www = null)
        {
            var r = new StreamResp { Status = status, ContentType = "application/json" };
            r.Chunks.Add(Encoding.UTF8.GetBytes(json));
            if (retryAfter != null) r.Headers["Retry-After"] = retryAfter;
            if (www != null) r.Headers["WWW-Authenticate"] = www;
            return r;
        }
    }

    internal sealed class ChunkStream : Stream
    {
        private readonly Queue<byte[]> _q;
        private readonly bool _hang;
        public ChunkStream(IEnumerable<byte[]> c, bool hang) { _q = new Queue<byte[]>(c); _hang = hang; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_q.Count == 0)
            {
                if (_hang) await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            byte[] c = _q.Dequeue();
            c.CopyTo(buffer);
            return c.Length;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(new Memory<byte>(buffer, offset, count), ct).AsTask();
    }

    internal sealed class StreamHandler : HttpMessageHandler
    {
        private readonly Func<Seen, StreamResp> _h;
        public readonly List<Seen> All = new List<Seen>();
        public StreamHandler(Func<Seen, StreamResp> h) { _h = h; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var s = new Seen { Method = request.Method.Method, Url = request.RequestUri.ToString(), Path = request.RequestUri.AbsolutePath };
            foreach (var h in request.Headers) s.Headers[h.Key] = string.Join(", ", h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) s.Headers[h.Key] = string.Join(", ", h.Value);
                s.Body = await request.Content.ReadAsByteArrayAsync(ct);
            }
            lock (All) All.Add(s);
            StreamResp r = _h(s);
            var resp = new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new StreamContent(new ChunkStream(r.Chunks, r.HangAtEnd)) };
            resp.Content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
            foreach (var kv in r.Headers) resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return resp;
        }
    }

    public class StreamTests
    {
        private static string Chunk(object delta, string finish = null) =>
            "data: " + new JObject { ["choices"] = new JArray(new JObject { ["index"] = 0, ["delta"] = JObject.FromObject(delta), ["finish_reason"] = finish }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";

        private static readonly string Basic =
            ": ping\n\n" +
            Chunk(new { role = "assistant", content = (string)null }) +
            Chunk(new { content = "Hel" }) +
            Chunk(new { content = "lo é " }) +
            Chunk(new { content = "😀" }, null) +
            Chunk(new { }, "stop") +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":4,\"total_tokens\":15}}\n\n" +
            "data: [DONE]\n\n";

        private static (HttpLlm llm, StreamHandler h, List<StreamEvent> ev, CallOptions opt) Make(Func<Seen, StreamResp> f, bool? engine = false, double timeout = 5, string nonStreamAnswer = null)
        {
            var h = new StreamHandler(s =>
            {
                if (s.Method == "GET") return StreamResp.Json(404, "{}");
                return f(s);
            });
            var http = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = engine, TimeoutS = timeout }, new CallLog(null), http);
            var ev = new List<StreamEvent>();
            return (llm, h, ev, new CallOptions { Sink = new StreamSink(e => { lock (ev) ev.Add(e); }) });
        }

        private static Task<Completion> Call(HttpLlm llm, CallOptions o, string kind = "line") =>
            llm.CompleteAsync(new LlmMessage[] { ("system", "S"), ("user", "u") }, 1, kind, 48, null, null, null, null, null, o);

        private static string Kinds(List<StreamEvent> ev) => string.Join(",", ev.Select(e => e.Kind.ToString()));

        [Fact]
        public async Task BasicStreamEventsTextAndStats()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Basic), engine: false);
            var c = await Call(llm, o);
            Assert.Equal("Hello é 😀", c.Text);
            Assert.Equal("Sent,FirstToken,Partial,Partial,Partial,Done", Kinds(ev));
            Assert.Equal(new[] { "Hel", "Hello é ", "Hello é 😀" }, ev.Where(e => e.Kind == StreamEventKind.Partial).Select(e => e.Text).ToArray());
            Assert.Equal("Hello é 😀", ev.Last().Text);
            Assert.Same(c.Stats, ev.Last().Stats);
            Assert.Equal(11, c.Stats.PromptN); Assert.Equal(4, c.Stats.PredictedN);
            Assert.NotNull(c.Stats.TtftS);
            Assert.True(c.Stats.Ok);
        }

        [Fact]
        public async Task BodyIsNonStreamBodyPlusStreamKeysAndHeaders()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Basic), engine: false);
            await Call(llm, o);
            var (llm2, h2, _, _) = Make(s => StreamResp.Json(200, "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}"), engine: false);
            await llm2.CompleteAsync(new LlmMessage[] { ("system", "S"), ("user", "u") }, 1);
            string ns = h2.All.Single().BodyText;
            string st = h.All.Single().BodyText;
            Assert.Equal(ns.Substring(0, ns.Length - 1) + ",\"stream\":true,\"stream_options\":{\"include_usage\":true}}", st);
            Assert.False(JObject.Parse(st).ContainsKey("return_progress"));
            Assert.False(h.All.Single().Headers.ContainsKey("X-Easy-Sampler"));
            Assert.Equal("text/event-stream", h.All.Single().Headers["Accept"]);
        }

        [Fact]
        public async Task EngineGetsReturnProgressLast()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Basic), engine: true);
            await Call(llm, o);
            string st = h.All.Single().BodyText;
            Assert.EndsWith(",\"stream\":true,\"stream_options\":{\"include_usage\":true},\"return_progress\":true}", st);
            Assert.Equal("rp", h.All.Single().Headers["X-Easy-Sampler"]);
        }

        [Fact]
        public async Task RequestIdOnEveryRequestAndInCallLog()
        {
            var h = new StreamHandler(s => s.Path.EndsWith("/chat/completions") && s.BodyText.Contains("\"stream\"") ? StreamResp.Sse(Basic) : StreamResp.Json(200, "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}"));
            var log = new CallLog(null);
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false }, log, new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan });
            var o = new CallOptions { Sink = new StreamSink(e => { }) };
            await Call(llm, o);
            await llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, null);
            var ids = h.All.Select(s => s.Headers["X-Request-Id"]).ToList();
            Assert.Equal(2, ids.Distinct().Count());
            foreach (var id in ids) { Assert.True(id.Length <= 128 && id.All(ch => ch >= 0x20 && ch < 0x7f)); }
            var rows = log.Snapshot();
            Assert.Equal(ids, rows.Select(r => (string)r["request_id"]).ToList());
            Assert.Equal("request_id", rows[0].Properties().Last().Name);        // appended after the old fields
        }

        [Fact]
        public async Task PromptProgressQueuedQuietAndPingsAndTimings()
        {
            string body =
                ": easy waiting\n\n: easy quiet\n\n: llama ping\n\n" +
                "data: {\"prompt_progress\":{\"total\":100,\"cache\":40,\"processed\":50,\"time_ms\":12.5},\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":null}}]}\n\n" +
                "data: {\"prompt_progress\":{\"total\":100,\"cache\":40,\"processed\":100,\"time_ms\":30}}\n\n" +
                Chunk(new { content = "Hi" }) +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":60,\"cache_n\":40,\"prompt_ms\":30,\"predicted_n\":2,\"predicted_ms\":8}}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n\n" +
                "data: [DONE]\n\n";
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(body), engine: true);
            var c = await Call(llm, o);
            Assert.Equal("Sent,Queued,Progress,Progress,FirstToken,Partial,Done", Kinds(ev));
            var p = ev.Where(e => e.Kind == StreamEventKind.Progress).ToList();
            Assert.Equal((50L, 100L, 40L, 12.5), (p[0].Processed, p[0].Total, p[0].Cache, p[0].TimeMs));
            Assert.Equal(100L, p[1].Processed);
            Assert.Equal((60L, 40L, 30.0, 2L, 8.0), (c.Stats.PromptN, c.Stats.CacheN, c.Stats.PromptMs, c.Stats.PredictedN, c.Stats.PredictedMs));   // timings win over usage
        }

        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(7)] [InlineData(13)] [InlineData(0)]
        public async Task SplitAtAnyByteGivesTheSameAnswer(int split)
        {
            string body = ": easy waiting\r\n\r\n" + Basic.Replace("\n", "\r\n");
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(body, split), engine: false);
            var c = await Call(llm, o);
            Assert.Equal("Hello é 😀", c.Text);
            Assert.Equal(3, ev.Count(e => e.Kind == StreamEventKind.Partial));
            Assert.Equal(1, ev.Count(e => e.Kind == StreamEventKind.Queued));
        }

        [Theory]
        [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
        public async Task LineEndings(string eol)
        {
            string body = Basic.Replace("\n", eol);
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(body, 4), engine: false);
            Assert.Equal("Hello é 😀", (await Call(llm, o)).Text);
        }

        [Fact]
        public async Task DoneWithoutTrailingBlankLineStillEnds()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Chunk(new { content = "x" }) + "data: [DONE]"), engine: false);
            Assert.Equal("x", (await Call(llm, o)).Text);
        }

        [Fact]
        public async Task MultiLineDataAndNoSpaceAfterColon()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse("data:{\"choices\":[{\"delta\":\ndata: {\"content\":\"ab\"}}]}\n\ndata:[DONE]\n\n"), engine: false);
            Assert.Equal("ab", (await Call(llm, o)).Text);
        }

        [Fact]
        public async Task JsonAnswerToAStreamRequestIsUsed()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Json(200, "{\"choices\":[{\"message\":{\"content\":\"plain\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}"), engine: false);
            var c = await Call(llm, o);
            Assert.Equal("plain", c.Text); Assert.Equal(3, c.Stats.PromptN);
        }

        // ---- errors

        [Theory]
        [InlineData("model_server_gone", "model_server_gone", 0)]
        [InlineData("upstream_ended", "upstream_ended", 0)]
        [InlineData("upstream_error", "upstream_error", 502)]
        public async Task MidStreamErrorEvents(string code, string cause, int status)
        {
            string err = "data: " + new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = "m", ["type"] = "t", ["request_id"] = "r1", ["status"] = status == 0 ? null : (JToken)status } }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Chunk(new { content = "par" }) + err), engine: true);
            var log = llm.CallLog;
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal(cause, e.Cause); Assert.Equal(status, e.Status); Assert.Equal("m", e.Detail); Assert.Equal("r1", e.ServerRequestId);
            Assert.False(e is LlmAway);
            Assert.Equal(StreamEventKind.Error, ev.Last().Kind); Assert.Equal(cause, ev.Last().Text);
            Assert.Equal(1, ev.Count(x => x.Kind == StreamEventKind.Error));
            var row = log.Snapshot().Single();
            Assert.False((bool)row["ok"]); Assert.Equal(cause, (string)row["error"]);
        }

        [Fact]
        public async Task MidStreamYieldedIsAwayWithRetryAndGate()
        {
            string err = "data: {\"error\":{\"code\":\"yielded\",\"message\":\"game on\",\"type\":\"unavailable_error\",\"request_id\":\"r\",\"retry\":12}}\n\n";
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(err), engine: true);
            var e = await Assert.ThrowsAsync<LlmAway>(() => Call(llm, o));
            Assert.Equal(12.0, e.RetryAfter); Assert.Equal("game on", e.AwayMessage);
            await Assert.ThrowsAsync<LlmAway>(() => Call(llm, o));    // gate: no second request
            Assert.Single(h.All);
        }

        [Fact]
        public async Task MidStreamYieldedWithoutRetryDefaultsToFive()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse("data: {\"error\":{\"code\":\"yielded\",\"message\":\"x\"}}\n\n"), engine: true);
            Assert.Equal(5.0, (await Assert.ThrowsAsync<LlmAway>(() => Call(llm, o))).RetryAfter);
        }

        [Fact]
        public async Task MidStreamNumericLlamaCode()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse("data: {\"error\":{\"code\":500,\"message\":\"boom\",\"type\":\"server_error\"}}\n\n"), engine: false);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal("HTTP 500", e.Cause); Assert.Equal(500, e.Status); Assert.Equal("boom", e.Detail);
        }

        [Fact]
        public async Task CloseWithoutDoneAndWithoutErrorIsAnError()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Chunk(new { content = "par" }) + Chunk(new { }, "stop")), engine: false);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal("stream ended without [DONE]", e.Cause);
            Assert.Equal(StreamEventKind.Error, ev.Last().Kind);
            Assert.Equal("stream ended without [DONE]", (string)llm.CallLog.Snapshot().Single()["error"]);
        }

        [Fact]
        public async Task BadChunkIsAnError()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse("data: {nope\n\n"), engine: false);
            Assert.Equal("bad stream chunk", (await Assert.ThrowsAsync<LlmError>(() => Call(llm, o))).Cause);
        }

        [Fact]
        public async Task PreStreamStringShape401And403()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Json(401, "{\"error\":\"bad key\"}", www: "Bearer realm=\"easy\""), engine: true);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal("HTTP 401", e.Cause); Assert.Equal(401, e.Status); Assert.Equal("bad key", e.Detail); Assert.Equal("Bearer realm=\"easy\"", e.WwwAuthenticate);
            Assert.Equal("Sent,Error", Kinds(ev));
            var (llm2, _, _, o2) = Make(s => StreamResp.Json(403, "{\"error\":\"nope\"}"), engine: true);
            e = await Assert.ThrowsAsync<LlmError>(() => Call(llm2, o2));
            Assert.Equal((403, "nope"), (e.Status, e.Detail));
        }

        [Fact]
        public async Task PreStream503YieldedIsAwayWithRetryAfter10()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Json(503, "{\"error\":{\"code\":\"yielded\",\"type\":\"unavailable_error\",\"message\":\"paused for game\"}}", "10"), engine: true);
            var e = await Assert.ThrowsAsync<LlmAway>(() => Call(llm, o));
            Assert.Equal(10.0, e.RetryAfter); Assert.Equal("paused for game", e.AwayMessage);
            Assert.True(llm.Gate.Away);
            Assert.Equal("Sent,Error", Kinds(ev));
        }

        [Theory]
        [InlineData("loading")]
        [InlineData("sleeping")]
        public async Task PreStream503NumericCodeIsErrorWithRetryAfter2(string what)
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Json(503, "{\"error\":{\"code\":503,\"type\":\"unavailable_error\",\"message\":\"" + what + "\"}}", "2"), engine: true);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.False(e is LlmAway);
            Assert.Equal("HTTP 503", e.Cause); Assert.Equal(2.0, e.RetryAfterS); Assert.Equal(what, e.Detail);
            Assert.False(llm.Gate.Away);
        }

        [Fact]
        public async Task PreStream502And500NonJson()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Json(502, "{\"error\":{\"code\":\"upstream_error\",\"type\":\"bad_gateway\",\"message\":\"down\"}}"), engine: true);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal((502, "upstream_error"), (e.Status, e.Cause));
            var (llm2, _, _, o2) = Make(s => StreamResp.Json(500, "<html>"), engine: false);
            e = await Assert.ThrowsAsync<LlmError>(() => Call(llm2, o2));
            Assert.Equal("HTTP 500", e.Cause);
            Assert.False((bool)llm2.CallLog.Snapshot().Single()["ok"]);
        }

        [Fact]
        public async Task SinkThatThrowsDoesNotBreakTheCall()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Basic), engine: false);
            o.Sink = new StreamSink(e => throw new InvalidOperationException("ui bug"));
            Assert.Equal("Hello é 😀", (await Call(llm, o)).Text);
        }

        // ---- cancellation and timeouts

        [Fact]
        public async Task CancelWhileStreamingCancelsAndRecordsARow()
        {
            var (llm, h, ev, o) = Make(s => { var r = StreamResp.Sse(Chunk(new { content = "par" })); r.HangAtEnd = true; return r; }, engine: false);
            var cts = new CancellationTokenSource();
            o.Cancel = cts.Token;
            var sink = o.Sink;
            o.Sink = new StreamSink(e => { sink.OnEvent(e); if (e.Kind == StreamEventKind.Partial) cts.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(llm, o));
            Assert.Equal(StreamEventKind.Error, ev.Last().Kind); Assert.Equal("cancelled", ev.Last().Text);
            Assert.Equal("cancelled", (string)llm.CallLog.Snapshot().Single()["error"]);
        }

        [Fact]
        public async Task CancelBeforeTheCallNeverSends()
        {
            var (llm, h, ev, o) = Make(s => StreamResp.Sse(Basic), engine: false);
            var cts = new CancellationTokenSource(); cts.Cancel(); o.Cancel = cts.Token;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(llm, o));
            Assert.Empty(h.All);
        }

        [Fact]
        public async Task IdleTimeoutIsAReadTimeout()
        {
            var (llm, h, ev, o) = Make(s => { var r = StreamResp.Sse(Chunk(new { content = "par" })); r.HangAtEnd = true; return r; }, engine: false, timeout: 0.2);
            var e = await Assert.ThrowsAsync<LlmError>(() => Call(llm, o));
            Assert.Equal("server unreachable (ReadTimeout)", e.Cause);
            Assert.Equal(StreamEventKind.Error, ev.Last().Kind);
        }

        [Fact]
        public async Task NonStreamCancelToken()
        {
            var h = new StreamHandler(s => { var r = StreamResp.Json(200, "{}"); r.HangAtEnd = true; r.Chunks.Clear(); return r; });
            var llm = new HttpLlm(new LlmConfig { BaseUrl = "http://h:1", Engine = false }, new CallLog(null), new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan });
            var cts = new CancellationTokenSource(); cts.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => llm.CompleteAsync(new LlmMessage[] { ("user", "u") }, null, "line", 48, null, null, null, null, null, new CallOptions { Cancel = cts.Token }));
        }

        // ---- backend

        private static OpenAIBackend Backend(Func<Seen, StreamResp> f, out StreamHandler h, bool engine = false)
        {
            h = new StreamHandler(f);
            return new OpenAIBackend("http://h:1", model: "m", client: new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan }, engine: engine);
        }

        private static readonly IReadOnlyList<JObject> Msgs = new[] { new JObject { ["role"] = "user", ["content"] = "hi" } };

        [Fact]
        public async Task BackendStreamsContentAndRequestId()
        {
            var be = Backend(s => StreamResp.Sse(Basic), out var h);
            var ev = new List<StreamEvent>();
            var r = await be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, 3, new CallOptions { Sink = new StreamSink(ev.Add) });
            Assert.Equal("Hello é 😀", r.Text); Assert.Equal("stop", r.Finish);
            Assert.Equal(h.All.Single().Headers["X-Request-Id"], r.RequestId);
            Assert.EndsWith(",\"stream\":true,\"stream_options\":{\"include_usage\":true}}", h.All.Single().BodyText);
            Assert.Equal("Sent,FirstToken,Partial,Partial,Partial,Done", Kinds(ev));
        }

        [Fact]
        public async Task BackendEngineGetsReturnProgress()
        {
            var be = Backend(s => StreamResp.Sse(Basic), out var h, engine: true);
            await be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, null, new CallOptions { Sink = new StreamSink(e => { }) });
            Assert.EndsWith(",\"return_progress\":true}", h.All.Single().BodyText);
        }

        [Fact]
        public async Task BackendAccumulatesToolCallDeltas()
        {
            string body =
                Chunk(new { role = "assistant", content = (string)null }) +
                Chunk(new { tool_calls = new[] { new { index = 0, id = "c1", type = "function", function = new { name = "look", arguments = "" } } } }) +
                Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "{\"wh" } } } }) +
                Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "o\":\"Ann\"}" } } } }) +
                Chunk(new { tool_calls = new[] { new { index = 1, function = new { name = "say", arguments = "{}" } } } }) +
                Chunk(new { }, "tool_calls") + "data: [DONE]\n\n";
            var be = Backend(s => StreamResp.Sse(body, 5), out var h);
            var r = await be.ChatAsync(Msgs, null, new JArray(), "auto", 160, 0.2, 5.0, null, new CallOptions { Sink = new StreamSink(e => { }) });
            Assert.Equal(2, r.ToolCalls.Count);
            Assert.Equal(("c1", "look", "{\"who\":\"Ann\"}"), (r.ToolCalls[0].Id, r.ToolCalls[0].Name, (string)r.ToolCalls[0].Arguments));
            Assert.Equal(("call_1", "say", "{}"), (r.ToolCalls[1].Id, r.ToolCalls[1].Name, (string)r.ToolCalls[1].Arguments));
            Assert.Equal("tool_calls", r.Finish); Assert.Equal("", r.Text);
        }

        [Fact]
        public async Task BackendErrorMapping()
        {
            var o = new CallOptions { Sink = new StreamSink(e => { }) };
            var be = Backend(s => StreamResp.Json(503, "{\"error\":{\"code\":\"yielded\",\"message\":\"game\"}}", "10"), out _);
            var a = await Assert.ThrowsAsync<BackendAway>(() => be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, null, o));
            Assert.Equal(10.0, a.RetryAfter);
            await Assert.ThrowsAsync<BackendAway>(() => be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, null, o));   // gate
            be = Backend(s => StreamResp.Json(401, "{\"error\":\"bad key\"}"), out _);
            var e = await Assert.ThrowsAsync<BackendError>(() => be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, null, o));
            Assert.Equal("HTTP 401: bad key", e.Message);
            be = Backend(s => StreamResp.Sse(Chunk(new { content = "x" })), out _);
            e = await Assert.ThrowsAsync<BackendError>(() => be.ChatAsync(Msgs, null, null, null, 160, 0.2, 5.0, null, o));
            Assert.Equal("stream ended without [DONE]", e.Message);
        }

        [Fact]
        public async Task BackendNonStreamSendsRequestIdAndCancels()
        {
            var be = Backend(s => StreamResp.Json(200, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"), out var h);
            var r = await be.ChatAsync(Msgs);
            Assert.Equal(h.All.Single().Headers["X-Request-Id"], r.RequestId);
            Assert.DoesNotContain("stream", h.All.Single().BodyText);
        }

        // ---- parser

        [Fact]
        public void SseParserSplitsUtf8AcrossFeeds()
        {
            var got = new List<string>();
            var p = new SseParser { OnData = got.Add };
            byte[] b = Encoding.UTF8.GetBytes("data: é😀\n\n");
            foreach (byte x in b) p.Feed(new[] { x }, 0, 1);
            p.Finish();
            Assert.Equal(new[] { "é😀" }, got.ToArray());
        }

        [Fact]
        public void SseParserCrThenLfAcrossFeedsIsOneBreak()
        {
            var got = new List<string>();
            var p = new SseParser { OnData = got.Add };
            p.Feed("data: a\r"); p.Feed("\n\r"); p.Feed("\ndata: b\n\n");
            Assert.Equal(new[] { "a", "b" }, got.ToArray());
        }

        [Fact]
        public void SseParserIgnoresOtherFieldsAndKeepsComments()
        {
            var got = new List<string>(); var com = new List<string>();
            var p = new SseParser { OnData = got.Add, OnComment = com.Add };
            p.Feed("event: x\nid: 4\nretry: 9\n: easy waiting\ndata: z\n\n\n\n");
            Assert.Equal(new[] { "z" }, got.ToArray()); Assert.Equal(new[] { " easy waiting" }, com.ToArray());
        }
    }
}

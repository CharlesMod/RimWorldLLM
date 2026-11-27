// NEW (not in the Python): opt-in streaming for the OpenAI chat mode. Events for a UI, an SSE parser, the chat-chunk accumulator,
// the error mapping (pre-stream and mid-stream) and the one sender HttpLlm and OpenAIBackend share.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public enum StreamEventKind { Sent, Queued, Progress, FirstToken, Partial, Done, Error }

    /// <summary>One thing that happened during a streamed call. Which fields are set depends on Kind:
    /// Progress: Processed/Total/Cache/TimeMs; Partial: Text = the whole text so far; Done: Text + Stats; Error: Text = the cause, Exception.</summary>
    public sealed class StreamEvent
    {
        public StreamEventKind Kind { get; private set; }
        public string Text { get; private set; }
        public long Processed { get; private set; }
        public long Total { get; private set; }
        public long Cache { get; private set; }
        public double TimeMs { get; private set; }
        public CallStats Stats { get; private set; }
        public Exception Exception { get; private set; }

        public static StreamEvent Sent() => new StreamEvent { Kind = StreamEventKind.Sent };
        public static StreamEvent Queued() => new StreamEvent { Kind = StreamEventKind.Queued };
        public static StreamEvent FirstToken() => new StreamEvent { Kind = StreamEventKind.FirstToken };
        public static StreamEvent Progress(long processed, long total, long cache, double timeMs)
            => new StreamEvent { Kind = StreamEventKind.Progress, Processed = processed, Total = total, Cache = cache, TimeMs = timeMs };
        public static StreamEvent Partial(string whole) => new StreamEvent { Kind = StreamEventKind.Partial, Text = whole };
        public static StreamEvent Done(string text, CallStats stats) => new StreamEvent { Kind = StreamEventKind.Done, Text = text, Stats = stats };
        public static StreamEvent Error(string cause, Exception e) => new StreamEvent { Kind = StreamEventKind.Error, Text = cause, Exception = e };
    }

    /// <summary>Receives a streamed call's events on the thread that reads the stream. Keep it quick and non-blocking; an exception it
    /// throws is swallowed.</summary>
    public interface ILlmStreamSink
    {
        void OnEvent(StreamEvent e);
    }

    public sealed class StreamSink : ILlmStreamSink
    {
        private readonly Action<StreamEvent> _h;
        public StreamSink(Action<StreamEvent> handler) { _h = handler; }
        public void OnEvent(StreamEvent e) => _h(e);
    }

    /// <summary>Per-call options. A null Sink means the ordinary non-streaming request (Cancel still applies).</summary>
    public sealed class CallOptions
    {
        public ILlmStreamSink Sink { get; set; }
        public CancellationToken Cancel { get; set; }
    }

    /// <summary>An ILlmClient that can also stream one call. Callers that do not stream use ILlmClient and are untouched.</summary>
    public interface IStreamingLlmClient : ILlmClient
    {
        Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind, int maxTokens,
                                       IReadOnlyList<string> stop, string grammar, double? temperature,
                                       int? actor, JObject jsonSchema, CallOptions options);
    }

    public static class RequestIds
    {
        /// <summary>A fresh printable-ASCII id, 36 bytes.</summary>
        public static string New() => "rwl-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>Server-sent events: feed bytes as they arrive, get data events and comments back. Splits lines on LF, CRLF or CR; a blank
    /// line ends an event; a multi-line "data" is joined with LF; fields other than data are ignored.</summary>
    public sealed class SseParser
    {
        private readonly Decoder _dec = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private StringBuilder _data;
        private bool _prevCr, _first = true;

        public Action<string> OnData;
        /// <summary>The comment text after the colon, untrimmed.</summary>
        public Action<string> OnComment;

        public void Feed(byte[] buf, int offset, int count)
        {
            var chars = new char[_dec.GetCharCount(buf, offset, count)];
            int n = _dec.GetChars(buf, offset, count, chars, 0);
            for (int i = 0; i < n; i++) FeedChar(chars[i]);
        }

        public void Feed(string text) { foreach (char c in text) FeedChar(c); }

        /// <summary>The stream ended: a last line and event that lacked their blank line still count.</summary>
        public void Finish()
        {
            if (_line.Length > 0) EndLine();
            Dispatch();
        }

        private void FeedChar(char c)
        {
            if (_first) { _first = false; if (c == '﻿') return; }
            if (_prevCr) { _prevCr = false; if (c == '\n') return; }
            if (c == '\r') { _prevCr = true; EndLine(); }
            else if (c == '\n') EndLine();
            else _line.Append(c);
        }

        private void EndLine()
        {
            string l = _line.ToString();
            _line.Clear();
            if (l.Length == 0) { Dispatch(); return; }
            if (l[0] == ':') { OnComment?.Invoke(l.Substring(1)); return; }
            int c = l.IndexOf(':');
            string field = c < 0 ? l : l.Substring(0, c);
            string value = c < 0 ? "" : l.Substring(c + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
            if (field != "data") return;
            if (_data == null) _data = new StringBuilder(); else _data.Append('\n');
            _data.Append(value);
        }

        private void Dispatch()
        {
            if (_data == null) return;
            string s = _data.ToString();
            _data = null;
            OnData?.Invoke(s);
        }
    }

    public sealed class StreamToolCall
    {
        public int Index;
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new StringBuilder();
    }

    /// <summary>What a streamed chat completion came to.</summary>
    public sealed class ChatStreamResult
    {
        public string Text = "";
        public string Reasoning = "";
        public string Finish;
        public List<StreamToolCall> ToolCalls = new List<StreamToolCall>();
        public JToken Usage;
        public JToken Timings;
        public double? TtftS;
    }

    /// <summary>Maps the engine's and llama-server's error answers, in both shapes, to exceptions.</summary>
    public static class StreamErrors
    {
        private static double Clamp(double ra) { if (0.0 > ra) ra = 0.0; if (LlmWire.AwayCapS < ra) ra = LlmWire.AwayCapS; return ra; }

        private static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);

        private static double? HeaderSeconds(string h)
        {
            if (h != null && PyFloat.TryParse(h, out double v) && !double.IsNaN(v)) return Clamp(v);
            return null;
        }

        /// <summary>The exception for an "error" value: {"error": "text"} or {"error": {"code","type","message",...}}.
        /// status is the HTTP status (0 mid-stream, where the object may carry its own "status"); the header values are null mid-stream.</summary>
        public static LlmError FromError(JToken err, int status, string retryAfterHeader, string wwwAuthenticate)
        {
            double? hdr = HeaderSeconds(retryAfterHeader);
            if (err is JObject o)
            {
                JToken codeT = o.TryGetValue("code", out JToken cv) ? cv : null;
                string msg = o.TryGetValue("message", out JToken mv) ? Str(mv) : "";
                string type = o.TryGetValue("type", out JToken tv) ? Str(tv) : "";
                string rid = o.TryGetValue("request_id", out JToken rv) ? Str(rv) : "";
                int st = status;
                if (o.TryGetValue("status", out JToken sv) && sv.Type == JTokenType.Integer) st = (int)(long)sv;
                double? retry = hdr;
                if (o.TryGetValue("retry", out JToken rt) && (rt.Type == JTokenType.Integer || rt.Type == JTokenType.Float)) retry = Clamp((double)rt);
                string code = Str(codeT);
                if (codeT != null && codeT.Type == JTokenType.String && code == "yielded")
                    return new LlmAway(string.IsNullOrEmpty(msg) ? "paused" : msg, retry ?? 5.0) { Code = code, Detail = msg, ErrorType = type, ServerRequestId = rid };
                LlmError e;
                if (codeT != null && codeT.Type == JTokenType.Integer)
                {
                    int num = (int)(long)codeT;
                    e = new LlmError("HTTP " + num.ToString(CultureInfo.InvariantCulture), num);
                }
                else if (codeT != null && codeT.Type == JTokenType.String && code.Length > 0)
                    e = new LlmError(code, st);
                else
                    e = new LlmError(st > 0 ? "HTTP " + st.ToString(CultureInfo.InvariantCulture) : "stream error", st);
                e.Code = code; e.Detail = msg; e.ErrorType = type; e.ServerRequestId = rid; e.RetryAfterS = retry; e.WwwAuthenticate = wwwAuthenticate;
                return e;
            }
            var s = new LlmError(status > 0 ? "HTTP " + status.ToString(CultureInfo.InvariantCulture) : "stream error", status)
            { Detail = Str(err), RetryAfterS = hdr, WwwAuthenticate = wwwAuthenticate };
            return s;
        }

        /// <summary>A non-200 answer before any stream byte.</summary>
        public static LlmError FromReply(int status, byte[] body, string retryAfterHeader, string wwwAuthenticate)
        {
            var y = LlmWire.ParseYielded(status, body, retryAfterHeader);
            if (y != null) return new LlmAway(y.Message, y.RetryAfter) { Code = "yielded", Detail = y.Message };
            JToken d = null;
            try { d = PyJson.Loads(body); } catch (PyJsonException) { }
            if (d is JObject dobj && dobj.TryGetValue("error", out JToken err)) return FromError(err, status, retryAfterHeader, wwwAuthenticate);
            return new LlmError("HTTP " + status.ToString(CultureInfo.InvariantCulture), status)
            { RetryAfterS = HeaderSeconds(retryAfterHeader), WwwAuthenticate = wwwAuthenticate };
        }
    }

    /// <summary>Turns SSE data into a ChatStreamResult and sink events. Throws the mapped error on a terminal error event.</summary>
    public sealed class ChatStreamReader
    {
        private readonly ILlmStreamSink _sink;
        private readonly Stopwatch _sw;
        private readonly StringBuilder _text = new StringBuilder();
        private readonly StringBuilder _reasoning = new StringBuilder();
        private readonly SortedDictionary<int, StreamToolCall> _calls = new SortedDictionary<int, StreamToolCall>();
        private bool _first;
        public bool Done { get; private set; }
        public ChatStreamResult Result { get; } = new ChatStreamResult();

        public ChatStreamReader(ILlmStreamSink sink, Stopwatch sw) { _sink = sink; _sw = sw; }

        public static void Emit(ILlmStreamSink sink, StreamEvent e)
        {
            if (sink == null) return;
            try { sink.OnEvent(e); } catch (Exception) { }
        }

        public void Comment(string c)
        {
            string t = c.Trim();
            if (t.StartsWith("easy waiting", StringComparison.Ordinal)) Emit(_sink, StreamEvent.Queued());
            // ": easy quiet" and llama's pings: nothing
        }

        private void FirstToken()
        {
            if (_first) return;
            _first = true;
            Result.TtftS = _sw.Elapsed.TotalSeconds;
            Emit(_sink, StreamEvent.FirstToken());
        }

        /// <summary>One SSE data payload (also used for a whole non-stream body, where "message" stands in for "delta").</summary>
        public void Data(string data)
        {
            if (Done) return;
            string t = data.Trim();
            if (t == "[DONE]") { Done = true; return; }
            JObject o;
            try { o = PyJson.Loads(t) as JObject; }
            catch (PyJsonException) { throw new LlmError("bad stream chunk"); }
            if (o == null) throw new LlmError("bad stream chunk");
            if (o.TryGetValue("error", out JToken err) && err != null && err.Type != JTokenType.Null)
                throw StreamErrors.FromError(err, 0, null, null);
            if (o.TryGetValue("prompt_progress", out JToken pp) && pp is JObject ppo)
                Emit(_sink, StreamEvent.Progress(Long(ppo, "processed"), Long(ppo, "total"), Long(ppo, "cache"), Dbl(ppo, "time_ms")));
            if (o.TryGetValue("usage", out JToken u) && u is JObject) Result.Usage = u;
            if (o.TryGetValue("timings", out JToken tm) && tm is JObject) Result.Timings = tm;
            if (o.TryGetValue("choices", out JToken ch) && ch is JArray ca && ca.Count > 0 && ca[0] is JObject c0)
            {
                JToken delta = c0.TryGetValue("delta", out JToken dv) ? dv : (c0.TryGetValue("message", out JToken mv) ? mv : null);
                if (delta is JObject d)
                {
                    if (d.TryGetValue("reasoning_content", out JToken rc) && rc.Type == JTokenType.String) _reasoning.Append((string)rc);
                    if (d.TryGetValue("tool_calls", out JToken tcs) && tcs is JArray ta)
                    {
                        FirstToken();
                        foreach (JToken tc in ta) Call(tc, _calls.Count);
                    }
                    if (d.TryGetValue("content", out JToken cont) && cont.Type == JTokenType.String && ((string)cont).Length > 0)
                    {
                        FirstToken();
                        _text.Append((string)cont);
                        Emit(_sink, StreamEvent.Partial(_text.ToString()));
                    }
                }
                if (c0.TryGetValue("finish_reason", out JToken fr) && fr.Type != JTokenType.Null) Result.Finish = PyJson.Str(fr);
            }
        }

        private void Call(JToken tc, int fallbackIndex)
        {
            if (!(tc is JObject o)) return;
            int idx = o.TryGetValue("index", out JToken iv) && iv.Type == JTokenType.Integer ? (int)(long)iv : fallbackIndex;
            if (!_calls.TryGetValue(idx, out StreamToolCall c)) { c = new StreamToolCall { Index = idx }; _calls[idx] = c; }
            if (o.TryGetValue("id", out JToken id) && id.Type == JTokenType.String && ((string)id).Length > 0) c.Id = (string)id;
            if (o.TryGetValue("function", out JToken f) && f is JObject fo)
            {
                if (fo.TryGetValue("name", out JToken n) && n.Type == JTokenType.String) c.Name += (string)n;
                if (fo.TryGetValue("arguments", out JToken a) && a.Type == JTokenType.String) c.Arguments.Append((string)a);
            }
        }

        private static long Long(JObject o, string k) => o.TryGetValue(k, out JToken v) && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (long)(double)v : 0;
        private static double Dbl(JObject o, string k) => o.TryGetValue(k, out JToken v) && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (double)v : 0.0;

        public ChatStreamResult Finish()
        {
            Result.Text = _text.ToString();
            Result.Reasoning = _reasoning.ToString();
            Result.ToolCalls = new List<StreamToolCall>(_calls.Values);
            return Result;
        }
    }

    /// <summary>The one streamed POST. Emits Sent and every event up to Done's predecessor; on failure emits Error once and throws
    /// (HttpTransportException, LlmError/LlmAway, or OperationCanceledException for the caller's token). The caller emits Done.</summary>
    public static class StreamSender
    {
        public static async Task<ChatStreamResult> PostAsync(HttpClient http, string url, JObject body, IDictionary<string, string> headers,
                                                             ILlmStreamSink sink, double idleTimeoutS, CancellationToken cancel, Stopwatch sw)
        {
            try
            {
                return await Run(http, url, body, headers, sink, idleTimeoutS, cancel, sw).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (cancel.IsCancellationRequested)
            {
                ChatStreamReader.Emit(sink, StreamEvent.Error("cancelled", e));
                throw;
            }
            catch (HttpTransportException e)
            {
                ChatStreamReader.Emit(sink, StreamEvent.Error("server unreachable (" + e.Kind + ")", e));
                throw;
            }
            catch (LlmError e)
            {
                ChatStreamReader.Emit(sink, StreamEvent.Error(e.Cause, e));
                throw;
            }
        }

        private static async Task<ChatStreamResult> Run(HttpClient http, string url, JObject body, IDictionary<string, string> headers,
                                                        ILlmStreamSink sink, double idleTimeoutS, CancellationToken cancel, Stopwatch sw)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            void Arm() { if (idleTimeoutS > 0 && !double.IsInfinity(idleTimeoutS)) idle.CancelAfter(TimeSpan.FromSeconds(Math.Min(idleTimeoutS, 86400.0))); }
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new ByteArrayContent(PyJson.HttpxJson(body));
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            ChatStreamReader.Emit(sink, StreamEvent.Sent());
            try
            {
                Arm();
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                int status = (int)resp.StatusCode;
                string retryAfter = null, www = null, ctype = null;
                foreach (var h in resp.Headers.NonValidated)
                {
                    if (string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase)) retryAfter = string.Join(", ", h.Value);
                    else if (string.Equals(h.Key, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)) www = string.Join(", ", h.Value);
                }
                foreach (var h in resp.Content.Headers.NonValidated)
                    if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) ctype = string.Join(", ", h.Value);
                using var stream = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                var buf = new byte[8192];
                if (status != 200)
                {
                    var ms = new MemoryStream();
                    int k;
                    while (true)
                    {
                        Arm();
                        k = await stream.ReadAsync(buf, 0, buf.Length, idle.Token).ConfigureAwait(false);
                        if (k <= 0) break;
                        ms.Write(buf, 0, k);
                    }
                    throw StreamErrors.FromReply(status, ms.ToArray(), retryAfter, www);
                }
                var reader = new ChatStreamReader(sink, sw);
                if (ctype != null && ctype.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // the server ignored "stream": a whole chat completion
                    var ms = new MemoryStream();
                    while (true)
                    {
                        Arm();
                        int k = await stream.ReadAsync(buf, 0, buf.Length, idle.Token).ConfigureAwait(false);
                        if (k <= 0) break;
                        ms.Write(buf, 0, k);
                    }
                    reader.Data(new UTF8Encoding(false, false).GetString(ms.ToArray()));
                    return reader.Finish();
                }
                var sse = new SseParser { OnData = reader.Data, OnComment = reader.Comment };
                while (!reader.Done)
                {
                    Arm();
                    int k = await stream.ReadAsync(buf, 0, buf.Length, idle.Token).ConfigureAwait(false);
                    if (k <= 0) break;
                    sse.Feed(buf, 0, k);
                }
                if (!reader.Done) sse.Finish();
                if (!reader.Done) throw new LlmError("stream ended without [DONE]") { Code = "no_done" };
                return reader.Finish();
            }
            catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
            {
                throw new HttpTransportException("ReadTimeout", "timed out", e);
            }
            catch (HttpRequestException e)
            {
                throw new HttpTransportException("ConnectError", e.Message, e);
            }
            catch (IOException e)
            {
                throw new HttpTransportException("ReadError", e.Message, e);
            }
        }
    }
}

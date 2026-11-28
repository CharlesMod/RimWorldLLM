// Port of core/toolloop/backend.py: the one place the tool loop talks HTTP.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    /// <summary>The server was unreachable, slow, or answered with something unusable. Never a model fault.</summary>
    public class BackendError : Exception, IPyNamed
    {
        public virtual string PyName => "BackendError";
        public BackendError(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>The engine answered 503 code "yielded": the model server machine is running a game.</summary>
    public sealed class BackendAway : BackendError
    {
        public override string PyName => "BackendAway";
        public string AwayMessage { get; }
        public double RetryAfter { get; }
        public BackendAway(string message, double retryAfter = 5.0) : base("model paused on the server: " + message)
        {
            AwayMessage = message; RetryAfter = retryAfter;
        }
    }

    public sealed class ToolCall
    {
        public string Id = "";
        public string Name = "";
        /// <summary>A JSON string, an object, or null.</summary>
        public JToken Arguments;
    }

    public sealed class Reply
    {
        public string Text = "";
        public List<ToolCall> ToolCalls = new List<ToolCall>();
        public string Finish;
        public string Reasoning = "";
        public double Ms;
        /// <summary>The X-Request-Id sent (OpenAIBackend); null otherwise.</summary>
        public string RequestId;
    }

    public interface IBackend
    {
        Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                              int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null);
    }

    /// <summary>A backend that can also stream one chat (content to the sink as it comes; tool_calls deltas accumulate into the Reply).</summary>
    public interface IStreamingBackend : IBackend
    {
        Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar, JArray tools, string toolChoice,
                              int maxTokens, double temperature, double timeout, int? slot, CallOptions options);
    }

    /// <summary>A backend that names its endpoint; the capability probe caches by it (Python: getattr(backend, "base"/"model")).</summary>
    public interface IBackendIdentity
    {
        string Base { get; }
        string Model { get; }
    }

    /// <summary>The kind a tool-loop call gets in calls.jsonl: set by ToolHarness.RunAsync for the length of a loop (flows with the async call).
    /// A call with no slot is the protocol probe.</summary>
    public static class ToolCallKind
    {
        private static readonly AsyncLocal<string> Cur = new AsyncLocal<string>();
        private static readonly AsyncLocal<long?> CurTurn = new AsyncLocal<long?>();
        private static long _turns;
        public static string Current => Cur.Value;
        /// <summary>The turn the calling loop belongs to (null outside a loop and for the probe).</summary>
        public static long? Turn => CurTurn.Value;
        public static string For(int? slot) => Cur.Value == "probe" || !slot.HasValue ? "probe" : (Cur.Value ?? "toolloop");
        /// <summary>Scopes a kind; a loop (newTurn) also gets a fresh turn number, so the calls of one turn can be summed.</summary>
        public static IDisposable Push(string kind, bool newTurn = false)
        {
            var prev = new Pop(Cur.Value, CurTurn.Value);
            Cur.Value = kind;
            CurTurn.Value = newTurn ? System.Threading.Interlocked.Increment(ref _turns) : (long?)null;
            return prev;
        }
        private sealed class Pop : IDisposable
        {
            private readonly string _prev; private readonly long? _prevTurn;
            public Pop(string prev, long? prevTurn) { _prev = prev; _prevTurn = prevTurn; }
            public void Dispose() { Cur.Value = _prev; CurTurn.Value = _prevTurn; }
        }
    }

    /// <summary>POST {base}/chat/completions. Extra goes into every body (id_slot, cache_prompt...).</summary>
    public sealed class OpenAIBackend : IStreamingBackend, IBackendIdentity, IDisposable
    {
        public string Base { get; }
        public string Model { get; }
        public string ApiKey { get; }
        public HttpClient Http { get; }
        public JObject Extra { get; }
        public JObject Sampler { get; }
        public bool HonorTemperature { get; }
        public AwayGate Gate { get; }
        private bool? _engine;
        /// <summary>Makes the X-Request-Id of each call (printable ASCII, at most 128 bytes). Replaceable for tests.</summary>
        public Func<string> RequestIdFactory { get; set; } = RequestIds.New;
        /// <summary>When set, every call is recorded to it (kind from <see cref="ToolCallKind"/>, "probe" when it has no slot). Same log the HttpLlm writes.</summary>
        public CallLog CallLog { get; set; }
        private readonly bool _ownsHttp;

        public OpenAIBackend(string baseUrl, string model = "", string apiKey = "", HttpClient client = null, JObject extra = null, bool? engine = null,
                             JObject sampler = null, bool honorTemperature = false, Func<double> clock = null, LogSink log = null)
        {
            string u = PyStr.Strip(baseUrl).TrimEnd('/');
            Base = u.EndsWith("/v1", StringComparison.Ordinal) ? u : u + "/v1";
            Model = model; ApiKey = apiKey;
            _ownsHttp = client == null;
            Http = client ?? HttpUtil.NewClient();
            Extra = new JObject { ["cache_prompt"] = true, ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false } };
            if (extra != null) foreach (var p in extra.Properties()) Extra[p.Name] = p.Value.DeepClone();
            Gate = new AwayGate(clock, log);
            _engine = engine;
            Sampler = sampler != null ? (JObject)sampler.DeepClone() : new JObject();
            HonorTemperature = honorTemperature;
        }

        public void Dispose() { if (_ownsHttp) Http.Dispose(); }

        private static IEnumerable<JToken> PyIter(JToken t)
        {
            if (t is JArray a) return a;
            if (t is JObject || (t != null && t.Type == JTokenType.String))
                throw new PyAttributeError("'" + (t is JObject ? "str" : "str") + "' object has no attribute 'get'");
            throw new PyTypeError("'" + PyAccess.TypeName(t) + "' object is not iterable");
        }

        public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                     int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
            => ChatAsync(messages, grammar, tools, toolChoice, maxTokens, temperature, timeout, slot, (CallOptions)null);

        /// <summary>With options: a cancellation token (cancels the request; OperationCanceledException comes out, not a BackendError) and a
        /// stream sink. The sink path sends "stream": true, "stream_options" and, on easyEngine only, "return_progress". The timeout is then the
        /// idle time (to the headers, and between reads) rather than a total.</summary>
        public async Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar, JArray tools, string toolChoice,
                                           int maxTokens, double temperature, double timeout, int? slot, CallOptions options)
        {
            CancellationToken cancel = options != null ? options.Cancel : default;
            ILlmStreamSink sink = options?.Sink;
            string requestId = RequestIdFactory();
            try { Gate.Check(); }
            catch (LlmAway e) { throw new BackendAway(e.AwayMessage, e.RetryAfter); }
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(ApiKey)) headers["Authorization"] = "Bearer " + ApiKey;
            headers["X-Request-Id"] = requestId;
            if (_engine == null) _engine = await LlmWire.DetectEngineAsync(Http, Base, headers).ConfigureAwait(false);
            bool engine = _engine == true;
            var samp = (JObject)Sampler.DeepClone();
            if (HonorTemperature && !samp.ContainsKey("temperature")) samp["temperature"] = temperature;
            if (!engine && LlmWire.IsKnownGemma4(Model))
            {
                if (!samp.ContainsKey("temperature")) samp["temperature"] = LlmWire.Gemma4Temperature;
                if (!samp.ContainsKey("top_k")) samp["top_k"] = LlmWire.Gemma4TopK;
                if (!samp.ContainsKey("top_p")) samp["top_p"] = LlmWire.Gemma4TopP;
            }
            var msgs = new JArray();
            foreach (var m in messages) msgs.Add(m);
            var body = new JObject { ["messages"] = msgs, ["max_tokens"] = maxTokens };
            foreach (var p in samp.Properties()) body[p.Name] = p.Value;
            foreach (var p in Extra.Properties()) body[p.Name] = p.Value;
            if (engine) headers["X-Easy-Sampler"] = "structured";
            if (!string.IsNullOrEmpty(Model)) body["model"] = Model;
            if (slot.HasValue) body["id_slot"] = slot.Value;
            if (!string.IsNullOrEmpty(grammar)) body["grammar"] = grammar;
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = string.IsNullOrEmpty(toolChoice) ? "auto" : toolChoice;
            }
            var sw = Stopwatch.StartNew();
            if (sink != null) return await StreamChatAsync(body, headers, engine, sink, timeout, cancel, sw, requestId, slot).ConfigureAwait(false);
            HttpReply r;
            try { r = await HttpUtil.PostJsonAsync(Http, Base + "/chat/completions", body, headers, timeout, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Record(slot, requestId, sw, null, null, false, "cancelled"); throw; }
            catch (HttpTransportException e) { Record(slot, requestId, sw, null, null, false, e.Kind); throw new BackendError("unreachable (" + e.Kind + ")", e); }
            double ms = sw.Elapsed.TotalMilliseconds;
            var y = LlmWire.ParseYielded(r.Status, r.Content, r.Header("Retry-After"));
            if (y != null)
            {
                Record(slot, requestId, sw, null, null, false, "away");
                Gate.WentAway(y.Message, y.RetryAfter);
                throw new BackendAway(y.Message, y.RetryAfter);
            }
            if (r.Status >= 400) { Record(slot, requestId, sw, null, null, false, "HTTP " + r.Status.ToString(CultureInfo.InvariantCulture)); throw new BackendError("HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + ": " + PyStr.Head(r.Text, 160)); }
            Gate.Ok();
            JToken ch, m2;
            try
            {
                ch = PyAccess.Item(PyAccess.Item(r.Json(), "choices"), 0);
                m2 = PyAccess.Get(ch, "message");
            }
            catch (Exception e) when (e is PyJsonException || e is PyKeyError || e is PyIndexError || e is PyTypeError)
            {
                Record(slot, requestId, sw, null, null, false, "bad reply");
                throw new BackendError("reply was not a chat completion", e);
            }
            if (CallLog != null)
            {
                JObject rj = null;
                try { rj = r.Json() as JObject; } catch (Exception) { }
                Record(slot, requestId, sw, rj?["timings"], rj?["usage"], true, "");
            }
            if (!PyJson.Truthy(m2)) m2 = new JObject();
            var calls = new List<ToolCall>();
            JToken tcs = PyAccess.Get(m2, "tool_calls");
            int i = 0;
            if (PyJson.Truthy(tcs))
            {
                foreach (var tc in PyIter(tcs))
                {
                    JToken f = PyAccess.Get(tc, "function");
                    if (!PyJson.Truthy(f)) f = new JObject();
                    JToken id = PyAccess.Get(tc, "id");
                    JToken name = PyAccess.Get(f, "name");
                    calls.Add(new ToolCall
                    {
                        Id = PyJson.Truthy(id) ? PyJson.Str(id) : "call_" + i.ToString(CultureInfo.InvariantCulture),
                        Name = PyJson.Truthy(name) ? PyJson.Str(name) : "",
                        Arguments = PyAccess.Get(f, "arguments"),
                    });
                    i++;
                }
            }
            JToken content = PyAccess.Get(m2, "content");
            JToken fin = PyAccess.Get(ch, "finish_reason");
            JToken rea = PyAccess.Get(m2, "reasoning_content");
            return new Reply
            {
                Text = content != null && content.Type == JTokenType.String ? (string)content : "",
                ToolCalls = calls,
                Finish = fin == null || fin.Type == JTokenType.Null ? null : PyJson.Str(fin),
                Reasoning = PyJson.Truthy(rea) ? PyJson.Str(rea) : "",
                Ms = ms,
                RequestId = requestId,
            };
        }

        /// <summary>One calls.jsonl row for a non-streamed call: timings (llama-server) or usage (any OpenAI server) give the token counts.</summary>
        private void Record(int? slot, string requestId, Stopwatch sw, JToken timings, JToken usage, bool ok, string error)
        {
            if (CallLog == null) return;
            var st = new CallStats { Kind = ToolCallKind.For(slot), Slot = slot, Turn = slot.HasValue ? ToolCallKind.Turn : null, RequestId = requestId, WallS = sw.Elapsed.TotalSeconds, Ok = ok, Error = error ?? "" };
            if (timings != null && PyJson.Truthy(timings))
            {
                st.PromptN = LlmWire.PyInt(PyAccess.Get(timings, "prompt_n", new JValue(0)));
                st.CacheN = LlmWire.PyInt(PyAccess.Get(timings, "cache_n", new JValue(0)));
                st.PromptMs = LlmWire.PyFloatOf(PyAccess.Get(timings, "prompt_ms", new JValue(0)));
                st.PredictedN = LlmWire.PyInt(PyAccess.Get(timings, "predicted_n", new JValue(0)));
                st.PredictedMs = LlmWire.PyFloatOf(PyAccess.Get(timings, "predicted_ms", new JValue(0)));
            }
            else if (usage != null && PyJson.Truthy(usage))
            {
                st.PromptN = LlmWire.PyInt(PyAccess.Get(usage, "prompt_tokens", new JValue(0)));
                st.PredictedN = LlmWire.PyInt(PyAccess.Get(usage, "completion_tokens", new JValue(0)));
            }
            CallLog.Record(st);
        }

        private async Task<Reply> StreamChatAsync(JObject body, Dictionary<string, string> headers, bool engine, ILlmStreamSink sink, double timeout,
                                                  CancellationToken cancel, Stopwatch sw, string requestId, int? slot)
        {
            body["stream"] = true;
            body["stream_options"] = new JObject { ["include_usage"] = true };
            if (engine) body["return_progress"] = true;
            ChatStreamResult sr;
            try { sr = await StreamSender.PostAsync(Http, Base + "/chat/completions", body, headers, sink, timeout, cancel, sw).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Record(slot, requestId, sw, null, null, false, "cancelled"); throw; }
            catch (HttpTransportException e) { Record(slot, requestId, sw, null, null, false, e.Kind); throw new BackendError("unreachable (" + e.Kind + ")", e); }
            catch (LlmAway e)
            {
                Record(slot, requestId, sw, null, null, false, "away");
                Gate.WentAway(e.AwayMessage, e.RetryAfter);
                throw new BackendAway(e.AwayMessage, e.RetryAfter);
            }
            catch (LlmError e)
            {
                Record(slot, requestId, sw, null, null, false, e.Cause);
                string d = string.IsNullOrEmpty(e.Detail) ? "" : ": " + PyStr.Head(e.Detail, 160);
                throw new BackendError(e.Cause + d, e);
            }
            Gate.Ok();
            double ms = sw.Elapsed.TotalMilliseconds;
            var calls = new List<ToolCall>();
            int i = 0;
            foreach (var tc in sr.ToolCalls)
            {
                calls.Add(new ToolCall
                {
                    Id = tc.Id.Length > 0 ? tc.Id : "call_" + i.ToString(CultureInfo.InvariantCulture),
                    Name = tc.Name,
                    Arguments = new JValue(tc.Arguments.ToString()),
                });
                i++;
            }
            var st = new CallStats { Kind = "toolloop", RequestId = requestId, WallS = ms / 1000.0, TtftS = sr.TtftS };
            if (sr.Timings != null && PyJson.Truthy(sr.Timings))
            {
                st.PromptN = LlmWire.PyInt(PyAccess.Get(sr.Timings, "prompt_n", new JValue(0)));
                st.CacheN = LlmWire.PyInt(PyAccess.Get(sr.Timings, "cache_n", new JValue(0)));
                st.PromptMs = LlmWire.PyFloatOf(PyAccess.Get(sr.Timings, "prompt_ms", new JValue(0)));
                st.PredictedN = LlmWire.PyInt(PyAccess.Get(sr.Timings, "predicted_n", new JValue(0)));
                st.PredictedMs = LlmWire.PyFloatOf(PyAccess.Get(sr.Timings, "predicted_ms", new JValue(0)));
            }
            else if (sr.Usage != null)
            {
                st.PromptN = LlmWire.PyInt(PyAccess.Get(sr.Usage, "prompt_tokens", new JValue(0)));
                st.PredictedN = LlmWire.PyInt(PyAccess.Get(sr.Usage, "completion_tokens", new JValue(0)));
            }
            if (CallLog != null) { st.Kind = ToolCallKind.For(slot); st.Slot = slot; st.Turn = slot.HasValue ? ToolCallKind.Turn : null; CallLog.Record(st); }
            ChatStreamReader.Emit(sink, StreamEvent.Done(sr.Text, st));
            return new Reply { Text = sr.Text, ToolCalls = calls, Finish = sr.Finish, Reasoning = sr.Reasoning, Ms = ms, RequestId = requestId };
        }
    }
}

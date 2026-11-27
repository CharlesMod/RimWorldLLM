// Port of core/toolloop/backend.py: the one place the tool loop talks HTTP.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
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
    }

    public interface IBackend
    {
        Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                              int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null);
    }

    /// <summary>A backend that names its endpoint; the capability probe caches by it (Python: getattr(backend, "base"/"model")).</summary>
    public interface IBackendIdentity
    {
        string Base { get; }
        string Model { get; }
    }

    /// <summary>POST {base}/chat/completions. Extra goes into every body (id_slot, cache_prompt...).</summary>
    public sealed class OpenAIBackend : IBackend, IBackendIdentity, IDisposable
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

        public async Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                           int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
        {
            try { Gate.Check(); }
            catch (LlmAway e) { throw new BackendAway(e.AwayMessage, e.RetryAfter); }
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(ApiKey)) headers["Authorization"] = "Bearer " + ApiKey;
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
            HttpReply r;
            try { r = await HttpUtil.PostJsonAsync(Http, Base + "/chat/completions", body, headers, timeout).ConfigureAwait(false); }
            catch (HttpTransportException e) { throw new BackendError("unreachable (" + e.Kind + ")", e); }
            double ms = sw.Elapsed.TotalMilliseconds;
            var y = LlmWire.ParseYielded(r.Status, r.Content, r.Header("Retry-After"));
            if (y != null)
            {
                Gate.WentAway(y.Message, y.RetryAfter);
                throw new BackendAway(y.Message, y.RetryAfter);
            }
            if (r.Status >= 400) throw new BackendError("HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + ": " + PyStr.Head(r.Text, 160));
            Gate.Ok();
            JToken ch, m2;
            try
            {
                ch = PyAccess.Item(PyAccess.Item(r.Json(), "choices"), 0);
                m2 = PyAccess.Get(ch, "message");
            }
            catch (Exception e) when (e is PyJsonException || e is PyKeyError || e is PyIndexError || e is PyTypeError)
            {
                throw new BackendError("reply was not a chat completion", e);
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
            };
        }
    }
}

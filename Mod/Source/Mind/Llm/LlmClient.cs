// Port of agent/rimworldllm_agent/core/llm.py: the LLM client interface, the two wire modes (OpenAI chat and llama.cpp
// native), engine detection, the sampler fields, the "yielded" back-off, and the Gemma turn format.
// strip_thought is NOT here: the text worker owns Core/Think.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>One chat turn: ("system" | "user" | "assistant", text). A (string, string) tuple converts implicitly.</summary>
    public sealed class LlmMessage
    {
        public string Role { get; }
        public string Text { get; }
        public LlmMessage(string role, string text) { Role = role; Text = text; }
        public static implicit operator LlmMessage((string role, string text) t) => new LlmMessage(t.role, t.text);
        public void Deconstruct(out string role, out string text) { role = Role; text = Text; }
        public override string ToString() => "(" + PyJson.StrRepr(Role) + ", " + PyJson.StrRepr(Text) + ")";
    }

    public sealed class Completion
    {
        public string Text { get; }
        public CallStats Stats { get; }
        public Completion(string text, CallStats stats) { Text = text; Stats = stats; }
    }

    public class LlmError : Exception, RimWorldLLM.Mind.Py.IPyNamed
    {
        public virtual string PyName => "LLMError";
        public string Cause { get; }
        public int Status { get; }
        public LlmError(string cause, int status = 0) : base(cause) { Cause = cause; Status = status; }
    }

    /// <summary>The engine answered 503 code "yielded": the model server machine is running a game. Not a fault.</summary>
    public sealed class LlmAway : LlmError
    {
        /// <summary>Python: LLMAway.message (the server's text).</summary>
        public string AwayMessage { get; }
        public double RetryAfter { get; }
        public override string PyName => "LLMAway";
        public LlmAway(string message, double retryAfter = 5.0) : base("yielded", 503) { AwayMessage = message; RetryAfter = retryAfter; }
    }

    /// <summary>The httpx.HTTPError analog: the request did not complete. Kind is the httpx class name
    /// ("ConnectError", "ReadTimeout", ...); IsTimeout is isinstance(e, httpx.TimeoutException).</summary>
    public sealed class HttpTransportException : Exception
    {
        public string Kind { get; }
        public bool IsTimeout => Kind.EndsWith("Timeout", StringComparison.Ordinal);
        public bool IsConnectError => Kind == "ConnectError";
        public HttpTransportException(string kind, string message = null, Exception inner = null) : base(message ?? kind, inner) { Kind = kind; }
    }

    public sealed class HttpReply
    {
        public int Status { get; set; }
        public byte[] Content { get; set; } = new byte[0];
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        /// <summary>httpx's r.text: UTF-8 with replacement characters.</summary>
        public string Text => new UTF8Encoding(false, false).GetString(Content);
        /// <summary>httpx's r.json(): strict JSON; PyJsonException (a ValueError) when it is not.</summary>
        public JToken Json() => PyJson.Loads(Content);
    }

    /// <summary>The one HTTP seam. The timeout is per request (a cancellation token), the client's own timeout is not used.</summary>
    public static class HttpUtil
    {
        public static HttpClient NewClient() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public static Task<HttpReply> GetAsync(HttpClient http, string url, IDictionary<string, string> headers, double timeoutS)
            => SendAsync(http, HttpMethod.Get, url, null, headers, timeoutS);

        /// <summary>POST with `json=body` as httpx 0.28 sends it (compact, UTF-8, Content-Type: application/json).</summary>
        public static Task<HttpReply> PostJsonAsync(HttpClient http, string url, object body, IDictionary<string, string> headers, double timeoutS)
            => SendAsync(http, HttpMethod.Post, url, PyJson.HttpxJson(body), headers, timeoutS);

        public static async Task<HttpReply> SendAsync(HttpClient http, HttpMethod method, string url, byte[] body,
                                                      IDictionary<string, string> headers, double timeoutS)
        {
            using var cts = new CancellationTokenSource();
            if (timeoutS > 0 && !double.IsInfinity(timeoutS)) cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeoutS, 86400.0)));
            using var req = new HttpRequestMessage(method, url);
            if (body != null)
            {
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            if (headers != null)
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            try
            {
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var reply = new HttpReply { Status = (int)resp.StatusCode };
                foreach (var h in resp.Headers.NonValidated) reply.Headers[h.Key] = string.Join(", ", h.Value);
                foreach (var h in resp.Content.Headers.NonValidated) reply.Headers[h.Key] = string.Join(", ", h.Value);
                reply.Content = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                return reply;
            }
            catch (HttpTransportException) { throw; }
            catch (OperationCanceledException e) when (cts.IsCancellationRequested)
            {
                throw new HttpTransportException("ReadTimeout", "timed out", e);
            }
            catch (HttpRequestException e)
            {
                throw new HttpTransportException("ConnectError", e.Message, e);
            }
            catch (System.IO.IOException e)
            {
                throw new HttpTransportException("ReadError", e.Message, e);
            }
        }
    }

    public interface ILlmClient
    {
        /// <summary>stop == null means ["\n"]; an empty list means no stop strings. The per-call temperature is accepted and ignored by HttpLlm.</summary>
        Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                       IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                       int? actor = null, JObject jsonSchema = null);
    }

    /// <summary>What ModelTier needs of a client that may be easyEngine (Python: getattr(llm, "is_engine"), llm.http, llm.base).</summary>
    public interface IEngineClient
    {
        Task<bool> IsEngineAsync();
        /// <summary>GET {base}/engine/v1/status (3 s). The decoded JSON, or null when the server could not be reached, answered non-200, or sent invalid JSON.</summary>
        Task<JToken> FetchEngineStatusAsync();
    }

    public sealed class YieldedInfo
    {
        public string Message;
        public double RetryAfter;
    }

    /// <summary>Back-off after a "yielded" answer: until Until calls are refused without touching the network.
    /// One quiet log line when the model goes away and one when it is back.</summary>
    public sealed class AwayGate
    {
        public double Until;
        public bool Away;
        public string Message = "";
        private readonly Func<double> _clock;
        private readonly LogSink _log;

        public static double MonotonicS() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public AwayGate(Func<double> clock = null, LogSink log = null)
        {
            _clock = clock ?? MonotonicS;
            _log = log ?? ((lvl, name, msg) => LlmLog.Info(name, msg));
        }

        public void Check()
        {
            if (Until != 0.0 && _clock() < Until) throw new LlmAway(Message, Until - _clock());
        }

        public void WentAway(string message, double retryAfter)
        {
            Until = _clock() + retryAfter;
            Message = message;
            if (!Away)
            {
                Away = true;
                // round() of a NaN raises in Python (after Until is set, before the call is recorded)
                if (double.IsNaN(retryAfter)) throw new PyValueError("cannot convert float NaN to integer");
                _log("INFO", "rimworldllm", "model paused on the server: " + message + "; retrying in "
                     + ((long)Math.Round(retryAfter, MidpointRounding.ToEven)).ToString(CultureInfo.InvariantCulture) + "s");
            }
        }

        public void Ok()
        {
            Until = 0.0;
            if (Away)
            {
                Away = false;
                _log("INFO", "rimworldllm", "model is back on the server");
            }
        }
    }

    public static class LlmWire
    {
        public const double AwayCapS = 30.0;

        /// <summary>(message, retry_after seconds) when this is the engine's "yielded" 503 (string code), else null.
        /// A plain 503 has a numeric code (loading) and is not away.</summary>
        public static YieldedInfo ParseYielded(int status, byte[] body, string retryAfterHeader)
        {
            if (status != 503) return null;
            string msg;
            try
            {
                JToken d = PyJson.Loads(body);
                JToken e = d is JObject dobj && dobj.TryGetValue("error", out JToken ev) ? ev : null;
                if (!(e is JObject eo)) return null;
                if (!eo.TryGetValue("code", out JToken code) || code.Type != JTokenType.String || (string)code != "yielded") return null;
                JToken m = eo.TryGetValue("message", out JToken mv) ? mv : null;
                msg = PyJson.Truthy(m) ? PyJson.Str(m) : "paused";
            }
            catch (PyJsonException) { return null; }
            double ra = 5.0;
            if (retryAfterHeader != null && !PyFloat.TryParse(retryAfterHeader, out ra)) ra = 5.0;
            // min(max(ra, 0.0), AWAY_CAP_S) with Python's comparison order (a NaN passes through both)
            if (0.0 > ra) ra = 0.0;
            if (AwayCapS < ra) ra = AwayCapS;
            return new YieldedInfo { Message = msg, RetryAfter = ra };
        }

        public static YieldedInfo ParseYielded(int status, string body, string retryAfterHeader)
            => ParseYielded(status, Encoding.UTF8.GetBytes(body ?? ""), retryAfterHeader);

        /// <summary>Hand-built Gemma turns with thinking off (bench/m0/lib.py raw()).</summary>
        public static string GemmaPrompt(IReadOnlyList<LlmMessage> messages)
        {
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                string r = m.Role == "assistant" ? "model" : m.Role;
                if (r == "model") sb.Append("<|turn>model\n<|channel>thought\n<channel|>").Append(m.Text).Append("<turn|>\n");
                else sb.Append("<|turn>").Append(r).Append('\n').Append(m.Text).Append("<turn|>\n");
            }
            if (messages.Count > 0 && messages[messages.Count - 1].Role != "assistant") sb.Append("<|turn>model\n<|channel>thought\n<channel|>");
            return sb.ToString();
        }

        // Google's generation_config.json, identical for every Gemma 4 build we ship. Used only for a non-engine endpoint.
        public const double Gemma4Temperature = 1.0;
        public const int Gemma4TopK = 64;
        public const double Gemma4TopP = 0.95;

        private static readonly string[] Gemma4Sizes = { "e2b", "e4b", "26b", "a4b", "rpmaxxed" };

        /// <summary>Calls whose output is machine-read JSON or a tool step use the engine's "structured" profile.</summary>
        public static readonly ISet<string> StructuredKinds = new HashSet<string> { "intent", "reflect", "reflection", "tool", "toolloop" };

        private static readonly Regex NonAlnum = new Regex("[^a-z0-9]+", RegexOptions.CultureInvariant);

        public static bool IsKnownGemma4(string model)
        {
            string n = NonAlnum.Replace((model ?? "").ToLowerInvariant(), "");
            return n.Contains("gemma4") && Gemma4Sizes.Any(z => n.Contains(z));
        }

        public static string SamplerProfile(string kind, string grammar = null, JObject jsonSchema = null)
            => (StructuredKinds.Contains(kind) || !string.IsNullOrEmpty(grammar) || (jsonSchema != null && jsonSchema.Count > 0)) ? "structured" : "rp";

        private static string RemoveSuffix(string s, string suf) => s.EndsWith(suf, StringComparison.Ordinal) ? s.Substring(0, s.Length - suf.Length) : s;

        public static string RemoveV1(string b) => RemoveSuffix(b, "/v1");

        public static string EngineStatusUrl(string b) => RemoveV1(b) + "/engine/v1/status";

        /// <summary>Is `baseUrl` easyEngine? true/false when answered; null when the server could not be reached (do not cache that).</summary>
        public static async Task<bool?> DetectEngineAsync(HttpClient http, string baseUrl, IDictionary<string, string> headers = null)
        {
            HttpReply r;
            try { r = await HttpUtil.GetAsync(http, EngineStatusUrl(baseUrl), headers, 3.0).ConfigureAwait(false); }
            catch (HttpTransportException) { return null; }
            if (r.Status != 200) return false;
            JToken d;
            try { d = r.Json(); } catch (PyJsonException) { return false; }
            return d is JObject o && o.ContainsKey("phase") && o.ContainsKey("sampler");
        }

        /// <summary>Request fields for sampling, in Python's dict order. Engine: only what the config forces (null = engine decides).
        /// Other servers: the config's values plus the official Gemma 4 values for any of temperature/top_k/top_p left unset when the
        /// model name is a known Gemma 4 build; for any other model, nothing.</summary>
        public static JObject SamplerFields(LlmConfig cfg, bool engine, string kind = "line")
        {
            var s = new JObject();
            if (cfg.Temperature.HasValue) s["temperature"] = cfg.Temperature.Value;
            if (cfg.TopK.HasValue) s["top_k"] = cfg.TopK.Value;
            if (cfg.TopP.HasValue) s["top_p"] = cfg.TopP.Value;
            if (cfg.MinP.HasValue) s["min_p"] = cfg.MinP.Value;
            if (cfg.RepeatPenalty.HasValue) s["repeat_penalty"] = cfg.RepeatPenalty.Value;
            if (!engine && IsKnownGemma4(cfg.Model))
            {
                if (!s.ContainsKey("temperature")) s["temperature"] = Gemma4Temperature;
                if (!s.ContainsKey("top_k")) s["top_k"] = Gemma4TopK;
                if (!s.ContainsKey("top_p")) s["top_p"] = Gemma4TopP;
            }
            if (cfg.LineDry && kind == "line")
            {
                s["dry_multiplier"] = cfg.DryMultiplier;
                s["dry_base"] = cfg.DryBase;
                s["dry_allowed_length"] = cfg.DryAllowedLength;
                s["dry_penalty_last_n"] = cfg.DryPenaltyLastN;
            }
            return s;
        }

        /// <summary>Return the base ending in /v1 (no trailing slash).</summary>
        public static string NormaliseBase(string url)
        {
            string u = PyFloat.PyStrip(url).TrimEnd('/');
            if (!(u.StartsWith("http://", StringComparison.Ordinal) || u.StartsWith("https://", StringComparison.Ordinal))) u = "http://" + u;
            foreach (var suf in new[] { "/chat/completions", "/completions", "/models" })
                if (u.EndsWith(suf, StringComparison.Ordinal)) u = u.Substring(0, u.Length - suf.Length);
            if (!u.EndsWith("/v1", StringComparison.Ordinal)) u += "/v1";
            return u;
        }

        /// <summary>int(x) on a decoded JSON number or numeric string (TypeError / ValueError otherwise).</summary>
        internal static long PyInt(JToken t)
        {
            switch (t == null ? JTokenType.Null : t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float:
                    {
                        double d = (double)t;
                        if (double.IsNaN(d)) throw new PyValueError("cannot convert float NaN to integer");
                        if (double.IsInfinity(d)) throw new PyException("OverflowError", "cannot convert float infinity to integer");
                        return (long)Math.Truncate(d);
                    }
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.String:
                    if (long.TryParse(PyFloat.PyStrip((string)t).Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return v;
                    throw new PyValueError("invalid literal for int() with base 10: " + PyJson.StrRepr((string)t));
                default:
                    throw new PyTypeError("int() argument must be a string, a bytes-like object or a real number, not '" + PyAccess.TypeName(t) + "'");
            }
        }

        /// <summary>float(x) on a decoded JSON number or numeric string.</summary>
        internal static double PyFloatOf(JToken t)
        {
            switch (t == null ? JTokenType.Null : t.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String:
                    if (PyFloat.TryParse((string)t, out double d)) return d;
                    throw new PyValueError("could not convert string to float: " + PyJson.StrRepr((string)t));
                default:
                    throw new PyTypeError("float() argument must be a string or a real number, not '" + PyAccess.TypeName(t) + "'");
            }
        }
    }

    public class HttpLlm : ILlmClient, IEngineClient
    {
        public LlmConfig Cfg { get; }
        public CallLog CallLog { get; }
        public HttpClient Http { get; }
        public string Base { get; }
        /// <summary>Python: _engine. null = not detected yet.</summary>
        public bool? Engine { get; set; }
        public AwayGate Gate { get; }

        private readonly bool _ownsHttp;

        public HttpLlm(LlmConfig cfg, CallLog calllog = null, HttpClient client = null, LogSink log = null, Func<double> clock = null)
        {
            Cfg = cfg;
            CallLog = calllog ?? new CallLog(null);
            _ownsHttp = client == null;
            Http = client ?? HttpUtil.NewClient();
            Base = LlmWire.NormaliseBase(cfg.BaseUrl);
            Engine = cfg.Engine;
            Gate = new AwayGate(clock, log);
        }

        /// <summary>For a derived client that shares another's pieces (SamplerRoute).</summary>
        protected HttpLlm(HttpLlm shareFrom) : this(shareFrom.Cfg, shareFrom.CallLog, shareFrom.Http) { }

        public void Dispose() { if (_ownsHttp) Http.Dispose(); }

        public Dictionary<string, string> Headers()
        {
            var h = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(Cfg.ApiKey)) h["Authorization"] = "Bearer " + Cfg.ApiKey;
            return h;
        }

        public virtual async Task<bool> IsEngineAsync()
        {
            if (Engine == null)
            {
                Engine = await LlmWire.DetectEngineAsync(Http, Base, Headers()).ConfigureAwait(false);
                return Engine == true;
            }
            return Engine.Value;
        }

        public async Task<JToken> FetchEngineStatusAsync()
        {
            try
            {
                var r = await HttpUtil.GetAsync(Http, LlmWire.EngineStatusUrl(Base), Headers(), 3.0).ConfigureAwait(false);
                return r.Status == 200 ? r.Json() : null;
            }
            catch (HttpTransportException) { return null; }
            catch (PyJsonException) { return null; }
        }

        /// <summary>(body fields, extra headers). The per-call temperature is not used: the engine's profile for the call's kind decides,
        /// or the config when it forces a value.</summary>
        protected virtual async Task<(JObject fields, Dictionary<string, string> headers)> SamplingAsync(string kind, string grammar, JObject jsonSchema)
        {
            bool eng = await IsEngineAsync().ConfigureAwait(false);
            var fields = LlmWire.SamplerFields(Cfg, eng, kind);
            var h = new Dictionary<string, string>();
            if (eng) h["X-Easy-Sampler"] = LlmWire.SamplerProfile(kind, grammar, jsonSchema);
            return (fields, h);
        }

        private static string TextOf(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            if (t.Type == JTokenType.String) return (string)t;
            return PyJson.Truthy(t) ? PyJson.Str(t) : "";
        }

        public virtual async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                                            IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                                            int? actor = null, JObject jsonSchema = null)
        {
            IReadOnlyList<string> stopL = stop ?? new[] { "\n" };
            Gate.Check();                           // backing off after "yielded": no request, no calllog record
            var sw = Stopwatch.StartNew();
            var st = new CallStats { Kind = kind, Slot = slot, Actor = actor };
            JToken tim;
            string text;
            try
            {
                var (samp, sh) = await SamplingAsync(kind, grammar, jsonSchema).ConfigureAwait(false);
                var hdr = Headers();
                foreach (var kv in sh) hdr[kv.Key] = kv.Value;
                if (Cfg.Mode == "native")
                {
                    var body = new JObject
                    {
                        ["prompt"] = LlmWire.GemmaPrompt(messages),
                        ["n_predict"] = maxTokens,
                        ["stop"] = new JArray(stopL.Select(x => (object)x).ToArray()),
                        ["cache_prompt"] = true,
                    };
                    foreach (var p in samp.Properties()) body[p.Name] = p.Value;
                    if (slot.HasValue) body["id_slot"] = slot.Value;
                    if (!string.IsNullOrEmpty(grammar)) body["grammar"] = grammar;
                    if (jsonSchema != null && jsonSchema.Count > 0) body["json_schema"] = jsonSchema.DeepClone();
                    var r = await HttpUtil.PostJsonAsync(Http, LlmWire.RemoveV1(Base) + "/completion", body, hdr, Cfg.TimeoutS).ConfigureAwait(false);
                    JToken data = Ok(r);
                    Gate.Ok();
                    text = TextOf(PyAccess.Get(data, "content", new JValue("")));
                    tim = PyAccess.Get(data, "timings", new JObject());
                }
                else
                {
                    var msgs = new JArray();
                    foreach (var m in messages) msgs.Add(new JObject { ["role"] = m.Role, ["content"] = m.Text });
                    var body = new JObject
                    {
                        ["messages"] = msgs,
                        ["max_tokens"] = maxTokens,
                        ["cache_prompt"] = true,
                        ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
                    };
                    foreach (var p in samp.Properties()) body[p.Name] = p.Value;
                    if (stopL.Count > 0) body["stop"] = new JArray(stopL.Select(x => (object)x).ToArray());
                    if (!string.IsNullOrEmpty(Cfg.Model)) body["model"] = Cfg.Model;
                    if (slot.HasValue) body["id_slot"] = slot.Value;
                    if (!string.IsNullOrEmpty(grammar)) body["grammar"] = grammar;
                    if (jsonSchema != null && jsonSchema.Count > 0) body["json_schema"] = jsonSchema.DeepClone();
                    var r = await HttpUtil.PostJsonAsync(Http, Base + "/chat/completions", body, hdr, Cfg.TimeoutS).ConfigureAwait(false);
                    JToken data = Ok(r);
                    Gate.Ok();
                    JToken content = PyAccess.Get(PyAccess.Item(PyAccess.Item(PyAccess.Item(data, "choices"), 0), "message"), "content");
                    text = TextOf(content);
                    tim = PyAccess.Get(data, "timings", new JObject());
                    if (!PyJson.Truthy(tim) && data is JObject dobj && dobj.ContainsKey("usage"))
                    {
                        JToken usage = dobj["usage"];
                        st.PromptN = LlmWire.PyInt(PyAccess.Get(usage, "prompt_tokens", new JValue(0)));
                        st.PredictedN = LlmWire.PyInt(PyAccess.Get(usage, "completion_tokens", new JValue(0)));
                    }
                }
            }
            catch (HttpTransportException e)
            {
                st.Ok = false; st.Error = e.Kind;
                st.WallS = sw.Elapsed.TotalSeconds;
                CallLog.Record(st);
                throw new LlmError("server unreachable (" + e.Kind + ")");
            }
            catch (LlmError e)
            {
                if (e is LlmAway away) Gate.WentAway(away.AwayMessage, away.RetryAfter);
                st.Ok = false; st.Error = e.Cause;
                st.WallS = sw.Elapsed.TotalSeconds;
                CallLog.Record(st);
                throw;
            }
            st.WallS = sw.Elapsed.TotalSeconds;
            if (PyJson.Truthy(tim))
            {
                st.PromptN = LlmWire.PyInt(PyAccess.Get(tim, "prompt_n", new JValue(0)));
                st.CacheN = LlmWire.PyInt(PyAccess.Get(tim, "cache_n", new JValue(0)));
                st.PromptMs = LlmWire.PyFloatOf(PyAccess.Get(tim, "prompt_ms", new JValue(0)));
                st.PredictedN = LlmWire.PyInt(PyAccess.Get(tim, "predicted_n", new JValue(0)));
                st.PredictedMs = LlmWire.PyFloatOf(PyAccess.Get(tim, "predicted_ms", new JValue(0)));
            }
            CallLog.Record(st);
            return new Completion(text, st);
        }

        private static JToken Ok(HttpReply r)
        {
            var y = LlmWire.ParseYielded(r.Status, r.Content, r.Header("Retry-After"));
            if (y != null) throw new LlmAway(y.Message, y.RetryAfter);
            if (r.Status >= 400) throw new LlmError("HTTP " + r.Status.ToString(CultureInfo.InvariantCulture), r.Status);
            try { return r.Json(); }
            catch (PyJsonException) { throw new LlmError("reply was not JSON", r.Status); }
        }
    }
}

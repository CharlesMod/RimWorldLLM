using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    public enum Light { Green, Amber, Red }

    public enum ProbeCode
    {
        Ok,               // all good
        OkFixedBase,      // worked after adding or removing /v1; Arg = the base that works
        Loading,          // server answered 503: the model is still loading
        Slow,             // reply took too long; Arg = ms
        ReasoningText,    // reasoning text came back in the output
        NoUrl,
        BadUrl,
        NothingListening,
        KeyRejected,
        WrongBase,        // 404 on /models either way; Arg = the suggestion, e.g. ".../v1"
        ModelNotFound,    // Arg = model
        Tls,
        NotJson,
        HttpError,        // Arg = status
        Timeout,          // no reply at all within the limit
        Other             // Arg = exception message
    }

    public sealed class ProbeResult
    {
        public Light Light;
        public ProbeCode Code;
        public string Arg = "";
        public string EffectiveBase = "";   // the base URL that worked (after any /v1 fix)
        public long ElapsedMs;              // the chat completion, when it ran

        public static ProbeResult Make(Light l, ProbeCode c, string arg = "") { return new ProbeResult { Light = l, Code = c, Arg = arg ?? "" }; }
    }

    /// <summary>One HTTP exchange as the classifier sees it. Status 0 with an Error set means no reply.</summary>
    public sealed class HttpOutcome
    {
        public int Status;
        public string Body = "";
        public Exception Error;
        public long Ms;
    }

    public enum Transport { Ok, Refused, Tls, Timeout, Other }

    /// <summary>
    /// The Test button's logic for an OpenAI-compatible endpoint. No game references, no UI: the transport is injected
    /// so the classification is testable without a server. Call Run on a worker thread.
    /// </summary>
    public static class LlmProbe
    {
        public const long SlowMs = 8000;
        public const int ChatTimeoutMs = 45000;

        // ---- URL handling ----------------------------------------------------------------------------------

        /// <summary>
        /// Trim, add http:// when there is no scheme, drop query/fragment and trailing slashes, and cut a pasted
        /// ".../chat/completions" or ".../models" back to the base. Null when it is not a usable http(s) URL.
        /// </summary>
        public static string NormalizeBase(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string s = input.Trim();
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "http://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            if (u.Scheme != "http" && u.Scheme != "https") return null;
            if (string.IsNullOrEmpty(u.Host)) return null;
            string path = u.AbsolutePath.TrimEnd('/');
            foreach (var tail in new[] { "/chat/completions", "/completions", "/models" })
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { path = path.Substring(0, path.Length - tail.Length).TrimEnd('/'); break; }
            return u.GetLeftPart(UriPartial.Authority) + path;
        }

        /// <summary>The same base with /v1 added, or removed when it already ends in /v1.</summary>
        public static string ToggleV1(string b)
        {
            if (b == null) return null;
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return b.Substring(0, b.Length - 3);
            return b + "/v1";
        }

        /// <summary>The server root without a trailing /v1 (where llama.cpp serves /health).</summary>
        public static string RootOf(string b)
        {
            return b != null && b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b.Substring(0, b.Length - 3) : b;
        }

        // ---- classification --------------------------------------------------------------------------------

        public static Transport ClassifyException(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is AuthenticationException) return Transport.Tls;
                if (x is TaskCanceledException || x is TimeoutException || x is OperationCanceledException) return Transport.Timeout;
                var se = x as SocketException;
                if (se != null)
                {
                    if (se.SocketErrorCode == SocketError.TimedOut) return Transport.Timeout;
                    return Transport.Refused;   // refused, unreachable, host not found: nothing is answering there
                }
                string m = x.Message ?? "";
                if (m.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0) return Transport.Tls;
                if (m.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Name or service not known", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0) return Transport.Refused;
            }
            return Transport.Other;
        }

        static ProbeResult FromTransport(HttpOutcome o)
        {
            switch (ClassifyException(o.Error))
            {
                case Transport.Tls: return ProbeResult.Make(Light.Red, ProbeCode.Tls);
                case Transport.Timeout: return ProbeResult.Make(Light.Amber, ProbeCode.Timeout);
                case Transport.Refused: return ProbeResult.Make(Light.Red, ProbeCode.NothingListening);
                default: return ProbeResult.Make(Light.Red, ProbeCode.Other, o.Error != null ? o.Error.GetBaseException().Message : "");
            }
        }

        static JToken TryJson(string body) { try { return JToken.Parse(body); } catch { return null; } }

        /// <summary>
        /// Judges the GET /models reply. Null means the step passed. A 404 is reported as WrongBase so the caller can
        /// retry with /v1 toggled before giving up.
        /// </summary>
        public static ProbeResult ClassifyModels(HttpOutcome o, string model)
        {
            if (o.Error != null) return FromTransport(o);
            if (o.Status == 401 || o.Status == 403) return ProbeResult.Make(Light.Red, ProbeCode.KeyRejected);
            if (o.Status == 404) return ProbeResult.Make(Light.Red, ProbeCode.WrongBase);
            if (o.Status == 503) return ProbeResult.Make(Light.Amber, ProbeCode.Loading);
            if (o.Status < 200 || o.Status >= 300) return ProbeResult.Make(Light.Red, ProbeCode.HttpError, o.Status.ToString());
            var j = TryJson(o.Body) as JObject;
            if (j == null) return ProbeResult.Make(Light.Red, ProbeCode.NotJson);
            var data = j["data"] as JArray;
            if (data == null) data = j["models"] as JArray;     // llama.cpp also lists under "models"
            if (data == null) return ProbeResult.Make(Light.Red, ProbeCode.NotJson);
            if (!string.IsNullOrWhiteSpace(model) && data.Count > 0)
            {
                bool found = false;
                foreach (var m in data)
                {
                    string id = (string)(m["id"] ?? m["model"] ?? m["name"]);
                    if (id != null && string.Equals(id.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (!found) return ProbeResult.Make(Light.Red, ProbeCode.ModelNotFound, model.Trim());
            }
            return null;
        }

        /// <summary>Judges the 8-token chat completion.</summary>
        public static ProbeResult ClassifyChat(HttpOutcome o, string model)
        {
            if (o.Error != null) return FromTransport(o);
            if (o.Status == 401 || o.Status == 403) return ProbeResult.Make(Light.Red, ProbeCode.KeyRejected);
            if (o.Status == 404)
            {
                // Servers answer 404 for an unknown model on /chat/completions as well.
                var e = TryJson(o.Body);
                string msg = e != null ? ((string)e.SelectToken("error.message") ?? "") : "";
                if (msg.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0) return ProbeResult.Make(Light.Red, ProbeCode.ModelNotFound, model ?? "");
                return ProbeResult.Make(Light.Red, ProbeCode.WrongBase);
            }
            if (o.Status == 503) return ProbeResult.Make(Light.Amber, ProbeCode.Loading);
            if (o.Status < 200 || o.Status >= 300)
            {
                var e = TryJson(o.Body);
                string msg = e != null ? ((string)e.SelectToken("error.message") ?? "") : "";
                if (o.Status == 400 && msg.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 && msg.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ProbeResult.Make(Light.Red, ProbeCode.ModelNotFound, model ?? "");
                return ProbeResult.Make(Light.Red, ProbeCode.HttpError, o.Status.ToString());
            }
            var j = TryJson(o.Body) as JObject;
            var choices = j != null ? j["choices"] as JArray : null;
            if (choices == null || choices.Count == 0) return ProbeResult.Make(Light.Red, ProbeCode.NotJson);
            var msgTok = choices[0]["message"];
            string content = msgTok != null ? ((string)msgTok["content"] ?? "") : "";
            string reasoning = msgTok != null ? ((string)msgTok["reasoning_content"] ?? (string)msgTok["reasoning"] ?? "") : "";
            if (reasoning.Trim().Length > 0 || content.IndexOf("<think", StringComparison.OrdinalIgnoreCase) >= 0)
                return ProbeResult.Make(Light.Amber, ProbeCode.ReasoningText);
            if (o.Ms > SlowMs) return ProbeResult.Make(Light.Amber, ProbeCode.Slow, o.Ms.ToString());
            return null;
        }

        // ---- the run ---------------------------------------------------------------------------------------

        public delegate HttpOutcome Send(string method, string url, string jsonBody, string apiKey, int timeoutMs);

        public static string ChatBody(string model)
        {
            return new JObject
            {
                ["model"] = model ?? "",
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "Reply with one word: ready." }),
                ["max_tokens"] = 8,
                ["temperature"] = 0,
                ["stream"] = false,
                ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },   // llama.cpp: thinking off
                ["reasoning_effort"] = "none"                                              // OpenAI-style servers
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Runs the whole check with the given transport. Blocking: call from a worker thread.</summary>
        public static ProbeResult Run(string baseUrlInput, string model, string apiKey, Send send)
        {
            if (string.IsNullOrWhiteSpace(baseUrlInput)) return ProbeResult.Make(Light.Red, ProbeCode.NoUrl);
            string b = NormalizeBase(baseUrlInput);
            if (b == null) return ProbeResult.Make(Light.Red, ProbeCode.BadUrl);
            bool fixedBase = false;

            var o = send("GET", b + "/models", null, apiKey, 15000);
            var r = ClassifyModels(o, model);
            if (r != null && r.Code == ProbeCode.WrongBase)
            {
                string alt = ToggleV1(b);
                var o2 = send("GET", alt + "/models", null, apiKey, 15000);
                var r2 = ClassifyModels(o2, model);
                if (r2 == null || r2.Code != ProbeCode.WrongBase)
                {
                    b = alt; fixedBase = true; r = r2; o = o2;
                }
                else
                {
                    // 404 both ways. A loading llama.cpp answers /health with 503 before its API is up.
                    var h = send("GET", RootOf(b) + "/health", null, apiKey, 8000);
                    if (h.Error == null && h.Status == 503) return ProbeResult.Make(Light.Amber, ProbeCode.Loading);
                    return ProbeResult.Make(Light.Red, ProbeCode.WrongBase, b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? RootOf(b) : b + "/v1");
                }
            }
            if (r != null)
            {
                if (r.Code == ProbeCode.Loading) { r.EffectiveBase = b; return r; }
                return r;
            }

            var c = send("POST", b + "/chat/completions", ChatBody(model), apiKey, ChatTimeoutMs);
            var cr = ClassifyChat(c, model);
            if (cr != null)
            {
                cr.EffectiveBase = b; cr.ElapsedMs = c.Ms;
                return cr;
            }
            var ok = ProbeResult.Make(Light.Green, fixedBase ? ProbeCode.OkFixedBase : ProbeCode.Ok, fixedBase ? b : "");
            ok.EffectiveBase = b; ok.ElapsedMs = c.Ms;
            return ok;
        }

        // ---- real transport ---------------------------------------------------------------------------------

        static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public static HttpOutcome HttpSend(string method, string url, string jsonBody, string apiKey, int timeoutMs)
        {
            var o = new HttpOutcome();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(new HttpMethod(method), url))
                {
                    if (!string.IsNullOrEmpty(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using (var resp = Client.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                    {
                        o.Status = (int)resp.StatusCode;
                        o.Body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception e) { o.Error = e; }
            o.Ms = sw.ElapsedMilliseconds;
            return o;
        }

        /// <summary>The result of the most recent RunAsync; null while running or before the first run.</summary>
        static volatile ProbeResult _last;
        static int _running;
        public static ProbeResult Last { get { return _last; } }
        public static bool IsRunning { get { return Volatile.Read(ref _running) == 1; } }

        /// <summary>Starts the check on the thread pool. The UI polls Last and IsRunning each frame; nothing here touches the game.</summary>
        public static bool RunAsync(string baseUrl, string model, string apiKey)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            _last = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ProbeResult r;
                try { r = Run(baseUrl, model, apiKey, HttpSend); }
                catch (Exception e) { r = ProbeResult.Make(Light.Red, ProbeCode.Other, e.GetBaseException().Message); }
                _last = r;
                Volatile.Write(ref _running, 0);
            });
            return true;
        }
    }
}

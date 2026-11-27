// Port of core/endpoint_test.py: the Test-button logic. Normalise the URL, retry once with/without /v1 on 404, GET /models,
// an 8-token completion with thinking off, timed. 503 = loading (amber). One cause line per failure.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public sealed class EndpointResult
    {
        public string Status;               // green | amber | red
        public string Cause;                // one line
        public string BaseUrl = "";
        public List<string> Models = new List<string>();
        public double? CompletionMs;
        public double? ModelMs;
        public bool Ok => Status == "green";
        public EndpointResult(string status, string cause, string baseUrl = "", List<string> models = null, double? completionMs = null, double? modelMs = null)
        {
            Status = status; Cause = cause; BaseUrl = baseUrl; Models = models ?? new List<string>(); CompletionMs = completionMs; ModelMs = modelMs;
        }
    }

    public static class EndpointCheck
    {
        private static (string status, string cause) Cause(HttpReply r, HttpTransportException exc, string what)
        {
            if (exc != null)
            {
                if (exc.IsConnectError) return ("red", what + ": connection refused or host unreachable; is the server running at that address?");
                if (exc.IsTimeout) return ("red", what + ": timed out; the server is too slow or blocked");
                return ("red", what + ": " + exc.Kind);
            }
            int c = r.Status;
            if (c == 503) return ("amber", what + ": 503, the model is still loading; try again in a few seconds");
            if (c == 401 || c == 403) return ("red", what + ": " + c + ", the API key is missing or wrong");
            if (c == 404) return ("red", what + ": 404, not an OpenAI-compatible endpoint (check the URL, with or without /v1)");
            if (c == 400) return ("red", what + ": 400, the server rejected the request (" + PyFloat.PyStrip(FirstCodePoints(r.Text, 80)) + ")");
            return ("red", what + ": HTTP " + c.ToString(CultureInfo.InvariantCulture));
        }

        private static string FirstCodePoints(string s, int n)
        {
            int i = 0, cps = 0;
            while (i < s.Length && cps < n) { i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1; cps++; }
            return s.Substring(0, i);
        }

        /// <summary>Python test_endpoint (renamed so test runners do not pick it up). `client` null = make one (and dispose it).</summary>
        public static async Task<EndpointResult> TestEndpointAsync(LlmConfig cfg, HttpClient client = null, double timeoutS = 20.0)
        {
            bool own = client == null;
            HttpClient http = client ?? HttpUtil.NewClient();
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(cfg.ApiKey)) headers["Authorization"] = "Bearer " + cfg.ApiKey;
            string b = LlmWire.NormaliseBase(cfg.BaseUrl);
            try
            {
                HttpReply r = null; HttpTransportException exc = null;
                var sw = Stopwatch.StartNew();
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { r = await HttpUtil.GetAsync(http, b + "/models", headers, timeoutS).ConfigureAwait(false); exc = null; }
                    catch (HttpTransportException e) { r = null; exc = e; }
                    if (r != null && r.Status == 404 && attempt == 0)
                    {
                        b = b.EndsWith("/v1", StringComparison.Ordinal) ? b.Substring(0, b.Length - 3) : b + "/v1";
                        continue;
                    }
                    break;
                }
                double modelMs = sw.Elapsed.TotalMilliseconds;
                if (exc != null || r.Status >= 400)
                {
                    var (st, cause) = Cause(r, exc, "GET /models");
                    return new EndpointResult(st, cause, b);
                }
                var models = new List<string>();
                try
                {
                    JToken data = PyAccess.Get(r.Json(), "data", new JArray());
                    if (data is JArray arr)
                    {
                        var tmp = new List<string>();
                        foreach (var m in arr)
                        {
                            JToken id = PyAccess.Get(m, "id", new JValue(""));
                            tmp.Add(id.Type == JTokenType.String ? (string)id : PyJson.Str(id));
                        }
                        models = tmp;
                    }
                    else if (!(data.Type == JTokenType.String || data is JObject))
                        throw new PyTypeError("'" + PyAccess.TypeName(data) + "' object is not iterable");   // not caught in Python either
                }
                catch (PyJsonException) { }
                catch (PyAttributeError) { }
                if (!string.IsNullOrEmpty(cfg.Model) && models.Count > 0 && !models.Contains(cfg.Model))
                    return new EndpointResult("red", "model '" + cfg.Model + "' is not served here (server has: " + string.Join(", ", models.Take(3)) + ")",
                                              b, models, null, modelMs);
                var body = new JObject
                {
                    ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "Say hi." }),
                    ["max_tokens"] = 8,
                    ["temperature"] = 0,
                    ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
                };
                if (!string.IsNullOrEmpty(cfg.Model) || models.Count > 0) body["model"] = !string.IsNullOrEmpty(cfg.Model) ? cfg.Model : models[0];
                sw.Restart();
                try { r = await HttpUtil.PostJsonAsync(http, b + "/chat/completions", body, headers, timeoutS).ConfigureAwait(false); exc = null; }
                catch (HttpTransportException e) { r = null; exc = e; }
                double ms = sw.Elapsed.TotalMilliseconds;
                if (exc != null || r.Status >= 400)
                {
                    var (st, cause) = Cause(r, exc, "completion");
                    return new EndpointResult(st, cause, b, models, null, modelMs);
                }
                try
                {
                    JToken msg = PyAccess.Item(PyAccess.Item(PyAccess.Item(r.Json(), "choices"), 0), "message");
                    PyAccess.Get(msg, "content");
                }
                catch (PyJsonException) { return new EndpointResult("red", "completion: reply was not an OpenAI chat completion", b, models, ms, modelMs); }
                catch (PyKeyError) { return new EndpointResult("red", "completion: reply was not an OpenAI chat completion", b, models, ms, modelMs); }
                catch (PyIndexError) { return new EndpointResult("red", "completion: reply was not an OpenAI chat completion", b, models, ms, modelMs); }
                return new EndpointResult("green", "ok: " + models.Count + " model(s), 8-token completion in " + Math.Round(ms, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture) + " ms",
                                          b, models, ms, modelMs);
            }
            finally { if (own) http.Dispose(); }
        }
    }
}

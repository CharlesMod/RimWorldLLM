// NEW (the narrator can look at the screen): does the LLM side take images? The gate for the look tool (docs/VIVARIUM.md, Vision).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>Can the model behind this endpoint read pictures?</summary>
    public interface IVisionGate
    {
        Task<bool> OpenAsync();
    }

    /// <summary>Engine mode (easyEngine answers GET /engine/v1/status): its status JSON has "vision": true. Own endpoint (llama-server):
    /// GET /props has modalities.vision == true. Anything else, including unreachable, is false. A definite answer is cached for
    /// CacheS seconds; an unreachable server is not cached.</summary>
    public sealed class VisionProbe : IVisionGate
    {
        public const double CacheS = 60.0;
        private readonly HttpClient _http;
        private readonly string _base;
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();
        private readonly Func<double> _clock;
        private bool _have;
        private bool _value;
        private double _until;
        public int Probes;

        /// <summary>baseUrl as the LLM base (with or without /v1).</summary>
        public VisionProbe(HttpClient http, string baseUrl, string apiKey = "", Func<double> clock = null)
        {
            _http = http; _base = baseUrl ?? "";
            if (!string.IsNullOrEmpty(apiKey)) _headers["Authorization"] = "Bearer " + apiKey;
            _clock = clock ?? AwayGate.MonotonicS;
        }

        public async Task<bool> OpenAsync()
        {
            double now = _clock();
            if (_have && now < _until) return _value;
            Probes++;
            bool? answer;
            try { answer = await ProbeAsync().ConfigureAwait(false); }
            catch (Exception) { answer = null; }
            if (answer == null) { _have = false; return false; }
            _value = answer.Value; _have = true; _until = now + CacheS;
            return _value;
        }

        /// <summary>true/false when the server answered; null when it could not be reached.</summary>
        private async Task<bool?> ProbeAsync()
        {
            HttpReply r;
            try { r = await HttpUtil.GetAsync(_http, LlmWire.EngineStatusUrl(_base), _headers, 3.0).ConfigureAwait(false); }
            catch (HttpTransportException) { return null; }
            if (r.Status == 200)
            {
                JToken d = null;
                try { d = r.Json(); } catch (PyJsonException) { }
                if (d is JObject o && o.ContainsKey("phase") && o.ContainsKey("sampler"))
                    return o.TryGetValue("vision", out JToken v) && v.Type == JTokenType.Boolean && (bool)v;      // an engine: only its own word counts
            }
            try { r = await HttpUtil.GetAsync(_http, LlmWire.RemoveV1(_base.TrimEnd('/')) + "/props", _headers, 3.0).ConfigureAwait(false); }
            catch (HttpTransportException) { return false; }
            if (r.Status != 200) return false;
            try
            {
                var p = r.Json() as JObject;
                var m = p?["modalities"] as JObject;
                return m != null && m.TryGetValue("vision", out JToken v) && v.Type == JTokenType.Boolean && (bool)v;
            }
            catch (PyJsonException) { return false; }
        }
    }
}

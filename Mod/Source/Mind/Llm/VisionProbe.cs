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

    /// <summary>How a picture reaches the model.</summary>
    public enum LookPath { None, Direct, Engine }

    /// <summary>The path rules: an engine that offers POST /engine/v1/look (look: true) gets EngineLookClient, local or remote. Direct (the
    /// picture rides in the steward's own chat request) is for a LOCAL engine with vision and no look, or an own endpoint that is not
    /// an easyEngine and whose /props shows vision. Anything else: no look tool.</summary>
    public static class LookRoute
    {
        public static LookPath Choose(bool isEngine, bool look, bool vision, bool local, bool propsVision)
        {
            if (isEngine)
            {
                if (look) return LookPath.Engine;
                return vision && local ? LookPath.Direct : LookPath.None;
            }
            return propsVision ? LookPath.Direct : LookPath.None;
        }

        public static bool IsLocalBase(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out Uri u)) return false;
            string h = u.Host.Trim('[', ']').ToLowerInvariant();
            return h == "localhost" || h == "::1" || h == "127.0.0.1" || h.StartsWith("127.", StringComparison.Ordinal);
        }
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
        private LookPath _value;
        private double _until;
        public int Probes;
        /// <summary>The path the last definite probe chose (None before any, and when unreachable).</summary>
        public LookPath Path => _have ? _value : LookPath.None;

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
            if (_have && now < _until) return _value != LookPath.None;
            Probes++;
            LookPath? answer;
            try { answer = await ProbeAsync().ConfigureAwait(false); }
            catch (Exception) { answer = null; }
            if (answer == null) { _have = false; return false; }
            _value = answer.Value; _have = true; _until = now + CacheS;
            return _value != LookPath.None;
        }

        /// <summary>true/false when the server answered; null when it could not be reached.</summary>
        private async Task<LookPath?> ProbeAsync()
        {
            HttpReply r;
            try { r = await HttpUtil.GetAsync(_http, LlmWire.EngineStatusUrl(_base), _headers, 3.0).ConfigureAwait(false); }
            catch (HttpTransportException) { return null; }
            if (r.Status == 200)
            {
                JToken d = null;
                try { d = r.Json(); } catch (PyJsonException) { }
                if (d is JObject o && o.ContainsKey("phase") && o.ContainsKey("sampler"))
                    return LookRoute.Choose(true, Flag(o, "look"), Flag(o, "vision"), LookRoute.IsLocalBase(_base), false);      // an engine: only its own word counts
            }
            try { r = await HttpUtil.GetAsync(_http, LlmWire.RemoveV1(_base.TrimEnd('/')) + "/props", _headers, 3.0).ConfigureAwait(false); }
            catch (HttpTransportException) { return LookPath.None; }
            if (r.Status != 200) return LookPath.None;
            try
            {
                var p = r.Json() as JObject;
                var m = p?["modalities"] as JObject;
                return LookRoute.Choose(false, false, false, LookRoute.IsLocalBase(_base), Flag(m, "vision"));
            }
            catch (PyJsonException) { return LookPath.None; }
        }

        private static bool Flag(JObject o, string k) => o != null && o.TryGetValue(k, out JToken v) && v.Type == JTokenType.Boolean && (bool)v;
    }
}

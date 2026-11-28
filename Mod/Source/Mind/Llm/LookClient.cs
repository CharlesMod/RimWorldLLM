// NEW (the narrator can look at the screen): the one seam between a captured picture and the model.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>One captured picture and what the steward wants from it.</summary>
    public sealed class LookRequest
    {
        public string Mime = "image/jpeg";
        public string B64 = "";
        /// <summary>close, base or wide.</summary>
        public string Kind = "base";
        /// <summary>The legend line the steward gets (what the frame covers); also given to an engine look as the label.</summary>
        public string Label = "";
        /// <summary>The steward's reason for looking, used as the question by an engine look.</summary>
        public string Question = "";
    }

    /// <summary>What came of a look: either a picture part to attach to the steward's next request (direct path), or the model's answer
    /// about the picture as text (an engine look). Null from LookAsync: the picture could not be used (fall back to text).</summary>
    public sealed class LookOutcome
    {
        public JObject Part;
        public string Answer;
    }

    /// <summary>Whether the model reads pictures, and how a captured picture reaches the model. Capture, camera moves, when to look and
    /// turning answers into actions stay outside it, so the direct path can switch to easyEngine's POST /engine/v1/look without
    /// touching them.</summary>
    public interface ILookClient : IVisionGate
    {
        Task<LookOutcome> LookAsync(LookRequest r);
    }

    /// <summary>Today's path (the engine has no /look yet): the gate is the endpoint probe (VisionProbe) and the picture, as the game sized
    /// it per shot kind, goes into the steward's own next request as an OpenAI image_url data URI part. Local endpoints only: a paired
    /// (remote) mod must use the engine look.</summary>
    public sealed class DirectLookClient : ILookClient
    {
        private readonly IVisionGate _gate;
        public DirectLookClient(IVisionGate gate) { _gate = gate; }
        public Task<bool> OpenAsync() => _gate == null ? Task.FromResult(false) : _gate.OpenAsync();
        public Task<LookOutcome> LookAsync(LookRequest r)
            => Task.FromResult(r == null || string.IsNullOrEmpty(r.B64) ? null
                : new LookOutcome { Part = ImageParts.Part(string.IsNullOrEmpty(r.Mime) ? "image/jpeg" : r.Mime, r.B64) });
    }

    /// <summary>easyEngine's look API: POST /engine/v1/look {images:[{data,label}], question, detail, max_tokens, id_slot}
    /// -> {text, tokens:{image,prompt,completion}, ms:{prefill,total}}; 409 vision_off means fall back to text. The engine bounds,
    /// downscales and re-encodes the picture. Looks use their own slot so a picture never evicts the conversation's cached prompt.</summary>
    public sealed class EngineLookClient : ILookClient
    {
        private readonly HttpClient _http;
        private readonly string _base;
        private readonly IVisionGate _gate;
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();
        public int? Slot;
        public int MaxTokens = 160;
        public double TimeoutS = 60.0;
        /// <summary>The last reply's token and time counts (for the call log / trace), or null.</summary>
        public JObject LastStats;

        public EngineLookClient(HttpClient http, string baseUrl, IVisionGate gate, string apiKey = "", int? slot = null)
        {
            _http = http; _base = baseUrl ?? ""; _gate = gate; Slot = slot;
            if (!string.IsNullOrEmpty(apiKey)) _headers["Authorization"] = "Bearer " + apiKey;
        }

        public static string LookUrl(string baseUrl) => LlmWire.RemoveV1((baseUrl ?? "").TrimEnd('/')) + "/engine/v1/look";

        /// <summary>close reads single tiles: high; base and wide carry the most detail: max.</summary>
        public static string Detail(string kind) => kind == "close" ? "high" : "max";

        public Task<bool> OpenAsync() => _gate == null ? Task.FromResult(false) : _gate.OpenAsync();

        public async Task<LookOutcome> LookAsync(LookRequest r)
        {
            if (r == null || string.IsNullOrEmpty(r.B64)) return null;
            var img = new JObject { ["data"] = "data:" + (string.IsNullOrEmpty(r.Mime) ? "image/jpeg" : r.Mime) + ";base64," + r.B64 };
            if (!string.IsNullOrEmpty(r.Label)) img["label"] = r.Label;
            string q = string.IsNullOrEmpty(r.Question) ? "Describe what you see." : r.Question;
            var body = new JObject { ["images"] = new JArray(img), ["question"] = q, ["detail"] = Detail(r.Kind), ["max_tokens"] = MaxTokens };
            if (Slot.HasValue) body["id_slot"] = Slot.Value;
            HttpReply rep;
            try { rep = await HttpUtil.PostJsonAsync(_http, LookUrl(_base), body, _headers, TimeoutS).ConfigureAwait(false); }
            catch (HttpTransportException) { return null; }
            if (rep.Status != 200) return null;                       // 409 vision_off and anything else: the steward goes on with text
            JObject o;
            try { o = rep.Json() as JObject; } catch (PyJsonException) { return null; }
            string text = o?["text"]?.Type == JTokenType.String ? ((string)o["text"]).Trim() : "";
            if (text.Length == 0) return null;
            LastStats = new JObject { ["tokens"] = o["tokens"]?.DeepClone(), ["ms"] = o["ms"]?.DeepClone() };
            return new LookOutcome { Answer = text };
        }
    }

    /// <summary>Picks the look path from what the endpoint says (see <see cref="LookRoute"/>) each time the gate is asked, so a changed
    /// engine is followed. Off() closes the gate for the session (a direct picture the endpoint refused with images_not_offered).</summary>
    public sealed class RoutedLookClient : ILookClient
    {
        private readonly VisionProbe _probe;
        private readonly DirectLookClient _direct;
        private readonly EngineLookClient _engine;
        public bool IsOff { get; private set; }
        public RoutedLookClient(VisionProbe probe, EngineLookClient engine)
        {
            _probe = probe; _engine = engine; _direct = new DirectLookClient(probe);
        }
        /// <summary>The path chosen by the last gate answer.</summary>
        public LookPath Path => IsOff ? LookPath.None : _probe.Path;
        public void Off() { IsOff = true; }
        public async Task<bool> OpenAsync()
        {
            if (IsOff) return false;
            await _probe.OpenAsync().ConfigureAwait(false);
            return Path != LookPath.None;
        }
        public Task<LookOutcome> LookAsync(LookRequest r)
            => Path == LookPath.Engine ? _engine.LookAsync(r) : Path == LookPath.Direct ? _direct.LookAsync(r) : Task.FromResult<LookOutcome>(null);
    }
}

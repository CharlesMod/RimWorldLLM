using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>Port of core/bridge.py BridgeError: a refused or failed bridge call. Code is the wire error code
    /// ("conflict", "expired", "duplicate", "refused", "not_found", "unauthorized"...) or "unreachable" / "bad_reply" / "http_NNN".</summary>
    public sealed class BridgeError : Exception
    {
        public readonly string Code;
        public readonly string Detail;
        public readonly int Status;
        public readonly JObject Body;

        public BridgeError(string code, string detail = "", int status = 0, JObject body = null)
            : base(string.IsNullOrEmpty(detail) ? code : code + ": " + detail)
        {
            Code = code;
            Detail = detail ?? "";
            Status = status;
            Body = body ?? new JObject();
        }
    }

    /// <summary>What the last reply said about the game (core/bridge.py Envelope): game time, epoch, pause, speed.</summary>
    public sealed class Envelope
    {
        public int Game;
        public int Epoch;
        public bool Paused;
        public int Speed = 1;
    }

    /// <summary>Port of core/adapter.py ProtocolNames: endpoint paths and body field names of the game's bridge.</summary>
    public sealed record ProtocolNames
    {
        public string ActorPath { get; init; } = "/actor";
        public string ScenePath { get; init; } = "/scene";
        public string MindPath { get; init; } = "/mind";
        public string RosterPath { get; init; } = "/roster";
        public string ActorField { get; init; } = "actor";
        public string TimeField { get; init; } = "time";
        public string EpochField { get; init; } = "epoch";
        public string ExpiryField { get; init; } = "expires_at";

        /// <summary>The generic defaults (core/adapter.py).</summary>
        public static ProtocolNames Default { get { return new ProtocolNames(); } }
    }

    /// <summary>One request on the transport seam (the httpx.request call of the Python).</summary>
    public sealed class BridgeRequest
    {
        public string Method = "GET";
        public string Path = "/";
        /// <summary>Query parameters in order (empty for none).</summary>
        public List<KeyValuePair<string, string>> Query = new List<KeyValuePair<string, string>>();
        /// <summary>Compact JSON text of the body; null for none.</summary>
        public string JsonBody;
        /// <summary>The Authorization header value ("Bearer ..."), or null when no header is sent.</summary>
        public string Authorization;

        /// <summary>"?a=1&amp;b=2" with percent-escaping, or "" when there is no query.</summary>
        public string QueryString()
        {
            if (Query.Count == 0) return "";
            var sb = new System.Text.StringBuilder("?");
            for (int i = 0; i < Query.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(Query[i].Key)).Append('=').Append(Uri.EscapeDataString(Query[i].Value ?? ""));
            }
            return sb.ToString();
        }
    }

    /// <summary>The status and raw body text of a reply.</summary>
    public sealed class BridgeReply
    {
        public int Status;
        public string Body = "";
        public BridgeReply() { }
        public BridgeReply(int status, string body) { Status = status; Body = body ?? ""; }
    }

    /// <summary>A transport could not reach the game (httpx.HTTPError in the Python); BridgeClient maps it to code "unreachable".</summary>
    public sealed class BridgeTransportException : Exception
    {
        public BridgeTransportException(string message) : base(message) { }
        public BridgeTransportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The seam under BridgeClient: one async request, status plus raw body text back. The HTTP transport,
    /// the in-process one (through the game thread queue) and the test FakeBridge implement it.</summary>
    public interface IBridgeTransport
    {
        Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken));
    }

    /// <summary>What the director and the minds need from the game (the Bridge Protocol of core/bridge.py, plus
    /// the vivarium and writing routes BridgeClient has). Dict bodies are JObject.</summary>
    public interface IBridge
    {
        ProtocolNames Protocol { get; }
        Envelope Env { get; }
        /// <summary>Sticky: set when a reply carried a new epoch; the director clears it.</summary>
        bool EpochChanged { get; set; }

        Task<JObject> HealthAsync();
        Task<JObject> ConfigAsync();
        Task<JObject> RosterAsync();
        Task<JObject> ActorAsync(int actorId);
        Task<JObject> SceneAsync(int actorId);
        Task<JObject> EventsAsync(int since);
        Task<List<JObject>> TalkAsync();
        /// <summary>Mind is null when the game holds none yet.</summary>
        Task<KeyValuePair<JObject, int>> GetMindAsync(int actorId);
        Task<int> PutMindAsync(int actorId, JObject mind, int rev, string opId = null);
        Task<JObject> SayAsync(int actor, string text, int? listener = null, string intent = "none",
            string conversationId = null, bool combat = false, string talkId = null, int? expiresAt = null,
            string opId = null, int? about = null, string vanillaRef = null, string kind = null);
        Task<JObject> ThoughtAsync(int actor, string text, string opId = null, string kind = null, string surface = null);
        Task<JObject> SocialActionAsync(int actor, string action, int target, string topic = "", string opId = null);
        Task<JObject> ChronicleAsync(int day, string text, string kind = "day", string opId = null);
        Task<List<JObject>> GetChronicleAsync(int sinceDay = 0);
        Task<JObject> RecapAsync(int epoch, IList<string> lines, string opId = null);
        Task<JObject> ArtAsync(int thing, string text, string opId = null);
        /// <summary>GET /text/queue?n=N -> {"pending": N, "items": [...]}, most important first. An older mod answers 404.</summary>
        Task<JObject> TextQueueAsync(int n = 4);
        /// <summary>POST /text/fill {op_id, id, text, title?} -> {"stored": true}. unknown_id: the item is gone or filled.</summary>
        Task<JObject> TextFillAsync(string id, string text, string title = null, string opId = null);
        Task<JObject> VivariumAsync();
        Task<JObject> DigestAsync();
        Task<JObject> ChatSinceAsync(int seq);
        /// <summary>POST /chat {who, text, stream_id?}: a streamId ends that /chat/status stream.</summary>
        Task<JObject> PostChatAsync(string who, string text, string opId = null, string streamId = null);
        /// <summary>POST /chat/status {who, state, stream_id, to?, progress?}; an older mod answers 404 not_found.</summary>
        Task<JObject> PostChatStatusAsync(string who, string state, string streamId, int? to = null, double? progress = null, string opId = null);
        Task<JObject> GoalsAsync();
        /// <summary>PUT /goals {rev, ...fields}: a stale rev is BridgeError code "conflict" (Body["board"] holds the current board).</summary>
        Task<JObject> PutGoalsAsync(int rev, JObject fields = null, string opId = null);
        Task<JObject> ActAsync(string verb, JObject body, int? expiresAt = null, string opId = null);
        Task<JObject> ActionsSinceAsync(int seq);
        Task CloseAsync();
    }

    /// <summary>Where core events come from (core/bridge.py EventSource).</summary>
    public interface IEventSource
    {
        bool LastGap { get; }
        Task<List<JObject>> PollAsync();
        void Reset();
    }

    /// <summary>Reads reply text without letting Newtonsoft turn date-like strings into dates (Python keeps them as str).</summary>
    public static class BridgeJson
    {
        public static bool Truthy(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            switch (t.Type)
            {
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.Integer: return t.Value<long>() != 0;
                case JTokenType.Float: return t.Value<double>() != 0;
                case JTokenType.String: return t.Value<string>().Length > 0;
                case JTokenType.Null: return false;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                case JTokenType.Object: return ((JObject)t).Count > 0;
                default: return true;
            }
        }

        public static JToken Parse(string text)
        {
            using (var sr = new System.IO.StringReader(text))
            using (var r = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                var t = JToken.ReadFrom(r);
                while (r.Read()) { if (r.TokenType != JsonToken.Comment) throw new JsonReaderException("extra data after JSON"); }
                return t;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>
    /// Port of core/bridge.py BridgeClient over an IBridgeTransport (HTTP, in-process or fake). Handles the
    /// envelope (game time, epoch, pause, speed), op_id and expiry on writes, the error mapping and the sticky epoch flag.
    /// </summary>
    public sealed class BridgeClient : IBridge
    {
        readonly IBridgeTransport _transport;
        int? _epochSeen;

        public ProtocolNames Protocol { get; private set; }
        public string Token { get; private set; }
        public Envelope Env { get; private set; }
        public bool EpochChanged { get; set; }

        public BridgeClient(IBridgeTransport transport, ProtocolNames protocol = null, string token = "")
        {
            _transport = transport;
            Protocol = protocol ?? ProtocolNames.Default;
            Token = token ?? "";
            Env = new Envelope();
        }

        public async Task CloseAsync()
        {
            var a = _transport as IAsyncDisposable;
            if (a != null) { await a.DisposeAsync().ConfigureAwait(false); return; }
            var d = _transport as IDisposable;
            if (d != null) d.Dispose();
        }

        // ---- plumbing -------------------------------------------------------
        static int ToInt(JToken t, int dflt)
        {
            if (t == null) return dflt;
            switch (t.Type)
            {
                case JTokenType.Integer: return (int)t.Value<long>();
                case JTokenType.Float: return (int)Math.Truncate(t.Value<double>());   // int(1.9) == 1
                case JTokenType.Boolean: return t.Value<bool>() ? 1 : 0;
                case JTokenType.String:
                    int v;
                    return int.TryParse(t.Value<string>().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : dflt;
                default: return dflt;
            }
        }

        void Absorb(JObject body)
        {
            var p = Protocol;
            if (body == null || body[p.TimeField] == null) return;
            int? old = _epochSeen;
            Env = new Envelope
            {
                Game = ToInt(body[p.TimeField], 0),
                Epoch = ToInt(body[p.EpochField], 0),
                Paused = BridgeJson.Truthy(body["paused"], false),
                Speed = ToInt(body["speed"], 1)
            };
            _epochSeen = Env.Epoch;
            if (old.HasValue && old.Value != Env.Epoch) EpochChanged = true;
        }

        async Task<JObject> Req(string method, string path, JObject jsonBody = null, bool auth = true,
            params KeyValuePair<string, string>[] query)
        {
            var req = new BridgeRequest { Method = method, Path = path };
            if (query != null) req.Query.AddRange(query);
            if (jsonBody != null) req.JsonBody = jsonBody.ToString(Formatting.None);
            if (auth && Token.Length > 0) req.Authorization = "Bearer " + Token;
            BridgeReply r;
            try { r = await _transport.SendAsync(req).ConfigureAwait(false); }
            catch (BridgeTransportException e) { throw new BridgeError("unreachable", e.Message.Length > 0 ? e.Message : e.GetType().Name); }
            catch (HttpRequestException e) { throw new BridgeError("unreachable", e.Message.Length > 0 ? e.Message : e.GetType().Name); }
            catch (TaskCanceledException e) when (!(e.CancellationToken.IsCancellationRequested)) { throw new BridgeError("unreachable", "TimeoutException"); }
            JObject body;
            try { body = BridgeJson.Parse(r.Body) as JObject; }
            catch (JsonException) { body = null; }
            if (body == null)
                throw new BridgeError("bad_reply", "HTTP " + r.Status.ToString(CultureInfo.InvariantCulture) + " not JSON", r.Status);
            Absorb(body);
            JToken ok = body["ok"];
            if (r.Status >= 400 || (ok != null && ok.Type == JTokenType.Boolean && !ok.Value<bool>()))
            {
                JToken err = body["error"], det = body["detail"];
                throw new BridgeError(
                    err != null ? Str(err) : "http_" + r.Status.ToString(CultureInfo.InvariantCulture),
                    det != null ? Str(det) : "", r.Status, body);
            }
            return body;
        }

        static string Str(JToken t) { return t.Type == JTokenType.String ? t.Value<string>() : t.ToString(Formatting.None); }

        static KeyValuePair<string, string> Q(string k, int v) { return new KeyValuePair<string, string>(k, v.ToString(CultureInfo.InvariantCulture)); }

        JObject WriteBody(JObject body, int? expiresAt, string opId)
        {
            var b = (JObject)body.DeepClone();
            b["op_id"] = string.IsNullOrEmpty(opId) ? Guid.NewGuid().ToString() : opId;
            if (expiresAt.HasValue) b[Protocol.ExpiryField] = expiresAt.Value;
            return b;
        }

        // ---- reads ----------------------------------------------------------
        public Task<JObject> HealthAsync() { return Req("GET", "/health", null, false); }
        public Task<JObject> ConfigAsync() { return Req("GET", "/config"); }
        public Task<JObject> RosterAsync() { return Req("GET", Protocol.RosterPath); }
        public Task<JObject> ActorAsync(int actorId) { return Req("GET", Protocol.ActorPath + "/" + actorId.ToString(CultureInfo.InvariantCulture)); }
        public Task<JObject> SceneAsync(int actorId) { return Req("GET", Protocol.ScenePath + "/" + actorId.ToString(CultureInfo.InvariantCulture)); }
        public Task<JObject> EventsAsync(int since) { return Req("GET", "/events", null, true, Q("since", since)); }

        public async Task<List<JObject>> TalkAsync()
        {
            return ObjectList((await Req("GET", "/talk").ConfigureAwait(false))["requests"]);
        }

        static List<JObject> ObjectList(JToken t)
        {
            var l = new List<JObject>();
            var a = t as JArray;
            if (a != null) foreach (var x in a) { var o = x as JObject; if (o != null) l.Add(o); }
            return l;
        }

        public async Task<KeyValuePair<JObject, int>> GetMindAsync(int actorId)
        {
            var b = await Req("GET", Protocol.MindPath + "/" + actorId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return new KeyValuePair<JObject, int>(b["mind"] as JObject, ToInt(b["rev"], 0));
        }

        // ---- writes ---------------------------------------------------------
        public Task<JObject> SayAsync(int actor, string text, int? listener = null, string intent = "none",
            string conversationId = null, bool combat = false, string talkId = null, int? expiresAt = null,
            string opId = null, int? about = null, string vanillaRef = null, string kind = null)
        {
            var body = new JObject { [Protocol.ActorField] = actor, ["text"] = text, ["intent"] = intent, ["combat"] = combat };
            if (!string.IsNullOrEmpty(kind)) body["kind"] = kind;                 // normal | shout | whisper (the mod guesses when absent)
            if (!string.IsNullOrEmpty(vanillaRef)) body["vanilla_ref"] = vanillaRef;   // written for one of the game's own interactions
            if (listener.HasValue) body["listener"] = listener.Value;
            if (about.HasValue) body["about"] = about.Value;
            if (!string.IsNullOrEmpty(conversationId)) body["conversation_id"] = conversationId;
            if (!string.IsNullOrEmpty(talkId)) body["talk_id"] = talkId;
            return Req("POST", "/say", WriteBody(body, expiresAt, opId));
        }

        public Task<JObject> ThoughtAsync(int actor, string text, string opId = null, string kind = null, string surface = null)
        {
            var body = new JObject { [Protocol.ActorField] = actor, ["text"] = text };
            if (!string.IsNullOrEmpty(kind)) body["kind"] = kind;
            if (!string.IsNullOrEmpty(surface)) body["surface"] = surface;
            return Req("POST", "/thought", WriteBody(body, null, opId));
        }

        public Task<JObject> SocialActionAsync(int actor, string action, int target, string topic = "", string opId = null)
        {
            var body = new JObject { [Protocol.ActorField] = actor, ["action"] = action, ["target"] = target };
            if (!string.IsNullOrEmpty(topic)) body["topic"] = topic;
            return Req("POST", "/social_action", WriteBody(body, null, opId));
        }

        public Task<JObject> ChronicleAsync(int day, string text, string kind = "day", string opId = null)
        {
            return Req("POST", "/chronicle", WriteBody(new JObject { ["day"] = day, ["text"] = text, ["kind"] = kind }, null, opId));
        }

        public Task<JObject> RecapAsync(int epoch, IList<string> lines, string opId = null)
        {
            return Req("POST", "/recap", WriteBody(new JObject { ["epoch"] = epoch, ["lines"] = new JArray(lines) }, null, opId));
        }

        public Task<JObject> ArtAsync(int thing, string text, string opId = null)
        {
            return Req("POST", "/art", WriteBody(new JObject { ["thing"] = thing, ["text"] = text }, null, opId));
        }

        public Task<JObject> TextQueueAsync(int n = 4)
        {
            return Req("GET", "/text/queue", null, true, Q("n", n));
        }

        public Task<JObject> TextFillAsync(string id, string text, string title = null, string opId = null)
        {
            var body = new JObject { ["id"] = id, ["text"] = text };
            if (title != null) body["title"] = title;
            return Req("POST", "/text/fill", WriteBody(body, null, opId));
        }

        public async Task<List<JObject>> GetChronicleAsync(int sinceDay = 0)
        {
            var b = await Req("GET", "/chronicle", null, true, Q("since_day", sinceDay)).ConfigureAwait(false);
            return ObjectList(b["entries"]);
        }

        // ---- vivarium (docs/BRIDGE-API.md "Vivarium routes"; an older mod answers 404) ----
        public Task<JObject> VivariumAsync() { return Req("GET", "/vivarium"); }
        public Task<JObject> DigestAsync() { return Req("GET", "/digest"); }
        /// <summary>GET /dialogs: the choice windows and letters open now ({"dialogs": [...], "timeout_s": N}).</summary>
        public Task<JObject> DialogsAsync() { return Req("GET", "/dialogs"); }
        public Task<JObject> ChatSinceAsync(int seq) { return Req("GET", "/chat", null, true, Q("since", seq)); }

        public Task<JObject> PostChatAsync(string who, string text, string opId = null, string streamId = null)
        {
            var body = new JObject { ["who"] = who, ["text"] = text };
            if (streamId != null) body["stream_id"] = streamId;
            return Req("POST", "/chat", WriteBody(body, null, opId));
        }

        public Task<JObject> PostChatStatusAsync(string who, string state, string streamId, int? to = null, double? progress = null, string opId = null)
        {
            var body = new JObject { ["who"] = who, ["state"] = state, ["stream_id"] = streamId };
            if (to != null) body["to"] = to.Value;
            if (progress != null) body["progress"] = progress.Value;
            return Req("POST", "/chat/status", WriteBody(body, null, opId));
        }

        public Task<JObject> PostChatPartialAsync(string streamId, string text, string opId = null)
        {
            var body = new JObject { ["stream_id"] = streamId, ["text"] = text };
            return Req("POST", "/chat/partial", WriteBody(body, null, opId));
        }

        public Task<JObject> GoalsAsync() { return Req("GET", "/goals"); }

        public Task<JObject> PutGoalsAsync(int rev, JObject fields = null, string opId = null)
        {
            var body = new JObject { ["rev"] = rev };
            if (fields != null) foreach (var p in fields.Properties()) body[p.Name] = p.Value.DeepClone();   // {"rev": rev, **fields}
            return Req("PUT", "/goals", WriteBody(body, null, opId));
        }

        public Task<JObject> ActionsSinceAsync(int seq) { return Req("GET", "/actions", null, true, Q("since", seq)); }

        public Task<JObject> LookAsync(string path, IList<KeyValuePair<string, string>> query = null)
        {
            return Req("GET", path, null, true, query == null ? new KeyValuePair<string, string>[0] : System.Linq.Enumerable.ToArray(query));
        }

        public Task<JObject> ActAsync(string verb, JObject body, int? expiresAt = null, string opId = null)
        {
            return Req("POST", "/act/" + verb, WriteBody(body, expiresAt, opId));
        }

        public async Task<int> PutMindAsync(int actorId, JObject mind, int rev, string opId = null)
        {
            var b = await Req("PUT", Protocol.MindPath + "/" + actorId.ToString(CultureInfo.InvariantCulture),
                WriteBody(new JObject { ["rev"] = rev, ["mind"] = mind }, null, opId)).ConfigureAwait(false);
            return ToInt(b["rev"], rev + 1);
        }
    }

    /// <summary>The event source: polls /events?since=seq. Handles gaps (the mod keeps only 2,000 events) and paging.</summary>
    public sealed class EventPoller : IEventSource
    {
        readonly IBridge _bridge;
        readonly Func<JObject, JObject> _normalize;

        public int Since;
        public int Gaps;
        public bool LastGap { get; private set; }

        public EventPoller(IBridge bridge, int since = 0, Func<JObject, JObject> normalize = null)
        {
            _bridge = bridge;
            _normalize = normalize ?? (e => e);
            Since = since;
        }

        public async Task<List<JObject>> PollAsync()
        {
            var output = new List<JObject>();
            LastGap = false;
            for (int page = 0; page < 20; page++)   // at most 20 pages per poll
            {
                var b = await _bridge.EventsAsync(Since).ConfigureAwait(false);
                JToken gap = b["gap"];
                if (BridgeJson.Truthy(gap, false))
                {
                    Gaps++;
                    LastGap = true;   // events were lost: the caller should resync from the roster
                }
                var evs = new List<JObject>();
                var arr = b["events"] as JArray;
                if (arr != null) foreach (var x in arr) { var o = x as JObject; if (o != null) evs.Add(o); }
                foreach (var e in evs) output.Add(_normalize(e));
                JToken nxt = b["next_seq"];
                if (nxt != null && nxt.Type != JTokenType.Null)
                    Since = Math.Max(Since, (int)nxt.Value<double>());
                else if (evs.Count > 0)
                {
                    int max = int.MinValue;
                    foreach (var e in evs) max = Math.Max(max, (int)e["seq"].Value<double>());
                    Since = Math.Max(Since, max);
                }
                if (evs.Count < 200) break;
            }
            return output;
        }

        /// <summary>After an epoch change the sequence numbers may restart.</summary>
        public void Reset() { Since = 0; }
    }
}

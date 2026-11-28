using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Finding an easyEngine on another machine and pairing with it by comparing a six-digit code
    // (easyEngine docs/ENGINE.md §7f). No game references. A token is never put in a log line, an exception
    // message, a ToString or a status text: it lives only in PairedEngine.Token and the request header.

    /// <summary>GET /engine/v1/hello.</summary>
    public sealed class EngineHello
    {
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("engine")] public string Engine = "";
        [JsonProperty("api")] public int Api;
        [JsonProperty("version")] public string Version = "";
        [JsonProperty("engine_id")] public string EngineId = "";
        [JsonProperty("model_name")] public string ModelName = "";
        [JsonProperty("model_id")] public string ModelId = "";   // optional (engines before easyEngine 871f821 omit it); "" = none selected or a custom file
        [JsonProperty("phase")] public string Phase = "";
        [JsonProperty("card")] public string Card = "";
        [JsonProperty("pairing")] public bool Pairing;
        [JsonProperty("token_required")] public bool TokenRequired;
        [JsonProperty("paired")] public bool? Paired;
        [JsonProperty("client_id")] public string ClientId = "";
        [JsonProperty("revoked")] public bool Revoked;
        /// <summary>POST /engine/v1/look is offered (missing is false).</summary>
        [JsonProperty("look")] public bool Look;

        /// <summary>An easyEngine this client can talk to (engine "easy-engine", api 1 or newer, an id).</summary>
        public bool IsEasyEngine { get { return Engine == "easy-engine" && Api >= 1 && !string.IsNullOrEmpty(EngineId); } }

        /// <summary>Parses a hello body; null when it is not one.</summary>
        public static EngineHello Parse(string body)
        {
            try
            {
                var h = JsonConvert.DeserializeObject<EngineHello>(body ?? "");
                if (h == null) return null;
                h.Name = h.Name ?? ""; h.Engine = h.Engine ?? ""; h.Version = h.Version ?? "";
                h.EngineId = (h.EngineId ?? "").Trim().ToLowerInvariant();
                h.ModelName = h.ModelName ?? ""; h.ModelId = (h.ModelId ?? "").Trim(); h.Phase = h.Phase ?? ""; h.Card = h.Card ?? ""; h.ClientId = h.ClientId ?? "";
                return h;
            }
            catch { return null; }
        }
    }

    /// <summary>How the engine judged our token (from hello, or from a 401's <c>code</c>).</summary>
    public enum EngineAuth
    {
        Unknown,        // not checked, or not an easyEngine answer
        Open,           // no token needed (loopback or --open)
        Paired,         // our token is good
        TokenRequired,  // no token sent and one is needed: offer pairing
        TokenInvalid,   // the engine does not know this token: pair again
        TokenRevoked,   // the token was revoked: pair again
    }

    public static class EngineAuthCodes
    {
        /// <summary>Maps a reply to an auth state: a 401 by its <c>code</c> field; anything else is Unknown.</summary>
        public static EngineAuth FromReply(int status, string body)
        {
            if (status != 401) return EngineAuth.Unknown;
            string code = null;
            try
            {
                var j = JObject.Parse(body ?? "");
                var c = j["code"];
                if (c == null && j["error"] is JObject eo) c = eo["code"];
                if (c != null && c.Type == JTokenType.String) code = (string)c;
            }
            catch { }
            switch (code)
            {
                case "token_required": return EngineAuth.TokenRequired;
                case "token_invalid": return EngineAuth.TokenInvalid;
                case "token_revoked": return EngineAuth.TokenRevoked;
                default: return EngineAuth.TokenInvalid;   // a 401 without our codes: the key was not accepted
            }
        }

        /// <summary>Judges a hello reply against the token we sent (null or "" = none).</summary>
        public static EngineAuth FromHello(EngineHello h, bool sentToken)
        {
            if (h == null) return EngineAuth.Unknown;
            if (!h.TokenRequired) return EngineAuth.Open;
            if (!sentToken) return EngineAuth.TokenRequired;
            if (h.Paired == true) return EngineAuth.Paired;
            if (h.Revoked) return EngineAuth.TokenRevoked;
            return EngineAuth.TokenInvalid;
        }

        /// <summary>The token must be dropped and the engine paired again.</summary>
        public static bool PairAgain(EngineAuth a) { return a == EngineAuth.TokenInvalid || a == EngineAuth.TokenRevoked; }
    }

    /// <summary>The last auth state any path saw (hello at start, a 401 from EngineClient, the Test button, the mind).</summary>
    public static class EngineAuthStatus
    {
        static volatile int _state;   // EngineAuth
        public static EngineAuth Current { get { return (EngineAuth)_state; } }
        public static void Set(EngineAuth a) { _state = (int)a; }
        /// <summary>Notes a reply from any /v1 or /engine/v1 call; only a 401 changes the state. Returns the mapped state.</summary>
        public static EngineAuth Note(int status, string body)
        {
            var a = EngineAuthCodes.FromReply(status, body);
            if (a != EngineAuth.Unknown) Set(a);
            return a;
        }
    }

    /// <summary>Turning what a person types or mDNS reports into the engine's root URL.</summary>
    public static class EngineAddress
    {
        public const int DefaultPort = 47480;

        /// <summary>
        /// "host", "host:port", "[v6]:port", "http://host:port", "http://host:port/v1" → "http://host:port" (no
        /// trailing slash, no /v1); null when it cannot be a host.
        /// </summary>
        public static string ToRoot(string input)
        {
            string s = (input ?? "").Trim();
            if (s.Length == 0) return null;
            if (!s.Contains("://")) s = "http://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            if (u.Scheme != "http" && u.Scheme != "https") return null;
            if (string.IsNullOrEmpty(u.Host)) return null;
            int port = u.IsDefaultPort && !HasExplicitPort(s) ? DefaultPort : u.Port;
            string host = u.HostNameType == UriHostNameType.IPv6 ? "[" + u.Host.Trim('[', ']') + "]" : u.Host;
            return u.Scheme + "://" + host + ":" + port;
        }

        static bool HasExplicitPort(string s)
        {
            int start = s.IndexOf("://", StringComparison.Ordinal) + 3;
            int slash = s.IndexOf('/', start);
            string auth = slash < 0 ? s.Substring(start) : s.Substring(start, slash - start);
            int close = auth.LastIndexOf(']');
            int colon = auth.LastIndexOf(':');
            return colon > close;
        }

        /// <summary>The OpenAI base URL of an engine root.</summary>
        public static string OpenAiBase(string root) { return (root ?? "").TrimEnd('/') + "/v1"; }

        /// <summary>The engine root of an OpenAI base ("http://h:p/v1" → "http://h:p"); null when not a URL.</summary>
        public static string RootOfBase(string baseUrl)
        {
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - 3);
            return b.Length == 0 ? null : ToRoot(b);
        }

        /// <summary>"host:port" of a root, for display.</summary>
        public static string Display(string root)
        {
            string r = root ?? "";
            int i = r.IndexOf("://", StringComparison.Ordinal);
            return i >= 0 ? r.Substring(i + 3) : r;
        }

        /// <summary>A loopback host (127.0.0.0/8, ::1, localhost): a local engine needs no token.</summary>
        public static bool IsLoopback(string root)
        {
            Uri u;
            if (!Uri.TryCreate(root ?? "", UriKind.Absolute, out u)) return false;
            if (string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress a;
            return IPAddress.TryParse(u.Host.Trim('[', ']'), out a) && IPAddress.IsLoopback(a);
        }
    }

    /// <summary>One engine this game is paired with. The token is never printed (ToString leaves it out).</summary>
    public sealed class PairedEngine
    {
        [JsonProperty("engine_id")] public string EngineId = "";
        [JsonProperty("token")] public string Token = "";
        [JsonProperty("client_id")] public string ClientId = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("address")] public string Address = "";   // last root URL that answered, "http://host:port"
        public bool HasToken { get { return !string.IsNullOrEmpty(Token); } }
        public override string ToString() { return "PairedEngine(" + Name + ", " + EngineId + ", " + Address + ", client " + ClientId + ", token " + (HasToken ? "set" : "none") + ")"; }
    }

    /// <summary>The engines this game holds tokens for, by engine_id. Saved in the mod settings as JSON.</summary>
    public sealed class PairStore
    {
        readonly List<PairedEngine> _items = new List<PairedEngine>();
        public IReadOnlyList<PairedEngine> Items { get { return _items; } }

        public PairedEngine Get(string engineId)
        {
            string id = (engineId ?? "").Trim().ToLowerInvariant();
            return id.Length == 0 ? null : _items.FirstOrDefault(p => p.EngineId == id);
        }

        public bool HasToken(string engineId) { var p = Get(engineId); return p != null && p.HasToken; }

        public void Put(PairedEngine e)
        {
            if (e == null || string.IsNullOrEmpty(e.EngineId)) return;
            e.EngineId = e.EngineId.Trim().ToLowerInvariant();
            _items.RemoveAll(p => p.EngineId == e.EngineId);
            _items.Add(e);
        }

        /// <summary>Drops the token for an engine (keeps its name and address so it can be found and paired again).</summary>
        public void ClearToken(string engineId)
        {
            var p = Get(engineId);
            if (p != null) { p.Token = ""; p.ClientId = ""; }
        }

        public void Remember(string engineId, string address)
        {
            var p = Get(engineId);
            if (p != null && !string.IsNullOrEmpty(address)) p.Address = address;
        }

        public string ToJson() { return JsonConvert.SerializeObject(_items, Formatting.None); }

        /// <summary>A deep copy, for work on another thread.</summary>
        public PairStore Clone() { return FromJson(ToJson()); }

        public static PairStore FromJson(string json)
        {
            var s = new PairStore();
            if (string.IsNullOrWhiteSpace(json)) return s;
            try
            {
                var list = JsonConvert.DeserializeObject<List<PairedEngine>>(json);
                if (list != null) foreach (var p in list) if (p != null) { p.Token = p.Token ?? ""; p.ClientId = p.ClientId ?? ""; p.Name = p.Name ?? ""; p.Address = p.Address ?? ""; s.Put(p); }
            }
            catch { }
            return s;
        }
    }

    /// <summary>A reply of the keyless paths: status and body, or a transport failure. Never holds a token in text.</summary>
    public sealed class PairReply
    {
        public int Status;
        public string Body = "";
        public bool Unreachable;     // connection refused, reset, dropped, name not found
        public bool TimedOut;
        public bool Cancelled;
        public string Code
        {
            get
            {
                try { var j = JObject.Parse(Body ?? ""); var c = j["code"]; if (c != null && c.Type == JTokenType.String) return (string)c; } catch { }
                return null;
            }
        }
        public bool Ok { get { return !Unreachable && !TimedOut && !Cancelled && Status >= 200 && Status < 300; } }
    }

    /// <summary>The HTTP calls of §7f against one engine root. Each call returns a PairReply; none throws.</summary>
    public sealed class PairingApi
    {
        public const string CsrfHeader = "X-Easy-Engine";
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        public int HelloTimeoutMs = 4000;
        public int ShortTimeoutMs = 5000;

        public async Task<PairReply> SendAsync(string method, string url, string token, string jsonBody, int timeoutMs, CancellationToken ct)
        {
            var r = new PairReply();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
                try
                {
                    using (var req = new HttpRequestMessage(new HttpMethod(method), url))
                    {
                        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        if (method != "GET")
                        {
                            req.Headers.TryAddWithoutValidation(CsrfHeader, "1");
                            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        }
                        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        {
                            r.Status = (int)resp.StatusCode;
                            r.Body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) r.Cancelled = true; else r.TimedOut = true; }
                catch (Exception) { r.Unreachable = true; }   // the message may hold the URL; nothing of it is kept
            }
            return r;
        }

        public async Task<(EngineHello hello, PairReply reply)> HelloAsync(string root, string token, CancellationToken ct = default(CancellationToken))
        {
            var r = await SendAsync("GET", root + "/engine/v1/hello", token, null, HelloTimeoutMs, ct).ConfigureAwait(false);
            return (r.Ok ? EngineHello.Parse(r.Body) : null, r);
        }

        /// <summary>
        /// Hello that never hands a token to the wrong engine: first without a bearer; then, only when that reply is an
        /// easy-engine whose engine_id is <paramref name="engineId"/> and a token is held, once more with the token
        /// (the reply that says paired/revoked). A stored token goes only to the engine that issued it, never to whatever
        /// answers at an address. <c>sentToken</c> says whether the returned hello was asked with the token.
        /// </summary>
        public async Task<(EngineHello hello, PairReply reply, bool sentToken)> HelloForAsync(string root, string engineId, string token, CancellationToken ct = default(CancellationToken))
        {
            var (h, r) = await HelloAsync(root, null, ct).ConfigureAwait(false);
            string want = (engineId ?? "").Trim().ToLowerInvariant();
            if (h == null || !h.IsEasyEngine || string.IsNullOrEmpty(token) || want.Length == 0 || h.EngineId != want) return (h, r, false);
            var (h2, r2) = await HelloAsync(root, token, ct).ConfigureAwait(false);
            if (h2 == null || !h2.IsEasyEngine || h2.EngineId != want) return (h, r, false);
            return (h2, r2, true);
        }

        /// <summary>GET /engine/v1/status with a Bearer: a 200 proves the key works (it needs a token on a non-loopback engine).</summary>
        public Task<PairReply> StatusAsync(string root, string token, CancellationToken ct = default(CancellationToken))
        {
            return SendAsync("GET", root + "/engine/v1/status", token, null, ShortTimeoutMs, ct);
        }

        public Task<PairReply> AskAsync(string root, string clientName, CancellationToken ct)
        {
            return SendAsync("POST", root + "/engine/v1/pair", null, new JObject { ["name"] = clientName ?? "" }.ToString(Formatting.None), ShortTimeoutMs, ct);
        }

        /// <summary>The held GET: no timeout of our own; ends on the person's answer, a drop, or <paramref name="ct"/>.</summary>
        public Task<PairReply> WaitAsync(string root, string id, CancellationToken ct)
        {
            return SendAsync("GET", root + "/engine/v1/pair/" + Uri.EscapeDataString(id), null, null, 0, ct);
        }

        public Task<PairReply> WithdrawAsync(string root, string id)
        {
            return SendAsync("DELETE", root + "/engine/v1/pair/" + Uri.EscapeDataString(id), null, null, ShortTimeoutMs, CancellationToken.None);
        }

        /// <summary>The first model id of GET /v1/models (the id the agent sends; the catalog id on an easyEngine), or null.</summary>
        public async Task<string> FirstModelIdAsync(string root, string token, int timeoutMs, CancellationToken ct = default(CancellationToken))
        {
            var r = await SendAsync("GET", root + "/v1/models", token, null, timeoutMs, ct).ConfigureAwait(false);
            if (!r.Ok) return null;
            try
            {
                var j = JObject.Parse(r.Body);
                var data = (j["data"] as JArray) ?? (j["models"] as JArray);
                if (data == null) return null;
                foreach (var m in data)
                {
                    string id = (string)(m["id"] ?? m["model"] ?? m["name"]);
                    if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                }
            }
            catch { }
            return null;
        }
    }

    public enum PairPhase
    {
        Idle,
        Checking,       // calling hello
        Asking,         // POST pair
        ShowingCode,    // waiting for the person to press Allow on the engine
        Paired,         // token stored (or no token needed: Open)
        Open,           // the engine needs no token (loopback, --open): connected without pairing
        Denied,         // 403 pair_denied
        Gone,           // 410 pair_gone (TryAgain offered once)
        NotAnEngine,    // hello failed or is not an easyEngine
        NoPairing,      // token required but pairing is off on that engine
        Unreachable,    // no answer at the address
        Failed,         // anything else (a 5xx on POST, a bad reply)
        Cancelled,
    }

    /// <summary>The result of a successful pairing (or of connecting to an engine that needs no token).</summary>
    public sealed class PairOutcome
    {
        public PairedEngine Engine;    // token "" when the engine needs none
        public string Root = "";       // "http://host:port"
        public string ModelId = "";    // from /v1/models, "" when it did not answer in time
        public string ModelName = "";  // hello's model_name
        public string EngineName = "";
        public override string ToString() { return "PairOutcome(" + EngineName + ", " + Root + ", model " + ModelId + ", " + (Engine != null ? Engine.ToString() : "none") + ")"; }
    }

    /// <summary>
    /// One pairing attempt against one engine root, run in the background; the UI reads <see cref="Phase"/>,
    /// <see cref="Code"/> and <see cref="Reconnecting"/> each frame and never waits. Follows the client's recipe of
    /// §7f: hello, POST pair, the held GET (a dropped connection is retried, 500 issue_failed is asked again,
    /// 410 offers one "Try again", 403 is final), DELETE on Cancel.
    /// </summary>
    public sealed class PairingSession
    {
        readonly PairingApi _api;
        readonly string _root, _clientName, _existingToken, _expectedId;
        readonly Action<string> _log;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly object _lock = new object();
        volatile int _phase;
        volatile string _code = "", _engineName = "", _detail = "";
        volatile bool _reconnecting, _triedAgain;
        string _id;                 // the secret request id (never logged, never shown)
        PairOutcome _outcome;
        Task _run;

        public int RetryMinMs = 500, RetryMaxMs = 8000;
        public int ModelsTimeoutMs = 5000;

        public PairPhase Phase { get { return (PairPhase)_phase; } }
        /// <summary>The comparison code as sent ("493201"); see <see cref="FormatCode"/> for display.</summary>
        public string Code { get { return _code; } }
        public string EngineName { get { return _engineName; } }
        /// <summary>A short non-secret reason for Failed/NotAnEngine (an HTTP status or error code).</summary>
        public string Detail { get { return _detail; } }
        /// <summary>The held GET dropped and is being asked again.</summary>
        public bool Reconnecting { get { return _reconnecting; } }
        /// <summary>After a 410: one "Try again" is offered; after it was used, not again.</summary>
        public bool CanTryAgain { get { return Phase == PairPhase.Gone && !_triedAgain; } }
        public bool Busy { get { var p = Phase; return p == PairPhase.Checking || p == PairPhase.Asking || p == PairPhase.ShowingCode; } }
        public string Root { get { return _root; } }
        public PairOutcome Outcome { get { lock (_lock) return _outcome; } }
        public Task Running { get { return _run ?? Task.CompletedTask; } }

        /// <param name="existingToken">a token held for <paramref name="expectedEngineId"/>; sent only after an anonymous
        /// hello shows that engine_id at <paramref name="root"/> (never without an expected id).</param>
        public PairingSession(PairingApi api, string root, string clientName, string existingToken = null, Action<string> log = null, string expectedEngineId = null)
        {
            _api = api ?? new PairingApi();
            _root = (root ?? "").TrimEnd('/');
            _clientName = clientName ?? "";
            _existingToken = existingToken;
            _expectedId = expectedEngineId;
            _log = log ?? (_ => { });
        }

        /// <summary>"493201" → "493 201".</summary>
        public static string FormatCode(string code)
        {
            string c = (code ?? "").Replace(" ", "");
            return c.Length == 6 ? c.Substring(0, 3) + " " + c.Substring(3) : c;
        }

        /// <summary>"RimWorld on &lt;host&gt;", within the engine's 40-character name cap.</summary>
        public static string ClientNameFor(string machine)
        {
            string m = string.IsNullOrWhiteSpace(machine) ? "this computer" : machine.Trim();
            string n = "RimWorld on " + m;
            return n.Length > 40 ? n.Substring(0, 40) : n;
        }

        public void Start()
        {
            lock (_lock) { if (_run != null) return; SetPhase(PairPhase.Checking); _run = Task.Run(() => RunAsync(false)); }
        }

        /// <summary>After a 410 (Gone): ask once more. Further 410s end in Gone without the offer.</summary>
        public bool TryAgain()
        {
            lock (_lock)
            {
                if (!CanTryAgain) return false;
                _triedAgain = true;
                _run = Task.Run(() => RunAsync(true));
                return true;
            }
        }

        /// <summary>The person cancelled on our side: stop waiting and withdraw the request (DELETE pair/{id}).</summary>
        public Task CancelAsync()
        {
            string id;
            lock (_lock) { id = _id; _id = null; }
            bool wasBusy = Busy;
            try { _cts.Cancel(); } catch { }
            if (wasBusy) SetPhase(PairPhase.Cancelled);
            if (string.IsNullOrEmpty(id)) return Task.CompletedTask;
            _log("pairing: withdrawn");
            return _api.WithdrawAsync(_root, id);
        }

        void SetPhase(PairPhase p) { _phase = (int)p; }

        void End(PairPhase p, string detail = "")
        {
            if (_cts.IsCancellationRequested) { SetPhase(PairPhase.Cancelled); return; }
            _detail = detail ?? "";
            SetPhase(p);
            _log("pairing: " + p + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
        }

        async Task RunAsync(bool again)
        {
            var ct = _cts.Token;
            try
            {
                EngineHello hello;
                if (!again)
                {
                    SetPhase(PairPhase.Checking);
                    var (h, hr, sent) = await _api.HelloForAsync(_root, _expectedId, _existingToken, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) { SetPhase(PairPhase.Cancelled); return; }
                    if (hr.Unreachable || hr.TimedOut) { End(PairPhase.Unreachable); return; }
                    if (h == null || !h.IsEasyEngine) { End(PairPhase.NotAnEngine, hr.Ok ? "" : "HTTP " + hr.Status); return; }
                    hello = h;
                    _engineName = h.Name;
                    var auth = EngineAuthCodes.FromHello(h, sent);
                    if (auth == EngineAuth.Open || auth == EngineAuth.Paired)
                    {
                        await Finish(h, auth == EngineAuth.Paired ? _existingToken : "", auth == EngineAuth.Paired ? h.ClientId : "", ct).ConfigureAwait(false);
                        return;
                    }
                    if (!h.Pairing) { End(PairPhase.NoPairing); return; }
                    lock (_lock) _hello = h;
                }
                else
                {
                    lock (_lock) hello = _hello;
                }

                SetPhase(PairPhase.Asking);
                _code = "";
                var ask = await _api.AskAsync(_root, _clientName, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { SetPhase(PairPhase.Cancelled); return; }
                if (ask.Unreachable || ask.TimedOut) { End(PairPhase.Unreachable); return; }
                if (ask.Status == 404) { End(PairPhase.NoPairing); return; }
                if (ask.Status == 410) { End(PairPhase.Gone); return; }
                if (!ask.Ok) { End(PairPhase.Failed, ask.Code ?? ("HTTP " + ask.Status)); return; }
                string id, code;
                try { var j = JObject.Parse(ask.Body); id = (string)j["id"]; code = (string)j["code"]; }
                catch { id = null; code = null; }
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(code)) { End(PairPhase.Failed, "bad reply"); return; }
                lock (_lock) _id = id;
                _code = code;
                SetPhase(PairPhase.ShowingCode);
                _log("pairing: code shown, waiting for Allow on " + _engineName);

                int backoff = RetryMinMs;
                while (true)
                {
                    var w = await _api.WaitAsync(_root, id, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || w.Cancelled) { SetPhase(PairPhase.Cancelled); return; }
                    if (w.Unreachable || w.TimedOut || (w.Status >= 502 && w.Status <= 504))
                    {
                        _reconnecting = true;
                        try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { SetPhase(PairPhase.Cancelled); return; }
                        backoff = Math.Min(RetryMaxMs, backoff * 2);
                        continue;
                    }
                    _reconnecting = false;
                    backoff = RetryMinMs;
                    if (w.Status == 200)
                    {
                        string token = null, clientId = null, engineId = null;
                        try { var j = JObject.Parse(w.Body); token = (string)j["token"]; clientId = (string)j["client_id"]; engineId = (string)j["engine_id"]; }
                        catch { }
                        if (string.IsNullOrEmpty(token))
                        {
                            // A cut-off body: ask again (a token already handed over answers 410, then Try again).
                            _reconnecting = true;
                            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { SetPhase(PairPhase.Cancelled); return; }
                            backoff = Math.Min(RetryMaxMs, backoff * 2);
                            continue;
                        }
                        lock (_lock) _id = null;
                        if (!string.IsNullOrEmpty(engineId) && hello != null && !string.Equals(engineId.Trim(), hello.EngineId, StringComparison.OrdinalIgnoreCase))
                            hello.EngineId = engineId.Trim().ToLowerInvariant();   // the token's own word wins
                        await Finish(hello, token, clientId ?? "", ct).ConfigureAwait(false);
                        return;
                    }
                    if (w.Status == 403) { lock (_lock) _id = null; End(PairPhase.Denied); return; }
                    if (w.Status == 410) { lock (_lock) _id = null; End(PairPhase.Gone); return; }
                    if (w.Status == 500 && w.Code == "issue_failed")
                    {
                        // The Allow stands; the engine could not write the token yet. Ask again after a pause.
                        try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { SetPhase(PairPhase.Cancelled); return; }
                        backoff = Math.Min(RetryMaxMs, backoff * 2);
                        continue;
                    }
                    lock (_lock) _id = null;
                    End(PairPhase.Failed, w.Code ?? ("HTTP " + w.Status));
                    return;
                }
            }
            catch (Exception e)
            {
                End(PairPhase.Failed, e.GetType().Name);   // never the message: it could carry a URL with an id
            }
        }

        EngineHello _hello;

        async Task Finish(EngineHello h, string token, string clientId, CancellationToken ct)
        {
            string model = string.IsNullOrEmpty(h.ModelId) ? null : h.ModelId;   // hello's catalog id wins; no /v1/models call
            if (model == null) try { model = await _api.FirstModelIdAsync(_root, token, ModelsTimeoutMs, ct).ConfigureAwait(false); } catch { }
            var o = new PairOutcome
            {
                Root = _root,
                EngineName = h.Name,
                ModelName = h.ModelName,
                ModelId = model ?? "",
                Engine = new PairedEngine { EngineId = h.EngineId, Token = token ?? "", ClientId = clientId ?? "", Name = h.Name, Address = _root },
            };
            lock (_lock) _outcome = o;
            if (ct.IsCancellationRequested) { SetPhase(PairPhase.Cancelled); return; }
            End(string.IsNullOrEmpty(token) ? PairPhase.Open : PairPhase.Paired);
        }
    }

    /// <summary>What the start-up check found for the engine the endpoint was paired with.</summary>
    public sealed class ReconnectResult
    {
        public EngineAuth Auth = EngineAuth.Unknown;
        public bool Found;              // an engine with that id answered
        public string Root = "";        // where it answered (may differ from the stored address)
        public bool Moved;              // Root differs from the address we had
        public bool TokenCleared;       // revoked or unknown: the token was dropped from the store
        public string ModelId;          // from /v1/models when paired and it answered, else null
        public EngineHello Hello;
    }

    /// <summary>
    /// Stay connected by engine_id: call hello at the last address with the token; if no engine with that id answers
    /// there, browse mDNS for the id and call hello at the new address. paired → fine; revoked or token_invalid →
    /// clear the token; token_required with no token → offer pairing.
    /// </summary>
    public static class EngineReconnect
    {
        public delegate Task<List<MdnsEngine>> Browse(int waitMs, CancellationToken ct);

        public static async Task<ReconnectResult> CheckAsync(PairStore store, string engineId, string lastRoot, PairingApi api, Browse browse, int browseMs = 2000, CancellationToken ct = default(CancellationToken))
        {
            var res = new ReconnectResult();
            var pe = store.Get(engineId);
            string token = pe != null ? pe.Token : "";
            string root = !string.IsNullOrEmpty(lastRoot) ? lastRoot : (pe != null ? pe.Address : "");
            EngineHello h = null;
            bool sent = false;
            if (!string.IsNullOrEmpty(root))
            {
                var (hh, _, s1) = await api.HelloForAsync(root, engineId, token, ct).ConfigureAwait(false);
                if (hh != null && hh.IsEasyEngine && hh.EngineId == (engineId ?? "").ToLowerInvariant()) { h = hh; sent = s1; }
            }
            if (h == null && browse != null)
            {
                List<MdnsEngine> seen = null;
                try { seen = await browse(browseMs, ct).ConfigureAwait(false); } catch { }
                var m = seen == null ? null : seen.FirstOrDefault(e => e.Id == (engineId ?? "").ToLowerInvariant());
                if (m != null)
                {
                    var (r2, hh, s2) = await EngineFinder.FirstAnsweringFor(m, api, token, ct).ConfigureAwait(false);
                    if (hh != null) { h = hh; root = r2; sent = s2; }
                }
            }
            if (h == null) return res;
            res.Found = true;
            res.Hello = h;
            res.Root = root;
            res.Moved = !string.Equals(root, lastRoot ?? "", StringComparison.OrdinalIgnoreCase);
            res.Auth = EngineAuthCodes.FromHello(h, sent);
            if (EngineAuthCodes.PairAgain(res.Auth)) { store.ClearToken(engineId); res.TokenCleared = true; }
            else store.Remember(engineId, root);
            if ((res.Auth == EngineAuth.Paired || res.Auth == EngineAuth.Open) && !string.IsNullOrEmpty(h.ModelId)) res.ModelId = h.ModelId;
            else if (res.Auth == EngineAuth.Paired || res.Auth == EngineAuth.Open)
            {
                try { res.ModelId = await api.FirstModelIdAsync(root, token, 5000, ct).ConfigureAwait(false); } catch { }
            }
            EngineAuthStatus.Set(res.Auth);
            return res;
        }
    }
}

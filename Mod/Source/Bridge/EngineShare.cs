using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Sharing this PC's engine with other PCs on the player's network (host side). No game references.
    //
    // Same-PC is the default: the mod's engine listens on loopback only and nothing here runs until the player
    // turns "Share this PC's engine on my network" on. Then the mod asks the running engine (PUT settings
    // {"share": true}, live, no restart) to add a listener on each private address (LAN and Tailscale), with
    // tokens and pairing on. A guest PC that asks to pair shows up as a `pairing` fact on the loopback events
    // stream; the mod reads GET pairing and shows an Allow/Deny window; the answer goes to POST pairing/answer
    // with the code the person compared. Paired PCs are listed by GET clients and removed by DELETE clients/{id}.
    // Engine paths: the easyEngine "share" slice (status share/share_addresses, clients) — provisional until it lands.

    public static class ShareDefaults
    {
        /// <summary>The share toggle's default: off. One-click installs serve this PC only.</summary>
        public const bool ShareEngine = false;
    }

    // ------------------------------------------------------------------------------------ DTOs

    public sealed class PendingPair
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("code")] public string Code = "";
        [JsonProperty("from")] public string From = "";
        [JsonProperty("quiet")] public bool Quiet;
    }

    public sealed class PairingList
    {
        [JsonProperty("offered")] public bool Offered;
        [JsonProperty("requests")] public List<PendingPair> Requests = new List<PendingPair>();
    }

    public sealed class PairAnswerReply
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("code")] public string Code = "";
        [JsonProperty("allowed")] public bool Allowed;
    }

    public sealed class PairedClient
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("created")] public string Created = "";
    }

    public sealed class PairedClientList
    {
        [JsonProperty("clients")] public List<PairedClient> Clients = new List<PairedClient>();
    }

    /// <summary>What the host side needs from its own engine (loopback). <see cref="EngineClient"/> is the real one.</summary>
    public interface IShareHostApi
    {
        Task<EngineResult<EngineStatus>> GetStatusAsync();
        Task<EngineResult<JObject>> PutShareAsync(bool on);
        Task<EngineResult<PairingList>> GetPairingAsync();
        Task<EngineResult<PairAnswerReply>> AnswerPairingAsync(string key, string code, bool allow);
        Task<EngineResult<PairedClientList>> GetClientsAsync();
        Task<EngineResult<JObject>> RemoveClientAsync(string id);
    }

    // ------------------------------------------------------------------------------------ status line

    public enum ShareView { Off, NotLocal, Working, On, OnNoNetwork, Unsupported, Failed }

    public sealed class ShareAddress
    {
        public string HostPort = "";
        public NetKind Kind;
        public string HelloUrl = "";
    }

    public static class ShareStatus
    {
        /// <summary>
        /// What to PUT so the engine matches the toggle, or null for nothing. An engine without share mode (null)
        /// is told only when the toggle is on (it then says it can't); one that already matches is left alone.
        /// The toggle is the truth: an engine shared from elsewhere is turned back to this PC only when the toggle is off.
        /// </summary>
        public static bool? PushFor(bool toggleOn, bool? engineShare)
        {
            if (engineShare == null) return toggleOn ? (bool?)true : null;
            return engineShare.Value == toggleOn ? (bool?)null : toggleOn;
        }

        public static ShareView View(bool toggleOn, bool localSource, bool? engineShare, IList<string> addresses, bool working, bool unsupported, string error)
        {
            if (!localSource) return ShareView.NotLocal;
            if (unsupported && toggleOn) return ShareView.Unsupported;
            if (!string.IsNullOrEmpty(error)) return ShareView.Failed;
            if (working) return ShareView.Working;
            if (!toggleOn) return engineShare == true ? ShareView.Working : ShareView.Off;
            if (engineShare != true) return ShareView.Working;
            return addresses != null && addresses.Count > 0 ? ShareView.On : ShareView.OnNoNetwork;
        }

        /// <summary>The keyed string for a view (args: On → engine name).</summary>
        public static string LineKey(ShareView v)
        {
            switch (v)
            {
                case ShareView.Off: return "RWL_Share_StatusOff";
                case ShareView.NotLocal: return "RWL_Share_StatusNotLocal";
                case ShareView.Working: return "RWL_Share_StatusWorking";
                case ShareView.On: return "RWL_Share_StatusOn";
                case ShareView.OnNoNetwork: return "RWL_Share_StatusNoNetwork";
                case ShareView.Unsupported: return "RWL_Share_StatusUnsupported";
                default: return "RWL_Share_StatusFailed";
            }
        }

        /// <summary>The engine's bound addresses as rows: LAN first, then Tailscale; each with its hello URL (the QR's text).</summary>
        public static List<ShareAddress> Addresses(IEnumerable<string> hostPorts)
        {
            var rows = new List<ShareAddress>();
            foreach (var hp0 in hostPorts ?? Enumerable.Empty<string>())
            {
                string hp = (hp0 ?? "").Trim();
                if (hp.Length == 0 || rows.Any(r => r.HostPort == hp)) continue;
                var k = NetKindOf.Of(hp);
                if (k == NetKind.Loopback) continue;
                rows.Add(new ShareAddress { HostPort = hp, Kind = k, HelloUrl = HelloUrl(hp) });
            }
            return rows.OrderBy(r => r.Kind == NetKind.Lan ? 0 : 1).ToList();
        }

        public static string HelloUrl(string hostPort) { return "http://" + hostPort + "/engine/v1/hello"; }
    }

    // ------------------------------------------------------------------------------------ the Allow/Deny book

    public enum PairAskState { Pending, Answering, Allowed, Denied, Expired }

    public enum PairAnswerOutcome { Done, Changed, NotWaiting, Failed }

    public sealed class PairAsk
    {
        public string Key = "", Name = "", Code = "", From = "";
        public bool Quiet;
        public PairAskState State;
        /// <summary>The source asked again with a new code while this one was on screen: compare again.</summary>
        public bool CodeChanged;
        /// <summary>The last answer could not be delivered (the engine did not answer); Allow/Deny can be pressed again.</summary>
        public bool AnswerFailed;
        public bool Live { get { return State == PairAskState.Pending || State == PairAskState.Answering; } }
        internal long Seq;
        public PairAsk Copy() { return (PairAsk)MemberwiseClone(); }
    }

    /// <summary>
    /// The host's pairing requests as the person sees them: pending → allowed / denied / expired. Thread-safe;
    /// the engine's list arrives on a background thread, the window reads <see cref="Current"/> each frame.
    /// Duplicates (the same request read twice) are one entry; a replacement by the same source (same key, new code)
    /// updates the code and asks the person to compare again; a request that leaves the engine's list unanswered
    /// (withdrawn, pushed out, answered in the tray) is expired; quiet ones (a source the person denied in this
    /// engine life) never open the window.
    /// </summary>
    public sealed class PairApprovals
    {
        readonly object _lk = new object();
        readonly List<PairAsk> _items = new List<PairAsk>();
        long _seq;
        int _version;
        public int KeepFinished = 5;

        /// <summary>Bumps on every change; a UI can redraw only when it moves.</summary>
        public int Version { get { lock (_lk) return _version; } }

        /// <summary>The engine's whole list of waiting requests (GET pairing).</summary>
        public void Sync(IEnumerable<PendingPair> waiting)
        {
            lock (_lk)
            {
                bool changed = false;
                var seen = new HashSet<string>();
                foreach (var w in waiting ?? Enumerable.Empty<PendingPair>())
                {
                    if (w == null || string.IsNullOrEmpty(w.Key) || !seen.Add(w.Key)) continue;
                    var it = _items.FirstOrDefault(x => x.Live && x.Key == w.Key);
                    if (it == null)
                    {
                        _items.Add(new PairAsk { Key = w.Key, Name = w.Name ?? "", Code = w.Code ?? "", From = w.From ?? "", Quiet = w.Quiet, State = PairAskState.Pending, Seq = ++_seq });
                        changed = true;
                        continue;
                    }
                    if (it.Code != (w.Code ?? ""))
                    {
                        it.Code = w.Code ?? "";
                        it.CodeChanged = true;
                        if (it.State == PairAskState.Answering) it.State = PairAskState.Pending;   // its answer names the old code: the engine refuses it
                        changed = true;
                    }
                    if (it.Name != (w.Name ?? "") || it.From != (w.From ?? "") || it.Quiet != w.Quiet)
                    {
                        it.Name = w.Name ?? ""; it.From = w.From ?? ""; it.Quiet = w.Quiet; changed = true;
                    }
                }
                foreach (var it in _items)
                    if (it.State == PairAskState.Pending && !seen.Contains(it.Key)) { it.State = PairAskState.Expired; changed = true; }
                if (changed) Touch();
            }
        }

        /// <summary>The request to show: the oldest live one that is not quiet, or null.</summary>
        public PairAsk Current
        {
            get { lock (_lk) { var it = _items.Where(x => x.Live && !x.Quiet).OrderBy(x => x.Seq).FirstOrDefault(); return it == null ? null : it.Copy(); } }
        }

        public List<PairAsk> Snapshot() { lock (_lk) return _items.Select(x => x.Copy()).ToList(); }

        /// <summary>
        /// Every live request, quiet ones included, oldest first: the settings list. A source the person denied once is
        /// quiet for the rest of the engine's life (no window), but it can still be allowed from here if the Deny was a mistake.
        /// </summary>
        public List<PairAsk> Waiting { get { lock (_lk) return _items.Where(x => x.Live).OrderBy(x => x.Seq).Select(x => x.Copy()).ToList(); } }

        public PairAsk Get(string key)
        {
            lock (_lk)
            {
                var it = _items.Where(x => x.Key == key).OrderByDescending(x => x.Seq).FirstOrDefault();
                return it == null ? null : it.Copy();
            }
        }

        /// <summary>The person pressed Allow or Deny: true with the code to send when the request is pending.</summary>
        public bool TryBeginAnswer(string key, out string code)
        {
            lock (_lk)
            {
                code = null;
                var it = _items.FirstOrDefault(x => x.State == PairAskState.Pending && x.Key == key);
                if (it == null) return false;
                it.State = PairAskState.Answering;
                it.CodeChanged = false;
                it.AnswerFailed = false;
                code = it.Code;
                Touch();
                return true;
            }
        }

        /// <summary>The engine's reply to the answer named for <paramref name="code"/>.</summary>
        public void Finish(string key, string code, bool allow, PairAnswerOutcome o)
        {
            lock (_lk)
            {
                var it = _items.FirstOrDefault(x => x.Live && x.Key == key);
                if (it == null) return;
                if (it.Code != code) { if (it.State == PairAskState.Answering) it.State = PairAskState.Pending; Touch(); return; }
                switch (o)
                {
                    case PairAnswerOutcome.Done: it.State = allow ? PairAskState.Allowed : PairAskState.Denied; break;
                    case PairAnswerOutcome.NotWaiting: it.State = PairAskState.Expired; break;
                    case PairAnswerOutcome.Changed: it.State = PairAskState.Pending; it.CodeChanged = true; break;
                    default: it.State = PairAskState.Pending; it.AnswerFailed = true; break;
                }
                Touch();
            }
        }

        /// <summary>The engine ended or the game is closing: every waiting request is over here (the engine answers the guest 410).</summary>
        public void EndAll()
        {
            lock (_lk)
            {
                bool changed = false;
                foreach (var it in _items) if (it.Live) { it.State = PairAskState.Expired; changed = true; }
                if (changed) Touch();
            }
        }

        void Touch()
        {
            _version++;
            var done = _items.Where(x => !x.Live).OrderBy(x => x.Seq).ToList();
            for (int i = 0; i < done.Count - KeepFinished; i++) _items.Remove(done[i]);
        }

        public static PairAnswerOutcome OutcomeOf<T>(EngineResult<T> r)
        {
            if (r == null) return PairAnswerOutcome.Failed;
            if (r.Ok) return PairAnswerOutcome.Done;
            if (r.HttpStatus == 404) return PairAnswerOutcome.NotWaiting;
            if (r.HttpStatus == 409) return PairAnswerOutcome.Changed;
            return PairAnswerOutcome.Failed;
        }
    }

    // ------------------------------------------------------------------------------------ host driver

    /// <summary>
    /// The host side's calls, run off the game thread: push the toggle, read the share state, refresh the waiting
    /// requests when a `pairing` event arrives, answer, list and remove paired PCs. Never throws.
    /// </summary>
    public sealed class ShareHost
    {
        readonly Func<IShareHostApi> _api;
        public readonly PairApprovals Book;
        readonly object _lk = new object();
        volatile bool _working, _unsupported;
        volatile string _error = "";
        bool? _engineShare;
        List<string> _addresses = new List<string>();
        List<PairedClient> _clients;
        volatile string _clientsError = "";

        public ShareHost(Func<IShareHostApi> api, PairApprovals book = null) { _api = api; Book = book ?? new PairApprovals(); }

        public bool Working { get { return _working; } }
        public bool Unsupported { get { return _unsupported; } }
        public string Error { get { return _error; } }
        public bool? EngineShare { get { lock (_lk) return _engineShare; } }
        public List<string> Addresses { get { lock (_lk) return _addresses.ToList(); } }
        public List<PairedClient> Clients { get { lock (_lk) return _clients == null ? null : _clients.ToList(); } }
        public string ClientsError { get { return _clientsError; } }

        public ShareView View(bool toggleOn, bool localSource)
        {
            return ShareStatus.View(toggleOn, localSource, EngineShare, Addresses, _working, _unsupported, _error);
        }

        /// <summary>Reads what the engine says about sharing (status share/share_addresses).</summary>
        public void Note(EngineStatus st)
        {
            if (st == null) return;
            lock (_lk)
            {
                _engineShare = st.Share;
                _addresses = st.ShareAddresses != null ? st.ShareAddresses.ToList() : new List<string>();
            }
        }

        /// <summary>Brings the engine to the toggle (live PUT), then reads status back. Safe to call on every attach.</summary>
        public async Task ApplyAsync(bool toggleOn)
        {
            var api = _api();
            if (api == null) return;
            _working = true;
            try
            {
                var st = await api.GetStatusAsync().ConfigureAwait(false);
                if (st.Ok) Note(st.Value);
                bool? push = ShareStatus.PushFor(toggleOn, st.Ok && st.Value != null ? st.Value.Share : EngineShare);
                if (push == null) { _error = ""; if (!toggleOn) _unsupported = false; return; }
                var r = await api.PutShareAsync(push.Value).ConfigureAwait(false);
                if (!r.Ok)
                {
                    // An engine without share mode refuses the field (400) or the path; the toggle says it can't.
                    if (r.HttpStatus == 400 || r.HttpStatus == 404 || r.HttpStatus == 422) { _unsupported = true; _error = ""; }
                    else _error = r.Error ?? ("HTTP " + r.HttpStatus);
                    return;
                }
                var after = await api.GetStatusAsync().ConfigureAwait(false);
                if (after.Ok) Note(after.Value);
                _unsupported = push.Value && after.Ok && after.Value != null && after.Value.Share == null;
                _error = "";
            }
            catch (Exception e) { _error = e.GetType().Name; }
            finally { _working = false; }
        }

        /// <summary>GET pairing → the book. Called when a `pairing` event arrives and when the stream (re)starts.</summary>
        public async Task RefreshPendingAsync()
        {
            var api = _api();
            if (api == null) return;
            try
            {
                var r = await api.GetPairingAsync().ConfigureAwait(false);
                if (r.Ok && r.Value != null) Book.Sync(r.Value.Requests);
                else if (r.Unreachable) Book.EndAll();
            }
            catch { }
        }

        /// <summary>The person's Allow or Deny for <paramref name="key"/>; a changed code re-reads the list.</summary>
        public async Task AnswerAsync(string key, bool allow)
        {
            string code;
            if (!Book.TryBeginAnswer(key, out code)) return;
            var api = _api();
            if (api == null) { Book.Finish(key, code, allow, PairAnswerOutcome.Failed); return; }
            EngineResult<PairAnswerReply> r = null;
            try { r = await api.AnswerPairingAsync(key, code, allow).ConfigureAwait(false); } catch { }
            var o = PairApprovals.OutcomeOf(r);
            Book.Finish(key, code, allow, o);
            if (o == PairAnswerOutcome.Changed || o == PairAnswerOutcome.NotWaiting) await RefreshPendingAsync().ConfigureAwait(false);
            if (o == PairAnswerOutcome.Done && allow) await RefreshClientsAsync().ConfigureAwait(false);
        }

        public async Task RefreshClientsAsync()
        {
            var api = _api();
            if (api == null) return;
            try
            {
                var r = await api.GetClientsAsync().ConfigureAwait(false);
                if (r.Ok && r.Value != null) { lock (_lk) _clients = r.Value.Clients ?? new List<PairedClient>(); _clientsError = ""; }
                else _clientsError = r.HttpStatus == 404 ? "unsupported" : (r.Error ?? ("HTTP " + r.HttpStatus));
            }
            catch (Exception e) { _clientsError = e.GetType().Name; }
        }

        /// <summary>Remove: revokes that PC's token at once (it must pair again). True when it is gone (204, or already unknown).</summary>
        public async Task<bool> RemoveAsync(string clientId)
        {
            var api = _api();
            if (api == null || string.IsNullOrEmpty(clientId)) return false;
            bool gone = false;
            try
            {
                var r = await api.RemoveClientAsync(clientId).ConfigureAwait(false);
                gone = r.Ok || r.Code == "client_unknown";
                if (!gone) _clientsError = r.Error ?? ("HTTP " + r.HttpStatus);
            }
            catch (Exception e) { _clientsError = e.GetType().Name; }
            await RefreshClientsAsync().ConfigureAwait(false);
            return gone;
        }
    }

    // ------------------------------------------------------------------------------------ events stream

    /// <summary>One line of GET /engine/v1/events (NDJSON).</summary>
    public sealed class EngineEventLine
    {
        public long Seq;
        public string Name = "";
        public string Key = "";
        public bool Gone, Gap;

        public static EngineEventLine Parse(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line)) return null;
                var j = JObject.Parse(line);
                var e = new EngineEventLine();
                if (j["gap"] != null && j["gap"].Type == JTokenType.Boolean && (bool)j["gap"]) { e.Gap = true; return e; }
                e.Seq = j["seq"] != null && j["seq"].Type == JTokenType.Integer ? (long)j["seq"] : 0;
                e.Name = j["name"] != null && j["name"].Type == JTokenType.String ? (string)j["name"] : "";
                e.Key = j["key"] != null && j["key"].Type == JTokenType.String ? (string)j["key"] : "";
                e.Gone = j["gone"] != null && j["gone"].Type == JTokenType.Boolean && (bool)j["gone"];
                return e;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Follows the local engine's events stream (docs/ENGINE.md, "A client's recipe": read facts, follow
    /// events?life&amp;since, read facts again on 409, a gap line or the end) and calls <c>onPairing</c> when a
    /// `pairing` fact changes, and once on every (re)start so the waiting list is read whole. No polling:
    /// the stream is held open; a refused connection waits and tries again (the engine may be restarting).
    /// </summary>
    public sealed class EngineEventWatch
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        readonly Func<string> _baseUrl;
        readonly Action _onPairing;
        readonly Action _onEngineGone;
        CancellationTokenSource _cts;
        Task _run;
        public int RetryMinMs = 1000, RetryMaxMs = 15000;
        public string WatchName = "pairing";

        public EngineEventWatch(Func<string> baseUrl, Action onPairing, Action onEngineGone = null)
        {
            _baseUrl = baseUrl; _onPairing = onPairing ?? (() => { }); _onEngineGone = onEngineGone ?? (() => { });
        }

        public bool Running { get { return _run != null && !_run.IsCompleted; } }

        public void Start()
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _run = Task.Run(() => Loop(ct));
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
        }

        async Task Loop(CancellationToken ct)
        {
            int backoff = RetryMinMs;
            while (!ct.IsCancellationRequested)
            {
                bool reached = false;
                try
                {
                    string root = _baseUrl();
                    if (!string.IsNullOrEmpty(root))
                    {
                        var (life, seq) = await ReadFacts(root, ct).ConfigureAwait(false);
                        if (life != null)
                        {
                            reached = true;
                            backoff = RetryMinMs;
                            _onPairing();   // read the list whole at every (re)start
                            await Follow(root, life, seq, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { }
                if (ct.IsCancellationRequested) return;
                if (!reached) _onEngineGone();
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { return; }
                if (!reached) backoff = Math.Min(RetryMaxMs, backoff * 2);
            }
        }

        static async Task<(string life, long seq)> ReadFacts(string root, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(5000);
                using (var resp = await Http.GetAsync(root + "/engine/v1/facts", cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return (null, 0);
                    var j = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return ((string)j["life"], j["seq"] != null ? (long)j["seq"] : 0);
                }
            }
        }

        async Task Follow(string root, string life, long since, CancellationToken ct)
        {
            string url = root + "/engine/v1/events?life=" + Uri.EscapeDataString(life) + "&since=" + since;
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return;   // 409: read the facts again
                using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var rd = new StreamReader(s))
                using (ct.Register(() => { try { resp.Dispose(); } catch { } }))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string line = await rd.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) return;   // the stream ended (the engine ended or restarted)
                        var e = EngineEventLine.Parse(line);
                        if (e == null) continue;
                        if (e.Gap) return;
                        if (e.Name == WatchName) _onPairing();
                    }
                }
            }
        }
    }
}

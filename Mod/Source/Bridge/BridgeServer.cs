using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One inbound HTTP request, routed on the game thread, answered on the HTTP thread.</summary>
    public sealed class InboundRequest : IDisposable
    {
        const int StatePending = 0, StateClaimed = 1, StateAbandoned = 2;

        public string Method = "";
        public string Path = "/";
        public string Query = "";
        public string Body = "";
        public int StatusCode = 500;
        public string Response = "";
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        /// <summary>In-process submissions only: completed alongside Done, with continuations run off the game thread.</summary>
        public TaskCompletionSource<bool> Async;

        int _state;

        /// <summary>Game thread: take ownership. False when the HTTP side already gave up.</summary>
        public bool TryClaim() { return Interlocked.CompareExchange(ref _state, StateClaimed, StatePending) == StatePending; }

        /// <summary>HTTP thread: give up waiting. False when the game thread already claimed it.</summary>
        public bool TryAbandon() { return Interlocked.CompareExchange(ref _state, StateAbandoned, StatePending) == StatePending; }

        public void Complete(int status, string body)
        {
            StatusCode = status;
            Response = body ?? "";
            Done.Set();
            var a = Async;
            if (a != null) a.TrySetResult(true);
        }

        public void Dispose() { Done.Dispose(); }
    }

    public sealed class BridgeOptions
    {
        public string BindAddress = "127.0.0.1";
        public int FirstPort = Const.DefaultPort;
        public int LastPort = Const.LastPort;
        public string Token = "";
        public Func<JObject> LlmInfo;       // the "llm" block for bridge.json ({base_url, model, api_key}); null = none
        public string ConfigDir;            // bridge.json goes to <ConfigDir>/RimWorldLLM/bridge.json; null = not written
        public string Version = Const.Version;
        public TimeSpan GameThreadTimeout = TimeSpan.FromSeconds(20);
        /// <summary>If the game thread has not pumped for this long, answer `busy` at once instead of waiting
        /// (the game window is in the background, loading, or frozen).</summary>
        public TimeSpan StalledAfter = TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// The HTTP bridge. HTTP threads authenticate, check sizes and wait; every other request is queued and
    /// answered by Pump() on the game thread. /health is answered on the HTTP thread from the last published GameState.
    /// </summary>
    public sealed class BridgeServer : IDisposable
    {
        readonly BridgeOptions _opt;
        readonly ApiRouter _router;
        readonly ConcurrentQueue<InboundRequest> _pending = new ConcurrentQueue<InboundRequest>();
        volatile HttpListener _listener;
        volatile GameState _state = GameState.Menu;
        string _token = "";
        long _lastPump; // Stopwatch timestamp of the last Pump call; 0 = never

        /// <summary>True when the game thread has not pumped within StalledAfter.</summary>
        public bool GameThreadStalled
        {
            get
            {
                long last = Interlocked.Read(ref _lastPump);
                if (last == 0) return false;
                return Stopwatch.GetTimestamp() - last > (long)(_opt.StalledAfter.TotalSeconds * Stopwatch.Frequency);
            }
        }

        public Action<string> LogInfo = delegate { };
        public Action<string> LogError = delegate { };

        public int Port { get; private set; }
        public string Token { get { return _token; } }
        public string LastError { get; private set; }
        public bool IsRunning { get { var l = _listener; return l != null && l.IsListening; } }
        public GameState State { get { return _state; } }
        public int PendingCount { get { return _pending.Count; } }
        public readonly RequestLog Requests = new RequestLog();

        public BridgeServer(BridgeOptions opt, ApiRouter router)
        {
            _opt = opt;
            _router = router;
            LastError = "";
        }

        /// <summary>Game thread: publish the state that replies carry and that /health reads.</summary>
        public void SetState(GameState s)
        {
            bool epochChanged = s.SaveEpoch != _state.SaveEpoch;
            _state = s;
            if (epochChanged && IsRunning) TryWriteInfo();
        }

        public bool Start()
        {
            StopCore();   // closes a previous listener; keeps the in-process door as it was
            LastError = "";
            _token = (_opt.Token ?? "").Trim();
            if (_token.Length == 0)
            {
                LastError = "no token: the bridge refuses to start without one";
                LogError("[RimWorldLLM] " + LastError);
                return false;
            }
            string bind = _opt.BindAddress;
            bool wildcard = string.IsNullOrWhiteSpace(bind) || bind == "0.0.0.0" || bind == "*";
            Exception last = null;
            for (int port = _opt.FirstPort; port <= _opt.LastPort; port++)
            {
                HttpListener l = null;
                try
                {
                    l = new HttpListener();
                    l.Prefixes.Add(wildcard ? "http://+:" + port + "/" : "http://" + bind + ":" + port + "/");
                    l.Start();
                }
                catch (Exception e)
                {
                    last = e;
                    try { l?.Close(); } catch { }
                    continue;
                }
                _listener = l;
                Port = port;
                var gate = new SemaphoreSlim(Const.MaxInFlight, Const.MaxInFlight);
                var thread = new Thread(() => ListenLoop(l, gate)) { IsBackground = true, Name = "RimWorldLLM-Bridge" };
                thread.Start();
                TryWriteInfo();
                LogInfo("[RimWorldLLM] Bridge listening on port " + port);
                return true;
            }
            LastError = "no free port in " + _opt.FirstPort + "-" + _opt.LastPort + (last != null ? " (" + last.Message + ")" : "");
            LogError("[RimWorldLLM] Bridge failed to start: " + LastError);
            return false;
        }

        /// <summary>Rewrites bridge.json (after the LLM settings changed). Any thread.</summary>
        public void RefreshInfo() { if (IsRunning) TryWriteInfo(); }

        void TryWriteInfo()
        {
            if (string.IsNullOrEmpty(_opt.ConfigDir)) return;
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                BridgeInfo.Write(_opt.ConfigDir, BridgeInfo.Build(Port, string.IsNullOrWhiteSpace(_opt.BindAddress) ? "0.0.0.0" : _opt.BindAddress, _token, pid, _opt.Version, _state.SaveEpoch, _opt.LlmInfo != null ? _opt.LlmInfo() : null));
            }
            catch (Exception e) { LogError("[RimWorldLLM] Could not write bridge.json: " + e.Message); }
        }

        /// <summary>Stops everything: the HTTP listener and the in-process door. Queued requests are answered 503 no_game.</summary>
        public void Stop()
        {
            StopCore();
            _inProcess = false;
        }

        void StopCore()
        {
            var l = _listener;
            _listener = null;
            if (l != null) { try { l.Close(); } catch { } }
            InboundRequest req;
            while (_pending.TryDequeue(out req))
                if (req.TryClaim()) req.Complete(503, Envelope.Json(_state, ApiResult.Fail("no_game", "bridge stopped")));
        }

        public void Dispose() { Stop(); }

        volatile bool _inProcess;

        /// <summary>True when requests may be submitted without HTTP (see SubmitAsync). Independent of the listener.</summary>
        public bool InProcessEnabled { get { return _inProcess; } }

        /// <summary>Opens the in-process door: SubmitAsync works from now on, with or without the HTTP listener. Any thread.</summary>
        public void EnableInProcess() { _inProcess = true; }

        /// <summary>
        /// Submits a request without HTTP and awaits its answer without blocking a thread. It takes the same road as an
        /// HTTP request: the pending queue, Pump() on the game thread, ApiRouter.Handle and the same envelope. The
        /// same refusals apply (413 too_large, 503 busy while the game thread is stalled, 504 timeout with abandon).
        /// In-process callers are trusted: no token. Returns the status and the raw JSON body, as the HTTP reply would carry them.
        /// Throws OperationCanceledException (after abandoning the request) when ct is cancelled.
        /// </summary>
        public async Task<KeyValuePair<int, string>> SubmitAsync(string method, string path, string query, string body, CancellationToken ct = default(CancellationToken))
        {
            var clock = Stopwatch.StartNew();
            int status = 0;
            try
            {
                var r = SubmitCore(method, path ?? "/", query ?? "", body ?? "", ct);
                var pair = await r.ConfigureAwait(false);
                status = pair.Key;
                return pair;
            }
            finally { Requests.Add(method, path, status, clock.Elapsed.TotalMilliseconds, true); }
        }

        async Task<KeyValuePair<int, string>> SubmitCore(string method, string path, string query, string body, CancellationToken ct)
        {
            if (!_inProcess)
                return Reply(503, ApiResult.Fail("no_game", "bridge stopped"));
            if (method == "GET" && path.TrimEnd('/') == "/health")
                return Reply(200, ApiResult.Success(HealthBody()));
            if (Encoding.UTF8.GetByteCount(body) > Const.MaxBodyBytes)
                return Reply(413, ApiResult.Fail("too_large", "body over 64 KiB"));
            if (GameThreadStalled)
                return Reply(503, ApiResult.Fail("busy", "the game is not running frames (window in the background, loading or frozen); retry shortly"));
            using (var req = new InboundRequest { Method = method, Path = path, Query = query, Body = body })
            {
                req.Async = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Enqueue(req);
                try
                {
                    if (!await WaitAsync(req.Async.Task, ct).ConfigureAwait(false))
                    {
                        if (req.TryAbandon())
                            return Reply(504, ApiResult.Fail("timeout", "the game did not answer in time; the request was dropped"));
                        // claimed by the game thread, so completion is imminent; but never wait for ever on the game
                        if (!await WaitAsync(req.Async.Task, ct).ConfigureAwait(false))
                            return Reply(504, ApiResult.Fail("timeout", "the game did not finish the request in time"));
                    }
                }
                catch (OperationCanceledException)
                {
                    req.TryAbandon();
                    throw;
                }
                return new KeyValuePair<int, string>(req.StatusCode, req.Response);
            }
        }

        async Task<bool> WaitAsync(Task t, CancellationToken ct)
        {
            try { await t.WaitAsync(_opt.GameThreadTimeout, ct).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }

        KeyValuePair<int, string> Reply(int status, ApiResult r)
        {
            return new KeyValuePair<int, string>(status, Envelope.Json(_state, r));
        }

        /// <summary>
        /// Game thread: answer queued requests until the budget is spent (always at least one if any waits).
        /// Returns the number handled.
        /// </summary>
        public int Pump(double budgetMs = Const.FrameBudgetMs)
        {
            Interlocked.Exchange(ref _lastPump, Stopwatch.GetTimestamp());
            if (_pending.IsEmpty) return 0;
            var sw = Stopwatch.StartNew();
            int handled = 0;
            InboundRequest req;
            while (_pending.TryDequeue(out req))
            {
                if (!req.TryClaim()) continue; // the HTTP side timed out and gave up
                handled++;
                var state = _state;
                ApiResult r;
                try { r = _router.Handle(req.Method, req.Path, req.Query, req.Body, state); }
                catch (Exception e) { r = ApiResult.Fail("internal", e.Message); }
                req.Complete(r.Status, Envelope.Json(state, r));
                if (sw.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
            return handled;
        }

        void ListenLoop(HttpListener listener, SemaphoreSlim gate)
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx, gate));
            }
        }

        bool AuthOk(HttpListenerRequest r)
        {
            string auth = r.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && Secure.Equals(_token, auth.Substring(7).Trim()))
                return true;
            if (r.HttpMethod == "GET")
            {
                string q = null;
                try { q = r.QueryString["token"]; } catch { }
                return !string.IsNullOrEmpty(q) && Secure.Equals(_token, q);
            }
            return false;
        }

        /// <summary>Reads at most MaxBodyBytes; returns null when the body is larger.</summary>
        static string ReadBody(HttpListenerRequest r)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[4096];
                int n;
                while ((n = r.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > Const.MaxBodyBytes) return null;
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        JObject HealthBody()
        {
            var s = _state;
            return new JObject { ["mod"] = "RimWorldLLM", ["version"] = _opt.Version, ["game_loaded"] = s.GameLoaded };
        }

        void HandleContext(HttpListenerContext ctx, SemaphoreSlim gate)
        {
            bool acquired = false;
            bool authed = false;
            InboundRequest req = null;
            var clock = Stopwatch.StartNew();
            string logMethod = "", logPath = "";
            try
            {
                var r = ctx.Request;
                logMethod = r.HttpMethod;
                logPath = r.Url != null ? r.Url.AbsolutePath : "/";
                string path = r.Url != null ? r.Url.AbsolutePath : "/";
                if (r.HttpMethod == "GET" && path.TrimEnd('/') == "/health")
                {
                    Write(ctx, 200, Envelope.Json(_state, ApiResult.Success(HealthBody())));
                    return;
                }
                if (!(acquired = gate.Wait(0)))
                {
                    Write(ctx, 503, Envelope.Json(_state, ApiResult.Fail("busy", "too many requests in flight")));
                    return;
                }
                if (!(authed = AuthOk(r)))
                {
                    Write(ctx, 401, Envelope.Json(_state, ApiResult.Fail("unauthorized", "missing or wrong bearer token")));
                    return;
                }
                if (r.ContentLength64 > Const.MaxBodyBytes)
                {
                    Write(ctx, 413, Envelope.Json(_state, ApiResult.Fail("too_large", "body over 64 KiB")));
                    return;
                }
                string body = ReadBody(r);
                if (body == null)
                {
                    Write(ctx, 413, Envelope.Json(_state, ApiResult.Fail("too_large", "body over 64 KiB")));
                    return;
                }
                req = new InboundRequest { Method = r.HttpMethod, Path = path, Query = r.Url != null ? r.Url.Query : "", Body = body };
                if (!IsRunning)
                {
                    Write(ctx, 503, Envelope.Json(_state, ApiResult.Fail("no_game", "bridge stopped")));
                    return;
                }
                if (GameThreadStalled)
                {
                    Write(ctx, 503, Envelope.Json(_state, ApiResult.Fail("busy", "the game is not running frames (window in the background, loading or frozen); retry shortly")));
                    return;
                }
                _pending.Enqueue(req);
                if (!req.Done.Wait(_opt.GameThreadTimeout))
                {
                    if (req.TryAbandon())
                    {
                        Write(ctx, 504, Envelope.Json(_state, ApiResult.Fail("timeout", "the game did not answer in time; the request was dropped")));
                        return;
                    }
                    // claimed by the game thread, so completion is imminent; but never wait for ever on the game
                    // (a frozen frame or an exception that skipped Done must not pin a server thread)
                    if (!req.Done.Wait(_opt.GameThreadTimeout))
                    {
                        Write(ctx, 504, Envelope.Json(_state, ApiResult.Fail("timeout", "the game did not finish the request in time")));
                        return;
                    }
                }
                Write(ctx, req.StatusCode, req.Response);
            }
            catch (Exception e)
            {
                try { Write(ctx, 500, Envelope.Json(_state, ApiResult.Fail("internal", e.Message))); } catch { }
            }
            finally
            {
                if (acquired) gate.Release();
                if (req != null) req.Dispose();
                int status = 0;
                try { status = ctx.Response.StatusCode; } catch { }
                Requests.Add(logMethod, logPath, status, clock.Elapsed.TotalMilliseconds, authed);
            }
        }

        static void Write(HttpListenerContext ctx, int status, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }
    }
}

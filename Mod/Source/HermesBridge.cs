using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Verse;

namespace HermesVoice
{
    /// <summary>One inbound HTTP request, routed on the game thread, answered on the HTTP thread.</summary>
    public class InboundRequest : IDisposable
    {
        const int StatePending = 0, StateClaimed = 1, StateAbandoned = 2;

        public string Method = "";
        public string Path = "/";
        public string Query = "";
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public int StatusCode = 500;
        public string Response = "";
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

        int _state;

        public string GetHeader(string name)
        {
            foreach (var k in Headers.Keys)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    return Headers[k];
            return "";
        }

        /// <summary>Game thread: take ownership of the request. False when the HTTP side already gave up.</summary>
        public bool TryClaim() => Interlocked.CompareExchange(ref _state, StateClaimed, StatePending) == StatePending;

        /// <summary>HTTP thread: give up waiting. False when the game thread already claimed it (completion is imminent).</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, StateAbandoned, StatePending) == StatePending;

        public void Complete(int status, string body)
        {
            StatusCode = status;
            Response = body ?? "";
            Done.Set();
        }

        public void Dispose() { Done.Dispose(); }
    }

    /// <summary>
    /// In-mod HTTP bridge (agent -> mod). A background thread accepts requests; auth and size limits are
    /// enforced on the pool thread, then each request is enqueued and drained on the game thread (by the
    /// GameComponent), then answered on the HTTP thread. This keeps all RimWorld state access on the game
    /// thread (thread-safety).
    /// </summary>
    public static class HermesBridge
    {
        const int MaxBodyBytes = 64 * 1024;
        const int MaxInFlight = 16;
        static readonly TimeSpan GameThreadTimeout = TimeSpan.FromSeconds(20);

        public static readonly ConcurrentQueue<InboundRequest> Pending = new ConcurrentQueue<InboundRequest>();
        public static int Port { get; private set; } = 8765;
        public static string Token { get; private set; } = "";
        public static string LastError { get; private set; } = "";
        public static bool IsRunning { get { var l = _listener; return l != null && l.IsListening; } }

        static volatile HttpListener _listener;

        public static bool IsLoopback(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress)) return false;
            string a = bindAddress.Trim().ToLowerInvariant();
            return a == "localhost" || a == "::1" || a == "[::1]" || a.StartsWith("127.");
        }

        public static bool IsExposedWithoutToken(string bindAddress, string token)
            => string.IsNullOrWhiteSpace(token) && !IsLoopback(bindAddress);

        public const string OpenWarning = "Anyone on your network can read colony state and make pawns speak";

        public static void Start(int port, string bindAddress, string token)
        {
            Stop();
            Port = port;
            Token = token ?? "";
            LastError = "";
            try
            {
                var listener = new HttpListener();
                string prefix;
                if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "0.0.0.0" || bindAddress == "*")
                    prefix = $"http://+:{port}/";
                else
                    prefix = $"http://{bindAddress}:{port}/";
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;

                byte[] tokenBytes = Encoding.UTF8.GetBytes(Token.Trim());
                var gate = new SemaphoreSlim(MaxInFlight, MaxInFlight);
                var thread = new Thread(() => ListenLoop(listener, tokenBytes, gate)) { IsBackground = true, Name = "HermesVoice-Bridge" };
                thread.Start();
                Log.Message($"[HermesVoice] Bridge listening on {prefix}");
                if (IsExposedWithoutToken(bindAddress, Token))
                    Log.Warning($"[HermesVoice] No shared token is set and the bridge is not bound to loopback. {OpenWarning}.");
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Error($"[HermesVoice] Bridge failed to start on port {port}: {e}");
                _listener = null;
            }
        }

        public static void Stop()
        {
            var l = _listener;
            _listener = null;
            if (l != null)
            {
                try { l.Close(); } catch { }
            }
            // Answer anything still queued so HTTP threads do not wait out their timeout.
            InboundRequest req;
            while (Pending.TryDequeue(out req))
            {
                if (req.TryClaim())
                    req.Complete(503, "{\"error\":\"bridge stopped\"}");
            }
        }

        static void ListenLoop(HttpListener listener, byte[] tokenBytes, SemaphoreSlim gate)
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    break;
                }
                ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx, tokenBytes, gate));
            }
        }

        static bool TokenEquals(byte[] expected, string given)
        {
            if (given == null) return false;
            byte[] g = Encoding.UTF8.GetBytes(given.Trim());
            // FixedTimeEquals returns early on a length mismatch; the token length is not secret enough to matter.
            return CryptographicOperations.FixedTimeEquals(expected, g);
        }

        static bool AuthOk(HttpListenerRequest r, byte[] tokenBytes)
        {
            if (tokenBytes.Length == 0) return true;
            string auth = r.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && TokenEquals(tokenBytes, auth.Substring(7)))
                return true;
            string q = null;
            try { q = r.QueryString["token"]; } catch { }
            return !string.IsNullOrEmpty(q) && TokenEquals(tokenBytes, q);
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
                    if (ms.Length + n > MaxBodyBytes) return null;
                    ms.Write(buf, 0, n);
                }
                return (r.ContentEncoding ?? Encoding.UTF8).GetString(ms.ToArray());
            }
        }

        static void HandleContext(HttpListenerContext ctx, byte[] tokenBytes, SemaphoreSlim gate)
        {
            bool acquired = false;
            InboundRequest req = null;
            try
            {
                if (!(acquired = gate.Wait(0)))
                {
                    WriteJson(ctx, 503, "{\"error\":\"too many requests\"}");
                    return;
                }
                var r = ctx.Request;
                if (!AuthOk(r, tokenBytes))
                {
                    WriteJson(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }
                if (r.ContentLength64 > MaxBodyBytes)
                {
                    WriteJson(ctx, 413, "{\"error\":\"body too large\"}");
                    return;
                }

                req = new InboundRequest
                {
                    Method = r.HttpMethod,
                    Path = r.Url?.AbsolutePath ?? "/",
                    Query = r.Url?.Query ?? ""
                };
                foreach (var k in r.Headers.AllKeys)
                    if (k != null) req.Headers[k] = r.Headers[k];

                string body = ReadBody(r);
                if (body == null)
                {
                    WriteJson(ctx, 413, "{\"error\":\"body too large\"}");
                    return;
                }
                req.Body = body;

                if (!IsRunning)
                {
                    WriteJson(ctx, 503, "{\"error\":\"bridge stopped\"}");
                    return;
                }
                Pending.Enqueue(req);
                if (!req.Done.Wait(GameThreadTimeout))
                {
                    if (req.TryAbandon())
                    {
                        WriteJson(ctx, 504, "{\"error\":\"timed out\"}");
                        return;
                    }
                    req.Done.Wait(); // the game thread already claimed it; completion is imminent
                }
                WriteJson(ctx, req.StatusCode, req.Response);
            }
            catch (Exception e)
            {
                try { WriteJson(ctx, 500, JsonConvert.SerializeObject(new { error = e.Message })); } catch { }
            }
            finally
            {
                if (acquired) gate.Release();
                req?.Dispose();
            }
        }

        static void WriteJson(HttpListenerContext ctx, int status, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }
    }
}

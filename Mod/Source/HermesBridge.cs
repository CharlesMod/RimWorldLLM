using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using Verse;

namespace HermesVoice
{
    /// <summary>One inbound HTTP request, routed on the game thread, answered on the HTTP thread.</summary>
    public class InboundRequest
    {
        public string Method = "";
        public string Path = "/";
        public string Query = "";
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public int StatusCode = 500;
        public string Response = "";
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

        public string GetHeader(string name)
        {
            foreach (var k in Headers.Keys)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    return Headers[k];
            return "";
        }

        public void Complete(int status, string body)
        {
            StatusCode = status;
            Response = body ?? "";
            Done.Set();
        }
    }

    /// <summary>
    /// In-mod HTTP bridge (agent -> mod). A background thread accepts requests; each request is
    /// enqueued and drained on the game thread (by the GameComponent), then answered on the HTTP
    /// thread. This keeps all RimWorld state access on the game thread (thread-safety).
    /// </summary>
    public static class HermesBridge
    {
        public static readonly ConcurrentQueue<InboundRequest> Pending = new ConcurrentQueue<InboundRequest>();
        public static int Port { get; private set; } = 8765;
        public static string Token { get; private set; } = "";
        public static bool IsRunning => _running && _listener != null && _listener.IsListening;

        static HttpListener _listener;
        static Thread _thread;
        static volatile bool _running;

        public static void Start(int port, string bindAddress, string token)
        {
            Stop();
            Port = port;
            Token = token ?? "";
            try
            {
                _listener = new HttpListener();
                string prefix;
                if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "0.0.0.0" || bindAddress == "*")
                    prefix = $"http://+:{port}/";
                else
                    prefix = $"http://{bindAddress}:{port}/";
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true, Name = "HermesVoice-Bridge" };
                _thread.Start();
                Log.Message($"[HermesVoice] Bridge listening on {prefix}");
            }
            catch (Exception e)
            {
                Log.Error($"[HermesVoice] Bridge failed to start on port {port}: {e}");
                _running = false;
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        static void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    if (!_running || _listener == null || !_listener.IsListening) break;
                    ctx = _listener.GetContext();
                }
                catch
                {
                    break;
                }
                ThreadPool.QueueUserWorkItem(HandleContext, ctx);
            }
        }

        static void HandleContext(object state)
        {
            var ctx = (HttpListenerContext)state;
            try
            {
                var req = new InboundRequest { Method = ctx.Request.HttpMethod };
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                int qIndex = path.IndexOf('?');
                if (qIndex >= 0)
                {
                    req.Query = path.Substring(qIndex + 1);
                    req.Path = path.Substring(0, qIndex);
                }
                else
                {
                    req.Path = path;
                }

                foreach (var k in ctx.Request.Headers.AllKeys)
                    req.Headers[k] = ctx.Request.Headers[k];

                using (var reader = new StreamReader(ctx.Request.InputStream))
                    req.Body = reader.ReadToEnd();

                Pending.Enqueue(req);
                bool completed = req.Done.Wait(TimeSpan.FromSeconds(20));
                if (!completed)
                {
                    req.StatusCode = 504;
                    req.Response = "{\"error\":\"timed out\"}";
                }

                WriteResponse(ctx, req);
            }
            catch (Exception e)
            {
                try
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"" + e.Message + "\"}");
                    ctx.Response.StatusCode = 500;
                    ctx.Response.OutputStream.Write(err, 0, err.Length);
                    ctx.Response.Close();
                }
                catch { }
            }
        }

        static void WriteResponse(HttpListenerContext ctx, InboundRequest req)
        {
            byte[] data = Encoding.UTF8.GetBytes(req.Response);
            ctx.Response.StatusCode = req.StatusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }
    }
}

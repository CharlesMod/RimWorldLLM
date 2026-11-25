using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    public static class Const
    {
        public const string Version = "2.0.0";
        public const int DefaultPort = 9765;
        public const int LastPort = 9774;
        public const int MaxBodyBytes = 64 * 1024;
        public const int MaxInFlight = 16;
        public const int EventRingSize = 2000;
        public const int EventsPerCall = 200;
        public const double OpIdTtlSeconds = 600;
        public const double FrameBudgetMs = 2.0;
    }

    /// <summary>What every reply says about the game. Immutable; the game thread publishes a new one each frame.</summary>
    public sealed class GameState
    {
        public bool GameLoaded;
        public int Tick;
        public int SaveEpoch;
        public bool Paused;
        public int Speed;

        public static readonly GameState Menu = new GameState();
    }

    /// <summary>A handler's result: an HTTP status plus the fields of the reply body (before the envelope is added).</summary>
    public sealed class ApiResult
    {
        public int Status = 200;
        public bool Ok = true;
        public JObject Body = new JObject();
        public string Error;

        public static ApiResult Success(JObject body = null)
        {
            return new ApiResult { Body = body ?? new JObject() };
        }

        public static ApiResult Fail(string code, string detail = null, JObject extra = null)
        {
            var r = new ApiResult { Ok = false, Error = code, Status = Envelope.StatusFor(code), Body = extra ?? new JObject() };
            r.Body["error"] = code;
            if (detail != null) r.Body["detail"] = detail;
            return r;
        }
    }

    public static class Envelope
    {
        public static int StatusFor(string code)
        {
            switch (code)
            {
                case "unauthorized": return 401;
                case "not_found": return 404;
                case "bad_request": return 400;
                case "too_large": return 413;
                case "conflict": return 409;
                case "refused": return 409;
                case "duplicate": return 409;
                case "expired": return 410;
                case "no_game": return 503;
                case "busy": return 503;
                case "timeout": return 504;
                default: return 500; // "internal"
            }
        }

        /// <summary>Builds the final JSON object: ok, tick, save_epoch, paused, speed first, then the body fields.</summary>
        public static JObject Wrap(GameState s, ApiResult r)
        {
            s = s ?? GameState.Menu;
            var o = new JObject
            {
                ["ok"] = r.Ok,
                ["tick"] = s.Tick,
                ["save_epoch"] = s.SaveEpoch,
                ["paused"] = s.Paused,
                ["speed"] = s.Speed
            };
            foreach (var p in r.Body.Properties())
                if (o[p.Name] == null) o[p.Name] = p.Value;
            return o;
        }

        public static string Json(GameState s, ApiResult r)
        {
            return Wrap(s, r).ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>One routed request, as a handler sees it (game thread).</summary>
    public sealed class RouteContext
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        public Dictionary<string, string> Args = new Dictionary<string, string>();
        public JObject Body;      // writes only
        public string OpId;       // writes only
        public GameState State;

        public string Q(string key) { string v; return Query.TryGetValue(key, out v) ? v : null; }
    }

    public delegate ApiResult RouteHandler(RouteContext ctx);

    /// <summary>Constant-time string compare, no framework dependency.</summary>
    public static class Secure
    {
        public static bool Equals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string NewToken()
        {
            var buf = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(buf);
            var sb = new System.Text.StringBuilder(buf.Length * 2);
            foreach (byte b in buf) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

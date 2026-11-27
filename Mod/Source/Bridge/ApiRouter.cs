using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Routes a request on the game thread: no_game check, body parsing, op_id dedup, expiry, handler call.
    /// The envelope is added by the caller.
    /// </summary>
    public sealed class ApiRouter
    {
        sealed class Route
        {
            public string Method;
            public string[] Segments;
            public bool NeedsGame;
            public RouteHandler Handler;
        }

        readonly List<Route> _routes = new List<Route>();
        public readonly OpIdStore OpIds;
        public Action<string> LogWarning = delegate { };

        public ApiRouter(OpIdStore opIds) { OpIds = opIds; }

        public static bool IsWrite(string method) { return method == "POST" || method == "PUT"; }

        public void Add(string method, string pattern, bool needsGame, RouteHandler handler)
        {
            _routes.Add(new Route
            {
                Method = method,
                Segments = pattern.Trim('/').Split('/'),
                NeedsGame = needsGame,
                Handler = handler
            });
        }

        bool Match(Route r, string path, Dictionary<string, string> args)
        {
            var segs = path.Trim('/').Split('/');
            if (segs.Length != r.Segments.Length) return false;
            args.Clear();
            for (int i = 0; i < segs.Length; i++)
            {
                string pat = r.Segments[i];
                if (pat.Length > 2 && pat[0] == '{' && pat[pat.Length - 1] == '}')
                    args[pat.Substring(1, pat.Length - 2)] = Uri.UnescapeDataString(segs[i]);
                else if (!string.Equals(pat, segs[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return d;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                try { d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            }
            return d;
        }

        public ApiResult Handle(string method, string path, string query, string body, GameState state)
        {
            var ctx = new RouteContext { Method = method, Path = path, State = state, Query = ParseQuery(query) };
            Route found = null;
            foreach (var r in _routes)
            {
                if (r.Method == method && Match(r, path, ctx.Args)) { found = r; break; }
            }
            if (found == null) return ApiResult.Fail("not_found", method + " " + path);
            if (found.NeedsGame && !state.GameLoaded) return ApiResult.Fail("no_game", "no game is loaded");

            bool write = IsWrite(method);
            if (write)
            {
                JToken tok;
                try { tok = string.IsNullOrWhiteSpace(body) ? null : JToken.Parse(body); }
                catch (Exception e) { return ApiResult.Fail("bad_request", "bad json: " + e.Message); }
                var obj = tok as JObject;
                if (obj == null) return ApiResult.Fail("bad_request", "body must be a JSON object");
                ctx.Body = obj;
                string opId = obj.Value<string>("op_id");
                if (string.IsNullOrWhiteSpace(opId)) return ApiResult.Fail("bad_request", "op_id is required on writes");
                ctx.OpId = opId;
                JObject original;
                if (OpIds.TryGet(opId, out original))
                    return ApiResult.Fail("duplicate", "op_id was already applied", new JObject { ["result"] = original });
                JToken exp = obj["expires_tick"];
                if (exp != null && exp.Type != JTokenType.Null)
                {
                    if (exp.Type != JTokenType.Integer && exp.Type != JTokenType.Float)
                        return ApiResult.Fail("bad_request", "expires_tick must be a number");
                    if (state.GameLoaded && state.Tick > exp.Value<long>())
                        return ApiResult.Fail("expired", "expires_tick " + exp.Value<long>() + " is before tick " + state.Tick);
                }
            }

            ApiResult result;
            try { result = found.Handler(ctx); }
            catch (Exception e)
            {
                LogWarning("Error handling " + method + " " + path + ": " + e);
                return ApiResult.Fail("internal", e.Message);
            }
            if (write && result.Ok) OpIds.Put(ctx.OpId, result.Body);
            return result;
        }
    }
}

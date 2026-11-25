using System;
using System.Linq;
using HermesVoice.Core;
using Newtonsoft.Json.Linq;
using Verse;

namespace HermesVoice
{
    /// <summary>Endpoint table. Handlers run on the game thread.</summary>
    public static partial class Routes
    {
        static bool _registered;

        public static void Register(ApiRouter r)
        {
            if (_registered) return;
            _registered = true;
            r.Add("GET", "/events", false, Events);
            RegisterSnapshots(r);
            RegisterWrites(r);
        }

        static ApiResult Events(RouteContext c)
        {
            long since = 0;
            string q = c.Q("since");
            if (!string.IsNullOrEmpty(q) && !long.TryParse(q, out since)) return ApiResult.Fail("bad_request", "since must be an integer");
            var page = HermesBridge.Events.Since(since);
            return ApiResult.Success(new JObject
            {
                ["events"] = new JArray(page.Events),
                ["next_seq"] = page.NextSeq,
                ["gap"] = page.Gap
            });
        }
    }
}

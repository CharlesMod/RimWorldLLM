using System;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>GET /inspect, /find and /survey: the narrator's read tools. Shapes are checked here, the facts come from IInspectHands.
    /// Wire with: VivariumRoutesInspect.Register(router, () => new VivariumInspectHands());</summary>
    public static class VivariumRoutesInspect
    {
        public const int MaxTarget = 60, MaxNear = 40;

        public static void Register(ApiRouter router, Func<IInspectHands> hands)
        {
            router.Add("GET", "/inspect", true, c =>
            {
                string target = (c.Q("target") ?? "").Trim();
                if (target.Length == 0) return Bad("target is required: a colonist, animal or prisoner name, a room label, a zone, or #id");
                if (target.Length > MaxTarget) return Bad("target is at most " + MaxTarget + " characters");
                var h = hands();
                if (h == null) return NoGame();
                return Wrap(h.Inspect(target));
            });
            router.Add("GET", "/find", true, c =>
            {
                string what = (c.Q("what") ?? "").Trim();
                FindKind k;
                if (!FindKinds.Parse(what, out k)) return Bad("what must be one of: " + FindKinds.List());
                string near = (c.Q("near") ?? "").Trim(), item = (c.Q("item") ?? "").Trim();
                if (near.Length > MaxNear) return Bad("near is at most " + MaxNear + " characters");
                if (item.Length > MaxNear) return Bad("item is at most " + MaxNear + " characters");
                var h = hands();
                if (h == null) return NoGame();
                return Wrap(h.Find(FindKinds.Word(k), near, item));
            });
            router.Add("GET", "/survey", true, c =>
            {
                string what = (c.Q("what") ?? "").Trim().ToLowerInvariant();
                if (what != "stock" && what != "threats") return Bad("what must be stock or threats");
                var h = hands();
                if (h == null) return NoGame();
                return Wrap(h.Survey(what));
            });
        }

        static ApiResult Wrap(JObject d)
        {
            if (d == null) return NoGame();
            if (d["refused"] != null) return ApiResult.Fail("refused", (string)d["refused"]);
            return ApiResult.Success(d);
        }

        static ApiResult NoGame() { return ApiResult.Fail("no_game", "no game is loaded"); }
        static ApiResult Bad(string detail) { return ApiResult.Fail("bad_request", detail); }
    }
}

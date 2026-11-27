using System;

namespace RimWorldLLM.Bridge
{
    /// <summary>The endpoint settings pairing fills (the same meaning as RimWorldLLMSettings' llm* fields).</summary>
    public sealed class EndpointFields
    {
        public string Source = "local";   // "local" | "own"
        public string BaseUrl = "";
        public string Model = "";
        public string ApiKey = "";
        public string EngineId = "";      // the paired engine the fields came from; "" for a typed endpoint
    }

    /// <summary>
    /// How a pairing result, a start-up check or a 401 changes the endpoint settings. Pure: the game side copies the
    /// fields in and out. Pairing only fills the own-endpoint fields; a typed endpoint (EngineId "") is never touched.
    /// </summary>
    public static class PairingApply
    {
        /// <summary>A pairing (or a no-token connection) succeeded: store the token by engine_id and point the endpoint at it.</summary>
        public static void Outcome(EndpointFields f, PairOutcome o, PairStore store)
        {
            if (f == null || o == null || o.Engine == null) return;
            bool sameEngine = f.EngineId == o.Engine.EngineId;
            store.Put(o.Engine);   // with its token, or none for an engine that needs none (so Typed knows its address)
            f.Source = "own";
            f.BaseUrl = EngineAddress.OpenAiBase(o.Root);
            if (!string.IsNullOrEmpty(o.ModelId)) f.Model = o.ModelId;
            else if (!sameEngine) f.Model = "";   // filled by the next check once the engine lists its model
            f.ApiKey = o.Engine.Token ?? "";
            f.EngineId = o.Engine.EngineId;
        }

        /// <summary>The start-up check: follow a moved engine, drop a token it no longer accepts. True when a field changed.</summary>
        public static bool Reconnect(EndpointFields f, ReconnectResult r, PairStore store)
        {
            if (f == null || r == null || !r.Found || string.IsNullOrEmpty(f.EngineId)) return false;
            bool changed = false;
            if (r.Moved && !string.IsNullOrEmpty(r.Root))
            {
                string b = EngineAddress.OpenAiBase(r.Root);
                if (b != f.BaseUrl) { f.BaseUrl = b; changed = true; }
                store.Remember(f.EngineId, r.Root);
            }
            if (r.TokenCleared || EngineAuthCodes.PairAgain(r.Auth))
            {
                store.ClearToken(f.EngineId);
                if (f.ApiKey.Length > 0) { f.ApiKey = ""; changed = true; }
            }
            else if (r.Auth == EngineAuth.Paired)
            {
                var pe = store.Get(f.EngineId);
                if (pe != null && pe.HasToken && f.ApiKey != pe.Token) { f.ApiKey = pe.Token; changed = true; }
            }
            if ((r.Auth == EngineAuth.Paired || r.Auth == EngineAuth.Open) && !string.IsNullOrEmpty(r.ModelId) && f.Model != r.ModelId)
            { f.Model = r.ModelId; changed = true; }
            return changed;
        }

        /// <summary>A 401 code seen on any path while the endpoint is a paired engine. True when the token was dropped.</summary>
        public static bool Auth(EndpointFields f, EngineAuth a, PairStore store)
        {
            if (f == null || string.IsNullOrEmpty(f.EngineId) || f.Source != "own" || !EngineAuthCodes.PairAgain(a)) return false;
            store.ClearToken(f.EngineId);
            if (f.ApiKey.Length == 0) return false;
            f.ApiKey = "";
            return true;
        }

        /// <summary>The person typed another URL by hand: the endpoint is no longer the paired engine's. True when it let go.</summary>
        public static bool Typed(EndpointFields f, PairStore store)
        {
            if (f == null || string.IsNullOrEmpty(f.EngineId)) return false;
            var pe = store.Get(f.EngineId);
            string root = EngineAddress.RootOfBase(f.BaseUrl);
            if (pe != null && root != null && string.Equals(root, pe.Address, StringComparison.OrdinalIgnoreCase)) return false;
            f.EngineId = "";
            return true;
        }
    }
}

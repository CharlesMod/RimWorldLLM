using System.Threading;
using System.Threading.Tasks;

namespace RimWorldLLM.Bridge
{
    /// <summary>What "Pair with this engine" found at the typed endpoint.</summary>
    public enum EndpointPairKind
    {
        NotAnEngine,   // hello failed, or it is not an easy-engine: change nothing
        Adopt,         // the key already in llmApiKey works as a Bearer (or none is needed): store it, no pairing round
        Pair,          // the engine wants a token this game lacks: run the code / Allow flow
    }

    /// <summary>The result of probing an endpoint. The key is never kept here and never printed.</summary>
    public sealed class EndpointProbe
    {
        public EndpointPairKind Kind;
        public string Root;
        public EngineHello Hello;
        public bool NeedsNoToken;   // adopt without a token (the engine is open)
        public override string ToString() { return "EndpointProbe(" + Kind + ", " + Root + ")"; }
    }

    /// <summary>
    /// The "Pair with this engine" button for an endpoint typed by hand (llmBaseUrl + llmApiKey): pure decisions and the
    /// probe. The token is only ever sent as a Bearer; nothing here logs it.
    /// </summary>
    public static class EndpointPairing
    {
        /// <summary>"http://sprinter:47480/v1/" → "http://sprinter:47480"; no port → 47480; null when not a URL.</summary>
        public static string RootOf(string baseUrl) { return EngineAddress.RootOfBase(baseUrl); }

        /// <summary>Offer the button: an endpoint is set and it is not already a paired engine (no id, or no stored token for it).</summary>
        public static bool ShouldOffer(string baseUrl, string engineId, PairStore store)
        {
            if (RootOf(baseUrl) == null) return false;
            if (string.IsNullOrEmpty(engineId)) return true;
            return store == null || !store.HasToken(engineId);
        }

        /// <summary>
        /// The pure decision. <paramref name="statusOk"/> is "an authenticated GET /engine/v1/status answered 200": a master
        /// token reports paired:true client "master" on hello, but status is the proof that does not depend on that.
        /// </summary>
        public static EndpointPairKind Decide(EngineHello h, bool hadKey, bool statusOk)
        {
            if (h == null || !h.IsEasyEngine) return EndpointPairKind.NotAnEngine;
            var a = EngineAuthCodes.FromHello(h, hadKey);
            if (a == EngineAuth.Open || a == EngineAuth.Paired || statusOk) return EndpointPairKind.Adopt;
            return EndpointPairKind.Pair;
        }

        public static async Task<EndpointProbe> ProbeAsync(PairingApi api, string baseUrl, string key, CancellationToken ct = default(CancellationToken))
        {
            string root = RootOf(baseUrl);
            var p = new EndpointProbe { Root = root, Kind = EndpointPairKind.NotAnEngine };
            if (root == null) return p;
            bool hasKey = !string.IsNullOrEmpty(key);
            var hr = await api.HelloAsync(root, hasKey ? key : null, ct).ConfigureAwait(false);
            p.Hello = hr.hello;
            if (hr.hello == null || !hr.hello.IsEasyEngine) return p;
            bool statusOk = false;
            if (hasKey && EngineAuthCodes.FromHello(hr.hello, true) != EngineAuth.Paired && hr.hello.TokenRequired)
            {
                var sr = await api.StatusAsync(root, key, ct).ConfigureAwait(false);
                statusOk = sr.Status == 200;
            }
            p.Kind = Decide(hr.hello, hasKey, statusOk);
            p.NeedsNoToken = p.Kind == EndpointPairKind.Adopt && !hr.hello.TokenRequired;
            return p;
        }

        /// <summary>Adopt: keep the typed key, store it under the engine_id and set the id. URL stays as typed; the model too unless hello names one (model_id).</summary>
        public static void Adopt(EndpointFields f, EndpointProbe p, PairStore store)
        {
            if (f == null || p == null || p.Hello == null || p.Kind != EndpointPairKind.Adopt) return;
            store.Put(new PairedEngine
            {
                EngineId = p.Hello.EngineId,
                Token = p.NeedsNoToken ? "" : (f.ApiKey ?? ""),
                ClientId = p.Hello.ClientId ?? "",
                Name = p.Hello.Name ?? "",
                Address = p.Root,
            });
            f.Source = "own";
            f.EngineId = p.Hello.EngineId;
            if (!string.IsNullOrEmpty(p.Hello.ModelId)) f.Model = p.Hello.ModelId;   // else the model stays as typed
        }
    }
}

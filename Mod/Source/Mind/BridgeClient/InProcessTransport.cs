using System;
using System.Threading;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;

namespace RimWorldLLM.Mind
{
    /// <summary>
    /// The in-process transport: requests go into the BridgeServer's pending queue and are served by its Pump on the
    /// game thread (ApiRouter.Handle, op_id dedup, expiry, the envelope), with no HTTP and no thread blocked.
    /// The server is fetched per call so a restart of the HTTP side (BridgeRuntime.Start/Stop) does not strand it.
    /// In-process requests are trusted: Authorization is ignored.
    /// </summary>
    public sealed class InProcessTransport : IBridgeTransport
    {
        readonly Func<BridgeServer> _server;

        public InProcessTransport(Func<BridgeServer> server) { _server = server; }
        public InProcessTransport(BridgeServer server) : this(() => server) { }

        public async Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
        {
            var s = _server();
            if (s == null)
                throw new BridgeTransportException("no bridge server");
            var r = await s.SubmitAsync(request.Method, request.Path, request.QueryString(), request.JsonBody ?? "", ct).ConfigureAwait(false);
            return new BridgeReply(r.Key, r.Value);
        }
    }
}

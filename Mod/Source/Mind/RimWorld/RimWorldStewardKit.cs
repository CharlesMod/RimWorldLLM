using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The RimWorld StewardKit as the core Steward's IStewardKit (the adapter's StewardKit object).</summary>
    public sealed class RimWorldStewardKit : IStewardKit
    {
        private readonly StewardKit _k;
        public RimWorldStewardKit(StewardKit k) { _k = k; }
        public string EventLine(JObject e) { return _k.EventLine(e); }
        public string Wake(JObject e) { return _k.Wake(e); }
        public string DefaultStrategy() { return _k.DefaultStrategy(); }
        public string SystemPrompt(bool advisor) { return _k.SystemPrompt(advisor); }
        public IStewardSession Session(JObject digest, IBridge bridge, bool advisor) { return new Sess(_k.Session(digest, bridge, advisor)); }
        public string PlaybookAlerts(JObject digest) { return _k.PlaybookAlerts(digest); }
        public string RenderDigest(JObject digest) { return _k.RenderDigest(digest); }

        private sealed class Sess : IStewardSession
        {
            private readonly StewardSession _s;
            public Sess(StewardSession s) { _s = s; }
            public IReadOnlyList<ToolSpec> Tools { get { return _s.Tools; } }
            public ISet<string> Acting { get { return _s.Acting; } }
            public async Task<StewardOutcome> RunAsync(string tool, JObject values)
            {
                var o = await _s.RunAsync(tool, values).ConfigureAwait(false);
                return new StewardOutcome { Result = o.Result, Verb = o.Verb, Note = o.Note, Ok = o.Ok };
            }
        }
    }
}

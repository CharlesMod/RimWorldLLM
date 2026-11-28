using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>The RimWorld StewardKit as the core Steward's IStewardKit (the adapter's StewardKit object).</summary>
    public sealed class RimWorldStewardKit : IStewardKit, IStewardCombatKit, IStewardTradeKit, IStewardVisionKit, IStewardCatalogKit
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
        public string StrategyExtra(JObject digest) { return _k.StrategyExtra(digest); }
        public IReadOnlyList<ToolSpec> Declared(bool advisor, bool withLook)
            => withLook ? StewardCatalog.Tools(advisor) : StewardCatalog.Tools(advisor).Where(t => t.Name != VisionKit.Name).ToList();
        public string AvailableNow(IEnumerable<ToolSpec> callable, IReadOnlyList<ToolSpec> declared) => StewardCatalog.AvailableNow(callable, declared);
        public IVisionLook NewLook(JObject digest, IBridge bridge, System.Func<int, Task> delay, ILookClient look) { return digest != null && digest["build"] is JObject ? new VisionKit(bridge, delay, look) : null; }
        public IReadOnlyCollection<string> TradeTools { get { return StewardKit.TradeToolNames; } }
        public IStewardSession TradeSession(JObject digest, IBridge bridge) { return new Sess(_k.TradeSession(digest, bridge)); }
        public string TradeSystemPrompt() { return _k.TradeSystemPrompt(); }
        public string TradeSkip(JObject digest) { return _k.TradeSkip(digest); }
        public (string View, string Food) RenderTrade(JObject digest, IList<string> arrived) { return _k.RenderTrade(digest, arrived); }
        public IReadOnlyCollection<string> CombatTools { get { return StewardKit.CombatToolNames; } }
        public IStewardSession SessionAfterFight(JObject digest, IBridge bridge, bool advisor) { return new Sess(_k.Session(digest, bridge, advisor, true)); }
        public IStewardSession CombatSession(JObject view, IBridge bridge) { return new Sess(_k.CombatSession(view, bridge)); }
        public string CombatSystemPrompt() { return _k.CombatSystemPrompt(); }
        public string RenderCombat(JObject view) { return _k.RenderCombat(view); }

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

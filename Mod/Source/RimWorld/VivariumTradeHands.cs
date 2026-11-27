using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;

namespace RimWorldLLM
{
    /// <summary>Phase 4 game bodies (trade, caravans). Placeholder so the mod builds: the game worker replaces it.</summary>
    public sealed partial class VivariumHands
    {
        public JObject TradeOffer(string with, TradeLimits limits) { return new JObject { ["refused"] = "not built yet" }; }
        public HandResult Trade(TradeArgs a, TradeLimits limits, TradeDay day) { return HandResult.No("not built yet"); }
        public JObject CaravanPlan(CaravanArgs a, TradeLimits limits) { return new JObject { ["refused"] = "not built yet" }; }
        public HandResult Caravan(CaravanArgs a, int maxPawns) { return HandResult.No("not built yet"); }
    }
}

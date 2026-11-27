using System;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Walking trades: every 60 ticks looks at the pending trade (observe, decide, act). The decision and the deal
    /// orchestration are in TradeWalk.Step (Verse-free, tested); this only observes and executes. Game thread only.</summary>
    public static class VivariumTradeWatch
    {
        const int EveryTicks = 60;

        public static void Tick()
        {
            if (Find.TickManager == null || Find.TickManager.TicksGame % EveryTicks != 0) return;
            var v = VivariumGame.State;
            if (v == null || v.TradeWalk == null) return;
            if (!EventGate.GameLive() || Current.Game == null) return;
            var hands = new VivariumHands();
            TradeWalk.Step(v, VivariumGame.NowTick, hands, hands.TradeObserve, hands.TradeReissue);
        }
    }
}

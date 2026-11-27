using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Game-side entry points for the Vivarium (the narrator plays). One accessor for the state so the
    /// merge with the bridge worker's BridgeRuntime.Vivarium field is a one-line change.</summary>
    public static class VivariumGame
    {
        // TEMP until the merge: BridgeRuntime gets `public readonly Vivarium Vivarium = new Vivarium();`; then this
        // becomes `RWLBridge.Runtime.Vivarium` (delete _temp).
        static readonly Vivarium _temp = new Vivarium();
        public static Vivarium State { get { return _temp; } }

        public static int NowTick { get { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; } }
    }
}

using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Game-side entry points for the Vivarium (the narrator plays). One accessor for the state so the
    /// merge with the bridge worker's BridgeRuntime.Vivarium field is a one-line change.</summary>
    public static class VivariumGame
    {
        public static Vivarium State { get { return RWLBridge.Runtime.Vivarium; } }

        /// <summary>The "from" of a waiting talk request ("chat" for the chat bar's @Name), or null.</summary>
        public static string TalkFrom(string talkId)
        {
            foreach (var t in RWLBridge.Talk.ToJson())
                if ((string)t["id"] == talkId) return (string)t["from"];
            return null;
        }

        public static int NowTick { get { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; } }
    }
}

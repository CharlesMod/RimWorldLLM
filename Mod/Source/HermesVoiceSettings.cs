using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Mod settings. Public fields are auto-persisted by RimWorld (ModSettings is IExposable).
    /// </summary>
    public class HermesVoiceSettings : ModSettings
    {
        public bool enabled = true;

        // In-mod HTTP bridge: the Hermes agent reads colony state and issues /say commands here.
        public int bridgePort = 8765;
        public string bindAddress = "0.0.0.0";

        // Dial-out: when a colony event fires, the mod POSTs to this URL (your agent endpoint).
        public string agentUrl = "";

        // Shared token: validated on inbound bridge requests, sent on outbound dial-outs.
        public string sharedToken = "";

        // Dialogue display.
        public bool showBubbles = true;
        public int bubbleDurationSec = 8;
    }
}

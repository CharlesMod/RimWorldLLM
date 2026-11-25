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
        public int bridgePort = 9765;
        public string bindAddress = "127.0.0.1";

        // Dial-out: when a colony event fires, the mod POSTs to this URL (your agent endpoint).
        public string agentUrl = "";

        // Shared token: validated on inbound bridge requests, sent on outbound dial-outs.
        public string sharedToken = "";

        // Dialogue display.
        public bool showBubbles = true;
        public int bubbleDurationSec = 8;

        // Colony events (improve/events): master toggle + one per kind.
        public bool sendColonyEvents = true;
        public bool evInjury = true;
        public bool evDeath = true;
        public bool evMentalBreak = true;
        public bool evLevelUp = true;
        public bool evTend = true;
        public bool evSocial = true;
        public bool evRaid = true;
        public bool evQuest = true;
        public bool evLodger = true;
        public bool evPrisoner = true;
        public bool evFuneral = true;
        public bool evWeather = true;
        public bool evThought = true;

        // What the agent must honour (GET /config).
        public string effects = "normal";          // off | light | normal
        public float talkFrequency = 1f;
        public int pauseAmbientAtSpeed = 3;
        public string language = "en";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref bridgePort, "bridgePort", 9765);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) bridgePort = Core.SettingsMigration.Port(bridgePort);
            Scribe_Values.Look(ref bindAddress, "bindAddress", "127.0.0.1");
            Scribe_Values.Look(ref agentUrl, "agentUrl", "");
            Scribe_Values.Look(ref sharedToken, "sharedToken", "");
            Scribe_Values.Look(ref showBubbles, "showBubbles", true);
            Scribe_Values.Look(ref bubbleDurationSec, "bubbleDurationSec", 8);
            Scribe_Values.Look(ref sendColonyEvents, "sendColonyEvents", true);
            Scribe_Values.Look(ref evInjury, "evInjury", true);
            Scribe_Values.Look(ref evDeath, "evDeath", true);
            Scribe_Values.Look(ref evMentalBreak, "evMentalBreak", true);
            Scribe_Values.Look(ref evLevelUp, "evLevelUp", true);
            Scribe_Values.Look(ref evTend, "evTend", true);
            Scribe_Values.Look(ref evSocial, "evSocial", true);
            Scribe_Values.Look(ref evRaid, "evRaid", true);
            Scribe_Values.Look(ref evQuest, "evQuest", true);
            Scribe_Values.Look(ref evLodger, "evLodger", true);
            Scribe_Values.Look(ref evPrisoner, "evPrisoner", true);
            Scribe_Values.Look(ref evFuneral, "evFuneral", true);
            Scribe_Values.Look(ref evWeather, "evWeather", true);
            Scribe_Values.Look(ref evThought, "evThought", true);
            Scribe_Values.Look(ref effects, "effects", "normal");
            Scribe_Values.Look(ref talkFrequency, "talkFrequency", 1f);
            Scribe_Values.Look(ref pauseAmbientAtSpeed, "pauseAmbientAtSpeed", 3);
            Scribe_Values.Look(ref language, "language", "en");
        }
    }
}

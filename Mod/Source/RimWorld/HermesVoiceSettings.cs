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

        // The agent's OpenAI-compatible LLM endpoint. The agent reads URL and model from GET /config and the key
        // from bridge.json (owner-only); the key itself never travels through the bridge API.
        public string llmBaseUrl = "";
        public string llmModel = "";
        public string llmApiKey = "";

        // Where the agent's LLM comes from: "local" (the HermesVoice engine, default) or "own" (the fields above).
        public string llmSource = "local";
        // Launch or attach the local engine when the game starts.
        public bool engineAutoStart = true;
        // The player answered the first-run "set up a local model" prompt with Set up or My own endpoint.
        public bool engineSetupDismissed = false;

        // The player asked not to be told (once per session) that the smallest fallback model is in use.
        public bool hideFallbackWarning = false;

        // Dialogue display.
        public bool showBubbles = true;
        public int bubbleDurationSec = 8;
        public bool colorBubbles = true;
        public string bubbleStyle = "comic";   // comic | classic
        public float bubbleOpacity = 1f;
        public bool comicLettering = false;     // ALL-CAPS comic lettering

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
            if (Scribe.mode == LoadSaveMode.PostLoadInit) bridgePort = Bridge.SettingsMigration.Port(bridgePort);
            Scribe_Values.Look(ref bindAddress, "bindAddress", "127.0.0.1");
            Scribe_Values.Look(ref agentUrl, "agentUrl", "");
            Scribe_Values.Look(ref sharedToken, "sharedToken", "");
            Scribe_Values.Look(ref llmBaseUrl, "llmBaseUrl", "");
            Scribe_Values.Look(ref llmModel, "llmModel", "");
            Scribe_Values.Look(ref llmApiKey, "llmApiKey", "");
            // Saved files from before the engine have no llmSource: keep a configured own endpoint, else default to local.
            Scribe_Values.Look(ref llmSource, "llmSource", "");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(llmSource))
                llmSource = string.IsNullOrWhiteSpace(llmBaseUrl) ? "local" : "own";
            Scribe_Values.Look(ref engineAutoStart, "engineAutoStart", true);
            Scribe_Values.Look(ref engineSetupDismissed, "engineSetupDismissed", false);
            Scribe_Values.Look(ref hideFallbackWarning, "hideFallbackWarning", false);
            Scribe_Values.Look(ref showBubbles, "showBubbles", true);
            Scribe_Values.Look(ref bubbleDurationSec, "bubbleDurationSec", 8);
            Scribe_Values.Look(ref colorBubbles, "colorBubbles", true);
            Scribe_Values.Look(ref bubbleStyle, "bubbleStyle", "comic");
            Scribe_Values.Look(ref bubbleOpacity, "bubbleOpacity", 1f);
            Scribe_Values.Look(ref comicLettering, "comicLettering", false);
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

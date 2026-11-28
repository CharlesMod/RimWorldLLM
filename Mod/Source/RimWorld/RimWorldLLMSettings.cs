using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Mod settings. Public fields are auto-persisted by RimWorld (ModSettings is IExposable).
    /// </summary>
    public class RimWorldLLMSettings : ModSettings
    {
        public bool enabled = true;

        // In-mod HTTP bridge: the agent reads colony state and issues /say commands here.
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

        // Where the agent's LLM comes from: "local" (the RimWorldLLM engine, default) or "own" (the fields above).
        public string llmSource = "local";
        // Pairing with an easyEngine on another machine (docs/SETTINGS.md "Finding and pairing an engine"). Pairing only
        // fills llmBaseUrl / llmModel / llmApiKey above (the token is the key); these remember which engine that was.
        // llmEngineId: the engine_id the own endpoint was filled from ("" for a typed endpoint).
        public string llmEngineId = "";
        // pairedEngines: JSON list of {engine_id, token, client_id, name, address} (Bridge.PairStore). Never logged.
        public string pairedEngines = "";
        // How the "own" source is shown: "pc" (an engine on another PC on my network, found and paired) or "endpoint"
        // (a URL typed by hand). Both fill the same fields above; this only picks the settings view.
        public string llmOwnKind = "";
        // Host: share this PC's local engine with other PCs on the player's networks (LAN and Tailscale), pairing on.
        // Off by default: a one-click install serves this PC only (docs/SETTINGS.md "Sharing this PC's engine").
        public bool shareEngine = ShareDefaults.ShareEngine;
        // Launch or attach the local engine when the game starts.
        public bool engineAutoStart = true;
        // The player answered the first-run "set up a local model" prompt with Set up or My own endpoint.
        public bool engineSetupDismissed = false;

        // The player asked not to be told (once per session) that the smallest fallback model is in use.
        public bool hideFallbackWarning = false;
        // The last .gguf path typed or picked in "Use my own GGUF file" (a convenience; the engine holds the real choice).
        public string customGgufPath = "";

        // Dialogue display.
        public bool showBubbles = true;
        public int bubbleDurationSec = 8;
        public bool colorBubbles = true;
        public string bubbleStyle = "comic";   // comic | classic
        public float bubbleOpacity = 1f;
        public bool comicLettering = false;     // ALL-CAPS comic lettering
        public bool showThoughts = true;        // pawn thoughts as cloud balloons on the map (comic style)
        public float thoughtOpacity = 0.6f;     // share of the bubble opacity a thought cloud uses
        public int thoughtPerPawnSec = 120;     // one thought cloud per pawn at most this often
        public int thoughtGlobalSec = 20;       // and one anywhere at most this often

        // Director camera (alive/camera): follows the most interesting pawn; any manual camera input pauses it.
        public bool directorCameraAllowed = true;    // the toggle (key, log button) exists
        public bool directorCameraOnAtStart = false;  // following is intrusive: off until the player turns it on
        public float directorPanSpeed = 2.5f;         // how quickly the camera closes in on its subject (1/seconds)

        // Watch mode (unattended play): the camera director cuts between events by itself. Setting, not per game.
        public bool watchMode = false;
        public int watchPlayerPauseSec = 20;         // the player touching the camera pauses the director this long
        public bool watchCaptions = true;            // one short 'Camera: ...' chat line per shot of interest
        public bool watchSelectFollowed = false;     // select the followed pawn (opens the inspect pane, so off by default)
        public bool watchZoom = true;                // zoom per shot (close for meals, wide for raids); the player's zoom comes back on exit
        public int watchPace = 1;                    // 0 calm, 1 normal, 2 lively: holds and the no-repeat window
        public bool watchSceneMeals = true, watchSceneRecreation = true, watchSceneWork = true, watchSceneNight = true,
            watchSceneMedical = true, watchSceneChildren = true, watchSceneRomance = true;   // everyday scenes by group

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
        public bool evArt = true;
        public bool evSleep = true;                     // sleep_start events (feed dreams)
        public bool shareFooter = true;                 // "Share this moment": add the RimWorldLLM footer to shared text and images
        public bool dreamsEnabled = true;               // sleepers get a dream cloud (POST /thought kind "dream"); also GET /config dreams
        public bool evFirsts = true;
        public bool evGatherings = true;                // gathering / gathering_end events (meals, parties, weddings, rituals, speeches)

        // What the agent must honour (GET /config).
        public string effects = "normal";          // off | light | normal
        public float talkFrequency = 1f;
        public int pauseAmbientAtSpeed = 4;           // 4 = dev speed; ambient talk keeps going at 3x (the agent budgets it)
        public bool textGen = true;                    // per-instance written texts (docs/TEXT-POOLS.md); also GET /config text_fill
        public bool textArt = true, textGraves = true, textBooks = true, textSocial = true;
        public bool textCombat = false;                // combat lines come fast; off unless the player wants them
        public bool artDescriptions = true;            // show the agent's art descriptions on the info card; also GET /config art_descriptions
        public bool wantsEnabled = true;               // pawns act on their wants (POST /social_action); also GET /config wants_enabled
        public bool recapOnLoad = true;                // "Previously on..." letter after loading a save; also GET /config recap
        public bool adultContent = false;              // explicit sexual content between adult colonists; OFF by default; also GET /config adult_content. The agent's age gate ignores it.
        public float wantsAvoidHours = 1f;             // how long an "avoid" want lasts, in game hours

        // Quotes: show the line that caused a thought or log entry; voice the game's own social interactions.
        public bool showQuotes = true;
        public bool voiceVanillaSocial = true;
        public int quoteMaxChars = 140;
        public int attachRealFloorSec = 5;                           // ... or this many real seconds, whichever is longer (dual clock)
        public int attachWindowMinutes = 10;                         // in-game minutes a line may take to attach to its interaction
        public string interactionHigh = Bridge.InteractionPolicy.DefaultHigh;   // def names, voiced first
        public string interactionIdle = Bridge.InteractionPolicy.DefaultIdle;   // def names, voiced only when the speaker is otherwise idle

        // Vivarium (the narrator plays).
        public bool narratorMayPauseForEmergencies = true;
        public bool narratorControlsSpeed = false;
        /// <summary>Biotech: the narrator may set a colonist to receive a xenogerm (never children). Default off.</summary>
        public bool narratorMayLook = true;            // the narrator may take a picture of the map (never the UI) when planning the base; GET /config vision_allowed; used only when the engine supports images
        public bool narratorMayImplantGenes = false;
        /// <summary>The narrator may order a prisoner executed (prisoner tool). Default off.</summary>
        public bool narratorMayExecutePrisoners = false;
        /// <summary>The narrator may slaughter named or bonded animals. Default off.</summary>
        public bool narratorMaySlaughterPets = false;
        public bool narratorSlowsForCombat = true;
        public float stewardHeartbeatHours = 6f;
        public bool narratorAnswerDialogs = true;
        public int narratorDialogTimeoutS = 90;
        public bool narratorCommentary = true;
        // Unattended play: the mod's own rotating autosave (only while the game's autosave is off) and what happens when the colony is lost.
        public bool narratorAutosave = true;
        public float narratorAutosaveDays = 1f;
        public int narratorAutosaveKeep = 5;
        public bool narratorTrade = true;
        public bool narratorCaravans = true;
        public int narratorTradeMaxDeal = 2000;      // 100..20000
        public int narratorTradeMaxDay = 6000;       // 100..50000
        public int narratorTradeDealsPerDay = 3;     // 1..10
        public int narratorKeepMedicine = 10;        // 0..50
        public int narratorKeepFoodDays = 10;        // 0..30
        public string narratorNeverSell = "";
        public int narratorCaravanMaxPawns = 3;      // 1..6
        public int narratorRestartMode = 0;   // 0 ask, 1 auto, 2 off (Bridge.RestartMode)

        // The chat panel (always on, MOBA style).
        public bool chatPanelShow = true;
        public bool chatPanelRight = false;
        public bool chatPanelMedium = false;     // font: false = small, true = medium
        public float chatPanelOpacity = 0.3f;    // background alpha while the mouse is elsewhere
        public float chatPanelWidth = 420f;
        public int chatPanelLines = 10;
        // Where the player dragged/resized the log box, as fractions of the screen; negative = not customised (the side + width/lines above).
        public float chatPanelX = -1f, chatPanelY = -1f, chatPanelW = -1f, chatPanelH = -1f;
        // Per-group line filters (chips in the panel's title strip).
        public bool chatShowTalk = true, chatShowThoughts = true, chatShowNarrator = true, chatShowActions = true, chatShowStory = true;
        public bool ChatCustom { get { return chatPanelX >= 0f && chatPanelY >= 0f && chatPanelW > 0f && chatPanelH > 0f; } }
        public void ChatResetPosition() { chatPanelX = chatPanelY = chatPanelW = chatPanelH = -1f; }

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
            Scribe_Values.Look(ref llmEngineId, "llmEngineId", "");
            Scribe_Values.Look(ref pairedEngines, "pairedEngines", "");
            Scribe_Values.Look(ref llmOwnKind, "llmOwnKind", "");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(llmOwnKind))
                llmOwnKind = string.IsNullOrEmpty(llmEngineId) ? "endpoint" : "pc";
            Scribe_Values.Look(ref shareEngine, "shareEngine", ShareDefaults.ShareEngine);
            Scribe_Values.Look(ref engineAutoStart, "engineAutoStart", true);
            Scribe_Values.Look(ref engineSetupDismissed, "engineSetupDismissed", false);
            Scribe_Values.Look(ref hideFallbackWarning, "hideFallbackWarning", false);
            Scribe_Values.Look(ref customGgufPath, "customGgufPath", "");
            // The old "language" key (saved by earlier versions, read by nothing) is no longer written; an old file that
            // still has it loads fine because Scribe ignores XML nodes nobody asks for.
            Scribe_Values.Look(ref showBubbles, "showBubbles", true);
            Scribe_Values.Look(ref bubbleDurationSec, "bubbleDurationSec", 8);
            Scribe_Values.Look(ref colorBubbles, "colorBubbles", true);
            Scribe_Values.Look(ref bubbleStyle, "bubbleStyle", "comic");
            Scribe_Values.Look(ref bubbleOpacity, "bubbleOpacity", 1f);
            Scribe_Values.Look(ref comicLettering, "comicLettering", false);
            Scribe_Values.Look(ref showThoughts, "showThoughts", true);
            Scribe_Values.Look(ref thoughtOpacity, "thoughtOpacity", 0.6f);
            Scribe_Values.Look(ref thoughtPerPawnSec, "thoughtPerPawnSec", 120);
            Scribe_Values.Look(ref thoughtGlobalSec, "thoughtGlobalSec", 20);
            Scribe_Values.Look(ref directorCameraAllowed, "directorCameraAllowed", true);
            Scribe_Values.Look(ref directorCameraOnAtStart, "directorCameraOnAtStart", false);
            Scribe_Values.Look(ref directorPanSpeed, "directorPanSpeed", 2.5f);
            Scribe_Values.Look(ref watchMode, "watchMode", false);
            Scribe_Values.Look(ref watchPlayerPauseSec, "watchPlayerPauseSec", 20);
            Scribe_Values.Look(ref watchCaptions, "watchCaptions", true);
            Scribe_Values.Look(ref watchSelectFollowed, "watchSelectFollowed", false);
            Scribe_Values.Look(ref watchZoom, "watchZoom", true);
            Scribe_Values.Look(ref watchPace, "watchPace", 1);
            Scribe_Values.Look(ref watchSceneMeals, "watchSceneMeals", true);
            Scribe_Values.Look(ref watchSceneRecreation, "watchSceneRecreation", true);
            Scribe_Values.Look(ref watchSceneWork, "watchSceneWork", true);
            Scribe_Values.Look(ref watchSceneNight, "watchSceneNight", true);
            Scribe_Values.Look(ref watchSceneMedical, "watchSceneMedical", true);
            Scribe_Values.Look(ref watchSceneChildren, "watchSceneChildren", true);
            Scribe_Values.Look(ref watchSceneRomance, "watchSceneRomance", true);
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
            Scribe_Values.Look(ref evArt, "evArt", true);
            Scribe_Values.Look(ref evSleep, "evSleep", true);
            Scribe_Values.Look(ref shareFooter, "shareFooter", true);
            Scribe_Values.Look(ref dreamsEnabled, "dreamsEnabled", true);
            Scribe_Values.Look(ref evFirsts, "evFirsts", true);
            Scribe_Values.Look(ref evGatherings, "evGatherings", true);
            Scribe_Values.Look(ref artDescriptions, "artDescriptions", true);
            Scribe_Values.Look(ref textGen, "textGen", true);
            Scribe_Values.Look(ref textArt, "textArt", true);
            Scribe_Values.Look(ref textGraves, "textGraves", true);
            Scribe_Values.Look(ref textBooks, "textBooks", true);
            Scribe_Values.Look(ref textSocial, "textSocial", true);
            Scribe_Values.Look(ref textCombat, "textCombat", false);
            Scribe_Values.Look(ref effects, "effects", "normal");
            Scribe_Values.Look(ref talkFrequency, "talkFrequency", 1f);
            Scribe_Values.Look(ref pauseAmbientAtSpeed, "pauseAmbientAtSpeed", 4);
            Scribe_Values.Look(ref wantsEnabled, "wantsEnabled", true);
            Scribe_Values.Look(ref recapOnLoad, "recapOnLoad", true);
            Scribe_Values.Look(ref adultContent, "adultContent", false);
            Scribe_Values.Look(ref wantsAvoidHours, "wantsAvoidHours", 1f);
            Scribe_Values.Look(ref showQuotes, "showQuotes", true);
            Scribe_Values.Look(ref voiceVanillaSocial, "voiceVanillaSocial", true);
            Scribe_Values.Look(ref quoteMaxChars, "quoteMaxChars", 140);
            Scribe_Values.Look(ref attachWindowMinutes, "attachWindowMinutes", 10);
            Scribe_Values.Look(ref attachRealFloorSec, "attachRealFloorSec", 5);
            Scribe_Values.Look(ref interactionHigh, "interactionHigh", Bridge.InteractionPolicy.DefaultHigh);
            Scribe_Values.Look(ref interactionIdle, "interactionIdle", Bridge.InteractionPolicy.DefaultIdle);
            Scribe_Values.Look(ref narratorMayPauseForEmergencies, "narratorMayPauseForEmergencies", true);
            Scribe_Values.Look(ref narratorControlsSpeed, "narratorControlsSpeed", false);
            Scribe_Values.Look(ref narratorMayLook, "narratorMayLook", true);
            Scribe_Values.Look(ref narratorMayImplantGenes, "narratorMayImplantGenes", false);
            Scribe_Values.Look(ref narratorMayExecutePrisoners, "narratorMayExecutePrisoners", false);
            Scribe_Values.Look(ref narratorMaySlaughterPets, "narratorMaySlaughterPets", false);
            Scribe_Values.Look(ref narratorSlowsForCombat, "narratorSlowsForCombat", true);
            Scribe_Values.Look(ref stewardHeartbeatHours, "stewardHeartbeatHours", 6f);
            Scribe_Values.Look(ref narratorAnswerDialogs, "narratorAnswerDialogs", true);
            Scribe_Values.Look(ref narratorDialogTimeoutS, "narratorDialogTimeoutS", 90);
            Scribe_Values.Look(ref narratorCommentary, "narratorCommentary", true);
            Scribe_Values.Look(ref narratorAutosave, "narratorAutosave", true);
            Scribe_Values.Look(ref narratorAutosaveDays, "narratorAutosaveDays", 1f);
            Scribe_Values.Look(ref narratorAutosaveKeep, "narratorAutosaveKeep", 5);
            Scribe_Values.Look(ref narratorRestartMode, "narratorRestartMode", 0);
            Scribe_Values.Look(ref narratorTrade, "narratorTrade", true);
            Scribe_Values.Look(ref narratorCaravans, "narratorCaravans", true);
            Scribe_Values.Look(ref narratorTradeMaxDeal, "narratorTradeMaxDeal", 2000);
            Scribe_Values.Look(ref narratorTradeMaxDay, "narratorTradeMaxDay", 6000);
            Scribe_Values.Look(ref narratorTradeDealsPerDay, "narratorTradeDealsPerDay", 3);
            Scribe_Values.Look(ref narratorKeepMedicine, "narratorKeepMedicine", 10);
            Scribe_Values.Look(ref narratorKeepFoodDays, "narratorKeepFoodDays", 10);
            Scribe_Values.Look(ref narratorNeverSell, "narratorNeverSell", "");
            Scribe_Values.Look(ref narratorCaravanMaxPawns, "narratorCaravanMaxPawns", 3);
            Scribe_Values.Look(ref chatPanelShow, "chatPanelShow", true);
            Scribe_Values.Look(ref chatPanelRight, "chatPanelRight", false);
            Scribe_Values.Look(ref chatPanelMedium, "chatPanelMedium", false);
            Scribe_Values.Look(ref chatPanelOpacity, "chatPanelOpacity", 0.3f);
            Scribe_Values.Look(ref chatPanelWidth, "chatPanelWidth", 420f);
            Scribe_Values.Look(ref chatPanelLines, "chatPanelLines", 10);
            Scribe_Values.Look(ref chatPanelX, "chatPanelX", -1f);
            Scribe_Values.Look(ref chatPanelY, "chatPanelY", -1f);
            Scribe_Values.Look(ref chatPanelW, "chatPanelW", -1f);
            Scribe_Values.Look(ref chatPanelH, "chatPanelH", -1f);
            Scribe_Values.Look(ref chatShowTalk, "chatShowTalk", true);
            Scribe_Values.Look(ref chatShowThoughts, "chatShowThoughts", true);
            Scribe_Values.Look(ref chatShowNarrator, "chatShowNarrator", true);
            Scribe_Values.Look(ref chatShowActions, "chatShowActions", true);
            Scribe_Values.Look(ref chatShowStory, "chatShowStory", true);
        }
    }
}

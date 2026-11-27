using System;
using RimWorldLLM.Bridge;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Game-side facade over the bridge: owns the server, router, op-id store and event ring, publishes the
    /// per-frame GameState, and drains requests from a Root.Update postfix (so the bridge also answers in the main menu).
    /// </summary>
    public static class RWLBridge
    {
        sealed class Adapter : IGameAdapter
        {
            public GameState BuildState() { return RWLBridge.BuildState(); }
            public void Info(string m) { Log.Message(m); }
            public void Warn(string m) { Log.Warning("[RimWorldLLM] " + m); }
            public void Error(string m) { Log.Error(m); }
        }

        public static readonly BridgeRuntime Runtime = new BridgeRuntime(new Adapter(), () => Environment.TickCount64 / 1000.0);
        public static OpIdStore OpIds { get { return Runtime.OpIds; } }
        public static ApiRouter Router { get { return Runtime.Router; } }
        public static EventRing Events { get { return Runtime.Events; } }
        public static TalkQueue Talk { get { return Runtime.Talk; } }
        public static ChronicleBook Chronicle { get { return Runtime.Chronicle; } }
        public static ArtBook Art { get { return Runtime.Art; } }
        public static MindBook Minds { get { return Runtime.Minds; } }
        static string _sessionToken;

        public static int Port { get { return Runtime.Port; } }
        public static string LastError { get { return Runtime.LastError; } }
        public static bool IsRunning { get { return Runtime.IsRunning; } }

        public static bool IsLoopback(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress)) return false;
            string a = bindAddress.Trim().ToLowerInvariant();
            return a == "localhost" || a == "::1" || a == "[::1]" || a.StartsWith("127.");
        }

        /// <summary>The token in force: the fixed one from settings, else a random one made once per run.</summary>
        public static string EffectiveToken(RimWorldLLMSettings s)
        {
            string fixedToken = (s?.sharedToken ?? "").Trim();
            if (fixedToken.Length > 0) return fixedToken;
            if (string.IsNullOrEmpty(_sessionToken)) _sessionToken = Secure.NewToken();
            return _sessionToken;
        }

        public static void Start()
        {
            Stop();
            Routes.Register(Router);
            var s = RimWorldLLMMod.Settings;
            if (s == null || !s.enabled) return;
            int first = SettingsMigration.Port(s.bridgePort);
            Runtime.Start(new BridgeOptions
            {
                BindAddress = string.IsNullOrWhiteSpace(s.bindAddress) ? "127.0.0.1" : s.bindAddress.Trim(),
                FirstPort = first,
                LastPort = Math.Min(65535, first + (Const.LastPort - Const.DefaultPort)),
                Token = EffectiveToken(s),
                ConfigDir = GenFilePaths.ConfigFolderPath,
                LlmInfo = LlmInfoForFile,
                Version = RimWorldLLMMod.Version
            });
        }

        public static void Stop() { Runtime.Stop(); }

        /// <summary>The "llm" block of bridge.json: the only place the API key is written (the file is owner-only).</summary>
        static Newtonsoft.Json.Linq.JObject LlmInfoForFile()
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null) return null;
            // Local engine: the agent talks to the engine's OpenAI proxy; the engine holds the model, there is no key.
            if (s.llmSource != "own")
                return new Newtonsoft.Json.Linq.JObject { ["base_url"] = EngineManager.OpenAiBase, ["model"] = EngineManager.CurrentModel, ["api_key"] = "" };
            return new Newtonsoft.Json.Linq.JObject
            {
                ["base_url"] = (s.llmBaseUrl ?? "").Trim(),
                ["model"] = (s.llmModel ?? "").Trim(),
                ["api_key"] = (s.llmApiKey ?? "").Trim()
            };
        }

        /// <summary>Rewrites bridge.json after the LLM settings changed.</summary>
        public static void RefreshLlm() { Runtime.RefreshInfo(); }

        /// <summary>New token: replaces a fixed one in settings, or makes a fresh per-run one. Restarts the bridge; the agent re-reads bridge.json.</summary>
        public static void RegenerateToken()
        {
            var s = RimWorldLLMMod.Settings;
            _sessionToken = Secure.NewToken();
            if (s != null && !string.IsNullOrWhiteSpace(s.sharedToken)) s.sharedToken = _sessionToken;
            Start();
        }

        public static string CurrentToken { get { return Runtime.Server != null ? Runtime.Server.Token : ""; } }

        public static GameState BuildState()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null
                || RimWorldLLMGameComponent.Instance == null)
                return GameState.Menu;
            var tm = Find.TickManager;
            return new GameState
            {
                GameLoaded = true,
                Tick = tm.TicksGame,
                SaveEpoch = RimWorldLLMGameComponent.Instance.SaveEpoch,
                Paused = tm.Paused,
                Speed = (int)tm.CurTimeSpeed
            };
        }

        /// <summary>Called every frame from the Root.Update postfix, game thread, also while paused and in the menu.</summary>
        public static void FrameUpdate()
        {
            Runtime.FrameUpdate();
            AgentDialOut.FlushLogs();
            EngineManager.Tick();
            EnginePairingUi.Tick();
        }
    }

    /// <summary>Starts the bridge once defs are loaded, in the main menu, so the agent can find it before any game exists.</summary>
    [StaticConstructorOnStartup]
    public static class BridgeStartup
    {
        static BridgeStartup()
        {
            try { RWLBridge.Start(); }
            catch (Exception e) { Log.Error("[RimWorldLLM] Bridge startup failed: " + e); }
            try { EngineManager.StartAtStartup(); }   // after the bridge: the engine's profile points at bridge.json
            catch (Exception e) { Log.Error("[RimWorldLLM] Engine startup failed: " + e); }
            try { EnginePairingUi.StartupCheck(); }   // a paired engine on another machine: find it by engine_id, check the token
            catch (Exception e) { Log.Error("[RimWorldLLM] Engine pairing check failed: " + e.GetType().Name); }
        }
    }

    // Root_Play.Update and Root_Entry.Update both run every frame, paused or not, in the game and in the menu.
    [HarmonyPatch]
    public static class RootUpdatePatch
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(Root_Play), "Update");
            yield return AccessTools.DeclaredMethod(typeof(Root_Entry), "Update");
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try { RWLBridge.FrameUpdate(); }
            catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] frame update failed: " + e, 7731001); }
        }
    }
}

using System;
using HermesVoice.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Game-side facade over the bridge: owns the server, router, op-id store and event ring, publishes the
    /// per-frame GameState, and drains requests from a Root.Update postfix (so the bridge also answers in the main menu).
    /// </summary>
    public static class HermesBridge
    {
        public static readonly OpIdStore OpIds = new OpIdStore(() => Environment.TickCount64 / 1000.0);
        public static readonly ApiRouter Router = new ApiRouter(OpIds) { LogWarning = m => Log.Warning("[HermesVoice] " + m) };
        public static readonly EventRing Events = new EventRing();
        static BridgeServer _server;
        static string _sessionToken;

        public const string OpenWarning = "Anyone on your network can read colony state and make pawns speak";

        public static BridgeServer Server { get { return _server; } }
        public static int Port { get { return _server != null ? _server.Port : Const.DefaultPort; } }
        public static string LastError { get { return _server != null ? _server.LastError : ""; } }
        public static bool IsRunning { get { return _server != null && _server.IsRunning; } }

        public static bool IsLoopback(string bindAddress)
        {
            if (string.IsNullOrWhiteSpace(bindAddress)) return false;
            string a = bindAddress.Trim().ToLowerInvariant();
            return a == "localhost" || a == "::1" || a == "[::1]" || a.StartsWith("127.");
        }

        /// <summary>The token in force: the fixed one from settings, else a random one made once per run.</summary>
        public static string EffectiveToken(HermesVoiceSettings s)
        {
            string fixedToken = (s?.sharedToken ?? "").Trim();
            if (fixedToken.Length > 0) return fixedToken;
            if (string.IsNullOrEmpty(_sessionToken)) _sessionToken = Secure.NewToken();
            return _sessionToken;
        }

        public static void Start()
        {
            Stop();
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.enabled) return;
            int first = SettingsMigration.Port(s.bridgePort);
            var opt = new BridgeOptions
            {
                BindAddress = string.IsNullOrWhiteSpace(s.bindAddress) ? "127.0.0.1" : s.bindAddress.Trim(),
                FirstPort = first,
                LastPort = Math.Min(65535, first + (Const.LastPort - Const.DefaultPort)),
                Token = EffectiveToken(s),
                ConfigDir = GenFilePaths.ConfigFolderPath,
                Version = HermesVoiceMod.Version
            };
            _server = new BridgeServer(opt, Router)
            {
                LogInfo = m => Log.Message(m),
                LogError = m => Log.Error(m)
            };
            _server.SetState(BuildState());
            _server.Start();
        }

        public static void Stop()
        {
            var s = _server;
            _server = null;
            if (s != null) s.Stop();
        }

        public static GameState BuildState()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null
                || HermesVoiceGameComponent.Instance == null)
                return GameState.Menu;
            var tm = Find.TickManager;
            return new GameState
            {
                GameLoaded = true,
                Tick = tm.TicksGame,
                SaveEpoch = HermesVoiceGameComponent.Instance.SaveEpoch,
                Paused = tm.Paused,
                Speed = (int)tm.CurTimeSpeed
            };
        }

        /// <summary>Called every frame from the Root.Update postfix, game thread, also while paused and in the menu.</summary>
        public static void FrameUpdate()
        {
            var s = _server;
            if (s != null)
            {
                s.SetState(BuildState());
                s.Pump(Const.FrameBudgetMs);
            }
            AgentDialOut.FlushLogs();
        }
    }

    /// <summary>Starts the bridge once defs are loaded, in the main menu, so the agent can find it before any game exists.</summary>
    [StaticConstructorOnStartup]
    public static class BridgeStartup
    {
        static BridgeStartup()
        {
            try { HermesBridge.Start(); }
            catch (Exception e) { Log.Error("[HermesVoice] Bridge startup failed: " + e); }
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
            try { HermesBridge.FrameUpdate(); }
            catch (Exception e) { Log.ErrorOnce("[HermesVoice] frame update failed: " + e, 7731001); }
        }
    }
}

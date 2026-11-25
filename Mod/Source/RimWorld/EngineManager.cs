using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Game-side owner of the local engine: launches or attaches it at startup, keeps the latest status/plan/hardware for the
    /// settings page and the setup window, and pushes the LLM source to it. Every engine call runs off the game thread;
    /// results land in volatile fields that the UI reads. UI code calls <see cref="Touch"/> each frame, which polls status
    /// about every 2 s while a window that shows it is open.
    /// </summary>
    public static class EngineManager
    {
        public const long PollMs = 2000;

        static volatile EngineClient _client;
        static volatile EngineStatus _status;
        static volatile EnginePlan _plan;
        static volatile EngineHardware _hardware;
        static volatile LaunchResult _launch;
        static volatile string _actionError;
        static volatile bool _launching;
        static volatile bool _statusInFlight, _planInFlight, _hardwareInFlight;
        static volatile bool _planStale = true;
        static volatile bool _showFirstRun;
        static long _lastPollMs = long.MinValue / 2;
        static bool _firstRunShown;

        public static EngineClient Client { get { return _client; } }
        public static EngineStatus Status { get { return _status; } }
        public static EnginePlan Plan { get { return _plan; } }
        public static EngineHardware Hardware { get { return _hardware; } }
        public static LaunchResult Launch { get { return _launch; } }
        public static bool Launching { get { return _launching; } }
        /// <summary>Error text of the last button action (install, cancel, import...), or null.</summary>
        public static string ActionError { get { return _actionError; } }

        public static bool LocalSource { get { var s = HermesVoiceMod.Settings; return s == null || s.llmSource != "own"; } }

        static long Now { get { return Environment.TickCount64; } }

        /// <summary>Base URL the agent and bridge.json use in local mode: the engine's port + /v1 (default port until discovery).</summary>
        public static string OpenAiBase
        {
            get
            {
                var c = _client;
                return c != null ? c.OpenAiBase : "http://127.0.0.1:" + EngineClient.FirstPort + "/v1";
            }
        }

        /// <summary>Model name for bridge.json in local mode ("" until the engine reports one).</summary>
        public static string CurrentModel { get { var st = _status; return st != null && st.Model != null ? st.Model : ""; } }

        static string ModRoot()
        {
            try { return HermesVoiceMod.Instance.Content.RootDir; } catch { return null; }
        }

        // ---------------------------------------------------------------------------------- startup
        /// <summary>Called once at game start (after the bridge has started and written bridge.json). Never blocks.</summary>
        public static void StartAtStartup()
        {
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.engineAutoStart) return;
            LaunchAsync();
        }

        /// <summary>Launch or attach the engine in the background (also the "retry" action).</summary>
        public static void LaunchAsync()
        {
            if (_launching) return;
            string root = ModRoot();
            if (root == null) return;
            _launching = true;
            string bridgeJson = BridgeInfo.PathIn(GenFilePaths.ConfigFolderPath);
            int pid = Process.GetCurrentProcess().Id;
            Task.Run(async () =>
            {
                try
                {
                    var r = await EngineLauncher.EnsureRunningAsync(new LaunchOptions { ModRoot = root, BridgeJsonPath = bridgeJson, Pid = pid }).ConfigureAwait(false);
                    _launch = r;
                    if (r.Outcome == LaunchOutcome.NoBinary || r.Outcome == LaunchOutcome.SpawnFailed)
                        Log.Warning("[HermesVoice] Engine: " + r.Error);
                    else if (r.AttachError != null)
                        Log.Warning("[HermesVoice] Engine attach: " + r.AttachError);
                    if (r.Client != null)
                    {
                        _client = r.Client;
                        await PushSourceAsync().ConfigureAwait(false);
                        await FetchStatusAsync().ConfigureAwait(false);
                        HermesBridge.RefreshLlm();   // bridge.json carries the engine's real port
                    }
                }
                catch (Exception e) { Log.Warning("[HermesVoice] Engine launch failed: " + e.Message); }
                finally { _launching = false; }
            });
        }

        /// <summary>Tells the engine whether to serve its own model (local) or the player's endpoint (external).</summary>
        public static Task PushSourceAsync()
        {
            var c = _client;
            var s = HermesVoiceMod.Settings;
            if (c == null || s == null) return Task.CompletedTask;
            var st = new EngineSettings();
            if (s.llmSource == "own")
            {
                st.Mode = "external";
                st.External = new EngineExternal { BaseUrl = (s.llmBaseUrl ?? "").Trim(), Model = (s.llmModel ?? "").Trim(), ApiKey = (s.llmApiKey ?? "").Trim() };
            }
            else st.Mode = "local";
            return c.PutSettingsAsync(st);
        }

        /// <summary>Settings changed (source or own endpoint): push to the engine and rewrite bridge.json. Fire and forget.</summary>
        public static void SettingsChanged()
        {
            Task.Run(async () =>
            {
                try { await PushSourceAsync().ConfigureAwait(false); } catch { }
                HermesBridge.RefreshLlm();
            });
        }

        // ---------------------------------------------------------------------------------- polling
        /// <summary>Call every frame from a window that shows the engine. Polls status about every 2 s.</summary>
        public static void Touch()
        {
            long now = Now;
            if (now - _lastPollMs < PollMs) return;
            _lastPollMs = now;
            if (_client == null)
            {
                if (!_launching) Task.Run(DiscoverAsync);   // engine started elsewhere (setup page, CLI), or lost and back
                return;
            }
            Task.Run(FetchStatusAsync);
        }

        /// <summary>Resets the poll clock so the next Touch polls at once (window just opened).</summary>
        public static void PollNow() { _lastPollMs = long.MinValue / 2; _planStale = true; }

        static async Task DiscoverAsync()
        {
            try
            {
                var c = await EnginePlatformDiscover().ConfigureAwait(false);
                if (c != null) { _client = c; await FetchStatusAsync().ConfigureAwait(false); HermesBridge.RefreshLlm(); }
            }
            catch { }
        }

        static Task<EngineClient> EnginePlatformDiscover() { return EngineLauncher.DefaultDiscoverAsync(EnginePlatform.Detect()); }

        static async Task FetchStatusAsync()
        {
            var c = _client;
            if (c == null || _statusInFlight) return;
            _statusInFlight = true;
            try
            {
                var r = await c.GetStatusAsync().ConfigureAwait(false);
                if (r.Ok && r.Value != null)
                {
                    string before = _status != null ? _status.Phase : null;
                    _status = r.Value;
                    if (before != r.Value.Phase) _planStale = true;     // installed flags change when a job ends
                    if (r.Value.Phase == EnginePhase.NotInstalled) _showFirstRun = true;
                }
                else if (r.Unreachable || r.TimedOut) { if (r.Unreachable) { _status = null; _client = null; } }
            }
            finally { _statusInFlight = false; }
            if (_planStale) await FetchPlanAsync().ConfigureAwait(false);
        }

        /// <summary>Refreshes plan and (once) hardware for the setup window.</summary>
        public static async Task FetchPlanAsync()
        {
            var c = _client;
            if (c == null || _planInFlight) return;
            _planInFlight = true;
            try
            {
                _planStale = false;
                var r = await c.GetPlanAsync().ConfigureAwait(false);
                if (r.Ok) _plan = r.Value;
                if (_hardware == null && !_hardwareInFlight)
                {
                    _hardwareInFlight = true;
                    try { var h = await c.GetHardwareAsync().ConfigureAwait(false); if (h.Ok) _hardware = h.Value; }
                    finally { _hardwareInFlight = false; }
                }
            }
            finally { _planInFlight = false; }
        }

        /// <summary>Re-probe hardware (the "refresh" in the setup window).</summary>
        public static void RefreshHardware()
        {
            var c = _client;
            if (c == null) return;
            Task.Run(async () => { var h = await c.GetHardwareAsync(true).ConfigureAwait(false); if (h.Ok) _hardware = h.Value; });
        }

        // ---------------------------------------------------------------------------------- actions (button handlers)
        static void Act<T>(Func<EngineClient, Task<EngineResult<T>>> call)
        {
            var c = _client;
            if (c == null) { _actionError = "engine not running"; return; }
            _actionError = null;
            Task.Run(async () =>
            {
                var r = await call(c).ConfigureAwait(false);
                if (!r.Ok) _actionError = r.Error ?? ("HTTP " + r.HttpStatus);
                await FetchStatusAsync().ConfigureAwait(false);
            });
        }

        static volatile EngineTestResult _test;
        static volatile bool _testRunning;
        public static EngineTestResult LastTest { get { return _test; } }
        public static bool TestRunning { get { return _testRunning; } }

        /// <summary>POST /engine/v1/test: tests the current endpoint and keeps the light for the settings page.</summary>
        public static void RunTest()
        {
            var c = _client;
            if (c == null || _testRunning) return;
            _testRunning = true;
            _test = null;
            Task.Run(async () =>
            {
                try
                {
                    var r = await c.TestAsync().ConfigureAwait(false);
                    _test = r.Ok && r.Value != null ? r.Value : new EngineTestResult { Light = "red", Message = r.Error ?? ("HTTP " + r.HttpStatus) };
                }
                finally { _testRunning = false; }
            });
        }

        public static void Install(string modelId) { Act(c => c.InstallAsync(modelId)); }
        public static void Cancel() { Act(c => c.CancelAsync()); }
        public static void Import(string path) { Act(c => c.ImportAsync(path)); _planStale = true; }

        public static string SetupUrl { get { var c = _client; return (c != null ? c.BaseUrl : "http://127.0.0.1:" + EngineClient.FirstPort) + "/"; } }

        // ---------------------------------------------------------------------------------- presentation helpers
        public enum Light { Grey, Green, Amber, Red }

        /// <summary>The phase light of the Local source: green ready, amber busy, red error, grey not installed or unknown.</summary>
        public static Light CurrentLight()
        {
            var st = _status;
            var l = _launch;
            if (st == null)
            {
                if (_launching) return Light.Amber;
                if (l != null && (l.Outcome == LaunchOutcome.NoBinary || l.Outcome == LaunchOutcome.SpawnFailed)) return Light.Red;
                if (l != null && l.Client == null) return Light.Red;
                return Light.Grey;
            }
            switch (st.Phase)
            {
                case EnginePhase.Ready: case EnginePhase.External: return Light.Green;
                case EnginePhase.Downloading: case EnginePhase.Installing: case EnginePhase.Starting:
                case EnginePhase.Loading: case EnginePhase.Resizing: return Light.Amber;
                case EnginePhase.Error: return Light.Red;
                default: return Light.Grey;   // not_installed, sleeping
            }
        }

        public static Color ColorOf(Light l)
        {
            switch (l)
            {
                case Light.Green: return new Color(0.4f, 0.85f, 0.4f);
                case Light.Amber: return new Color(0.95f, 0.75f, 0.25f);
                case Light.Red: return new Color(0.9f, 0.3f, 0.3f);
                default: return Color.gray;
            }
        }

        /// <summary>One line: the model, slots x ctx, phase and progress.</summary>
        public static string StatusLine()
        {
            var st = _status;
            var l = _launch;
            if (st == null)
            {
                if (_launching) return UiUtil.T("HV_Eng_Launching");
                if (l != null && (l.Outcome == LaunchOutcome.NoBinary || l.Outcome == LaunchOutcome.SpawnFailed)) return UiUtil.T("HV_Eng_LaunchError", l.Error);
                if (l != null && l.Client == null) return UiUtil.T("HV_Eng_NoAnswer");
                return UiUtil.T("HV_Eng_NotRunning");
            }
            string phase = PhaseLabel(st.Phase);
            string line = phase;
            if (st.Progress != null && st.Progress.TotalBytes > 0)
                line += " " + UiUtil.T("HV_Eng_Percent", Mathf.RoundToInt((float)st.Progress.Fraction * 100f));
            if (!string.IsNullOrEmpty(st.Model)) line = UiUtil.T("HV_Eng_ModelPrefix", st.Model) + " | " + line;
            if (st.Shape != null && st.Shape.Slots > 0) line += " | " + UiUtil.T("HV_Eng_Shape", st.Shape.Slots, Ctx(st.Shape.CtxPerSlot));
            if (st.TokS.HasValue && st.TokS.Value > 0) line += " | " + UiUtil.T("HV_Eng_TokS", Mathf.RoundToInt((float)st.TokS.Value));
            if (st.Phase == EnginePhase.Error && st.Errors != null && st.Errors.Count > 0) line += " | " + st.Errors[st.Errors.Count - 1];
            return line;
        }

        /// <summary>True when the local engine reports that the model in use is the catalog's fallback.</summary>
        public static bool FallbackInUse
        {
            get { var st = _status; return LocalSource && st != null && st.ModelFallback; }
        }

        /// <summary>The engine's note about the fallback model, or the stock sentence when it sent none.</summary>
        public static string FallbackNote(string fromEngine)
        {
            return string.IsNullOrWhiteSpace(fromEngine) ? UiUtil.T("HV_Fallback_DefaultNote") : fromEngine.Trim();
        }

        /// <summary>The amber warning line for the status area, or null when the model in use is not a fallback.</summary>
        public static string FallbackLine()
        {
            if (!FallbackInUse) return null;
            return UiUtil.T("HV_Fallback_StatusLine", FallbackNote(_status.ModelNote));
        }

        public static string Ctx(int ctx) { return ctx % 1024 == 0 ? (ctx / 1024) + "k" : ctx.ToString(); }

        public static string PhaseLabel(string phase)
        {
            switch (phase)
            {
                case EnginePhase.NotInstalled: return UiUtil.T("HV_Eng_Phase_NotInstalled");
                case EnginePhase.Downloading: return UiUtil.T("HV_Eng_Phase_Downloading");
                case EnginePhase.Installing: return UiUtil.T("HV_Eng_Phase_Installing");
                case EnginePhase.Starting: return UiUtil.T("HV_Eng_Phase_Starting");
                case EnginePhase.Loading: return UiUtil.T("HV_Eng_Phase_Loading");
                case EnginePhase.Ready: return UiUtil.T("HV_Eng_Phase_Ready");
                case EnginePhase.Resizing: return UiUtil.T("HV_Eng_Phase_Resizing");
                case EnginePhase.Sleeping: return UiUtil.T("HV_Eng_Phase_Sleeping");
                case EnginePhase.External: return UiUtil.T("HV_Eng_Phase_External");
                case EnginePhase.Error: return UiUtil.T("HV_Eng_Phase_Error");
                default: return phase ?? "?";
            }
        }

        // ---------------------------------------------------------------------------------- first run
        /// <summary>Game thread, every frame (from HermesBridge.FrameUpdate): shows the first-run prompt once.</summary>
        public static void Tick()
        {
            if (!_showFirstRun || _firstRunShown) return;
            var s = HermesVoiceMod.Settings;
            if (s == null || s.engineSetupDismissed || s.llmSource == "own") { _showFirstRun = false; return; }
            if (Find.WindowStack == null) return;
            _firstRunShown = true;
            _showFirstRun = false;
            Find.WindowStack.Add(new Window_EngineFirstRun());
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
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

        public static bool LocalSource { get { var s = RimWorldLLMMod.Settings; return s == null || s.llmSource != "own"; } }

        static long Now { get { return Environment.TickCount64; } }

        /// <summary>Base URL the mind and bridge.json use in local mode: the engine's port + /v1 (default port until discovery).</summary>
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
            try { return RimWorldLLMMod.Instance.Content.RootDir; } catch { return null; }
        }

        // ---------------------------------------------------------------------------------- startup
        /// <summary>Called once at game start (after the bridge has started and written bridge.json). Never blocks.</summary>
        public static void StartAtStartup()
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null || !s.engineAutoStart || !LocalSource) return;   // the player's own endpoint: no engine is launched or attached
            LaunchAsync();
        }

        /// <summary>Launch or attach the engine in the background (also the "retry" action).</summary>
        public static void LaunchAsync()
        {
            if (_launching || !LocalSource) return;
            string root = ModRoot();
            if (root == null) return;
            _launching = true;
            string bridgeJson = BridgeInfo.PathIn(GenFilePaths.ConfigFolderPath);
            int pid = Process.GetCurrentProcess().Id;
            var hostCard = ReadHostAdapter();   // Unity calls stay on the game thread
            Task.Run(async () =>
            {
                try
                {
                    var r = await EngineLauncher.EnsureRunningAsync(new LaunchOptions { ModRoot = root, BridgeJsonPath = bridgeJson, Pid = pid, HostAdapter = hostCard }).ConfigureAwait(false);
                    _launch = r;
                    if (r.Outcome == LaunchOutcome.NoBinary || r.Outcome == LaunchOutcome.SpawnFailed)
                        Log.Warning("[RimWorldLLM] Engine: " + r.Error);
                    else if (r.AttachError != null)
                        Log.Warning("[RimWorldLLM] Engine attach: " + r.AttachError);
                    if (r.Client != null)
                    {
                        _client = r.Client;
                        await PushSourceAsync().ConfigureAwait(false);
                        await FetchStatusAsync().ConfigureAwait(false);
                        RWLBridge.RefreshLlm();   // bridge.json carries the engine's real port
                    }
                }
                catch (Exception e) { Log.Warning("[RimWorldLLM] Engine launch failed: " + e.Message); }
                finally { _launching = false; }
            });
        }

        /// <summary>The card the game renders on, from Unity. Game thread only; never throws.</summary>
        static HostAdapterInfo ReadHostAdapter()
        {
            try { return new HostAdapterInfo(SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsMemorySize); }
            catch { return null; }
        }

        /// <summary>The "Game on X · Y: N GB free for the model" line, or null (older engine, no data).</summary>
        public static string GpuLine()
        {
            var st = _status;
            return st == null ? null : Bridge.GpuLine.Format(st.Gpu, (k, a) => UiUtil.T(k, a));
        }

        /// <summary>Tells a running engine to serve its own model (local source). Own-endpoint mode never talks to the engine: the mind calls the player's endpoint itself.</summary>
        public static Task PushSourceAsync()
        {
            var c = _client;
            var s = RimWorldLLMMod.Settings;
            if (c == null || s == null) return Task.CompletedTask;
            if (s.llmSource == "own") return Task.CompletedTask;
            return c.PutSettingsAsync(new EngineSettings { Mode = "local" });
        }

        /// <summary>Settings changed (source or own endpoint): push to the engine and rewrite bridge.json. Fire and forget.</summary>
        public static void SettingsChanged()
        {
            var s = RimWorldLLMMod.Settings;
            if (s != null && s.engineAutoStart && LocalSource && _client == null) LaunchAsync();   // switched back to the local engine
            Task.Run(async () =>
            {
                try { await PushSourceAsync().ConfigureAwait(false); } catch { }
                RWLBridge.RefreshLlm();
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
                if (!_launching && LocalSource) Task.Run(DiscoverAsync);   // engine started elsewhere (setup page, CLI), or lost and back
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
                if (c != null) { _client = c; await FetchStatusAsync().ConfigureAwait(false); RWLBridge.RefreshLlm(); }
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
                    _test = r.Ok && r.Value != null ? r.Value : new EngineTestResult { Light = "red", Message = r.Error == EngineClient.YieldedError ? UiUtil.T("RWL_Probe_Paused") : (r.Error ?? ("HTTP " + r.HttpStatus)) };
                }
                finally { _testRunning = false; }
            });
        }

        // ---------------------------------------------------------------------------------- storage and custom file
        static volatile EngineStorage _storage;
        static volatile bool _storageInFlight, _storageFailed;
        static volatile CustomCheck _customCheck;
        static volatile string _customCheckError;
        static volatile bool _customCheckInFlight, _customBusy;

        public static EngineStorage Storage { get { return _storage; } }
        public static bool StorageInFlight { get { return _storageInFlight; } }
        public static bool StorageFailed { get { return _storageFailed; } }
        /// <summary>The last "Check" of a custom file (null until run or after the path changed).</summary>
        public static CustomCheck LastCustomCheck { get { return _customCheck; } }
        public static string CustomCheckError { get { return _customCheckError; } }
        public static bool CustomCheckRunning { get { return _customCheckInFlight; } }
        public static bool CustomBusy { get { return _customBusy; } }

        /// <summary>GET /storage off the game thread; the result lands in <see cref="Storage"/>.</summary>
        public static void RefreshStorage()
        {
            var c = _client;
            if (c == null || _storageInFlight) return;
            _storageInFlight = true;
            Task.Run(async () =>
            {
                try
                {
                    var r = await c.GetStorageAsync(true).ConfigureAwait(false);
                    if (r.Ok && r.Value != null) { _storage = r.Value; _storageFailed = false; }
                    else _storageFailed = true;
                }
                finally { _storageInFlight = false; }
            });
        }

        public static void ClearCustomCheck() { _customCheck = null; _customCheckError = null; }

        /// <summary>POST custom/check: reads the file header only.</summary>
        public static void CheckCustom(string path)
        {
            var c = _client;
            if (c == null) { _customCheckError = UiUtil.T("RWL_Custom_NoEngine"); return; }
            if (_customCheckInFlight) return;
            _customCheckInFlight = true;
            _customCheck = null;
            _customCheckError = null;
            Task.Run(async () =>
            {
                try
                {
                    var r = await c.CheckCustomAsync(path).ConfigureAwait(false);
                    if (r.Ok && r.Value != null) _customCheck = r.Value;
                    else _customCheckError = r.Error ?? ("HTTP " + r.HttpStatus);
                }
                finally { _customCheckInFlight = false; }
            });
        }

        static void BusyAct<T>(Func<EngineClient, Task<EngineResult<T>>> call, Action after = null)
        {
            var c = _client;
            if (c == null) { _actionError = "engine not running"; return; }
            if (_customBusy) return;
            _customBusy = true;
            _actionError = null;
            Task.Run(async () =>
            {
                try
                {
                    var r = await call(c).ConfigureAwait(false);
                    if (!r.Ok) _actionError = r.Error ?? ("HTTP " + r.HttpStatus);
                    _planStale = true;
                    await FetchStatusAsync().ConfigureAwait(false);
                    if (after != null) after();
                }
                finally { _customBusy = false; }
            });
        }

        public static void UseCustom(string path) { BusyAct(c => c.UseCustomAsync(path)); }
        public static void ClearCustom() { BusyAct(c => c.ClearCustomAsync()); }
        public static void DeleteModel(string file) { BusyAct(c => c.DeleteModelAsync(null, file), RefreshStorage); }
        public static void DeleteModelById(string id) { BusyAct(c => c.DeleteModelAsync(id), RefreshStorage); }
        public static void RemoveDownloads() { RemoveEverything(false); }

        static volatile string _removeNote;
        /// <summary>What happened to the mind's memory and logs after the last "remove everything" (null when nothing to say).</summary>
        public static string RemoveNote { get { return _removeNote; } }

        /// <summary>Removes the downloads and, when <paramref name="alsoMemory"/>, the mind's memory and logs (the mind is stopped first and started again after).</summary>
        public static void RemoveEverything(bool alsoMemory)
        {
            _removeNote = null;
            var plat = EnginePlatform.Detect();
            var where = AgentDataDir.ResolveHere(plat);
            if (_customBusy) return;
            if (_client == null)
            {
                // no engine (the player's own endpoint, or it is not running): there are no downloads to remove, but the memory can still go
                if (alsoMemory) { MindHost.Hold(); Task.Run(() => RemoveAgentMemory(where, plat)); }
                else _actionError = "engine not running";
                return;
            }
            if (alsoMemory) MindHost.Hold();   // before anything else: the mind may be writing into the folder
            BusyAct(c => c.RemoveDownloadsAsync(), () =>
            {
                _storage = null; RefreshStorage();
                if (alsoMemory) Task.Run(() => RemoveAgentMemory(where, plat));
            });
        }

        static async Task RemoveAgentMemory(AgentDataDir.Resolved where, EnginePlatform plat)
        {
            try
            {
                await MindHost.StoppedAsync().ConfigureAwait(false);   // the mind has stopped writing before its folder goes
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var guard = new[] { home, Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                    Path.Combine(home, ".local", "share"), Path.Combine(home, "AppData", "Local"),
                    EngineClient.DataDir(plat.Os, n => Environment.GetEnvironmentVariable(n), home) };
                var d = AgentDataDir.Delete(where, where.Path, guard);
                // a folder still under the mod's old name (both existed when the agent started) goes too
                var legacy = AgentDataDir.Legacy(where);
                if (legacy != null && Directory.Exists(legacy.Path) && d.Outcome != AgentDataDir.DeleteOutcome.Refused)
                {
                    var dl = AgentDataDir.Delete(legacy, legacy.Path, guard);
                    if (dl.Outcome == AgentDataDir.DeleteOutcome.Deleted) { d.FreedBytes += dl.FreedBytes; d.Outcome = AgentDataDir.DeleteOutcome.Deleted; }
                }
                if (d.Outcome == AgentDataDir.DeleteOutcome.Deleted) _removeNote = UiUtil.T("RWL_Storage_MemoryDeleted", Window_EngineSetup.Bytes(d.FreedBytes));
                else if (d.Outcome == AgentDataDir.DeleteOutcome.NothingThere) _removeNote = UiUtil.T("RWL_Storage_MemoryNone");
                else { _removeNote = UiUtil.T("RWL_Storage_MemoryRefused", where.Path, d.Reason); Log.Warning("[RimWorldLLM] Agent memory not deleted: " + d.Reason); }
            }
            catch (Exception e) { _removeNote = UiUtil.T("RWL_Storage_MemoryRefused", where != null ? where.Path : "", e.Message); }
            finally { MindHost.Release(); }   // the mind starts again when a game is loaded
        }

        /// <summary>The model name to show: the engine's human name, the custom file's name, or the model id.</summary>
        public static string ModelLabel()
        {
            var st = _status;
            if (st == null) return "";
            if (!string.IsNullOrEmpty(st.ModelName)) return st.ModelName;
            if (st.Custom != null && !string.IsNullOrEmpty(st.Custom.Name)) return st.Custom.Name;
            return st.Model ?? "";
        }

        /// <summary>The user's own GGUF file is the selected model (status.custom is also kept, as a memory, after a switch back to a catalog model).</summary>
        public static bool CustomActive { get { var st = _status; return st != null && st.Custom != null && (st.Model ?? "").StartsWith("custom:", System.StringComparison.Ordinal); } }

        /// <summary>Line for a background model switch (the old one keeps talking), or null.</summary>
        public static string SwitchLine()
        {
            var st = _status;
            if (st == null || st.Upgrade == null || StarterInUse || string.IsNullOrEmpty(st.Upgrade.State)) return null;
            if (st.Upgrade.State == "done" || st.Upgrade.State == "idle") return null;
            var p = st.Upgrade.Progress;
            int pct = p == null ? -1 : Bridge.StarterWatch.Percent(p.DoneBytes, p.TotalBytes);
            string name = !string.IsNullOrEmpty(st.Upgrade.TargetName) ? st.Upgrade.TargetName : st.Upgrade.Target;
            return pct >= 0 ? UiUtil.T("RWL_Switch_Line", name, pct) : UiUtil.T("RWL_Switch_LineNoPct", name);
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
                if (_launching) return UiUtil.T("RWL_Eng_Launching");
                if (l != null && (l.Outcome == LaunchOutcome.NoBinary || l.Outcome == LaunchOutcome.SpawnFailed)) return UiUtil.T("RWL_Eng_LaunchError", l.Error);
                if (l != null && l.Client == null) return UiUtil.T("RWL_Eng_NoAnswer");
                return UiUtil.T("RWL_Eng_NotRunning");
            }
            string phase = PhaseLabel(st.Phase);
            string line = phase;
            if (st.Progress != null && st.Progress.TotalBytes > 0)
                line += " " + (st.Progress.Verifying ? UiUtil.T("RWL_Eng_Verifying") + " " : "")
                     + UiUtil.T("RWL_Eng_Percent", Mathf.RoundToInt((float)st.Progress.Fraction * 100f));
            string mlabel = ModelLabel();
            if (!string.IsNullOrEmpty(mlabel)) line = UiUtil.T("RWL_Eng_ModelPrefix", mlabel) + " | " + line;
            if (st.Shape != null && st.Shape.Slots > 0) line += " | " + UiUtil.T("RWL_Eng_Shape", st.Shape.Slots, Ctx(st.Shape.CtxPerSlot));
            if (st.TokS.HasValue && st.TokS.Value > 0) line += " | " + UiUtil.T("RWL_Eng_TokS", Mathf.RoundToInt((float)st.TokS.Value));
            if (st.Phase == EnginePhase.Error && st.Errors != null && st.Errors.Count > 0) line += " | " + st.Errors[st.Errors.Count - 1];
            return line;
        }

        /// <summary>True when the local engine reports that the model in use is the catalog's fallback.</summary>
        public static bool FallbackInUse
        {
            get { var st = _status; return LocalSource && st != null && st.ModelFallback; }
        }

        /// <summary>True while a starter model answers and the chosen model is still downloading (fast first run).</summary>
        public static bool StarterInUse
        {
            get { var st = _status; return LocalSource && st != null && Bridge.StarterWatch.IsStarter(st.ModelTier); }
        }

        /// <summary>The chosen model's download percent while a starter serves, or -1.</summary>
        public static int StarterPercent()
        {
            var st = _status;
            var p = st != null && st.Upgrade != null ? st.Upgrade.Progress : null;
            return p == null ? -1 : Bridge.StarterWatch.Percent(p.DoneBytes, p.TotalBytes);
        }

        public static string TargetName()
        {
            var st = _status;
            return st != null && st.Upgrade != null && !string.IsNullOrEmpty(st.Upgrade.TargetName) ? st.Upgrade.TargetName
                 : st != null ? st.Model : "";
        }

        /// <summary>The engine's note about the fallback model, or the stock sentence when it sent none.</summary>
        public static string FallbackNote(string fromEngine)
        {
            return string.IsNullOrWhiteSpace(fromEngine) ? UiUtil.T("RWL_Fallback_DefaultNote") : fromEngine.Trim();
        }

        /// <summary>The amber warning line for the status area, or null when the model in use is not a fallback.</summary>
        public static string FallbackLine()
        {
            if (StarterInUse)
            {
                int pct = StarterPercent();
                var up = _status.Upgrade != null ? _status.Upgrade.Progress : null;
                if (up != null && up.Verifying) return UiUtil.T("RWL_Starter_StatusLineVerifying", pct < 0 ? 0 : pct);
                return pct >= 0 ? UiUtil.T("RWL_Starter_StatusLine", pct) : UiUtil.T("RWL_Starter_StatusLineNoPct");
            }
            if (!FallbackInUse) return null;
            return UiUtil.T("RWL_Fallback_StatusLine", FallbackNote(_status.ModelNote));
        }

        public static string Ctx(int ctx) { return ctx % 1024 == 0 ? (ctx / 1024) + "k" : ctx.ToString(); }

        public static string PhaseLabel(string phase)
        {
            switch (phase)
            {
                case EnginePhase.NotInstalled: return UiUtil.T("RWL_Eng_Phase_NotInstalled");
                case EnginePhase.Downloading: return UiUtil.T("RWL_Eng_Phase_Downloading");
                case EnginePhase.Installing: return UiUtil.T("RWL_Eng_Phase_Installing");
                case EnginePhase.Starting: return UiUtil.T("RWL_Eng_Phase_Starting");
                case EnginePhase.Loading: return UiUtil.T("RWL_Eng_Phase_Loading");
                case EnginePhase.Ready: return UiUtil.T("RWL_Eng_Phase_Ready");
                case EnginePhase.Resizing: return UiUtil.T("RWL_Eng_Phase_Resizing");
                case EnginePhase.Sleeping: return UiUtil.T("RWL_Eng_Phase_Sleeping");
                case EnginePhase.External: return UiUtil.T("RWL_Eng_Phase_External");
                case EnginePhase.Error: return UiUtil.T("RWL_Eng_Phase_Error");
                default: return phase ?? "?";
            }
        }

        // ---------------------------------------------------------------------------------- first run
        /// <summary>Game thread, every frame (from RWLBridge.FrameUpdate): shows the first-run prompt once.</summary>
        public static void Tick()
        {
            if (!_showFirstRun || _firstRunShown) return;
            var s = RimWorldLLMMod.Settings;
            if (s == null || s.engineSetupDismissed || s.llmSource == "own") { _showFirstRun = false; return; }
            if (Find.WindowStack == null) return;
            _firstRunShown = true;
            _showFirstRun = false;
            Find.WindowStack.Add(new Window_EngineFirstRun());
        }
    }
}

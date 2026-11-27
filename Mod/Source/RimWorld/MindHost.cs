using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Hosts the mind (RimWorldLLM.Mind: MindRunner + Director) inside the mod. It runs on the thread pool, never on Unity's main thread, and reaches
    /// the game only through the bridge's in-process door (requests are served by RWLBridge.FrameUpdate on the game thread). The start/stop
    /// decisions live in the Verse-free MindSupervisor/MindPlan; this class only reads the game's state into MindInputs and wires the real runner.
    /// Nothing here throws into the game.
    /// </summary>
    public static class MindHost
    {
        const double TickEveryS = 0.25, StampEveryS = 5.0, EnginePollEveryS = 10.0, HoldMaxS = 600.0;
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly ConcurrentQueue<Action> GameQueue = new ConcurrentQueue<Action>();
        static readonly ConcurrentQueue<string> Logs = new ConcurrentQueue<string>();
        static readonly MindSupervisor Sup = new MindSupervisor(Make, Now, m => Logs.Enqueue("mind: " + m));
        static MindInputs _last = new MindInputs();
        static double _nextTick, _nextStamp, _nextEnginePoll, _holdUntil = double.PositiveInfinity;
        static string _stamp = "", _dataDir;
        static bool _sinkSet, _migrated;
        static Task _holdTask = Task.CompletedTask;

        static double Now() { return Clock.Elapsed.TotalSeconds; }

        sealed class Proc : IMindProc
        {
            readonly MindRunner _r;
            public Proc(MindRunner r) { _r = r; }
            public bool Running { get { return _r.Running; } }
            public string LastError { get { return _r.LastError; } }
            public int Restarts { get { return _r.Restarts; } }
            public void Start() { _r.Start(); }
            public Task StopAsync() { return _r.StopAsync(); }
        }

        /// <summary>Builds the runner for the current inputs (game thread; the config file is read here, a bad one throws ConfigError).</summary>
        static IMindProc Make(MindInputs i)
        {
            if (!_sinkSet) { _sinkSet = true; LlmLog.Sink = (lvl, logger, msg) => Logs.Enqueue("[" + lvl + "] " + msg); }
            if (!_migrated) { _migrated = true; try { MindConfig.MigrateLegacyDataDir(i.DataDir, MindEnv.Process); } catch (Exception) { } }
            var cfg = MindPlan.BuildConfig(i);
            if (!RWLBridge.Runtime.InProcessEnabled || RWLBridge.Runtime.Server == null) RWLBridge.Runtime.EnableInProcess();
            var runner = new MindRunner(cfg, new RimWorldAdapter(), new InProcessTransport(() => RWLBridge.Runtime.Server))
            {
                ChatLiveFactory = bridge => new InProcessChatLive(RWLBridge.Runtime.Vivarium.Live, () => GameQueue.Enqueue(NewNarratorCycle))
            };
            return new Proc(runner);
        }

        /// <summary>A narrator line began (a steward cycle): fresh build caps, as POST /chat/status does. Game thread.</summary>
        static void NewNarratorCycle()
        {
            RWLBridge.Runtime.Vivarium.Budget.NewCycle(RWLBridge.BuildState().Tick);
        }

        static MindInputs Snapshot(RimWorldLLMSettings s)
        {
            var i = new MindInputs
            {
                ModEnabled = s.enabled,
                GameLoaded = RWLBridge.BuildState().GameLoaded,
                Own = s.llmSource == "own",
                OwnBaseUrl = s.llmBaseUrl ?? "", OwnModel = s.llmModel ?? "", OwnApiKey = s.llmApiKey ?? "",
                EngineBase = EngineManager.OpenAiBase, EngineModel = EngineManager.CurrentModel,
                EngineReady = EngineReady(EngineManager.Status),
                DataDir = _dataDir ?? (_dataDir = AgentDataDir.ResolveHere(EnginePlatform.Detect()).Path),
                FileStamp = _stamp,
                PauseAmbientAtSpeed = s.pauseAmbientAtSpeed,
                Wants = s.wantsEnabled, Dreams = s.dreamsEnabled, ArtDescriptions = s.artDescriptions, Recap = s.recapOnLoad, TextFill = s.textGen
            };
            i.Held = Sup.Held;
            return i;
        }

        static bool EngineReady(EngineStatus st)
        {
            return st != null && (st.Phase == EnginePhase.Ready || st.Phase == EnginePhase.Sleeping || st.Phase == EnginePhase.External);
        }

        /// <summary>Every frame, game thread (RWLBridge.FrameUpdate). Cheap: the real work runs about four times a second.</summary>
        public static void Tick()
        {
            try
            {
                string line;
                while (Logs.TryDequeue(out line)) Log.Message("[RimWorldLLM] " + line);
                Action a;
                while (GameQueue.TryDequeue(out a)) { try { a(); } catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] mind: " + e.Message, 7731010); } }
                double now = Now();
                if (now < _nextTick) return;
                _nextTick = now + TickEveryS;
                var s = RimWorldLLMMod.Settings;
                if (s == null) return;
                if (Sup.Held && now > _holdUntil) Sup.Release();   // a removal that never finished must not keep the mind off for good
                if (now >= _nextStamp) { _nextStamp = now + StampEveryS; _stamp = MindPlan.Stamp(_dataDir ?? (_dataDir = AgentDataDir.ResolveHere(EnginePlatform.Detect()).Path)); }
                var i = Snapshot(s);
                // keep the engine's status and port fresh while the mind needs them (the settings window is not necessarily open)
                if (i.GameLoaded && i.ModEnabled && !i.Own && now >= _nextEnginePoll)
                {
                    _nextEnginePoll = now + (EngineManager.Client != null ? 2.0 : EnginePollEveryS);
                    EngineManager.Touch();
                }
                _last = i;
                Sup.Tick(i);
            }
            catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] mind host: " + e, 7731011); }
        }

        /// <summary>The game ended (GenScene.GoToMainMenu): stop the mind at once.</summary>
        public static void OnGameEnd()
        {
            try { _last.GameLoaded = false; Sup.StopNow(); } catch (Exception) { }
        }

        /// <summary>Stops the mind and keeps it off (a memory removal is about to delete its folder). Pair with <see cref="Release"/>.</summary>
        public static void Hold()
        {
            try { _holdUntil = Now() + HoldMaxS; _holdTask = Sup.HoldAsync(); } catch (Exception) { }
        }

        /// <summary>Completes once the mind has stopped (after Hold).</summary>
        public static Task StoppedAsync() { return _holdTask ?? Task.CompletedTask; }

        /// <summary>The mind may run again; it starts at the next tick when a game is loaded.</summary>
        public static void Release() { try { Sup.Release(); _holdUntil = double.PositiveInfinity; } catch (Exception) { } }

        public static MindState State
        {
            get { return MindPlan.State(_last, Sup.IsRunning, Sup.LastError, Sup.ErrorAgeS); }
        }

        /// <summary>The line for the settings window: mind running / waiting for the model / waiting for a game / error.</summary>
        public static string StatusLine()
        {
            var st = State;
            string key = MindPlan.StateKey(st);
            return st == MindState.Error ? UiUtil.T(key, Sup.LastError ?? "") : UiUtil.T(key);
        }
    }
}

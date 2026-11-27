// Port of agent/rimworldllm_agent/core/director.py: the orchestrator. One StepAsync polls events, writes witness-only memories,
// submits work to the lease manager, starts what the windows allow and saves dirty minds. Work runs as tasks; DrainAsync waits for them.
//
// Concurrency (not in the Python): asyncio ran one thing at a time and switched only at an await that suspends. The Director keeps that
// by running every public entry point (StartAsync, StepAsync, RunAsync, DrainAsync) and every Spawn()ed task on one
// ConcurrentExclusiveSchedulerPair exclusive scheduler (FIFO, one task at a time, a task yields only at an await that does not complete
// at once, as an asyncio task did). Spawn() defers the start like asyncio.ensure_future. Tests run their bodies through RunOnLoop so the
// same holds for them. The one gap: library code that awaits with ConfigureAwait(false) continues on the thread pool for the rest of
// that library call (it re-joins the loop when it returns to the Director), as noted in docs/csmind/parity-director.md.
//
// Split in partial files: Director.cs (state, clock, loading, the loop), Director.Events.cs (ingest, react, talk, launch),
// Director.Serve.cs (prepare, serve, say, bystanders, conversations, narrator, save), Director.Hosts.cs (the host interfaces).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    public sealed partial class Director : IDisposable, IGroupHost, IIdleHost, IConcernsHost, IGrudgesHost, IStewardHost, IRecapHost, ICommentaryHost
    {
        /// <summary>After a PUT /mind conflict: the player's edits in the game's blob win over ours (director's notes and notes_rev, an
        /// edited voice card); every other field stays ours. True if anything was taken.</summary>
        public static bool MergePlayerEdits(Mind mind, JObject theirs)
        {
            if (theirs == null || theirs.Count == 0) return false;
            bool took = false;
            if (BridgeJson.Truthy(theirs["notes_edited_by_player"], false))
            {
                JToken n = theirs["notes"];
                if (n != null) mind.Notes = n.Type == JTokenType.String ? (string)n : PyJson.Str(n);
                JToken r = theirs["notes_rev"];
                if (r != null) mind.NotesRev = (int)JobsJson.PyInt(r);
                mind.NotesEditedByPlayer = true;
                took = true;
            }
            var v = theirs["voice"] as JObject;
            if (v != null && BridgeJson.Truthy(v["edited_by_player"], false))
            {
                try { mind.Voice = PyModel.Validate<VoiceCard>(v); took = true; }
                catch (Exception) { }
            }
            return took;
        }

        public MindConfig Cfg { get; }
        public IBridge Bridge { get; }
        public ILlmClient Llm { get; }
        public IGameAdapter Adapter { get; }
        private readonly Func<double> _clock;
        public readonly PyRandom Rng;
        public readonly IEventSource Events;
        public int Day { get; }
        public Dictionary<int, Mind> Minds { get; } = new Dictionary<int, Mind>();
        public readonly Dictionary<int, int> Revs = new Dictionary<int, int>();
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly Dictionary<int, HashSet<string>> _nameTokens = new Dictionary<int, HashSet<string>>();
        private readonly Dictionary<int, double> _weights = new Dictionary<int, double>();
        private readonly Dictionary<int, int> _lineCount = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _wantsLast = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _reputations = new Dictionary<int, string>();
        public Ledger Ledger { get; }
        public int RecapEpoch { get; set; }
        public LeaseManager Leases { get; }
        private readonly HashSet<Task> _tasks = new HashSet<Task>();
        public JObject BridgeCfg { get; private set; } = new JObject();
        public readonly List<JObject> Said = new List<JObject>();
        public readonly Dictionary<int, double> ShoutUntil = new Dictionary<int, double>();
        public List<(int Actor, string Reason)> Dropped { get; } = new List<(int Actor, string Reason)>();
        private int _convN;
        public readonly LoopStats Loop = new LoopStats();
        public readonly Dictionary<int, double?> Ages = new Dictionary<int, double?>();
        /// <summary>A toolloop backend; wants with follow-through and the steward are off while null.</summary>
        public IBackend ToolBackend { get; set; }
        public string ToolMode { get; set; } = "auto";
        public WantsStats Wants { get; } = new WantsStats();
        public GateStats Gate { get; } = new GateStats();
        public readonly Dictionary<int, List<string>> RecentOwn = new Dictionary<int, List<string>>();
        public bool Started { get; private set; }
        public Chronicle Chronicle { get; }
        public LoreBook Lore { get; }
        public ArtQueue Art { get; } = new ArtQueue();
        /// <summary>The game's text queue answered: it writes the art texts, so the art idle job stands down.</summary>
        public bool TextQueueLive { get; set; }
        /// <summary>Set by the adapter's idle fill: real seconds before the next call.</summary>
        public double TextFillWaitS { get; set; }
        public HashSet<string> TextTried { get; } = new HashSet<string>();
        public ModelTier Tier { get; }
        public DreamBook DreamBook { get; } = new DreamBook();
        public ConcernEngine Concerns { get; }
        public GrudgeEngine Grudges { get; }
        public IdleRunner Idle { get; }
        public readonly Steward Steward;
        /// <summary>core/commentary.py: the documentary voice on the narrator slot.</summary>
        public Commentary Commentary { get; }
        private int _upkeepDay = -1;
        private double? _rawLast;
        private double _play;
        private int _speedSeen = 1;
        public readonly PyRandom InnerRng = new PyRandom(11);
        public readonly CloudGate CloudGate = new CloudGate();
        public readonly DayCap FirstCap = new DayCap();
        public GroupState Group { get; } = new GroupState();
        public readonly Bystanders Bystand = new Bystanders();
        public List<PieceStats> CreativeLog { get; } = new List<PieceStats>();
        private double _cfgAt = double.NegativeInfinity;
        private (string Name, ILlmClient From, ILlmClient Routed)? _aimLlmCache;
        private readonly ConcurrentExclusiveSchedulerPair _pair = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1);
        private readonly CreativeView _creative;
        private readonly GroundView _ground;

        /// <summary>The live chat sink the steward streams to (null: a /chat/status around the call, as the Python).</summary>
        public IChatLive ChatLive { get { return Steward.Live; } set { Steward.Live = value; } }

        /// <param name="streaming">hand the steward a BridgeChatLive (POST /chat/status and /chat/partial).</param>
        public Director(MindConfig cfg, IBridge bridge, ILlmClient llm, IGameAdapter adapter, IEventSource events = null,
                        Func<double> clock = null, PyRandom rng = null, bool streaming = false)
        {
            Cfg = cfg; Bridge = bridge; Llm = llm; Adapter = adapter;
            _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            Rng = rng ?? new PyRandom(7);
            Events = events ?? new EventPoller(bridge, 0, adapter.NormalizeEvent);
            Day = adapter.UnitsPerDay;
            Ledger = new Ledger(adapter.IsLedgerEnd);
            var q = cfg.Quotas;
            Leases = new LeaseManager(cfg.ActorSlots, cfg.NarratorSlot,
                new QuotaTracker(q.ActorPerDay, q.PairPerDay, Day, q.MinGapGameAmbient, q.MinGapRealSAmbient, q.AmbientShare),
                cfg.PauseAmbientAtSpeed, adapter.Priority,
                new VoiceBudget(cfg.VoiceLinesPerMin, cfg.VoiceBurst, cfg.VoiceFastScale));
            Chronicle = new Chronicle(cfg.DataDir, Day);
            Lore = new LoreBook(cfg.LoreMax);
            Tier = new ModelTier(cfg, _clock);
            _creative = new CreativeView(this);
            _ground = new GroundView(this);
            Concerns = new ConcernEngine(this);
            Grudges = new GrudgeEngine(this);
            Idle = new IdleRunner(this);
            Commentary = new Commentary(this);
            Steward = new Steward(this);
            if (streaming) Steward.Live = new BridgeChatLive(bridge);
        }

        public void Dispose() { }

        // ---- the loop (asyncio stand-in) --------------------------------------
        public TaskScheduler Scheduler { get { return _pair.ExclusiveScheduler; } }
        public bool OnLoop { get { return TaskScheduler.Current == _pair.ExclusiveScheduler; } }

        /// <summary>Runs work on the director's loop (inline when already there).</summary>
        public Task RunOnLoop(Func<Task> work)
        {
            if (OnLoop) return work();
            return Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _pair.ExclusiveScheduler).Unwrap();
        }

        public Task<T> RunOnLoop<T>(Func<Task<T>> work)
        {
            if (OnLoop) return work();
            return Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _pair.ExclusiveScheduler).Unwrap();
        }

        /// <summary>d._spawn(coro): a task tracked by the director, started at the next suspension of the caller; a failure is
        /// logged and never thrown (asyncio's _report).</summary>
        public Task Spawn(Func<Task> work)
        {
            Task t = Task.Factory.StartNew(async () =>
            {
                try { await work(); }
                catch (OperationCanceledException) { }
                catch (Exception e) { LlmLog.Warning("rimworldllm", "task failed: " + PyNames.Of(e) + ": " + e.Message); }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _pair.ExclusiveScheduler).Unwrap();
            lock (_tasks) _tasks.Add(t);
            return t;
        }

        private List<Task> TaskList() { lock (_tasks) return _tasks.Where(t => !t.IsCompleted).ToList(); }

        /// <summary>The number of spawned tasks still running.</summary>
        public int TaskCount { get { return TaskList().Count; } }

        /// <summary>Waits until every spawned task is done (tests and replay use it).</summary>
        public Task DrainAsync()
        {
            return RunOnLoop(async () =>
            {
                while (true)
                {
                    var l = TaskList();
                    lock (_tasks) _tasks.RemoveWhere(t => t.IsCompleted);
                    if (l.Count == 0) break;
                    await Task.WhenAll(l);
                    await Task.Yield();
                }
            });
        }

        // ---- clock ----------------------------------------------------------
        /// <summary>The director clock (d.clock()): monotonic seconds.</summary>
        public double Clock() { return _clock(); }

        /// <summary>Both clocks. Real is the play clock (it stands still while the game is paused, so nothing expires in a pause), Rate
        /// the game units per real second at the speed the game last reported.</summary>
        public Now Now()
        {
            double raw = _clock();
            var env = Bridge.Env;
            if (_rawLast != null && raw > _rawLast.Value && !env.Paused) _play += raw - _rawLast.Value;
            _rawLast = _rawLast == null ? raw : Math.Max(_rawLast.Value, raw);
            if (env.Speed >= 1) _speedSeen = env.Speed;
            return new Now(env.Game, _play, Adapter.UnitsPerSecond(_speedSeen));
        }

        /// <summary>Real seconds between polls: shorter as the game speeds up.</summary>
        public double PollDelay() { return Cfg.PollIntervalS / Math.Max(1.0, Math.Pow(Now().Rate / 60.0, 0.5)); }

        /// <summary>expires_tick for the mod: the item's deadline at the current speed, plus half a second of slack for the
        /// request itself, so the mod only refuses what is truly late.</summary>
        public int? WireExpiry(WorkItem item)
        {
            var now = Now();
            long? dl = item.Deadline(now.Rate);
            return dl == null ? (int?)null : (int)(dl.Value + (long)(0.5 * now.Rate));
        }

        // ---- age gate (core/agegate.py) ---------------------------------------
        /// <summary>Under 18; an actor whose age was never read counts as a minor.</summary>
        public bool IsMinor(int? aid)
        {
            if (aid == null) return false;
            double? a;
            return AgeGate.IsMinor(Ages.TryGetValue(aid.Value, out a) ? a : null);
        }

        public ISet<int> MinorIds() { return new HashSet<int>(_names.Keys.Where(i => IsMinor((int?)i))); }

        public HashSet<string> MinorTokens()
        {
            var s = new HashSet<string>();
            if (_names.Count == 0) return s;
            foreach (int i in MinorIds())
            {
                HashSet<string> tk;
                if (_nameTokens.TryGetValue(i, out tk)) s.UnionWith(tk);
            }
            return s;
        }

        private ISet<string> Romantic() { return Adapter.RomanticIntents; }

        private void LearnAge(int aid, JObject snap) { Ages[aid] = Adapter.SnapshotAge(snap); }

        public async Task LearnAgesAsync(IEnumerable<int> ids)
        {
            foreach (int i in ids.ToList())
            {
                if (_names.ContainsKey(i) && !Ages.ContainsKey(i))
                {
                    try { LearnAge(i, await Bridge.ActorAsync(i)); }
                    catch (BridgeError) { Ages[i] = null; }
                }
            }
        }

        public IList<string> LabelsFor(bool minor) { return AgeGate.LabelsFor(Adapter.IntentLabels.ToList(), Romantic(), minor); }

        /// <summary>The tool-loop allowed set for a scene: romance-type tools are removed when a minor is involved.</summary>
        public ISet<string> ToolsAllowed(IEnumerable<string> names, bool minor) { return AgeGate.AllowedTools(names, Romantic(), minor); }

        // ---- loading ----------------------------------------------------------
        public Task StartAsync() { return RunOnLoop(StartCore); }

        private async Task StartCore()
        {
            await Bridge.HealthAsync();
            await RefreshConfigAsync();
            await LoadWorldAsync();
            if (Cfg.Lore) await RimWorldLLM.Mind.Lore.LoadAsync(this);
            if (Recap.LateDue(this))
            {
                int ep = Bridge.Env.Epoch;
                _ = Spawn(() => RecapWhenFreeAsync(ep));          // connected after the load: the window is still open
            }
            Started = true;
        }

        public async Task RefreshConfigAsync()
        {
            _cfgAt = _clock();
            BridgeCfg = await Bridge.ConfigAsync();
            JToken p = BridgeCfg["pause_ambient_at_speed"];
            Leases.PauseAmbientAtSpeed = p != null ? (int)JobsJson.PyInt(p) : Cfg.PauseAmbientAtSpeed;
            JToken ad = BridgeCfg["adult_content"];
            Adult.SetEnabled(ad == null ? (object)false : (ad.Type == JTokenType.Boolean ? (object)(bool)ad : (object)PyJson.Truthy(ad)));
        }

        /// <summary>Lines dropped because the Adult content setting is off (reason ending adult_off, any path).</summary>
        public int AdultOffDrops() { return Dropped.Count(d => d.Reason.EndsWith("adult_off", StringComparison.Ordinal)); }

        public JObject GateStatsDict()
        {
            var o = Gate.ToDict();
            o["adult_off_dropped"] = AdultOffDrops();
            return o;
        }

        public async Task<List<int>> RosterIdsAsync()
        {
            JObject resp = await Bridge.RosterAsync();
            Ledger.LoadWorld(Adapter.RosterLedger(resp));
            var ids = new List<int>();
            foreach (var (aid, raw) in Adapter.Roster(resp)) { RememberName(aid, raw); ids.Add(aid); }
            return ids;
        }

        private void RememberName(int aid, JToken raw)
        {
            NameInfo ni = Adapter.NameInfo(raw);
            if (!string.IsNullOrEmpty(ni.Short)) { _names[aid] = ni.Short; _nameTokens[aid] = new HashSet<string>(ni.Tokens); }
        }

        public async Task LoadWorldAsync()
        {
            foreach (int aid in await RosterIdsAsync())
                if (!Adapter.IsControlled(aid)) await LoadActorAsync(aid);
        }

        public async Task<Mind> LoadActorAsync(int aid)
        {
            JObject snap, blob; int rev;
            try
            {
                snap = await Bridge.ActorAsync(aid);
                var kv = await Bridge.GetMindAsync(aid);
                blob = kv.Key; rev = kv.Value;
            }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "cannot load actor " + aid + ": " + e.Message); return null; }
            RememberName(aid, Adapter.SnapshotName(snap));
            LearnAge(aid, snap);
            string nm;
            _names.TryGetValue(aid, out nm);
            Mind mind = Mind.FromBlob(blob, aid, nm ?? "");
            Revs[aid] = rev;
            Minds[aid] = mind;
            _weights[aid] = Adapter.TalkWeight(snap);
            if (blob == null || blob.Count == 0) mind.LastReflectTime = Bridge.Env.Game;        // the interval starts when we first meet them
            if (await MindBuilders.EnsureMindBuiltAsync(mind, snap, _names, Adapter, Llm, Cfg.UseLlmVoiceCards)) _dirty.Add(aid);
            return mind;
        }

        /// <summary>The world was reloaded (epoch changed): drop the working minds and re-read them.</summary>
        public async Task ReloadMindsAsync()
        {
            // Python cancels the tasks and awaits them. A C# task cannot be cancelled from outside: wait a bounded time for the
            // running ones to finish on their own, then go on.
            var old = TaskList();
            if (old.Count > 0)
                await Task.WhenAny(Task.WhenAll(old), Task.Delay(TimeSpan.FromSeconds(2)));
            lock (_tasks) _tasks.Clear();
            Idle.Preempt("reload");
            Commentary.Reset();
            Minds.Clear(); Revs.Clear(); _dirty.Clear();
            _upkeepDay = -1;
            Ledger.Clear();
            Steward.Reset();
            Leases.Leases.Clear(); Leases.Queue.Clear(); Leases.LastSignature.Clear();
            Events.Reset();
            Bridge.EpochChanged = false;
            if (Bridge.Env.Epoch == 0) return;                // no world loaded: nothing to read
            await RefreshConfigAsync();
            await LoadWorldAsync();
            Art.Rollback((int)Bridge.Env.Game);
            Chronicle.Rollback(Bridge.Env.Game);              // the chronicle follows the save back in time
            if (Cfg.Lore) await RimWorldLLM.Mind.Lore.LoadAsync(this);          // the loaded save's own lore
            if (Cfg.RecapEnabled && !JobsJson.IsFalse(BridgeCfg["recap"]))
            {
                int ep = Bridge.Env.Epoch;
                _ = Spawn(() => RecapWhenFreeAsync(ep));          // once per epoch; a one-off, no voice budget
            }
        }

        /// <summary>The recap uses slot 0: it waits for a steward cycle to end instead of running beside it.</summary>
        private async Task RecapWhenFreeAsync(int epoch)
        {
            while (Leases.NarratorOwner != null) await Task.Delay(500);
            await Recap.RunAsync(this, epoch);
        }

        // ---- main loop --------------------------------------------------------
        public Task StepAsync() { return RunOnLoop(StepCore); }

        private async Task StepCore()
        {
            if (Tier.Due()) _ = Spawn(() => Tier.RefreshAsync(Llm));             // off the line path; at most every 60 s
            List<JObject> events = await Events.PollAsync();
            if (Bridge.EpochChanged) { await ReloadMindsAsync(); return; }
            if (Events.LastGap) await RosterIdsAsync();           // events were lost: resync the ledger from the roster
            if (_clock() - _cfgAt >= Cfg.ConfigRefreshRealS)
            {
                try { await RefreshConfigAsync(); }           // mod settings (adult content, wants, dreams...) apply within seconds
                catch (BridgeError e) { LlmLog.Warning("rimworldllm", "config refresh failed: " + e.Message); }
            }
            await IngestAsync(events);
            await TakeTalkAsync();
            await Steward.StepAsync();                         // one GET /vivarium when idle; may start a cycle that owns slot 0
            Idle.PreemptIfWanted();                            // idle work yields to actor windows at once
            Commentary.PreemptIfWanted();                      // ... and commentary yields to a steward cycle
            DailyUpkeep();
            Grudges.Advance();
            Leases.Expire(Now());
            Leases.ExpireLeases(Now());
            Launch();
            await SaveDirtyAsync();
        }

        /// <summary>Runs until stop is cancelled: waits for a loaded world (the game may be at the main menu), then steps. A bridge
        /// error in a step is logged; waits are in real time (waitS between world checks).</summary>
        public Task RunAsync(CancellationToken stop = default(CancellationToken), double waitS = 2.0)
        {
            return RunOnLoop(async () =>
            {
                while (!stop.IsCancellationRequested)             // wait for a world: the host may not have one loaded yet
                {
                    try { await StartCore(); break; }
                    catch (BridgeError e)
                    {
                        if (e.Code != "no_game" && e.Code != "unreachable") throw;
                        LlmLog.Info("rimworldllm", "waiting for a world (" + e.Code + ")");
                        try { await Task.Delay(TimeSpan.FromSeconds(waitS), stop); } catch (OperationCanceledException) { return; }
                    }
                }
                if (stop.IsCancellationRequested) return;
                while (!stop.IsCancellationRequested)
                {
                    try { await StepCore(); }
                    catch (BridgeError e) { LlmLog.Warning("rimworldllm", "bridge: " + e.Message); }
                    try { await Task.Delay(TimeSpan.FromSeconds(PollDelay()), stop); } catch (OperationCanceledException) { return; }
                }
            });
        }
    }
}

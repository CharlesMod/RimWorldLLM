// Port of games/rimworld/speedsim.py: one synthetic colony day (or a recorded events file) played at game speed 1-4 against the whole agent,
// with a fake LLM that has realistic latency (3 slots, jitter). Time is virtual. Python used an asyncio loop whose clock jumps to the next
// timer; here a TaskScheduler runs everything on one thread and, when its queue is empty, jumps the clock to the next timer. The Director
// takes that scheduler and a delay function through two optional constructor parameters (Director.cs); nothing else in it knows about the sim.
// A day at speed 1 (1000 s) costs a second of CPU and results are deterministic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;

namespace RimWorldLLM.Harness
{
    // ---- virtual time ---------------------------------------------------------------------------------------------
    /// <summary>A single-thread scheduler with a virtual clock: when nothing is runnable the clock jumps to the next timer.</summary>
    public sealed class VirtualTime : TaskScheduler
    {
        readonly Queue<Task> _q = new Queue<Task>();
        readonly SortedDictionary<(double, long), (TaskCompletionSource<bool> Tcs, CancellationTokenRegistration Reg)> _timers =
            new SortedDictionary<(double, long), (TaskCompletionSource<bool>, CancellationTokenRegistration)>();
        long _seq;
        Thread _thread;
        public double Now;
        public bool OnDriver => Thread.CurrentThread == _thread;
        public static bool Debug = Environment.GetEnvironmentVariable("SPEEDSIM_DEBUG") != null;
        bool _warned;
        public void Check(string where) { if (Debug && !OnDriver && !_warned) { _warned = true; Console.Error.WriteLine("ESCAPE at " + where + "\n" + Environment.StackTrace); } }

        protected override void QueueTask(Task task) { lock (_q) _q.Enqueue(task); }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => !taskWasPreviouslyQueued && Thread.CurrentThread == _thread && TryExecuteTask(task);
        protected override IEnumerable<Task> GetScheduledTasks() { lock (_q) return _q.ToArray(); }
        public override int MaximumConcurrencyLevel => 1;

        /// <summary>asyncio.sleep: completes when the virtual clock reaches now + seconds.</summary>
        public Task Delay(double seconds, CancellationToken ct = default(CancellationToken))
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var tcs = new TaskCompletionSource<bool>();       // continuations run inline on the driver thread
            long k = Interlocked.Increment(ref _seq);
            var key = (Now + Math.Max(0.0, seconds), k);
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);
            if (ct.CanBeCanceled) reg = ct.Register(() => tcs.TrySetCanceled(ct));
            lock (_timers) _timers[key] = (tcs, reg);
            return tcs.Task;
        }

        /// <summary>Runs the coroutine to completion on a fresh thread (no SynchronizationContext), driving the clock.</summary>
        public T Run<T>(Func<Task<T>> main)
        {
            T result = default(T); Exception err = null;
            var th = new Thread(() =>
            {
                try { result = Drive(main); }
                catch (Exception e) { err = e; }
            }, 16 * 1024 * 1024);
            th.Start(); th.Join();
            if (err != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(err).Throw();
            return result;
        }


        // A continuation awaited with ConfigureAwait(false) from inside a task on this scheduler is queued to the thread pool (inlining is not valid
        // under a custom scheduler), so the driver may only jump the clock once the pool has gone quiet: no queued items, and a sentinel item
        // queued behind them has run with no other item completing meanwhile. Items are short CPU runs, so this settles in a few microseconds.
        static readonly double SettleMs = double.Parse(Environment.GetEnvironmentVariable("SPEEDSIM_SETTLE_MS") ?? "0.5", CultureInfo.InvariantCulture);
        bool PoolQuiet()
        {
            for (int round = 0; round < 200; round++)
            {
                long c0 = ThreadPool.CompletedWorkItemCount;
                using (var ev = new ManualResetEventSlim())
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => ev.Set(), null);
                    ev.Wait();
                }
                lock (_q) { if (_q.Count > 0) return false; }
                if (ThreadPool.PendingWorkItemCount == 0 && ThreadPool.CompletedWorkItemCount - c0 <= 1) return true;
                Thread.Yield();
            }
            return true;
        }

        T Drive<T>(Func<Task<T>> main)
        {
            _thread = Thread.CurrentThread;
            Task<T> t = Task.Factory.StartNew(main, CancellationToken.None, TaskCreationOptions.DenyChildAttach, this).Unwrap();
            while (!t.IsCompleted)
            {
                Task next = null;
                lock (_q) { if (_q.Count > 0) next = _q.Dequeue(); }
                if (next != null) { TryExecuteTask(next); continue; }
                if (!PoolQuiet()) continue;
                { var sw0 = System.Diagnostics.Stopwatch.StartNew(); while (sw0.Elapsed.TotalMilliseconds < SettleMs) Thread.SpinWait(200); }
                lock (_q) { if (_q.Count > 0) continue; }
                if (!PoolQuiet()) continue;                                              // a ConfigureAwait(false) continuation is still running on the pool
                // idle: jump to the next live timer
                while (true)
                {
                    KeyValuePair<(double, long), (TaskCompletionSource<bool> Tcs, CancellationTokenRegistration Reg)> first;
                    lock (_timers)
                    {
                        if (_timers.Count == 0) break;
                        first = _timers.First();
                        _timers.Remove(first.Key);
                    }
                    first.Value.Reg.Dispose();
                    if (first.Value.Tcs.Task.IsCompleted) continue;            // cancelled: the clock does not move for it
                    Now = Math.Max(Now, first.Key.Item1);
                    first.Value.Tcs.TrySetResult(true);
                    break;
                }
                bool dead;
                lock (_q) { lock (_timers) dead = _q.Count == 0 && _timers.Count == 0 && !t.IsCompleted; }
                if (dead)
                {
                    // something may be running on a pool thread (a ConfigureAwait(false) escape): give it a moment to queue back
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 500 && !t.IsCompleted) { lock (_q) { if (_q.Count > 0) { dead = false; break; } } Thread.Sleep(1); }
                    if (dead && !t.IsCompleted) throw new InvalidOperationException("the simulation deadlocked at virtual t=" + Now.ToString("F3", CultureInfo.InvariantCulture));
                }
            }
            return t.GetAwaiter().GetResult();
        }
    }

    // ---- the model server -----------------------------------------------------------------------------------------
    /// <summary>FakeLlm text, with latency: 3 slots share the machine, so at most Slots calls decode at once.</summary>
    public sealed class SimLlm : ILlmClient
    {
        static readonly Dictionary<string, (double, double)> Lat = new Dictionary<string, (double, double)>
        {
            ["line"] = (0.10, 0.25), ["intent"] = (0.09, 0.13), ["memory"] = (0.15, 0.30), ["relation"] = (0.15, 0.30),
            ["narrator"] = (0.15, 0.30), ["diary"] = (1.2, 2.5), ["chronicle"] = (2.3, 3.5), ["chronicle_summary"] = (1.5, 2.5),
            ["reflect"] = (3.0, 5.0),
        };
        readonly FakeLlm _fake = new FakeLlm();
        readonly PyRandom _rng; readonly VirtualTime _vt;
        public readonly int Slots;
        int _free; readonly Queue<TaskCompletionSource<bool>> _waiters = new Queue<TaskCompletionSource<bool>>();
        public int InFlight, MaxInFlight;
        public double BusyS; double _last;
        public Dictionary<string, int> Kinds = new Dictionary<string, int>();
        readonly HashSet<int> _seen = new HashSet<int>();

        public SimLlm(PyRandom rng, VirtualTime vt, int slots = 3) { _rng = rng; _vt = vt; Slots = slots; _free = slots; }

        readonly object _g = new object();       // continuations may resume on a pool thread (ConfigureAwait(false)), so the model's state is locked
        public void Tick() { lock (_g) { double t = _vt.Now; BusyS += InFlight * (t - _last); _last = t; } }

        async Task Acquire()
        {
            TaskCompletionSource<bool> w = null;
            lock (_g) { if (_free > 0) _free--; else { w = new TaskCompletionSource<bool>(); _waiters.Enqueue(w); } }
            if (w != null) await w.Task;
        }
        void Release()
        {
            TaskCompletionSource<bool> w = null;
            lock (_g) { if (_waiters.Count > 0) w = _waiters.Dequeue(); else _free++; }
            w?.SetResult(true);
        }

        public async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                                    IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                                    int? actor = null, JObject jsonSchema = null)
        {
            _vt.Check("llm");
            await Acquire();
            try
            {
                Tick();
                double wait; 
                lock (_g)
                {
                    InFlight++; MaxInFlight = Math.Max(MaxInFlight, InFlight);
                    var (lo, hi) = Lat.TryGetValue(kind, out var l) ? l : (0.1, 0.3);
                    bool cold = slot != null && !_seen.Contains(slot.Value);
                    _seen.Add(slot ?? -1);
                    wait = _rng.Uniform(lo, hi) + (cold && kind == "line" ? 0.15 : 0.0);
                }
                try
                {
                    await _vt.Delay(wait);
                    lock (_g) Kinds[kind] = (Kinds.TryGetValue(kind, out int n) ? n : 0) + 1;
                    return await _fake.CompleteAsync(messages, slot, kind, maxTokens, stop, grammar, temperature, actor, jsonSchema);
                }
                finally { Tick(); lock (_g) InFlight--; }
            }
            finally { Release(); }
        }
    }

    // ---- the colony -----------------------------------------------------------------------------------------------
    public sealed class SimEvent
    {
        public int Tick; public string Type; public List<KeyValuePair<int, string>> Actors; public List<int> Witnesses;
        public double Severity; public string Summary; public JObject Data = new JObject();
        public SimEvent(int tick, string type, IEnumerable<(int, string)> actors, IEnumerable<int> witnesses, double severity, string summary, JObject data = null)
        {
            Tick = tick; Type = type; Actors = actors.Select(a => new KeyValuePair<int, string>(a.Item1, a.Item2)).ToList();
            Witnesses = witnesses.ToList(); Severity = severity; Summary = summary; if (data != null) Data = data;
        }
    }

    public static class SpeedSim
    {
        public const int Day = 60000;
        public static readonly Dictionary<int, int> TicksPerS = new Dictionary<int, int> { [1] = 60, [2] = 180, [3] = 360, [4] = 900 };

        static string Inv(int i) => i.ToString(CultureInfo.InvariantCulture);

        /// <summary>A synthetic colony day: thoughts, level ups, chitchat and a few consequential social interactions, injuries and tends,
        /// one mental break, and a raid (raidSize events inside 1,080 ticks). Draws from PyRandom in the Python's order.</summary>
        public static List<SimEvent> ColonyDay(int seed = 3, int pawns = 4, int days = 1, int raidAt = 30000, int raidSize = 40)
        {
            var r = new PyRandom(seed);
            var ids = Enumerable.Range(1, pawns).ToList();
            var ev = new List<SimEvent>();
            int n = 0;
            List<int> Near(IEnumerable<int> who)
            {
                var s = new SortedSet<int>(who);
                foreach (int i in ids) if (r.Random() < 0.6) s.Add(i);
                return s.ToList();
            }
            void Add(long t, string typ, (int, string)[] actors, double sev, string summary, JObject data = null, List<int> wit = null)
            {
                n++;
                ev.Add(new SimEvent((int)t, typ, actors, wit ?? Near(actors.Select(a => a.Item1)), sev, summary + " #" + Inv(n), data));
            }
            JObject Van(string rf, string tier, string inter) => new JObject
            {
                ["vanilla"] = true, ["ref"] = rf, ["tier"] = tier, ["interaction"] = inter, ["vanilla_text"] = "A sentence the game wrote.", ["window_ticks"] = 417,
            };
            for (int d = 0; d < days; d++)
            {
                long b = (long)d * Day;
                for (int k = 0; k < 40; k++)
                {
                    int p = r.Choice(ids); long t = b + r.RandRange(Day); double sev = r.Uniform(0.2, 0.45);
                    Add(t, "thought", new[] { (p, "subject") }, sev, "Pawn" + Inv(p) + " had a passing thought");
                }
                for (int k = 0; k < 6; k++)
                {
                    int p = r.Choice(ids); long t = b + r.RandRange(Day); double sev = r.Uniform(0.25, 0.45);
                    Add(t, "level_up", new[] { (p, "subject") }, sev, "Pawn" + Inv(p) + " gained a skill level");
                }
                for (int i = 0; i < 22; i++)          // the game's own interactions: chitchat (idle) and consequential (high)
                {
                    var ab = r.Sample(ids, 2); int a = ab[0], c = ab[1];
                    bool high = i % 4 == 0;
                    long t = b + r.RandRange(Day);
                    Add(t, "social", new[] { (a, "initiator"), (c, "recipient") }, high ? 0.5 : 0.26, "Pawn" + Inv(a) + " and Pawn" + Inv(c) + " spoke",
                        Van("v" + Inv(d) + "-" + Inv(i), high ? "high" : "idle", high ? "Insult" : "Chitchat"), new List<int> { a, c });
                }
                for (int k = 0; k < 8; k++)           // a pair meets and talks (a conversation)
                {
                    var ab = r.Sample(ids, 2); int a = ab[0], c = ab[1];
                    long t = b + r.RandRange(Day);
                    Add(t, "social", new[] { (a, "initiator"), (c, "recipient") }, 0.4, "Pawn" + Inv(a) + " and Pawn" + Inv(c) + " fell into talk", null, new List<int> { a, c });
                }
                for (int k = 0; k < 8; k++)
                {
                    int p = r.Choice(ids); long t = b + r.RandRange(Day);
                    double sev = r.Uniform(0.3, 0.7);
                    Add(t, "injury", new[] { (p, "victim") }, sev, "Pawn" + Inv(p) + " was hurt");
                    long t2 = t + r.RandRange(200, 1500); int doc = r.Choice(ids);
                    Add(t2, "tend", new[] { (doc, "doctor"), (p, "patient") }, 0.3, "Pawn" + Inv(p) + " was tended");
                }
                Add(b + 15000, "mental_break", new[] { (2, "subject") }, 0.8, "Pawn2 snapped");
                if (d == 0)
                {
                    Add(b + raidAt, "raid_start", new[] { (1, "subject") }, 0.95, "Raiders attack the colony", null, new List<int>(ids));
                    var kinds = new List<string> { "injury", "injury", "thought", "tend" };
                    for (int k = 0; k < raidSize - 1; k++)
                    {
                        int p = r.Choice(ids);
                        long t = b + raidAt + r.RandRange(1, 1080); string typ = r.Choice(kinds); double sev = r.Uniform(0.3, 0.8);
                        Add(t, typ, new[] { (p, "victim") }, sev, "Pawn" + Inv(p) + " in the fight");
                    }
                }
            }
            return ev.OrderBy(e => e.Tick).ToList();      // stable, as Python's sorted
        }

        // ---- running ----------------------------------------------------------------------------------------------
        /// <summary>Game speed over real time: (real_s, speed, paused), the first entry at 0.</summary>
        public sealed class Schedule
        {
            public List<(double Real, int Speed, bool Paused)> Steps;
            public Schedule(IEnumerable<(double, int, bool)> steps) { Steps = steps.ToList(); }
            public static Schedule Fixed(int speed) => new Schedule(new[] { (0.0, speed, false) });
            public (int Speed, bool Paused) At(double t)
            {
                var cur = Steps[0];
                foreach (var s in Steps) if (s.Real <= t) cur = s;
                return (cur.Speed, cur.Paused);
            }
        }

        public sealed class Thought { public double Real; public int Tick; public int? Pawn; public double Tps; }

        public sealed class Say { public double Real; public int Tick; public JObject Body; public bool Ok; public int Status; public string Attach; }

        /// <summary>The mod's pending table for the game's own interactions: a line attaches within the game-time window OR the real-time floor.</summary>
        public sealed class AttachModel
        {
            public int WindowTicks = 417; public double FloorS = 5.0;
            public Dictionary<string, (int Tick, double Real)> Reg = new Dictionary<string, (int, double)>();
            public Dictionary<string, string> Results = new Dictionary<string, string>();
            public void Register(string rf, int tick, double real) { Reg[rf] = (tick, real); }
            public string TryAttach(string rf, int tick, double real)
            {
                if (!Reg.ContainsKey(rf) || Results.ContainsKey(rf)) return "unknown";
                var (t0, r0) = Reg[rf];
                bool ok = tick - t0 <= WindowTicks || (FloorS > 0 && real - r0 <= FloorS);
                return Results[rf] = ok ? "attached" : "expired";
            }
        }

        /// <summary>Frame-bound request latency on the mock bridge; records every /say with real and game time.</summary>
        public sealed class SimTransport : IBridgeTransport
        {
            readonly FakeBridge _fb; readonly PyRandom _rng; readonly AttachModel _attach; readonly VirtualTime _vt;
            public readonly List<Say> Says = new List<Say>();
            public readonly List<Thought> Thoughts = new List<Thought>();
            public readonly SortedDictionary<string, int> Counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public SimTransport(FakeBridge fb, PyRandom rng, AttachModel attach, VirtualTime vt) { _fb = fb; _rng = rng; _attach = attach; _vt = vt; }

            public async Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
            {
                _vt.Check("transport");
                double wait; lock (_fb) wait = _rng.Uniform(0.008, 0.03);
                double t0 = _vt.Now;
                await _vt.Delay(wait, ct);
                bool isSay = request.Method == "POST" && request.Path == "/say";
                lock (_fb) { string ck = request.Method + " " + (request.Path.Count(c => c == '/') > 1 ? request.Path.Substring(0, request.Path.LastIndexOf('/')) : request.Path); Counts[ck] = (Counts.TryGetValue(ck, out int cn) ? cn : 0) + 1; }
                BridgeReply resp;
                lock (_fb) resp = _fb.SendAsync(request, ct).GetAwaiter().GetResult();     // the mock is single-threaded state: one request at a time (it never suspends)
                if (request.Method == "POST" && request.Path == "/thought" && resp.Status == 200)
                {
                    var body = (JObject)BridgeJson.Parse(request.JsonBody);
                    string kind = body["kind"] == null || body["kind"].Type == JTokenType.Null ? "thought" : (string)body["kind"];
                    if (kind == "thought")
                        lock (_fb) Thoughts.Add(new Thought { Real = _vt.Now, Tick = _fb.Tick, Pawn = body["pawn"] == null || body["pawn"].Type == JTokenType.Null ? (int?)null : (int)body["pawn"], Tps = TicksPerS[_fb.Speed] });
                }
                if (isSay)
                {
                    var body = (JObject)BridgeJson.Parse(request.JsonBody);
                    bool ok = resp.Status == 200;
                    var rec = new Say { Real = _vt.Now, Tick = _fb.Tick, Body = body, Ok = ok, Status = resp.Status };
                    var vr = body["vanilla_ref"];
                    if (ok && vr != null && vr.Type != JTokenType.Null && ((string)vr).Length > 0) rec.Attach = _attach.TryAttach((string)vr, _fb.Tick, _vt.Now);
                    lock (_fb) Says.Add(rec);
                }
                return resp;
            }
        }

        /// <summary>The mod's ThoughtPacing as a model: a cloud shows when this pawn's and the global gap have passed. realGaps false = the old
        /// rule (gaps in game ticks); true = the gaps are real seconds at the current speed.</summary>
        public static int CloudsShown(IEnumerable<Thought> thoughts, bool realGaps, int perPawnS = 120, int globalS = 20)
        {
            var last = new Dictionary<long, int>();
            long lastG = -1000000000000L;
            int n = 0;
            foreach (var t in thoughts)
            {
                double scale = realGaps ? Math.Max(60.0, t.Tps) : 60.0;
                double pp = perPawnS * scale, gg = globalS * scale;
                long key = t.Pawn ?? long.MinValue;
                if ((last.ContainsKey(key) && t.Tick - last[key] < pp) || t.Tick - lastG < gg) continue;
                last[key] = t.Tick; lastG = t.Tick;
                n++;
            }
            return n;
        }

        public sealed class Metrics
        {
            public string Speed; public double RealS; public int GameTicks;
            public int Produced, Ingested, Lines;
            public double LinesPerGameHour, LinesPerRealMin;
            public int DroppedLate; public double LateShare, DelayGameMedian, DelayGameP90, DelayGameMax, DelayRealP90;
            public int ConvsDone, ConvsCut, RingGaps, EventsLost, QueueMax;
            public double QueueMean, SlotUtil;
            public Dictionary<string, int> Idle = new Dictionary<string, int>();
            public Dictionary<string, List<int>> IdleDays = new Dictionary<string, List<int>>();
            public int EffectsForDropped, MemoriesMissed, MemoriesExpected, VanillaHigh, VanillaHighAttached;
            public double AttachShare;
            public Dictionary<string, int> DropReasons = new Dictionary<string, int>();
            public int SayRefused, QueueExpired, Shed;
            public Dictionary<string, int> LeaseLog = new Dictionary<string, int>();
            public Dictionary<string, int> SaidByKind = new Dictionary<string, int>();
            public int Thoughts, CloudsOld, Clouds;
            public double CloudsPerRealMin;
            public string Crashed = "";
            public Dictionary<string, int> Requests = new Dictionary<string, int>();
        }

        static double Pct(List<double> xs, double p)
        {
            if (xs.Count == 0) return 0.0;
            var s = xs.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        }
        static void Bump(Dictionary<string, int> d, string k) { d[k] = (d.TryGetValue(k, out int v) ? v : 0) + 1; }

        static async Task<Metrics> SimulateAsync(VirtualTime vt, List<SimEvent> events, Schedule schedule, MindConfig cfg, int seed, int pawns,
                                                 int totalTicks, double attachFloorS, string label, int windowTicks = 417, double maxRealS = 4000.0)
        {
            var rng = new PyRandom(seed);
            var fb = new FakeBridge();
            fb.Tick = 0;
            for (int i = 1; i <= pawns; i++) fb.AddPawn(i, "Pawn" + Inv(i));
            var attach = new AttachModel { WindowTicks = windowTicks, FloorS = attachFloorS };
            var tr = new SimTransport(fb, rng, attach, vt);
            var adapter = new RimWorldAdapter();
            var bridge = new BridgeClient(tr, RimWorldAdapter.WireNames, fb.Token);
            var llm = new SimLlm(new PyRandom(seed + 1), vt);
            string tmp = Path.Combine(Path.GetTempPath(), "rimworldllm-speedsim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var m = new Metrics { Speed = label };
            var delaysGame = new List<double>(); var delaysReal = new List<double>();
            var evReal = new Dictionary<int, double>();
            var queueSamples = new List<int>();
            var convs = new List<string>();
            var ingested = new HashSet<int>();
            int memExpected = 0, memMissing = 0;
            var produced = new List<JObject>();
            Director d;
            double real;
            try
            {
                cfg.DataDir = tmp;
                var poller = new EventPoller(bridge, 0, adapter.NormalizeEvent);
                d = new Director(cfg, bridge, llm, adapter, poller, () => vt.Now, new PyRandom(seed + 2), false, vt, (s, ct) => vt.Delay(s, ct));
                // ---- probes (the director is not modified beyond these hooks)
                d.Probes.Say = (aid, e) =>
                {
                    if (e == null) return;
                    lock (fb) delaysGame.Add(fb.Tick - (e["game"] != null && e["game"].Type != JTokenType.Null ? (int)e["game"].Value<double>() : fb.Tick));
                    int sq = e["seq"] != null && e["seq"].Type != JTokenType.Null ? (int)e["seq"].Value<double>() : -1;
                    lock (fb) delaysReal.Add(vt.Now - (evReal.TryGetValue(sq, out double r0) ? r0 : vt.Now));
                };
                d.Probes.Finish = conv => { lock (fb) convs.Add(conv.Ended); };
                d.Probes.Ingested = evs =>
                {
                  lock (fb) {
                    foreach (var e in evs) ingested.Add((int)e["seq"].Value<double>());
                    foreach (var e in evs)                  // right after ingest: later upkeep may forget by design
                        foreach (var w in (e["witnesses"] as JArray) ?? new JArray())
                            if (d.Minds.TryGetValue((int)w.Value<double>(), out var mind))
                            {
                                memExpected++;
                                int sq = (int)e["seq"].Value<double>();
                                if (!mind.Memories.Any(mm => mm.EventSeq == sq)) memMissing++;
                            }
                  }
                };
                await d.StartAsync();
                var pending = new Queue<SimEvent>(events);
                double t0 = vt.Now;
                bool stop = false;
                var agentCt = new CancellationTokenSource();
                Task agent = Task.Factory.StartNew(async () =>
                {
                    while (!stop)
                    {
                        double st0 = vt.Now;
                        try { await d.StepAsync(); if (Environment.GetEnvironmentVariable("SPEEDSIM_TRACE") != null) Console.Error.WriteLine("step " + st0.ToString("F2") + " took " + (vt.Now - st0).ToString("F3") + " poll " + d.PollDelay().ToString("F3") + " rate " + d.Now().Rate); }
                        catch (Exception e) { m.Crashed = PyNames.Of(e) + "(" + e.Message + ")" + (Environment.GetEnvironmentVariable("SPEEDSIM_TRACE") != null ? "\n" + e.ToString() : ""); stop = true; return; }
                        queueSamples.Add(d.Leases.Queue.Count);
                        try { await vt.Delay(d.PollDelay(), agentCt.Token); } catch (OperationCanceledException) { return; }
                    }
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, vt).Unwrap();
                const double dt = 0.05;
                while (fb.Tick < totalTicks && !stop && vt.Now - t0 < maxRealS)
                {
                    var (speed, paused) = schedule.At(vt.Now - t0);
                    lock (fb)
                    {
                        fb.Speed = speed; fb.Paused = paused;
                        if (!paused) fb.Tick += (int)(TicksPerS[speed] * dt);
                        while (pending.Count > 0 && pending.Peek().Tick <= fb.Tick)
                        {
                            var e = pending.Dequeue();
                            var ev = fb.AddEvent(e.Type, e.Actors, e.Summary, e.Witnesses, e.Severity, null, (JObject)e.Data.DeepClone());
                            ev["tick"] = e.Tick;
                            evReal[(int)ev["seq"].Value<double>()] = vt.Now;
                            produced.Add(ev);
                            if (e.Data["vanilla"] != null) attach.Register((string)e.Data["ref"], e.Tick, vt.Now);
                        }
                    }
                    await vt.Delay(dt);
                }
                for (int k = 0; k < 60; k++)                    // let the tail drain
                {
                    await vt.Delay(1.0);
                    if (d.TaskCount == 0 && d.Leases.Queue.Count == 0 && !d.Idle.Active()) break;
                }
                stop = true;
                await vt.Delay(2.0);
                await d.DrainAsync();
                agentCt.Cancel();
                real = vt.Now - t0;
                llm.Tick();
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }

            // ---- numbers
            m.RealS = real; m.GameTicks = fb.Tick;
            m.Produced = produced.Count; m.Ingested = ingested.Count;
            var okSays = tr.Says.Where(s => s.Ok).ToList();
            m.Lines = okSays.Count;
            m.LinesPerGameHour = m.Lines / Math.Max(1e-9, fb.Tick / (Day / 24.0));
            m.LinesPerRealMin = m.Lines / Math.Max(1e-9, real / 60);
            var late = d.Dropped.Where(x => x.Reason.Contains("expired")).ToList();
            int qExpired = d.Leases.Log.Count(x => x.Item1 == "expired");
            foreach (var (w, why) in d.Leases.Log)
                if (w == "drop" || w == "merged" || w == "expired")
                    Bump(m.LeaseLog, w != "expired" ? w + ":" + why.Split(':')[0] : "expired");
            foreach (var s in okSays)
            {
                var vr = s.Body["vanilla_ref"]; var cid = s.Body["conversation_id"];
                bool v = vr != null && vr.Type != JTokenType.Null && ((string)vr).Length > 0;
                bool c = cid != null && cid.Type != JTokenType.Null && ((string)cid).Length > 0;
                Bump(m.SaidByKind, v ? "vanilla" : c ? "conv" : "line");
            }
            m.Requests = new Dictionary<string, int>(tr.Counts);
            m.QueueExpired = qExpired;
            m.Thoughts = tr.Thoughts.Count;
            m.CloudsOld = CloudsShown(tr.Thoughts, false);
            m.Clouds = CloudsShown(tr.Thoughts, true);
            m.CloudsPerRealMin = m.Clouds / Math.Max(1e-9, real / 60);
            m.DroppedLate = late.Count;
            m.Shed = qExpired;
            m.LateShare = m.DroppedLate / (double)Math.Max(1, m.DroppedLate + m.Lines);
            m.DelayGameMedian = Pct(delaysGame, .5); m.DelayGameP90 = Pct(delaysGame, .9); m.DelayGameMax = delaysGame.Count == 0 ? 0 : delaysGame.Max();
            m.DelayRealP90 = Pct(delaysReal, .9);
            string[] done = { "chance", "turn_cap", "repetition" };
            m.ConvsDone = convs.Count(r => done.Contains(r)); m.ConvsCut = convs.Count(r => !done.Contains(r));
            m.RingGaps = ((EventPoller)d.Events).Gaps;
            m.EventsLost = produced.Select(e => (int)e["seq"].Value<double>()).Except(ingested).Count();
            m.QueueMax = queueSamples.Count == 0 ? 0 : queueSamples.Max();
            m.QueueMean = queueSamples.Count == 0 ? 0 : queueSamples.Sum() / (double)Math.Max(1, queueSamples.Count);
            m.SlotUtil = llm.BusyS / Math.Max(1e-9, real * llm.Slots);
            var days = new Dictionary<string, SortedSet<int>>();
            foreach (var i in d.Idle.Log)
            {
                string kind = (string)i["kind"];
                Bump(m.Idle, kind);
                int game = i["game"] != null && i["game"].Type != JTokenType.Null ? (int)i["game"].Value<double>() : 0;
                int day = game / Day;
                if (!days.ContainsKey(kind)) days[kind] = new SortedSet<int>();
                days[kind].Add(kind != "diary" ? day : (i["day"] != null ? (int)i["day"].Value<double>() : day));
            }
            m.IdleDays = days.ToDictionary(k => k.Key, k => k.Value.ToList());
            var said = new HashSet<(int, string)>(d.Said.Select(s => ((int)s["actor"], (string)s["text"])));
            m.EffectsForDropped = okSays.Count(s =>
            {
                var it = s.Body["intent"]; string intent = it == null || it.Type == JTokenType.Null ? "none" : (string)it;
                return intent != "none" && !said.Contains(((int)s.Body["pawn"], (string)s.Body["text"]));
            });
            m.MemoriesExpected = memExpected; m.MemoriesMissed = memMissing;
            var hi = events.Where(e => e.Data["vanilla"] != null && (string)e.Data["tier"] == "high").ToList();
            m.VanillaHigh = hi.Count;
            m.VanillaHighAttached = hi.Count(e => attach.Results.TryGetValue((string)e.Data["ref"], out var r) && r == "attached");
            m.AttachShare = m.VanillaHighAttached / (double)Math.Max(1, m.VanillaHigh);
            foreach (var (_, r) in d.Dropped) Bump(m.DropReasons, r.StartsWith("idle_preempted", StringComparison.Ordinal) ? "idle_preempted" : r.Split(':')[0]);
            m.SayRefused = tr.Says.Count - okSays.Count;
            return m;
        }

        /// <summary>simulate(): one run on a fresh virtual clock. speed 1-4, or a Schedule.</summary>
        public static Metrics Simulate(int speed = 1, List<SimEvent> events = null, MindConfig cfg = null, int seed = 3, int pawns = 4, double days = 1.5,
                                       double attachFloorS = 5.0, string label = "") => Simulate(Schedule.Fixed(speed), events, cfg, seed, pawns, days, attachFloorS, label == "" ? "x" + Inv(speed) : label);

        public static Metrics Simulate(Schedule sched, List<SimEvent> events = null, MindConfig cfg = null, int seed = 3, int pawns = 4, double days = 1.5,
                                       double attachFloorS = 5.0, string label = "sched")
        {
            var evs = events ?? ColonyDay(seed, pawns);
            var vt = new VirtualTime();
            return vt.Run(() => SimulateAsync(vt, evs, sched, cfg ?? new MindConfig(), seed, pawns, (int)(days * Day), attachFloorS, label));
        }

        static readonly (Func<Metrics, double> Get, bool Int, string Head)[] Columns =
        {
            (m => m.Lines, true, "lines"), (m => m.LinesPerGameHour, false, "ln/ghr"), (m => m.LinesPerRealMin, false, "ln/min"), (m => m.LateShare, false, "late%"),
            (m => m.Shed, true, "shed"), (m => m.DelayGameMedian, false, "dly50"), (m => m.DelayGameP90, false, "dly90"), (m => m.ConvsDone, true, "conv ok"),
            (m => m.ConvsCut, true, "cut"), (m => m.EventsLost, true, "lost"), (m => m.RingGaps, true, "gaps"), (m => m.QueueMax, true, "qmax"),
            (m => m.SlotUtil, false, "util"), (m => m.MemoriesMissed, true, "memmiss"), (m => m.AttachShare, false, "attach"), (m => m.EffectsForDropped, true, "fx-drop"),
            (m => m.CloudsOld, true, "cl-old"), (m => m.Clouds, true, "clouds"), (m => m.CloudsPerRealMin, false, "cl/min"),
        };

        public static string Table(IEnumerable<Metrics> ms)
        {
            var rows = new List<string> { "speed  real_s " + string.Join(" ", Columns.Select(c => c.Head.PadLeft(8))) + "  idle(refl/diary/chron)" };
            foreach (var m in ms)
            {
                var cells = Columns.Select(c => c.Int ? ((long)c.Get(m)).ToString(CultureInfo.InvariantCulture).PadLeft(8) : c.Get(m).ToString("F2", CultureInfo.InvariantCulture).PadLeft(8));
                int I(string k) => m.Idle.TryGetValue(k, out int v) ? v : 0;
                rows.Add(m.Speed.PadRight(6) + " " + m.RealS.ToString("F0", CultureInfo.InvariantCulture).PadLeft(6) + " " + string.Join(" ", cells) +
                         "  " + I("reflection") + "/" + I("diary") + "/" + I("chronicle") + (m.Crashed.Length > 0 ? "  CRASH " + m.Crashed : ""));
            }
            return string.Join("\n", rows);
        }

        /// <summary>The harness command: speedsim [--floor S] [--speed N | --all] [--seed N].</summary>
        public static int Run(string[] argv, TextWriter stdout, TextWriter stderr)
        {
            double floor = 5.0; int seed = 3; int? speed = null;
            for (int i = 0; i < argv.Length; i++)
            {
                string Next() { if (i + 1 >= argv.Length) throw new ArgumentException("expected a value after " + argv[i]); return argv[++i]; }
                try
                {
                    switch (argv[i])
                    {
                        case "--floor": floor = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--speed": speed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--all": speed = null; break;
                        default: stderr.WriteLine("speedsim: unrecognized argument " + argv[i]); return 2;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException) { stderr.WriteLine("speedsim: " + e.Message); return 2; }
            }
            if (speed != null && !TicksPerS.ContainsKey(speed.Value)) { stderr.WriteLine("speedsim: --speed must be 1-4"); return 2; }
            var ms = (speed != null ? new[] { speed.Value } : new[] { 1, 2, 3, 4 }).Select(s => Simulate(s, seed: seed, attachFloorS: floor)).ToList();
            stdout.WriteLine(Table(ms));
            foreach (var m in ms)
            {
                if (Environment.GetEnvironmentVariable("SPEEDSIM_TRACE") != null) stdout.WriteLine(m.Speed + " requests " + m.Requests.Values.Sum() + ": " + string.Join(", ", m.Requests.OrderByDescending(k => k.Value).Take(12).Select(k => k.Key + "=" + k.Value)));
                string D<T>(Dictionary<string, T> d, Func<T, string> f) => "{" + string.Join(", ", d.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => "'" + k.Key + "': " + f(k.Value))) + "}";
                stdout.WriteLine(m.Speed + " drops: " + D(m.DropReasons, v => v.ToString(CultureInfo.InvariantCulture)) + " lease: " + D(m.LeaseLog, v => v.ToString(CultureInfo.InvariantCulture)) +
                                 " said: " + D(m.SaidByKind, v => v.ToString(CultureInfo.InvariantCulture)) + " idle days: " + D(m.IdleDays, v => "[" + string.Join(", ", v) + "]"));
            }
            return 0;
        }
    }
}

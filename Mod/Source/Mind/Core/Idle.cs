// Port of agent/rimworldllm_agent/core/idle.py: idle-time work (reflection, chronicle, art, dreams, diaries, thoughts, wants, text fill).
//
// Rules (SPEC 5, 7): only when no window is leased for real work and nothing is queued; one idle job at a time; the moment actor work is
// queued or leased the running job is cancelled (its LLM call is dropped, nothing applied) and its window released.
//
// Cancellation: Python cancels the asyncio task, which raises CancelledError at the job's current await. Here every await inside a job goes
// through W(...) (Task.WaitAsync with the job's token), so the job sees an OperationCanceledException at the same points; the underlying
// call (an LLM request, which has no token in ILlmClient) keeps running detached and its result is discarded.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>
    /// The director members idle.py reads. It inherits the host interfaces of the modules idle passes the director to (art, dreams, lore,
    /// wants, text fill). Members that several of those declare (Bridge, Llm, Cfg, Adapter, Names, Minds, NameTokens, Chronicle, Dirty, Dropped,
    /// Day, BridgeCfg, MinorTokens, IsMinor) are NOT redeclared here: the IdleRunner reaches each through one named sub-interface
    /// (a plain `d.Bridge` on this interface would be ambiguous). Only what no inherited interface gives is declared below.
    /// </summary>
    public interface IIdleHost : IArtHost, IDreamHost, ILoreHost, IWantsHost, ITextFillHost
    {
        /// <summary>d.ledger: the open events (the chronicle's ledger lines).</summary>
        Ledger Ledger { get; }
        /// <summary>d.weights: talk weight per actor (default 1.0).</summary>
        IDictionary<int, double> Weights { get; }
        /// <summary>d.wants_last: game time of the last wants decision per actor.</summary>
        IDictionary<int, int> WantsLast { get; }
        /// <summary>d.reputations: how the others see each actor (actor id -> line).</summary>
        IDictionary<int, string> Reputations { get; }
        /// <summary>d.tool_backend: null while the model cannot run tools (wants stay off).</summary>
        IBackend ToolBackend { get; }
        /// <summary>d.tool_mode: auto | grammar | json | native.</summary>
        string ToolMode { get; }
        /// <summary>d.clock(): monotonic seconds (the director clock).</summary>
        double Clock();
        /// <summary>d.now(): the play clock (Game and Real).</summary>
        Now Now();
        /// <summary>d._spawn(coro): run the work as a director-tracked background task.</summary>
        Task Spawn(Func<Task> work);
        /// <summary>d.refresh_reputations(): recompute Reputations from the minds.</summary>
        void RefreshReputations();
    }

    public sealed class IdleRunner : IIdleYield, IStewardIdle
    {
        public const int MaxDiaryMemories = 10;
        /// <summary>The text filler holds an ordinary actor window (Cls.Idle) under a pseudo actor id: real minds are >= 0, -1 is the narrator.</summary>
        public const int TextFillActor = -2;
        public const double TextFillGapS = 10.0;

        public readonly IIdleHost D;
        public Task Task;
        public Lease Lease;
        public string Kind { get; set; } = "";
        public readonly Dictionary<(string, int), double> FailUntil = new Dictionary<(string, int), double>();
        public int? DueSince;
        /// <summary>per actor: real time of the next map thought</summary>
        public readonly Dictionary<int, double> ThoughtNext = new Dictionary<int, double>();
        /// <summary>memory ids the last thought drew on</summary>
        public readonly Dictionary<int, string[]> ThoughtLast = new Dictionary<int, string[]>();
        /// <summary>per actor: the last ThoughtKeep thought texts, oldest first (in memory; seeded from the mind's stored musings)</summary>
        public readonly Dictionary<int, List<string>> ThoughtHistory = new Dictionary<int, List<string>>();
        public const int ThoughtKeep = 10;
        public const int DiaryOpeningsKept = 3;
        /// <summary>director clock: earliest next text-fill call</summary>
        public double TextNext = 0.0;
        /// <summary>what idle work produced (reflections, diaries, chronicle entries)</summary>
        public readonly List<JObject> Log = new List<JObject>();

        private CancellationTokenSource _cts;

        // One named view per sub-interface (see IIdleHost).
        private IArtHost A { get { return D; } }
        private IDreamHost Dr { get { return D; } }
        private ILoreHost Lo { get { return D; } }
        private IWantsHost Wa { get { return D; } }
        private ITextFillHost Tf { get { return D; } }
        private IBridge Bridge { get { return A.Bridge; } }
        private MindConfig Cfg { get { return A.Cfg; } }
        private IGameAdapter Adapter { get { return A.Adapter; } }
        private int Day { get { return A.Day; } }
        private IDictionary<int, string> Names { get { return A.Names; } }
        private IDictionary<int, HashSet<string>> NameTokens { get { return A.NameTokens; } }
        private IDictionary<int, Mind> Minds { get { return Wa.Minds; } }
        private List<(int Actor, string Reason)> Dropped { get { return Tf.Dropped; } }
        private ISet<int> Dirty { get { return Dr.Dirty; } }
        private LeaseManager Leases { get { return Lo.Leases; } }

        public IdleRunner(IIdleHost d) { D = d; }

        // ---- state ------------------------------------------------------------
        public bool Active() { return Task != null && !Task.IsCompleted; }

        public bool ActorsWantWindows() { return ActorsWantWindows(false); }

        /// <summary>Real work is queued or leased. Starving idle work only yields to reactive and urgent work (SPEC 7).</summary>
        public bool ActorsWantWindows(bool starving)
        {
            var ls = Leases;
            Cls floor = starving ? Cls.Reactive : Cls.Ambient;
            return ls.Queue.Any(q => q.Cls >= floor) || ls.Leases.Values.Any(l => l.Cls > Cls.Idle && l.Cls >= floor);
        }

        public bool AnythingDue()
        {
            // A pending art description counts: it expires after one day of the clock, so without this a busy world would let it
            // lapse unwritten. Starving only lets idle work run beside ambient talk; reactive and urgent still go first.
            return DueReflection().Count > 0 || DueDiary().Count > 0 ||
                   (Art.Enabled(A) && !Tf.TextQueueLive && Ok("art", -1) && A.Art.Pending(Bridge.Env.Game, Day)) ||
                   (Cfg.ChronicleEnabled && A.Chronicle.Due((int)FloorDiv(Bridge.Env.Game, Day)) != null && Ok("chronicle", -1));
        }

        /// <summary>Idle work has been due for a share of a game day without running: it may now run beside ambient chatter.
        /// A floor per game day, whatever the speed: at 3x a busy game would otherwise never be idle.</summary>
        public bool Starving()
        {
            if (!AnythingDue()) { DueSince = null; return false; }
            int game = Bridge.Env.Game;
            if (DueSince == null || game < DueSince.Value) DueSince = game;
            return game - DueSince.Value >= Cfg.IdleStarvingFraction * Day;
        }

        /// <summary>Cancel the running idle job at once. Nothing it was producing is applied.</summary>
        public bool Preempt(string reason = "window_wanted")
        {
            if (!Active()) return false;
            var lease = Lease;                                    // the job's own continuation can clear it from another thread
            if (lease != null)
            {
                lease.Preempted = true;
                Leases.Release(lease, "preempted");
                Dropped.Add((lease.ActorId, "idle_preempted:" + Kind));
            }
            else Dropped.Add((-1, "idle_preempted:" + Kind));
            Leases.NarratorBusy = Kind == "chronicle" ? false : Leases.NarratorBusy;
            if (_cts != null) _cts.Cancel();
            Task = null; Lease = null;
            return true;
        }

        void IStewardIdle.Preempt(string who) { Preempt(who); }

        public void PreemptIfWanted()
        {
            if (ActorsWantWindows(Starving())) Preempt();
        }

        // ---- picking ----------------------------------------------------------
        public bool Ok(string kind, int aid)
        {
            double until;
            return (FailUntil.TryGetValue((kind, aid), out until) ? until : -1.0) <= D.Now().Real;
        }

        public void Fail(string kind, int aid)
        {
            FailUntil[(kind, aid)] = D.Now().Real + Cfg.IdleRetryRealS;
        }

        public List<int> DueReflection()
        {
            int now = Bridge.Env.Game;
            var c = Cfg;
            var due = new List<(double Imp, int Aid)>();
            foreach (var kv in Minds)
            {
                var m = kv.Value;
                if (Reflection.ReflectionDue(m, now, Day, c.ReflectionImportance, c.ReflectionEveryDays) && Ok("reflect", kv.Key))
                    due.Add((m.UnreflectedImportance, kv.Key));
            }
            return due.OrderByDescending(t => t.Imp).ThenByDescending(t => t.Aid).Select(t => t.Aid).ToList();
        }

        public int? DiaryTarget(Mind mind)
        {
            int now = Bridge.Env.Game;
            int day = Day;
            int today = (int)FloorDiv(now, day);
            double frac = (double)PyMod(now, day) / day;
            int target = frac >= Cfg.DiaryDayFraction ? today : today - 1;
            if (target < 0 || mind.LastDiaryDay >= target) return null;
            if (!mind.Memories.Any(m => FloorDiv(m.Game, day) == target)) return null;
            return target;
        }

        public List<(int Aid, int Target)> DueDiary()
        {
            var o = new List<(int, int)>();
            if (!Cfg.DiaryEnabled) return o;
            foreach (var kv in Minds.OrderBy(k => k.Key))
            {
                int? t = DiaryTarget(kv.Value);
                if (t != null && Ok("diary", kv.Key)) o.Add((kv.Key, t.Value));
            }
            return o;
        }

        /// <summary>What a thought may draw on: heaviest recent public memories (a private one never reaches the map).</summary>
        public List<Memory> ThoughtMemories(Mind mind)
        {
            int now = Bridge.Env.Game;
            long floor = now - (long)(Cfg.ThoughtMemoryWindowDays * Day);          // int(): truncation toward zero
            return MemoryOps.WeightSorted(mind, now, Day).Where(m => m.Game >= floor && m.Visibility == "public" && !(Cfg.SpeechChecks && MemoryOps.IsRoutineSocial(m, Adapter))).Take(3).ToList();
        }

        /// <summary>The mind's recent thoughts: the in-memory list plus its stored musings, so a restart does not forget them.</summary>
        public List<string> RecentThoughts(int aid, Mind mind)
        {
            var o = new List<string>();
            if (mind != null) o.AddRange(mind.Inner.Where(t => t.Kind == "musing").Select(t => t.Text));
            List<string> mem;
            if (ThoughtHistory.TryGetValue(aid, out mem)) o.AddRange(mem);
            return o;
        }

        void RememberThought(int aid, string text)
        {
            List<string> l;
            if (!ThoughtHistory.TryGetValue(aid, out l)) { l = new List<string>(); ThoughtHistory[aid] = l; }
            l.Add(text);
            if (l.Count > ThoughtKeep) l.RemoveRange(0, l.Count - ThoughtKeep);
        }

        public List<(int Aid, List<Memory> Mems)> DueThought()
        {
            var o = new List<(int, List<Memory>)>();
            if (!Cfg.ThoughtEnabled) return o;
            foreach (var kv in Minds.OrderBy(k => k.Key))
            {
                int aid = kv.Key;
                double tn;
                if ((ThoughtNext.TryGetValue(aid, out tn) ? tn : -1.0) > D.Clock() || !Ok("thought", aid)) continue;
                var mems = ThoughtMemories(kv.Value);
                string[] last;
                ThoughtLast.TryGetValue(aid, out last);
                if (mems.Count > 0 && (last == null || !mems.Select(x => x.Id).SequenceEqual(last))) o.Add((aid, mems));
            }
            return o;
        }

        /// <summary>Called every step after actor work was launched.</summary>
        public void RunOnce()
        {
            bool starving = Starving();
            if (ActorsWantWindows(starving)) { Preempt(); return; }
            if (Active() || !Leases.NarratorMayDoIdleWork(starving)) return;
            foreach (int aid in DueReflection())
                if (StartActor("reflect", aid, (l, t) => ReflectAsync(aid, l, t))) return;
            if (Cfg.ChronicleEnabled && A.Chronicle.Due((int)FloorDiv(Bridge.Env.Game, Day)) != null && Ok("chronicle", -1))
            {
                Kind = "chronicle";
                Leases.NarratorBusy = true;
                StartNarrator(ChronicleAsync);
                return;
            }
            if (StartArt()) return;
            if (Dreams.Enabled(Dr))
                foreach (var job in Dr.DreamBook.Due(Dr))
                {
                    var j = job;
                    if (StartActor("dream", j.Actor, (l, t) => DreamAsync(j, l, t))) return;
                }
            foreach (var (aid, target) in DueDiary())
            {
                int a = aid, tg = target;
                if (StartActor("diary", a, (l, t) => DiaryAsync(a, tg, l, t))) return;
            }
            foreach (var (aid, mems) in DueThought())
            {
                int a = aid; var ms = mems;
                if (StartActor("thought", a, (l, t) => ThoughtAsync(a, ms, l, t))) return;
            }
            foreach (int aid in DueWants())
            {
                int a = aid;
                if (StartActor("wants", a, (l, t) => WantsAsync(a, l, t))) return;
            }
            StartTextFill();                       // the lowest job: texts for single things
        }

        /// <summary>The adapter's text filler, or null while it is off: no adapter hook, cfg.TextFill false, or the game's text_fill setting false.</summary>
        public Func<ITextFillHost, int, Task<int>> TextFillFn()
        {
            if (!Cfg.TextFill || JobsJson.IsFalse(JobsJson.Get(A.BridgeCfg, "text_fill"))) return null;
            return Adapter.IdleFill();
        }

        public bool StartTextFill()
        {
            var fn = TextFillFn();
            if (fn == null || !Ok("textfill", TextFillActor) || D.Clock() < TextNext) return false;
            // a call every ~10 s of the director clock at most; the filler lengthens the wait (404, empty queue)
            TextNext = D.Clock() + TextFillGapS;
            return StartActor("textfill", TextFillActor, (l, t) => TextFillAsync(fn, l, t));
        }

        private async Task TextFillAsync(Func<ITextFillHost, int, Task<int>> fn, Lease lease, CancellationToken tok)
        {
            Tf.TextFillWaitS = 0.0;
            lease.Generating = true;
            try
            {
                int n = await W(fn(D, lease.Slot), tok).ConfigureAwait(false);
                TextNext = Math.Max(TextNext, D.Clock() + Tf.TextFillWaitS);
                if (n != 0) Log.Add(new JObject { ["kind"] = "textfill", ["count"] = n, ["game"] = Bridge.Env.Game });
            }
            catch (LlmError)
            {
                Fail("textfill", TextFillActor);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                LlmLog.Warning("rimworldllm", "text fill failed: " + e);
                Fail("textfill", TextFillActor);
            }
        }

        /// <summary>Wants with follow-through (core/wants.py), paced by the existing ambient pacing: off while no tool backend is set, off while
        /// ambient talk is paused (speed), at least the ambient per-actor game gap between decisions, and thinned by the talk-frequency setting.</summary>
        public List<int> DueWants()
        {
            var o = new List<int>();
            var bc = A.BridgeCfg;
            if (D.ToolBackend == null || !Cfg.Wants || JobsJson.IsFalse(JobsJson.Get(bc, "wants_enabled")) || Leases.AmbientPaused(Bridge.Env.Speed))
                return o;
            double freq = PyFloatOf(JobsJson.Get(bc, "talk_frequency"), 1.0);
            JToken eff = JobsJson.Get(bc, "effects");
            if (freq <= 0 || (eff != null && eff.Type == JTokenType.String && (string)eff == "off")) return o;
            int now = Bridge.Env.Game;
            int gap = Cfg.Quotas.MinGapGameAmbient;
            foreach (int aid in Minds.Keys.OrderBy(k => k))
            {
                int last;
                long since = now - (D.WantsLast.TryGetValue(aid, out last) ? (long)last : -1000000000000L);
                if (since < gap || !Ok("wants", aid)) continue;
                double roll = WantsRoll(aid, now, gap);
                double w;
                if (roll < Math.Min(1.0, (D.Weights.TryGetValue(aid, out w) ? w : 1.0) * freq * 0.5)) o.Add(aid);
            }
            return o;
        }

        /// <summary>int(sha1(f"wants:{aid}:{now // max(gap, 1)}").hexdigest(), 16) % 1000 / 1000</summary>
        internal static double WantsRoll(int aid, long now, long gap)
        {
            return Inner.Sha1Mod("wants:" + aid.ToString(CultureInfo.InvariantCulture) + ":" +
                                 FloorDiv(now, Math.Max(gap, 1)).ToString(CultureInfo.InvariantCulture), 1000) / 1000.0;
        }

        private async Task WantsAsync(int aid, Lease lease, CancellationToken tok)
        {
            D.WantsLast[aid] = Bridge.Env.Game;
            lease.Generating = true;
            try
            {
                await W(WantsOps.RunWantsAsync(Wa, aid, D.ToolBackend, D.ToolMode), tok).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                LlmLog.Warning("rimworldllm", "wants failed: " + e);
                Fail("wants", aid);
            }
        }

        // ---- launching --------------------------------------------------------
        private bool StartActor(string kind, int aid, Func<Lease, CancellationToken, Task> job)
        {
            var now = D.Now();
            var item = Leases.MakeItem(aid, kind, now, cls: Cls.Idle);
            var lease = Leases.Acquire(item, now);
            if (lease == null) return false;
            Kind = kind; Lease = lease;
            lease.Data["idle"] = true;
            var cts = new CancellationTokenSource();
            _cts = cts;
            Task = D.Spawn(() => GuardAsync(job, lease, cts));
            return true;
        }

        /// <summary>A narrator-slot job (chronicle, art): no lease. The caller already set Kind and NarratorBusy.</summary>
        private void StartNarrator(Func<CancellationToken, Task> job)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            Task = D.Spawn(async () =>
            {
                try { await job(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception e) { LlmLog.Warning("rimworldllm", "idle job failed: " + e); }
            });
        }

        private async Task GuardAsync(Func<Lease, CancellationToken, Task> job, Lease lease, CancellationTokenSource cts)
        {
            try
            {
                await job(lease, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { LlmLog.Warning("rimworldllm", "idle job failed: " + e); }
            finally
            {
                lease.Generating = false;
                if (!lease.Ended) Leases.Release(lease, "idle_done");
                if (ReferenceEquals(Lease, lease)) { Task = null; Lease = null; }
            }
        }

        private static Task W(Task t, CancellationToken tok) { return t.WaitAsync(tok); }
        private static Task<T> W<T>(Task<T> t, CancellationToken tok) { return t.WaitAsync(tok); }

        // ---- the jobs ---------------------------------------------------------
        private async Task ReflectAsync(int aid, Lease lease, CancellationToken tok)
        {
            var mind = Minds[aid];
            int now = Bridge.Env.Game;
            lease.Generating = true;
            bool askHabit = Cfg.Habits && Habits.HabitAllowed(mind, now, Day, Cfg.HabitEveryDays);
            var toks = new Dictionary<int, ISet<string>>();
            foreach (var kv in NameTokens) toks[kv.Key] = kv.Value;
            var res = await W(Reflection.RunReflectionAsync(A.Llm, Adapter, mind, Names, lease.Slot, now, Day, Cfg.ReflectionMaxTokens, toks,
                new HashSet<string>(Adapter.CommonWords), A.IsMinor(aid), A.MinorTokens(), Cfg.InnerMonologue, askHabit, Cfg.Concerns), tok).ConfigureAwait(false);
            lease.Generating = false;
            if (lease.Preempted) return;
            if (res == null)
            {
                Fail("reflect", aid);
                Dropped.Add((aid, "reflect_failed"));
                return;
            }
            var nameIds = new Dictionary<string, int>();
            foreach (var kv in Names) nameIds[kv.Value.ToLowerInvariant()] = kv.Key;
            D.RefreshReputations();
            IDictionary<string, string> reps = null;
            if (Cfg.Reputation) reps = D.Reputations.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            var o = Reflection.ApplyReflection(mind, res, now, nameIds, Cfg.InnerMonologue ? (int?)2 : null, reps, Cfg.ReputationShow,
                askHabit ? (double?)Cfg.HabitEveryDays : null, Day);
            Wa.Concerns.ApplyReflection(aid, res, now);
            var up = MemoryOps.Upkeep(mind, now, Day, Cfg.MemoryFloor, Cfg.MemoryMergeSimilarity);
            Dirty.Add(aid);
            var entry = new JObject
            {
                ["kind"] = "reflection", ["actor"] = aid, ["name"] = NameTok(aid), ["game"] = now,
                ["bio"] = o.Bio, ["wants"] = new JArray(o.Wants), ["relations"] = o.Relations, ["merged"] = up.Merged,
            };
            if (o.Habit != null) entry["habit"] = o.Habit;
            entry["dropped"] = up.Dropped;
            Log.Add(entry);
        }

        private async Task DiaryAsync(int aid, int target, Lease lease, CancellationToken tok)
        {
            var mind = Minds[aid];
            int now = Bridge.Env.Game;
            int day = Day;
            var ofDay = MemoryOps.WeightSorted(mind, now, day).Where(m => FloorDiv(m.Game, day) == target).ToList();
            // Real events of the day first; routine talk is left out, or is one plain line when nothing else happened.
            var kept = Cfg.SpeechChecks ? ofDay.Where(m => !MemoryOps.IsRoutineSocial(m, Adapter)).ToList() : ofDay;
            var mems = kept.Take(MaxDiaryMemories).OrderBy(m => m.Game).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            string today = string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event")));
            if (mems.Count == 0 && ofDay.Count > 0) today = "- Nothing out of the ordinary happened; I spent part of the day around other people.";
            var stream = Cfg.InnerMonologue ? Inner.InnerOnDay(mind, target, day) : new List<InnerThought>();
            string words = await W(Speech.RegisterPhraseAsync(Adapter, Bridge, aid, mind), tok).ConfigureAwait(false);
            string card = PromptBlocks.PawnCard(mind, true);          // the actor's fixed card rides in the system message
            string user = "=== WHAT HAPPENED TODAY ===\n" + GameData.DataSection("events", today)
                + (stream.Count > 0 ? "=== WHAT YOU WERE THINKING TODAY ===\n" + GameData.DataSection("thoughts", Inner.ThoughtsText(stream)) : "")
                + "=== REQUEST ===\n" + Speech.RegisterLine(mind.Name, words) + Adapter.DiaryRequest;
            lease.Generating = true;
            Completion c;
            try
            {
                c = await W(A.Llm.CompleteAsync(new List<LlmMessage> { new LlmMessage("system", Adapter.DiarySystem + "\n\n" + GameData.DataRule + "\n" + card),
                    new LlmMessage("user", user) }, lease.Slot, "diary", Cfg.DiaryMaxTokens, new string[0], null, 0.8, aid), tok).ConfigureAwait(false);
            }
            catch (LlmError)
            {
                lease.Generating = false;
                Fail("diary", aid);
                return;
            }
            lease.Generating = false;
            if (lease.Preempted) return;
            var toks = NameTokens;
            var allnames = Union(toks.Values);
            var known = new HashSet<string>(toks.TryGetValue(aid, out var mine) ? mine : new HashSet<string>());
            foreach (var m in mems) foreach (var p in m.People) if (toks.TryGetValue(p, out var pt)) known.UnionWith(pt);
            var speaker = toks.TryGetValue(aid, out var sp) ? sp : new HashSet<string>();
            var s = Sanitize.SanitizeProse(c.Text, known, allnames, Sanitize.PromptCapWords(card + user), new HashSet<string>(Adapter.CommonWords), speaker,
                5, 90, A.IsMinor(aid), A.MinorTokens());
            if (!s.Ok)
            {
                Fail("diary", aid);
                Dropped.Add((aid, "diary:" + s.Reason));
                return;
            }
            if (Cfg.SpeechChecks && Sanitize.IsTalkAboutTalk(s.Text))
            {
                Fail("diary", aid);
                Dropped.Add((aid, "diary:meta_talk"));
                return;
            }
            if (Cfg.SpeechChecks && LoopGuard.FindRepeatedDiaryOpening(s.Text, Mind.Tail(mind.Diary, DiaryOpeningsKept).Select(e => e.Text)) != null)
            {
                Fail("diary", aid);
                Dropped.Add((aid, "diary:repeat"));
                return;
            }
            mind.AddDiary(target, now, s.Text);
            Dirty.Add(aid);
            Log.Add(new JObject { ["kind"] = "diary", ["actor"] = aid, ["name"] = NameTok(aid), ["day"] = target, ["game"] = now, ["text"] = s.Text });
            try { await W(Bridge.ThoughtAsync(aid, s.Text, null, "diary"), tok).ConfigureAwait(false); }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "diary thought refused for " + aid + ": " + e.Message); }
        }

        /// <summary>A dream cloud for a sleeper (core/dreams.py). Placed before the diary and the thought: its window is the actor's sleep.</summary>
        private async Task DreamAsync(DreamJob job, Lease lease, CancellationToken tok)
        {
            try { await W(Dreams.RunDreamAsync(Dr, job, lease), tok).ConfigureAwait(false); }
            catch (Exception e) when (!(e is OperationCanceledException)) { LlmLog.Warning("rimworldllm", "dream failed: " + e); }
        }

        public const int NowKeep = 3;
        private readonly Dictionary<int, List<List<string>>> _nowRecent = new Dictionary<int, List<List<string>>>();

        /// <summary>What the character actually has at this moment, picked by salience: its job, where it is, a low need, pain, what weighs on it,
        /// a recent event that was not routine talk. Empty when the game cannot say.</summary>
        public async Task<List<string>> RightNowAsync(int aid, Mind mind)
        {
            JObject snap, scene;
            try { snap = await Bridge.ActorAsync(aid).ConfigureAwait(false); scene = await Bridge.SceneAsync(aid).ConfigureAwait(false); }
            catch (BridgeError) { return new List<string>(); }
            long now = Bridge.Env.Game;
            var events = new List<JObject>();
            foreach (var m in mind.Memories.Where(x => x.Visibility != "private" && x.Source != "told" && now - x.Game <= Day && !string.IsNullOrWhiteSpace(x.Text)
                                                       && !MemoryOps.IsRoutineSocial(x, Adapter)).OrderByDescending(x => x.Game).Take(6))
                events.Add(new JObject { ["type"] = string.IsNullOrEmpty(m.Etype) ? "memory" : m.Etype, ["summary"] = m.Text, ["game"] = m.Game,
                                         ["severity"] = Math.Min(1.0, m.Importance) });
            var all = Adapter.SalientFacts(snap, scene, null, -1, events, now);
            var recentKeys = new HashSet<string>();
            List<List<string>> recent;
            if (_nowRecent.TryGetValue(aid, out recent)) foreach (var r in recent) recentKeys.UnionWith(r);
            var picked = Salience.Pick(all.Where(f => f.Kind != FactKind.Listener),
                new SalienceAsk { Purpose = SaliencePurpose.Thought, Min = 2, Max = 4, RecentKeys = recentKeys });
            if (!_nowRecent.TryGetValue(aid, out recent)) { recent = new List<List<string>>(); _nowRecent[aid] = recent; }
            recent.Add(picked.Select(f => f.Key).ToList());
            if (recent.Count > NowKeep) recent.RemoveAt(0);
            return picked.Select(f => f.Text).ToList();
        }

        /// <summary>One short thought for the map's cloud balloon. The text is the surface line itself; no diary, no memory.</summary>
        private async Task ThoughtAsync(int aid, List<Memory> mems, Lease lease, CancellationToken tok)
        {
            var mind = Minds[aid];
            string memLines = string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event")));
            string words = await W(Speech.RegisterPhraseAsync(Adapter, Bridge, aid, mind), tok).ConfigureAwait(false);
            var now = Cfg.SpeechChecks ? await W(RightNowAsync(aid, mind), tok).ConfigureAwait(false) : new List<string>();
            string card = PromptBlocks.PawnCard(mind, true);          // the actor's fixed card rides in the system message
            var minors = A.MinorTokens();
            bool involvesMinor = A.IsMinor(aid) || mems.Any(m => m.People.Any(p => A.IsMinor(p)));   // per call: the age rule leads the request, not the system message
            string user = "=== WHAT IS ON YOUR MIND ===\n" + GameData.DataSection("memories", memLines)
                + (now.Count > 0 ? "=== RIGHT NOW ===\n" + GameData.DataSection("now", string.Join("\n", now.Select(t => "- " + GameData.Clean(t, "event")))) : "")
                + PromptBlocks.MinorRuleBeforeRequest(involvesMinor) + "=== REQUEST ===\n" + Speech.RegisterLine(mind.Name, words) + Adapter.ThoughtRequest;
            string system = Adapter.ThoughtSystem + "\n\n" + GameData.DataRule + "\n" + card;
            lease.Generating = true;
            Completion c;
            try
            {
                c = await W(A.Llm.CompleteAsync(new List<LlmMessage> { new LlmMessage("system", system), new LlmMessage("user", user) },
                    lease.Slot, "thought", 40, new[] { "\n" }, null, 0.9, aid), tok).ConfigureAwait(false);
            }
            catch (LlmError)
            {
                lease.Generating = false;
                Fail("thought", aid);
                return;
            }
            lease.Generating = false;
            if (lease.Preempted) return;
            var toks = NameTokens;
            var allnames = Union(toks.Values);
            var known = new HashSet<string>(toks.TryGetValue(aid, out var mine) ? mine : new HashSet<string>());
            foreach (var m in mems) foreach (var p in m.People) if (toks.TryGetValue(p, out var pt)) known.UnionWith(pt);
            var speaker = toks.TryGetValue(aid, out var sp) ? sp : new HashSet<string>();
            var s = Sanitize.SanitizeProse(c.Text, known, allnames, Sanitize.PromptCapWords(card + user), new HashSet<string>(Adapter.CommonWords), speaker,
                2, 18, involvesMinor, minors);
            string text = s.Ok ? s.Text : null;
            if (text != null && AgeGate.Violation(text, involvesMinor, minors))
            {
                text = null; s = new Sanitized(null, "minor_safety");           // last guard, as in the dream path
            }
            if (text != null && PyStr.Len(text) > Cfg.ThoughtMaxChars) text = null;          // a long thought never reaches the map
            if (text != null && Adapter.DropsMeta(text, mems.Select(m => m.Text)))
            {
                text = null; s = new Sanitized(null, "meta_talk");               // a thought that only remarks on the chatter
            }
            if (text != null && Cfg.SpeechChecks && Sanitize.IsTalkAboutTalk(text))
            {
                text = null; s = new Sanitized(null, "meta_talk");               // a thought whose subject is the talking itself
            }
            if (text != null && Cfg.SpeechChecks && LoopGuard.FindRepeatedThought(text, RecentThoughts(aid, mind)) != null)
            {
                text = null; s = new Sanitized(null, "repeat");                  // the same thought as one of its last ten
            }
            if (text == null)
            {
                Fail("thought", aid);
                Dropped.Add((aid, "thought:" + (!s.Ok ? s.Reason : "too_long")));
                return;
            }
            ThoughtNext[aid] = D.Clock() + Cfg.ThoughtEveryRealS;
            ThoughtLast[aid] = mems.Select(m => m.Id).ToArray();
            RememberThought(aid, text);
            Log.Add(new JObject { ["kind"] = "thought", ["actor"] = aid, ["name"] = NameTok(aid), ["game"] = Bridge.Env.Game, ["text"] = text });
            if (Cfg.InnerMonologue)
            {
                Inner.AddInner(mind, text, "musing", Bridge.Env.Game, Cfg.InnerStreamKeep);
                Dirty.Add(aid);
            }
            try { await W(Bridge.ThoughtAsync(aid, text, null, "thought", text), tok).ConfigureAwait(false); }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "thought refused for " + aid + ": " + e.Message); }
        }

        /// <summary>An art description (core/art.py), on the narrator slot like the chronicle. The job leaves the queue only now.</summary>
        public bool StartArt()
        {
            if (!Art.Enabled(A) || Tf.TextQueueLive || !Ok("art", -1)) return false;
            var job = A.Art.Take(Bridge.Env.Game, Day);
            if (job == null) return false;
            Kind = "art";
            Leases.NarratorBusy = true;
            StartNarrator(tok => ArtAsync(job, tok));
            return true;
        }

        private async Task ArtAsync(ArtJob job, CancellationToken tok)
        {
            var cts = _cts;
            try
            {
                var (text, why) = await W(Art.WriteArtAsync(A, job, Cfg.NarratorSlot), tok).ConfigureAwait(false);
                if (text == null)
                {
                    Fail("art", -1);
                    Dropped.Add((job.Maker, "art:" + why));
                    return;
                }
                Log.Add(new JObject { ["kind"] = "art", ["actor"] = job.Maker, ["name"] = NameTok(job.Maker), ["thing"] = job.Thing,
                    ["game"] = Bridge.Env.Game, ["text"] = text });
                try { await W(Bridge.ArtAsync(job.Thing, text), tok).ConfigureAwait(false); }
                catch (BridgeError ex)
                {
                    LlmLog.Warning("rimworldllm", "art description for " + job.Thing + " refused: " + ex.Message);
                    if (ex.Status == 404) A.Art.Unsupported = true;               // an older mod without POST /art: stop asking
                }
            }
            catch (LlmError)
            {
                Fail("art", -1);
            }
            catch (OperationCanceledException)
            {
                A.Art.PutBack(job);                                                // preempted: nothing applied, try again later
                throw;
            }
            finally
            {
                Leases.NarratorBusy = false;
                if (Lease == null && ReferenceEquals(_cts, cts)) Task = null;
            }
        }

        private async Task ChronicleAsync(CancellationToken tok)
        {
            var cts = _cts;
            try
            {
                int today = (int)FloorDiv(Bridge.Env.Game, Day);
                var led = D.Ledger.Entries.Values.OrderBy(e => e.OpenedTime).ThenBy(e => e.LedgerId, StringComparer.Ordinal).Select(e => e.Summary).ToList();
                var chron = A.Chronicle;
                var e2 = await W(chron.WriteAsync(A.Llm, new ChronicleView(Adapter), Cfg.NarratorSlot, today, led, NameTokens, Cfg.NarratorContextTokens,
                    Cfg.ChronicleMaxTokens, new HashSet<string>(Adapter.CommonWords), A.MinorTokens(), Cfg.Lore ? Lo.Lore.Lines() : null), tok).ConfigureAwait(false);
                if (e2 == null)
                {
                    Fail("chronicle", -1);
                    if (!string.IsNullOrEmpty(chron.LastDrop))
                    {
                        Dropped.Add((-1, "chronicle:" + chron.LastDrop));
                        chron.LastDrop = "";
                    }
                    return;
                }
                Log.Add(new JObject { ["kind"] = "chronicle", ["day"] = e2.Day, ["entry_kind"] = e2.Kind, ["game"] = e2.Game, ["text"] = e2.Text,
                    ["summary"] = chron.State.Summary });
                if (Cfg.ChronicleBridge)
                {
                    try { await W(Bridge.ChronicleAsync(e2.Day, e2.Text, e2.Kind), tok).ConfigureAwait(false); }
                    catch (BridgeError ex) { LlmLog.Warning("rimworldllm", "chronicle not written to the bridge: " + ex.Message); }
                }
                if (e2.Kind == "major" && Cfg.Lore)
                {
                    var made = await W(Lore.CoinAsync(Lo, e2.Text, (int)e2.Game, e2.Day), tok).ConfigureAwait(false);       // one small extra narrator call
                    if (made != null) Log.Add(new JObject { ["kind"] = "lore", ["name"] = made.Name, ["what"] = made.What, ["game"] = e2.Game });
                }
            }
            finally
            {
                Leases.NarratorBusy = false;
                if (Lease == null && ReferenceEquals(_cts, cts)) Task = null;
            }
        }

        /// <summary>IGameAdapter carries the chronicle prompts but is not an IChronicleAdapter; Chronicle.WriteAsync wants one.</summary>
        private sealed class ChronicleView : IChronicleAdapter
        {
            private readonly IGameAdapter _a;
            public ChronicleView(IGameAdapter a) { _a = a; }
            public string ChronicleRequest { get { return _a.ChronicleRequest; } }
            public string ChronicleSystem { get { return _a.ChronicleSystem; } }
            public string ChronicleSummarySystem { get { return _a.ChronicleSummarySystem; } }
        }

        // ---- helpers ----------------------------------------------------------
        private JToken NameTok(int aid)
        {
            return Names.TryGetValue(aid, out var n) ? (JToken)n : JValue.CreateNull();
        }

        private static HashSet<string> Union(IEnumerable<HashSet<string>> sets)
        {
            var r = new HashSet<string>();
            foreach (var s in sets) r.UnionWith(s);
            return r;
        }

        /// <summary>Python a // b for positive b.</summary>
        internal static long FloorDiv(long a, long b) { return JobsJson.FloorDiv(a, b); }

        /// <summary>Python a % b for positive b.</summary>
        internal static long PyMod(long a, long b) { return a - FloorDiv(a, b) * b; }

        /// <summary>float(x) of a bridge-config value; a missing key gives the default.</summary>
        internal static double PyFloatOf(JToken t, double dflt)
        {
            if (t == null) return dflt;
            switch (t.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String:
                    double v;
                    if (PyFloat.TryParse((string)t, out v)) return v;
                    throw new PyValueError("could not convert string to float: " + PyJson.StrRepr((string)t));
                default:
                    throw new PyTypeError("float() argument must be a string or a real number");
            }
        }
    }
}

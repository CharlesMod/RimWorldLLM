// Port of agent/rimworldllm_agent/core/steward.py: the steward: the narrator plays the game (docs/VIVARIUM.md).
//
// Owned by the Director; Steward.StepAsync runs every director step and costs one GET /vivarium when idle. When something wakes it, one
// cycle runs as a task: read the digest and the goals board, run the tool loop on the narrator slot (slot 0) with the game's tools plus
// the board tools, then post the final line, batch the board edits into one PUT /goals, and add one log line. While a cycle runs the
// narrator's idle jobs yield (LeaseManager.NarratorOwner).
//
// Wakes (one cycle at a time; a chat that arrives mid-cycle is answered by the next cycle at once):
//   chat       a new player line addressed to the narrator (to == 0): at once, ignores the real-time floor, works in advisor mode (the
//              narrator is not playing) and while the game is paused
//   event      while playing and unpaused: what the adapter calls urgent
//   heartbeat  while playing and unpaused: heartbeat_hours of game time since the last cycle
//   paused     while playing: the narrator's own emergency pause is on (once per pause), so it can resume
//   switched on  the player just handed the game over (only after a first look)
//
// NEW (not in the Python): an optional IChatLive sink (Steward.Live). When set, the cycle's tool loop streams through IStreamingBackend
// and the sink shows the narrator thinking / typing and the partial final line; the final line is still posted to /chat, with the
// stream's id so the panel does not show it twice. With no sink, behaviour and request bytes are exactly the Python's.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>What a game tool did (the steward only reads these).</summary>
    public sealed class StewardOutcome
    {
        public JObject Result;
        public string Verb = "";
        public string Note = "";
        public bool Ok;
    }

    /// <summary>One cycle's game tools (steward.kit.session(...)).</summary>
    public interface IStewardSession
    {
        IReadOnlyList<ToolSpec> Tools { get; }
        ISet<string> Acting { get; }
        Task<StewardOutcome> RunAsync(string tool, JObject values);
    }

    /// <summary>What the steward asks of the game adapter (adapter.steward_kit).</summary>
    public interface IStewardKit
    {
        string EventLine(JObject e);
        string Wake(JObject e);
        string DefaultStrategy();
        string SystemPrompt(bool advisor);
        IStewardSession Session(JObject digest, IBridge bridge, bool advisor);
        /// <summary>The playbook's alert section; null when the kit has no playbook.</summary>
        string PlaybookAlerts(JObject digest);
        string RenderDigest(JObject digest);
        /// <summary>Notes that only some runs need (the kit decides from the digest); "" for none. Joined to the strategy text.</summary>
        string StrategyExtra(JObject digest);
    }

    /// <summary>NEW (the Python's hasattr(kit, "trade_session")): a kit that has the trade turn, a small turn for each new trader.</summary>
    public interface IStewardTradeKit
    {
        /// <summary>trade_tools: what a trade turn may use.</summary>
        IReadOnlyCollection<string> TradeTools { get; }
        IStewardSession TradeSession(JObject digest, IBridge bridge);
        string TradeSystemPrompt();
        /// <summary>Why no trade turn now (food is critical, ...), or "" to go on.</summary>
        string TradeSkip(JObject digest);
        /// <summary>The trade view for the new traders and a food line ("" for none).</summary>
        (string View, string Food) RenderTrade(JObject digest, IList<string> arrived);
    }

    /// <summary>NEW (the Python's hasattr(kit, "combat_session")): a kit that has the combat loop. core/steward.py reads these members
    /// only through this interface, so a kit without combat is untouched.</summary>
    public interface IStewardCombatKit
    {
        /// <summary>COMBAT_TOOLS: what a combat turn may use.</summary>
        IReadOnlyCollection<string> CombatTools { get; }
        /// <summary>kit.session(digest, bridge, advisor, after_fight=True): the "combat over" cycle keeps squad for undraft and rescue.</summary>
        IStewardSession SessionAfterFight(JObject digest, IBridge bridge, bool advisor);
        /// <summary>The tools of a combat turn: built from the combat view alone, no digest.</summary>
        IStewardSession CombatSession(JObject view, IBridge bridge);
        string CombatSystemPrompt();
        string RenderCombat(JObject view);
    }

    /// <summary>The idle scheduler as the steward sees it (d.idle).</summary>
    public interface IStewardIdle
    {
        bool Active();
        string Kind { get; }
        void Preempt(string who);
    }

    /// <summary>NEW: the live chat line for the narrator (the panel's "Narrator is thinking / typing" line). One Begin per cycle; the
    /// implementation maps these to POST /chat/status and /chat/partial (docs/BRIDGE-API.md). Calls must not block.</summary>
    public interface IChatLive
    {
        /// <summary>Start a live line (thinking); returns its stream id. pawnId is null for the narrator.</summary>
        string Begin(string who, int? pawnId, string name);
        /// <summary>Prefill progress 0..1 (thinking). A negative figure: refresh the thinking state without a figure.</summary>
        void Progress(string id, double progress);
        /// <summary>The first token arrived: typing.</summary>
        void Typing(string id);
        /// <summary>The whole text so far (not a delta).</summary>
        void Partial(string id, string wholeText);
        /// <summary>The line is final (it has been posted to /chat with this stream id). A null finalText ends with no line.</summary>
        void End(string id, string finalText);
        /// <summary>An error or a cancelled answer: no line.</summary>
        void Cancel(string id);
    }

    /// <summary>The director members steward.py reads.</summary>
    public interface IStewardHost
    {
        IBridge Bridge { get; }
        MindConfig Cfg { get; }
        IGameAdapter Adapter { get; }
        LeaseManager Leases { get; }
        /// <summary>d.tool_backend: null when the model cannot run tools (the steward stays idle).</summary>
        IBackend ToolBackend { get; }
        /// <summary>d.tool_mode: auto | grammar | json | native.</summary>
        string ToolMode { get; }
        IStewardIdle Idle { get; }
        GateStats Gate { get; }
        IList<(int Actor, string Reason)> Dropped { get; }
        int Day { get; }
        /// <summary>d.clock(): monotonic seconds.</summary>
        double Clock();
        /// <summary>d._spawn(coro): run the cycle as a director-tracked task.</summary>
        Task Spawn(Func<Task> work);
        HashSet<string> MinorTokens();
    }

    public sealed class StewardStats
    {
        public int Cycles;
        public Dictionary<string, int> ByReason = new Dictionary<string, int>();
        public int Acts, Refused, Lines, Notes, Failed;
        /// <summary>One object per cycle, newest last (200 at most).</summary>
        public List<JObject> Trace = new List<JObject>();
    }

    public sealed class Steward
    {
        public const int MaxEventLines = 15;
        public const int MaxChatLines = 10;
        public const int CatchUpLines = 3;                // at most this many unanswered player lines are answered on first sight
        public const int MaxNotes = 3;
        public const int Steps = 8;
        public const double BudgetS = 120.0, StepTimeoutS = 60.0;
        public const int FinalMax = 300;
        // a combat turn is small: the combat view and the plan line, the combat tools only, three steps (the last is the closing line)
        public const int CombatSteps = 3, CombatMaxTokens = 160, CombatFinalMax = 120;
        public const double CombatBudgetS = 40.0, CombatStepTimeoutS = 30.0;
        // a trade turn (a trader arrived): the trade view, the trade tools, a few steps (the last is the closing line)
        public const int TradeSteps = 4, TradeMaxTokens = 300, TradeFinalMax = 120;
        public const double TradeBudgetS = 60.0, TradeStepTimeoutS = 30.0;
        public const int TicksPerGameS = 60;
        public const int CombatStartSteps = 5;            // the "combat started" cycle: draft, place, flee, one defence, the line; the combat turns follow
        public const string EnvStrategy = "RIMWORLDLLM_STEWARD_STRATEGY";
        public const string StrategyHeading = "## Strategy notes";
        public const string PausedNote = "Paused: a game is running on that machine";
        private static readonly HashSet<string> NoLine = new HashSet<string> { "", "done", "nothing", "nothing to say", "none", "n/a", "ok" };

        public readonly IStewardHost D;
        public readonly IStewardKit Kit;
        public bool Disabled;
        public Task Task;
        public readonly StewardStats Stats = new StewardStats();
        public JObject Vv = new JObject();
        /// <summary>NEW: the live chat sink; null keeps the Python behaviour (a /chat/status thinking and idle around the call).</summary>
        public IChatLive Live;
        /// <summary>NEW: how often the live line is refreshed during a long wait (the mod drops a stream silent for 90 s).</summary>
        public TimeSpan LiveRefresh = TimeSpan.FromSeconds(30);
        /// <summary>NEW: the least gap between two partial posts (at most 8 a second).</summary>
        public double PartialMinGapS = 0.125;
        /// <summary>NEW (the look tool): can the model read pictures? Null: built from the tool backend's endpoint on first use.</summary>
        public IVisionGate Vision;
        /// <summary>NEW: gate + picture preparation behind one seam (ILookClient). Null: a DirectLookClient over <see cref="Vision"/>.</summary>
        public ILookClient Look;
        /// <summary>NEW: the game's "narrator may look" setting (GET /config vision_allowed; missing is allowed). The director sets it.</summary>
        public Func<bool> VisionAllowed = () => true;
        /// <summary>NEW: the engine look's own id_slot (never the conversation's). Null: one past the highest narrator/actor slot.</summary>
        public int? LookSlot;
        /// <summary>NEW: the wait between two polls of a pending picture (the look tool); replaceable for tests.</summary>
        public Func<int, Task> VisionDelay;
        private int _streams;
        private string _strategyNote;

        public int? Cursor;
        public List<JObject> PendingChat = new List<JObject>();
        public List<string> Events = new List<string>();
        public List<string> Urgent = new List<string>();
        public int? LastGame;
        public double? LastReal;
        public bool WasPlaying;                           // the switch was on at the last step (an off to on flip wakes at once)
        public bool SwitchedOn;
        public bool PauseWoken;
        public bool DialogPending;                        // a choice window opened: wakes even while the game is paused
        public bool CombatOn;                             // a fight is running and the combat loop owns the cadence
        public bool CombatStart;                          // the "combat started" cycle is due
        public bool CombatOver;                           // the "combat over" cycle is due
        public HashSet<string> TradeSeen = new HashSet<string>(StringComparer.Ordinal);   // trader ids already given their trade turn (one per visit)
        public List<string> TradeNew = new List<string>();                                  // the ids the due trade turn is for
        public bool Slowed;                               // we put the game on 1x for the fight

        public Steward(IStewardHost d)
        {
            D = d;
            object k = d.Adapter.StewardKit;
            Kit = k as IStewardKit;
            Disabled = Kit == null || !d.Cfg.Steward;
            Reset();
        }

        // ---- the strategy file -----------------------------------------------------------
        /// <summary>The strategy section text and a note for the log ("" when fine). Order: env path, config path, the shipped text.
        /// A chosen file that is missing, unreadable or empty leaves the section out; a long one is cut at steward_strategy_max_bytes.</summary>
        public static (string Text, string Note) LoadStrategy(MindConfig cfg, string defaultText)
        {
            string path = (Environment.GetEnvironmentVariable(EnvStrategy) ?? "").Trim();
            if (path.Length == 0) path = (cfg.StewardStrategyPath ?? "").Trim();
            string note = "";
            string text;
            if (path.Length > 0)
            {
                try { text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(ExpandUser(path))).Replace("\r\n", "\n").Replace("\r", "\n"); }   // Python text mode: universal newlines
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is ArgumentException)
                {
                    return ("", "strategy file " + path + " not read (" + (e is IOException ? "OSError" : e is UnauthorizedAccessException ? "PermissionError" : PyNames.Of(e)) + "): section left out");
                }
            }
            else text = defaultText;
            text = PyStr.Strip(text);
            if (text.Length == 0) return ("", "strategy file " + (path.Length > 0 ? path : "(shipped)") + " is empty: section left out");
            int cap = cfg.StewardStrategyMaxBytes;
            byte[] raw = Encoding.UTF8.GetBytes(text);
            if (raw.Length > cap)
            {
                var ignore = Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(""));
                string cut = ignore.GetString(raw, 0, Math.Max(0, cap));
                int nl = cut.LastIndexOf('\n');
                text = nl >= 0 ? RStrip(cut.Substring(0, nl)) : cut;
                note = "strategy cut to " + cap + " bytes (was " + raw.Length + ")";
            }
            return (text, note);
        }

        private static string ExpandUser(string p)
        {
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p.Substring(2) : "");
            return p;
        }

        private static string RStrip(string s)
        {
            int b = s.Length;
            while (b > 0 && PyStr.IsSpace(s[b - 1])) b--;
            return s.Substring(0, b);
        }

        // ---- state -------------------------------------------------------------------------
        /// <summary>A new world (the save epoch changed): forget the chat cursor, the queued wakes and the cycle clocks.</summary>
        public void Reset()
        {
            Cursor = null;
            PendingChat = new List<JObject>();
            Events = new List<string>();
            Urgent = new List<string>();
            LastGame = null;
            LastReal = null;
            WasPlaying = false;
            SwitchedOn = false;
            PauseWoken = false;
            CombatOn = false;
            CombatStart = false;
            CombatOver = false;
            Slowed = false;
            TradeSeen = new HashSet<string>(StringComparer.Ordinal);
            TradeNew = new List<string>();
            DialogPending = false;
            Vv = new JObject();
            if (D.Leases.NarratorOwner == "steward") D.Leases.NarratorOwner = null;
        }

        public bool Active() { return Task != null && !Task.IsCompleted; }

        // ---- inputs ------------------------------------------------------------------------
        /// <summary>Director.ingest hands every event here. Only while the narrator plays does the steward collect them.</summary>
        public void Observe(JObject e)
        {
            if (Disabled || !PyJson.Truthy(JobsJson.Get(Vv, "playing") ?? JValue.CreateNull())) return;
            string line = Kit.EventLine(e);
            if (!string.IsNullOrEmpty(line))
            {
                Events.Add(line);
                KeepLast(Events, MaxEventLines);
            }
            string why = Kit.Wake(e);
            if (why == "dialog") DialogPending = true;        // modal windows pause the game: handled in Wake, not as an urgent event
            else if (!string.IsNullOrEmpty(why))
            {
                Urgent.Add(why);
                KeepLast(Urgent, MaxEventLines);
            }
        }

        private static void KeepLast<T>(List<T> l, int n) { if (l.Count > n) l.RemoveRange(0, l.Count - n); }

        private static bool Truthy(JToken t) { return t != null && PyJson.Truthy(t); }
        private static int IntOr0(JToken t) { return Truthy(t) ? (int)JobsJson.PyInt(t) : 0; }

        private static bool IsPlayerLine(JObject e)
        {
            return JobsJson.Str(e, "who", null) == "player" && IntOr0(JobsJson.Get(e, "to")) == 0 && PyStr.Strip(JobsJson.Str(e, "text", "")).Length > 0;
        }

        private static IEnumerable<JObject> Entries(JObject b)
        {
            JToken t = JobsJson.Get(b, "entries");
            return Truthy(t) ? ((IEnumerable<JToken>)t).OfType<JObject>() : Enumerable.Empty<JObject>();
        }

        public async Task StepAsync()
        {
            var d = D;
            if (Disabled || d.ToolBackend == null) return;
            JObject vv;
            try { vv = await d.Bridge.VivariumAsync().ConfigureAwait(false); }
            catch (BridgeError e)
            {
                if (e.Status == 404)
                {
                    Disabled = true;
                    LlmLog.Info("rimworldllm.steward", "the mod has no /vivarium (older mod): the steward stays off");
                }
                return;
            }
            Vv = vv;
            bool playing = Truthy(JobsJson.Get(vv, "playing"));
            if (playing && !WasPlaying) SwitchedOn = true;      // the player just handed over: look at the world now
            WasPlaying = playing;
            int seq = IntOr0(JobsJson.Get(vv, "chat_seq"));
            if (Cursor == null || seq < Cursor)
            {
                Cursor = seq;                                    // first sight (or a new log): old history is not answered,
                await CatchUpAsync(seq).ConfigureAwait(false);   // but recent player lines nobody answered yet are
            }
            else if (seq > Cursor)
            {
                JObject b;
                try { b = await d.Bridge.ChatSinceAsync(Cursor.Value).ConfigureAwait(false); }
                catch (BridgeError e)
                {
                    LlmLog.Warning("rimworldllm.steward", "steward: chat not read: " + e.Message);
                    return;
                }
                foreach (var e in Entries(b)) if (IsPlayerLine(e)) PendingChat.Add(e);
                KeepLast(PendingChat, MaxChatLines);
                JToken ns = JobsJson.Get(b, "next_seq");
                int next = ns == null ? seq : (Truthy(ns) ? (int)JobsJson.PyInt(ns) : seq);
                Cursor = Math.Max(Cursor.Value, next);
            }
            if (!playing)
            {
                Events.Clear();
                Urgent.Clear();
                DialogPending = false;
            }
            await CombatStateAsync(playing).ConfigureAwait(false);
            if (!Truthy(JobsJson.Get(vv, "paused_by_narrator"))) PauseWoken = false;
            if (Active()) return;
            string reason = Wake(playing);
            if (reason != null) Start(reason, playing);
        }

        /// <summary>Follow GET /vivarium combat.active: a fight starting (speed rule in code, a full cycle), ending (a full cycle, the
        /// speed put back). Only while the narrator plays and the kit has combat; advisor mode has no combat loop.</summary>
        private async Task CombatStateAsync(bool playing)
        {
            if (!(Kit is IStewardCombatKit)) return;
            bool active = playing && Truthy(JobsJson.Get(JobsJson.Get(Vv, "combat") as JObject ?? new JObject(), "active"));
            if (active && !CombatOn)
            {
                CombatOn = true; CombatStart = true; CombatOver = false;
                SwitchedOn = false;                              // the combat cycle is the first look
                var st = JobsJson.Get(Vv, "settings") as JObject ?? new JObject();
                var env = D.Bridge.Env;
                if (Truthy(JobsJson.Get(st, "slow_for_combat")) && env.Speed > 1 && !Slowed)
                {
                    try
                    {
                        await D.Bridge.ActAsync("time", new JObject { ["speed"] = 1, ["combat"] = true }).ConfigureAwait(false);
                        Slowed = true;
                    }
                    catch (BridgeError e) { LlmLog.Info("rimworldllm.steward", "steward: speed not slowed for the fight: " + e.Message); }
                }
            }
            else if (!active && CombatOn)
            {
                CombatOn = false; CombatStart = false;
                CombatOver = playing;                            // a fight that ended because the player took over needs no cycle
                await RestoreSpeedAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Put the speed back if we slowed it and the player has not changed it since (still 1x).</summary>
        private async Task RestoreSpeedAsync()
        {
            if (!Slowed) return;
            Slowed = false;
            if (D.Bridge.Env.Speed != 1) return;                 // the player changed it: leave it
            try { await D.Bridge.ActAsync("time", new JObject { ["combat"] = true, ["restore"] = true }).ConfigureAwait(false); }
            catch (BridgeError e) { LlmLog.Info("rimworldllm.steward", "steward: speed not restored after the fight: " + e.Message); }
        }

        /// <summary>First sight of a chat log: queue the player's lines after the last narrator line (typed while the agent was starting,
        /// restarting or away), so they get an answer. Lines the narrator already answered stay history.</summary>
        private async Task CatchUpAsync(int seq)
        {
            if (seq <= 0) return;
            JObject b;
            try { b = await D.Bridge.ChatSinceAsync(Math.Max(0, seq - MaxChatLines)).ConfigureAwait(false); }
            catch (BridgeError) { return; }
            var tail = new List<JObject>();
            foreach (var e in Entries(b))
            {
                string who = JobsJson.Str(e, "who", null);
                if (who == "narrator") tail = new List<JObject>();     // answered up to here
                else if (IsPlayerLine(e)) tail.Add(e);
            }
            PendingChat.AddRange(tail.Skip(Math.Max(0, tail.Count - CatchUpLines)));
        }

        private string Wake(bool playing)
        {
            var d = D;
            var env = d.Bridge.Env;
            if (PendingChat.Count > 0) return "chat";
            if (!playing || (env.Paused && !Truthy(JobsJson.Get(Vv, "paused_by_narrator")) && !DialogPending)) return null;
            if (SwitchedOn)
            {
                SwitchedOn = false;
                if (LastGame != null) return "switched on";      // never looked yet: the heartbeat below fires anyway
            }
            if (DialogPending && (CombatOn || CombatOver) && (LastReal == null || d.Clock() - LastReal.Value >= d.Cfg.StewardCombatGapRealS))
            {
                DialogPending = false;
                return "dialog";                                 // a choice in a fight (a ransom, a joiner) is not starved by the combat turns
            }
            if (!env.Paused)
            {
                string fight = CombatWake();
                if (fight != null) return fight.Length > 0 ? fight : null;
            }
            if (LastReal != null && d.Clock() - LastReal.Value < d.Cfg.StewardMinGapRealS) return null;
            if (DialogPending)
            {
                DialogPending = false;
                return "dialog";                                 // also while paused: the window is why the game is paused
            }
            if (Urgent.Count > 0 && !env.Paused) return "event";
            if (env.Paused)
            {
                if (!PauseWoken)
                {
                    PauseWoken = true;
                    return "paused";
                }
                return null;
            }
            if (Kit is IStewardTradeKit)
            {
                var fresh = TraderIds().Where(x => !TradeSeen.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (fresh.Count > 0)
                {
                    TradeNew = fresh;
                    return "trade";
                }
            }
            var st = JobsJson.Get(Vv, "settings") as JObject;
            JToken hh = st == null ? null : JobsJson.Get(st, "heartbeat_hours");
            double hours = Truthy(hh) ? Convert.ToDouble(hh.Type == JTokenType.String ? (object)double.Parse((string)hh, System.Globalization.CultureInfo.InvariantCulture) : (double)hh) : 6.0;
            double hb = hours * d.Day / 24;
            if (LastGame == null || env.Game < LastGame || env.Game - LastGame.Value >= hb) return "heartbeat";
            return null;
        }

        /// <summary>The ids of the traders the vivarium lists (GET /vivarium traders).</summary>
        private HashSet<string> TraderIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (JobsJson.Get(Vv, "traders") is JArray a)
                foreach (var x in a.OfType<JObject>())
                    if (Truthy(JobsJson.Get(x, "id"))) ids.Add(PyJson.Str(JobsJson.Get(x, "id")));
            return ids;
        }

        /// <summary>The combat wakes. "" = a fight is on and nothing is due (nothing else wakes); null = not in a fight, carry on.</summary>
        private string CombatWake()
        {
            var d = D;
            var env = d.Bridge.Env;
            if (!(CombatOn || CombatOver)) return null;
            if (LastReal != null && d.Clock() - LastReal.Value < d.Cfg.StewardCombatGapRealS) return "";
            if (CombatStart)
            {
                CombatStart = false;
                return "combat started";
            }
            if (CombatOver)
            {
                CombatOver = false;
                return "combat over";
            }
            double gap = d.Cfg.StewardCombatGapGameS * TicksPerGameS;
            if (LastGame == null || env.Game < LastGame || env.Game - LastGame.Value >= gap) return "combat";
            return "";
        }

        private void Start(string reason, bool playing)
        {
            var d = D;
            var chat = PendingChat;
            PendingChat = new List<JObject>();
            List<string> events;
            if (reason == "trade") TradeSeen.UnionWith(TraderIds());     // once per visit, whatever the turn does
            if (reason == "combat" || reason == "trade") events = new List<string>();   // a combat or trade turn leaves the events for the full cycle that follows the fight
            else
            {
                events = playing ? Events : new List<string>();
                Events = new List<string>();
                Urgent = new List<string>();
            }
            d.Leases.NarratorOwner = "steward";                  // idle narrator work yields from here on
            if (d.Idle != null && d.Idle.Active() && (d.Idle.Kind == "chronicle" || d.Idle.Kind == "art"))
                d.Idle.Preempt("steward");                       // a running slot-0 job is cancelled; nothing it made is applied
            if (reason != "trade") LastGame = d.Bridge.Env.Game;        // a trade turn does not push the routine look back
            LastReal = d.Clock();
            Task = d.Spawn(() => CycleAsync(reason, chat, events, playing));
        }

        // ---- a cycle -----------------------------------------------------------------------
        internal async Task CycleAsync(string reason, List<JObject> chat, List<string> events, bool playing)
        {
            var d = D;
            var t = new JObject
            {
                ["reason"] = reason, ["playing"] = playing,
                ["chat"] = new JArray(chat.Select(c => (object)(Truthy(JobsJson.Get(c, "text")) ? JobsJson.Str(c, "text") : JobsJson.Str(c, "text", ""))).ToArray()),
                ["events"] = new JArray(events.Cast<object>().ToArray()),
            };
            try { await RunAsync(reason, chat, events, playing, t).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)                                  // a cycle never takes the director down
            {
                Stats.Failed++;
                t["error"] = PyNames.Describe(e);
                LlmLog.Warning("rimworldllm.steward", "steward cycle failed: " + e);
            }
            finally
            {
                if (d.Leases.NarratorOwner == "steward") d.Leases.NarratorOwner = null;
                LastReal = d.Clock();
                Stats.Cycles++;
                Stats.ByReason[reason] = (Stats.ByReason.TryGetValue(reason, out int n) ? n : 0) + 1;
                Stats.Trace.Add(t);
                KeepLast(Stats.Trace, 200);
            }
        }

        private async Task<bool> SayAsync(string who, string text, string streamId = null)
        {
            try
            {
                await D.Bridge.PostChatAsync(who, text, null, streamId).ConfigureAwait(false);
                return true;
            }
            catch (BridgeError e)
            {
                LlmLog.Warning("rimworldllm.steward", "steward: chat line not posted: " + e.Message);
                return false;
            }
        }

        /// <summary>A narrator line made safe to post, or null: empty, "done", or romantic/sexual with a minor.</summary>
        private string CleanLine(string text, int cap = 500)
        {
            string t = StewardBoard.Plain(text, cap);
            if (NoLine.Contains(t.Trim(' ', '.', '!').ToLowerInvariant()) || !HasLetter(t)) return null;     // "done", or a scrap with no words in it
            if (AgeGate.Violation(t, false, D.MinorTokens()))
            {
                D.Gate.SkippedEvents++;
                D.Dropped.Add((-1, "steward_line:age_gate"));
                return null;
            }
            return t;
        }

        private static bool HasLetter(string t)
        {
            foreach (var r in t.EnumerateRunes()) if (Rune.IsLetter(r)) return true;
            return false;
        }

        /// <summary>Tell the game's chat the narrator is thinking / idle. Best effort: never breaks or stalls a cycle.</summary>
        private async Task StatusAsync(string state, string streamId)
        {
            try
            {
                var call = D.Bridge.PostChatStatusAsync("narrator", state, streamId);
                var first = await Task.WhenAny(call, Task.Delay(3000)).ConfigureAwait(false);
                if (first != call) { var _ = call.ContinueWith(c => { var ex = c.Exception; }, TaskContinuationOptions.OnlyOnFaulted); throw new TimeoutException(); }
                await call.ConfigureAwait(false);
            }
            catch (Exception e)                                  // older mod (not_found), network, timeout
            {
                LlmLog.Debug("rimworldllm.steward", "steward: chat status " + state + " not posted: " + e.Message);
            }
        }

        /// <summary>A chat completion refused with 400 images_not_offered (a remote caller may not send images in chat).</summary>
        public static bool IsImagesNotOffered(Exception e)
            => e != null && e.Message != null && e.Message.Contains("HTTP 400") && e.Message.Contains("images_not_offered");

        /// <summary>The vision gate: the game's setting allows it and the model reads pictures. A failing probe is a closed gate, never a failed turn.</summary>
        private async Task<bool> VisionOpenAsync()
        {
            try
            {
                if (!VisionAllowed()) return false;
                if (Look == null)
                {
                    if (Vision == null && D.ToolBackend is OpenAIBackend ob)
                    {
                        var probe = new VisionProbe(ob.Http, ob.Base, ob.ApiKey);
                        int slot = LookSlot ?? (D.Cfg.ActorSlots.Concat(new[] { D.Cfg.NarratorSlot }).Max() + 1);
                        Vision = probe;
                        Look = new RoutedLookClient(probe, new EngineLookClient(ob.Http, ob.Base, probe, ob.ApiKey, slot));
                    }
                    if (Vision == null) return false;
                    if (Look == null) Look = new DirectLookClient(Vision);
                }
                return await Look.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LlmLog.Debug("rimworldllm.steward", "steward: vision gate failed: " + e.Message);
                return false;
            }
        }

        private static string Need(JObject v, string key)
        {
            JToken t = JobsJson.Get(v, key);
            if (t == null) throw new PyKeyError(key);
            return PyJson.Str(t);
        }

        private async Task RunAsync(string reason, List<JObject> chat, List<string> events, bool playing, JObject t)
        {
            var d = D;
            var kit = Kit;
            bool advisor = !playing;
            if (reason == "combat") { await RunCombatAsync(t).ConfigureAwait(false); return; }
            if (reason == "trade") { await RunTradeAsync(t).ConfigureAwait(false); return; }
            JObject digest, board;
            try
            {
                digest = await d.Bridge.DigestAsync().ConfigureAwait(false);
                board = await d.Bridge.GoalsAsync().ConfigureAwait(false);
            }
            catch (BridgeError e)
            {
                t["error"] = "read failed: " + e.Message;
                Stats.Failed++;
                if (chat.Count > 0) await SayAsync("narrator", "I can't read the game state right now, so I can't act on that yet. Ask me again in a moment.").ConfigureAwait(false);
                return;
            }
            var (strategy, snote) = LoadStrategy(d.Cfg, kit.DefaultStrategy());
            if (snote != _strategyNote)
            {
                _strategyNote = snote;
                if (snote.Length > 0) LlmLog.Info("rimworldllm.steward", "steward: " + snote);
            }
            string extra = kit.StrategyExtra(digest) ?? "";                // notes that only some games need (the adapter decides)
            if (extra.Length > 0) strategy = strategy.Length > 0 ? strategy + "\n\n" + extra : extra;
            string system = kit.SystemPrompt(advisor) + (strategy.Length > 0 ? "\n\n" + StrategyHeading + "\n" + strategy : "") + "\n\n" + GameData.DataRule;
            var session = reason == "combat over" && kit is IStewardCombatKit afterKit ? afterKit.SessionAfterFight(digest, d.Bridge, advisor) : kit.Session(digest, d.Bridge, advisor);
            var tools = session.Tools.Concat(StewardBoard.BoardTools(board, advisor)).ToList();
            IVisionLook vision = null;                                     // the look tool: routine turns only, when the setting allows it and the model reads pictures
            if (reason != "combat started" && reason != "combat over" && kit is IStewardVisionKit vk && await VisionOpenAsync().ConfigureAwait(false))
            {
                vision = vk.NewLook(digest, d.Bridge, VisionDelay, Look);
                if (vision != null) tools.Add(vision.Spec);
            }
            HashSet<string> allowed = advisor
                ? new HashSet<string>(session.Tools.Select(x => x.Name)) { "note", "add_order", "do_nothing" }
                : new HashSet<string>(tools.Select(x => x.Name));
            if (vision != null) allowed.Add(vision.ToolName);
            string alerts = "";
            try { alerts = kit.PlaybookAlerts(digest) ?? ""; }
            catch (Exception e) { LlmLog.Warning("rimworldllm.steward", "steward: playbook alerts failed: " + e); }     // the playbook never takes a cycle down
            string user = UserText(reason, chat, events, board, kit.RenderDigest(digest), advisor, alerts);
            t["system_chars"] = PyStr.Len(system);
            t["user_chars"] = PyStr.Len(user);

            var edits = new Edits();
            var acts = new List<(string Verb, string Note)>();           // (verb, note) of every success
            int notesUsed = 0, posted = 0, refused = 0;

            Task<string> validator(string tool, JObject v)
            {
                if (advisor && session.Acting.Contains(tool) && acts.Count > 0) return Task.FromResult("advisor mode allows one task only; say what you did");
                if (tool == "note" && notesUsed >= MaxNotes) return Task.FromResult("at most " + MaxNotes + " notes per turn");
                string text = string.Join(" ", v.Properties().Where(p => p.Value.Type == JTokenType.String).Select(p => (string)p.Value));
                if (AgeGate.Violation(text, false, d.MinorTokens())) return Task.FromResult("not allowed: nothing romantic or sexual involving a minor");
                return Task.FromResult<string>(null);
            }

            async Task<JToken> executor(string tool, JObject v)
            {
                if (tool == "do_nothing") return null;
                if (vision != null && tool == vision.ToolName) return await vision.RunAsync(v).ConfigureAwait(false);
                if (tool == "note")
                {
                    string line = CleanLine(Need(v, "text"), 160);
                    if (line == null) return new JObject { ["error"] = "empty note" };
                    notesUsed++;
                    if (await SayAsync("note", line).ConfigureAwait(false))
                    {
                        Stats.Notes++;
                        posted++;
                    }
                    return new JObject { ["done"] = "note posted" };
                }
                if (Array.IndexOf(StewardBoard.BoardToolNames, tool) >= 0)
                {
                    EditOp op;
                    switch (tool)
                    {
                        case "add_goal": op = new EditOp("add_goal", StewardBoard.Plain(Need(v, "text")), Need(v, "horizon")); break;
                        case "set_goal": op = new EditOp("set_goal", Need(v, "goal_id"), Need(v, "status")); break;
                        case "set_plan": op = new EditOp("set_plan", StewardBoard.Plain(Need(v, "text"))); break;
                        case "add_order": op = new EditOp("add_order", StewardBoard.Plain(Need(v, "text"))); break;
                        case "drop_order": op = new EditOp("drop_order", Need(v, "order_id")); break;
                        default: throw new PyKeyError(tool);
                    }
                    string err = edits.TryAdd(board, op);
                    return err != null ? new JObject { ["error"] = err } : new JObject { ["done"] = "recorded; it is saved to the board at the end of the turn" };
                }
                var outc = await session.RunAsync(tool, v).ConfigureAwait(false);
                if (outc.Ok)
                {
                    acts.Add((outc.Verb, outc.Note));
                    Stats.Acts++;
                }
                else if (outc.Result.ContainsKey("refused"))
                {
                    refused++;
                    Stats.Refused++;
                }
                return outc.Result;
            }

            var harness = new ToolHarness(d.ToolBackend, tools, d.ToolMode, null, reason == "combat started" ? CombatStartSteps : Steps, 2, BudgetS, StepTimeoutS);
            _streams++;
            string streamId = "steward-" + Environment.ProcessId + "-" + _streams;
            StewardLiveSink sink = null;
            CancellationTokenSource refreshCts = null;
            Task refresher = null;
            if (Live == null) await StatusAsync("thinking", streamId).ConfigureAwait(false);
            else
            {
                sink = new StewardLiveSink(Live, d.Clock, PartialMinGapS, text => LivePartialOk(text));
                sink.EnsureBegun();
                refreshCts = new CancellationTokenSource();
                refresher = RefreshLoop(sink, refreshCts.Token);
            }
            LoopResult res;
            try
            {
                var allowedSorted = allowed.OrderBy(x => x, StringComparer.Ordinal).ToList();
                res = await harness.RunAsync(system, user, executor, validator, null, o =>
                {
                    o.Allowed = allowedSorted;
                    o.Slot = d.Cfg.NarratorSlot;
                    o.FinalMax = FinalMax;
                    o.MaxTokens = 220;
                    if (sink != null) o.CallOptions = new CallOptions { Sink = sink };
                    if (vision != null) o.TakeAttachment = vision.TakeAttachment;
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (vision != null && Look is RoutedLookClient rl && rl.Path == LookPath.Direct && IsImagesNotOffered(e) && !rl.IsOff)
                {
                    rl.Off();                                       // the endpoint refuses pictures in chat: no look for the rest of the session, text only
                    LlmLog.Warning("rimworldllm.steward", "steward: the endpoint refused a picture (images_not_offered); look is off for this session");
                }
                if (sink != null) sink.Finish(null, false, Live);
                throw;
            }
            finally
            {
                if (Live == null) await StatusAsync("idle", streamId).ConfigureAwait(false);
                if (refreshCts != null)
                {
                    refreshCts.Cancel();
                    try { await refresher.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    refreshCts.Dispose();
                }
            }
            t["outcome"] = res.Outcome;
            t["reason_ended"] = res.Reason;
            t["protocol"] = res.Protocol;
            t["failures"] = new JArray(res.Failures.Skip(Math.Max(0, res.Failures.Count - 4)).Cast<object>().ToArray());
            t["executed"] = new JArray(res.Executed.Select(e => (object)new JArray(e.Tool, e.Args, e.Result)).ToArray());
            t["elapsed_s"] = Math.Round(res.ElapsedS, 2);
            if (vision != null && vision.Looked.Count > 0) t["looks"] = new JArray(vision.Looked.Cast<object>().ToArray());

            // no automatic chat notes: the game writes one greentext line per act itself (from the act's "why")
            // the cycle's final line
            string final = res.Outcome == "final" ? CleanLine(res.FinalText, 500) : null;
            bool away = res.Reason == "away" || (sink != null && sink.Away);
            bool sentFinal = false;
            if (final != null && await SayAsync("narrator", final, sink?.Id).ConfigureAwait(false))
            {
                sentFinal = true;
                Stats.Lines++;
                posted++;
                t["line"] = final;
            }
            else if (chat.Count > 0 && posted == 0 && res.Executed.Count == 0)     // the player asked and would otherwise hear nothing
                await SayAsync("narrator", "I couldn't think that through just now. Ask me again in a moment.").ConfigureAwait(false);
            if (sink != null) sink.Finish(sentFinal ? final : null, away && !sentFinal, Live);
            // the goals board: one PUT with the edits and the log line
            if (edits.Any || acts.Count > 0) await SaveBoardAsync(board, edits, reason, chat, acts, t).ConfigureAwait(false);
        }

        /// <summary>A combat turn: GET /combat and the plan line, the small combat prompt, the combat tools, three steps. No digest, no
        /// board edits (notes only), no closing chat line (the game prints each act and its why as greentext).</summary>
        private async Task RunCombatAsync(JObject t)
        {
            var d = D;
            var kit = (IStewardCombatKit)Kit;
            JObject view, board;
            try
            {
                view = await d.Bridge.CombatAsync().ConfigureAwait(false);
                board = await d.Bridge.GoalsAsync().ConfigureAwait(false);
            }
            catch (BridgeError e)
            {
                t["error"] = "read failed: " + e.Message;
                Stats.Failed++;
                return;
            }
            if (!Truthy(JobsJson.Get(view, "active")))
            {
                t["skipped"] = "the fight is over";             // the next step sees combat.active false and starts the "combat over" cycle
                return;
            }
            string system = kit.CombatSystemPrompt() + "\n\n" + GameData.DataRule;
            var session = kit.CombatSession(view, d.Bridge);
            var allowed = new HashSet<string>(kit.CombatTools, StringComparer.Ordinal);
            var tools = session.Tools.Concat(StewardBoard.BoardTools(board)).Where(x => allowed.Contains(x.Name)).ToList();
            JToken planTok = JobsJson.Get(board, "plan");
            string plan = StewardBoard.Plain(Truthy(planTok) ? PyJson.Str(planTok) : "", 300);
            string user = string.Join("\n", new[]
            {
                "=== COMBAT TURN ===\nA fight is on. Look at the fight below and give the orders it needs now, or call do_nothing if the squad is fine.\n",
                "=== YOUR PLAN ===\n" + (plan.Length > 0 ? plan : "none") + "\n",
                "=== THE FIGHT ===\n" + GameData.DataSection("combat", kit.RenderCombat(view)),
                "Decide now: call at most a couple of tools, then end with done.",
            });
            t["system_chars"] = PyStr.Len(system);
            t["user_chars"] = PyStr.Len(user);
            int notes = 0;

            Task<string> validator(string tool, JObject v)
            {
                if (tool == "note" && notes >= MaxNotes) return Task.FromResult("at most " + MaxNotes + " notes per turn");
                string text = string.Join(" ", v.Properties().Where(p => p.Value.Type == JTokenType.String).Select(p => (string)p.Value));
                if (AgeGate.Violation(text, false, d.MinorTokens())) return Task.FromResult("not allowed: nothing romantic or sexual involving a minor");
                return Task.FromResult<string>(null);
            }

            async Task<JToken> executor(string tool, JObject v)
            {
                if (tool == "do_nothing") return null;
                if (tool == "note")
                {
                    string line = CleanLine(Need(v, "text"), 160);
                    if (line == null) return new JObject { ["error"] = "empty note" };
                    notes++;
                    if (await SayAsync("note", line).ConfigureAwait(false)) Stats.Notes++;
                    return new JObject { ["done"] = "note posted" };
                }
                var outc = await session.RunAsync(tool, v).ConfigureAwait(false);
                if (outc.Ok) Stats.Acts++;
                else if (outc.Result.ContainsKey("refused")) Stats.Refused++;
                return outc.Result;
            }

            var harness = new ToolHarness(d.ToolBackend, tools, d.ToolMode, null, CombatSteps, 2, CombatBudgetS, CombatStepTimeoutS);
            var allowedSorted = allowed.OrderBy(x => x, StringComparer.Ordinal).ToList();
            LoopResult res = await harness.RunAsync(system, user, executor, validator, null, o =>
            {
                o.Allowed = allowedSorted;
                o.Slot = d.Cfg.NarratorSlot;
                o.FinalMax = CombatFinalMax;
                o.MaxTokens = CombatMaxTokens;
            }).ConfigureAwait(false);
            t["outcome"] = res.Outcome;
            t["reason_ended"] = res.Reason;
            t["protocol"] = res.Protocol;
            t["failures"] = new JArray(res.Failures.Skip(Math.Max(0, res.Failures.Count - 4)).Cast<object>().ToArray());
            t["executed"] = new JArray(res.Executed.Select(e => (object)new JArray(e.Tool, e.Args, e.Result)).ToArray());
            t["elapsed_s"] = Math.Round(res.ElapsedS, 2);
        }

        /// <summary>A trade turn: GET /digest, a short prompt with the trade view first, trade_offer and trade, four steps. Skipped (and
        /// the trader stays seen) when food is critical or a fight is on. No board edits, no closing chat line.</summary>
        internal async Task RunTradeAsync(JObject t)
        {
            var d = D;
            var kit = (IStewardTradeKit)Kit;
            JObject digest, board;
            try
            {
                digest = await d.Bridge.DigestAsync().ConfigureAwait(false);
                board = await d.Bridge.GoalsAsync().ConfigureAwait(false);
            }
            catch (BridgeError e)
            {
                t["error"] = "read failed: " + e.Message;
                Stats.Failed++;
                return;
            }
            string skip = CombatOn || Truthy(JobsJson.Get(JobsJson.Get(Vv, "combat") as JObject ?? new JObject(), "active")) ? "a fight is on" : kit.TradeSkip(digest);
            IStewardSession session = string.IsNullOrEmpty(skip) ? kit.TradeSession(digest, d.Bridge) : null;
            if (session != null)
            {
                var names = new HashSet<string>(session.Tools.Select(x => x.Name), StringComparer.Ordinal);
                if (!(names.Contains("trade_offer") && names.Contains("trade"))) skip = "no trader that can be dealt with now";
            }
            if (!string.IsNullOrEmpty(skip))
            {
                t["skipped"] = skip;
                LlmLog.Info("rimworldllm.steward", "steward: trade turn skipped: " + skip);
                return;
            }
            var (view, food) = kit.RenderTrade(digest, TradeNew);
            string system = kit.TradeSystemPrompt() + "\n\n" + GameData.DataRule;
            var allowed = new HashSet<string>(kit.TradeTools, StringComparer.Ordinal);
            var tools = session.Tools.Concat(StewardBoard.BoardTools(board)).Where(x => allowed.Contains(x.Name)).ToList();
            JToken planTok = JobsJson.Get(board, "plan");
            string plan = StewardBoard.Plain(Truthy(planTok) ? PyJson.Str(planTok) : "", 300);
            string user = string.Join("\n", new[]
            {
                "=== THE TRADE ===\n" + GameData.DataSection("trade", view),
                food.Length > 0 ? food + "\n" : "",
                "=== YOUR PLAN ===\n" + (plan.Length > 0 ? plan : "none") + "\n",
                "Decide now: look at the offer, make the deal this trader is worth, then end with done.",
            });
            t["system_chars"] = PyStr.Len(system);
            t["user_chars"] = PyStr.Len(user);
            int notes = 0;

            Task<string> validator(string tool, JObject v)
            {
                if (tool == "note" && notes >= MaxNotes) return Task.FromResult("at most " + MaxNotes + " notes per turn");
                string text = string.Join(" ", v.Properties().Where(p => p.Value.Type == JTokenType.String).Select(p => (string)p.Value));
                if (AgeGate.Violation(text, false, d.MinorTokens())) return Task.FromResult("not allowed: nothing romantic or sexual involving a minor");
                return Task.FromResult<string>(null);
            }

            async Task<JToken> executor(string tool, JObject v)
            {
                if (tool == "do_nothing") return null;
                if (tool == "note")
                {
                    string line = CleanLine(Need(v, "text"), 160);
                    if (line == null) return new JObject { ["error"] = "empty note" };
                    notes++;
                    if (await SayAsync("note", line).ConfigureAwait(false)) Stats.Notes++;
                    return new JObject { ["done"] = "note posted" };
                }
                var outc = await session.RunAsync(tool, v).ConfigureAwait(false);
                if (outc.Ok) Stats.Acts++;
                else if (outc.Result.ContainsKey("refused")) Stats.Refused++;
                return outc.Result;
            }

            var harness = new ToolHarness(d.ToolBackend, tools, d.ToolMode, null, TradeSteps, 2, TradeBudgetS, TradeStepTimeoutS);
            var allowedSorted = allowed.OrderBy(x => x, StringComparer.Ordinal).ToList();
            LoopResult res = await harness.RunAsync(system, user, executor, validator, null, o =>
            {
                o.Allowed = allowedSorted;
                o.Slot = d.Cfg.NarratorSlot;
                o.FinalMax = TradeFinalMax;
                o.MaxTokens = TradeMaxTokens;
            }).ConfigureAwait(false);
            t["outcome"] = res.Outcome;
            t["reason_ended"] = res.Reason;
            t["protocol"] = res.Protocol;
            t["failures"] = new JArray(res.Failures.Skip(Math.Max(0, res.Failures.Count - 4)).Cast<object>().ToArray());
            t["executed"] = new JArray(res.Executed.Select(e => (object)new JArray(e.Tool, e.Args, e.Result)).ToArray());
            t["elapsed_s"] = Math.Round(res.ElapsedS, 2);
        }

        private bool LivePartialOk(string text)
        {
            return !AgeGate.Violation(StewardBoard.Plain(text, 500), false, D.MinorTokens());
        }

        private async Task RefreshLoop(StewardLiveSink sink, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LiveRefresh, ct).ConfigureAwait(false);
                sink.Refresh();
            }
        }

        private string LogLine(string reason, List<JObject> chat, List<(string Verb, string Note)> acts, Edits edits)
        {
            string label = reason == "chat" ? "Asked" : reason == "heartbeat" ? "Routine look" : reason == "paused" ? "After my pause" : reason == "combat over" ? "After the fight" : reason == "trade" ? "Trade turn" : "On " + reason;
            if (reason == "chat" && chat.Count > 0) label += " (" + StewardBoard.Plain(JobsJson.Str(chat[chat.Count - 1], "text", ""), 50) + ")";
            var did = acts.Select(a => a.Note).ToList();
            if (edits.Any) did.Add("updated the goals board");
            return StewardBoard.Plain(label + ": " + string.Join("; ", did), 300);
        }

        private async Task SaveBoardAsync(JObject board, Edits edits, string reason, List<JObject> chat, List<(string Verb, string Note)> acts, JObject t)
        {
            var bridge = D.Bridge;
            string line = LogLine(reason, chat, acts, edits);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                JObject fields;
                try { fields = edits.Any ? edits.Apply(board) : new JObject(); }
                catch (PyValueError e)                           // the board changed under us and an edit no longer fits: keep the log
                {
                    LlmLog.Info("rimworldllm.steward", "steward: board edit dropped: " + e.Message);
                    fields = new JObject();
                }
                var body = new JObject { ["log_add"] = new JArray(line) };
                foreach (var p in fields.Properties()) body[p.Name] = p.Value;
                try
                {
                    await bridge.PutGoalsAsync((int)JobsJson.PyInt(JobsJson.Get(board, "rev") ?? new JValue(0)), body).ConfigureAwait(false);
                    t["board_saved"] = true;
                    return;
                }
                catch (BridgeError e)
                {
                    if (e.Code != "conflict" || attempt > 0)
                    {
                        LlmLog.Warning("rimworldllm.steward", "steward: goals board not saved: " + e.Message);
                        return;
                    }
                    try { board = await bridge.GoalsAsync().ConfigureAwait(false); }      // stale rev: take the player's board and replay our edits on it
                    catch (BridgeError) { return; }
                }
            }
        }

        private string UserText(string reason, List<JObject> chat, List<string> events, JObject board, string digestText, bool advisor, string alerts = "")
        {
            var st = JobsJson.Get(Vv, "settings") as JObject ?? new JObject();
            var parts = new List<string>();
            if (advisor)
                parts.Add("=== MODE ===\nThe narrator is not playing: you are only an advisor. Act only on what the player's message explicitly asks, "
                          + "at most one task, and say what you did.\n");
            var why = new List<string>();
            if (chat.Count > 0)
                why.Add("The player said in the chat:\n" + string.Join("\n", chat.Select(c => "> " + StewardBoard.Plain(JobsJson.Str(c, "text", ""), 500))));
            if (reason == "heartbeat") why.Add("Routine look: time has passed since you last checked. Look for what needs doing.");
            else if (reason == "event") why.Add("Something important happened (see below). Deal with it first.");
            else if (reason == "combat started")
                why.Add("A fight has started (see COMBAT in the state below). Act now: draft the fighters and put them in cover or at the chokepoint, "
                    + "send non-fighters indoors, then plan defences if there is time. Leave the long-term work for later.");
            else if (reason == "combat over")
                why.Add("The fight is over. Undraft the fighters (squad undraft), rescue the downed, make sure the wounded are tended, "
                    + "check fires, and send everyone back to work. Then the routine look.");
            else if (reason == "dialog") why.Add("A window or letter is waiting for a choice (see OPEN DIALOGS in the state). Answer it with choose.");
            else if (reason == "paused")
                why.Add("The game is paused because you paused it for an emergency. If it is handled, unpause with set_time (pause no); otherwise carry on.");
            if (events.Count > 0) why.Add("Since you last looked:\n" + string.Join("\n", events.Select(x => "- " + x)));
            parts.Add("=== WHY YOU WOKE ===\n" + string.Join("\n", why) + "\n");
            if (PyStr.Strip(alerts).Length > 0) parts.Add(alerts.TrimEnd('\n') + "\n");
            var sets = new List<string>();
            JToken mayPause = JobsJson.Get(st, "may_pause_for_emergencies");
            sets.Add(Truthy(JobsJson.Get(st, "controls_speed")) ? "You may change the game speed or pause it whenever you like."
                : (mayPause == null || Truthy(mayPause)
                    ? "You may not change the game speed. You may pause only in a real emergency (set_time with emergency yes)."
                    : "You may not pause the game or change its speed."));
            if (Truthy(JobsJson.Get(Vv, "paused_by_narrator"))) sets.Add("The game is paused by you.");
            parts.Add("=== SETTINGS ===\n" + string.Join(" ", sets) + "\n");
            parts.Add("=== YOUR GOALS BOARD ===\n" + StewardBoard.RenderBoard(board) + "\n");
            parts.Add("=== THE STATE OF THE GAME ===\n" + GameData.DataSection("digest", digestText));
            parts.Add("Decide now: call the tools you need, then end with one short chat line for the player (or done).");
            return string.Join("\n", parts);
        }
    }

    /// <summary>NEW: turns the stream events of a cycle's model calls into IChatLive calls: thinking at the first Sent/Queued, progress,
    /// typing at the first token, the partial final line throttled to at most 8 a second, and a refresh at least every 60 s.</summary>
    internal sealed class StewardLiveSink : ILlmStreamSink
    {
        private readonly IChatLive _live;
        private readonly Func<double> _clock;
        private readonly double _partialGap;
        private readonly Func<string, bool> _partialOk;
        private readonly object _gate = new object();
        private string _id;
        private bool _typing, _ended, _blocked;
        private double _lastProgressAt = double.NegativeInfinity, _lastPartialAt = double.NegativeInfinity, _progress = -1;
        private string _lastText, _shown;
        public bool Away { get; private set; }

        public StewardLiveSink(IChatLive live, Func<double> clock, double partialGap, Func<string, bool> partialOk)
        {
            _live = live; _clock = clock; _partialGap = partialGap; _partialOk = partialOk;
        }

        public string Id { get { lock (_gate) return _id; } }

        private void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { LlmLog.Debug("rimworldllm.steward", "steward: live chat call failed: " + e.Message); }
        }

        public void EnsureBegun()
        {
            lock (_gate)
            {
                if (_id != null || _ended) return;
                Safe(() => _id = _live.Begin("narrator", null, "Narrator"));
            }
        }

        public void OnEvent(StreamEvent e)
        {
            lock (_gate)
            {
                if (_ended) return;
                switch (e.Kind)
                {
                    case StreamEventKind.Sent:
                    case StreamEventKind.Queued:
                        EnsureBegunLocked();
                        if (_typing)                          // a later step of the loop: back to thinking, no figure
                        {
                            _typing = false;
                            _lastText = null;
                            _progress = -1;
                            if (_id != null) Safe(() => _live.Progress(_id, -1));
                        }
                        break;
                    case StreamEventKind.Progress:
                        EnsureBegunLocked();
                        if (_typing || _id == null || e.Total <= 0) break;
                        _progress = Math.Max(0.0, Math.Min(1.0, (double)e.Processed / e.Total));
                        double now = _clock();
                        if (now - _lastProgressAt >= 0.25)
                        {
                            _lastProgressAt = now;
                            Safe(() => _live.Progress(_id, _progress));
                        }
                        break;
                    case StreamEventKind.FirstToken:
                        EnsureBegunLocked();
                        if (_id != null && !_typing)
                        {
                            _typing = true;
                            Safe(() => _live.Typing(_id));
                        }
                        break;
                    case StreamEventKind.Partial:
                        EnsureBegunLocked();
                        PartialLocked(FinalText.Decode(e.Text), false);
                        break;
                    case StreamEventKind.Done:
                        PartialLocked(_lastText, true);       // the trailing partial the throttle held back
                        break;
                    case StreamEventKind.Error:
                        if (e.Exception is BackendAway || e.Exception is LlmAway) Away = true;
                        break;
                }
            }
        }

        private void EnsureBegunLocked()
        {
            if (_id == null && !_ended) Safe(() => _id = _live.Begin("narrator", null, "Narrator"));
        }

        private void PartialLocked(string decoded, bool flush)
        {
            if (_id == null || _blocked || string.IsNullOrEmpty(decoded)) return;
            string text = PyStr.Head(decoded, 500);
            if (!flush)
            {
                if (!_partialOk(text)) { _blocked = true; return; }      // never show what the age gate would drop
                _lastText = text;
            }
            if (text == _shown) return;
            double now = _clock();
            if (!flush && now - _lastPartialAt < _partialGap) return;
            _lastPartialAt = now;
            _shown = text;
            Safe(() => _live.Partial(_id, text));
        }

        /// <summary>Re-send the live state so the mod keeps the stream (it drops one silent for 90 s).</summary>
        public void Refresh()
        {
            lock (_gate)
            {
                if (_ended || _id == null) return;
                if (_typing && _shown != null) Safe(() => _live.Partial(_id, _shown));
                else Safe(() => _live.Progress(_id, _progress));
            }
        }

        /// <summary>The cycle is over. finalLine posted: End with it. away: End with the paused note. Otherwise Cancel (no line).</summary>
        public void Finish(string finalLine, bool away, IChatLive live)
        {
            lock (_gate)
            {
                if (_ended) return;
                _ended = true;
                if (_id == null) return;
                if (finalLine != null) Safe(() => live.End(_id, finalLine));
                else if (away) Safe(() => live.End(_id, Steward.PausedNote));
                else Safe(() => live.Cancel(_id));
            }
        }
    }
}

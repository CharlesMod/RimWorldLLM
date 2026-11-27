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
using RimWorldLLM.Mind.RimWorld;
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
    }

    /// <summary>Wraps the RimWorld StewardKit (the adapter's StewardKit object) as an IStewardKit.</summary>
    public sealed class RimWorldStewardKit : IStewardKit
    {
        private readonly StewardKit _k;
        public RimWorldStewardKit(StewardKit k) { _k = k; }
        public string EventLine(JObject e) { return _k.EventLine(e); }
        public string Wake(JObject e) { return _k.Wake(e); }
        public string DefaultStrategy() { return _k.DefaultStrategy(); }
        public string SystemPrompt(bool advisor) { return _k.SystemPrompt(advisor); }
        public IStewardSession Session(JObject digest, IBridge bridge, bool advisor) { return new Sess(_k.Session(digest, bridge, advisor)); }
        public string PlaybookAlerts(JObject digest) { return _k.PlaybookAlerts(digest); }
        public string RenderDigest(JObject digest) { return _k.RenderDigest(digest); }

        private sealed class Sess : IStewardSession
        {
            private readonly StewardSession _s;
            public Sess(StewardSession s) { _s = s; }
            public IReadOnlyList<ToolSpec> Tools { get { return _s.Tools; } }
            public ISet<string> Acting { get { return _s.Acting; } }
            public async Task<StewardOutcome> RunAsync(string tool, JObject values)
            {
                var o = await _s.RunAsync(tool, values).ConfigureAwait(false);
                return new StewardOutcome { Result = o.Result, Verb = o.Verb, Note = o.Note, Ok = o.Ok };
            }
        }
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

        public Steward(IStewardHost d)
        {
            D = d;
            object k = d.Adapter.StewardKit;
            Kit = k as IStewardKit ?? (k is StewardKit rk ? new RimWorldStewardKit(rk) : null);
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
                try { text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(ExpandUser(path))); }
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
            if (!string.IsNullOrEmpty(why))
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
            }
            if (!Truthy(JobsJson.Get(vv, "paused_by_narrator"))) PauseWoken = false;
            if (Active()) return;
            string reason = Wake(playing);
            if (reason != null) Start(reason, playing);
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
            if (!playing || (env.Paused && !Truthy(JobsJson.Get(Vv, "paused_by_narrator")))) return null;
            if (SwitchedOn)
            {
                SwitchedOn = false;
                if (LastGame != null) return "switched on";      // never looked yet: the heartbeat below fires anyway
            }
            if (LastReal != null && d.Clock() - LastReal.Value < d.Cfg.StewardMinGapRealS) return null;
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
            var st = JobsJson.Get(Vv, "settings") as JObject;
            JToken hh = st == null ? null : JobsJson.Get(st, "heartbeat_hours");
            double hours = Truthy(hh) ? Convert.ToDouble(hh.Type == JTokenType.String ? (object)double.Parse((string)hh, System.Globalization.CultureInfo.InvariantCulture) : (double)hh) : 6.0;
            double hb = hours * d.Day / 24;
            if (LastGame == null || env.Game < LastGame || env.Game - LastGame.Value >= hb) return "heartbeat";
            return null;
        }

        private void Start(string reason, bool playing)
        {
            var d = D;
            var chat = PendingChat;
            PendingChat = new List<JObject>();
            var events = playing ? Events : new List<string>();
            Events = new List<string>();
            Urgent = new List<string>();
            d.Leases.NarratorOwner = "steward";                  // idle narrator work yields from here on
            if (d.Idle != null && d.Idle.Active() && (d.Idle.Kind == "chronicle" || d.Idle.Kind == "art"))
                d.Idle.Preempt("steward");                       // a running slot-0 job is cancelled; nothing it made is applied
            LastGame = d.Bridge.Env.Game;
            LastReal = d.Clock();
            Task = d.Spawn(() => CycleAsync(reason, chat, events, playing));
        }

        // ---- a cycle -----------------------------------------------------------------------
        private async Task CycleAsync(string reason, List<JObject> chat, List<string> events, bool playing)
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
            string system = kit.SystemPrompt(advisor) + (strategy.Length > 0 ? "\n\n" + StrategyHeading + "\n" + strategy : "") + "\n\n" + GameData.DataRule;
            var session = kit.Session(digest, d.Bridge, advisor);
            var tools = session.Tools.Concat(StewardBoard.BoardTools(board, advisor)).ToList();
            HashSet<string> allowed = advisor
                ? new HashSet<string>(session.Tools.Select(x => x.Name)) { "note", "add_order", "do_nothing" }
                : new HashSet<string>(tools.Select(x => x.Name));
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

            var harness = new ToolHarness(d.ToolBackend, tools, d.ToolMode, null, Steps, 2, BudgetS, StepTimeoutS);
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
                }).ConfigureAwait(false);
            }
            catch
            {
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
            string label = reason == "chat" ? "Asked" : reason == "heartbeat" ? "Routine look" : reason == "paused" ? "After my pause" : "On " + reason;
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

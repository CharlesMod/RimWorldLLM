// Port of agent/rimworldllm_agent/core/commentary.py: the documentary voice. One wry narrator sentence at a big moment, and one
// recap sentence per finished game day.
//
// note(event): the director hands every event; the adapter's CommentaryMoment turns it into a Moment (kind, dedupe key, text, weight,
// urgent) or null. Moments below MinWeight are ignored, a repeat of the same key inside DedupeGame is ignored, the pending list is capped.
// RunOnce() (every step): speak at most one pending moment, else the recap of a finished game day. Only while the vivarium is there,
// the narrator plays and commentary is on, the narrator slot is free and no idle job holds it. Gates: commentary_min_gap_real_s real
// seconds since the last line AND commentary_min_gap_game game units; an urgent moment skips both gaps but never the
// UrgentFloorRealS floor. The recap needs only the floor.
// PreemptIfWanted(): a steward cycle owns slot 0 from its start; the running call is cancelled and nothing is posted.
//
// Python's asyncio.Task.cancel() is a CancellationTokenSource here; the LLM wait goes through WaitAsync(token) (as the idle jobs do).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>One documentary moment (commentary.Moment).</summary>
    public sealed class Moment
    {
        public readonly string Kind;
        /// <summary>dedupe key: kind plus who/what</summary>
        public readonly string Key;
        /// <summary>what happened, one line (cleaned before it reaches a prompt)</summary>
        public readonly string Text;
        /// <summary>0..1 interest</summary>
        public readonly double Weight;
        public readonly bool Urgent;
        public readonly int Game;
        /// <summary>a minor is in it: the age gate sees it</summary>
        public readonly bool Minor;

        public Moment(string kind, string key, string text, double weight, bool urgent, int game, bool minor = false)
        {
            Kind = kind; Key = key; Text = text; Weight = weight; Urgent = urgent; Game = game; Minor = minor;
        }
    }

    /// <summary>One line that was said (commentary.log entries).</summary>
    public sealed class CommentaryLine
    {
        public string Mode;
        public string Kind;
        public int Day;
        public string Text;
    }

    /// <summary>The director members commentary.py reads.</summary>
    public interface ICommentaryHost
    {
        MindConfig Cfg { get; }
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        LeaseManager Leases { get; }
        IGameAdapter Adapter { get; }
        int Day { get; }
        Now Now();
        Task Spawn(Func<Task> work);
        IList<(int Actor, string Reason)> Dropped { get; }
        IDictionary<int, HashSet<string>> NameTokens { get; }
        HashSet<string> MinorTokens();
        bool IsMinor(int actorId);
        /// <summary>Whether an idle job is running (d.idle.active()).</summary>
        bool IdleActive { get; }
        /// <summary>d.steward.vv: the last /vivarium reply ({} before any).</summary>
        JObject StewardVv { get; }
    }

    public sealed class Commentary
    {
        public const double MinWeight = 0.4;           // a moment lighter than this is never spoken about
        public const int DedupeGame = 2500;            // same key again within this many units (about an hour) is dropped
        public const int MaxPending = 8;
        public const int StaleGame = 30000;            // a non-urgent moment older than this (half a day) is no longer news
        public const double UrgentFloorRealS = 30.0;   // nothing is spoken sooner than this after the last line, urgent or not
        public const int RecapEvents = 6;              // notable events a recap is built from (heaviest first, told in order)
        public const int DayKeep = 24;                 // moments remembered for the recap of a day

        private readonly ICommentaryHost _d;
        public List<Moment> Pending { get; } = new List<Moment>();
        /// <summary>key -> game time last accepted</summary>
        public Dictionary<string, int> Seen { get; } = new Dictionary<string, int>();
        public Dictionary<int, List<Moment>> DayMoments { get; } = new Dictionary<int, List<Moment>>();
        /// <summary>a finished day whose recap is owed</summary>
        public int? RecapDay;
        public HashSet<int> Recapped { get; } = new HashSet<int>();
        public double LastReal = double.NegativeInfinity;
        public long LastGame = -1000000000L;
        public Task Task;
        private CancellationTokenSource _cts;
        /// <summary>the mod refused who=comment</summary>
        public bool Unsupported;
        /// <summary>what was said</summary>
        public List<CommentaryLine> Log { get; } = new List<CommentaryLine>();
        public List<string> Drops { get; } = new List<string>();

        public Commentary(ICommentaryHost d) { _d = d; }

        /// <summary>A new save: nothing pending, no day remembered, no gap owed.</summary>
        public void Reset()
        {
            Pending.Clear(); Seen.Clear(); DayMoments.Clear(); Recapped.Clear();
            RecapDay = null; Task = null; _cts = null;
            LastReal = double.NegativeInfinity; LastGame = -1000000000L;
            _d.Leases.NarratorBusy = false;
        }

        // ---- intake -----------------------------------------------------------
        public bool Enabled()
        {
            var d = _d;
            if (Unsupported || !d.Cfg.Commentary) return false;
            var vv = d.StewardVv;
            if (vv == null || vv.Count == 0 || !PyJson.Truthy(vv["playing"])) return false;
            var settings = vv["settings"] as JObject;
            JToken c = settings == null ? null : settings["commentary"];
            return c == null || PyJson.Truthy(c);
        }

        public void Note(JObject e)
        {
            if (!_d.Cfg.Commentary) return;
            Moment m = _d.Adapter.CommentaryMoment(e, _d);
            if (m == null || m.Weight < MinWeight) return;
            int prev;
            if (m.Game - (Seen.TryGetValue(m.Key, out prev) ? prev : -1000000000) < DedupeGame) return;
            Seen[m.Key] = m.Game;
            if (Seen.Count > 200)
            {
                foreach (var k in Seen.OrderBy(kv => kv.Value).Take(100).Select(kv => kv.Key).ToList()) Seen.Remove(k);
            }
            Pending.Add(m);
            while (Pending.Count > MaxPending) Pending.RemoveAt(0);
            int day = (int)PyFmt.FloorDiv(m.Game, _d.Day);
            List<Moment> list;
            if (!DayMoments.TryGetValue(day, out list)) DayMoments[day] = list = new List<Moment>();
            list.Add(m);
            while (list.Count > DayKeep) list.RemoveAt(0);
            foreach (int old in DayMoments.Keys.Where(k => k < day - 2).ToList()) DayMoments.Remove(old);
        }

        // ---- running ----------------------------------------------------------
        public bool Active() { return Task != null && !Task.IsCompleted; }

        public void PreemptIfWanted()
        {
            var d = _d;
            if (Active() && (d.Leases.NarratorOwner != null || !Enabled()))
            {
                if (_cts != null) _cts.Cancel();
                Task = null;
                d.Leases.NarratorBusy = false;
                d.Dropped.Add((-1, "commentary_preempted"));
            }
        }

        private Moment Pick(long nowGame, double realGap)
        {
            var c = _d.Cfg;
            Pending.RemoveAll(m => !(m.Urgent || nowGame - m.Game <= StaleGame));
            if (realGap < UrgentFloorRealS) return null;
            bool gameGapOk = nowGame - LastGame >= c.CommentaryMinGapGame;
            bool realOk = realGap >= c.CommentaryMinGapRealS;
            Moment best = null;
            foreach (var m in Pending)
            {
                if (!(m.Urgent || (gameGapOk && realOk))) continue;
                if (best == null || m.Weight > best.Weight || (m.Weight == best.Weight && m.Game > best.Game)) best = m;
            }
            return best;
        }

        public void RunOnce()
        {
            var d = _d;
            if (Active()) return;
            int today = (int)PyFmt.FloorDiv(d.Bridge.Env.Game, d.Day);
            if (RecapDay == null && d.Cfg.CommentaryRecap)
            {
                var done = DayMoments.Keys.Where(k => k < today && !Recapped.Contains(k)).ToList();
                if (done.Count > 0)
                {
                    RecapDay = done.Max();
                    Recapped.UnionWith(done);          // older unspoken days are dropped: one line per day, the latest
                }
            }
            if (Pending.Count == 0 && RecapDay == null) return;
            if (!Enabled() || !d.Leases.NarratorMayDoIdleWork() || d.IdleActive) return;
            double realGap = d.Now().Real - LastReal;
            Moment m = Pick(d.Bridge.Env.Game, realGap);
            if (m != null)
            {
                Pending.Remove(m);
                int mday = (int)PyFmt.FloorDiv(m.Game, d.Day);
                StartSpeak("moment", m.Kind, new List<Moment> { m }, mday);
            }
            else if (RecapDay != null && realGap >= UrgentFloorRealS)
            {
                int day = RecapDay.Value;
                RecapDay = null;
                List<Moment> ms;
                if (!DayMoments.TryGetValue(day, out ms)) ms = new List<Moment>();
                if (ms.Count > 0) StartSpeak("recap", "recap", ms, day);
            }
        }

        private void StartSpeak(string mode, string kind, List<Moment> ms, int day)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            Task = _d.Spawn(() => SpeakAsync(mode, kind, ms, day, cts.Token));
        }

        // ---- one line ---------------------------------------------------------
        public List<LlmMessage> Messages(string mode, IList<Moment> ms, int day)
        {
            var a = _d.Adapter;
            string user;
            if (mode == "recap")
            {
                var top = ms.OrderByDescending(m => m.Weight).Take(RecapEvents).OrderBy(m => m.Game).ToList();
                string body = string.Join("\n", top.Select(m => "- " + GameData.Clean(m.Text, "event")));
                user = "=== THE DAY (day " + (day + 1) + ") ===\n" + GameData.DataSection("events", body) +
                       "=== REQUEST ===\n" + a.CommentaryRecapRequest;
            }
            else
            {
                string body = "- " + GameData.Clean(ms[0].Text, "event");
                user = "=== WHAT JUST HAPPENED ===\n" + GameData.DataSection("moment", body) +
                       "=== REQUEST ===\n" + a.CommentaryRequest;
            }
            return new List<LlmMessage>
            {
                new LlmMessage("system", a.CommentaryPrompt + "\n\n" + GameData.DataRule),
                new LlmMessage("user", user),
            };
        }

        private async Task SpeakAsync(string mode, string kind, List<Moment> ms, int day, CancellationToken tok)
        {
            var d = _d;
            d.Leases.NarratorBusy = true;
            try
            {
                var msgs = Messages(mode, ms, day);
                Completion c;
                try
                {
                    c = await d.Llm.CompleteAsync(msgs, d.Cfg.NarratorSlot, "narrator", 48, new[] { "\n" }).WaitAsync(tok).ConfigureAwait(false);
                }
                catch (LlmError) { return; }
                var names = new HashSet<string>();
                foreach (var t in d.NameTokens.Values) names.UnionWith(t);
                var s = Sanitize.SanitizeLine(c.Text, new HashSet<string>(), names, names,
                    promptWords: Sanitize.PromptCapWords(msgs[1].Text), commonWords: new HashSet<string>(d.Adapter.CommonWords),
                    involvesMinor: ms.Any(m => m.Minor), minorNames: d.MinorTokens());
                if (!s.Ok)
                {
                    Drops.Add(kind + ":" + s.Reason);
                    d.Dropped.Add((-1, "commentary:" + (string.IsNullOrEmpty(s.Reason) ? "sanitize" : s.Reason)));
                    return;
                }
                try { await d.Bridge.PostChatAsync("comment", s.Text).WaitAsync(tok).ConfigureAwait(false); }
                catch (BridgeError e)
                {
                    if (e.Status == 400 || e.Status == 404) Unsupported = true;   // an older mod without who=comment: stay quiet
                    LlmLog.Warning("rimworldllm", "commentary not posted: " + e.Message);
                    return;
                }
                LastReal = d.Now().Real;
                LastGame = d.Bridge.Env.Game;
                Log.Add(new CommentaryLine { Mode = mode, Kind = kind, Day = day, Text = s.Text });
                while (Log.Count > 100) Log.RemoveAt(0);
            }
            finally
            {
                d.Leases.NarratorBusy = false;
                Task = null;
            }
        }
    }
}

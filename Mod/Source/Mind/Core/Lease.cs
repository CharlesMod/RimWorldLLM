// Port of agent/rimworldllm_agent/core/lease.py.
// Lease manager. Windows are slots 1..N, each leased to one actor at a time; slot 0 is the narrator.
// - A lease prefers the window its actor last held when free, else the first free one (SlotMap).
// - Priority: PLAYER > URGENT > REACTIVE > SOCIAL > AMBIENT. A higher class may end a lower-class lease early.
// - Expiry by class on a dual clock (game units and real seconds, whichever comes first).
// - Quotas per actor and per pair per game day; skip-if-nothing-new; ambient pause at high speed.
// - Narrator: idle-only work, and coverage when the windows are busy.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public enum Cls
    {
        Idle = 0,       // reflection and diaries: only in idle time, preempted by anything
        Ambient = 1,    // background chatter
        Social = 2,
        Reactive = 3,   // reactions to events that happened to someone
        Urgent = 4,     // the most pressing events
        Player = 5,     // player talk
    }

    public static class ClsExt
    {
        /// <summary>The Python enum member name (IDLE, AMBIENT, ...), used in log strings.</summary>
        public static string Name(this Cls c) { return c.ToString().ToUpperInvariant(); }
    }

    /// <summary>A request's life on the dual clock. Units: game units the moment stays on screen. RealS: hard cap in
    /// real play seconds. FloorRealS: the least real time a line is given; the game window is
    /// max(units, floor_real_s * units_per_second_now), never above the cap.</summary>
    public sealed class Expiry
    {
        public readonly long? Units;
        public readonly double? RealS;
        public readonly double FloorRealS;
        public Expiry(long? units, double? realS, double floorRealS = 0.0) { Units = units; RealS = realS; FloorRealS = floorRealS; }
    }

    /// <summary>game: game units. real: play seconds (real time with pauses taken out). rate: game units per real second now.</summary>
    public sealed class Now
    {
        public long Game;
        public double Real;
        public double Rate;
        public Now(long game, double real, double rate = 60.0) { Game = game; Real = real; Rate = rate; }
    }

    public sealed class WorkItem
    {
        public int Id;
        public int ActorId;
        public Cls Cls;
        public string Kind;                  // event type or "talk", "reflect", "diary"
        public Now Created;
        public long? ExpiresAt;
        public double? ExpiresReal;
        public JObject Event;
        public int? Listener;
        public JObject Payload = new JObject();
        public double Severity;
        public double FloorRealS;

        public WorkItem(int id, int actorId, Cls cls, string kind, Now created, long? expiresAt, double? expiresReal,
            JObject ev = null, int? listener = null, JObject payload = null, double severity = 0.0, double floorRealS = 0.0)
        {
            Id = id; ActorId = actorId; Cls = cls; Kind = kind; Created = created; ExpiresAt = expiresAt; ExpiresReal = expiresReal;
            Event = ev; Listener = listener; Payload = payload ?? new JObject(); Severity = severity; FloorRealS = floorRealS;
        }

        /// <summary>The game time after which the line is late, at the current speed: the game-time window or the real
        /// floor, whichever is longer.</summary>
        public long? Deadline(double rate)
        {
            if (ExpiresAt == null) return null;
            return Math.Max(ExpiresAt.Value, Created.Game + (long)(FloorRealS * rate));
        }

        public bool Expired(Now now)
        {
            var dl = Deadline(now.Rate);
            return (dl != null && now.Game >= dl.Value) || (ExpiresReal != null && now.Real >= ExpiresReal.Value);
        }
    }

    public sealed class Lease
    {
        public int Slot;
        public int ActorId;
        public Cls Cls;
        public string Kind;
        public Now Started;
        public double TouchedReal;
        public string ConversationId;
        public bool Generating;
        public bool Preempted;          // set when a higher class took the window; holder must stop
        public bool Ended;
        public string EndReason = "";
        public int TurnNo;
        public Dictionary<string, object> Data = new Dictionary<string, object>();   // prefix, turns, ... owned by the director
        internal long Seq;              // insertion order in LeaseManager.Leases

        public Lease(int slot, int actorId, Cls cls, string kind, Now started, double touchedReal)
        {
            Slot = slot; ActorId = actorId; Cls = cls; Kind = kind; Started = started; TouchedReal = touchedReal;
        }

        public bool IdleExpired(Now now) { return now.Real - TouchedReal > LeaseManager.LeaseIdleTtlS[Cls]; }
    }

    /// <summary>Per actor and per ordered pair per game day, plus an ambient gap on both clocks.</summary>
    public sealed class QuotaTracker
    {
        public int ActorPerDay, PairPerDay;
        public long Tpd;
        public double AmbientShare;     // ambient chatter may use this share of a day's lines; the rest is kept for social events
        public long MinGapGame;
        public double MinGapRealS;
        public Dictionary<(int, long), int> ActorCounts = new Dictionary<(int, long), int>();
        public Dictionary<(int, int, long), int> PairCounts = new Dictionary<(int, int, long), int>();
        public Dictionary<int, Now> LastSpoke = new Dictionary<int, Now>();

        public QuotaTracker(int actorPerDay = 12, int pairPerDay = 4, long unitsPerDay = 60000,
            long minGapGame = 1500, double minGapRealS = 20.0, double ambientShare = 0.75)
        {
            ActorPerDay = actorPerDay; PairPerDay = pairPerDay; Tpd = unitsPerDay;
            AmbientShare = ambientShare; MinGapGame = minGapGame; MinGapRealS = minGapRealS;
        }

        long Day(long game) { return PyFmt.FloorDiv(game, Tpd); }

        public (bool, string) Allowed(int actor, int? other, Cls cls, Now now)
        {
            if (cls >= Cls.Reactive) return (true, "");   // reactive and above bypass quotas
            long day = Day(now.Game);
            ActorCounts.TryGetValue((actor, day), out int used);
            if (used >= ActorPerDay) return (false, "actor_quota");
            if (cls <= Cls.Ambient && used >= (int)(ActorPerDay * AmbientShare)) return (false, "ambient_quota");
            if (other != null)
            {
                PairCounts.TryGetValue((Math.Min(actor, other.Value), Math.Max(actor, other.Value), day), out int pc);
                if (pc >= PairPerDay) return (false, "pair_quota");
            }
            if (LastSpoke.TryGetValue(actor, out var last) && cls <= Cls.Social)
            {
                if (now.Game - last.Game < MinGapGame && now.Real - last.Real < MinGapRealS)
                    return (false, "gap");   // both clocks say too soon
            }
            return (true, "");
        }

        /// <summary>A line was spoken. The pair's count is per talk, not per line: a conversation passes pair=false after
        /// its first turn.</summary>
        public void Record(int actor, int? other, Now now, bool pair = true)
        {
            long day = Day(now.Game);
            ActorCounts.TryGetValue((actor, day), out int c);
            ActorCounts[(actor, day)] = c + 1;
            if (other != null && pair)
            {
                var k = (Math.Min(actor, other.Value), Math.Max(actor, other.Value), day);
                PairCounts.TryGetValue(k, out int pc);
                PairCounts[k] = pc + 1;
            }
            LastSpoke[actor] = now;
        }
    }

    /// <summary>How much the community speaks per real minute, whatever the game speed: a token bucket on the play
    /// clock. A request below URGENT waits in the queue until a token is free.</summary>
    public sealed class VoiceBudget
    {
        public double PerMin, Burst, FastScale, FastRate;
        public double Tokens;
        double? _last;

        public VoiceBudget(double perMin = 5.0, double burst = 5.0, double fastScale = 0.5, double fastRate = 600.0)
        {
            PerMin = perMin; Burst = burst; FastScale = fastScale; FastRate = fastRate;
            Tokens = burst;
        }

        public void Refill(Now now)
        {
            if (_last != null && now.Real > _last.Value)
            {
                double k = now.Rate >= FastRate ? FastScale : 1.0;
                Tokens = Math.Min(Burst, Tokens + (now.Real - _last.Value) * PerMin / 60.0 * k);
            }
            _last = _last == null ? now.Real : Math.Max(_last.Value, now.Real);
        }

        public bool Ready(Now now, double cost = 1.0)
        {
            Refill(now);
            return Tokens >= cost;
        }

        public void Spend(Now now, double n = 1.0)
        {
            Refill(now);
            Tokens = Math.Max(-Burst, Tokens - n);
        }
    }

    public sealed class LeaseManager
    {
        public const int MaxQueue = 16;
        public const double AmbientReserve = 3.0;   // tokens that must be in the budget before ambient chatter may start

        // player never; urgent 600 units (5 s cap); event reactions 3,000 units (60 s cap); social and ambient 1,200 units (20 s cap).
        public static readonly Dictionary<Cls, Expiry> RequestExpiry = new Dictionary<Cls, Expiry>
        {
            [Cls.Idle] = new Expiry(null, null),
            [Cls.Player] = new Expiry(null, null),
            [Cls.Urgent] = new Expiry(600, 5.0, 3.0),
            [Cls.Reactive] = new Expiry(3000, 60.0, 6.0),
            [Cls.Social] = new Expiry(1200, 20.0, 4.0),
            [Cls.Ambient] = new Expiry(1200, 20.0, 4.0),
        };

        // how long a lease may sit without activity before it ends by rule
        public static readonly Dictionary<Cls, double> LeaseIdleTtlS = new Dictionary<Cls, double>
        {
            [Cls.Idle] = 300.0, [Cls.Player] = 300.0, [Cls.Urgent] = 30.0, [Cls.Reactive] = 30.0,
            [Cls.Social] = 40.0, [Cls.Ambient] = 15.0,
        };

        public VoiceBudget Budget;
        public Func<string, Cls> Priority;
        public List<int> ActorSlots;
        public int NarratorSlot;
        public SlotMap Slots;
        public Dictionary<int, Lease> Leases = new Dictionary<int, Lease>();   // slot -> lease (use LeasesInOrder for Python's order)
        public List<WorkItem> Queue = new List<WorkItem>();
        public QuotaTracker Quotas;
        public int PauseAmbientAtSpeed;
        int _ids;
        long _leaseSeq;
        public Dictionary<int, string> LastSignature = new Dictionary<int, string>();   // skip-if-nothing-new
        public bool NarratorBusy;
        public string NarratorOwner;   // "steward" while a steward cycle holds slot 0: idle narrator work yields
        public List<(string, string)> Log = new List<(string, string)>();   // (what, why) trail for the debug view and tests

        public LeaseManager(IEnumerable<int> actorSlots, int narratorSlot = 0, QuotaTracker quotas = null,
            int pauseAmbientAtSpeed = 4, Func<string, Cls> priority = null, VoiceBudget budget = null)
        {
            Budget = budget ?? new VoiceBudget(1e9, 1e9);   // unlimited unless the director sets one
            Priority = priority ?? (kind => Cls.Ambient);
            ActorSlots = actorSlots.ToList();
            NarratorSlot = narratorSlot;
            Slots = new SlotMap(ActorSlots, narratorSlot);
            Quotas = quotas ?? new QuotaTracker();
            PauseAmbientAtSpeed = pauseAmbientAtSpeed;
        }

        /// <summary>The leases in the order they were taken (Python dict iteration order).</summary>
        public List<Lease> LeasesInOrder() { return Leases.Values.OrderBy(l => l.Seq).ToList(); }

        // ---- requests -------------------------------------------------------
        public bool AmbientPaused(int speed) { return speed >= PauseAmbientAtSpeed; }

        static double EventSeverity(JObject ev)
        {
            var t = ev?["severity"];
            if (t == null) return 0.0;
            if (t.Type == JTokenType.String) return double.Parse(((string)t).Trim(), CultureInfo.InvariantCulture);
            return (double)t;
        }

        public WorkItem MakeItem(int actorId, string kind, Now now, JObject ev = null, int? listener = null, Cls? cls = null,
            JObject payload = null, double? severity = null, long? units = null, double? floorRealS = null)
        {
            Cls c = cls ?? Priority(kind);
            var ex = RequestExpiry[c];
            long? u = (units != null && ex.Units != null) ? units : ex.Units;
            double fl = floorRealS ?? ex.FloorRealS;
            double sev = severity ?? EventSeverity(ev);
            return new WorkItem(++_ids, actorId, c, kind, now,
                u == null ? (long?)null : now.Game + Math.Max(u.Value, (long)(fl * now.Rate)),
                ex.RealS == null ? (double?)null : now.Real + ex.RealS.Value,
                ev, listener, payload ?? new JObject(), sev, fl);
        }

        static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null: return false;
                case JTokenType.String: return ((string)t).Length > 0;
                case JTokenType.Integer: return (long)t != 0;
                case JTokenType.Float: return (double)t != 0.0;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                case JTokenType.Object: return ((JObject)t).Count > 0;
                default: return true;
            }
        }

        /// <summary>Test seam (Python tests wrap leases.submit): called with every submitted item before it is judged.</summary>
        public Action<WorkItem> SubmitHook;

        /// <summary>Queue a request. Returns "queued", or why it was refused.</summary>
        public string Submit(WorkItem item, Now now, int speed = 1)
        {
            if (SubmitHook != null) SubmitHook(item);
            if (item.Cls <= Cls.Social && AmbientPaused(speed)) return Why("drop", "ambient_paused");
            // the game's own interactions are already rate-limited by the game: no pair quota for them
            var (ok, why) = Quotas.Allowed(item.ActorId, Truthy(item.Payload["vanilla"]) ? null : item.Listener, item.Cls, now);
            if (!ok) return Why("drop", why);
            foreach (var q in Queue)   // merge duplicates: same actor and kind (ambient events coalesce; the strongest stays)
            {
                if (q.ActorId == item.ActorId && q.Kind == item.Kind && q.Listener == item.Listener)
                {
                    if (item.Cls > q.Cls) q.Cls = item.Cls;
                    q.Severity = Math.Max(q.Severity, item.Severity);
                    return Why("merged", item.Kind);
                }
            }
            if (Queue.Count >= MaxQueue)   // bounded: the least important request makes room, or is the one refused
            {
                WorkItem victim = null;
                foreach (var q in Queue)
                    if (victim == null || CompareVictim(q, victim) < 0) victim = q;
                if (item.Cls < victim.Cls || (item.Cls == victim.Cls && item.Severity <= victim.Severity))
                    return Why("drop", "queue_full");
                Queue.Remove(victim);
                Log.Add(("evicted", victim.Kind + ":" + victim.ActorId.ToString(CultureInfo.InvariantCulture)));
            }
            Queue.Add(item);
            return "queued";
        }

        // key = (cls, severity, -created.real, -id), ascending
        static int CompareVictim(WorkItem a, WorkItem b)
        {
            int c = a.Cls.CompareTo(b.Cls); if (c != 0) return c;
            c = a.Severity.CompareTo(b.Severity); if (c != 0) return c;
            c = (-a.Created.Real).CompareTo(-b.Created.Real); if (c != 0) return c;
            return (-a.Id).CompareTo(-b.Id);
        }

        string Why(string what, string why)
        {
            Log.Add((what, why));
            return what + ":" + why;
        }

        public List<WorkItem> Expire(Now now)
        {
            var dead = Queue.Where(q => q.Expired(now)).ToList();
            Queue = Queue.Where(q => !q.Expired(now)).ToList();
            foreach (var q in dead) Log.Add(("expired", q.Kind + ":" + q.ActorId.ToString(CultureInfo.InvariantCulture)));
            return dead;
        }

        public List<Lease> ExpireLeases(Now now)
        {
            var o = new List<Lease>();
            foreach (var l in LeasesInOrder())
            {
                if (!l.Generating && l.IdleExpired(now))
                {
                    Release(l, "idle_expired");
                    o.Add(l);
                }
            }
            return o;
        }

        // ---- leasing --------------------------------------------------------
        public Lease LeaseFor(int actorId) { return LeasesInOrder().FirstOrDefault(l => l.ActorId == actorId); }

        public List<int> FreeSlots() { return ActorSlots.Where(s => !Leases.ContainsKey(s)).ToList(); }

        /// <summary>First free window; else preempt the lowest strictly-lower-class lease; else null.</summary>
        public Lease Acquire(WorkItem item, Now now)
        {
            if (item.Expired(now)) return null;
            if (LeaseFor(item.ActorId) != null) return null;   // actor already inhabited; the item stays queued
            var free = FreeSlots();
            int slot;
            if (free.Count > 0) slot = Slots.Pick(item.ActorId, free);
            else
            {
                var victims = LeasesInOrder().Where(l => l.Cls < item.Cls)
                    .OrderBy(l => l.Cls).ThenBy(l => l.Started.Real).ToList();
                if (victims.Count == 0) return null;
                var v = victims[0];
                v.Preempted = true;
                slot = v.Slot;
                Release(v, "preempted");
            }
            var lease = new Lease(slot, item.ActorId, item.Cls, item.Kind, now, now.Real) { Seq = _leaseSeq++ };
            Leases[slot] = lease;
            Slots.Remember(item.ActorId, slot);
            Log.Add(("lease", "slot" + slot.ToString(CultureInfo.InvariantCulture) + ":actor"
                + item.ActorId.ToString(CultureInfo.InvariantCulture) + ":" + item.Cls.Name()));
            return lease;
        }

        public void Release(Lease lease, string reason = "done")
        {
            lease.Ended = true; lease.EndReason = reason;
            if (Leases.TryGetValue(lease.Slot, out var cur) && ReferenceEquals(cur, lease)) Leases.Remove(lease.Slot);
            Log.Add(("release", "slot" + lease.Slot.ToString(CultureInfo.InvariantCulture) + ":" + reason));
        }

        public void Touch(Lease lease, Now now) { lease.TouchedReal = now.Real; }

        /// <summary>Most important first (class, then severity, then oldest): lease as many queued items as windows and
        /// the voice budget allow.</summary>
        public List<(WorkItem, Lease)> PopRunnable(Now now)
        {
            Expire(now);
            var started = new List<(WorkItem, Lease)>();
            var order = Queue.OrderByDescending(q => q.Cls).ThenByDescending(q => q.Severity)
                .ThenBy(q => q.Created.Real).ThenBy(q => q.Id).ToList();
            foreach (var item in order)
            {
                // ambient chatter only fills slack: it needs a reserve of tokens, so the consequential lines are never starved by it
                if (item.Cls < Cls.Urgent && !Budget.Ready(now, item.Cls <= Cls.Ambient ? AmbientReserve : 1.0))
                    continue;   // the community has spoken enough for now; it waits, or expires unspoken
                var lease = Acquire(item, now);
                if (lease != null)
                {
                    Queue.Remove(item);
                    started.Add((item, lease));
                }
            }
            return started;
        }

        // ---- skip-if-nothing-new -------------------------------------------
        public bool NothingNew(int actorId, string signature)
        {
            return LastSignature.TryGetValue(actorId, out var s) && s == signature;
        }

        public void MarkSpoke(int actorId, string signature) { LastSignature[actorId] = signature; }

        // ---- narrator -------------------------------------------------------
        /// <summary>No window leased for real work and nothing queued. Starving idle work ignores ambient and social
        /// chatter, so a busy fast game still gets its diaries and chronicle.</summary>
        public bool WindowsIdle(bool starving = false)
        {
            Cls floor = starving ? Cls.Reactive : Cls.Ambient;
            Cls lf = floor > Cls.Ambient ? floor : Cls.Ambient;
            return !Leases.Values.Any(l => l.Cls > Cls.Idle && l.Cls >= lf) && !Queue.Any(q => q.Cls >= floor);
        }

        /// <summary>Chronicle, diary, reflection: only when no window is leased and nothing is queued.</summary>
        public bool NarratorMayDoIdleWork(bool starving = false)
        {
            return !NarratorBusy && NarratorOwner == null && WindowsIdle(starving);
        }

        /// <summary>Both windows busy, nothing preemptable, event still live: the narrator covers it.</summary>
        public bool NarratorShouldCover(WorkItem item, Now now)
        {
            if (NarratorBusy || NarratorOwner != null || item.Expired(now) || FreeSlots().Count > 0) return false;
            return !Leases.Values.Any(l => l.Cls < item.Cls);
        }
    }
}

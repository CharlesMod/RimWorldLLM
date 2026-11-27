using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Walking trades (docs/VIVARIUM.md "Phase 4"): a trade with a visiting or orbital trader sends a colonist to the comms
// console or to the trader, and the deal runs when they get there. Verse-free: the pending trade, the decision table and
// the orchestration (decide, run, charge, chat) live here and are unit tested with fakes; the game file only observes and acts.
namespace RimWorldLLM.Bridge
{
    /// <summary>The one trade under way: what was asked, who walks, where to, and the clock.</summary>
    public sealed class PendingTrade
    {
        public const int HourTicks = 2500;
        public const int MaxReissues = 3;
        public const string ToConsole = "console", ToTrader = "trader";

        public string With;                 // P.. or S..
        public List<DealLine> Buy = new List<DealLine>();
        public List<DealLine> Sell = new List<DealLine>();
        public string Why = "";
        public string OpId = "";
        public int NegotiatorId;
        public string Negotiator = "";
        public string TargetKind = ToTrader;   // console | trader
        public string Target = "";             // "the comms console", "Tomas"
        public string Trader = "";             // "the bulk goods trader"
        public int StartTick, DeadlineTick;
        public int Reissues;
        /// <summary>Runtime only (not saved): the cell the Goto was last aimed at, to re-target when the trader moves; -1 = none yet.</summary>
        public int GoalX = -1, GoalZ = -1;

        public static PendingTrade Start(TradeArgs a, int tick)
        {
            return new PendingTrade
            {
                With = a.With, Buy = a.Buy.Select(l => new DealLine(l.Item, l.Count)).ToList(), Sell = a.Sell.Select(l => new DealLine(l.Item, l.Count)).ToList(),
                StartTick = tick, DeadlineTick = tick + HourTicks
            };
        }

        public TradeArgs Args()
        {
            return new TradeArgs
            {
                With = With, Negotiator = NegotiatorId,
                Buy = Buy.Select(l => new DealLine(l.Item, l.Count)).ToList(), Sell = Sell.Select(l => new DealLine(l.Item, l.Count)).ToList()
            };
        }

        /// <summary>"Ana is walking to the comms console"</summary>
        public string Walking { get { return Negotiator + " is walking to " + Target; } }

        /// <summary>"Ana is walking to the comms console to trade with the exotic goods trader"</summary>
        public string StartNote { get { return Walking + " to trade with " + Trader; } }

        /// <summary>The digest's trade.pending block.</summary>
        public JObject DigestJson() { return new JObject { ["negotiator"] = Negotiator, ["to"] = Target, ["with"] = With }; }

        static JArray Lines(List<DealLine> l) { return new JArray(l.Select(x => new JObject { ["item"] = x.Item, ["count"] = x.Count })); }
        static List<DealLine> ReadLines(JToken t)
        {
            var res = new List<DealLine>();
            var arr = t as JArray;
            if (arr == null) return res;
            foreach (var x in arr)
            {
                string item = VivJson.Str(x["item"]); int n = VivJson.Int(x["count"]);
                if (item.Length > 0 && n > 0) res.Add(new DealLine(item, n));
            }
            return res;
        }

        public JObject Export()
        {
            return new JObject
            {
                ["with"] = With, ["buy"] = Lines(Buy), ["sell"] = Lines(Sell), ["why"] = Why, ["op_id"] = OpId,
                ["negotiator_id"] = NegotiatorId, ["negotiator"] = Negotiator, ["target_kind"] = TargetKind, ["target"] = Target,
                ["trader"] = Trader, ["start_tick"] = StartTick, ["deadline_tick"] = DeadlineTick, ["reissues"] = Reissues
            };
        }

        /// <summary>null when the saved object is missing or has no trader id (an old or damaged save).</summary>
        public static PendingTrade Import(JObject o)
        {
            if (o == null) return null;
            string with = VivJson.Str(o["with"]);
            if (with.Length == 0) return null;
            var p = new PendingTrade
            {
                With = with, Buy = ReadLines(o["buy"]), Sell = ReadLines(o["sell"]),
                Why = ChatFeed.CleanWhy(VivJson.Str(o["why"])), OpId = VivJson.Clean(VivJson.Str(o["op_id"]), 60),
                NegotiatorId = VivJson.Int(o["negotiator_id"]), Negotiator = VivJson.Clean(VivJson.Str(o["negotiator"]), 60),
                TargetKind = VivJson.Str(o["target_kind"]) == ToConsole ? ToConsole : ToTrader,
                Target = VivJson.Clean(VivJson.Str(o["target"]), 80), Trader = VivJson.Clean(VivJson.Str(o["trader"]), 80),
                StartTick = VivJson.Int(o["start_tick"]), DeadlineTick = VivJson.Int(o["deadline_tick"]),
                Reissues = Math.Max(0, VivJson.Int(o["reissues"]))
            };
            if (p.DeadlineTick <= 0) p.DeadlineTick = p.StartTick + HourTicks;
            return p;
        }
    }

    /// <summary>What the game side saw this poll.</summary>
    public sealed class WalkObs
    {
        public int Tick;
        public bool TraderPresent;       // still there by id and able to trade
        public string TraderWhy;         // when !TraderPresent: the reason ("the console has no power"); null = "the trader has left"
        public bool NegotiatorOk;        // spawned, not downed, not drafted, not in a mental state, not dead
        public string NegotiatorWhy;     // why not, when !NegotiatorOk ("is downed")
        public bool CanReach = true;
        public bool Arrived;
        public bool HasOurJob;           // the Goto we gave is still the current job
    }

    public enum WalkStep { Wait, ReissueJob, Run, Instant, Refuse }

    public sealed class WalkDecision
    {
        public WalkStep Step;
        public string Reason;
        public static WalkDecision Of(WalkStep s, string reason = null) { return new WalkDecision { Step = s, Reason = reason }; }
    }

    public static class TradeWalk
    {
        public static WalkDecision Decide(PendingTrade p, WalkObs o)
        {
            if (!o.TraderPresent) return WalkDecision.Of(WalkStep.Refuse, string.IsNullOrEmpty(o.TraderWhy) ? "the trader has left" : o.TraderWhy);
            if (o.Arrived && o.NegotiatorOk) return WalkDecision.Of(WalkStep.Run);
            if (!o.NegotiatorOk) return WalkDecision.Of(WalkStep.Instant, p.Negotiator + " couldn't get there: " + (string.IsNullOrEmpty(o.NegotiatorWhy) ? "can't walk now" : o.NegotiatorWhy));
            if (!o.CanReach) return WalkDecision.Of(WalkStep.Instant, p.Negotiator + " couldn't get there: no way through");
            if (o.Tick >= p.DeadlineTick) return WalkDecision.Of(WalkStep.Instant, p.Negotiator + " took too long");
            if (!o.HasOurJob)
            {
                if (p.Reissues < PendingTrade.MaxReissues) return WalkDecision.Of(WalkStep.ReissueJob);
                return WalkDecision.Of(WalkStep.Instant, p.Negotiator + " couldn't get there: kept being interrupted");
            }
            return WalkDecision.Of(WalkStep.Wait);
        }

        /// <summary>Charges the day from a finished deal's Extra (spent, got, optional value) and adds `today`.</summary>
        public static void ChargeDay(Vivarium v, HandResult r, TradeLimits limits)
        {
            if (r == null || !r.Ok) return;
            var x = r.Extra ?? (r.Extra = new JObject());
            int spent = VivJson.Int(x["spent"]), got = VivJson.Int(x["got"]);
            v.TradeDay.Charge(spent, got, x["value"] != null ? VivJson.Int(x["value"]) : -1);
            x["today"] = v.TradeDay.ToJson(limits);
        }

        /// <summary>One poll of the pending trade: observe, decide, act. Returns a short word for what happened (null when nothing
        /// pending): "waiting", "reissued", "ran", "instant", "refused". `reissue` re-gives the Goto job (the game side).</summary>
        public static string Step(Vivarium v, int tick, IVivariumHands hands, Func<PendingTrade, WalkObs> observe, Action<PendingTrade> reissue)
        {
            var p = v.TradeWalk;
            if (p == null) return null;
            var o = observe(p);
            o.Tick = tick;
            var d = Decide(p, o);
            switch (d.Step)
            {
                case WalkStep.Wait: return "waiting";
                case WalkStep.ReissueJob: p.Reissues++; reissue(p); return "reissued";
                case WalkStep.Refuse: Fail(v, tick, p, d.Reason); return "refused";
                default: return Execute(v, tick, hands, p, d);
            }
        }

        static void Fail(Vivarium v, int tick, PendingTrade p, string reason)
        {
            v.TradeWalk = null;
            ChatFeed.Refused(v.Chat, tick, "trade with " + (string.IsNullOrEmpty(p.Trader) ? p.With : p.Trader), reason);
        }

        static string Execute(Vivarium v, int tick, IVivariumHands hands, PendingTrade p, WalkDecision d)
        {
            // The deal is checked again when it runs: prices, stock, silver, never-sell and the day's caps.
            v.TradeDay.Roll(tick);
            var limits = v.Settings.Limits();
            if (!v.Settings.Trade) { Fail(v, tick, p, "trading is off in the settings"); return "refused"; }
            if (v.TradeDay.DealsLeft(limits) <= 0) { Fail(v, tick, p, "the narrator has made its " + limits.DealsPerDay + " deals for today"); return "refused"; }
            HandResult r;
            try { r = hands.Trade(p.Args(), limits, v.TradeDay); }
            catch (Exception e) { r = HandResult.No("the game refused: " + e.Message); }
            if (r == null || !r.Ok) { Fail(v, tick, p, r != null && !string.IsNullOrEmpty(r.Reason) ? r.Reason : "the game gave no answer"); return "refused"; }
            ChargeDay(v, r, limits);
            string deal = r.Extra != null ? VivJson.Str(r.Extra["deal"], null) : null;
            if (string.IsNullOrEmpty(deal)) deal = r.Note ?? "";
            string note = d.Step == WalkStep.Run
                ? p.Negotiator + " walked to " + p.Target + " and " + deal
                : (r.Note ?? deal) + " (" + d.Reason + "; traded over the radio)";
            v.TradeWalk = null;
            v.Actions.Add("trade", note, tick, "narrator", p.Why);
            ChatFeed.Action(v.Chat, tick, note, p.Why);
            return d.Step == WalkStep.Run ? "ran" : "instant";
        }
    }

    /// <summary>A trader that can trade now, as the digest lists it ("P5521", "S31").</summary>
    public sealed class TraderStub
    {
        public string Id, Kind, Name;
        public TraderStub() { }
        public TraderStub(string id, string kind, string name) { Id = id; Kind = kind; Name = name; }
    }

    /// <summary>Runtime (never saved) list of the traders that can trade now; the game polls it, GET /vivarium shows it.</summary>
    public sealed class TraderList
    {
        List<TraderStub> _items = new List<TraderStub>();
        public IReadOnlyList<TraderStub> Items { get { return _items; } }
        public void Set(IEnumerable<TraderStub> now) { _items = (now ?? new TraderStub[0]).Take(12).ToList(); }
        public void Clear() { _items = new List<TraderStub>(); }
        public JArray ToJson() { return new JArray(_items.Select(t => new JObject { ["id"] = t.Id, ["kind"] = t.Kind, ["name"] = t.Name })); }
    }
}

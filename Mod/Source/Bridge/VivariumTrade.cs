using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

// Vivarium phase 4 (docs/VIVARIUM.md "Phase 4: trade, caravans, quests"). Verse-free: argument records, the trade
// rules, the per-day caps and the deal planner. The game bodies fill TradeOffer/TradeSituation and execute the plan.
namespace RimWorldLLM.Bridge
{
    /// <summary>One line of a deal: an item word (label or defName) and how many.</summary>
    public sealed class DealLine
    {
        public string Item;
        public int Count;
        public DealLine() { }
        public DealLine(string item, int count) { Item = item; Count = count; }
    }

    public sealed class TradeArgs
    {
        public string With;
        public List<DealLine> Buy = new List<DealLine>();
        public List<DealLine> Sell = new List<DealLine>();
    }

    public sealed class CaravanArgs
    {
        public string Op;                        // form | send | cancel (the plan route uses form)
        public List<int> Pawns = new List<int>();
        public List<int> Animals = new List<int>();
        public List<DealLine> Goods = new List<DealLine>();
        public string To;                        // T<tile>, Q<quest>, W<tile>, home (send only)
        public string Purpose;                   // trade | visit | quest | deliver
        public string Caravan;                   // C<id> for send / cancel
        public bool PlayerAsked;
    }

    /// <summary>What the game says about one item the trader or the colony could trade. Filled by the game body.</summary>
    public sealed class TradeOffer
    {
        public string Item;          // label, with stuff ("steel longsword")
        public string Def;           // defName
        public string Stuff;
        public int TraderCount;      // what the trader sells
        public int ColonyCount;      // what the colony has to sell, before the never-sell rules
        public double BuyPrice;      // each, what we pay
        public double SellPrice;     // each, what we get
        public bool TraderWillTrade; // the trader buys this kind of item
        public bool InUse;           // equipped or worn
        public bool Forbidden;
        public bool IsMedicine;
        public bool IsFood;
        public double NutritionEach;
        public double MarketValueEach;
    }

    public sealed class TradeSituation
    {
        public int ColonySilver;
        public int TraderSilver;
        public List<TradeOffer> Offers = new List<TradeOffer>();
        public int MedicineTotal;            // all medicine on the map
        public double FoodNutritionTotal;    // all food nutrition on the map
        public double NutritionPerDay;       // the colony's daily nutrition; 0 = no food floor

        public TradeSituation Copy()
        {
            return new TradeSituation { ColonySilver = ColonySilver, TraderSilver = TraderSilver, Offers = Offers, MedicineTotal = MedicineTotal, FoodNutritionTotal = FoodNutritionTotal, NutritionPerDay = NutritionPerDay };
        }
    }

    public sealed class TradeLimits
    {
        public int MaxDeal = 2000;
        public int MaxDay = 6000;
        public int DealsPerDay = 3;
        public int KeepMedicine = 10;
        public int KeepFoodDays = 10;
        public List<string> NeverSell = new List<string>();
    }

    /// <summary>The never-sell rules. Pure.</summary>
    public static class TradeRules
    {
        public static string Num(double d, int dec = 1) { return d.ToString("F" + dec, CultureInfo.InvariantCulture); }

        static bool Matches(string word, TradeOffer o)
        {
            word = (word ?? "").Trim();
            if (word.Length == 0) return false;
            return (o.Item ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 || (o.Def ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The player's never-sell word that matches the item, or null.</summary>
        public static string ListMatch(TradeOffer o, TradeLimits limits)
        {
            if (limits == null || limits.NeverSell == null) return null;
            foreach (var w in limits.NeverSell) if (Matches(w, o)) return w.Trim();
            return null;
        }

        /// <summary>How many of the colony's stack the rules let go (0..ColonyCount).</summary>
        public static int SellableCount(TradeOffer o, TradeSituation s, TradeLimits limits)
        {
            int n = Math.Max(0, o.ColonyCount);
            if (n == 0) return 0;
            if (o.InUse || o.Forbidden || ListMatch(o, limits) != null) return 0;
            if (o.IsMedicine && limits.KeepMedicine > 0)
                n = Math.Min(n, Math.Max(0, s.MedicineTotal - limits.KeepMedicine));
            if (o.IsFood && limits.KeepFoodDays > 0 && s.NutritionPerDay > 0 && o.NutritionEach > 0)
            {
                double spare = s.FoodNutritionTotal - limits.KeepFoodDays * s.NutritionPerDay;
                n = Math.Min(n, spare <= 0 ? 0 : (int)Math.Floor(spare / o.NutritionEach + 1e-9));
            }
            return n;
        }

        /// <summary>Why the colony cannot sell all it has of this item ("can't sell medicine: the colony keeps 10"), or null when nothing limits it.</summary>
        public static string Reason(TradeOffer o, TradeSituation s, TradeLimits limits)
        {
            string name = o.Item;
            if (o.ColonyCount <= 0) return "the colony has no " + name + " to sell";
            if (o.InUse) return "can't sell " + name + ": a colonist is using it";
            if (o.Forbidden) return "can't sell " + name + ": it is forbidden";
            string w = ListMatch(o, limits);
            if (w != null) return "can't sell " + name + ": \"" + w + "\" is on the player's never-sell list";
            int n = SellableCount(o, s, limits);
            if (n >= o.ColonyCount) return null;
            if (o.IsMedicine && limits.KeepMedicine > 0 && Math.Max(0, s.MedicineTotal - limits.KeepMedicine) < o.ColonyCount)
                return "can't sell " + name + ": the colony keeps " + limits.KeepMedicine + " medicine";
            return "can't sell " + name + ": the colony keeps " + limits.KeepFoodDays + " days of food";
        }

        /// <summary>The active rules as the digest lists them (`never_sell`).</summary>
        public static List<string> ActiveRules(TradeLimits limits)
        {
            var r = new List<string> { "weapons and apparel in use" };
            if (limits.KeepMedicine > 0) r.Add("medicine below " + limits.KeepMedicine);
            if (limits.KeepFoodDays > 0) r.Add("food below " + limits.KeepFoodDays + " days");
            r.Add("forbidden items");
            if (limits.NeverSell != null) foreach (var w in limits.NeverSell) { string t = (w ?? "").Trim(); if (t.Length > 0) r.Add(t); }
            return r;
        }
    }

    /// <summary>The per-game-day trade counters. Saved as `trade_day`. A day is 60000 ticks.</summary>
    public sealed class TradeDay
    {
        public const int TicksPerDay = 60000;
        public int Day = -1;
        public int Deals, Spent, Got, Value;

        public static int DayOf(int tick) { return tick < 0 ? 0 : tick / TicksPerDay; }

        /// <summary>Resets the counters when the game day has changed.</summary>
        public void Roll(int tick)
        {
            int d = DayOf(tick);
            if (d != Day) { Day = d; Deals = 0; Spent = 0; Got = 0; Value = 0; }
        }

        public int DealsLeft(TradeLimits l) { return Math.Max(0, l.DealsPerDay - Deals); }
        public int ValueLeft(TradeLimits l) { return Math.Max(0, l.MaxDay - Value); }

        /// <summary>Records one finished deal. value defaults to spent + got. Call Roll(tick) first.</summary>
        public void Charge(int spent, int got, int value = -1)
        {
            Deals++; Spent += Math.Max(0, spent); Got += Math.Max(0, got);
            Value += value >= 0 ? value : Math.Max(0, spent) + Math.Max(0, got);
        }

        public JObject ToJson(TradeLimits l)
        {
            return new JObject { ["deals"] = Deals, ["deals_left"] = DealsLeft(l), ["spent"] = Spent, ["got"] = Got, ["value_left"] = ValueLeft(l) };
        }

        public JObject Export() { return new JObject { ["day"] = Day, ["deals"] = Deals, ["spent"] = Spent, ["got"] = Got, ["value"] = Value }; }

        public void Import(JObject o)
        {
            Clear();
            if (o == null) return;
            Day = VivJson.Int(o["day"], -1); Deals = Math.Max(0, VivJson.Int(o["deals"])); Spent = Math.Max(0, VivJson.Int(o["spent"]));
            Got = Math.Max(0, VivJson.Int(o["got"])); Value = Math.Max(0, VivJson.Int(o["value"]));
        }

        public void Clear() { Day = -1; Deals = 0; Spent = 0; Got = 0; Value = 0; }
    }

    public sealed class PlannedLine
    {
        public TradeOffer Offer;
        public int Count;        // +n buy, -n sell
        public int Money;        // silver: price x n
    }

    public sealed class DealPlan
    {
        public bool Ok;
        public string Reason;
        public List<PlannedLine> Lines = new List<PlannedLine>();
        public int Spent, Got, Value;
        public int Net { get { return Got - Spent; } }
        public string Note;
        /// <summary>Sell lines cut down to a floor ("sold 5 of the 12 medicine: the colony keeps 10").</summary>
        public List<string> Clips = new List<string>();

        public static DealPlan No(string reason) { return new DealPlan { Ok = false, Reason = reason }; }
    }

    /// <summary>Checks a deal against the trader's offers, the never-sell rules, silver on both sides and the caps.</summary>
    public static class DealPlanner
    {
        public const int MaxLines = 12;

        /// <summary>Matches an item word to one offer of the pool: exact label, exact def, then a unique prefix, then a unique contains
        /// (case-insensitive). Returns null with a refusal (more than one match lists up to 5) or the offer with null.</summary>
        public static TradeOffer Match(string word, IList<TradeOffer> pool, out string refusal)
        {
            refusal = null;
            word = (word ?? "").Trim();
            if (word.Length == 0) { refusal = "an item line has no name"; return null; }
            var stages = new Func<TradeOffer, bool>[]
            {
                o => string.Equals(o.Item, word, StringComparison.OrdinalIgnoreCase),
                o => string.Equals(o.Def, word, StringComparison.OrdinalIgnoreCase),
                o => (o.Item ?? "").StartsWith(word, StringComparison.OrdinalIgnoreCase) || (o.Def ?? "").StartsWith(word, StringComparison.OrdinalIgnoreCase),
                o => (o.Item ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 || (o.Def ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
            };
            foreach (var st in stages)
            {
                var hit = pool.Where(st).ToList();
                if (hit.Count == 1) return hit[0];
                if (hit.Count > 1)
                {
                    refusal = "\"" + word + "\" could be " + string.Join(", ", hit.Take(5).Select(h => h.Item)) + (hit.Count > 5 ? " or more" : "") + "; name one";
                    return null;
                }
            }
            return null;
        }

        static string SellsList(IEnumerable<TradeOffer> offers)
        {
            var l = offers.Where(o => o.TraderCount > 0).OrderByDescending(o => o.BuyPrice * o.TraderCount).Take(8).Select(o => o.Item).ToList();
            return l.Count == 0 ? "nothing" : string.Join(", ", l);
        }

        sealed class Wanted { public string Word; public int Count; public TradeOffer Offer; }

        static List<Wanted> Merge(IList<DealLine> lines, IList<TradeOffer> pool, bool buy, TradeSituation s, out string refusal)
        {
            refusal = null;
            var res = new List<Wanted>();
            foreach (var l in lines ?? new List<DealLine>())
            {
                if (l == null || l.Count <= 0) { refusal = "every count must be at least 1"; return null; }
                string why;
                var o = Match(l.Item, pool, out why);
                if (o == null)
                {
                    if (why != null) { refusal = why; return null; }
                    string w = (l.Item ?? "").Trim();
                    refusal = buy ? "the trader has no " + w + "; it sells: " + SellsList(s.Offers)
                                  : "the colony has no " + w + " to sell";
                    return null;
                }
                var ex = res.Find(x => x.Offer == o);
                if (ex != null) ex.Count += l.Count; else res.Add(new Wanted { Word = l.Item, Count = l.Count, Offer = o });
            }
            return res;
        }

        public static DealPlan Plan(TradeArgs a, TradeSituation s, TradeLimits limits, TradeDay day, int tick)
        {
            if (a == null || ((a.Buy == null || a.Buy.Count == 0) && (a.Sell == null || a.Sell.Count == 0))) return DealPlan.No("the deal is empty: give something to buy or sell");
            if (a.Buy.Count + a.Sell.Count > MaxLines) return DealPlan.No("a deal takes at most " + MaxLines + " lines");
            day.Roll(tick);
            if (day.DealsLeft(limits) <= 0) return DealPlan.No("the narrator has made its " + limits.DealsPerDay + " deals for today");

            string refusal;
            var buyPool = s.Offers.Where(o => o.TraderCount > 0).ToList();
            var sellPool = s.Offers.Where(o => o.ColonyCount > 0).ToList();
            var buys = Merge(a.Buy, buyPool, true, s, out refusal);
            if (buys == null) return DealPlan.No(refusal);
            var sells = Merge(a.Sell, sellPool, false, s, out refusal);
            if (sells == null) return DealPlan.No(refusal);
            foreach (var b in buys) if (sells.Exists(x => x.Offer == b.Offer)) return DealPlan.No("can't buy and sell " + b.Offer.Item + " in one deal");

            var plan = new DealPlan();
            foreach (var b in buys)
            {
                if (b.Count > b.Offer.TraderCount) return DealPlan.No("the trader has only " + b.Offer.TraderCount + " " + b.Offer.Item + ", not " + b.Count);
                int money = (int)Math.Round(b.Offer.BuyPrice * b.Count);
                plan.Lines.Add(new PlannedLine { Offer = b.Offer, Count = b.Count, Money = money });
                plan.Spent += money;
            }
            var sit = s.Copy();   // medicine and food floors count what earlier sell lines already take
            foreach (var x in sells)
            {
                var o = x.Offer;
                if (!o.TraderWillTrade) return DealPlan.No("the trader does not buy " + o.Item);
                int can = TradeRules.SellableCount(o, sit, limits);
                string rule = TradeRules.Reason(o, sit, limits);
                if (can <= 0) return DealPlan.No(rule ?? "can't sell " + o.Item);
                int n = x.Count;
                if (n > o.ColonyCount) { n = o.ColonyCount; }
                if (n > can)
                {
                    plan.Clips.Add("sold " + can + " of the " + n + " " + o.Item + ": " + (rule ?? "a rule keeps the rest").Replace("can't sell " + o.Item + ": ", ""));
                    n = can;
                }
                if (o.IsMedicine) sit.MedicineTotal -= n;
                if (o.IsFood) sit.FoodNutritionTotal -= n * o.NutritionEach;
                int money = (int)Math.Round(o.SellPrice * n);
                plan.Lines.Add(new PlannedLine { Offer = o, Count = -n, Money = money });
                plan.Got += money;
            }
            plan.Value = plan.Spent + plan.Got;
            if (plan.Lines.Count == 0) return DealPlan.No("the deal is empty");
            if (plan.Value > limits.MaxDeal) return DealPlan.No("this deal is worth " + plan.Value + " silver; the cap is " + limits.MaxDeal + " per deal");
            if (plan.Value > day.ValueLeft(limits)) return DealPlan.No("this deal is worth " + plan.Value + " silver; only " + day.ValueLeft(limits) + " is left of today's " + limits.MaxDay);
            int net = plan.Net;
            if (net < 0 && -net > s.ColonySilver) return DealPlan.No("the colony has " + s.ColonySilver + " silver; the deal costs " + (-net));
            if (net > 0 && net > s.TraderSilver) return DealPlan.No("the trader has only " + s.TraderSilver + " silver; the deal pays " + net);

            var parts = new List<string>();
            var bought = plan.Lines.Where(l => l.Count > 0).Select(l => l.Count + " " + l.Offer.Item).ToList();
            var sold = plan.Lines.Where(l => l.Count < 0).Select(l => (-l.Count) + " " + l.Offer.Item).ToList();
            if (bought.Count > 0) parts.Add("bought " + string.Join(", ", bought));
            if (sold.Count > 0) parts.Add("sold " + string.Join(", ", sold));
            plan.Note = string.Join(", ", parts) + " (" + (net >= 0 ? "+" : "-") + Math.Abs(net) + " silver)";
            if (plan.Clips.Count > 0) plan.Note += "; " + string.Join("; ", plan.Clips);
            plan.Ok = true;
            return plan;
        }
    }
}

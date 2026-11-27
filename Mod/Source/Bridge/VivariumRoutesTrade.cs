using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Phase 4 routes: GET /trade/offer, POST /act/trade, GET /caravan/plan, POST /act/caravan (docs/VIVARIUM.md "Phase 4").
    // Shapes and the settings gates are checked here; the game checks are in the hands.
    public static partial class VivariumRoutes
    {
        static readonly string[] CaravanOps = { "form", "send", "cancel" };
        static readonly string[] CaravanPurposes = { "trade", "visit", "quest", "deliver" };
        static readonly Regex TraderId = new Regex("^[PST][0-9]{1,10}$", RegexOptions.Compiled);
        static readonly Regex PlaceId = new Regex("^[TQW][0-9]{1,10}$", RegexOptions.Compiled);
        static readonly Regex CaravanId = new Regex("^C[0-9]{1,10}$", RegexOptions.Compiled);
        public const int MaxDealLines = 12, MaxCaravanPawns = 6, MaxGoalLines = 12;
        const string TradeOff = "trading is off in the settings";
        const string CaravansOff = "caravans are off in the settings";

        static void RegisterTrade(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("GET", "/trade/offer", true, c =>
            {
                string with = c.Q("with");
                if (string.IsNullOrEmpty(with) || !TraderId.IsMatch(with.Trim())) return Bad("with must be a trader id (P.., S.. or T<tile>)");
                var v = vivarium();
                if (!v.Settings.Trade) return ApiResult.Fail("refused", TradeOff);
                var h = hands();
                if (h == null) return NoGame();
                var d = h.TradeOffer(with.Trim(), v.Settings.Limits());
                if (d == null) return NoGame();
                if (d["refused"] != null) return ApiResult.Fail("refused", (string)d["refused"]);
                v.TradeDay.Roll(c.State.Tick);
                if (d["today"] == null) d["today"] = v.TradeDay.ToJson(v.Settings.Limits());
                return ApiResult.Success(d);
            });
            router.Add("POST", "/act/trade", true, c => Act(c, vivarium, hands, "trade", b => TradePlan(b, vivarium)));
            router.Add("GET", "/caravan/plan", true, c =>
            {
                CaravanArgs a;
                try { a = QueryCaravan(c); }
                catch (ArgError e) { return Bad(e.Message); }
                var v = vivarium();
                if (!v.Settings.Caravans) return ApiResult.Fail("refused", CaravansOff);
                var h = hands();
                if (h == null) return NoGame();
                var d = h.CaravanPlan(a, v.Settings.Limits());
                if (d == null) return NoGame();
                if (d["refused"] != null) return ApiResult.Fail("refused", (string)d["refused"]);
                return ApiResult.Success(d);
            });
            router.Add("POST", "/act/caravan", true, c => Act(c, vivarium, hands, "caravan", b => CaravanPlanOf(b, vivarium)));
        }

        // ---------------------------------------------------------------- shared parsers
        static string PlaceArg(string s, string f, bool allowHome)
        {
            s = (s ?? "").Trim();
            if (allowHome && string.Equals(s, "home", StringComparison.OrdinalIgnoreCase)) return "home";
            s = s.ToUpperInvariant();
            if (!PlaceId.IsMatch(s)) throw new ArgError(f + " must be T<tile>, Q<quest id> or W<tile>" + (allowHome ? ", or home" : ""));
            return s;
        }

        static List<DealLine> DealLines(JObject b, string f)
        {
            var res = new List<DealLine>();
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return res;
            var arr = t as JArray;
            if (arr == null) throw new ArgError(f + " must be an array of {item, count}");
            if (arr.Count > MaxDealLines) throw new ArgError(f + " takes at most " + MaxDealLines + " lines");
            int i = 0;
            foreach (var x in arr)
            {
                var o = x as JObject;
                if (o == null) throw new ArgError(f + "[" + i + "] must be an object {item, count}");
                res.Add(new DealLine(Str(o, "item", 1, 60), Int(o, "count", 1, 100000)));
                i++;
            }
            return res;
        }

        static List<int> IdList(JObject b, string f, int max)
        {
            var res = new List<int>();
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return res;
            var arr = t as JArray;
            if (arr == null) throw new ArgError(f + " must be an array of ids");
            if (arr.Count > max) throw new ArgError(f + " takes at most " + max + " ids");
            foreach (var x in arr)
            {
                if (x.Type != JTokenType.Integer || x.Value<long>() < 1 || x.Value<long>() > int.MaxValue) throw new ArgError(f + " must be positive integer ids");
                int id = x.Value<int>();
                if (!res.Contains(id)) res.Add(id);
            }
            return res;
        }

        static List<int> QueryIds(string q, string f, int max)
        {
            var res = new List<int>();
            if (string.IsNullOrWhiteSpace(q)) return res;
            foreach (var part in q.Split(','))
            {
                int id;
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1) throw new ArgError(f + " must be comma-separated positive integer ids");
                if (!res.Contains(id)) res.Add(id);
            }
            if (res.Count > max) throw new ArgError(f + " takes at most " + max + " ids");
            return res;
        }

        static List<DealLine> QueryGoods(string q)
        {
            var res = new List<DealLine>();
            if (string.IsNullOrWhiteSpace(q)) return res;
            foreach (var part in q.Split(','))
            {
                int i = part.LastIndexOf(':');
                int n;
                if (i < 1 || !int.TryParse(part.Substring(i + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > 100000)
                    throw new ArgError("goods must be item:count pairs, comma-separated");
                string item = part.Substring(0, i).Trim();
                if (item.Length == 0 || item.Length > 60) throw new ArgError("goods item names are 1..60 chars");
                res.Add(new DealLine(item, n));
            }
            if (res.Count > MaxGoalLines) throw new ArgError("goods takes at most " + MaxGoalLines + " lines");
            return res;
        }

        static CaravanArgs QueryCaravan(RouteContext c)
        {
            var a = new CaravanArgs { Op = "form" };
            a.Pawns = QueryIds(c.Q("pawns"), "pawns", MaxCaravanPawns);
            if (a.Pawns.Count == 0) throw new ArgError("pawns is required (comma-separated ids)");
            a.Animals = QueryIds(c.Q("animals"), "animals", MaxCaravanPawns);
            a.Goods = QueryGoods(c.Q("goods"));
            string to = c.Q("to");
            if (string.IsNullOrEmpty(to)) throw new ArgError("to is required");
            a.To = PlaceArg(to, "to", false);
            string pur = c.Q("purpose");
            a.Purpose = string.IsNullOrEmpty(pur) ? "visit" : pur.Trim().ToLowerInvariant();
            if (Array.IndexOf(CaravanPurposes, a.Purpose) < 0) throw new ArgError("purpose must be one of " + string.Join(", ", CaravanPurposes));
            return a;
        }

        // ---------------------------------------------------------------- /act/trade
        static Plan TradePlan(JObject b, Func<Vivarium> vivarium)
        {
            var a = new TradeArgs { With = Str(b, "with", 1, 40).ToUpperInvariant() };
            if (!TraderId.IsMatch(a.With)) throw new ArgError("with must be a trader id (P.., S.. or T<tile>)");
            a.Buy = DealLines(b, "buy");
            a.Sell = DealLines(b, "sell");
            if (a.Buy.Count + a.Sell.Count == 0) throw new ArgError("give at least one line in buy or sell");
            if (a.Buy.Count + a.Sell.Count > MaxDealLines) throw new ArgError("a deal takes at most " + MaxDealLines + " lines");
            TradeLimits limits = null;
            return new Plan
            {
                What = () => "trade with " + a.With,
                GateAt = (v, tick) =>
                {
                    if (!v.Settings.Trade) return HandResult.No(TradeOff);
                    limits = v.Settings.Limits();
                    v.TradeDay.Roll(tick);
                    if (v.TradeDay.DealsLeft(limits) <= 0) return HandResult.No("the narrator has made its " + limits.DealsPerDay + " deals for today");
                    return null;
                },
                Run = h =>
                {
                    var v = vivarium();
                    var r = h.Trade(a, limits ?? v.Settings.Limits(), v.TradeDay);
                    if (r != null && r.Ok)
                    {
                        var x = r.Extra ?? (r.Extra = new JObject());
                        int spent = VivJson.Int(x["spent"]), got = VivJson.Int(x["got"]);
                        v.TradeDay.Charge(spent, got, x["value"] != null ? VivJson.Int(x["value"]) : -1);
                        x["today"] = v.TradeDay.ToJson(limits ?? v.Settings.Limits());
                    }
                    return r;
                }
            };
        }

        // ---------------------------------------------------------------- /act/caravan
        static Plan CaravanPlanOf(JObject b, Func<Vivarium> vivarium)
        {
            var a = new CaravanArgs { Op = OneOf(b, "op", CaravanOps) };
            a.PlayerAsked = OptBool(b, "player_asked") ?? false;
            if (a.Op == "form")
            {
                a.Pawns = IdList(b, "pawns", MaxCaravanPawns);
                if (a.Pawns.Count == 0) throw new ArgError("form needs pawns (1.." + MaxCaravanPawns + " ids)");
                a.Animals = IdList(b, "animals", MaxCaravanPawns);
                a.Goods = DealLines(b, "goods");
                a.To = PlaceArg(Str(b, "to", 1, 40), "to", false);
                string pur = OptOneOf(b, "purpose", CaravanPurposes, "visit");
                a.Purpose = pur;
            }
            else if (a.Op == "send")
            {
                a.Caravan = CaravanKey(b);
                a.To = PlaceArg(Str(b, "to", 1, 40), "to", true);
            }
            else a.Caravan = CaravanKey(b);
            int tickAt = 0;
            return new Plan
            {
                What = () => a.Op == "form" ? "send a caravan to " + a.To : a.Op == "send" ? "send caravan " + a.Caravan : "cancel caravan " + a.Caravan,
                GateAt = (v, tick) =>
                {
                    tickAt = tick;
                    if (!v.Settings.Caravans) return HandResult.No(CaravansOff);
                    if (a.PlayerAsked && !PlayerAskedRecently(v, tick))
                        return HandResult.No("player_asked is set but the player has said nothing in the chat this past hour");
                    return null;
                },
                Run = h =>
                {
                    var v = vivarium();
                    var r = h.Caravan(a, v.Settings.CaravanMaxPawns);
                    if (r == null || !r.Ok) return r;
                    string key = r.Extra != null ? VivJson.Str(r.Extra["caravan"], null) : null;
                    if (a.Op == "form" && !string.IsNullOrEmpty(key))
                        v.CaravanPlans.Set(key, a.Purpose, a.To, a.To.StartsWith("Q") ? a.To : null, tickAt, r.Note);
                    else if (a.Op == "send")
                    {
                        var e = v.CaravanPlans.Get(a.Caravan);
                        if (e != null) e.To = a.To;
                    }
                    else if (a.Op == "cancel") v.CaravanPlans.Remove(a.Caravan);
                    return r;
                }
            };
        }

        static string CaravanKey(JObject b)
        {
            string k = Str(b, "caravan", 1, 20).ToUpperInvariant();
            if (!CaravanId.IsMatch(k)) throw new ArgError("caravan must be a caravan id (C..)");
            return k;
        }
    }
}

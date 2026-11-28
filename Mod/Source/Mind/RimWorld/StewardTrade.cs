// Port of games/rimworld/steward_trade.py: the steward's trade tools (Vivarium phase 4): trade_offer and trade.
//
// Built from the digest's `trade` block (docs/VIVARIUM.md, "Phase 4"): a tool exists only when the digest holds what it needs,
// and its enums come from the digest's ids. One tool only looks (GET, never an action, so it is not in Acting); one acts:
//
//     trade_offer(with)                  GET  /trade/offer?with=ID        -> a compact price list
//     trade(with, buy, sell, why)        POST /act/trade {with, buy, sell}
//
// Optional parameters follow StewardBuild: an optional text may be empty. Accepting or declining a quest stays with `choose`;
// `player_asked` is never sent.
//
// Caravans are off (the Python's too since 4fb37b5): LOOKS = trade_offer, ACTS = trade; a settlement is no trade partner.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    public sealed class TradeKit
    {
        public const string None = "none";
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", StewardKit.WhyText, maxLen: WhyMax, required: false, sameCall: true);   // as StewardSession's (a copy: the Python's too)

        public static readonly string[] Looks = { "trade_offer" };
        public static readonly string[] Acts = { "trade" };
        public const int MaxLines = 12;
        public const int MaxCount = 99999;
        public const int RenderMax = 1200;
        private static readonly string[] Blank = { "", "none", "nothing", "-", "n/a" };

        private static readonly Regex Lead = new Regex(@"^(\d+)\s*[x×]?\s*(.+)$", RegexOptions.CultureInvariant);          // "20 cloth", "20x cloth"
        private static readonly Regex TrailX = new Regex(@"^(.+?)\s*[x×]\s*(\d+)$", RegexOptions.CultureInvariant);       // "cloth x20", "cloth x 20"
        private static readonly Regex Trail = new Regex(@"^(.+?)\s+(\d+)$", RegexOptions.CultureInvariant);               // "cloth 20"
        private static readonly Regex SplitLines = new Regex(@"[,;\n]|\band\b", RegexOptions.CultureInvariant);

        /// <summary>'cloth x20, 5 components, steel 100' -> [{item, count}, ...]; PyValueError names the part that does not read.</summary>
        public static List<JObject> ParseLines(string text, string what)
        {
            var counts = new List<KeyValuePair<string, BigInteger>>();
            foreach (string part in SplitLines.Split(text ?? ""))
            {
                string p = PyStr.Strip(part).Trim('.');
                if (p.Length == 0) continue;
                string item, n;
                Match m = TrailX.Match(p);
                if (!m.Success) m = Trail.Match(p);
                if (m.Success) { item = m.Groups[1].Value; n = m.Groups[2].Value; }
                else
                {
                    Match l = Lead.Match(p);
                    if (!l.Success) throw new PyValueError(what + ": give a count for " + p + "; write items like: cloth x20, steel x100");
                    n = l.Groups[1].Value; item = l.Groups[2].Value;
                }
                item = PyStr.Strip(GameData.Clean(item, "label", 40)).ToLowerInvariant();
                BigInteger count = Digits(n);
                if (item.Length == 0 || count < 1 || count > MaxCount)
                    throw new PyValueError(what + ": " + p + " needs an item name and a count from 1 to " + MaxCount);
                int at = counts.FindIndex(kv => kv.Key == item);
                if (at >= 0) counts[at] = new KeyValuePair<string, BigInteger>(item, counts[at].Value + count);
                else counts.Add(new KeyValuePair<string, BigInteger>(item, count));
            }
            return counts.Select(kv => new JObject { ["item"] = kv.Key, ["count"] = (long)kv.Value }).ToList();
        }

        /// <summary>int() of a run of Unicode decimal digits.</summary>
        private static BigInteger Digits(string s)
        {
            BigInteger r = BigInteger.Zero;
            foreach (char c in s) r = r * 10 + (int)char.GetNumericValue(c);
            return r;
        }

        private static bool IsBlank(JToken v) => Array.IndexOf(Blank, PyStr.Strip(Text(v)).ToLowerInvariant()) >= 0;

        /// <summary>str(x or "").</summary>
        private static string Text(JToken t) => t != null && PyJson.Truthy(t) ? PyJson.Str(t) : "";

        private static string Short(JToken text, int n = 24) => PyStr.Strip(GameData.Clean(Text(text), "label", n));

        private static string IdOf(JToken x) => x is JObject o ? PyStr.Strip(Text(DigestJson.G(o, "id"))) : "";

        private static string Price(JObject x)
        {
            JToken p = DigestJson.G(x, "price");
            return DigestJson.Str(x, "item", "?") + " " + DigestJson.Str(x, "count", "?")
                + (p != null && p.Type != JTokenType.Boolean && (p.Type == JTokenType.Integer || p.Type == JTokenType.Float) ? " @" + DigestJson.G6(p) : "");
        }

        /// <summary>GET /trade/offer -> 'Tomas sells: steel 400 @2.1, ...; buys of ours: cloth 300 @1.1, ...; trader silver 1800'.</summary>
        public static string RenderOffer(JObject r)
        {
            string name = Short(DigestJson.Truthy(DigestJson.G(r, "name")) ? DigestJson.G(r, "name") : DigestJson.G(r, "with"), 30);
            var sells = DigestJson.Items(DigestJson.G(r, "buys")).OfType<JObject>().ToList();      // the route's `buys` is what we buy from them
            var buys = DigestJson.Items(DigestJson.G(r, "sells")).OfType<JObject>().ToList();      // and its `sells` is what we may sell to them
            string a = string.Join(", ", sells.Take(30).Select(Price));
            string b = string.Join(", ", buys.Take(30).Select(Price));
            var outl = new List<string>
            {
                name + " sells: " + (a.Length > 0 ? a : "nothing"),
                "buys of ours: " + (b.Length > 0 ? b : "nothing"),
            };
            JToken silver = DigestJson.G(r, "silver");
            if (DigestJson.IsNumberish(silver)) outl.Add("trader silver " + DigestJson.G6(silver));
            if (DigestJson.Truthy(DigestJson.G(r, "negotiator"))) outl.Add("negotiator " + Short(DigestJson.G(r, "negotiator")));
            string text = string.Join("; ", outl);
            if (PyStr.Len(text) > RenderMax)
            {
                string head = PyStr.Head(text, RenderMax - 3);
                int comma = head.LastIndexOf(',');
                text = (comma >= 0 ? head.Substring(0, comma) : head) + " ...";
            }
            return text;
        }

        public readonly bool Has;
        public readonly Dictionary<string, JObject> Traders = new Dictionary<string, JObject>(StringComparer.Ordinal);
        /// <summary>Who we can deal with: the visiting traders.</summary>
        public readonly Dictionary<string, JObject> Partners = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;
        public readonly HashSet<string> Acting;

        public TradeKit(JObject digest)
        {
            var t = DigestJson.G(digest, "trade") as JObject;
            Has = t != null;
            t = t ?? new JObject();
            foreach (var x in DigestJson.Items(DigestJson.G(t, "traders"))) { string i = IdOf(x); if (i.Length > 0) Traders[i] = (JObject)x; }
            foreach (var kv in Traders) Partners[kv.Key] = kv.Value;
            Tools = Has ? BuildTools() : new List<ToolSpec>();
            Names = new HashSet<string>(Tools.Select(x => x.Name), StringComparer.Ordinal);
            Acting = new HashSet<string>(Names.Where(n => Array.IndexOf(Acts, n) >= 0), StringComparer.Ordinal);
        }

        // ---- the tools ------------------------------------------------------------------
        private Param WithParam()
        {
            return new Param("with", "enum", "who (a trader's id from TRADERS)", choices: Partners.Keys.ToList());
        }

        private List<ToolSpec> BuildTools()
        {
            var outl = new List<ToolSpec>();
            if (Partners.Count > 0)
            {
                outl.Add(new ToolSpec("trade_offer", "Look at one trader's prices (not an action); do this before trade.", new[] { WithParam() }));
                outl.Add(new ToolSpec("trade", "Buy and sell with one trader at once; final.",
                    new[]
                    {
                        WithParam(),
                        new Param("buy", "text", "items to buy: cloth x20, steel x100 (none if unused)", maxLen: 200, required: false),
                        new Param("sell", "text", "items to sell, same form (none if unused)", maxLen: 200, required: false), WhyParam,
                    }));
            }
            return outl;
        }

        // ---- the bodies -----------------------------------------------------------------
        private string Partner(JObject v)
        {
            string w = Text(v.TryGetValue("with", out JToken t) ? t : null);
            if (!Partners.ContainsKey(w)) throw new PyValueError("no such trader; you can trade with: " + string.Join(", ", Partners.Keys));
            return w;
        }

        private (string, JObject) Trade(JObject v)
        {
            string w = Partner(v);
            JObject t = Partners[w];
            JToken ct = DigestJson.G(t, "can_trade");
            if (ct != null && ct.Type == JTokenType.Boolean && !(bool)ct)
            {
                string why = Short(DigestJson.G(t, "why_not"), 80);
                throw new PyValueError("cannot trade with " + Short(DigestJson.G(t, "name")) + " now: " + (why.Length > 0 ? why : "not possible"));
            }
            JToken bt = v.TryGetValue("buy", out JToken b0) ? b0 : null, st = v.TryGetValue("sell", out JToken s0) ? s0 : null;
            var buy = IsBlank(bt) ? new List<JObject>() : ParseLines(PyJson.Str(bt), "buy");
            var sell = IsBlank(st) ? new List<JObject>() : ParseLines(PyJson.Str(st), "sell");
            if (buy.Count == 0 && sell.Count == 0) throw new PyValueError("give buy or sell, like: buy cloth x20, steel x100");
            if (buy.Count + sell.Count > MaxLines) throw new PyValueError("at most " + MaxLines + " lines per deal");
            return ("trade", new JObject { ["with"] = w, ["buy"] = new JArray(buy), ["sell"] = new JArray(sell) });
        }

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            (string verb, JObject b) r;
            if (tool == "trade") r = Trade(v);
            else throw new PyValueError("unknown tool " + tool);
            string why = PyStr.Strip(GameData.Clean(Text(v.TryGetValue("why", out JToken wt) ? wt : null), "why", WhyMax));
            if (why.Length > 0) r.b["why"] = why;
            return r;
        }

        // ---- the looks (GET, no action) ---------------------------------------------------
        /// <summary>(path, query params) of a look tool call; PyValueError for text that does not resolve.</summary>
        public (string path, List<KeyValuePair<string, string>> query) LookRequest(string tool, JObject v)
        {
            return ("/trade/offer", new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("with", Partner(v)) });
        }

        public async Task<string> LookAsync(string tool, JObject v, IBridge bridge)
        {
            var (path, q) = LookRequest(tool, v);
            JObject r = await bridge.LookAsync(path, q).ConfigureAwait(false);
            return RenderOffer(r);
        }
    }
}

// Port of agent/rimworldllm_agent/games/rimworld/concern_rules.py: RimWorld's rule tables for ongoing concerns and grudges.
//
// Births and resolutions are decided from event fields only (importance from the game's severity, never a model call).
// Texts are first person, about ten words, with "{who}" standing for the first pawn in "about".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class ConcernRules
    {
        public const double InjuryMin = 0.4;     // an injury this severe (game severity) can worry someone who loves the victim
        public const int GuardOneIn = 4;         // a captured prisoner is on one colonist in four's mind (deterministic by ids)
        public static readonly Dictionary<string, string> Offences = new Dictionary<string, string> { { "Insult", "insulted" }, { "Slight", "slighted" } };
        public const int RaidWorriers = 2;       // at most this many witnesses carry a raid worry (and so its payoff line)
        public static readonly HashSet<string> Abrasive = new HashSet<string> { "abrasive", "psychopath", "bloodlust" };
        public static readonly HashSet<string> Kind = new HashSet<string> { "kind" };

        private static List<int> Roles(JObject e, params string[] roles)
        {
            var r = new List<int>();
            foreach (var a in Js.ItemsGet(e, "actors"))
                if (roles.Contains(Js.StrOr(a, "role", null) ?? "\u0000none")) r.Add(Js.Int(a["id"]));
            return r;
        }

        /// <summary>The (at most RaidWorriers) witnesses who worry about a raid: most loved ones among the colonists first,
        /// then the lowest id (the event carries no mood, so id is the deterministic tie-break).</summary>
        public static List<int> RaidWorriersOf(JObject e, IList<int> holders, Func<int, int, bool> loved)
        {
            var held = new HashSet<int>(holders);
            var cands = Js.ItemsGet(e, "witnesses").Select(w => Js.Int(w)).Distinct().Where(held.Contains).OrderBy(x => x).ToList();
            var score = cands.ToDictionary(h => h, h => holders.Count(o => o != h && loved(h, o)));
            return cands.OrderBy(h => -score[h]).ThenBy(h => h).Take(RaidWorriers).ToList();
        }

        private static JObject Birth(int actor, string text, int[] about, string kind, string hint, double importance)
        {
            return new JObject
            {
                { "actor", actor }, { "text", text }, { "about", new JArray(about) }, { "kind", kind }, { "hint", hint }, { "importance", importance }
            };
        }

        private static int GuardDigit(int p, int h)
        {
            byte[] hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(p.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + h.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return hash[hash.Length - 1] % GuardOneIn;      // the 160-bit number mod 4 is its last byte mod 4
        }

        public static List<JObject> Births(JObject e, IList<int> holders, Func<int, int, bool> loved)
        {
            string t = Js.StrOr(e, "type", "");
            var data = Js.Get(e, "data");
            double sev = Js.Get(e, "severity") == null ? 0.0 : Js.Float(Js.Get(e, "severity"));
            var outp = new List<JObject>();
            if (t == "injury" && sev >= InjuryMin)
            {
                foreach (int v in Roles(e, "victim").Take(1))
                    foreach (int h in holders)
                        if (h != v && loved(h, v))
                            outp.Add(Birth(h, "I can't stop worrying about {who}; they are hurt.", new[] { v }, "hurt", "healed:" + v.ToString(System.Globalization.CultureInfo.InvariantCulture), sev));
            }
            else if (t == "death" && !Js.Truthy(Js.Get(data, "animal")))
            {
                foreach (int v in Roles(e, "victim").Take(1))
                    foreach (int h in holders)
                        if (h != v && loved(h, v))
                            outp.Add(Birth(h, "I can't believe {who} is gone.", new[] { v }, "grief", "", 0.9));
            }
            else if (t == "mental_break")
            {
                foreach (int v in Roles(e, "subject").Take(1))
                    foreach (int h in holders)
                        if (h != v && loved(h, v))
                            outp.Add(Birth(h, "I'm worried about {who}; they broke down.", new[] { v }, "break", "", Math.Max(0.4, sev)));
            }
            else if (t == "raid_start" && Js.Truthy(Js.Get(e, "ledger_id")))
            {
                foreach (int h in RaidWorriersOf(e, holders, loved))
                    outp.Add(Birth(h, "Raiders are at our gates; I fear for us all.", new int[0], "threat", "ledger:" + PyJson.Str(Js.Get(e, "ledger_id")), Math.Max(0.5, sev)));
            }
            else if (t == "prisoner" && Js.StrOr(data, "phase", null) == "captured")
            {
                foreach (int p in Roles(e, "prisoner").Take(1))
                    foreach (int h in Js.ItemsGet(e, "witnesses").Select(w => Js.Int(w)))
                        if (holders.Contains(h) && h != p && GuardDigit(p, h) == 0)
                            outp.Add(Birth(h, "A prisoner is held here; I keep an eye on {who}.", new[] { p }, "prisoner", "prisoner:" + p.ToString(System.Globalization.CultureInfo.InvariantCulture), 0.35));
            }
            return outp;
        }

        /// <summary>(key, what happened): every concern whose resolve_hint equals a key is resolved.</summary>
        public static List<(string Key, string Text)> Resolutions(JObject e, Func<JObject, bool> isLedgerEnd)
        {
            string t = Js.StrOr(e, "type", "");
            var data = Js.Get(e, "data");
            string summary = PyStr.Strip(Js.StrOr(e, "summary", ""));
            if (summary.Length == 0) summary = t;
            var outp = new List<(string, string)>();
            string I(int x) { return x.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (isLedgerEnd(e) && Js.Truthy(Js.Get(e, "ledger_id")))
                outp.Add(("ledger:" + PyJson.Str(Js.Get(e, "ledger_id")), summary));
            if (t == "tend")
                foreach (int p in Roles(e, "patient")) outp.Add(("healed:" + I(p), summary));
            else if (t == "death")
            {
                foreach (int p in Roles(e, "victim")) outp.Add(("healed:" + I(p), summary));         // a worry about the dead ends as grief
                foreach (int p in Roles(e, "victim")) outp.Add(("prisoner:" + I(p), summary));
            }
            else if (t == "prisoner" && new[] { "released", "recruited", "died" }.Contains(Js.StrOr(data, "phase", null)))
                foreach (int p in Roles(e, "prisoner")) outp.Add(("prisoner:" + I(p), summary));
            return outp;
        }

        /// <summary>(offender, victim, verb: what the offender did to the victim) for an Insult/Slight interaction, or a social fight that names its target.</summary>
        public static (int Offender, int Victim, string Verb)? SocialOffence(JObject e)
        {
            var data = Js.Get(e, "data");
            string t = Js.StrOr(e, "type", "");
            var inter = Js.Get(data, "interaction");
            if (t == "social" && inter != null && inter.Type == JTokenType.String && Offences.ContainsKey((string)inter))
            {
                var a = Roles(e, "initiator"); var b = Roles(e, "recipient");
                if (a.Count > 0 && b.Count > 0) return (a[0], b[0], Offences[(string)inter]);
            }
            if (t == "mental_break" && Js.StrOr(data, "state", "") == "SocialFighting" && Js.Get(data, "target") != null)
            {
                var a = Roles(e, "subject");
                if (a.Count > 0 && Js.Int(Js.Get(data, "target")) != a[0]) return (a[0], Js.Int(Js.Get(data, "target")), "picked a fight with");
            }
            return null;
        }

        public static string Style(JObject snap)
        {
            var defs = Builders.TraitDefs(snap);
            if (defs.Overlaps(Abrasive)) return "abrasive";
            if (defs.Overlaps(Kind)) return "kind";
            return "";
        }
    }
}

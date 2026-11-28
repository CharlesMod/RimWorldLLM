using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // POST /act/rezone: change an existing zone (stockpile priority and filter, a field's or the hydroponics' crop). Same Act pipeline
    // as the rest; shapes are checked here, the zone and the words are resolved by the hands (ZonesPure). Marks "zone_settings" cost no cells.
    public static partial class VivariumRoutes
    {
        public static void RegisterZones(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/rezone", true, c => Act(c, vivarium, hands, "rezone", Rezone));
        }

        static Plan Rezone(JObject b)
        {
            var a = new RezoneArgs { Zone = Str(b, "zone", 1, 40) };
            string pr = OptStr(b, "priority", 40);
            if (pr != null && pr.ToLowerInvariant() != "none")
            {
                a.Priority = ZonesPure.ParsePriority(pr);
                if (a.Priority == null) throw new ArgError("priority must be one of " + string.Join(", ", ZonesPure.Priorities));
            }
            a.Allow = Words(b, "allow");
            a.Deny = Words(b, "deny");
            string plant = OptStr(b, "plant", 40);
            if (plant != null && plant.ToLowerInvariant() != "none") a.Plant = plant.Trim();
            if (a.Empty) throw new ArgError("give priority, allow, deny or plant");
            return new Plan
            {
                What = () => "change " + a.Zone,
                Run = h =>
                {
                    var z = h as IZonesHands;
                    return z != null ? z.Rezone(a) : HandResult.No("zone hands are not available");
                }
            };
        }

        static List<string> Words(JObject b, string f)
        {
            string s = OptStr(b, f, 200);
            var l = ZonesPure.SplitWords(s);
            if (l.Count > ZonesPure.MaxWords) throw new ArgError(f + " takes at most " + ZonesPure.MaxWords + " words");
            foreach (var w in l) if (w.Length > ZonesPure.MaxWord) throw new ArgError("each word in " + f + " must be at most " + ZonesPure.MaxWord + " chars");
            return l;
        }
    }
}

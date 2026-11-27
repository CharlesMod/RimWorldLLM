using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>Shapes the art_created fields material and tale from plain values (no game references).</summary>
    public static class ArtText
    {
        public const int MaterialMax = 60, TaleDefMax = 60, SummaryMax = 200, NameMax = 40, MaxPawns = 3;

        public static string Material(string label) { return PawnText.Cap(label, MaterialMax); }

        /// <summary>The tale object, or null when there is nothing to say (no def and no summary). day is the game day
        /// (negative or null: left out); pawns are names (blank ones dropped, at most MaxPawns, each capped).</summary>
        public static JObject Tale(string def, string summary, int? day, IEnumerable<string> pawns)
        {
            string d = PawnText.Cap(def, TaleDefMax), s = PawnText.Cap(summary, SummaryMax);
            if (d.Length == 0 && s.Length == 0) return null;
            var o = new JObject { ["def"] = d, ["summary"] = s };
            if (day.HasValue && day.Value >= 0) o["day"] = day.Value;
            var names = new JArray();
            if (pawns != null)
                foreach (var p in pawns)
                {
                    string n = PawnText.Cap(p, NameMax);
                    if (n.Length == 0) continue;
                    names.Add(n);
                    if (names.Count >= MaxPawns) break;
                }
            o["pawns"] = names;
            return o;
        }
    }
}

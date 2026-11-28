using System;
using System.Collections.Generic;
using System.Linq;

// Verse-free logic of the narrator's rezone tool: priority words, filter word lists, crop words and the refusal wording.
// The game side (Mod/Source/RimWorld/VivariumZonesHands.cs) turns the game's defs into the small records below and asks these
// functions to decide, so the decisions are tested without the game.
namespace RimWorldLLM.Bridge
{
    /// <summary>POST /act/rezone. zone: "#id", a label, a word of the ZONES line, or "hydroponics".</summary>
    public sealed class RezoneArgs
    {
        public string Zone;
        public string Priority;                                  // null = leave
        public List<string> Allow = new List<string>();
        public List<string> Deny = new List<string>();
        public string Plant;                                     // null = leave

        public bool Empty { get { return Priority == null && Allow.Count == 0 && Deny.Count == 0 && Plant == null; } }
    }

    /// <summary>The hands of the rezone route (separate from IVivariumHands; a hands object without it refuses).</summary>
    public interface IZonesHands
    {
        HandResult Rezone(RezoneArgs a);
    }

    /// <summary>One existing zone as the model sees it in the ZONES line. Kind: grow, stockpile, hydroponics (every basin, Id 0).</summary>
    public sealed class ZoneRef
    {
        public int Id;
        public string Kind, Label, Crop, Preset;
    }

    /// <summary>A sowable plant. Ground: sowable in soil; Hydro: sowable in a hydroponics basin; Research: the first unfinished sowing prerequisite, or null.</summary>
    public sealed class CropInfo
    {
        public string Def, Label, Harvest, Research;
        public bool Ground, Hydro;
    }

    /// <summary>A def with its label, for filter words.</summary>
    public sealed class NamedDef
    {
        public string Def, Label;
        public NamedDef(string def, string label) { Def = def; Label = label; }
    }

    public sealed class FilterHit
    {
        public bool Category;
        public string Def, Label;
    }

    public static class ZonesPure
    {
        public const int MaxWords = 8, MaxWord = 40;
        public static readonly string[] Priorities = BuildPure.Priorities;

        /// <summary>Filter presets as words: the category defNames each one allows ("Root" is every category).</summary>
        public static readonly Dictionary<string, string[]> PresetCategories = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["food"] = new[] { "Foods" }, ["meds"] = new[] { "Medicine" }, ["raw"] = new[] { "ResourcesRaw" },
            ["everything"] = new[] { "Root" }, ["all"] = new[] { "Root" },
        };

        // ---------------------------------------------------------------- priority
        /// <summary>The canonical priority word, or null when it is none of low, normal, preferred, important, critical.</summary>
        public static string ParsePriority(string s)
        {
            string w = (s ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Priorities, w) >= 0 ? w : null;
        }

        // ---------------------------------------------------------------- word lists
        /// <summary>"meals, steel and weapons" -> meals, steel, weapons: lower case, no empties or "none", no repeats.</summary>
        public static List<string> SplitWords(string csv)
        {
            var l = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return l;
            string s = csv.Replace(';', ',').Replace(" and ", ",");
            foreach (var part in s.Split(','))
            {
                string w = part.Trim().ToLowerInvariant();
                if (w.Length == 0 || w == "none" || l.Contains(w)) continue;
                l.Add(w);
            }
            return l;
        }

        static string Stem(string w)
        {
            w = (w ?? "").Trim().ToLowerInvariant();
            return w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) ? w.Substring(0, w.Length - 1) : w;
        }

        static string[] Tokens(string s)
        {
            return (s ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray();
        }

        static NamedDef Find(IList<NamedDef> list, string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant();
            foreach (var d in list)
                if (string.Equals(d.Label, w, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Def, w, StringComparison.OrdinalIgnoreCase)) return d;
            string sw = Stem(w);
            foreach (var d in list) if (Stem(d.Label) == sw) return d;
            var qt = Tokens(w);
            if (qt.Length == 0) return null;
            NamedDef best = null;
            foreach (var d in list)
            {
                var lt = Tokens(d.Label);
                if (qt.All(q => lt.Contains(q)) && (best == null || d.Label.Length < best.Label.Length)) best = d;
            }
            return best;
        }

        /// <summary>A filter word to a category or a thing: presets, then category labels, then thing labels.
        /// False with a refusal that names a few valid words.</summary>
        public static bool ResolveFilterWord(string word, IList<NamedDef> categories, IList<NamedDef> things, out FilterHit hit, out string refusal)
        {
            hit = null; refusal = null;
            string w = (word ?? "").Trim().ToLowerInvariant();
            string[] preset;
            if (PresetCategories.TryGetValue(w, out preset))
            {
                var c = categories.FirstOrDefault(x => string.Equals(x.Def, preset[0], StringComparison.OrdinalIgnoreCase));
                hit = new FilterHit { Category = true, Def = preset[0], Label = c != null ? c.Label : w };
                return true;
            }
            var cat = Find(categories, w);
            if (cat != null) { hit = new FilterHit { Category = true, Def = cat.Def, Label = cat.Label }; return true; }
            var th = Find(things, w);
            if (th != null) { hit = new FilterHit { Category = false, Def = th.Def, Label = th.Label }; return true; }
            refusal = "no item word '" + w + "'; try: food, meds, raw, everything" + ExampleWords(categories, 5);
            return false;
        }

        static string ExampleWords(IList<NamedDef> cats, int n)
        {
            var ex = cats.Select(c => c.Label).Where(l => !string.IsNullOrEmpty(l)).Take(n).ToList();
            return ex.Count == 0 ? "" : ", " + string.Join(", ", ex);
        }

        // ---------------------------------------------------------------- crops
        /// <summary>The word the model uses for a crop: the alias word when there is one ("potatoes"), else the label without " plant".</summary>
        public static string CropWord(string defName, string label)
        {
            foreach (var kv in BuildPure.PlantAliases)
                if (string.Equals(kv.Value, defName, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            string l = (label ?? defName ?? "").Trim();
            if (l.EndsWith(" plant", StringComparison.OrdinalIgnoreCase)) l = l.Substring(0, l.Length - 6);
            return l;
        }

        static CropInfo FindCrop(IList<CropInfo> crops, string word)
        {
            string w = (word ?? "").Trim();
            string alias;
            if (BuildPure.PlantAliases.TryGetValue(w, out alias))
            {
                var a = crops.FirstOrDefault(c => string.Equals(c.Def, alias, StringComparison.OrdinalIgnoreCase));
                if (a != null) return a;
            }
            string lw = w.ToLowerInvariant(), sw = Stem(lw);
            Func<Func<CropInfo, bool>, CropInfo> first = p => crops.FirstOrDefault(p);
            return first(c => string.Equals(c.Label, lw, StringComparison.OrdinalIgnoreCase))
                ?? first(c => string.Equals(CropWord(c.Def, c.Label), lw, StringComparison.OrdinalIgnoreCase))
                ?? first(c => string.Equals(c.Def, w, StringComparison.OrdinalIgnoreCase))
                ?? first(c => c.Harvest != null && Stem(c.Harvest) == sw)
                ?? first(c => Stem(CropWord(c.Def, c.Label)) == sw);
        }

        /// <summary>A crop word for a field (hydro false) or the hydroponics basins (hydro true), from every sowable plant.
        /// False with the reason: unknown word, research missing, or not sowable there.</summary>
        public static bool ResolveCrop(string word, IList<CropInfo> crops, bool hydro, out CropInfo crop, out string refusal)
        {
            crop = null; refusal = null;
            var c = FindCrop(crops, word);
            string w = (word ?? "").Trim();
            if (c == null)
            {
                var ok = crops.Where(x => x.Research == null && (hydro ? x.Hydro : x.Ground)).Select(x => CropWord(x.Def, x.Label)).Distinct().Take(6).ToList();
                refusal = "no crop called '" + w + "'" + (ok.Count > 0 ? "; try: " + string.Join(", ", ok) : "");
                return false;
            }
            string name = CropWord(c.Def, c.Label);
            if (hydro ? !c.Hydro : !c.Ground)
            {
                refusal = hydro ? name + " can't grow in a hydroponics basin" : name + " is not sowable in soil" + (c.Hydro ? " (hydroponics only)" : "");
                return false;
            }
            if (c.Research != null) { refusal = name + " needs research " + c.Research + " first"; return false; }
            crop = c;
            return true;
        }

        // ---------------------------------------------------------------- zones
        public static bool IsHydroWord(string w)
        {
            string s = Stem((w ?? "").Trim().ToLowerInvariant());
            return s == "hydroponic" || s == "hydroponics" || s == "hydroponic basin" || s == "basin" || s == "hydro";
        }

        public static string ZoneName(ZoneRef z)
        {
            if (z.Kind == "hydroponics") return "the hydroponics basins";
            string id = z.Id > 0 ? " (#" + z.Id + ")" : "";
            if (z.Kind == "grow") return "the " + (string.IsNullOrEmpty(z.Crop) ? "" : z.Crop + " ") + "field" + id;
            return "the " + (string.IsNullOrEmpty(z.Preset) || z.Preset == "mixed" ? "" : z.Preset + " ") + "stockpile" + id;
        }

        public static string ZoneList(IEnumerable<ZoneRef> zones)
        {
            return string.Join(", ", zones.Take(8).Select(z => z.Kind == "hydroponics" ? "hydroponics"
                : "#" + z.Id + " " + z.Kind + " " + (z.Kind == "grow" ? z.Crop : z.Preset)));
        }

        /// <summary>An existing zone by "#id", a bare id, "zone 3", its label, "hydroponics", or words of its kind/crop/preset when they pick exactly one.</summary>
        public static bool ResolveZone(string word, IList<ZoneRef> zones, out ZoneRef zone, out string refusal)
        {
            zone = null; refusal = null;
            string w = (word ?? "").Trim().ToLowerInvariant();
            if (IsHydroWord(w))
            {
                zone = zones.FirstOrDefault(z => z.Kind == "hydroponics");
                if (zone != null) return true;
                refusal = "no hydroponics basins to change";
                return false;
            }
            string num = w.StartsWith("#", StringComparison.Ordinal) ? w.Substring(1) : w.StartsWith("zone ", StringComparison.Ordinal) ? w.Substring(5) : w;
            int id;
            if (int.TryParse(num.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id))
            {
                zone = zones.FirstOrDefault(z => z.Id == id && z.Kind != "hydroponics");
                if (zone != null) return true;
                refusal = "no zone #" + id + "; the zones are: " + ZoneList(zones);
                return false;
            }
            zone = zones.FirstOrDefault(z => z.Kind != "hydroponics" && string.Equals(z.Label, w, StringComparison.OrdinalIgnoreCase));
            if (zone != null) return true;
            var qt = Tokens(w);
            if (qt.Length > 0)
            {
                var hits = zones.Where(z => z.Kind != "hydroponics" && qt.All(q => Tokens(z.Kind + " " + z.Crop + " " + z.Preset + " " + z.Label).Contains(q))).ToList();
                if (hits.Count == 1) { zone = hits[0]; return true; }
                if (hits.Count > 1) { refusal = "'" + w + "' fits several zones; say which: " + ZoneList(hits); return false; }
            }
            refusal = "no zone called '" + w + "'; the zones are: " + ZoneList(zones);
            return false;
        }

        // ---------------------------------------------------------------- wording
        public static string JoinWords(IList<string> l)
        {
            if (l.Count == 0) return "";
            if (l.Count == 1) return l[0];
            return string.Join(", ", l.Take(l.Count - 1)) + " and " + l[l.Count - 1];
        }

        /// <summary>"set the food stockpile (#4) to important, allow meals and steel, stop taking weapons".</summary>
        public static string SetNote(ZoneRef z, string priority, IList<string> allowed, IList<string> denied, string crop)
        {
            var parts = new List<string>();
            if (priority != null) parts.Add("to " + priority);
            if (crop != null) parts.Add("to grow " + crop);
            string head = "set " + ZoneName(z) + (parts.Count > 0 ? " " + string.Join(" and ", parts) : "");
            var more = new List<string>();
            if (allowed.Count > 0) more.Add("allow " + JoinWords(allowed));
            if (denied.Count > 0) more.Add("stop taking " + JoinWords(denied));
            if (parts.Count == 0 && more.Count > 0) head = "changed what " + ZoneName(z) + " takes";
            return more.Count == 0 ? head : parts.Count == 0 ? head + ": " + string.Join(", ", more) : head + ", " + string.Join(", ", more);
        }

        /// <summary>What a zone of this kind does not take: the refusal for a wrong field.</summary>
        public static string WrongField(string kind, RezoneArgs a)
        {
            if (kind == "grow" || kind == "hydroponics")
            {
                if (a.Priority != null || a.Allow.Count > 0 || a.Deny.Count > 0)
                    return (kind == "grow" ? "a field" : "hydroponics") + " has no priority or filter; give plant";
                return a.Plant == null ? "give plant" : null;
            }
            if (a.Plant != null) return "a stockpile grows nothing; plant is for fields and hydroponics";
            return a.Empty ? "give priority, allow or deny" : null;
        }

        // ---------------------------------------------------------------- undo encoding of a filter change
        /// <summary>The defs a filter change turned on and off, as "+A,B|-C" (the undo mark's Extra).</summary>
        public static string EncodeDiff(IEnumerable<string> before, IEnumerable<string> after)
        {
            var b = new HashSet<string>(before); var a = new HashSet<string>(after);
            var on = a.Where(x => !b.Contains(x)).OrderBy(x => x, StringComparer.Ordinal);
            var off = b.Where(x => !a.Contains(x)).OrderBy(x => x, StringComparer.Ordinal);
            return "+" + string.Join(",", on) + "|-" + string.Join(",", off);
        }

        /// <summary>Back to the defs to turn off (turned on by the change) and to turn on again.</summary>
        public static void DecodeDiff(string s, out List<string> turnedOn, out List<string> turnedOff)
        {
            turnedOn = new List<string>(); turnedOff = new List<string>();
            if (string.IsNullOrEmpty(s)) return;
            var halves = s.Split('|');
            if (halves.Length > 0 && halves[0].StartsWith("+", StringComparison.Ordinal))
                turnedOn.AddRange(halves[0].Substring(1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            if (halves.Length > 1 && halves[1].StartsWith("-", StringComparison.Ordinal))
                turnedOff.AddRange(halves[1].Substring(1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The undo note's piece: "restored 2 zones' settings".</summary>
        public static string RestoredNote(int n) { return "restored the settings of " + (n == 1 ? "a zone" : n + " zones"); }
    }
}

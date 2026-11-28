using System;
using System.Collections.Generic;
using System.Linq;

// Verse-free logic of the narrator's floor tool: the words for floor kinds, their aliases, research / stock / budget refusals and the
// notes. The game side (Mod/Source/RimWorld/VivariumFloorHands.cs) turns the game's TerrainDefs into FloorInfo records and asks these
// functions to decide, so the decisions are tested without the game.
namespace RimWorldLLM.Bridge
{
    /// <summary>POST /act/floor. room: the digest room id; kind: a floor word or "smooth stone".</summary>
    public sealed class FloorArgs
    {
        public int Room;
        public string Kind;
    }

    /// <summary>The hands of the floor route (separate from IVivariumHands; a hands object without it refuses).</summary>
    public interface IFloorHands
    {
        HandResult Floor(FloorArgs a, BuildBudget left);
    }

    /// <summary>One material of a floor's cost.</summary>
    public sealed class FloorCost
    {
        public string Def, Label;
        public int Count;
        public FloorCost(string def, string label, int count) { Def = def; Label = label; Count = count; }
    }

    /// <summary>A floor the player can build. Research: the first unfinished prerequisite's label, or null when it is done.</summary>
    public sealed class FloorInfo
    {
        public string Def, Label, Research;
        public List<FloorCost> Cost = new List<FloorCost>();
    }

    public static class FloorsPure
    {
        public const string Smooth = "smooth stone";
        public const int MaxWord = 40, MaxListed = 10, MaxCarpets = 3;

        /// <summary>Other ways to say a floor: lower case, as the model may write it -> the canonical word.</summary>
        public static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = "wood floor", ["wooden floor"] = "wood floor", ["wood planks"] = "wood floor", ["planks"] = "wood floor", ["plank floor"] = "wood floor",
            ["straw"] = "straw matting", ["straw mat"] = "straw matting", ["straw floor"] = "straw matting",
            ["concrete floor"] = "concrete",
            ["paved"] = "paved tile", ["paving"] = "paved tile", ["paved floor"] = "paved tile",
            ["steel"] = "steel tile", ["steel floor"] = "steel tile", ["metal tile"] = "steel tile", ["metal floor"] = "steel tile",
            ["sterile"] = "sterile tile", ["sterile floor"] = "sterile tile",
            ["smooth"] = Smooth, ["smooth floor"] = Smooth, ["smooth stone floor"] = Smooth, ["smoothed stone"] = Smooth, ["smooth rock"] = Smooth,
        };

        /// <summary>"carpet (red)" -> "red carpet"; any other label as it is, lower case and trimmed.</summary>
        public static string Word(string def, string label)
        {
            string l = Norm(string.IsNullOrEmpty(label) ? def : label);
            if (l.StartsWith("carpet (", StringComparison.Ordinal) && l.EndsWith(")", StringComparison.Ordinal) && l.Length > 9)
                return l.Substring(8, l.Length - 9).Trim() + " carpet";
            return l;
        }

        public static string Word(FloorInfo f) { return Word(f.Def, f.Label); }

        static string Norm(string s) { return string.Join(" ", (s ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)); }

        public static bool IsSmooth(string kind)
        {
            string k = Norm(kind);
            if (k == Smooth) return true;
            string c;
            return Aliases.TryGetValue(k, out c) && c == Smooth;
        }

        public static bool IsCarpet(FloorInfo f) { return Word(f).EndsWith(" carpet", StringComparison.Ordinal) || Word(f) == "carpet"; }

        /// <summary>Cost of one cell for a stock check: every material the floor needs.</summary>
        public static bool Affordable(FloorInfo f, Func<string, int> have, int cells)
        {
            return f.Cost.All(c => have(c.Def) >= c.Count * Math.Max(1, cells));
        }

        /// <summary>The words for the digest's build block: researched floors, buildable-from-stock first, then by the order given, at most
        /// max words and MaxCarpets carpets. Smooth stone is not listed (the tool adds it).</summary>
        public static List<string> DigestWords(IEnumerable<FloorInfo> infos, Func<string, int> have, int max)
        {
            var rows = infos.Where(f => f.Research == null).Select((f, i) => new { f, i, ok = Affordable(f, have, 1) }).OrderBy(r => r.ok ? 0 : 1).ThenBy(r => r.i).ToList();
            var words = new List<string>();
            int carpets = 0;
            foreach (var r in rows)
            {
                if (words.Count >= max) break;
                string w = Word(r.f);
                if (words.Contains(w)) continue;
                if (IsCarpet(r.f)) { if (carpets >= MaxCarpets) continue; carpets++; }
                words.Add(w);
            }
            return words;
        }

        static string Listed(IEnumerable<FloorInfo> infos)
        {
            var w = infos.Where(f => f.Research == null).Select(Word).Distinct().Take(MaxListed).ToList();
            w.Add(Smooth);
            return string.Join(", ", w);
        }

        /// <summary>The floor for a word: exact word, an alias, "carpet (red)" for "red carpet", else a word every part of which is in one
        /// floor's word. False with the refusal: unknown (with the valid words), ambiguous, or not researched.</summary>
        public static bool Resolve(string kind, IList<FloorInfo> infos, out FloorInfo hit, out string refusal)
        {
            hit = null; refusal = null;
            string k = Norm(kind);
            string canon;
            if (Aliases.TryGetValue(k, out canon)) k = canon;
            var exact = infos.Where(f => Word(f) == k).ToList();
            var cands = exact.Count > 0 ? exact : infos.Where(f => Word(f).Contains(k) && k.Length >= 3).ToList();
            if (cands.Count == 0)
            {
                refusal = "no floor called '" + (k.Length > MaxWord ? k.Substring(0, MaxWord) : k) + "'; try: " + Listed(infos);
                return false;
            }
            if (cands.Count > 1)
            {
                var avail = cands.Where(f => f.Research == null).ToList();
                if (avail.Count == 1) cands = avail;
                else
                {
                    refusal = "'" + k + "' fits several floors: " + string.Join(", ", cands.Select(Word).Distinct().Take(6));
                    return false;
                }
            }
            hit = cands[0];
            if (hit.Research != null)
            {
                refusal = Word(hit) + " needs research " + hit.Research + " first";
                hit = null;
                return false;
            }
            return true;
        }

        /// <summary>Refusal when the cycle's blueprint budget (or designation budget) cannot take the cells; else null.</summary>
        public static string Budget(int cells, int left, string what = "blueprints")
        {
            if (cells <= left) return null;
            return "only " + left + " " + what + " left this cycle; try a smaller room or wait";
        }

        /// <summary>Refusal when the stock cannot pay for the cells; else null. have: stock of a material by defName.</summary>
        public static string Stock(FloorInfo f, int cells, Func<string, int> have)
        {
            foreach (var c in f.Cost)
            {
                int need = c.Count * cells, got = have(c.Def);
                if (got < need) return "not enough " + c.Label + " in stock: " + cells + (cells == 1 ? " cell needs " : " cells need ") + need + ", there is " + got;
            }
            return null;
        }

        /// <summary>"the dining room" for a room role or label (an owner's "Ana's bedroom" stays as it is).</summary>
        public static string RoomName(string roleOrLabel)
        {
            string r = string.Join(" ", (roleOrLabel ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return r.Length == 0 ? "the room" : r.Contains("'s ") ? r : "the " + r.ToLowerInvariant();
        }

        /// <summary>"laid wood floor in the dining room (12 cells)" or, with cells it could not lay, "(12 cells, 2 skipped)".</summary>
        public static string Note(string word, string room, int cells, int skipped)
        {
            return "laid " + word + " in " + room + " (" + Cells(cells) + (skipped > 0 ? ", " + skipped + " skipped" : "") + ")";
        }

        public static string SmoothNote(string room, int cells, int skipped)
        {
            return "marked " + room + "'s rough stone floor for smoothing (" + Cells(cells) + (skipped > 0 ? ", " + skipped + " skipped" : "") + ")";
        }

        public static string AlreadyNote(string word, string room) { return room + " already has " + word + " everywhere"; }

        static string Cells(int n) { return n + (n == 1 ? " cell" : " cells"); }
    }
}

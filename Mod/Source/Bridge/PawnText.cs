using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Bridge
{
    /// <summary>Text clean-up for the /pawn snapshot (no game references): markup stripped, whitespace collapsed,
    /// capped at a sentence end, list caps.</summary>
    public static class PawnText
    {
        public const string Ellipsis = "...";
        public const int BackstoryMax = 400, TraitMax = 240, XenotypeMax = 200, MemeMax = 160,
                         ArrivalMax = 160, TaleMax = 120, AppearanceMax = 160;
        public const int MaxPrecepts = 10, MaxTales = 5, MaxApparel = 3;

        static readonly Regex Tags = new Regex("<[^<>]*>", RegexOptions.Compiled);
        static readonly Regex Space = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Rich-text tags removed (nested ones too) and whitespace collapsed to single spaces. Null gives "".</summary>
        public static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string prev;
            do { prev = s; s = Tags.Replace(s, ""); } while (s != prev);
            return Space.Replace(s, " ").Trim();
        }

        /// <summary>Stripped text of at most max characters: cut after the last ". ! ?" that fits; when none fits, cut at a word
        /// and add "...". Text already within max is returned whole.</summary>
        public static string Cap(string s, int max)
        {
            s = Strip(s);
            if (max <= 0) return "";
            if (s.Length <= max) return s;
            int best = -1;
            for (int i = 0; i < max && i < s.Length; i++)
            {
                char c = s[i];
                if (c != '.' && c != '!' && c != '?') continue;
                int end = i;
                while (end + 1 < s.Length && end + 1 < max && (s[end + 1] == '"' || s[end + 1] == '\'' || s[end + 1] == ')')) end++;
                bool boundary = end + 1 >= s.Length || s[end + 1] == ' ';
                if (boundary && end < max) best = end + 1;
            }
            if (best > 0) return s.Substring(0, best);
            int budget = max - Ellipsis.Length;
            if (budget <= 0) return s.Substring(0, max);
            int cut = s.LastIndexOf(' ', budget);
            if (cut < budget / 2) cut = budget;
            string head = s.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '-');
            if (head.Length == 0) head = s.Substring(0, budget);
            return head + Ellipsis;
        }

        /// <summary>The first sentence (stripped), at most max characters.</summary>
        public static string FirstSentence(string s, int max)
        {
            s = Strip(s);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c == '.' || c == '!' || c == '?') && (i + 1 >= s.Length || s[i + 1] == ' '))
                { s = s.Substring(0, i + 1); break; }
            }
            return Cap(s, max);
        }

        /// <summary>Non-empty stripped entries, each capped, at most maxCount, order kept, duplicates dropped.</summary>
        public static List<string> CapList(IEnumerable<string> items, int maxCount, int maxEach)
        {
            var r = new List<string>();
            if (items == null) return r;
            foreach (var it in items)
            {
                if (r.Count >= maxCount) break;
                string t = Cap(it, maxEach);
                if (t.Length == 0 || r.Contains(t)) continue;
                r.Add(t);
            }
            return r;
        }

        /// <summary>One line "Hair: x. Skin: y. Wears: a, b, c" from the parts that are present, at most 3 apparel, capped.</summary>
        public static string Appearance(string hair, string skin, IEnumerable<string> apparel)
        {
            var parts = new List<string>();
            string h = Strip(hair), sk = Strip(skin);
            if (h.Length > 0) parts.Add("Hair: " + h);
            if (sk.Length > 0) parts.Add("Skin: " + sk);
            var worn = CapList(apparel, MaxApparel, 40);
            if (worn.Count > 0) parts.Add("Wears: " + string.Join(", ", worn));
            if (parts.Count == 0) return "";
            return Cap(string.Join(". ", parts), AppearanceMax);
        }
    }
}

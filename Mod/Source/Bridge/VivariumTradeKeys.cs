using System;
using System.Globalization;

// Vivarium phase 4, Verse-free helpers for the game bodies: id keys (P<pawn>, S<ship>, T<tile>, W<tile>, Q<quest>, C<caravan>)
// and the food kind a def counts as in the caravan planner.
namespace RimWorldLLM.Bridge
{
    public static class TradeKeys
    {
        /// <summary>Splits "T4321" into ('T', 4321). False for anything else.</summary>
        public static bool TryParse(string s, out char kind, out int num)
        {
            kind = '\0'; num = 0;
            s = (s ?? "").Trim();
            if (s.Length < 2 || !char.IsLetter(s[0])) return false;
            if (!int.TryParse(s.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out num)) return false;
            kind = char.ToUpperInvariant(s[0]);
            return true;
        }

        public static string Make(char kind, int num) { return kind + num.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The trailing number of a load id like "PassingShip_31" (or "Lord_5"); -1 when none.</summary>
        public static int TrailingNumber(string loadId)
        {
            if (string.IsNullOrEmpty(loadId)) return -1;
            int i = loadId.Length;
            while (i > 0 && char.IsDigit(loadId[i - 1])) i--;
            int n;
            return i < loadId.Length && int.TryParse(loadId.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : -1;
        }
    }

    public static class FoodKinds
    {
        /// <summary>The planner's food kind for a def, or null when it is not caravan food. mealPreferability: the def's
        /// preferability is one of the Meal* kinds; humanEdible: people eat it; keeps: it does not rot, or rots slowly.</summary>
        public static string Classify(string defName, bool humanEdible, bool mealPreferability, bool isKibble, bool isRawAnimalFeed)
        {
            string d = (defName ?? "").ToLowerInvariant();
            if (d == "pemmican") return "pemmican";
            if (d.Contains("mealsurvival") || d.Contains("survival")) return "survival";
            if (isKibble || d == "kibble") return "kibble";
            if (humanEdible && mealPreferability) return "meal";
            if (humanEdible) return "raw";
            return null;
        }
    }
}

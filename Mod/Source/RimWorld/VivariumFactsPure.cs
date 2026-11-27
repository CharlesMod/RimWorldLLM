using System.Text;

namespace RimWorldLLM
{
    /// <summary>Verse-free helpers for the digest "facts" block.</summary>
    public static class FactsPure
    {
        /// <summary>"TemperateForest" gives "temperate_forest"; "ToxicFallout" gives "toxic_fallout"; spaces and dashes become '_'; null gives "".</summary>
        public static string SnakeCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '-' || c == '_')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                    continue;
                }
                if (char.IsUpper(c))
                {
                    bool prevLowerOrDigit = i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]));
                    bool acronymEnd = i > 0 && char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                    if ((prevLowerOrDigit || acronymEnd) && sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString().Trim('_');
        }

        /// <summary>Season enum name to the rule key: Spring|Summer|Fall|Winter; PermanentSummer/PermanentWinter map to summer/winter; else null.</summary>
        public static string SeasonKey(string seasonEnumName)
        {
            switch (seasonEnumName)
            {
                case "Spring": return "spring";
                case "Summer": case "PermanentSummer": return "summer";
                case "Fall": return "fall";
                case "Winter": case "PermanentWinter": return "winter";
                default: return null;
            }
        }

        /// <summary>Storyteller defName to cassandra|phoebe|randy|other.</summary>
        public static string StorytellerKey(string defName)
        {
            string d = (defName ?? "").ToLowerInvariant();
            return d == "cassandra" || d == "phoebe" || d == "randy" ? d : "other";
        }
    }
}

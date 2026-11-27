// Port of agent/rimworldllm_agent/core/adult.py.
// The "Adult content" setting (default OFF). The director sets it from the bridge /config on every refresh.
// OFF: the system prompt carries Rule, and the sanitiser drops explicit sexual lines (reason "adult_off").
// ON:  behaviour is unchanged. The age gate (AgeGate) is a separate, broader filter and is never relaxed here.
using System.Text.RegularExpressions;

namespace RimWorldLLM.Mind
{
    public static class Adult
    {
        public const string Rule = "Romance, attraction, affection and relationships are fine and in character. Anything sexual is only implied " +
            "and fades to black; never describe it.";

        /// <summary>Module-global flag; off until the bridge says otherwise. Written only through SetEnabled.</summary>
        public static bool Enabled { get; private set; }

        private static readonly string[] Words =
        {
            @"sex(?:ual\w*)?(?= (?:with|scene|act|toy)\b)", @"(?:have|having|had|good|rough|wild) sex", @"sexual\w*", @"intercourse",
            @"orgasm\w*", @"climax(?:ed|ing)?", @"aroused", @"arousal", @"horny", @"erotic\w*", @"fuck\w*", @"shag(?:ging|ged)?",
            @"blow ?jobs?", @"hand ?jobs?", @"cum(?:ming|shot)?", @"penis\w*", @"vagin\w*", @"cocks?", @"dicks?", @"pussy", @"clit\w*",
            @"genitals?", @"genitalia", @"boobs?", @"breasts?", @"nipples?", @"naked", @"nude\w*", @"undress\w*", @"stripp?(?:ing|ed) (?:off|naked)",
            @"thrust(?:ing|s)? (?:into|inside)", @"moan(?:ing|s|ed)? (?:with|in) (?:pleasure|ecstasy)", @"between (?:my|your|her|his) (?:legs|thighs)",
            @"make love", @"making love", @"made love", @"kinky", @"fetish\w*", @"slut\w*", @"whore\w*",
        };
        private static readonly Regex Explicit_ = new Regex(@"\b(?:" + string.Join("|", Words) + @")\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Explicit sexual vocabulary (also seen through letter-spacing and leetspeak).</summary>
        public static bool Explicit(string text)
        {
            string t = AgeGate.Norm(text);
            if (Explicit_.IsMatch(t) || Explicit_.IsMatch(AgeGate.Leetspeak(t))) return true;
            string joined = AgeGate.JoinSpaced(t);
            return joined != t && Explicit_.IsMatch(joined);
        }

        /// <summary>Python set_enabled(value): true only for boolean true or the string "true" (any case). The director calls this on every config refresh.</summary>
        public static void SetEnabled(object value)
        {
            if (value is Newtonsoft.Json.Linq.JValue jv) value = jv.Value;
            Enabled = (value is bool b && b) || (value is string s && s.ToLowerInvariant() == "true");
        }

        public static bool Off() { return !Enabled; }
    }
}

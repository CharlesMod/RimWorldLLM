// Port of agent/rimworldllm_agent/core/bystanders.py: bystander reactions (item 9): when two actors argue in front of others, one
// witness shows a tiny reaction cloud. One bystander per argument (the witness with the strongest feelings about the pair), a
// per-actor gap, a very small LLM call (kind "reaction", about 12 tokens) run in a free window as an AMBIENT-class item so it
// waits behind real talk, or a template line when every window is busy.
//
// The Python dataclass `Bystanders` is the class below; the module functions are in BystanderOps.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>Per-actor rate limit (play clock) and counters, for the debug view and tests.</summary>
    public sealed class Bystanders
    {
        public Dictionary<int, double> Last = new Dictionary<int, double>();
        public int Asked, Shown, Templated, Dropped, Skipped;
        public List<JObject> Log = new List<JObject>();

        public bool Ready(int aid, double playNow, double gap)
        {
            double t;
            return !Last.TryGetValue(aid, out t) || playNow - t >= gap;
        }

        public void Mark(int aid, double playNow) { Last[aid] = playNow; }
    }

    public static class BystanderOps
    {
        public const int ReactionMaxTokens = 12;
        public const int MinWords = 2, MaxWords = 6;

        /// <summary>The one bystander: a witness we have a mind for, not a party, past its gap, and the one with the strongest opinion of
        /// either party (ties: the lowest id, so it is stable).</summary>
        public static int? Pick(IEnumerable<int> witnesses, IList<int> parties, IDictionary<int, Mind> minds, Bystanders bs, double playNow, double gap)
        {
            int? best = null;
            int score = -1;
            foreach (int w in witnesses.Distinct().OrderBy(x => x))
            {
                if (parties.Contains(w) || !minds.ContainsKey(w) || !bs.Ready(w, playNow, gap)) continue;
                var rels = minds[w].Relations;
                int s = 0;
                foreach (int p in parties)
                {
                    Relation r;
                    if (rels.TryGetValue(p.ToString(CultureInfo.InvariantCulture), out r)) s = Math.Max(s, Math.Abs(r.Opinion ?? 0));
                }
                if (s > score) { best = w; score = s; }
            }
            return best;
        }

        public static string Template(IList<string> templates, int aid, object key)
        {
            int h = Inner.Sha1Mod(aid.ToString(CultureInfo.InvariantCulture) + ":" + PyStr.StrOf(key), templates.Count);
            return templates[h];
        }

        /// <summary>The whole prompt, kept small: who you are in one line, what you saw, what you feel about the two.</summary>
        public static List<(string, string)> Messages(IGameAdapter adapter, Mind mind, IDictionary<int, string> names, IList<int> parties,
            string summary, bool minor, string words = null)
        {
            string reg = GameData.Clean(!string.IsNullOrEmpty(mind.Voice.RegisterLine) ? mind.Voice.RegisterLine : "plain and dry", "desc");
            var seen = new List<string>();
            foreach (int p in parties)
            {
                Relation r;
                mind.Relations.TryGetValue(p.ToString(CultureInfo.InvariantCulture), out r);
                string op = r != null && r.Opinion != null ? " (you feel " + (r.Opinion >= 0 ? "+" : "-") + Math.Abs((long)r.Opinion.Value).ToString(CultureInfo.InvariantCulture) + " about them)" : "";
                string nm;
                names.TryGetValue(p, out nm);
                seen.Add(GameData.Name(nm ?? "") + op);
            }
            string body = Sanitize.PyStrip(string.Join(" and ", seen) + " are quarrelling in front of you. " + GameData.Clean(summary, "event"));
            string user = "=== WHO YOU ARE ===\n" + GameData.DataSection("voice", "You talk like this: " + reg)
                + "=== WHAT YOU SEE ===\n" + GameData.DataSection("scene", body)
                + "=== REQUEST ===\n" + Speech.RegisterLine(mind.Name, words) + adapter.ReactionRequest;
            string system = adapter.ReactionSystem + "\n\n" + GameData.DataRule + (minor ? "\n\n" + AgeGate.MinorRule : "");
            return new List<(string, string)> { ("system", system), ("user", user) };
        }

        /// <summary>A 2-6 word reaction naming nobody, or null. A name in it, a romantic word with a minor around, or a long line drops it.</summary>
        public static string CleanReaction(string raw, string prompt, int aid, IDictionary<int, ISet<string>> nameTokens, ISet<string> commonWords,
            bool minor, ISet<string> minorNames, int maxChars = 90)
        {
            var allNames = new HashSet<string>();
            if (nameTokens != null) foreach (var v in nameTokens.Values) allNames.UnionWith(v);
            ISet<string> speaker = null;
            if (nameTokens == null || !nameTokens.TryGetValue(aid, out speaker)) speaker = new HashSet<string>();
            var s = Sanitize.SanitizeProse(raw, new HashSet<string>(), allNames, Sanitize.PromptCapWords(prompt), commonWords, speaker,
                MinWords, MaxWords, minor, minorNames);
            if (!s.Ok || string.IsNullOrEmpty(s.Text) || PyStr.Len(s.Text) > maxChars) return null;
            if (AgeGate.Violation(s.Text, minor, minorNames ?? (IEnumerable<string>)new string[0])) return null;
            return s.Text;
        }
    }
}

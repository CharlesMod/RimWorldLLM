// Port of agent/rimworldllm_agent/core/grounding.py: shared helpers for creative_ground: richer, cleaned game facts for
// art, recap, lore and ceremony prompts. Everything returned is game text already passed through GameData.Clean; the
// callers wrap it in data sections. Game-specific wording and word lists come from the adapter (creative_card, recap_importance).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>What grounding needs of the game adapter (the getattr(adapter, "creative_card"/"recap_importance") of the Python).</summary>
    public interface IGroundingAdapter
    {
        /// <summary>False when the adapter has no creative_card (actor_card then returns {} without calling the bridge).</summary>
        bool HasCreativeCard { get; }
        /// <summary>{"traits": [...], "backstory_title", "mood", "mood_thoughts": [...]} from an actor snapshot; null or an empty object for none.</summary>
        JObject CreativeCard(JObject snapshot);
        /// <summary>(regex pattern, weight) pairs; searched case-insensitively.</summary>
        IReadOnlyList<KeyValuePair<string, int>> RecapImportance { get; }
    }

    /// <summary>The idle-job scheduler's one question (core idle.py actors_want_windows).</summary>
    public interface IIdleYield
    {
        bool ActorsWantWindows();
    }

    /// <summary>The director members grounding reads (the Python takes the whole director as d).</summary>
    public interface IGroundingHost
    {
        IBridge Bridge { get; }
        IGroundingAdapter Adapter { get; }
        /// <summary>Null when there is no lease manager.</summary>
        LeaseManager Leases { get; }
        /// <summary>Null when there is no idle scheduler.</summary>
        IIdleYield Idle { get; }
    }

    public static class Grounding
    {
        private static readonly HashSet<string> Stop = new HashSet<string>(StrSplit(
            "the a an and or of in on at to for with from by his her their its this that was were is are it he she they " +
            "them there then when while after before day today night morning evening"));
        private static readonly Regex CapWord = new Regex(@"\b[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,})?\b", RegexOptions.CultureInvariant);
        private static readonly Regex TokenRe = new Regex(@"[A-Za-z']{3,}", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> NotReferent = new HashSet<string>(StrSplit(
            "day days hour hours night morning evening time way rest while first last other same"));

        public const int PlanTokens = 48;
        public const string PlanAsk = "Make a private plan in one line: two concrete details from the data above you will use, and the feeling " +
                                      "the piece carries. Plan only, in under 35 words.";

        private static string[] StrSplit(string s) { return s.Split(' '); }

        /// <summary>str.casefold() for the text this module sees: lower-casing plus the one common full fold (sharp s).</summary>
        internal static string Casefold(string s) { return s.ToLowerInvariant().Replace("ß", "ss"); }

        /// <summary>{"traits": [...], "backstory_title", "mood", "mood_thoughts": [...]}: empty when the game gives none.</summary>
        public static async Task<JObject> ActorCardAsync(IGroundingHost d, int aid)
        {
            var empty = new JObject();
            if (!d.Adapter.HasCreativeCard) return empty;
            JObject snap;
            try { snap = await d.Bridge.ActorAsync(aid).ConfigureAwait(false); }
            catch (BridgeError) { return empty; }
            try { return d.Adapter.CreativeCard(snap) ?? empty; }
            catch (Exception) { return empty; }             // a snapshot shape we do not know: no card, never a crash
        }

        private static bool Truthy(JToken t) { return t != null && BridgeJson.Truthy(t, false); }

        private static string Str(JToken t) { return Py.PyJson.Str(t); }

        public static List<string> CardLines(JObject card)
        {
            var o = new List<string>();
            if (Truthy(card["traits"]))
                o.Add("Traits: " + string.Join(", ", ((IEnumerable<JToken>)card["traits"]).Select(Str)));
            if (Truthy(card["backstory_title"])) o.Add("Background: " + Str(card["backstory_title"]));
            if (Truthy(card["mood"]))
            {
                string t = "Mood now: " + Str(card["mood"]);
                if (Truthy(card["mood_thoughts"]))
                    t += " (" + string.Join(", ", ((IEnumerable<JToken>)card["mood_thoughts"]).Select(Str)) + ")";
                o.Add(t);
            }
            return o;
        }

        public static HashSet<string> TokensOf(IEnumerable<string> names)
        {
            var o = new HashSet<string>();
            foreach (var n in names)
                foreach (Match m in TokenRe.Matches(n ?? "")) o.Add(Casefold(m.Value));
            return o;
        }

        /// <summary>True when the text names one of the (casefolded) tokens as a whole word.</summary>
        public static bool Mentions(string text, ISet<string> tokens)
        {
            if (tokens == null || tokens.Count == 0) return false;
            foreach (Match m in TokenRe.Matches(text ?? "")) if (tokens.Contains(Casefold(m.Value))) return true;
            return false;
        }

        /// <summary>Up to k texts naming the tokens (newest kept); when none do, the newest k.</summary>
        public static List<string> PickMentioning(IList<string> texts, ISet<string> tokens, int k, bool fallbackRecent = true)
        {
            var hit = Tail(texts.Where(t => Mentions(t, tokens)).ToList(), k);
            if (hit.Count > 0 || !fallbackRecent) return hit;
            return Tail(texts, k);
        }

        /// <summary>Python's lst[-k:] (k &gt; 0).</summary>
        internal static List<T> Tail<T>(IList<T> l, int k)
        {
            if (k <= 0) return new List<T>(l);   // [-0:] is the whole list
            int from = Math.Max(0, l.Count - k);
            return l.Skip(from).ToList();
        }

        public static int Importance(IGroundingAdapter adapter, string text, string kind = "")
        {
            int score = 0;
            foreach (var pw in adapter.RecapImportance)
                if (Regex.IsMatch(text ?? "", pw.Key, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) score += pw.Value;
            if (kind == "major") score += 4;
            return score;
        }

        /// <summary>The most important of the last window entries, biggest first (recency breaks ties), at most top.</summary>
        public static List<JObject> RankEntries(IGroundingAdapter adapter, IList<JObject> entries, int window = 12, int top = 4)
        {
            var pool = Tail(entries, window).Select((e, i) => (i, e)).ToList();
            var ranked = pool.OrderBy(p => -Importance(adapter, Py.PyJson.Str(p.e["text"] ?? JValue.CreateString("")),
                                                       Py.PyJson.Str(p.e["kind"] ?? JValue.CreateString(""))))
                             .ThenBy(p => -p.i).ToList();
            return ranked.Take(top).Select(p => p.e).ToList();
        }

        /// <summary>Concrete things a nickname could be built from: the people named, then capitalised names that appear in the
        /// texts. Cleaned, deduplicated, at most k.</summary>
        public static List<string> Referents(IEnumerable<string> texts, IEnumerable<string> names, int k = 8)
        {
            var o = new List<string>();
            var seen = new HashSet<string>();
            Action<string> add = x =>
            {
                x = GameData.Clean(x, "label").Trim(' ', '.', ',', ';', ':');
                string key = Casefold(x);
                if (x.Length > 0 && !seen.Contains(key) && !Stop.Contains(key) && !NotReferent.Contains(key))
                {
                    seen.Add(key);
                    o.Add(x);
                }
            };
            string blob = string.Join(" ", texts);
            foreach (var n in names)
                if (!string.IsNullOrEmpty(n) && Regex.IsMatch(blob, @"\b" + Regex.Escape(n) + @"\b", RegexOptions.CultureInvariant)) add(n);
            // ("the <thing>" phrases were tried: they handed the model fragments like "yard with" as names; judge r1 lore 1.9)
            foreach (Match m in CapWord.Matches(blob))
            {
                string w = m.Value;
                if (!Stop.Contains(Casefold(Py.PyStr.Split(w)[0]))) add(w);
            }
            return o.Take(k).ToList();
        }

        /// <summary>True when a waiting or running item of class REACTIVE or higher belongs to another actor.</summary>
        public static bool OtherReactiveWaiting(IGroundingHost d, int? me)
        {
            var ls = d.Leases;
            if (ls == null) return false;
            return ls.Queue.Any(q => q.Cls >= Cls.Reactive && (me == null || q.ActorId != me.Value))
                || ls.Leases.Values.Any(l => l.Cls >= Cls.Reactive && (me == null || l.ActorId != me.Value));
        }

        /// <summary>For idle jobs (art, lore): stop extra candidates when actors want windows.</summary>
        public static bool IdleShouldYield(IGroundingHost d)
        {
            var idle = d.Idle;
            return idle != null && idle.ActorsWantWindows();
        }
    }
}

// Port of agent/rimworldllm_agent/core/lore.py: shared lore (aliveness item 4): shared nicknames for places and events (a short name
// people say, and what it refers to).
//
// Coined by the narrator in one small extra call after a major chronicle entry (kind "lore", about 24 tokens: "Name: what it refers
// to", or "none"). Stored once in the mod's chronicle book as kind "lore" (text "Name: what it refers to") so it travels with the
// save, and cached here; the oldest fade past max entries. Reused in block D (the entries that match the scene) and in the chronicle
// prompt, so the narrator keeps one set of names.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members lore.py reads.</summary>
    public interface ILoreHost
    {
        ICreativeHost Creative { get; }
        IGroundingHost Ground { get; }
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        MindConfig Cfg { get; }
        IGameAdapter Adapter { get; }
        LeaseManager Leases { get; }
        Chronicle Chronicle { get; }
        LoreBook Lore { get; }
        IDictionary<int, string> Names { get; }
        IDictionary<int, HashSet<string>> NameTokens { get; }
        HashSet<string> MinorTokens();
    }

    public sealed class LoreEntry
    {
        public string Name;
        public string What;
        public int Game;
        public int Day;
        public LoreEntry(string name, string what, int game = 0, int day = 0) { Name = name; What = what; Game = game; Day = day; }
        public string Text { get { return Name + ": " + What; } }
    }

    public sealed class LoreBook
    {
        public int Max;
        public List<LoreEntry> Entries = new List<LoreEntry>();

        public LoreBook(int maxEntries = 12) { Max = maxEntries; }

        public LoreEntry Add(string name, string what, int game = 0, int day = 0)
        {
            string key = Grounding.Casefold(name);
            Entries = Entries.Where(e => Grounding.Casefold(e.Name) != key).ToList();      // a re-coined name replaces itself
            var e2 = new LoreEntry(name, what, game, day);
            Entries.Add(e2);
            if (Max > 0 && Entries.Count > Max) Entries.RemoveRange(0, Entries.Count - Max);   // the oldest fade (Python: del entries[:-max])
            return e2;
        }

        /// <summary>Replace the cache with the kind "lore" entries the mod holds (the save's truth), oldest first.</summary>
        public void Load(IEnumerable<JObject> bridgeEntries)
        {
            Entries = new List<LoreEntry>();
            foreach (var e in bridgeEntries)
            {
                JToken kind = JobsJson.Get(e, "kind");
                if (!(kind != null && kind.Type == JTokenType.String && (string)kind == "lore")) continue;
                var p = Lore.ParseEntry(JobsJson.Str(e, "text"));
                if (p != null)
                {
                    JToken day = JobsJson.Get(e, "day");
                    Add(p.Value.Name, p.Value.What, 0, day != null && PyJson.Truthy(day) ? (int)JobsJson.PyInt(day) : 0);
                }
            }
        }

        public void Rollback(int game) { Entries = Entries.Where(e => e.Game <= game).ToList(); }

        public List<string> Lines() { return Entries.Select(e => e.Text).ToList(); }

        /// <summary>The entries sharing words with the scene (name or referent), best first; none when nothing matches.</summary>
        public List<LoreEntry> Relevant(string sceneText, int k = 3)
        {
            var words = MemoryOps.Keywords(sceneText);
            var scored = new List<(int Hit, int Game, LoreEntry E)>();
            foreach (var e in Entries)
            {
                int hit = words.Count(w => MemoryOps.Keywords(e.Text).Contains(w));
                if (hit > 0) scored.Add((hit, e.Game, e));
            }
            return scored.OrderBy(t => -t.Hit).ThenBy(t => -t.Game).Select(t => t.E).Take(k).ToList();      // OrderBy is stable, like sort()
        }

        /// <summary>The block D line ("Shared names: ..."), or "" when nothing is relevant.</summary>
        public string NamesLine(string sceneText, int k = 3, string label = "Shared names")
        {
            var parts = new List<string>();
            int used = 0;
            foreach (var e in Relevant(sceneText, k))
            {
                string t = GameData.Clean(e.Text, "line", 60);
                if (used + PyStr.Len(t) > Lore.NamesLineChars) break;
                parts.Add(t);
                used += PyStr.Len(t) + 2;
            }
            return parts.Count > 0 ? label + ": " + string.Join("; ", parts) : "";
        }
    }

    public static class Lore
    {
        public const int MaxTokens = 24;
        public const int NameWordsMin = 2, NameWordsMax = 5;
        public const int WhatChars = 60;
        public const int NamesLineChars = 150;
        private static readonly Regex NameOk = new Regex(@"^[A-Za-z][A-Za-z' -]*$", RegexOptions.CultureInvariant);
        private static readonly Regex None = new Regex(@"^\W*none\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // sanitize._REFUSAL (private there; the same pattern)
        private static readonly Regex Refusal = new Regex(
            @"\bas an ai\b|\blanguage model\b|\bi(?:'m| am) (?:sorry|unable|not able)\b[^.!?]{0,40}\b(?:help|assist|continue|comply|provide|write|generate)\b" +
            @"|\bi (?:can't|cannot|won't|will not|am unable to) (?:help|assist|comply|continue|fulfil+|write|generate|provide|engage|roleplay|create)\b" +
            @"|\bi apologi[sz]e\b|\bcontent polic(?:y|ies)\b|\bmy guidelines\b|\bsafety guidelines\b" +
            @"|\bnot appropriate (?:for me )?to\b|\bdisclaimer\b|\bfictional (?:scenario|setting)\b|\bin this roleplay\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly char[] StripSet = { ' ', '"', '\'' };
        private static readonly char[] StripSetDot = { ' ', '"', '\'', '.' };

        /// <summary>"Name: what" with a 2-5 word name and a short referent; null for "none" or anything else.</summary>
        public static (string Name, string What)? ParseEntry(string text)
        {
            string t = PyStr.CollapseSpaces(text).Trim(StripSet);
            if (t.Length == 0 || None.IsMatch(t) || t.IndexOf(':') < 0) return null;
            int i = t.IndexOf(':');
            string name = t.Substring(0, i).Trim(StripSetDot);
            string what = t.Substring(i + 1).Trim(StripSetDot);
            int words = PyStr.Split(name).Count;
            if (!(NameWordsMin <= words && words <= NameWordsMax) || PyStr.Len(name) > 40 || what.Length == 0) return null;
            return (name, RStrip(PyStr.Head(what, WhatChars)));
        }

        private static string RStrip(string s)
        {
            int b = s.Length;
            while (b > 0 && PyStr.IsSpace(s[b - 1])) b--;
            return s.Substring(0, b);
        }

        /// <summary>referents (creative_ground) is the list of concrete things a name can be built from; null keeps today's prompt.</summary>
        public static List<LlmMessage> Messages(IGameAdapter adapter, string entryText, IList<string> existing, IList<string> referents = null)
        {
            string have = string.Join("\n", existing.Select(t => "- " + GameData.Clean(t, "line", 80)));
            if (have.Length == 0) have = "- none yet";
            string refSec = "";
            string request = adapter.LoreRequest;
            if (referents != null)
            {
                string body = string.Join("\n", referents.Select(r => "- " + GameData.Clean(r, "label")));
                if (body.Length == 0) body = "- (nothing concrete)";
                refSec = "=== REFERENTS TO NAME FROM ===\n" + GameData.DataSection("referents", body);
                request = adapter.LoreRequestGround ?? request;
            }
            string user = "=== CHRONICLE ENTRY ===\n" + GameData.DataSection("entry", GameData.Clean(entryText, "event", 700))
                + refSec
                + "=== NAMES ALREADY IN USE ===\n" + GameData.DataSection("names", have)
                + "=== REQUEST ===\n" + request;
            return new List<LlmMessage>
            {
                new LlmMessage("system", adapter.LoreSystem + "\n\n" + GameData.DataRule + "\n" + AgeGate.MinorRule),
                new LlmMessage("user", user),
            };
        }

        /// <summary>One small narrator call after a major entry. Stores and returns the new entry, or null ("none", unusable text).</summary>
        public static async Task<LoreEntry> CoinAsync(ILoreHost d, string entryText, int game, int day)
        {
            if (!d.Cfg.Lore || d.Leases.NarratorOwner != null) return null;     // off, or a steward cycle holds slot 0: no new coinage now
            List<string> referents = null;
            if (Creative.On(d.Creative, "ground", "lore"))
            {
                var recent = Grounding.Tail(d.Chronicle.State.Recent.Select(e => e.Text).ToList(), 5);
                var texts = new List<string> { entryText };
                texts.AddRange(recent);
                referents = Grounding.Referents(texts, d.Names.Values);
            }
            var msgs = Messages(d.Adapter, entryText, d.Lore.Lines(), referents);
            var allnames = JobsJson.Union(d.NameTokens.Values);
            var minors = d.MinorTokens();
            var pw = Sanitize.PromptCapWords(msgs[1].Text);
            int slot = d.Cfg.NarratorSlot;

            async Task<string> write(string note)
            {
                var m = string.IsNullOrEmpty(note) ? msgs : JobsJson.WithNote(msgs, note);
                var c = await d.Llm.CompleteAsync(m, slot, "lore", MaxTokens, new[] { "\n" }, null, 0.7).ConfigureAwait(false);
                if (None.IsMatch(c.Text ?? "")) return null;
                var p = ParseEntry(PyStr.CollapseSpaces(c.Text));
                if (p == null || Refusal.IsMatch(c.Text) || !NameOk.IsMatch(p.Value.Name) || AgeGate.Violation(c.Text, false, minors)) return null;
                if (p.Value.Name.Trim(' ', '.').ToLowerInvariant() == p.Value.What.Trim(' ', '.').ToLowerInvariant())
                    return null;                                  // "Yard with: Yard with" names nothing
                // the name is an invention by design; what it refers to must stand on the entry's own words and names
                var s = Sanitize.SanitizeProse(p.Value.What, allnames, allnames, pw, new HashSet<string>(d.Adapter.CommonWords), null, 2, 12, false, minors);
                if (!s.Ok) return null;
                return p.Value.Name + ": " + s.Text.TrimEnd('.', ' ');
            }

            async Task<string> plan()
            {
                var c = await d.Llm.CompleteAsync(JobsJson.PlanMessages(msgs), slot, "lore", Grounding.PlanTokens, new[] { "\n" }, null, 0.7).ConfigureAwait(false);
                return c.Text;
            }

            string text;
            try
            {
                (text, _) = await Creative.ComposeAsync(d.Creative, "lore", slot, null, JobsJson.BeforeRequest(msgs[1].Text), new HashSet<string>(), write, plan,
                    () => Grounding.IdleShouldYield(d.Ground)).ConfigureAwait(false);
            }
            catch (LlmError ex)
            {
                LlmLog.Info("rimworldllm.core.lore", "lore call failed: " + ex.Message);
                return null;
            }
            var pe = !string.IsNullOrEmpty(text) ? ParseEntry(text) : null;
            if (pe == null) return null;
            var e2 = d.Lore.Add(pe.Value.Name, pe.Value.What, game, day);
            try { await d.Bridge.ChronicleAsync(day, e2.Text, "lore").ConfigureAwait(false); }
            catch (BridgeError ex) { LlmLog.Info("rimworldllm.core.lore", "lore kept in the agent only: " + ex.Message); }
            return e2;
        }

        /// <summary>Read the save's lore from the mod (after a start or a save_epoch change).</summary>
        public static async Task LoadAsync(ILoreHost d)
        {
            try { d.Lore.Load(await d.Bridge.GetChronicleAsync().ConfigureAwait(false)); }
            catch (BridgeError ex) { LlmLog.Info("rimworldllm.core.lore", "no lore read: " + ex.Message); }
        }
    }
}

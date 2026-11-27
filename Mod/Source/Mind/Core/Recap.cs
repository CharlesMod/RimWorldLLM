// Port of agent/rimworldllm_agent/core/recap.py: the "Previously on ..." recap shown when the player loads a save (aliveness item 12).
//
// One narrator-slot call, once per save epoch, built from the save's own chronicle (the mod's copy is the truth after a load). The
// result is 3 short lines POSTed to /recap; the mod decides whether to show them. A failed or unusable call falls back to a recap
// built from the chronicle text with no model.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members recap.py reads.</summary>
    public interface IRecapHost
    {
        ICreativeHost Creative { get; }
        IGroundingHost Ground { get; }
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        MindConfig Cfg { get; }
        IGameAdapter Adapter { get; }
        JObject BridgeCfg { get; }
        IDictionary<int, HashSet<string>> NameTokens { get; }
        /// <summary>The epoch a recap was last tried for (read and written).</summary>
        int RecapEpoch { get; set; }
        HashSet<string> MinorTokens();
    }

    public static class Recap
    {
        public const int MinEntries = 2;       // fewer chronicle entries than this: nothing to recap
        public const int UseEntries = 4;       // the last few entries go into the prompt
        public const int Lines = 3;
        public const int LineChars = 160;
        public const int MaxTokens = 90;
        public const int RankWindow = 12;      // creative_ground: the last this-many entries are ranked
        public const int RankTop = 4;          // ... and the top ones go in, biggest first
        public const string GroundRequest =
            "Write exactly 3 lines, one short sentence each, under 20 words. Lead with the entry marked (biggest): " +
            "name who it happened to and what changed. Then the others in the order they happened; keep separate " +
            "events separate. End on what is still unresolved. No title, no numbering, no quotes.";

        private static readonly Regex Lead = new Regex(@"^\s*(?:[-*•>]+|\d+[.)])\s*", RegexOptions.CultureInvariant);
        private static readonly Regex Sent = new Regex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant);

        private static int DefaultDay(JObject e) { return (int)JobsJson.PyInt(JobsJson.Get(e, "day") ?? new JValue(0)) + 1; }

        /// <summary>System + user messages for the recap call. groundAdapter non-null (creative_ground) ranks the last RankWindow entries by
        /// importance and lists the top ones in the order they happened, the biggest marked.</summary>
        public static List<LlmMessage> Messages(IGameAdapter adapter, IList<JObject> entries, Func<JObject, int> dayLabel = null,
            IGroundingAdapter groundAdapter = null)
        {
            dayLabel = dayLabel ?? DefaultDay;
            string user;
            if (groundAdapter != null)
            {
                var top = Grounding.RankEntries(groundAdapter, entries, RankWindow, RankTop);
                var ids = new HashSet<JObject>(top, ReferenceEqualityComparer.Instance as IEqualityComparer<JObject>);
                var window = entries.Skip(Math.Max(0, entries.Count - RankWindow));
                var order = window.Where(e => ids.Contains(e));
                string body = string.Join("\n", order.Select(e => (top.Count > 0 && ReferenceEquals(e, top[0]) ? "(biggest) " : "") +
                    "Day " + dayLabel(e) + ": " + GameData.Clean(JobsJson.StrOrEmpty(e, "text"), "event", 320)));
                user = "=== CHRONICLE (oldest first) ===\n" + GameData.DataSection("chronicle", body) +
                       "=== REQUEST ===\n" + (adapter.RecapRequestGround ?? GroundRequest);
            }
            else
            {
                string body = string.Join("\n", entries.Skip(Math.Max(0, entries.Count - UseEntries))
                    .Select(e => "Day " + dayLabel(e) + ": " + GameData.Clean(JobsJson.StrOrEmpty(e, "text"), "event", 320)));
                user = "=== CHRONICLE (oldest first) ===\n" + GameData.DataSection("chronicle", body) + "=== REQUEST ===\n" + adapter.RecapRequest;
            }
            return new List<LlmMessage>
            {
                new LlmMessage("system", adapter.RecapSystem + "\n\n" + GameData.DataRule + "\n" + AgeGate.MinorRule),
                new LlmMessage("user", user),
            };
        }

        public static List<string> ParseLines(string raw)
        {
            var o = new List<string>();
            foreach (var ln0 in JobsJson.SplitLines(raw))
            {
                string ln = Lead.Replace(ln0, "", 1).Trim();
                if (ln.Length > 0) o.Add(ln);
            }
            return o.Take(Lines).ToList();
        }

        /// <summary>No model: the first sentence of each of the last 3 entries, oldest first.</summary>
        public static List<string> FallbackLines(IList<JObject> entries, ISet<string> minorNames = null)
        {
            var o = new List<string>();
            foreach (var e in entries.Skip(Math.Max(0, entries.Count - Lines)))
            {
                string t = GameData.Clean(JobsJson.StrOrEmpty(e, "text"), "event", 400);
                string first = Sent.Split(t, 2)[0].Trim();
                if (first.Length == 0 || AgeGate.Violation(first, false, minorNames ?? (ISet<string>)new HashSet<string>())) continue;
                o.Add(JobsJson.Cut(first, LineChars));
            }
            return o;
        }

        /// <summary>The recap lines (1 to 3), from the model when it gives usable lines, else from the chronicle itself.</summary>
        public static async Task<List<string>> BuildAsync(IRecapHost d, IList<JObject> entries)
        {
            var minors = d.MinorTokens();
            var msgs = Messages(d.Adapter, entries, null, Creative.On(d.Creative, "ground", "recap") ? d.Ground.Adapter : null);
            var allnames = JobsJson.Union(d.NameTokens.Values);
            var pw = Sanitize.PromptCapWords(msgs[1].Text);
            int slot = d.Cfg.NarratorSlot;

            async Task<string> write(string note)
            {
                var m = string.IsNullOrEmpty(note) ? msgs : JobsJson.WithNote(msgs, note);
                var c = await d.Llm.CompleteAsync(m, slot, "recap", MaxTokens, new string[0], null, 0.7).ConfigureAwait(false);
                var got = new List<string>();
                foreach (var raw in ParseLines(c.Text))
                {
                    var s = Sanitize.SanitizeProse(raw, allnames, allnames, pw, new HashSet<string>(d.Adapter.CommonWords), null, 3, 30, false, minors);
                    if (s.Ok) got.Add(JobsJson.Cut(s.Text, LineChars));
                }
                return got.Count >= 2 ? string.Join("\n", got) : null;
            }

            async Task<string> plan()
            {
                var c = await d.Llm.CompleteAsync(JobsJson.PlanMessages(msgs), slot, "recap", Grounding.PlanTokens, new[] { "\n" }, null, 0.7).ConfigureAwait(false);
                return c.Text;
            }

            var lines = new List<string>();
            try
            {
                var (text, _) = await Creative.ComposeAsync(d.Creative, "recap", slot, null, JobsJson.BeforeRequest(msgs[1].Text), new HashSet<string>(),
                    write, plan, () => Grounding.OtherReactiveWaiting(d.Ground, null)).ConfigureAwait(false);
                lines = !string.IsNullOrEmpty(text) ? JobsJson.SplitLines(text) : new List<string>();
            }
            catch (LlmError ex)
            {
                LlmLog.Info("rimworldllm.core.recap", "recap call failed: " + ex.Message);
            }
            if (lines.Count < 2) lines = FallbackLines(entries, minors);
            return lines.Take(Lines).ToList();
        }

        /// <summary>Once per epoch: read the save's chronicle, write the recap, POST it. True when the mod took it.</summary>
        public static async Task<bool> RunAsync(IRecapHost d, int epoch)
        {
            if (epoch == 0 || d.RecapEpoch == epoch || !d.Cfg.RecapEnabled || JobsJson.IsFalse(JobsJson.Get(d.BridgeCfg, "recap"))) return false;
            d.RecapEpoch = epoch;
            List<JObject> all;
            try { all = await d.Bridge.GetChronicleAsync().ConfigureAwait(false); }
            catch (BridgeError ex) { LlmLog.Info("rimworldllm.core.recap", "recap skipped, no chronicle: " + ex.Message); return false; }
            var entries = all.Where(e => JobsJson.StrOrEmpty(e, "text").Trim().Length > 0 && !(JobsJson.Get(e, "kind") is JToken k && k.Type == JTokenType.String && (string)k == "lore")).ToList();   // lore is not history
            if (entries.Count < MinEntries) return false;
            var lines = await BuildAsync(d, entries).ConfigureAwait(false);
            if (lines.Count == 0 || d.Bridge.Env.Epoch != epoch) return false;      // nothing usable, or another load came meanwhile
            try { await d.Bridge.RecapAsync(epoch, lines).ConfigureAwait(false); }
            catch (BridgeError ex) { LlmLog.Info("rimworldllm.core.recap", "recap not shown: " + ex.Message); return false; }
            return true;
        }

        /// <summary>A late connect: the mod says a recap is still open (GET /config recap_open) for an epoch we have not tried.</summary>
        public static bool LateDue(IRecapHost d)
        {
            int epoch = d.Bridge.Env.Epoch;
            return epoch != 0 && JobsJson.IsTrue(JobsJson.Get(d.BridgeCfg, "recap_open")) && d.RecapEpoch != epoch
                && d.Cfg.RecapEnabled && !JobsJson.IsFalse(JobsJson.Get(d.BridgeCfg, "recap"));
        }
    }
}

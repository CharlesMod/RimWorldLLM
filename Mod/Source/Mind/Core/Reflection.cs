// Port of agent/rimworldllm_agent/core/reflection.py (SPEC section 5): one structured LLM call rewrites an actor's evolving bio,
// wants and relations and merges memories. It is the only place block C changes.
//
// The wording of the prompt comes from the adapter (ReflectionSystem / ReflectionRequest); this module supplies the facts, the
// JSON schema, the parsing and the application to the mind.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public class ReflectRelation
    {
        [PyRequired] [JsonProperty("who")] public string Who { get; set; }
        [JsonProperty("feeling")] public string Feeling { get; set; } = "";
        [JsonProperty("history")] public string History { get; set; } = "";
    }

    public class ReflectMerge
    {
        [PyRequired] [JsonProperty("ids")] public List<string> Ids { get; set; }
        [PyRequired] [JsonProperty("summary")] public string Summary { get; set; }
    }

    public class ReflectResult
    {
        [PyRequired] [JsonProperty("bio")] public string Bio { get; set; }
        [JsonProperty("wants")] public List<string> Wants { get; set; } = new List<string>();
        [JsonProperty("relations")] public List<ReflectRelation> Relations { get; set; } = new List<ReflectRelation>();
        [JsonProperty("merge")] public List<ReflectMerge> Merge { get; set; } = new List<ReflectMerge>();
        [JsonProperty("habit")] public string Habit { get; set; } = "";
        /// <summary>optional: one new lasting worry (core/concerns.py)</summary>
        [JsonProperty("concern")] public string Concern { get; set; } = "";
        /// <summary>optional: ids of worries now over</summary>
        [JsonProperty("resolved")] public List<string> Resolved { get; set; } = new List<string>();
    }

    /// <summary>What apply_reflection returns (a dict in the Python); Habit is null when the "habit" key is absent.</summary>
    public sealed class ReflectApplied
    {
        public string Bio;
        public List<string> Wants = new List<string>();
        public int Relations;
        public int Merged;
        public string Habit;
    }

    public static class Reflection
    {
        public const int MaxPromptMemories = 24;
        public const int MaxBioWords = 90;
        public const int MaxWantWords = 20;
        public const int MaxLineWords = 30;

        public static JObject ReflectSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["bio"] = new JObject { ["type"] = "string" },
                    ["wants"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = 3 },
                    ["relations"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["who"] = new JObject { ["type"] = "string" },
                                ["feeling"] = new JObject { ["type"] = "string" },
                                ["history"] = new JObject { ["type"] = "string" },
                            },
                            ["required"] = new JArray("who", "feeling", "history"),
                        },
                    },
                    ["merge"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["minItems"] = 2 },
                                ["summary"] = new JObject { ["type"] = "string" },
                            },
                            ["required"] = new JArray("ids", "summary"),
                        },
                    },
                },
                ["required"] = new JArray("bio", "wants", "relations", "merge"),
            };
        }

        /// <summary>REFLECT_SCHEMA, plus the optional "habit" field (item 7) when this reflection may change a habit.</summary>
        public static JObject SchemaFor(bool habits)
        {
            var b = ReflectSchema();
            if (!habits) return b;
            ((JObject)b["properties"])["habit"] = new JObject { ["type"] = "string" };
            ((JArray)b["required"]).Add("habit");
            return b;
        }

        /// <summary>Unreflected importance passed the threshold, or the interval ran out with something new to reflect on.</summary>
        public static bool ReflectionDue(Mind mind, long now, long day, double importance, double everyDays)
        {
            if (mind.UnreflectedImportance <= 0) return false;
            if (mind.UnreflectedImportance >= importance) return true;
            return now - mind.LastReflectTime >= everyDays * day;
        }

        private static string Signed(int n) { return (n >= 0 ? "+" : "-") + Math.Abs((long)n).ToString(CultureInfo.InvariantCulture); }

        public static List<(string, string)> ReflectionMessages(IGameAdapter adapter, Mind mind, IDictionary<int, string> names, long now, long day,
            bool inner = false, bool habits = false, bool concerns = false)
        {
            var mems = MemoryOps.WeightSorted(mind, now, (int)day).Take(MaxPromptMemories).ToList();
            mems = mems.OrderBy(m => m.Game).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            string memLines = string.Join("\n", mems.Select(m => m.Id + (m.Pinned ? " [pinned]" : "") + ": " + GameData.Clean(m.Text, "event")));
            if (memLines.Length == 0) memLines = "(none)";
            var rels = mind.Relations.Values.OrderBy(r => -Math.Abs(r.Opinion ?? 0)).Take(8).ToList();
            string relLines = string.Join("\n", rels.Select(r =>
                "- " + GameData.Name(r.Name) + (r.Opinion != null ? " (opinion " + Signed(r.Opinion.Value) + ")" : "") +
                (r.Feeling.Length > 0 ? ": " + GameData.Clean(r.Feeling, "line") : "")));
            if (relLines.Length == 0) relLines = "(nobody yet)";
            string bio = GameData.Clean(mind.Fixed.Text, "line", 600, true);
            string becoming = GameData.Clean(mind.EvolvingBio.Length > 0 ? mind.EvolvingBio : "(nothing yet)", "desc");
            string wants = string.Join("; ", mind.Wants.Select(w => GameData.Clean(w, "label")));
            if (wants.Length == 0) wants = "none";
            string worries = concerns ? ConcernOps.WorriesBlock(mind) : "";
            string card = "=== WHO YOU ARE ===\n" + GameData.DataSection("bio", bio);   // fixed per actor: rides in the system message
            string user = "=== WHO YOU ARE BECOMING NOW ===\n" + GameData.DataSection("character", "Becoming: " + becoming + "\n" + "Wants: " + wants)
                + "=== WHAT YOU REMEMBER (id: text) ===\n" + GameData.DataSection("memories", memLines)
                + "=== THE PEOPLE YOU KNOW ===\n" + GameData.DataSection("people", relLines)
                + (inner && mind.Inner.Count > 0
                    ? "=== WHAT YOU HAVE BEEN THINKING ===\n" + GameData.DataSection("thoughts", Inner.ThoughtsText(mind.Inner)) : "")
                + (habits ? "=== YOUR VERBAL HABITS NOW ===\n" + GameData.DataSection("habits", Habits.HabitsLine(mind)) : "")
                + (worries.Length > 0 ? "=== YOUR WORRIES (id: text) ===\n" + GameData.DataSection("worries", worries) : "")
                + "=== REQUEST ===\n" + adapter.ReflectionRequest
                + (habits ? " " + (adapter.ReflectionHabitRequest ?? "") : "")
                + (concerns ? ConcernOps.ReflectConcernRequest : "");
            return new List<(string, string)> { ("system", adapter.ReflectionSystem + "\n\n" + GameData.DataRule + "\n" + card), ("user", user) };
        }

        public static ReflectResult ParseResult(string text)
        {
            string t = Sanitize.PyStrip(Think.StripThought(text));
            int a = t.IndexOf('{'), b = t.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            JToken tok;
            if (!PyJson.TryLoads(t.Substring(a, b + 1 - a), out tok)) return null;
            try { return PyModel.Validate<ReflectResult>(tok); }
            catch (PyValidationException) { return null; }
        }

        /// <summary>The one call. Null if the model failed, was malformed or wrote an ungrounded bio. habits: this reflection may
        /// carry a new verbal habit (the caller checked Habits.HabitAllowed); a bad one is blanked, never fatal.
        /// concerns: it may add one worry and resolve others (core/concerns.py).</summary>
        public static async Task<ReflectResult> RunReflectionAsync(ILlmClient llm, IGameAdapter adapter, Mind mind, IDictionary<int, string> names,
            int slot, long now, long day, int maxTokens = 500, IDictionary<int, ISet<string>> nameTokens = null, ISet<string> commonWords = null,
            bool involvesMinor = false, ISet<string> minorNames = null, bool inner = false, bool habits = false, bool concerns = false)
        {
            var msgs = ReflectionMessages(adapter, mind, names, now, day, inner, habits, concerns);
            Completion c;
            try
            {
                c = await llm.CompleteAsync(Passes.Msgs(msgs), slot, "reflect", maxTokens, new string[0], null, 0.7, mind.ActorId,
                    ConcernOps.ReflectSchema(SchemaFor(habits), concerns)).ConfigureAwait(false);
            }
            catch (LlmError)
            {
                return null;
            }
            var res = ParseResult(c.Text);
            if (res == null) return null;
            var toks = nameTokens ?? new Dictionary<int, ISet<string>>();
            var allNames = new HashSet<string>();
            foreach (var v in toks.Values) allNames.UnionWith(v);
            ISet<string> speaker;
            if (!toks.TryGetValue(mind.ActorId, out speaker)) speaker = new HashSet<string>();
            string userText = msgs[0].Item2 + "\n" + msgs[1].Item2;   // the card (system) and the rest (user)
            var s = Sanitize.SanitizeProse(res.Bio, allNames, allNames, Sanitize.PromptCapWords(userText), commonWords, speaker, 4, MaxBioWords,
                involvesMinor, minorNames);
            if (!s.Ok) return null;
            res.Bio = s.Text;
            res.Habit = habits ? Habits.CleanHabit(res.Habit, userText, toks, mind.ActorId, commonWords, involvesMinor, minorNames) : "";
            if (concerns)                // a bad new worry or a stray id is dropped; the reflection stands
            {
                res.Concern = ConcernOps.SanitizeConcern(res.Concern, new ConcernCtx
                {
                    Known = allNames, All = allNames, PromptWords = Sanitize.PromptCapWords(userText), CommonWords = commonWords,
                    Speaker = speaker, Minor = involvesMinor, MinorNames = minorNames,
                });
                res.Resolved = res.Resolved.Take(4).ToList();
            }
            else
            {
                res.Concern = ""; res.Resolved = new List<string>();
            }
            return res;
        }

        private static string Clean(string t, int maxWords)
        {
            var w = Sanitize.PySplit(string.Join(" ", Sanitize.PySplit(t)));
            return Sanitize.PyStrip(string.Join(" ", w.Take(maxWords)));
        }

        /// <summary>Write a reflection into the mind: new bio (as a diff), wants, relation lines, merged memories.
        /// Resets the unreflected counter and refreshes block C's relations digest. nameIds keys are lower-case names.</summary>
        public static ReflectApplied ApplyReflection(Mind mind, ReflectResult res, long now, IDictionary<string, int> nameIds,
            int? innerKeep = null, IDictionary<string, string> reputations = null, int repLimit = 3,
            double? habitEveryDays = null, long day = 60000)
        {
            var o = new ReflectApplied { Bio = res.Bio };
            if (habitEveryDays != null && !string.IsNullOrEmpty(res.Habit))       // null: habits are off, a stray field is ignored
                o.Habit = Habits.ApplyHabit(mind, res.Habit, now, day, habitEveryDays.Value) ? res.Habit : "";
            mind.AddBioDiff(now, res.Bio);
            var wants = res.Wants.Select(w => Clean(w, MaxWantWords)).Where(w => w.Length > 0).Take(3).ToList();
            if (wants.Count > 0) mind.Wants = wants;
            o.Wants = new List<string>(mind.Wants);
            foreach (var r in res.Relations)
            {
                int pid;
                if (!nameIds.TryGetValue(Sanitize.PyStrip(r.Who).ToLowerInvariant(), out pid) || pid == mind.ActorId) continue;
                string key = pid.ToString(CultureInfo.InvariantCulture);
                Relation rel;
                if (!mind.Relations.TryGetValue(key, out rel))
                {
                    rel = new Relation { Name = Sanitize.PyStrip(r.Who) };
                    mind.Relations[key] = rel;
                }
                if (Sanitize.PyStrip(r.Feeling).Length > 0) rel.Feeling = Clean(r.Feeling, MaxLineWords);
                if (Sanitize.PyStrip(r.History).Length > 0) rel.History = Clean(r.History, MaxLineWords);
                rel.UpdatedTime = now;
                o.Relations++;
            }
            var byId = new Dictionary<string, Memory>();
            foreach (var m in mind.Memories) byId[m.Id] = m;
            var used = new HashSet<string>();
            foreach (var mg in res.Merge)
            {
                var ids = mg.Ids.Distinct().ToList();
                var ms = ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
                string text = Clean(mg.Summary, 40);
                if (ms.Count < 2 || ms.Count != ids.Count || Sanitize.PySplit(text).Length < 3 || ids.Any(used.Contains)) continue;
                if (ms.Any(m => m.Pinned)) continue;                // deaths, betrayals and firsts are never folded away
                used.UnionWith(ids);
                foreach (var m in ms) mind.Memories.Remove(m);
                mind.Memories.Add(SummaryMemory(mind, ms, text));
                o.Merged += ms.Count;
            }
            mind.UnreflectedImportance = 0.0;
            mind.LastReflectTime = now;
            mind.FreezeRelations(6, reputations, repLimit);
            if (innerKeep != null) mind.Inner = innerKeep.Value > 0 ? Mind.Tail(mind.Inner, innerKeep.Value) : new List<InnerThought>();   // the reflection condensed the stream
            return o;
        }

        public static Memory SummaryMemory(Mind mind, IList<Memory> ms, string text)
        {
            string vis = ms.Any(m => m.Visibility == "private") ? "private" : ms.Any(m => m.Visibility == "told") ? "told" : "public";
            var toldTo = ms.SelectMany(m => m.ToldTo).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            return new Memory
            {
                Id = mind.NextMemId(), Game = ms.Max(m => m.Game), Text = text, Confidence = ms.Min(m => m.Confidence), Visibility = vis,
                ToldTo = toldTo, Importance = ms.Max(m => m.Importance) * 0.9,
                People = ms.SelectMany(m => m.People).Distinct().OrderBy(x => x).ToList(),
                Etype = ms.Select(m => m.Etype).Distinct().Count() == 1 ? ms[0].Etype : "summary",
                SummaryOf = ms.Sum(m => Math.Max(1, m.SummaryOf)),
            };
        }
    }
}

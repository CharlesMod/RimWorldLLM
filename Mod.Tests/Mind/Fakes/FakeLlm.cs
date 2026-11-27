// Port of agent/rimworldllm_agent/core/fakes.py (FakeLLM): a deterministic fake client for tests and offline replay.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;

namespace RimWorldLLM.Tests.Mind.Fakes
{
    /// <summary>One recorded call (Python: a dict with these keys).</summary>
    public sealed class FakeCall
    {
        public string Kind;
        public int? Slot;
        public IReadOnlyList<LlmMessage> Messages;
        public string Grammar;
        public int? Actor;
        public JObject JsonSchema;
    }

    /// <summary>Deterministic: a line is picked by hashing (actor, last 200 characters of the prompt). Script maps kind to a queue of replies
    /// consumed in order (then falls back to canned). Slot prompts drive a crude prefix-cache model (4 characters per token).</summary>
    public sealed class FakeLlm : ILlmClient
    {
        private static readonly string[] CannedLine =
        {
            "Hm. Noted.", "Not my favourite thing.", "Right. What now?", "That is going to hurt tomorrow.",
            "Keep your head down.", "I have seen worse. Not much worse.", "Somebody get the medicine.",
        };
        private const string Aim = "I just want this to be over with.";
        private static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
        {
            ["reflect"] = "{\"bio\": \"The recent days have left a mark on me, and I am changing slowly.\", \"wants\": [\"Get through the week\"], \"relations\": [], \"merge\": []}",
            ["diary"] = "Another day behind me. I kept my head down and got through it.",
            ["thought"] = "That stew had better be hot.",
            ["reaction"] = "Not again, you two.",
            ["chronicle"] = "The community got through another day with what it had, and little more than that.",
            ["art"] = "Hana made this over two quiet evenings, and it shows a lot of patience for a plain piece.",
            ["chronicle_summary"] = "Earlier days: the community endured and kept working.",
        };
        private static readonly Regex Kind_ = new Regex(@"\b(thanks|friend|sit|eat)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Insult = new Regex(@"\b(idiot|useless|fool)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public Dictionary<string, List<string>> Script = new Dictionary<string, List<string>>();
        public List<FakeCall> Calls = new List<FakeCall>();
        public Dictionary<int, string> SlotPrompts = new Dictionary<int, string>();
        public bool Fail;

        private static int[] Cps(string s)
        {
            var l = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { l.Add(char.ConvertToUtf32(s[i], s[i + 1])); i++; }
                else l.Add(s[i]);
            }
            return l.ToArray();
        }

        private static string Tail(string s, int n)
        {
            var c = Cps(s);
            if (c.Length <= n) return s;
            var sb = new StringBuilder();
            foreach (int cp in c.Skip(c.Length - n)) sb.Append(cp > 0xFFFF ? char.ConvertFromUtf32(cp) : ((char)cp).ToString());
            return sb.ToString();
        }

        public Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                              IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                              int? actor = null, JObject jsonSchema = null)
        {
            if (Fail) throw new LlmError("fake failure");
            string flat = string.Join("\n", messages.Select(m => m.Text));
            Calls.Add(new FakeCall { Kind = kind, Slot = slot, Messages = messages, Grammar = grammar, Actor = actor, JsonSchema = jsonSchema });
            string text;
            if (Script.TryGetValue(kind, out var q) && q.Count > 0) { text = q[0]; q.RemoveAt(0); }
            else if (kind == "intent")
            {
                string last = messages.Count >= 2 ? messages[messages.Count - 2].Text : "";
                text = Kind_.IsMatch(last) ? "kind" : Insult.IsMatch(last) ? "insult" : "none";
                if (!string.IsNullOrEmpty(grammar) && grammar.Contains("aim ::="))
                    text = grammar.StartsWith("root ::= aim", StringComparison.Ordinal) ? Aim : text + " normal " + Aim;
            }
            else if (Defaults.TryGetValue(kind, out var d)) text = d;
            else if (kind == "line")
            {
                byte[] hash = SHA1.HashData(new UTF8Encoding(false).GetBytes((actor.HasValue ? actor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "None") + Tail(flat, 200)));
                var h = new BigInteger(hash, isUnsigned: true, isBigEndian: true);
                text = CannedLine[(int)(h % CannedLine.Length)];
            }
            else text = "Fine.";
            int key = slot ?? -1;
            SlotPrompts.TryGetValue(key, out string prev);
            var pc = Cps(prev ?? ""); var fc = Cps(flat);
            int common = 0;
            while (common < pc.Length && common < fc.Length && pc[common] == fc[common]) common++;
            SlotPrompts[key] = flat;
            var st = new CallStats
            {
                Kind = kind, Slot = slot, Actor = actor,
                PromptN = Math.Max(1, (fc.Length - common) / 4), CacheN = common / 4, PromptMs = 10.0,
                PredictedN = Math.Max(1, Cps(text).Length / 4), PredictedMs = 20.0,
            };
            return Task.FromResult(new Completion(text, st));
        }
    }
}

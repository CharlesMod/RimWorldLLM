// Port of agent/rimworldllm_agent/core/builders.py: generic mind building. Ask the adapter for the fixed bio and voice card,
// optionally write the voice card's example lines with the LLM once. Game-specific templates live in the adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public static class MindBuilders
    {
        /// <summary>Optional pass: 3 example lines, written once and saved in the mind. Null when the call fails or the lines are not 3 usable ones.</summary>
        public static async Task<List<string>> LlmExampleLinesAsync(ILlmClient llm, IGameAdapter adapter, Mind mind, int? slot = 0)
        {
            string prompt = adapter.VoiceExamplePrompt(mind.Fixed.Text, mind.Voice.RegisterLine, string.Join("; ", mind.Voice.Tics));
            Completion c;
            try
            {
                c = await llm.CompleteAsync(new LlmMessage[] { ("user", prompt) }, slot, "voice_card", 120, new string[0], null, null, mind.ActorId).ConfigureAwait(false);
            }
            catch (Exception) { return null; }
            var lines = new List<string>();
            foreach (var raw in SplitLines(c.Text))
            {
                if (PyStr.Strip(raw).Length == 0) continue;
                // l.strip().strip('"').lstrip("-* ").strip()
                string l = PyStr.Strip(raw).Trim('"').TrimStart('-', '*', ' ');
                lines.Add(PyStr.Strip(l));
            }
            lines = lines.Where(l => { int n = PyStr.Split(l).Count; return n >= 2 && n <= 30; }).Take(3).ToList();
            return lines.Count == 3 ? lines : null;
        }

        /// <summary>str.splitlines(): \n \r \r\n \v \f \x1c-\x1e \x85 \u2028 \u2029.</summary>
        private static List<string> SplitLines(string s)
        {
            var r = new List<string>();
            int st = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool brk = ch == '\n' || ch == '\r' || ch == '\v' || ch == '\f' || (ch >= '\u001c' && ch <= '\u001e') || ch == '\u0085' || ch == '\u2028' || ch == '\u2029';
                if (!brk) continue;
                r.Add(s.Substring(st, i - st));
                if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                st = i + 1;
            }
            if (st < s.Length) r.Add(s.Substring(st));
            return r;
        }

        /// <summary>Create or refresh the fixed bio and voice card. Returns true if anything changed.</summary>
        public static async Task<bool> EnsureMindBuiltAsync(Mind mind, Newtonsoft.Json.Linq.JObject snap, IDictionary<int, string> names,
                                                           IGameAdapter adapter, ILlmClient llm = null, bool useLlm = false)
        {
            bool changed = false;
            string shortName = adapter.NameInfo(adapter.SnapshotName(snap)).Short;
            mind.Name = string.IsNullOrEmpty(shortName) ? mind.Name : shortName;
            string h = adapter.FactsHash(snap);
            if (string.IsNullOrEmpty(mind.Fixed.Text) || mind.Fixed.FactsHash != h)
            {
                mind.Fixed = adapter.BuildFixedBio(snap, names);
                changed = true;
            }
            if (mind.Voice.Examples.Count == 0 && !mind.Voice.EditedByPlayer)
            {
                mind.Voice = adapter.BuildVoiceCard(snap);
                if (useLlm && llm != null)
                {
                    var ex = await LlmExampleLinesAsync(llm, adapter, mind).ConfigureAwait(false);
                    if (ex != null && ex.Count > 0) { mind.Voice.Examples = ex; mind.Voice.LlmExamples = true; }
                }
                changed = true;
            }
            return changed;
        }
    }
}

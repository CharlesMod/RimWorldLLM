// strip_thought and its regexes, copied from agent/rimworldllm_agent/core/llm.py (that function only).
using System.Text.RegularExpressions;

namespace RimWorldLLM.Mind
{
    public static class Think
    {
        public static readonly Regex ThoughtRe = new Regex(@"<\|channel>.*?<channel\|>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        public static readonly Regex ThinkRe = new Regex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>Removes Gemma channel blocks and think blocks from model output.</summary>
        public static string StripThought(string t)
        {
            return ThinkRe.Replace(ThoughtRe.Replace(t, ""), "");
        }
    }
}

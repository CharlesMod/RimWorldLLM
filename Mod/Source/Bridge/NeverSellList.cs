using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>Parses the "never sell" setting: comma separated names, trimmed, empties dropped.</summary>
    public static class NeverSellList
    {
        public static List<string> Parse(string text)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(text)) return r;
            foreach (var part in text.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) r.Add(p);
            }
            return r;
        }
    }
}

using System;
using System.Collections.Generic;

namespace RimWorldLLM
{
    /// <summary>Verse-free parsing of the chat bar's "@Name text" form.</summary>
    public static class ChatParse
    {
        /// <summary>"@Ana  hello there" gives name "Ana" and rest "hello there". False when the text does not start with '@'.</summary>
        public static bool TryAt(string text, out string name, out string rest)
        {
            name = rest = "";
            if (text == null) return false;
            text = text.Trim();
            if (text.Length < 2 || text[0] != '@') return false;
            int sp = 1;
            while (sp < text.Length && !char.IsWhiteSpace(text[sp])) sp++;
            name = text.Substring(1, sp - 1);
            rest = text.Substring(sp).Trim();
            return name.Length > 0;
        }

        /// <summary>Index of the colonist a typed name means: exact (case-insensitive) first, else the only prefix match; -1 when none or ambiguous.</summary>
        public static int Match(string typed, IList<string> names)
        {
            if (string.IsNullOrEmpty(typed) || names == null) return -1;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], typed, StringComparison.OrdinalIgnoreCase)) return i;
            int found = -1;
            for (int i = 0; i < names.Count; i++)
                if (names[i] != null && names[i].StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                {
                    if (found >= 0) return -1;
                    found = i;
                }
            return found;
        }
    }
}

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

        /// <summary>"/undo" or "/undo 3": the player takes back the narrator's last n placing actions (n 1..20, default 1;
        /// a word after it counts as 1). False for any other text.</summary>
        public static bool TryUndo(string text, out int n)
        {
            n = 0;
            if (text == null) return false;
            text = text.Trim();
            if (!text.StartsWith("/undo", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = text.Substring(5);
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return false;   // "/undone" is not the command
            int v;
            n = int.TryParse(rest.Trim(), out v) ? Math.Max(1, Math.Min(20, v)) : 1;
            return true;
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

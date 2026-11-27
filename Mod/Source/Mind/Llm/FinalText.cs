// NEW (not in the Python): incremental decoder for grammar answers like {"final": "..."} so a UI can show the final line as it streams.
using System;
using System.Globalization;
using System.Text;

namespace RimWorldLLM.Mind
{
    public static class FinalText
    {
        /// <summary>The decoded value of the top-level "final" string in the raw JSON text received so far, or null when the key's string
        /// has not started yet (or the answer has no string "final"). The value may still be growing: an unterminated string returns what is
        /// decoded so far, leaving out a partial escape or a lone high surrogate at the very end.</summary>
        public static string Decode(string raw) => Decode(raw, out _);

        /// <param name="complete">true once the closing quote of the "final" string has been seen.</param>
        public static string Decode(string raw, out bool complete)
        {
            complete = false;
            if (string.IsNullOrEmpty(raw)) return null;
            int i = 0, n = raw.Length;
            SkipWs(raw, ref i);
            if (i >= n || raw[i] != '{') return null;
            i++;
            while (true)
            {
                SkipWs(raw, ref i);
                if (i >= n) return null;
                if (raw[i] == ',') { i++; continue; }
                if (raw[i] != '"') return null;          // '}' or garbage: no "final" at the top level
                if (!ReadString(raw, ref i, out string key, out bool keyDone) || !keyDone) return null;
                SkipWs(raw, ref i);
                if (i >= n || raw[i] != ':') return null;
                i++;
                SkipWs(raw, ref i);
                if (i >= n) return null;
                if (key == "final")
                {
                    if (raw[i] != '"') return null;
                    ReadString(raw, ref i, out string val, out complete);
                    return val;
                }
                if (!SkipValue(raw, ref i)) return null;
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        /// <summary>raw[i] is the opening quote. Decodes up to the closing quote or the end of the text. i ends after the closing quote.</summary>
        private static bool ReadString(string s, ref int i, out string value, out bool closed)
        {
            var sb = new StringBuilder();
            closed = false;
            int p = i + 1;
            while (p < s.Length)
            {
                char c = s[p];
                if (c == '"') { closed = true; p++; break; }
                if (c != '\\') { sb.Append(c); p++; continue; }
                if (p + 1 >= s.Length) break;            // lone backslash at the end
                char e = s[p + 1];
                if (e == 'u')
                {
                    if (p + 6 > s.Length) break;         // partial \uXXXX at the end
                    if (!int.TryParse(s.Substring(p + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp))
                    {
                        sb.Append("\\u");               // malformed: keep it literally and go on
                        p += 2;
                        continue;
                    }
                    sb.Append((char)cp);
                    p += 6;
                    continue;
                }
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    default: sb.Append(e); break;        // \" \\ \/ and anything else
                }
                p += 2;
            }
            if (!closed && sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
            i = p;
            value = sb.ToString();
            return true;
        }

        /// <summary>Skip one JSON value starting at s[i]. False when the text ends inside it.</summary>
        private static bool SkipValue(string s, ref int i)
        {
            char c = s[i];
            if (c == '"')
            {
                int p = i + 1;
                while (p < s.Length)
                {
                    if (s[p] == '\\') { p += 2; continue; }
                    if (s[p] == '"') { i = p + 1; return true; }
                    p++;
                }
                return false;
            }
            if (c == '{' || c == '[')
            {
                int depth = 0, p = i;
                while (p < s.Length)
                {
                    char d = s[p];
                    if (d == '"')
                    {
                        p++;
                        while (p < s.Length && s[p] != '"') p += s[p] == '\\' ? 2 : 1;
                        if (p >= s.Length) return false;
                    }
                    else if (d == '{' || d == '[') depth++;
                    else if (d == '}' || d == ']')
                    {
                        depth--;
                        if (depth == 0) { i = p + 1; return true; }
                    }
                    p++;
                }
                return false;
            }
            // number or literal: runs to the next structural character; it must be followed by one to count as finished
            int q = i;
            while (q < s.Length && s[q] != ',' && s[q] != '}' && s[q] != ']' && s[q] != ' ' && s[q] != '\n' && s[q] != '\r' && s[q] != '\t') q++;
            if (q >= s.Length) return false;
            i = q;
            return true;
        }
    }
}

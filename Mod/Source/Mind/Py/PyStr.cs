// Python str helpers the port needs: whitespace splitting and stripping (str.isspace), code point counts and slices
// (len(s), s[:n]), and exception names for the loops' error strings (type(e).__name__).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.Py
{
    /// <summary>An exception that has a Python-style class name (type(e).__name__).</summary>
    public interface IPyNamed { string PyName { get; } }

    public static class PyStr
    {
        /// <summary>str.isspace() for one UTF-16 unit (Unicode White_Space plus the C0 separators U+001C to U+001F).</summary>
        public static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\u001c' && c <= '\u001f');

        public static string Strip(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsSpace(s[a])) a++;
            while (b > a && IsSpace(s[b - 1])) b--;
            return s.Substring(a, b - a);
        }

        /// <summary>str.split() with no arguments.</summary>
        public static List<string> Split(string s)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && IsSpace(s[i])) i++;
                int st = i;
                while (i < s.Length && !IsSpace(s[i])) i++;
                if (i > st) parts.Add(s.Substring(st, i - st));
            }
            return parts;
        }

        /// <summary>" ".join(s.split())</summary>
        public static string CollapseSpaces(string s) => string.Join(" ", Split(s));

        /// <summary>len(s): code points (a surrogate pair counts once).</summary>
        public static int Len(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }

        /// <summary>s[:n]</summary>
        public static string Head(string s, int n)
        {
            if (n <= 0) return "";
            int i = 0, cps = 0;
            while (i < s.Length && cps < n)
            {
                i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                cps++;
            }
            return s.Substring(0, i);
        }

        /// <summary>str(x) for the values tool results and arguments carry.</summary>
        public static string StrOf(object x)
        {
            switch (x)
            {
                case null: return "None";
                case string s: return s;
                case bool b: return b ? "True" : "False";
                case JToken t: return PyJson.Str(t);
                case double d: return PyJson.FloatRepr(d);
                case float f: return PyJson.FloatRepr(f);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return x.ToString();
            }
        }
    }

    public static class PyNames
    {
        /// <summary>type(e).__name__ for the Python class this .NET exception stands for.</summary>
        public static string Of(Exception e)
        {
            if (e is IPyNamed n) return n.PyName;
            switch (e)
            {
                case InvalidOperationException _: return "RuntimeError";
                case NotImplementedException _: return "NotImplementedError";
                case KeyNotFoundException _: return "KeyError";
                case System.IO.IOException _: return "OSError";
                case TimeoutException _: return "TimeoutError";
                case DivideByZeroException _: return "ZeroDivisionError";
                case OverflowException _: return "OverflowError";
                case InvalidCastException _: return "TypeError";
                case IndexOutOfRangeException _: return "IndexError";
                case ArgumentOutOfRangeException _: return "IndexError";
                case ArgumentException _: return "ValueError";
                case FormatException _: return "ValueError";
                case NullReferenceException _: return "AttributeError";
                case System.Net.Http.HttpRequestException _: return "ConnectError";
                default: return e.GetType().Name;
            }
        }

        /// <summary>f"{type(e).__name__}: {e}"[:200]</summary>
        public static string Describe(Exception e) => PyStr.Head(Of(e) + ": " + e.Message, 200);
    }
}

// Python-shaped failures and JSON access, so the LLM-side port keeps Python's "this raised, that did not" behaviour:
// d["k"] on a missing key is a KeyError, .get on a non-dict is an AttributeError, and so on. The tool loop's error
// strings use PyName (type(e).__name__).
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.Py
{
    public class PyException : Exception, IPyNamed
    {
        public string PyName { get; }
        public PyException(string pyName, string message) : base(message) { PyName = pyName; }
    }

    public sealed class PyKeyError : PyException { public PyKeyError(string key) : base("KeyError", PyJson.StrRepr(key)) { } }
    public sealed class PyIndexError : PyException { public PyIndexError(string m = "list index out of range") : base("IndexError", m) { } }
    public sealed class PyTypeError : PyException { public PyTypeError(string m) : base("TypeError", m) { } }
    public sealed class PyAttributeError : PyException { public PyAttributeError(string m) : base("AttributeError", m) { } }
    public sealed class PyValueError : PyException { public PyValueError(string m) : base("ValueError", m) { } }

    public static class PyAccess
    {
        public static string TypeName(JToken t)
        {
            if (t == null) return "NoneType";
            switch (t.Type)
            {
                case JTokenType.Null: case JTokenType.Undefined: return "NoneType";
                case JTokenType.Boolean: return "bool";
                case JTokenType.Integer: return "int";
                case JTokenType.Float: return "float";
                case JTokenType.String: return "str";
                case JTokenType.Array: return "list";
                case JTokenType.Object: return "dict";
                default: return "object";
            }
        }

        /// <summary>t[key]: KeyError when missing, TypeError when t is not a dict.</summary>
        public static JToken Item(JToken t, string key)
        {
            if (t is JObject o)
            {
                if (o.TryGetValue(key, out JToken v)) return v;
                throw new PyKeyError(key);
            }
            if (t is JArray) throw new PyTypeError("list indices must be integers or slices, not str");
            if (t != null && t.Type == JTokenType.String) throw new PyTypeError("string indices must be integers, not 'str'");
            throw new PyTypeError("'" + TypeName(t) + "' object is not subscriptable");
        }

        /// <summary>t[i]: IndexError when out of range, TypeError when t is not a list.</summary>
        public static JToken Item(JToken t, int i)
        {
            if (t is JArray a)
            {
                int n = i < 0 ? a.Count + i : i;
                if (n < 0 || n >= a.Count) throw new PyIndexError();
                return a[n];
            }
            if (t is JObject) throw new PyKeyError(i.ToString(CultureInfo.InvariantCulture));
            throw new PyTypeError("'" + TypeName(t) + "' object is not subscriptable");
        }

        /// <summary>t.get(key, default): AttributeError when t is not a dict.</summary>
        public static JToken Get(JToken t, string key, JToken dflt = null)
        {
            if (t is JObject o) return o.TryGetValue(key, out JToken v) ? v : dflt;
            throw new PyAttributeError("'" + TypeName(t) + "' object has no attribute 'get'");
        }
    }

    public static class PyFloat
    {
        private static readonly Regex Plain = new Regex(@"^[+-]?(\d+(_\d+)*(\.(\d+(_\d+)*)?)?|\.\d+(_\d+)*)([eE][+-]?\d+(_\d+)*)?$",
            RegexOptions.CultureInvariant);

        private static bool IsPySpace(char c) => char.IsWhiteSpace(c) || (c >= '\u001c' && c <= '\u001f');

        public static string PyStrip(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsPySpace(s[a])) a++;
            while (b > a && IsPySpace(s[b - 1])) b--;
            return s.Substring(a, b - a);
        }

        /// <summary>float(str): true when Python would accept the text (surrounding whitespace, a sign, digits with
        /// single underscores between them, a fraction, an exponent, inf / infinity / nan in any case).</summary>
        public static bool TryParse(string text, out double value)
        {
            value = 0;
            if (text == null) return false;
            string s = PyStrip(text);
            if (s.Length == 0) return false;
            string body = s.TrimStart('+', '-');
            if (s.Length - body.Length <= 1)
            {
                string low = body.ToLowerInvariant();
                bool neg = s.StartsWith("-", StringComparison.Ordinal);
                if (low == "inf" || low == "infinity") { value = neg ? double.NegativeInfinity : double.PositiveInfinity; return true; }
                if (low == "nan") { value = double.NaN; return true; }
            }
            if (!Plain.IsMatch(s)) return false;
            return double.TryParse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}

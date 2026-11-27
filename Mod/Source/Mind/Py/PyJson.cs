// Python-compatible JSON for the mind: a serializer byte-compatible with CPython's json.dumps (both ensure_ascii
// modes, the separators variants, float repr, key order preserved) and a strict json.loads (what Python accepts and
// rejects, not what Newtonsoft tolerates: no comments, no single quotes, no unquoted names, no control characters in
// strings, NaN/Infinity accepted).
//
// httpx 0.28.1 sends `json=` bodies as json.dumps(obj, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
// encoded as UTF-8 (httpx/_content.py encode_json); use HttpxJson for exactly that.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.Py
{
    /// <summary>json.JSONDecodeError (a ValueError in Python).</summary>
    public sealed class PyJsonException : FormatException
    {
        public PyJsonException(string message) : base(message) { }
    }

    public static class PyJson
    {
        // ---------------------------------------------------------------- dumps

        /// <summary>json.dumps(value, ensure_ascii=, separators=(itemSep, keySep), sort_keys=, indent=, allow_nan=).
        /// With itemSep/keySep null the Python defaults apply: (", ", ": "), or (",", ": ") when indent is given.
        /// value: a JToken tree, string, bool, any integer type, float/double/decimal, null, an IDictionary with string
        /// keys (enumeration order kept) or any IEnumerable (a list).</summary>
        public static string Dumps(object value, bool ensureAscii = true, string itemSep = null, string keySep = null,
                                   bool sortKeys = false, int? indent = null, bool allowNaN = true)
        {
            var sb = new StringBuilder();
            string item = itemSep ?? (indent.HasValue ? "," : ", ");
            new Writer(sb, ensureAscii, item, keySep ?? ": ", sortKeys, indent, allowNaN).Write(value, 0);
            return sb.ToString();
        }

        /// <summary>json.dumps(value, ensure_ascii=..., separators=(",", ":")).</summary>
        public static string Compact(object value, bool ensureAscii = true, bool sortKeys = false)
            => Dumps(value, ensureAscii, ",", ":", sortKeys);

        /// <summary>The body text httpx 0.28.1 sends for `json=value`: compact, ensure_ascii=False, allow_nan=False.</summary>
        public static string HttpxBody(object value) => Dumps(value, false, ",", ":", false, null, false);

        /// <summary>HttpxBody as UTF-8 bytes (what goes on the wire; Content-Length counts these).</summary>
        public static byte[] HttpxJson(object value) => new UTF8Encoding(false).GetBytes(HttpxBody(value));

        private sealed class Writer
        {
            private readonly StringBuilder _sb;
            private readonly bool _ascii, _sort, _nan;
            private readonly string _item, _key;
            private readonly int? _indent;

            public Writer(StringBuilder sb, bool ascii, string item, string key, bool sort, int? indent, bool nan)
            {
                _sb = sb; _ascii = ascii; _item = item; _key = key; _sort = sort; _indent = indent; _nan = nan;
            }

            private void NewLine(int level)
            {
                _sb.Append('\n').Append(' ', _indent.Value * level);
            }

            public void Write(object v, int level)
            {
                switch (v)
                {
                    case null: _sb.Append("null"); return;
                    case JToken t: WriteToken(t, level); return;
                    case string s: WriteString(s); return;
                    case bool b: _sb.Append(b ? "true" : "false"); return;
                    case sbyte or byte or short or ushort or int or uint or long or ulong:
                        _sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                    case BigInteger bi: _sb.Append(bi.ToString(CultureInfo.InvariantCulture)); return;
                    case double d: WriteFloat(d); return;
                    case float f: WriteFloat((double)f); return;
                    case decimal m: WriteFloat((double)m); return;
                    case IDictionary dict:
                        {
                            var pairs = new List<KeyValuePair<string, object>>();
                            foreach (DictionaryEntry e in dict) pairs.Add(new KeyValuePair<string, object>((string)e.Key, e.Value));
                            WriteObject(pairs, level);
                            return;
                        }
                    case IEnumerable seq:
                        {
                            var items = new List<object>();
                            foreach (var o in seq) items.Add(o);
                            WriteArray(items, level);
                            return;
                        }
                    default:
                        throw new ArgumentException("Object of type " + v.GetType().Name + " is not JSON serializable");
                }
            }

            private void WriteToken(JToken t, int level)
            {
                switch (t.Type)
                {
                    case JTokenType.Null:
                    case JTokenType.Undefined:
                        _sb.Append("null"); return;
                    case JTokenType.Boolean: _sb.Append((bool)t ? "true" : "false"); return;
                    case JTokenType.String: WriteString((string)t); return;
                    case JTokenType.Integer:
                        {
                            object o = ((JValue)t).Value;
                            if (o is BigInteger bi) _sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                            else _sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
                            return;
                        }
                    case JTokenType.Float: WriteFloat(Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture)); return;
                    case JTokenType.Object:
                        {
                            var pairs = new List<KeyValuePair<string, object>>();
                            foreach (var p in ((JObject)t).Properties()) pairs.Add(new KeyValuePair<string, object>(p.Name, p.Value));
                            WriteObject(pairs, level);
                            return;
                        }
                    case JTokenType.Array:
                        {
                            var items = new List<object>();
                            foreach (var c in (JArray)t) items.Add(c);
                            WriteArray(items, level);
                            return;
                        }
                    default:
                        throw new ArgumentException("JSON token of type " + t.Type + " is not JSON serializable");
                }
            }

            private void WriteObject(List<KeyValuePair<string, object>> pairs, int level)
            {
                if (pairs.Count == 0) { _sb.Append("{}"); return; }
                if (_sort) pairs.Sort((a, b) => CompareCodePoints(a.Key, b.Key));
                _sb.Append('{');
                if (_indent.HasValue) NewLine(level + 1);
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(_item);
                        if (_indent.HasValue) NewLine(level + 1);
                    }
                    WriteString(pairs[i].Key);
                    _sb.Append(_key);
                    Write(pairs[i].Value, level + 1);
                }
                if (_indent.HasValue) NewLine(level);
                _sb.Append('}');
            }

            private void WriteArray(List<object> items, int level)
            {
                if (items.Count == 0) { _sb.Append("[]"); return; }
                _sb.Append('[');
                if (_indent.HasValue) NewLine(level + 1);
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(_item);
                        if (_indent.HasValue) NewLine(level + 1);
                    }
                    Write(items[i], level + 1);
                }
                if (_indent.HasValue) NewLine(level);
                _sb.Append(']');
            }

            private void WriteFloat(double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    if (!_nan) throw new ArgumentException("Out of range float values are not JSON compliant");
                    _sb.Append(double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");
                    return;
                }
                _sb.Append(FloatRepr(d));
            }

            private void WriteString(string s)
            {
                _sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        case '\b': _sb.Append("\\b"); break;
                        case '\f': _sb.Append("\\f"); break;
                        default:
                            if (c < 0x20 || (_ascii && c > 0x7e))
                                _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _sb.Append(c);
                            break;
                    }
                }
                _sb.Append('"');
            }
        }

        /// <summary>Order strings the way Python's sort does (by code point, not by UTF-16 unit).</summary>
        public static int CompareCodePoints(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int ca = char.ConvertToUtf32(a, i), cb = char.ConvertToUtf32(b, j);
                if (ca != cb) return ca < cb ? -1 : 1;
                i += char.IsSurrogatePair(a, i) ? 2 : 1;
                j += char.IsSurrogatePair(b, j) ? 2 : 1;
            }
            if (i < a.Length) return 1;
            if (j < b.Length) return -1;
            return 0;
        }

        // ---------------------------------------------------------------- float repr

        /// <summary>Python's repr(float): the shortest digits that round-trip, 1.0 -> "1.0", 1e16 -> "1e+16",
        /// 0.0001 -> "0.0001", 0.00001 -> "1e-05", inf -> "inf", nan -> "nan".</summary>
        public static string FloatRepr(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            if (d == 0) return (1 / d < 0) ? "-0.0" : "0.0";
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            bool neg = r[0] == '-';
            if (neg) r = r.Substring(1);
            int exp = 0;
            int e = r.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0)
            {
                exp = int.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r.Substring(0, e);
            }
            int dot = r.IndexOf('.');
            string digits = dot >= 0 ? r.Remove(dot, 1) : r;
            int decpt = (dot >= 0 ? dot : r.Length) + exp;
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
            digits = digits.Substring(lead);
            decpt -= lead;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            if (decpt <= -4 || decpt > 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                int x = decpt - 1;
                sb.Append('e').Append(x < 0 ? '-' : '+');
                sb.Append(Math.Abs(x).ToString("00", CultureInfo.InvariantCulture));
            }
            else if (decpt <= 0)
            {
                sb.Append("0.").Append('0', -decpt).Append(digits);
            }
            else if (decpt >= digits.Length)
            {
                sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- str() / repr() of a JSON value (Python values)

        /// <summary>Python str(value) for a value json.loads would produce.</summary>
        public static string Str(JToken t)
        {
            if (t != null && t.Type == JTokenType.String) return (string)t;
            return Repr(t);
        }

        /// <summary>Python repr(value) for a value json.loads would produce (dict/list/str/int/float/bool/None).</summary>
        public static string Repr(JToken t)
        {
            if (t == null) return "None";
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: return "None";
                case JTokenType.Boolean: return (bool)t ? "True" : "False";
                case JTokenType.Integer:
                    {
                        object o = ((JValue)t).Value;
                        return o is BigInteger bi ? bi.ToString(CultureInfo.InvariantCulture) : Convert.ToString(o, CultureInfo.InvariantCulture);
                    }
                case JTokenType.Float: return FloatRepr(Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture));
                case JTokenType.String: return StrRepr((string)t);
                case JTokenType.Array:
                    {
                        var parts = new List<string>();
                        foreach (var c in (JArray)t) parts.Add(Repr(c));
                        return "[" + string.Join(", ", parts) + "]";
                    }
                case JTokenType.Object:
                    {
                        var parts = new List<string>();
                        foreach (var p in ((JObject)t).Properties()) parts.Add(StrRepr(p.Name) + ": " + Repr(p.Value));
                        return "{" + string.Join(", ", parts) + "}";
                    }
                default: return t.ToString();
            }
        }

        /// <summary>repr() of a str.</summary>
        public static string StrRepr(string s)
        {
            char q = (s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0) ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(q);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == q || c == '\\') { sb.Append('\\').Append(c); continue; }
                if (c == '\n') { sb.Append("\\n"); continue; }
                if (c == '\r') { sb.Append("\\r"); continue; }
                if (c == '\t') { sb.Append("\\t"); continue; }
                int cp = c;
                int len = 1;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(c, s[i + 1]); len = 2; }
                if (IsPrintable(cp, len == 2 ? null : (char?)c))
                {
                    sb.Append(s, i, len);
                }
                else if (cp <= 0xff) sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                else if (cp <= 0xffff) sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
                i += len - 1;
            }
            sb.Append(q);
            return sb.ToString();
        }

        private static bool IsPrintable(int cp, char? c)
        {
            if (cp == ' ') return true;
            UnicodeCategory cat = c.HasValue ? char.GetUnicodeCategory(c.Value) : CharUnicodeInfo.GetUnicodeCategory(cp);
            switch (cat)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
                default: return true;
            }
        }

        // ---------------------------------------------------------------- loads

        /// <summary>json.loads(str). Throws PyJsonException where Python raises JSONDecodeError.</summary>
        public static JToken Loads(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (s.Length > 0 && s[0] == '﻿') throw new PyJsonException("Unexpected UTF-8 BOM (decode using utf-8-sig)");
            var p = new Parser(s);
            p.SkipWs();
            JToken v = p.ParseValue();
            p.SkipWs();
            if (p.Pos != s.Length) throw new PyJsonException("Extra data: char " + p.Pos);
            return v;
        }

        /// <summary>json.loads(bytes): UTF-8 (a UTF-8 BOM is skipped, as json.loads does); bad UTF-8 is a ValueError.</summary>
        public static JToken Loads(byte[] b)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            int skip = (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ? 3 : 0;
            string s;
            try { s = new UTF8Encoding(false, true).GetString(b, skip, b.Length - skip); }
            catch (ArgumentException) { throw new PyJsonException("invalid utf-8"); }
            return Loads(s);
        }

        public static bool TryLoads(string s, out JToken value)
        {
            try { value = Loads(s); return true; }
            catch (PyJsonException) { value = null; return false; }
        }

        public static bool TryLoads(byte[] b, out JToken value)
        {
            try { value = Loads(b); return true; }
            catch (PyJsonException) { value = null; return false; }
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;

            public Parser(string s) { _s = s; }

            public void SkipWs()
            {
                while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\n' || _s[Pos] == '\r')) Pos++;
            }

            private PyJsonException Err(string msg) => new PyJsonException(msg + ": char " + Pos);

            public JToken ParseValue()
            {
                if (Pos >= _s.Length) throw Err("Expecting value");
                char c = _s[Pos];
                switch (c)
                {
                    case '"': return new JValue(ParseString());
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case 'n': Literal("null"); return JValue.CreateNull();
                    case 't': Literal("true"); return new JValue(true);
                    case 'f': Literal("false"); return new JValue(false);
                    case 'N': Literal("NaN"); return new JValue(double.NaN);
                    case 'I': Literal("Infinity"); return new JValue(double.PositiveInfinity);
                    case '-':
                        if (string.CompareOrdinal(_s, Pos, "-Infinity", 0, 9) == 0) { Pos += 9; return new JValue(double.NegativeInfinity); }
                        return ParseNumber();
                    default:
                        if (c >= '0' && c <= '9') return ParseNumber();
                        throw Err("Expecting value");
                }
            }

            private void Literal(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Err("Expecting value");
                Pos += word.Length;
            }

            private JToken ParseNumber()
            {
                int start = Pos;
                int i = Pos;
                if (i < _s.Length && _s[i] == '-') i++;
                if (i >= _s.Length) throw Err("Expecting value");
                if (_s[i] == '0') i++;
                else if (_s[i] >= '1' && _s[i] <= '9') { while (i < _s.Length && _s[i] >= '0' && _s[i] <= '9') i++; }
                else throw Err("Expecting value");
                bool isFloat = false;
                if (i + 1 < _s.Length && _s[i] == '.' && _s[i + 1] >= '0' && _s[i + 1] <= '9')
                {
                    isFloat = true;
                    i++;
                    while (i < _s.Length && _s[i] >= '0' && _s[i] <= '9') i++;
                }
                if (i < _s.Length && (_s[i] == 'e' || _s[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < _s.Length && (_s[j] == '+' || _s[j] == '-')) j++;
                    if (j < _s.Length && _s[j] >= '0' && _s[j] <= '9')
                    {
                        while (j < _s.Length && _s[j] >= '0' && _s[j] <= '9') j++;
                        isFloat = true;
                        i = j;
                    }
                }
                string text = _s.Substring(start, i - start);
                Pos = i;
                if (isFloat) return new JValue(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return new JValue(l);
                return new JValue(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }

            private string ParseString()
            {
                // _s[Pos] is the opening quote
                int begin = Pos;
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw new PyJsonException("Unterminated string starting at: char " + begin);
                    char c = _s[Pos];
                    if (c == '"') { Pos++; return sb.ToString(); }
                    if (c < 0x20) throw Err("Invalid control character at");
                    if (c != '\\') { sb.Append(c); Pos++; continue; }
                    Pos++;
                    if (Pos >= _s.Length) throw new PyJsonException("Unterminated string starting at: char " + begin);
                    char e = _s[Pos];
                    switch (e)
                    {
                        case '"': sb.Append('"'); Pos++; break;
                        case '\\': sb.Append('\\'); Pos++; break;
                        case '/': sb.Append('/'); Pos++; break;
                        case 'b': sb.Append('\b'); Pos++; break;
                        case 'f': sb.Append('\f'); Pos++; break;
                        case 'n': sb.Append('\n'); Pos++; break;
                        case 'r': sb.Append('\r'); Pos++; break;
                        case 't': sb.Append('\t'); Pos++; break;
                        case 'u':
                            sb.Append(Hex4(Pos + 1));
                            Pos += 5;
                            break;
                        default: throw Err("Invalid \\escape");
                    }
                }
            }

            private char Hex4(int at)
            {
                if (at + 4 > _s.Length) throw Err("Invalid \\uXXXX escape");
                int v = 0;
                for (int k = 0; k < 4; k++)
                {
                    char h = _s[at + k];
                    int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                    if (d < 0) throw Err("Invalid \\uXXXX escape");
                    v = v * 16 + d;
                }
                return (char)v;
            }

            private JToken ParseObject()
            {
                Pos++; // {
                var obj = new JObject();
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return obj; }
                while (true)
                {
                    SkipWs();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw Err("Expecting property name enclosed in double quotes");
                    string key = ParseString();
                    SkipWs();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw Err("Expecting ':' delimiter");
                    Pos++;
                    SkipWs();
                    obj[key] = ParseValue();
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return obj; }
                    if (Pos >= _s.Length || _s[Pos] != ',') throw Err("Expecting ',' delimiter");
                    Pos++;
                }
            }

            private JToken ParseArray()
            {
                Pos++; // [
                var arr = new JArray();
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return arr; }
                while (true)
                {
                    SkipWs();
                    arr.Add(ParseValue());
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return arr; }
                    if (Pos >= _s.Length || _s[Pos] != ',') throw Err("Expecting ',' delimiter");
                    Pos++;
                }
            }
        }
    }
}

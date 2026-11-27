// A small TOML reader for the agent's config files: comments, tables ([a] and [a.b]), arrays of tables ([[a]]),
// bare/quoted/dotted keys, basic and literal strings (single and multi-line), integers (decimal with _, 0x, 0o, 0b),
// floats (incl. inf and nan), booleans, arrays (multi-line, trailing comma) and inline tables. Dates and times are not
// supported (the config has none) and throw. Result: a JObject (ints long, floats double), like tomllib.loads.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public sealed class TomlException : FormatException
    {
        public TomlException(string message) : base(message) { }
    }

    public static class TomlLite
    {
        public static JObject Parse(string text)
        {
            return new Reader(text).ParseDocument();
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _p;
            private int _line = 1;

            public Reader(string s) { _s = s; }

            private TomlException Err(string m) => new TomlException(m + " (line " + _line + ")");
            private bool Eof => _p >= _s.Length;
            private char Cur => _s[_p];

            private void SkipSpaces() { while (!Eof && (Cur == ' ' || Cur == '\t')) _p++; }

            private void SkipComment()
            {
                if (!Eof && Cur == '#') { while (!Eof && Cur != '\n') _p++; }
            }

            private void SkipWsAndNewlines()
            {
                while (!Eof)
                {
                    if (Cur == ' ' || Cur == '\t' || Cur == '\r') _p++;
                    else if (Cur == '\n') { _p++; _line++; }
                    else if (Cur == '#') SkipComment();
                    else break;
                }
            }

            private void EndOfLine()
            {
                SkipSpaces();
                SkipComment();
                if (Eof) return;
                if (Cur == '\r') _p++;
                if (!Eof && Cur == '\n') { _p++; _line++; return; }
                throw Err("expected end of line");
            }

            public JObject ParseDocument()
            {
                var root = new JObject();
                JObject cur = root;
                var defined = new HashSet<JObject>();       // tables created by a [header] (a header may not repeat)
                while (true)
                {
                    SkipWsAndNewlines();
                    if (Eof) break;
                    if (Cur == '[')
                    {
                        bool arr = _p + 1 < _s.Length && _s[_p + 1] == '[';
                        _p += arr ? 2 : 1;
                        var path = ParseKeyPath();
                        SkipSpaces();
                        if (arr) { Expect("]]"); } else { Expect("]"); }
                        cur = arr ? EnterArrayTable(root, path) : EnterTable(root, path, defined);
                        EndOfLine();
                        continue;
                    }
                    var keys = ParseKeyPath();
                    SkipSpaces();
                    Expect("=");
                    SkipSpaces();
                    JToken val = ParseValue();
                    JObject target = cur;
                    for (int i = 0; i < keys.Count - 1; i++)
                    {
                        if (target[keys[i]] == null) target[keys[i]] = new JObject();
                        target = target[keys[i]] as JObject ?? throw Err("key " + keys[i] + " is not a table");
                    }
                    string last = keys[keys.Count - 1];
                    if (target[last] != null) throw Err("duplicate key " + last);
                    target[last] = val;
                    EndOfLine();
                }
                return root;
            }

            private void Expect(string tok)
            {
                if (string.CompareOrdinal(_s, _p, tok, 0, tok.Length) != 0) throw Err("expected " + tok);
                _p += tok.Length;
            }

            private static JObject EnterTable(JObject root, List<string> path, HashSet<JObject> defined)
            {
                JObject t = root;
                for (int i = 0; i < path.Count; i++)
                {
                    JToken next = t[path[i]];
                    if (next == null) { next = new JObject(); t[path[i]] = next; }
                    if (next is JArray arr)
                    {
                        if (arr.Count == 0 || !(arr[arr.Count - 1] is JObject lastTable)) throw new TomlException("key " + path[i] + " is not a table");
                        next = lastTable;
                    }
                    t = next as JObject ?? throw new TomlException("key " + path[i] + " is not a table");
                }
                if (!defined.Add(t)) throw new TomlException("table [" + string.Join(".", path) + "] defined twice");
                return t;
            }

            private static JObject EnterArrayTable(JObject root, List<string> path)
            {
                JObject t = root;
                for (int i = 0; i < path.Count - 1; i++)
                {
                    JToken next = t[path[i]];
                    if (next == null) { next = new JObject(); t[path[i]] = next; }
                    if (next is JArray arr)
                    {
                        if (arr.Count == 0 || !(arr[arr.Count - 1] is JObject lastTable)) throw new TomlException("key " + path[i] + " is not a table");
                        next = lastTable;
                    }
                    t = next as JObject ?? throw new TomlException("key " + path[i] + " is not a table");
                }
                string key = path[path.Count - 1];
                var list = t[key] as JArray;
                if (list == null)
                {
                    if (t[key] != null) throw new TomlException("key " + key + " is not an array of tables");
                    list = new JArray();
                    t[key] = list;
                }
                var item = new JObject();
                list.Add(item);
                return item;
            }

            private List<string> ParseKeyPath()
            {
                var keys = new List<string>();
                while (true)
                {
                    SkipSpaces();
                    keys.Add(ParseKey());
                    SkipSpaces();
                    if (!Eof && Cur == '.') { _p++; continue; }
                    return keys;
                }
            }

            private string ParseKey()
            {
                if (Eof) throw Err("expected a key");
                if (Cur == '"') return ParseBasicString();
                if (Cur == '\'') return ParseLiteralString();
                int start = _p;
                while (!Eof && (char.IsAsciiLetterOrDigit(Cur) || Cur == '_' || Cur == '-')) _p++;
                if (_p == start) throw Err("expected a key");
                return _s.Substring(start, _p - start);
            }

            private JToken ParseValue()
            {
                if (Eof) throw Err("expected a value");
                char c = Cur;
                if (c == '"')
                {
                    if (string.CompareOrdinal(_s, _p, "\"\"\"", 0, 3) == 0) return new JValue(ParseMultiBasic());
                    return new JValue(ParseBasicString());
                }
                if (c == '\'')
                {
                    if (string.CompareOrdinal(_s, _p, "'''", 0, 3) == 0) return new JValue(ParseMultiLiteral());
                    return new JValue(ParseLiteralString());
                }
                if (c == '[') return ParseArray();
                if (c == '{') return ParseInlineTable();
                if (string.CompareOrdinal(_s, _p, "true", 0, 4) == 0 && IsBoundary(_p + 4)) { _p += 4; return new JValue(true); }
                if (string.CompareOrdinal(_s, _p, "false", 0, 5) == 0 && IsBoundary(_p + 5)) { _p += 5; return new JValue(false); }
                return ParseNumber();
            }

            private bool IsBoundary(int at) => at >= _s.Length || " \t\r\n,]}#".IndexOf(_s[at]) >= 0;

            private JToken ParseNumber()
            {
                int start = _p;
                while (!Eof && " \t\r\n,]}#".IndexOf(Cur) < 0) _p++;
                string tok = _s.Substring(start, _p - start);
                if (tok.Length == 0) throw Err("expected a value");
                string t = tok.Replace("_", "");
                if (t.IndexOf(':') >= 0 || (t.Length >= 10 && t[4] == '-' && t[7] == '-')) throw Err("dates and times are not supported");
                string body = t.TrimStart('+', '-');
                bool neg = t.StartsWith("-");
                if (body == "inf") return new JValue(neg ? double.NegativeInfinity : double.PositiveInfinity);
                if (body == "nan") return new JValue(double.NaN);
                if (body.StartsWith("0x") || body.StartsWith("0o") || body.StartsWith("0b"))
                {
                    int radix = body[1] == 'x' ? 16 : body[1] == 'o' ? 8 : 2;
                    BigInteger v = BigInteger.Zero;
                    foreach (char ch in body.Substring(2))
                    {
                        int d = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : 99;
                        if (d >= radix) throw Err("bad number " + tok);
                        v = v * radix + d;
                    }
                    return IntToken(neg ? -v : v);
                }
                bool isFloat = t.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0;
                if (isFloat)
                {
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) throw Err("bad number " + tok);
                    return new JValue(d);
                }
                if (!BigInteger.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger bi)) throw Err("bad number " + tok);
                return IntToken(bi);
            }

            private static JToken IntToken(BigInteger v)
            {
                if (v >= long.MinValue && v <= long.MaxValue) return new JValue((long)v);
                return new JValue(v);
            }

            private JToken ParseArray()
            {
                _p++; // [
                var arr = new JArray();
                while (true)
                {
                    SkipWsAndNewlines();
                    if (Eof) throw Err("unterminated array");
                    if (Cur == ']') { _p++; return arr; }
                    arr.Add(ParseValue());
                    SkipWsAndNewlines();
                    if (Eof) throw Err("unterminated array");
                    if (Cur == ',') { _p++; continue; }
                    if (Cur == ']') { _p++; return arr; }
                    throw Err("expected , or ] in array");
                }
            }

            private JToken ParseInlineTable()
            {
                _p++; // {
                var obj = new JObject();
                SkipSpaces();
                if (!Eof && Cur == '}') { _p++; return obj; }
                while (true)
                {
                    SkipSpaces();
                    var keys = ParseKeyPath();
                    SkipSpaces();
                    Expect("=");
                    SkipSpaces();
                    JToken v = ParseValue();
                    JObject target = obj;
                    for (int i = 0; i < keys.Count - 1; i++)
                    {
                        if (target[keys[i]] == null) target[keys[i]] = new JObject();
                        target = target[keys[i]] as JObject ?? throw Err("key " + keys[i] + " is not a table");
                    }
                    if (target[keys[keys.Count - 1]] != null) throw Err("duplicate key " + keys[keys.Count - 1]);
                    target[keys[keys.Count - 1]] = v;
                    SkipSpaces();
                    if (Eof) throw Err("unterminated inline table");
                    if (Cur == ',') { _p++; continue; }
                    if (Cur == '}') { _p++; return obj; }
                    throw Err("expected , or } in inline table");
                }
            }

            private string ParseLiteralString()
            {
                _p++;
                int start = _p;
                while (!Eof && Cur != '\'' && Cur != '\n') _p++;
                if (Eof || Cur != '\'') throw Err("unterminated string");
                string r = _s.Substring(start, _p - start);
                _p++;
                return r;
            }

            private string ParseMultiLiteral()
            {
                _p += 3;
                if (!Eof && Cur == '\r') _p++;
                if (!Eof && Cur == '\n') { _p++; _line++; }
                int start = _p;
                int end = _s.IndexOf("'''", _p, StringComparison.Ordinal);
                if (end < 0) throw Err("unterminated string");
                while (end + 3 < _s.Length && _s[end + 3] == '\'' ) end++;   // up to two quotes may close the string
                string r = _s.Substring(start, end - start);
                foreach (char ch in r) if (ch == '\n') _line++;
                _p = end + 3;
                return r;
            }

            private string ParseBasicString()
            {
                _p++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Eof || Cur == '\n') throw Err("unterminated string");
                    char c = Cur;
                    if (c == '"') { _p++; return sb.ToString(); }
                    if (c == '\\') { _p++; sb.Append(ParseEscape()); continue; }
                    if (c < 0x20 && c != '\t') throw Err("control character in string");
                    sb.Append(c); _p++;
                }
            }

            private string ParseMultiBasic()
            {
                _p += 3;
                if (!Eof && Cur == '\r') _p++;
                if (!Eof && Cur == '\n') { _p++; _line++; }
                var sb = new StringBuilder();
                while (true)
                {
                    if (Eof) throw Err("unterminated string");
                    char c = Cur;
                    if (c == '"' && string.CompareOrdinal(_s, _p, "\"\"\"", 0, 3) == 0)
                    {
                        int n = 3;
                        while (_p + n < _s.Length && _s[_p + n] == '"' && n < 5) n++;   // up to two extra quotes belong to the text
                        sb.Append('"', n - 3);
                        _p += n;
                        return sb.ToString();
                    }
                    if (c == '\\')
                    {
                        _p++;
                        if (!Eof && (Cur == '\n' || Cur == '\r' || Cur == ' ' || Cur == '\t'))
                        {
                            int q = _p;
                            while (q < _s.Length && (_s[q] == ' ' || _s[q] == '\t' || _s[q] == '\r')) q++;
                            if (q < _s.Length && _s[q] == '\n')
                            {
                                while (q < _s.Length && (_s[q] == ' ' || _s[q] == '\t' || _s[q] == '\r' || _s[q] == '\n')) { if (_s[q] == '\n') _line++; q++; }
                                _p = q;
                                continue;
                            }
                        }
                        sb.Append(ParseEscape());
                        continue;
                    }
                    if (c == '\n') _line++;
                    sb.Append(c); _p++;
                }
            }

            private string ParseEscape()
            {
                if (Eof) throw Err("bad escape");
                char e = Cur; _p++;
                switch (e)
                {
                    case 'b': return "\b";
                    case 't': return "\t";
                    case 'n': return "\n";
                    case 'f': return "\f";
                    case 'r': return "\r";
                    case '"': return "\"";
                    case '\\': return "\\";
                    case 'u': return HexChars(4);
                    case 'U': return HexChars(8);
                    default: throw Err("bad escape \\" + e);
                }
            }

            private string HexChars(int n)
            {
                if (_p + n > _s.Length) throw Err("bad unicode escape");
                if (!int.TryParse(_s.Substring(_p, n), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
                    || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) throw Err("bad unicode escape");
                _p += n;
                return char.ConvertFromUtf32(cp);
            }
        }
    }
}

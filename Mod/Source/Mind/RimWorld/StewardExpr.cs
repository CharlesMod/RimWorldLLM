// Port of the condition compiler in games/rimworld/steward_rules.py: a small safe expression parser and evaluator.
//
// Python parsed conditions with ast.parse(mode="eval") and checked the tree against a whitelist. Here a hand-written
// tokenizer and recursive-descent parser accept exactly the whitelisted grammar and raise RuleError for everything else
// Python would parse and the whitelist would refuse (calls, attributes, subscripts, lambdas, comprehensions, dicts, sets,
// conditional expressions, ** // % @ & | ^ << >>, unary + and ~, is / is not, bytes, f-strings, complex and Ellipsis
// constants, dunder names, lists holding more than constants). Nothing is ever compiled to code.
//
// Differences from Python (messages only, never the accepted set): a syntax error says "syntax: invalid syntax" whatever
// CPython's own text would be; a disallowed construct is reported where the parser meets it rather than after the whole
// tree is built, so an expression with two faults may name the other one; "\N{NAME}" escapes are unsupported and refused.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>A playbook condition the whitelist refuses (or that does not parse).</summary>
    public sealed class RuleError : PyException
    {
        public RuleError(string message) : base("RuleError", message) { }
    }

    /// <summary>A compiled condition: facts to true / false / null (null = skipped, a needed field is missing).</summary>
    public sealed class CompiledCondition
    {
        internal readonly StewardExpr.Node Body;
        /// <summary>The body is exactly the name true: always-on guidance that belongs to the static prompt.</summary>
        public bool AlwaysOn { get; internal set; }
        /// <summary>Research defNames the condition names (string constants compared with research_done / research_current).</summary>
        public IReadOnlyCollection<string> ResearchNames { get; internal set; }

        internal CompiledCondition(StewardExpr.Node body) { Body = body; }

        /// <summary>true, false, or null when a field the condition needs is missing (or None where a number is needed).</summary>
        public bool? Run(IDictionary<string, object> facts) => StewardExpr.Run(Body, facts);
        public bool? this[IDictionary<string, object> facts] => Run(facts);
    }

    internal static class StewardExpr
    {
        // ------------------------------------------------------------------ AST
        internal abstract class Node { }
        internal sealed class ConstNode : Node { public object Value; public ConstNode(object v) { Value = v; } }
        internal sealed class NameNode : Node { public string Id; public NameNode(string id) { Id = id; } }
        internal sealed class SeqNode : Node { public List<Node> Elts; public SeqNode(List<Node> e) { Elts = e; } }
        internal sealed class UnaryNode : Node { public bool IsNot; public Node Operand; public UnaryNode(bool n, Node o) { IsNot = n; Operand = o; } }
        internal sealed class BinNode : Node { public char Op; public Node L, R; public BinNode(char op, Node l, Node r) { Op = op; L = l; R = r; } }
        internal sealed class BoolNode : Node { public bool IsAnd; public List<Node> Values; public BoolNode(bool a, List<Node> v) { IsAnd = a; Values = v; } }
        internal sealed class CmpNode : Node { public Node Left; public List<string> Ops; public List<Node> Comps; public CmpNode(Node l, List<string> o, List<Node> c) { Left = l; Ops = o; Comps = c; } }

        private static RuleError Syntax(string why = "invalid syntax") => new RuleError("syntax: " + why);

        // ------------------------------------------------------------------ tokens
        internal enum T { Name, Num, Str, Op, Newline, End }

        internal sealed class Tok
        {
            public T Kind;
            public string Text;          // name / operator text
            public object Value;         // Num: BigInteger | double | "imag"; Str: string
            public string StrKind;       // Str: "str" | "bytes" | "f" | "t"
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
            "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
            "return", "try", "while", "with", "yield",
        };

        private static readonly string[] Ops3 = { "...", "<<=", ">>=", "**=", "//=" };
        private static readonly string[] Ops2 =
        {
            "<=", ">=", "==", "!=", "<>", "//", "**", "<<", ">>", ":=", "->", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@=",
        };
        private const string Ops1 = "()[]{},:.;+-*/%@&|^~<>=!";

        private static bool NameStart(char c) => c == '_' || char.IsLetter(c);

        private static bool NameChar(char c)
        {
            if (c == '_' || char.IsLetterOrDigit(c)) return true;
            var cat = char.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                || cat == UnicodeCategory.LetterNumber || cat == UnicodeCategory.ConnectorPunctuation;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        internal static List<Tok> Lex(string s)
        {
            var toks = new List<Tok>();
            var stack = new Stack<char>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\f') { i++; continue; }
                if (c == '#') { while (i < n && s[i] != '\n' && s[i] != '\r') i++; continue; }
                if (c == '\\')
                {
                    if (i + 1 < n && s[i + 1] == '\n') { i += 2; continue; }
                    if (i + 1 < n && s[i + 1] == '\r') { i += (i + 2 < n && s[i + 2] == '\n') ? 3 : 2; continue; }
                    throw Syntax();
                }
                if (c == '\n' || c == '\r')
                {
                    i += (c == '\r' && i + 1 < n && s[i + 1] == '\n') ? 2 : 1;
                    if (stack.Count == 0) toks.Add(new Tok { Kind = T.Newline });
                    continue;
                }
                if (NameStart(c))
                {
                    int j = i + 1;
                    while (j < n && NameChar(s[j])) j++;
                    string word = s.Substring(i, j - i);
                    if (j < n && (s[j] == '\'' || s[j] == '"') && IsStrPrefix(word))
                    {
                        toks.Add(LexString(s, ref j, word));
                        i = j;
                        continue;
                    }
                    toks.Add(new Tok { Kind = T.Name, Text = word.Normalize(NormalizationForm.FormKC) });
                    i = j;
                    continue;
                }
                if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(s[i + 1])))
                {
                    toks.Add(LexNumber(s, ref i));
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    int j = i;
                    toks.Add(LexString(s, ref j, ""));
                    i = j;
                    continue;
                }
                string op = null;
                if (i + 3 <= n && Ops3.Contains(s.Substring(i, 3))) op = s.Substring(i, 3);
                else if (i + 2 <= n && Ops2.Contains(s.Substring(i, 2))) op = s.Substring(i, 2);
                else if (Ops1.IndexOf(c) >= 0) op = c.ToString();
                if (op == null) throw Syntax();
                if (op.Length == 1)
                {
                    if (c == '(' || c == '[' || c == '{')
                    {
                        if (stack.Count >= 200) throw Syntax("too many nested parentheses");
                        stack.Push(c == '(' ? ')' : c == '[' ? ']' : '}');
                    }
                    else if (c == ')' || c == ']' || c == '}')
                    {
                        if (stack.Count == 0 || stack.Pop() != c) throw Syntax("unmatched bracket");
                    }
                }
                toks.Add(new Tok { Kind = T.Op, Text = op });
                i += op.Length;
            }
            if (stack.Count > 0) throw Syntax("bracket was never closed");
            toks.Add(new Tok { Kind = T.Newline });
            toks.Add(new Tok { Kind = T.End });
            return toks;
        }

        private static bool IsStrPrefix(string w)
        {
            switch (w.ToLowerInvariant())
            {
                case "r": case "u": case "b": case "f": case "t":
                case "br": case "rb": case "fr": case "rf": case "tr": case "rt": return true;
                default: return false;
            }
        }

        private static Tok LexString(string s, ref int i, string prefix)
        {
            string p = prefix.ToLowerInvariant();
            bool raw = p.Contains('r');
            string kind = p.Contains('b') ? "bytes" : p.Contains('f') ? "f" : p.Contains('t') ? "t" : "str";
            char q = s[i];
            bool triple = i + 2 < s.Length && s[i + 1] == q && s[i + 2] == q;
            i += triple ? 3 : 1;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Syntax("unterminated string");
                char c = s[i];
                if (c == q)
                {
                    if (!triple) { i++; break; }
                    if (i + 2 < s.Length && s[i + 1] == q && s[i + 2] == q) { i += 3; break; }
                    sb.Append(c); i++; continue;
                }
                if ((c == '\n' || c == '\r') && !triple) throw Syntax("unterminated string");
                if (c == '\\')
                {
                    if (i + 1 >= s.Length) throw Syntax("unterminated string");
                    char d = s[i + 1];
                    if (raw || kind == "f" || kind == "t") { sb.Append(c).Append(d); i += 2; continue; }
                    i += 2;
                    switch (d)
                    {
                        case '\n': break;
                        case '\r': if (i < s.Length && s[i] == '\n') i++; break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case 'a': sb.Append('\a'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'v': sb.Append('\v'); break;
                        case 'x': AppendHex(sb, s, ref i, 2); break;
                        case 'u': if (kind == "bytes") { sb.Append("\\u"); break; } AppendHex(sb, s, ref i, 4); break;
                        case 'U': if (kind == "bytes") { sb.Append("\\U"); break; } AppendHex(sb, s, ref i, 8); break;
                        case 'N':
                            if (kind == "bytes") { sb.Append("\\N"); break; }
                            throw Syntax("\\N{name} escapes are not supported");
                        default:
                            if (d >= '0' && d <= '7')
                            {
                                int v = d - '0', k = 1;
                                while (k < 3 && i < s.Length && s[i] >= '0' && s[i] <= '7') { v = v * 8 + (s[i] - '0'); i++; k++; }
                                sb.Append((char)v);
                            }
                            else sb.Append('\\').Append(d);
                            break;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return new Tok { Kind = T.Str, Value = sb.ToString(), StrKind = kind };
        }

        private static void AppendHex(StringBuilder sb, string s, ref int i, int digits)
        {
            if (i + digits > s.Length) throw Syntax("truncated escape");
            for (int k = 0; k < digits; k++) if (!IsHex(s[i + k])) throw Syntax("truncated escape");
            long v = long.Parse(s.Substring(i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += digits;
            if (v > 0x10FFFF) throw Syntax("illegal Unicode character");
            if (v >= 0x10000) sb.Append(char.ConvertFromUtf32((int)v));
            else sb.Append((char)v);
        }

        private static string DigitRun(string s, ref int i, Func<char, bool> ok)
        {
            // digits with single underscores between them; the caller has checked the first char
            int start = i;
            bool lastUnderscore = false;
            while (i < s.Length && (ok(s[i]) || s[i] == '_'))
            {
                if (s[i] == '_') { if (lastUnderscore || i == start) throw Syntax("invalid decimal literal"); lastUnderscore = true; }
                else lastUnderscore = false;
                i++;
            }
            if (lastUnderscore) throw Syntax("invalid decimal literal");
            return s.Substring(start, i - start).Replace("_", "");
        }

        private static Tok LexNumber(string s, ref int i)
        {
            int n = s.Length;
            object value;
            if (s[i] == '0' && i + 1 < n && "xXoObB".IndexOf(s[i + 1]) >= 0)
            {
                char kind = char.ToLowerInvariant(s[i + 1]);
                i += 2;
                if (i < n && s[i] == '_') i++;
                Func<char, bool> ok = kind == 'x' ? (Func<char, bool>)IsHex : kind == 'o' ? (c => c >= '0' && c <= '7') : (c => c == '0' || c == '1');
                if (i >= n || !ok(s[i])) throw Syntax("invalid literal");
                string digits = DigitRun(s, ref i, ok);
                BigInteger v = BigInteger.Zero;
                int b = kind == 'x' ? 16 : kind == 'o' ? 8 : 2;
                foreach (char ch in digits) v = v * b + (IsDigit(ch) ? ch - '0' : char.ToLowerInvariant(ch) - 'a' + 10);
                value = v;
            }
            else
            {
                int start = i;
                string intPart = IsDigit(s[i]) ? DigitRun(s, ref i, IsDigit) : "";
                bool isFloat = false;
                string frac = "";
                if (i < n && s[i] == '.')
                {
                    isFloat = true; i++;
                    if (i < n && IsDigit(s[i])) frac = DigitRun(s, ref i, IsDigit);
                }
                string exp = "";
                if (i < n && (s[i] == 'e' || s[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < n && (s[j] == '+' || s[j] == '-')) j++;
                    if (j < n && IsDigit(s[j]))
                    {
                        isFloat = true;
                        string sign = (s[i + 1] == '-') ? "-" : "";
                        i = j;
                        exp = "e" + sign + DigitRun(s, ref i, IsDigit);
                    }
                }
                bool imag = false;
                if (i < n && (s[i] == 'j' || s[i] == 'J')) { imag = true; i++; }
                if (i < n && (NameStart(s[i]) || IsDigit(s[i])))
                {
                    // "1if x" style is tolerated by Python (a warning); anything else glued to a number is a syntax error
                    int j = i;
                    while (j < n && NameChar(s[j])) j++;
                    string w = s.Substring(i, j - i);
                    if (!(w == "and" || w == "or" || w == "if" || w == "else" || w == "in" || w == "is" || w == "not" || w == "for"))
                        throw Syntax("invalid decimal literal");
                }
                if (imag) return new Tok { Kind = T.Num, Value = "imag" };
                if (isFloat)
                {
                    string text = (intPart.Length == 0 ? "0" : intPart) + "." + (frac.Length == 0 ? "0" : frac) + exp;
                    value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                else
                {
                    if (intPart.Length > 1 && intPart[0] == '0' && intPart.Any(ch => ch != '0'))
                        throw Syntax("leading zeros in decimal integer literals are not permitted");
                    value = BigInteger.Parse(intPart, CultureInfo.InvariantCulture);
                }
            }
            return new Tok { Kind = T.Num, Value = value };
        }

        // ------------------------------------------------------------------ parser
        private sealed class Parser
        {
            private readonly List<Tok> _t;
            private int _p;
            private int _depth;

            public Parser(List<Tok> toks) { _t = toks; }

            private Tok Cur => _t[_p];
            private bool IsOp(string text) => Cur.Kind == T.Op && Cur.Text == text;
            private bool IsKw(string text) => Cur.Kind == T.Name && Cur.Text == text;

            public Node ParseTop()
            {
                Node n = ParseTestList();
                while (Cur.Kind == T.Newline) _p++;
                if (Cur.Kind != T.End) throw Syntax();
                return n;
            }

            // testlist: a bare comma-separated list is a tuple (eval mode accepts it)
            private Node ParseTestList()
            {
                Node first = ParseStarTest();
                if (!IsOp(",")) return first;
                var elts = new List<Node> { first };
                while (IsOp(","))
                {
                    _p++;
                    if (Cur.Kind == T.Newline || Cur.Kind == T.End || IsOp(")")) break;
                    elts.Add(ParseStarTest());
                }
                return new SeqNode(elts);
            }

            private Node ParseStarTest()
            {
                if (IsOp("*")) throw new RuleError("Starred not allowed");
                return ParseTest();
            }

            private Node ParseTest()
            {
                if (IsKw("lambda")) throw new RuleError("Lambda not allowed");
                Node n = ParseOr();
                if (IsKw("if")) throw new RuleError("IfExp not allowed");
                return n;
            }

            private Node ParseOr()
            {
                Node first = ParseAnd();
                if (!IsKw("or")) return first;
                var vals = new List<Node> { first };
                while (IsKw("or")) { _p++; vals.Add(ParseAnd()); }
                return new BoolNode(false, vals);
            }

            private Node ParseAnd()
            {
                Node first = ParseNot();
                if (!IsKw("and")) return first;
                var vals = new List<Node> { first };
                while (IsKw("and")) { _p++; vals.Add(ParseNot()); }
                return new BoolNode(true, vals);
            }

            private Node ParseNot()
            {
                if (IsKw("not"))
                {
                    _p++;
                    Enter();
                    Node o = ParseNot();
                    _depth--;
                    return new UnaryNode(true, o);
                }
                return ParseCompare();
            }

            private void Enter()
            {
                if (++_depth > 400) throw Syntax("too deeply nested");
            }

            private Node ParseCompare()
            {
                Node left = ParseBitOr();
                List<string> ops = null;
                List<Node> comps = null;
                while (true)
                {
                    string op = null;
                    if (Cur.Kind == T.Op && (Cur.Text == "<" || Cur.Text == ">" || Cur.Text == "<=" || Cur.Text == ">=" || Cur.Text == "==" || Cur.Text == "!="))
                    { op = Cur.Text; _p++; }
                    else if (IsKw("in")) { op = "in"; _p++; }
                    else if (IsKw("not") && _t[_p + 1].Kind == T.Name && _t[_p + 1].Text == "in") { op = "not in"; _p += 2; }
                    else if (IsKw("is"))
                    {
                        _p++;
                        if (IsKw("not")) _p++;
                        ParseBitOr();
                        throw new RuleError("comparison not allowed");
                    }
                    if (op == null) break;
                    if (ops == null) { ops = new List<string>(); comps = new List<Node>(); }
                    ops.Add(op);
                    comps.Add(ParseBitOr());
                }
                return ops == null ? left : new CmpNode(left, ops, comps);
            }

            // the bitwise / shift levels exist only to refuse their operators where Python would parse them
            private Node ParseBitOr()
            {
                Node n = ParseSum();
                if (Cur.Kind == T.Op && (Cur.Text == "|" || Cur.Text == "^" || Cur.Text == "&" || Cur.Text == "<<" || Cur.Text == ">>"))
                    throw new RuleError("arithmetic operator not allowed");
                return n;
            }

            private Node ParseSum()
            {
                Node n = ParseTerm();
                while (Cur.Kind == T.Op && (Cur.Text == "+" || Cur.Text == "-"))
                {
                    char op = Cur.Text[0];
                    _p++;
                    n = new BinNode(op, n, ParseTerm());
                }
                return n;
            }

            private Node ParseTerm()
            {
                Node n = ParseFactor();
                while (Cur.Kind == T.Op && (Cur.Text == "*" || Cur.Text == "/" || Cur.Text == "//" || Cur.Text == "%" || Cur.Text == "@"))
                {
                    if (Cur.Text != "*" && Cur.Text != "/") throw new RuleError("arithmetic operator not allowed");
                    char op = Cur.Text[0];
                    _p++;
                    n = new BinNode(op, n, ParseFactor());
                }
                return n;
            }

            private Node ParseFactor()
            {
                if (IsOp("-"))
                {
                    _p++;
                    Enter();
                    Node o = ParseFactor();
                    _depth--;
                    return new UnaryNode(false, o);
                }
                if (IsOp("+") || IsOp("~")) throw new RuleError("unary operator not allowed");
                return ParsePower();
            }

            private Node ParsePower()
            {
                if (IsKw("await")) throw new RuleError("Await not allowed");
                Node n = ParsePrimary();
                if (IsOp("**")) throw new RuleError("arithmetic operator not allowed");
                return n;
            }

            private Node ParsePrimary()
            {
                Node n = ParseAtom();
                while (true)
                {
                    if (IsOp(".")) throw new RuleError("Attribute not allowed");
                    if (IsOp("(")) throw new RuleError("Call not allowed");
                    if (IsOp("[")) throw new RuleError("Subscript not allowed");
                    return n;
                }
            }

            private Node ParseAtom()
            {
                Tok t = Cur;
                switch (t.Kind)
                {
                    case T.Name:
                        if (t.Text == "True") { _p++; return new ConstNode(true); }
                        if (t.Text == "False") { _p++; return new ConstNode(false); }
                        if (t.Text == "None") { _p++; return new ConstNode(null); }
                        if (t.Text == "lambda") throw new RuleError("Lambda not allowed");
                        if (t.Text == "yield") throw new RuleError("Yield not allowed");
                        if (Keywords.Contains(t.Text)) throw Syntax();
                        _p++;
                        return new NameNode(t.Text);
                    case T.Num:
                        _p++;
                        if (t.Value is string) throw new RuleError("constant type not allowed");
                        return new ConstNode(t.Value);
                    case T.Str:
                        return ParseStrings();
                    case T.Op:
                        if (t.Text == "(") return ParseParen();
                        if (t.Text == "[") return ParseList();
                        if (t.Text == "{") return ParseBrace();
                        if (t.Text == "...") { _p++; throw new RuleError("constant type not allowed"); }
                        throw Syntax();
                    default:
                        throw Syntax();
                }
            }

            private Node ParseStrings()
            {
                var sb = new StringBuilder();
                bool bytes = false, text = false;
                string special = null;
                while (Cur.Kind == T.Str)
                {
                    Tok t = Cur;
                    _p++;
                    if (t.StrKind == "bytes") bytes = true; else text = true;
                    if (t.StrKind == "f" || t.StrKind == "t") special = t.StrKind == "f" ? "JoinedStr" : "TemplateStr";
                    sb.Append((string)t.Value);
                }
                if (bytes && text) throw Syntax("cannot mix bytes and nonbytes literals");
                if (special != null) throw new RuleError(special + " not allowed");
                if (bytes) throw new RuleError("constant type not allowed");
                return new ConstNode(sb.ToString());
            }

            private Node ParseParen()
            {
                _p++;                                   // (
                if (_depth++ > 400) throw Syntax("too deeply nested");
                try
                {
                    if (IsOp(")")) { _p++; return new SeqNode(new List<Node>()); }
                    Node first = ParseStarTest();
                    if (IsKw("for") || IsKw("async")) throw new RuleError("GeneratorExp not allowed");
                    if (IsOp(":=")) throw new RuleError("NamedExpr not allowed");
                    if (IsOp(","))
                    {
                        var elts = new List<Node> { first };
                        while (IsOp(","))
                        {
                            _p++;
                            if (IsOp(")")) break;
                            elts.Add(ParseStarTest());
                        }
                        Expect(")");
                        return new SeqNode(elts);
                    }
                    Expect(")");
                    return first;
                }
                finally { _depth--; }
            }

            private Node ParseList()
            {
                _p++;                                   // [
                if (_depth++ > 400) throw Syntax("too deeply nested");
                try
                {
                    var elts = new List<Node>();
                    while (!IsOp("]"))
                    {
                        elts.Add(ParseStarTest());
                        if (elts.Count == 1 && (IsKw("for") || IsKw("async"))) throw new RuleError("ListComp not allowed");
                        if (IsOp(",")) { _p++; continue; }
                        break;
                    }
                    Expect("]");
                    return new SeqNode(elts);
                }
                finally { _depth--; }
            }

            private Node ParseBrace()
            {
                _p++;                                   // {
                if (IsOp("}")) throw new RuleError("Dict not allowed");
                ParseStarTest();
                if (IsOp(":")) throw new RuleError("Dict not allowed");
                throw new RuleError("Set not allowed");
            }

            private void Expect(string op)
            {
                if (!IsOp(op)) throw Syntax();
                _p++;
            }
        }

        // ------------------------------------------------------------------ whitelist check
        internal static void Check(Node n)
        {
            switch (n)
            {
                case BoolNode b: foreach (var v in b.Values) Check(v); return;
                case UnaryNode u: Check(u.Operand); return;
                case CmpNode c:
                    Check(c.Left);
                    foreach (var x in c.Comps) Check(x);
                    return;
                case BinNode bn: Check(bn.L); Check(bn.R); return;
                case NameNode nm:
                    if (nm.Id.StartsWith("__", StringComparison.Ordinal)) throw new RuleError("dunder name");
                    return;
                case ConstNode _: return;
                case SeqNode s:
                    foreach (var e in s.Elts) if (!(e is ConstNode)) throw new RuleError("lists hold constants only");
                    return;
                default: throw new RuleError(n.GetType().Name + " not allowed");
            }
        }

        // ------------------------------------------------------------------ compile
        internal static CompiledCondition Compile(string cond)
        {
            if (cond == null || PyStr.Strip(cond).Length == 0 || PyStr.Len(cond) > 400) throw new RuleError("empty or too long");
            string src = PyStr.Strip(cond);
            if (src.IndexOf('\0') >= 0) throw Syntax("source code string cannot contain null bytes");
            Node body = new Parser(Lex(src)).ParseTop();
            Check(body);
            var rc = new CompiledCondition(body)
            {
                AlwaysOn = body is NameNode nn && nn.Id == "true",
                ResearchNames = ResearchNames(body),
            };
            return rc;
        }

        private static readonly string[] ResearchFields = { "research_done", "research_current" };

        private static HashSet<string> ResearchNames(Node root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Walk(Node n)
            {
                switch (n)
                {
                    case BoolNode b: foreach (var v in b.Values) Walk(v); break;
                    case UnaryNode u: Walk(u.Operand); break;
                    case BinNode bn: Walk(bn.L); Walk(bn.R); break;
                    case SeqNode s: foreach (var e in s.Elts) Walk(e); break;
                    case CmpNode c:
                        var sides = new List<Node> { c.Left };
                        sides.AddRange(c.Comps);
                        if (sides.Any(x => x is NameNode nm && ResearchFields.Contains(nm.Id)))
                        {
                            foreach (var x in sides)
                            {
                                if (x is ConstNode k && k.Value is string ks) names.Add(ks);
                                else if (x is SeqNode sq)
                                    foreach (var e in sq.Elts) if (e is ConstNode ke && ke.Value is string kes) names.Add(kes);
                            }
                        }
                        foreach (var x in sides) Walk(x);
                        break;
                }
            }
            Walk(root);
            return names;
        }

        // ------------------------------------------------------------------ evaluation
        private static readonly object Skip = new object();

        internal static bool? Run(Node body, IDictionary<string, object> facts)
        {
            object r = Eval(body, facts ?? new Dictionary<string, object>());
            if (ReferenceEquals(r, Skip)) return null;
            return Truthy(r);
        }

        private static object Eval(Node n, IDictionary<string, object> f)
        {
            switch (n)
            {
                case ConstNode c: return c.Value;
                case NameNode nm:
                    switch (nm.Id)
                    {
                        case "true": case "True": return true;
                        case "false": case "False": return false;
                        case "None": case "null": return null;
                    }
                    if (!f.TryGetValue(nm.Id, out object fv)) return Skip;
                    return Norm(fv);
                case SeqNode s: return s.Elts.Select(e => ((ConstNode)e).Value).ToList<object>();
                case UnaryNode u:
                    {
                        object v = Eval(u.Operand, f);
                        if (ReferenceEquals(v, Skip)) return Skip;
                        if (u.IsNot) return !Truthy(v);
                        if (v is BigInteger bi) return -bi;
                        if (v is double d) return -d;
                        return Skip;
                    }
                case BinNode b:
                    {
                        object x = Eval(b.L, f);
                        if (ReferenceEquals(x, Skip)) return Skip;
                        object y = Eval(b.R, f);
                        if (ReferenceEquals(y, Skip)) return Skip;
                        if (!(IsNum(x) && IsNum(y))) return Skip;
                        return Arith(b.Op, x, y);
                    }
                case BoolNode bo:
                    {
                        bool skipped = false;
                        foreach (var vn in bo.Values)
                        {
                            object v = Eval(vn, f);
                            if (ReferenceEquals(v, Skip)) { skipped = true; continue; }
                            bool r = Truthy(v);
                            if (r != bo.IsAnd) return r;           // a False in an and / a True in an or decides it
                        }
                        if (skipped) return Skip;
                        return bo.IsAnd;
                    }
                case CmpNode cm:
                    {
                        object left = Eval(cm.Left, f);
                        if (ReferenceEquals(left, Skip)) return Skip;
                        for (int i = 0; i < cm.Ops.Count; i++)
                        {
                            object right = Eval(cm.Comps[i], f);
                            if (ReferenceEquals(right, Skip)) return Skip;
                            string op = cm.Ops[i];
                            bool ok;
                            if (op == "in" || op == "not in")
                            {
                                if (!(right is List<object> lst) || left == null) return Skip;
                                bool has = lst.Any(e => PyEq(left, e));
                                ok = op == "in" ? has : !has;
                            }
                            else if (op == "==") ok = PyEq(left, right);
                            else if (op == "!=") ok = !PyEq(left, right);
                            else
                            {
                                if (!(IsNum(left) && IsNum(right))) return Skip;
                                int? cmp = CompareNum(left, right);
                                switch (op)
                                {
                                    case "<": ok = cmp.HasValue && cmp.Value < 0; break;
                                    case "<=": ok = cmp.HasValue && cmp.Value <= 0; break;
                                    case ">": ok = cmp.HasValue && cmp.Value > 0; break;
                                    default: ok = cmp.HasValue && cmp.Value >= 0; break;
                                }
                            }
                            if (!ok) return false;
                            left = right;
                        }
                        return true;
                    }
                default: return Skip;
            }
        }

        // ------------------------------------------------------------------ Python value semantics
        internal static bool IsNum(object x) => x is BigInteger || x is double;

        /// <summary>Facts from callers (long, int, float, JToken, lists, dictionaries) as the evaluator's value domain:
        /// null, bool, BigInteger, double, string, List&lt;object&gt;, Dictionary&lt;string, object&gt;.</summary>
        internal static object Norm(object v)
        {
            switch (v)
            {
                case null: return null;
                case bool _: case string _: case BigInteger _: case double _: return v;
                case long l: return new BigInteger(l);
                case int i: return new BigInteger(i);
                case short sh: return new BigInteger(sh);
                case byte by: return new BigInteger(by);
                case uint ui: return new BigInteger(ui);
                case ulong ul: return new BigInteger(ul);
                case float fl: return (double)fl;
                case decimal de: return (double)de;
                case JToken jt: return FromToken(jt);
                case IDictionary<string, object> d:
                    {
                        var o = new Dictionary<string, object>();
                        foreach (var kv in d) o[kv.Key] = Norm(kv.Value);
                        return o;
                    }
                case IEnumerable e: return e.Cast<object>().Select(Norm).ToList();
                default: return v;
            }
        }

        internal static object FromToken(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Null: case JTokenType.Undefined: return null;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer:
                    {
                        object o = ((JValue)t).Value;
                        return o is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(o, CultureInfo.InvariantCulture));
                    }
                case JTokenType.Float: return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
                case JTokenType.String: return (string)t;
                case JTokenType.Array: return ((JArray)t).Select(FromToken).ToList();
                case JTokenType.Object:
                    {
                        var d = new Dictionary<string, object>();
                        foreach (var p in ((JObject)t).Properties()) d[p.Name] = FromToken(p.Value);
                        return d;
                    }
                default: return t.ToString();
            }
        }

        internal static bool Truthy(object v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case BigInteger bi: return !bi.IsZero;
                case double d: return d != 0.0;
                case string s: return s.Length > 0;
                case List<object> l: return l.Count > 0;
                case Dictionary<string, object> m: return m.Count > 0;
                default: return true;
            }
        }

        private static bool IsNumLike(object x) => x is BigInteger || x is double || x is bool;
        private static object AsNum(object x) => x is bool b ? (object)(b ? BigInteger.One : BigInteger.Zero) : x;

        /// <summary>Python == between values of the evaluator's domain (bool equals 1 and 0, 1 equals 1.0, nan equals nothing).</summary>
        internal static bool PyEq(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumLike(a) && IsNumLike(b)) { int? c = CompareNum(AsNum(a), AsNum(b)); return c.HasValue && c.Value == 0; }
            if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
            if (a is List<object> la && b is List<object> lb)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!PyEq(la[i], lb[i])) return false;
                return true;
            }
            if (a is Dictionary<string, object> da && b is Dictionary<string, object> db)
            {
                if (da.Count != db.Count) return false;
                foreach (var kv in da) if (!db.TryGetValue(kv.Key, out object o) || !PyEq(kv.Value, o)) return false;
                return true;
            }
            return false;
        }

        /// <summary>Exact comparison of two numbers (int and float compare exactly, as in Python); null when unordered (nan).</summary>
        internal static int? CompareNum(object a, object b)
        {
            if (a is BigInteger x && b is BigInteger y) return x.CompareTo(y);
            if (a is double dx && b is double dy)
            {
                if (double.IsNaN(dx) || double.IsNaN(dy)) return null;
                return dx.CompareTo(dy);
            }
            if (a is BigInteger bi && b is double d2) return CompareIntFloat(bi, d2);
            if (a is double d1 && b is BigInteger bj) { int? r = CompareIntFloat(bj, d1); return r.HasValue ? -r.Value : (int?)null; }
            return null;
        }

        private static int? CompareIntFloat(BigInteger i, double d)
        {
            if (double.IsNaN(d)) return null;
            if (double.IsPositiveInfinity(d)) return -1;
            if (double.IsNegativeInfinity(d)) return 1;
            double fl = Math.Floor(d);
            var bf = new BigInteger(fl);
            int c = i.CompareTo(bf);
            if (c != 0) return c;
            return d > fl ? -1 : 0;
        }

        private static object Arith(char op, object a, object b)
        {
            if (a is BigInteger x && b is BigInteger y)
            {
                switch (op)
                {
                    case '+': return x + y;
                    case '-': return x - y;
                    case '*': return x * y;
                    default:
                        {
                            if (y.IsZero) return Skip;                 // ZeroDivisionError
                            double dx = (double)x, dy = (double)y;
                            if (double.IsInfinity(dx) || double.IsInfinity(dy)) return Skip;   // OverflowError in Python
                            return dx / dy;
                        }
                }
            }
            double p = a is BigInteger ba ? (double)ba : (double)a;
            double q = b is BigInteger bb ? (double)bb : (double)b;
            if ((a is BigInteger && double.IsInfinity(p)) || (b is BigInteger && double.IsInfinity(q))) return Skip;   // OverflowError
            switch (op)
            {
                case '+': return p + q;
                case '-': return p - q;
                case '*': return p * q;
                default: return q == 0.0 ? Skip : (object)(p / q);
            }
        }

        // ------------------------------------------------------------------ entry
        internal static Node ParseForTests(string cond) => new Parser(Lex(PyStr.Strip(cond))).ParseTop();
    }
}

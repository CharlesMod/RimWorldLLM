// Port of core/toolloop/spec.py: tool registration, the GBNF grammar and the prompt text derived from it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    public static class Syntax
    {
        /// <summary>Characters a free-text argument may not contain.</summary>
        public const string ForbiddenText = "<>{}\\\"\n\r\t";
        public const int MaxIntEnum = 40;

        private static readonly Regex RawSyntax = new Regex(
            "<\\|?[a-z_]*(tool|channel|turn)[a-z_]*\\|?>|\\btool_call\\b|\\bcall:\\w+\\s*\\{|<\\|\"\\|>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool HasRawSyntax(string text) => RawSyntax.IsMatch(text ?? "");
    }

    /// <summary>One tool parameter. Kind is text, int, scale or enum.</summary>
    public sealed class Param
    {
        public string Name;
        public string Kind = "text";
        public string Description = "";
        public int MaxLen = 200;
        public int MinLen = 1;
        public long? Lo, Hi;
        public Scale Scale;
        public List<string> Choices;
        public bool Required = true;

        private static readonly Regex IntText = new Regex(@"\A\s*-?\d+\s*\z", RegexOptions.CultureInvariant);

        public Param(string name, string kind = "text", string description = "", int maxLen = 200, int minLen = 1, long? lo = null, long? hi = null,
                     Scale scale = null, IEnumerable<string> choices = null, bool required = true)
        {
            Name = name; Kind = kind; Description = description; MaxLen = maxLen; MinLen = minLen; Lo = lo; Hi = hi; Scale = scale;
            Choices = choices?.ToList(); Required = required;
            if (Kind != "text" && Kind != "int" && Kind != "scale" && Kind != "enum") throw new PyValueError("bad param kind " + Kind);
            if (Kind == "scale" && Scale == null) throw new PyValueError("scale param needs a Scale");
            if (Kind == "enum" && (Choices == null || Choices.Count == 0)) throw new PyValueError("enum param needs choices");
            if (Kind == "int" && (!Lo.HasValue || !Hi.HasValue || Lo > Hi)) throw new PyValueError("int param needs lo <= hi");
        }

        private static string Join(string sep, IEnumerable<string> xs) => string.Join(sep, xs);

        public string Hint()
        {
            switch (Kind)
            {
                case "text": return "short text, at most " + MaxLen.ToString(CultureInfo.InvariantCulture) + " characters";
                case "int": return "a whole number from " + Lo + " to " + Hi;
                case "scale": return "one of: " + Join(" | ", Scale.Words.Select(w => "\"" + w + "\""));
                default: return "one of: " + Join(" | ", Choices.Select(c => "\"" + c + "\""));
            }
        }

        internal static string Lit(string quotedJson) => "\"" + quotedJson.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public string Gbnf()
        {
            if (Kind == "text") return "tstr" + MaxLen.ToString(CultureInfo.InvariantCulture);
            if (Kind == "int")
            {
                if (Hi.Value - Lo.Value + 1 <= Syntax.MaxIntEnum)
                {
                    var parts = new List<string>();
                    for (long n = Lo.Value; n <= Hi.Value; n++) parts.Add("\"" + n.ToString(CultureInfo.InvariantCulture) + "\"");
                    return "(" + Join(" | ", parts) + ")";
                }
                return "integer";
            }
            IEnumerable<string> words = Kind == "scale" ? Scale.Words : Choices;
            return "(" + Join(" | ", words.Select(w => Lit(PyJson.Dumps(w, false)))) + ")";
        }

        public JObject JsonSchema()
        {
            var d = new JObject();
            if (Kind == "text") { d["type"] = "string"; d["maxLength"] = MaxLen; d["minLength"] = MinLen; }
            else if (Kind == "int") { d["type"] = "integer"; d["minimum"] = Lo.Value; d["maximum"] = Hi.Value; }
            else if (Kind == "scale") { d["type"] = "string"; d["enum"] = new JArray(Scale.Words.Select(w => (object)w).ToArray()); }
            else { d["type"] = "string"; d["enum"] = new JArray(Choices.Select(w => (object)w).ToArray()); }
            if (!string.IsNullOrEmpty(Description)) d["description"] = Description;
            return d;
        }

        /// <summary>(game value, error). The value is a JValue (string or integer). Tolerant accepts near misses the grammar would have made impossible.</summary>
        public (JToken value, string error) Check(JToken raw, bool tolerant)
        {
            JTokenType rt = raw == null ? JTokenType.Null : raw.Type;
            if (Kind == "text")
            {
                if (rt != JTokenType.String) return (null, Name + " must be text");
                string t = PyStr.CollapseSpaces((string)raw);
                if (t.Length == 0) return (null, Name + " is empty");
                if (PyStr.Len(t) > MaxLen) return (null, Name + " is longer than " + MaxLen.ToString(CultureInfo.InvariantCulture) + " characters");
                if (Syntax.HasRawSyntax(t)) return (null, Name + " contains tool syntax");
                if (t.IndexOfAny(new[] { '<', '>', '{', '}' }) >= 0) return (null, Name + " contains characters < > { }");
                return (new JValue(t), null);
            }
            if (Kind == "int")
            {
                if (rt == JTokenType.Boolean) return (null, Name + " must be a whole number");
                BigInteger? n = null;
                if (rt == JTokenType.String && IntText.IsMatch((string)raw)) n = ParseIntText((string)raw);
                else if (rt == JTokenType.Float)
                {
                    double d = (double)raw;
                    if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d) n = new BigInteger(d);
                }
                else if (rt == JTokenType.Integer) n = ((JValue)raw).Value is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(((JValue)raw).Value, CultureInfo.InvariantCulture));
                if (!n.HasValue) return (null, Name + " must be a whole number");
                if (n.Value < Lo.Value || n.Value > Hi.Value)
                    return (null, Name + " must be from " + Lo + " to " + Hi + ", not " + n.Value.ToString(CultureInfo.InvariantCulture));
                return (new JValue((long)n.Value), null);
            }
            if (Kind == "scale")
            {
                if (rt == JTokenType.String && Scale.Has((string)raw)) return (new JValue(Scale.ToValue((string)raw)), null);
                if (tolerant)
                {
                    int? v = Scale.Parse(raw);
                    if (v.HasValue) return (new JValue(v.Value), null);
                }
                return (null, Name + " must be one of: " + Join(", ", Scale.Words.Select(w => "\"" + w + "\"")));
            }
            if (rt == JTokenType.String)
            {
                string s = (string)raw;
                foreach (var c in Choices) if (c == s) return (new JValue(c), null);
                if (tolerant)
                {
                    var low = new Dictionary<string, string>();
                    foreach (var c in Choices) low[c.ToLowerInvariant()] = c;
                    if (low.TryGetValue(PyStr.Strip(s).ToLowerInvariant(), out string hit)) return (new JValue(hit), null);
                }
            }
            return (null, Name + " must be one of: " + Join(", ", Choices.Select(c => "\"" + c + "\"")));
        }

        private static BigInteger ParseIntText(string s)
        {
            string t = PyStr.Strip(s);
            bool neg = t.StartsWith("-", StringComparison.Ordinal);
            if (neg) t = t.Substring(1);
            BigInteger v = BigInteger.Zero;
            foreach (char ch in t) v = v * 10 + (int)char.GetNumericValue(ch);
            return neg ? -v : v;
        }
    }

    /// <summary>A tool the model may call. ResultScales turns numbers in a dict result back into words for the model.
    /// EndsTurn: once called, the turn is over.</summary>
    public sealed class ToolSpec
    {
        public string Name;
        public string Description;
        public List<Param> Params;
        public Dictionary<string, Scale> ResultScales;
        public bool EndsTurn;

        private static readonly Regex NameRe = new Regex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

        public ToolSpec(string name, string description, IEnumerable<Param> prms = null, Dictionary<string, Scale> resultScales = null, bool endsTurn = false)
        {
            Name = name; Description = description;
            Params = prms?.ToList() ?? new List<Param>();
            ResultScales = resultScales ?? new Dictionary<string, Scale>();
            EndsTurn = endsTurn;
            if (!NameRe.IsMatch(Name)) throw new PyValueError("bad tool name " + Name);
        }

        public string Signature()
        {
            if (Params.Count == 0) return Name + "()";
            return Name + "(" + string.Join(", ", Params.Select(p => p.Name + (p.Required ? "" : "?"))) + ")";
        }

        public JObject OpenAiTool()
        {
            var props = new JObject();
            foreach (var p in Params) props[p.Name] = p.JsonSchema();
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = Name,
                    ["description"] = Description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = new JArray(Params.Where(p => p.Required).Select(p => (object)p.Name).ToArray()),
                        ["additionalProperties"] = false,
                    },
                },
            };
        }

        /// <summary>Validate raw model args and translate to game values. Unknown keys are an error.</summary>
        public (JObject values, string error) CheckArgs(JToken raw, bool tolerant)
        {
            if (raw == null || raw.Type == JTokenType.Null) raw = new JObject();
            if (!(raw is JObject ro)) return (new JObject(), "args must be an object");
            var names = new HashSet<string>(Params.Select(p => p.Name));
            var extra = ro.Properties().Select(p => p.Name).Where(k => !names.Contains(k)).Distinct().ToList();
            if (extra.Count > 0)
            {
                extra.Sort(PyJson.CompareCodePoints);
                return (new JObject(), "unknown argument " + extra[0] + " for " + Name);
            }
            var outp = new JObject();
            foreach (var p in Params)
            {
                if (!ro.TryGetValue(p.Name, out JToken rv))
                {
                    if (p.Required) return (new JObject(), "missing argument " + p.Name + " for " + Name);
                    continue;
                }
                var (v, err) = p.Check(rv, tolerant);
                if (err != null) return (new JObject(), err);
                outp[p.Name] = v;
            }
            return (outp, null);
        }

        public string RenderResult(JToken result)
        {
            if (result is JObject o)
            {
                var r = new JObject();
                foreach (var p in o.Properties())
                {
                    JToken v = p.Value;
                    if (ResultScales.TryGetValue(p.Name, out Scale sc) && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float))
                        v = new JValue(sc.ToWords(Convert.ToDouble(((JValue)v).Value, CultureInfo.InvariantCulture)));
                    r[p.Name] = v;
                }
                return PyJson.Dumps(r, false);
            }
            if (result == null || result.Type == JTokenType.Null) return "ok";
            return PyJson.Str(result);
        }
    }

    public static class Grammar
    {
        private static string Tstr(int n)
        {
            const string cls = "[^\"\\\\\\n\\r\\t<>{} ]";
            const string rest = "[^\"\\\\\\n\\r\\t<>{}]";
            return "\"\\\"\" " + cls + " " + rest + "{0," + Math.Max(n - 1, 0).ToString(CultureInfo.InvariantCulture) + "} \"\\\"\"";
        }

        /// <summary>GBNF for exactly one step: a tool call, a final line, or done.</summary>
        public static string Build(IReadOnlyList<ToolSpec> tools, int finalMax = 200, bool allowFinal = true, bool allowDone = true)
        {
            var alts = new List<string>();
            var lens = new SortedSet<int>();
            if (allowFinal) lens.Add(finalMax);
            foreach (var t in tools) foreach (var p in t.Params) if (p.Kind == "text") lens.Add(p.MaxLen);
            var rules = new List<string>();
            foreach (int n in lens) rules.Add("tstr" + n.ToString(CultureInfo.InvariantCulture) + " ::= " + Tstr(n));
            rules.Add("integer ::= \"-\"? [0-9]{1,6}");
            for (int i = 0; i < tools.Count; i++)
            {
                var t = tools[i];
                var parts = new List<string>();
                for (int j = 0; j < t.Params.Count; j++)
                {
                    var p = t.Params[j];
                    parts.Add((j > 0 ? "\",\" " : "") + "\"\\\"" + p.Name + "\\\":\" " + p.Gbnf());
                }
                string body = "\"{\\\"tool\\\":\\\"" + t.Name + "\\\",\\\"args\\\":{\" " + string.Join(" ", parts) + (parts.Count > 0 ? " " : "") + "\"}}\"";
                rules.Add("call" + i.ToString(CultureInfo.InvariantCulture) + " ::= " + body);
                alts.Add("call" + i.ToString(CultureInfo.InvariantCulture));
            }
            if (allowFinal)
            {
                rules.Add("final ::= \"{\\\"final\\\":\" \" \"? tstr" + finalMax.ToString(CultureInfo.InvariantCulture) + " \"}\"");
                alts.Add("final");
            }
            if (allowDone)
            {
                rules.Add("done ::= \"{\\\"done\\\":true}\"");
                alts.Add("done");
            }
            if (alts.Count == 0) throw new PyValueError("no tools and no final or done: nothing the model may say");
            return "root ::= " + string.Join(" | ", alts) + "\n" + string.Join("\n", rules) + "\n";
        }
    }

    public static class Prompt
    {
        public const string Protocol =
            "Reply with exactly one JSON object and nothing else. The object is one of:\n" +
            "  {\"tool\": \"<name>\", \"args\": {...}}   call one tool\n" +
            "  {\"final\": \"<one spoken line>\"}       say a last line and stop\n" +
            "  {\"done\": true}                       stop; nothing more to do\n";

        public static string RenderTools(IReadOnlyList<ToolSpec> tools)
        {
            var lines = new List<string> { "Tools:" };
            foreach (var t in tools)
            {
                lines.Add("- " + t.Signature() + ": " + t.Description);
                foreach (var p in t.Params)
                    lines.Add("    " + p.Name + ": " + (p.Description.Length > 0 ? p.Description + "; " : "") + p.Hint());
            }
            return string.Join("\n", lines);
        }

        public static string RenderSystem(string baseText, IReadOnlyList<ToolSpec> tools)
            => PyStr.Strip(baseText) + "\n\n" + Protocol + "\n" + RenderTools(tools);
    }
}

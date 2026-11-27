// Port of core/toolloop/parse.py: turn model text into a step. Strict JSON in grammar mode; tolerant salvage outside it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    /// <summary>What the model said, before validation. Kind: call, final, done.</summary>
    public sealed class RawStep
    {
        public string Kind;
        public string Tool = "";
        /// <summary>The arguments as the model wrote them: any JSON value (null when absent).</summary>
        public JToken Args;
        public string Text = "";
        /// <summary>Non-empty when a repair was needed to read it.</summary>
        public string Salvaged = "";
        public RawStep(string kind) { Kind = kind; }
    }

    public static class StepParser
    {
        public const string GemmaQuote = "<|\"|>";

        private static readonly Regex Fence = new Regex("^```(?:json)?\\s*|\\s*```$", RegexOptions.CultureInvariant);
        private static readonly Regex Gemma = new Regex(@"(?:<\|tool_call>)?\s*(?:call:)?([A-Za-z_]\w*)\s*\{(.*?)\}\s*(?:<tool_call\|>|$)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex BareKey = new Regex(@"([{,]\s*)([A-Za-z_]\w*)\s*:", RegexOptions.CultureInvariant);

        /// <summary>Top-level balanced {...} slices, string-aware.</summary>
        public static List<string> JsonObjects(string text)
        {
            var outp = new List<string>();
            int depth = 0, start = -1; bool instr = false, esc = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (instr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') instr = false;
                    continue;
                }
                if (ch == '"' && depth > 0) instr = true;
                else if (ch == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (ch == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0) outp.Add(text.Substring(start, i + 1 - start));
                }
            }
            return outp;
        }

        private static bool IsDoneValue(JToken v)
        {
            if (v == null) return false;
            switch (v.Type)
            {
                case JTokenType.Boolean: return (bool)v;
                case JTokenType.String: return (string)v == "true";
                case JTokenType.Integer: return ((JValue)v).Value is long l ? l == 1 : false;
                case JTokenType.Float: return (double)v == 1.0;
                default: return false;
            }
        }

        private static JToken Get(JObject o, string key) => o.TryGetValue(key, out JToken v) ? v : null;

        private static RawStep FromObj(JToken tok, ISet<string> names)
        {
            if (!(tok is JObject o)) return null;
            if (o.Count == 1 && names != null && names.Count > 0)                      // {"remember": {...}}: the tool name used as the key
            {
                var p = o.Properties().First();
                if (names.Contains(p.Name) && p.Value is JObject)
                    return new RawStep("call") { Tool = p.Name, Args = p.Value, Salvaged = "tool name as key" };
            }
            if (IsDoneValue(Get(o, "done")) && o.Count == 1) return new RawStep("done");
            foreach (var k in new[] { "final", "say", "reply" })
                if (Get(o, k) is JValue fv && fv.Type == JTokenType.String && o.Count == 1) return new RawStep("final") { Text = (string)fv };
            foreach (var k in new[] { "tool", "name", "tool_name", "function" })
            {
                if (Get(o, k) is JValue kv && kv.Type == JTokenType.String)
                {
                    var rest = new JObject();
                    foreach (var p in o.Properties())
                        if (!(p.Name == "tool" || p.Name == "name" || p.Name == "tool_name" || p.Name == "function" || p.Name == "args" || p.Name == "arguments" || p.Name == "parameters"))
                            rest[p.Name] = p.Value.DeepClone();
                    JToken args = o.ContainsKey("args") ? o["args"] : o.ContainsKey("arguments") ? o["arguments"] : o.ContainsKey("parameters") ? o["parameters"] : rest;
                    if (args != null && args.Type == JTokenType.String)
                    {
                        try { args = PyJson.Loads((string)args); }
                        catch (PyJsonException) { }
                    }
                    return new RawStep("call") { Tool = (string)kv, Args = args };
                }
            }
            foreach (var k in new[] { "tool_call", "call", "function_call" })
                if (Get(o, k) is JObject inner) return FromObj(inner, names);
            return null;
        }

        /// <summary>`line:&lt;|"|&gt;text&lt;|"|&gt;,importance:3` to a dict. Null when it cannot be read.</summary>
        public static JObject GemmaArgs(string body)
        {
            string b = body.Replace(GemmaQuote, "\"");
            b = BareKey.Replace("{" + b + "}", "$1\"$2\":");
            try { return PyJson.Loads(b) as JObject; }
            catch (PyJsonException) { return null; }
        }

        /// <summary>(step, why-not). Strict: one JSON object. Salvage: fenced or surrounded JSON, Gemma raw call syntax.</summary>
        public static (RawStep step, string why) ParseText(string text, ISet<string> toolNames)
        {
            string t = PyStr.Strip(text ?? "");
            if (t.Length == 0) return (null, "the reply was empty");
            try
            {
                var s = FromObj(PyJson.Loads(t), toolNames);
                if (s != null) return (s, "");
            }
            catch (PyJsonException) { }
            string t2 = PyStr.Strip(Fence.Replace(t, ""));
            foreach (var cand in JsonObjects(t2))
            {
                RawStep s;
                try { s = FromObj(PyJson.Loads(cand), toolNames); }
                catch (PyJsonException) { continue; }
                if (s != null)
                {
                    s.Salvaged = "json inside other text";
                    return (s, "");
                }
            }
            var m = Gemma.Match(t);
            if (m.Success && toolNames.Contains(m.Groups[1].Value))
            {
                string body = m.Groups[2].Value;
                JObject args = PyStr.Strip(body).Length > 0 ? GemmaArgs(body) : new JObject();
                if (args != null) return (new RawStep("call") { Tool = m.Groups[1].Value, Args = args, Salvaged = "gemma raw call syntax" }, "");
            }
            return (null, "the reply was not one JSON object of the allowed forms");
        }
    }
}

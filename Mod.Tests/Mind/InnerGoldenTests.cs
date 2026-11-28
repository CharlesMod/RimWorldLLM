// Golden tests: prompt text, schemas and hash picks of the inner modules, captured from CPython by csmind/inner/gen_golden.py.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class InnerGoldenTests
    {
        static readonly JObject G = JObject.Parse(InnerGoldenData.Json);
        static readonly RimWorldAdapter AD = new RimWorldAdapter();
        static readonly Dictionary<int, string> NAMES = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" }, { 3, "Cy" } };
        static MindM M() { return MindM.FromJson(G["mind"].ToString()); }
        static List<(string, string)> Msgs(JToken t) { return t.Select(x => ((string)x[0], (string)x[1])).ToList(); }
        // The C# RimWorldAdapter text (contract worker, not mine) lacks the Python _NO_META_RULE sentence added to reaction_request after the port;
        // see docs/csmind/parity-inner.md. Only the bystander goldens strip it.
        const string NoMeta = " Don't remark on the conversation or the act of talking itself; react to the people and what's happening.";
        static void SameMsgs(JToken want, List<(string, string)> got, bool stripNoMeta = false)
        {
            var w = Msgs(want);
            if (stripNoMeta) w = w.Select(x => (x.Item1, x.Item2.Replace(NoMeta, ""))).ToList();
            Assert.Equal(w.Count, got.Count);
            for (int i = 0; i < w.Count; i++) { Assert.Equal(w[i].Item1, got[i].Item1); Assert.Equal(w[i].Item2, got[i].Item2); }
        }

        /// <summary>Turncost: the bio section (fixed per actor) moved from the head of the reflection's user message to the end of its
        /// system message, so the system message is the same for that actor across reflections. Nothing else changed.</summary>
        static JToken ReflReshaped(JToken want)
        {
            var r = (JArray)want.DeepClone();
            string user = (string)r[1][1];
            int i = user.IndexOf("=== WHO YOU ARE BECOMING NOW ===", StringComparison.Ordinal);
            Assert.True(i > 0);
            r[0][1] = (string)r[0][1] + "\n" + user.Substring(0, i);
            r[1][1] = user.Substring(i);
            return r;
        }

        [Fact]
        public void ReflectionMessagesAreByteIdenticalInAllEightFlagCombinations()
        {
            var m = M();
            foreach (int a in new[] { 0, 1 }) foreach (int h in new[] { 0, 1 }) foreach (int c in new[] { 0, 1 })
                SameMsgs(ReflReshaped(G["refl"][$"{a}{h}{c}"]), Reflection.ReflectionMessages(AD, m, NAMES, 9000, 60000, a == 1, h == 1, c == 1));
        }

        [Fact]
        public void BystanderMessagesAreByteIdentical()
        {
            var m = M();
            SameMsgs(G["bystander"], BystanderOps.Messages(AD, m, NAMES, new List<int> { 1, 2 }, "Ana -> Bram: insult.", false));
            SameMsgs(G["bystander_minor"], BystanderOps.Messages(AD, m, NAMES, new List<int> { 1, 2 }, "Ana -> Bram: insult.", true, "simple words"));
        }

        [Fact]
        public void SubtextKindMatchesPythonSha1Pick()
        {
            foreach (var r in (JArray)G["subtext"]) Assert.Equal((string)r[3], Inner.SubtextKind((int)r[0], (int)r[1], (int)r[2]));
        }

        [Fact]
        public void BystanderTemplatePickMatchesPython()
        {
            var tpl = new List<string> { "Not again.", "Here we go.", "Oh, come on.", "Ugh." };
            foreach (var r in (JArray)G["template"])
            {
                object key = r[1].Type == JTokenType.Integer ? (object)(long)r[1] : (string)r[1];
                Assert.Equal((string)r[2], BystanderOps.Template(tpl, (int)r[0], key));
            }
        }

        [Fact]
        public void AimTensionTextMatchesPython()
        {
            var m = M();
            var cases = new (int? l, string n, string k)[] { (1, "Ana", "want"), (2, "Bram", "secret"), (null, null, "judgment"), (9, "Zed", "want") };
            for (int i = 0; i < cases.Length; i++)
                Assert.Equal((string)G["aim_tension"][i], Inner.AimTension(m, 3, cases[i].l, cases[i].n, cases[i].k));
        }

        [Fact]
        public void SchemasMatchPython()
        {
            var s = G["schemas"];
            Assert.True(JToken.DeepEquals(s["reflect"], Reflection.ReflectSchema()));
            Assert.True(JToken.DeepEquals(s["concern_on"], ConcernOps.ReflectSchema(Reflection.ReflectSchema(), true)));
            Assert.True(JToken.DeepEquals(s["concern_off"], ConcernOps.ReflectSchema(Reflection.ReflectSchema(), false)));
            Assert.True(JToken.DeepEquals(s["habits"], Reflection.SchemaFor(true)));
        }

        [Fact]
        public void WantsSystemTextAndWorriesBlockMatchPython()
        {
            Assert.Equal((string)G["wants_system"], WantsOps.SystemText);
            Assert.Equal((string)G["worries_block"], ConcernOps.WorriesBlock(M()));
        }
    }
}

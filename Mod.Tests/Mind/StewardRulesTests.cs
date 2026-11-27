// Ported from agent/tests/test_steward_rules.py (every test that needs no Director / world fixture), plus goldens generated
// from the Python (csmind/side/gen_steward_golden.py -> Data/steward_golden.json): conditions evaluated over fact sets,
// all 66 shipped rules over random facts, derive_facts, alerts_section and render_digest over mutated digests.
// Deferred (need the Director / world fixture or core/steward.py): test_user_turn_has_alerts_after_why_you_woke,
// test_cycle_sends_alerts_to_the_model, and the load_strategy half of test_shipped_strategy_loaded_capped_and_override_order.
// Not applicable to C#: test_package_data_lists_strategy_files (the files are embedded).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public static class StewardTestData
    {
        public static string Resource(string name)
        {
            var asm = typeof(StewardTestData).Assembly;
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        public static readonly JObject Sample = JObject.Parse(Resource("RimWorldLLM.Tests.steward_digest_sample.json"));
        public static readonly JObject Golden = JObject.Parse(Resource("RimWorldLLM.Tests.steward_golden.json"));

        public static JObject Clone(JObject o) => (JObject)o.DeepClone();

        public static Dictionary<string, object> Facts(params (string, object)[] kv)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        public static Dictionary<string, object> FactsOf(JToken t)
            => ((Dictionary<string, object>)StewardExpr.FromToken(t));

        /// <summary>Strict comparison: an int and a float with the same value are different, as in Python's repr.</summary>
        public static bool Same(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Type != b.Type) return false;
            switch (a.Type)
            {
                case JTokenType.Object:
                    {
                        var x = (JObject)a; var y = (JObject)b;
                        return x.Count == y.Count && x.Properties().All(p => y.TryGetValue(p.Name, out JToken o) && Same(p.Value, o));
                    }
                case JTokenType.Array:
                    {
                        var x = (JArray)a; var y = (JArray)b;
                        return x.Count == y.Count && x.Zip(y, Same).All(t => t);
                    }
                default: return JToken.DeepEquals(a, b);
            }
        }

        /// <summary>A facts dictionary (as DeriveFacts returns it) as JSON, ints and floats kept apart.</summary>
        public static JToken ToJson(object v)
        {
            switch (v)
            {
                case null: return JValue.CreateNull();
                case bool b: return new JValue(b);
                case long l: return new JValue(l);
                case System.Numerics.BigInteger bi: return new JValue(bi);
                case double d: return new JValue(d);
                case string s: return new JValue(s);
                case List<object> li: return new JArray(li.Select(ToJson));
                case Dictionary<string, object> m: { var o = new JObject(); foreach (var kv in m) o[kv.Key] = ToJson(kv.Value); return o; }
                default: throw new InvalidOperationException(v.GetType().Name);
            }
        }
    }

    public class StewardRulesTests
    {
        private static bool? Ev(string cond, params (string, object)[] f) => StewardRules.CompileCondition(cond).Run(StewardTestData.Facts(f));

        [Fact]
        public void EveryShippedRuleCompiles()
        {
            var raw = (JArray)JObject.Parse(StewardResources.Text(StewardResources.RulesName))["rules"];
            Assert.True(raw.Count > 20);
            var rules = StewardRules.LoadRules();
            Assert.Equal(raw.Count, rules.Count);                  // none dropped
            Assert.Contains(rules, r => r.Id == "food.emergency");
        }

        [Theory]
        [InlineData("__import__('os').system('x')")]
        [InlineData("food_days.real")]
        [InlineData("a[0] < 1")]
        [InlineData("(lambda: 1)()")]
        [InlineData("[x for x in events]")]
        [InlineData("__class__ == 1")]
        [InlineData("len(events) > 1")]
        [InlineData("x if y else z")]
        [InlineData("a ** 2 > 1")]
        [InlineData("a // 2 > 1")]
        [InlineData("a % 2 == 0")]
        [InlineData("~a")]
        [InlineData("a is None")]
        [InlineData("{1: 2}")]
        [InlineData("f'{a}'")]
        [InlineData("a := 1")]
        [InlineData("b'x' == a")]
        [InlineData("[a, b] == c")]
        [InlineData("1 +")]
        [InlineData("")]
        [InlineData("food_days < 3; 1")]
        public void WhitelistRejects(string bad) => Assert.Throws<RuleError>(() => StewardRules.CompileCondition(bad));

        [Fact]
        public void MissingFieldSkipsAndNoneNumericSkips()
        {
            Assert.Null(Ev("food_days < 3"));
            Assert.Null(Ev("food_days < 3", ("food_days", null)));
            Assert.True(Ev("food_days < 3", ("food_days", 2)));
            Assert.True(Ev("food_days >= 3 and food_days < 5", ("food_days", 4)));
            Assert.True(Ev("research_current == null", ("research_current", null)));
            Assert.Null(Ev("research_current == null"));
            // three-valued: a False operand still decides an and; a True one an or
            Assert.False(Ev("food_days < 3 and mood_avg < 40", ("mood_avg", 50)));
            Assert.True(Ev("food_days < 3 or mood_avg < 40", ("mood_avg", 30)));
            Assert.Null(Ev("food_days < 3 or mood_avg < 40", ("mood_avg", 50)));
            Assert.Null(Ev("not food_days < 3"));
        }

        [Fact]
        public void InNotInAndLists()
        {
            Assert.True(Ev("'cold_snap' in events", ("events", new List<object> { "cold_snap" })));
            Assert.True(Ev("'cold_snap' not in events", ("events", new List<object>())));
            Assert.True(Ev("season in ['summer','fall']", ("season", "fall")));
            Assert.False(Ev("season in ['summer','fall']", ("season", "winter")));
            Assert.Null(Ev("'x' in events"));
            Assert.Null(Ev("'x' in events", ("events", null)));
            Assert.True(Ev("raid_active == false and raid_active != true", ("raid_active", false)));
        }

        [Fact]
        public void ArithmeticAndChains()
        {
            Assert.True(Ev("food_days < days_to_growing - days_to_winter + 5", ("food_days", 4), ("days_to_growing", 40), ("days_to_winter", 10)));
            Assert.True(Ev("-a < 0", ("a", 3)));
            Assert.True(Ev("a * 2 > b / 2", ("a", 3), ("b", 4)));
            Assert.Null(Ev("a / 0 > 1", ("a", 3)));
            Assert.Null(Ev("a + 1 > 1", ("a", null)));
            Assert.True(Ev("0 < a < 5", ("a", 3)));
            Assert.False(Ev("0 < a < 5", ("a", 7)));
            Assert.True(Ev("true"));
            Assert.False(Ev("false"));
            Assert.True(Ev("a == True", ("a", true)));
        }

        [Fact]
        public void AlwaysOnFlagAndExclusion()
        {
            var rules = StewardRules.LoadRules().ToDictionary(r => r.Id);
            var always = rules.Values.Where(r => r.AlwaysOn).ToList();
            Assert.NotEmpty(always);
            Assert.All(always, r => Assert.True(r.Run.Run(new Dictionary<string, object>())));
            var fired = StewardRules.FiredRules(StewardTestData.Facts(("food_days", 1))).Select(x => x.Id).ToHashSet();
            Assert.Contains("food.emergency", fired);
            Assert.Empty(fired.Intersect(always.Select(r => r.Id)));
        }

        [Fact]
        public void PrioritySortCapAndP1Always()
        {
            var fired = new List<(long, string, string)>();
            for (int i = 0; i < 10; i++) fired.Add((2, "b" + i, "x"));
            for (int i = 0; i < 11; i++) fired.Add((1, "a" + i, "y"));
            fired.Add((3, "c", "z"));
            Func<IEnumerable<(long, string, string)>, List<(long Priority, string Id, string Action)>> sorted = xs =>
                xs.OrderBy(t => t.Item1).ThenBy(t => t.Item2, StringComparer.Ordinal).Select(t => (t.Item1, t.Item2, t.Item3)).ToList();
            var sel = StewardRules.Select(sorted(fired));
            Assert.All(sel, r => Assert.Equal(1, r.Priority));
            Assert.Equal(11, sel.Count);                          // all P1 shown even over the cap of 8
            sel = StewardRules.Select(sorted(fired.Take(10).Concat(fired.Skip(10).Take(3))));
            Assert.Equal(8, sel.Count);
            Assert.Equal(1, sel[0].Priority);
            Assert.Equal(sel.Select(r => r.Priority).OrderBy(x => x).ToList(), sel.Select(r => r.Priority).ToList());
        }

        [Fact]
        public void FactsDerivationFromDigestAndPrecedence()
        {
            var d = StewardTestData.Clone(StewardTestData.Sample);
            d.Remove("facts");
            var f = StewardRules.DeriveFacts(d);
            Assert.Equal(2.4, f["food_days"]);
            Assert.Equal(5L, f["colonists"]);
            Assert.Equal(38L, f["mood_min"]);
            Assert.Equal(56.6, f["mood_avg"]);
            Assert.Equal(0L, f["colonists_downed"]);
            Assert.Equal(2L, f["beds_free"]);
            Assert.Equal(410L, f["steel"]);
            Assert.Equal(280L, f["wood"]);
            Assert.Equal(6L, f["components"]);
            Assert.False(f.ContainsKey("silver"));
            Assert.Equal(4L, f["medicine_total"]);
            Assert.Equal(0L, f["medicine_industrial"]);
            Assert.Equal("Electricity", f["research_current"]);
            Assert.Equal(false, f["raid_active"]);
            Assert.Equal(0L, f["enemies_count"]);
            Assert.Equal(0L, f["fires"]);
            Assert.Equal("spring", f["season"]);
            Assert.Equal(12.5, f["temp_c"]);
            Assert.Equal(0L, f["power_net_w"]);
            Assert.Equal(0L, f["battery_wd"]);
            Assert.False(f.ContainsKey("day"));
            Assert.False(f.ContainsKey("prisoners"));
            Assert.False(f.ContainsKey("dlc"));                     // never invented
            d["facts"] = new JObject { ["food_days"] = 9.0, ["day"] = 33 };
            f = StewardRules.DeriveFacts(d);
            Assert.Equal(9.0, f["food_days"]);
            Assert.Equal(33L, f["day"]);
            Assert.Equal(5L, f["colonists"]);
            d["threats"] = JArray.Parse(@"[{""count"": 4, ""label"": ""raiders"", ""hostile"": true}, {""count"": 2, ""label"": ""x"", ""hostile"": false}]");
            d["alerts"] = JArray.Parse(@"[{""alert"": ""Alert_Fire"", ""label"": ""Fire"", ""critical"": true}]");
            d.Remove("facts");
            f = StewardRules.DeriveFacts(d);
            Assert.Equal(true, f["raid_active"]);
            Assert.Equal(4L, f["enemies_count"]);
            Assert.Equal(1L, f["fires"]);
            Assert.Empty(StewardRules.DeriveFacts(new JObject()));
        }

        [Fact]
        public void AlertsSectionShapeAndOmittedWhenNone()
        {
            string s = StewardRules.AlertsSection(StewardTestData.Sample);
            Assert.StartsWith(StewardRules.Heading + "\n", s);
            Assert.Contains("[P1 food.emergency] ", s);
            Assert.True(s.Length <= StewardRules.SectionCap);
            var lines = s.Split('\n').Where(l => l.Length > 0).Skip(1).ToList();
            Assert.All(lines, l => Assert.StartsWith("[P", l));
            Assert.True(lines.Count <= 8);
            Assert.Equal(lines.Select(l => l.Split(' ')[0]).OrderBy(x => x, StringComparer.Ordinal).ToList(), lines.Select(l => l.Split(' ')[0]).ToList());
            var calm = JObject.Parse(@"{""date"": {""season"": ""spring""}, ""food_days"": 30, ""pawns"": [], ""resources"": []}");
            Assert.DoesNotContain("food.emergency", StewardRules.AlertsSection(calm));
        }

        [Fact]
        public void AlertsSectionCapKeepsP1()
        {
            string big = string.Concat(Enumerable.Repeat("word ", 120));
            var compiled = StewardRules.CompileCondition("true == true");
            var rules = Enumerable.Range(0, 8).Select(i => new PlaybookRule("r." + i, i < 3 ? 1 : 2, big, compiled, false)).ToList();
            string s = StewardRules.AlertsSection(new JObject(), StewardRules.SectionCap, rules);
            Assert.True(s.Length <= StewardRules.SectionCap);
            Assert.Equal(3, CountOf(s, "[P1 "));
            Assert.Contains("[P1 r.0]", s);
            Assert.Equal(0, CountOf(s, "[P2 "));
        }

        private static int CountOf(string s, string sub)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
            return n;
        }

        [Fact]
        public void ShippedStrategyAndSystemPromptAreEmbedded()
        {
            string text = StewardResources.Text(StewardResources.StrategyName);
            Assert.StartsWith("You are the RimWorld colony steward", text);
            Assert.Contains("## Order of concern", text);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 12288);       // the Config default steward_strategy_max_bytes
        }

        [Fact]
        public void UnknownResearchNameTurnsRuleOff()
        {
            StewardRules.ResetWarned();
            var run = StewardRules.CompileCondition("'Batteries' not in research_done and colonists > 0");
            Assert.Equal(new[] { "Batteries" }, run.ResearchNames.ToArray());
            var facts = StewardTestData.Facts(("research_done", new List<object>()), ("colonists", 3));
            Assert.True(run.Run(facts));
            Assert.Empty(StewardRules.UnknownResearch(run.ResearchNames, facts));          // no list: cannot judge
            facts["research_known"] = new List<object> { "Electricity" };
            Assert.Equal(new[] { "Batteries" }, StewardRules.UnknownResearch(run.ResearchNames, facts).ToArray());
            facts["research_known"] = new List<object> { "Batteries", "Electricity" };
            Assert.Empty(StewardRules.UnknownResearch(run.ResearchNames, facts));
        }

        [Fact]
        public void FiredRulesSkipRulesWithUnknownResearch()
        {
            var rules = StewardRules.LoadRules().Where(r => r.Run.ResearchNames.Count > 0).ToList();
            Assert.NotEmpty(rules);                                                       // the shipped playbook names some research
            var ids = rules.Select(r => r.Id).ToHashSet();
            var baseFacts = StewardTestData.Facts(("research_known", new List<object>()));  // the game defines no research at all
            Assert.DoesNotContain(StewardRules.FiredRules(baseFacts), x => ids.Contains(x.Id));
            var names = rules[0].Run.ResearchNames;
            var known = StewardTestData.Facts(("research_known", names.Cast<object>().OrderBy(x => x).ToList()));
            Assert.Empty(StewardRules.UnknownResearch(names, known));
        }

        // ---- extras beyond the Python tests -------------------------------------------------------------------

        [Fact]
        public void BadRulesAreDroppedWithTheRestKept()
        {
            string json = @"{""rules"": [
                {""id"": ""ok"", ""priority"": 2, ""action"": "" do it "", ""condition"": ""a > 1""},
                {""id"": ""bad1"", ""priority"": 1, ""action"": ""x"", ""condition"": ""a.b""},
                {""id"": ""bad2"", ""action"": ""x"", ""condition"": ""a > 1""},
                {""id"": ""bad3"", ""priority"": ""high"", ""action"": ""x"", ""condition"": ""a > 1""},
                {""id"": ""bad4"", ""priority"": 1, ""action"": ""x""},
                5,
                {""id"": 7, ""priority"": 2.9, ""action"": ""on"", ""condition"": ""true""}]}";
            var rules = StewardRules.ParseRules(() => json);
            Assert.Equal(new[] { "ok", "7" }, rules.Select(r => r.Id).ToArray());
            Assert.Equal("do it", rules[0].Action);
            Assert.Equal(2L, rules[1].Priority);
            Assert.True(rules[1].AlwaysOn);
            Assert.Empty(StewardRules.ParseRules(() => "not json"));
            Assert.Empty(StewardRules.ParseRules(() => throw new IOException("gone")));
        }

        [Fact]
        public void FiredRulesUseInjectedRulesAndSortByPriorityThenId()
        {
            var run = StewardRules.CompileCondition("a > 1");
            var rules = new[]
            {
                new PlaybookRule("z", 2, "zz", run, false), new PlaybookRule("b", 2, "bb", run, false),
                new PlaybookRule("p", 1, "pp", run, false), new PlaybookRule("t", 1, "tt", StewardRules.CompileCondition("true"), true),
            };
            var fired = StewardRules.FiredRules(StewardTestData.Facts(("a", 3)), rules);
            Assert.Equal(new[] { "p", "b", "z" }, fired.Select(x => x.Id).ToArray());
        }

        [Fact]
        public void IntAndFloatCompareExactlyAndBoolIsNotANumber()
        {
            Assert.True(Ev("a == 1", ("a", 1.0)));
            Assert.True(Ev("a == 1.0", ("a", 1)));
            Assert.True(Ev("a == 1", ("a", true)));                // True == 1 in Python
            Assert.Null(Ev("a < 2", ("a", true)));                 // but a bool is not a number for ordering
            Assert.Null(Ev("-a < 2", ("a", true)));
            Assert.False(Ev("a == 9007199254740993", ("a", 9007199254740992.0)));   // exact int/float comparison
            Assert.True(Ev("a < 9007199254740993", ("a", 9007199254740992.0)));
            Assert.False(Ev("a == a", ("a", double.NaN)));
            Assert.Null(Ev("a < 'x'", ("a", 1)));
            Assert.Null(Ev("a / 0.0 > 1", ("a", 1)));
        }

        [Fact]
        public void ConstantsAndNames()
        {
            var always = StewardRules.CompileCondition("true");
            Assert.True(always.AlwaysOn);
            Assert.False(StewardRules.CompileCondition("True").AlwaysOn);       // only the name true is always-on
            Assert.False(StewardRules.CompileCondition("not true").AlwaysOn);
            Assert.Null(Ev("null == a"));
            Assert.True(Ev("null == a", ("a", null)));
            Assert.True(Ev("true == a", ("a", true)));
            Assert.Throws<RuleError>(() => StewardRules.CompileCondition(new string('a', 401) + " < 1"));
            Assert.Throws<RuleError>(() => StewardRules.CompileCondition(null));
            Assert.Throws<RuleError>(() => StewardRules.CompileCondition("   "));
        }

        // ---- goldens from the Python ---------------------------------------------------------------------------

        public static IEnumerable<object[]> GoldenConds() => ((JArray)StewardTestData.Golden["conds"]).Select((c, i) => new object[] { i });

        [Theory]
        [MemberData(nameof(GoldenConds))]
        public void ConditionMatchesPython(int idx)
        {
            var c = (JObject)((JArray)StewardTestData.Golden["conds"])[idx];
            string cond = (string)c["cond"];
            if (cond.Contains("\\N{"))                       // documented divergence: \N{NAME} escapes are refused
            {
                Assert.Throws<RuleError>(() => StewardRules.CompileCondition(cond));
                return;
            }
            if (!(bool)c["ok"])
            {
                Assert.Throws<RuleError>(() => StewardRules.CompileCondition(cond));
                return;
            }
            var run = StewardRules.CompileCondition(cond);
            Assert.Equal((bool)c["always"], run.AlwaysOn);
            Assert.Equal(((JArray)c["research"]).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                run.ResearchNames.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            var sets = (JArray)StewardTestData.Golden["factsets"];
            var results = (JArray)c["results"];
            for (int i = 0; i < sets.Count; i++)
            {
                bool? want = results[i].Type == JTokenType.Null ? (bool?)null : (bool)results[i];
                bool? got = run.Run(StewardTestData.FactsOf(sets[i]));
                Assert.True(want == got, "cond " + cond + " facts " + sets[i].ToString(Newtonsoft.Json.Formatting.None) + " want " + want + " got " + got);
            }
        }

        [Fact]
        public void ShippedRulesMatchPythonOverRandomFacts()
        {
            var g = StewardTestData.Golden;
            var ids = ((JArray)g["rule_ids"]).Select(x => (string)x).ToList();
            var rules = StewardRules.LoadRules();
            Assert.Equal(ids, rules.Select(r => r.Id).ToList());
            Assert.Equal(((JArray)g["rule_always"]).Select(x => (bool)x).ToList(), rules.Select(r => r.AlwaysOn).ToList());
            for (int i = 0; i < rules.Count; i++)
                Assert.Equal(((JArray)g["rule_research"][i]).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    rules[i].Run.ResearchNames.OrderBy(x => x, StringComparer.Ordinal).ToList());
            var facts = (JArray)g["rule_facts"];
            for (int k = 0; k < facts.Count; k++)
            {
                var f = StewardTestData.FactsOf(facts[k]);
                string got = string.Concat(rules.Select(r => { var v = r.Run.Run(f); return v == null ? "N" : v.Value ? "T" : "F"; }));
                Assert.Equal((string)g["rule_results"][k], got);
                var fired = StewardRules.FiredRules(f);
                var want = ((JArray)g["rule_fired"][k]).Select(x => ((long)x[0], (string)x[1], (string)x[2])).ToList();
                Assert.Equal(want, fired.Select(x => (x.Priority, x.Id, x.Action)).ToList());
            }
        }

        [Fact]
        public void DeriveFactsAndAlertsMatchPython()
        {
            var g = StewardTestData.Golden;
            var digests = (JArray)g["digests"];
            for (int i = 0; i < digests.Count; i++)
            {
                var d = (JObject)digests[i];
                var got = StewardTestData.ToJson(StewardRules.DeriveFacts(d));
                Assert.True(StewardTestData.Same(g["derive"][i], got), "derive " + i + "\nwant " + g["derive"][i].ToString(Newtonsoft.Json.Formatting.None) + "\n got " + got.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal((string)g["alerts"][i], StewardRules.AlertsSection(d));
            }
            foreach (var pair in (JArray)g["alerts_cap"])
                Assert.Equal((string)pair[1], StewardRules.AlertsSection((JObject)digests[0], (int)pair[0]));
        }

        [Fact]
        public void TrimMatchesPython()
        {
            // _trim is private in Python too; it is exercised through a tiny cap on the sample's alerts
            foreach (var pair in (JArray)StewardTestData.Golden["alerts_cap"])
                Assert.True(((string)pair[1]).Length <= (int)pair[0]);
        }

        [Fact]
        public void FactsCountStoredPlusLooseButNotForbidden()
        {
            var d = StewardTestData.Clone(StewardTestData.Sample);
            d.Remove("facts");
            d["resources"] = JArray.Parse(@"[{""category"": ""materials"", ""label"": ""steel"", ""count"": 40, ""loose"": 300, ""forbidden"": 50},
                {""category"": ""materials"", ""label"": ""components"", ""count"": 0, ""loose"": 7, ""forbidden"": 0}]");
            var f = StewardRules.DeriveFacts(d);
            Assert.Equal(340L, f["steel"]);
            Assert.Equal(7L, f["components"]);
        }
    }
}

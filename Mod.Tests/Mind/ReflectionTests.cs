// Port of agent/tests/test_reflection.py (the tests that need no Director) plus the RunReflection call itself against FakeLlm.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class ReflectionTests
    {
        const long DAY = 60000;
        static readonly RimWorldAdapter AD = new RimWorldAdapter();

        static string Result(string bio = "I am harder than I was, and tired of the cold.", string[] wants = null, object[] rels = null, object[] merge = null)
        {
            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["bio"] = bio, ["wants"] = wants ?? new[] { "Get warm" }, ["relations"] = rels ?? new object[0], ["merge"] = merge ?? new object[0],
            });
        }

        [Fact]
        public void ReflectionDueOnImportanceOrInterval()
        {
            var m = new MindM(1);
            Assert.False(Reflection.ReflectionDue(m, 10 * DAY, DAY, 3.0, 3.0));
            m.UnreflectedImportance = 0.5;
            Assert.False(Reflection.ReflectionDue(m, 2 * DAY, DAY, 3.0, 3.0));
            Assert.True(Reflection.ReflectionDue(m, 3 * DAY, DAY, 3.0, 3.0));
            m.LastReflectTime = 3 * DAY;
            Assert.False(Reflection.ReflectionDue(m, 4 * DAY, DAY, 3.0, 3.0));
            m.UnreflectedImportance = 3.0;
            Assert.True(Reflection.ReflectionDue(m, 4 * DAY, DAY, 3.0, 3.0));
        }

        [Fact]
        public void ParseResultToleratesFencesAndJunk()
        {
            Assert.Equal(new[] { "Get warm" }, Reflection.ParseResult("```json\n" + Result() + "\n```").Wants);
            Assert.Null(Reflection.ParseResult("no json here"));
            Assert.Null(Reflection.ParseResult("{\"bio\": 3}"));
            Assert.Equal(new[] { "bio", "wants", "relations", "merge" }.OrderBy(x => x), Reflection.ReflectSchema()["required"].Select(x => (string)x).OrderBy(x => x));
        }

        [Fact]
        public void ApplyReflectionBioHistoryWantsRelationsMerge()
        {
            var m = new MindM(1, "Ana");
            m.Relations["2"] = new Relation { Name = "Bram", Opinion = -30 };
            for (int i = 0; i < 3; i++)
                m.Memories.Add(new Memory { Id = "m" + i, Game = i, Text = "I saw Bram snub me " + i, Importance = 0.3, People = new List<int> { 2 } });
            m.Memories.Add(new Memory { Id = "m9", Game = 9, Text = "Cy died.", Importance = 1.0, Pinned = true });
            m.UnreflectedImportance = 4.0;
            var res = new ReflectResult
            {
                Bio = "I am wary of Bram now.", Wants = new List<string> { "a", "b", "c", "d" },
                Relations = new List<ReflectRelation>
                {
                    new ReflectRelation { Who = "Bram", Feeling = "cold toward him", History = "he keeps snubbing me" },
                    new ReflectRelation { Who = "Nobody", Feeling = "x", History = "y" },
                },
                Merge = new List<ReflectMerge>
                {
                    new ReflectMerge { Ids = new List<string> { "m0", "m1", "m2" }, Summary = "Bram snubbed me again and again." },
                    new ReflectMerge { Ids = new List<string> { "m0", "m9" }, Summary = "should be refused, m9 is pinned" },
                },
            };
            var o = Reflection.ApplyReflection(m, res, 5 * DAY, new Dictionary<string, int> { ["bram"] = 2, ["ana"] = 1 });
            Assert.Equal("I am wary of Bram now.", m.EvolvingBio); Assert.Equal(1, m.BioHistory.Last().Rev);
            Assert.Equal(5 * DAY, m.BioHistory.Last().Game); Assert.Equal(new[] { "a", "b", "c" }, m.Wants);
            Assert.Equal("cold toward him", m.Relations["2"].Feeling);
            Assert.DoesNotContain(m.Relations.Values, r => r.Name == "Nobody");
            Assert.Equal(3, o.Merged); Assert.Contains(m.Memories, x => x.Id == "m9");
            Assert.Contains(m.Memories, x => x.SummaryOf == 3); Assert.DoesNotContain(m.Memories, x => x.Id == "m0");
            Assert.Equal(0, m.UnreflectedImportance); Assert.Equal(5 * DAY, m.LastReflectTime);
            Assert.NotEmpty(m.CRelations); Assert.Equal("Bram", m.CRelations[0].Name);
        }

        [Fact]
        public void BioHistoryCappedAtTenWithRevs()
        {
            var m = new MindM(1);
            for (int i = 0; i < 14; i++) m.AddBioDiff(i * 100, "bio " + i);
            Assert.Equal(MindM.BioHistoryKeep, 10); Assert.Equal(10, m.BioHistory.Count);
            Assert.Equal(Enumerable.Range(5, 10), m.BioHistory.Select(d => d.Rev));
            Assert.True(m.UndoBio()); Assert.Equal("bio 12", m.EvolvingBio);
        }

        // ---- RunReflection (the call the Director's idle window makes) ----
        static MindM Ana()
        {
            var m = new MindM(1, "Ana");
            m.Fixed = new FixedBio { Text = "Ana is a cook." };
            return m;
        }
        static readonly Dictionary<int, string> NAMES = new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" } };
        static readonly Dictionary<int, ISet<string>> TOKS = new Dictionary<int, ISet<string>>
            { { 1, new HashSet<string> { "Ana" } }, { 2, new HashSet<string> { "Bram" } } };

        [Fact]
        public async Task ReflectionIsOneCallWithTheSchemaAndAGoodBioIsKept()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { Result() };
            var res = await Reflection.RunReflectionAsync(llm, AD, Ana(), NAMES, 1, 9000, DAY, nameTokens: TOKS, concerns: true);
            Assert.NotNull(res); Assert.StartsWith("I am harder", res.Bio);
            var c = Assert.Single(llm.Calls);
            Assert.Equal("reflect", c.Kind);
            Assert.True(JToken.DeepEquals(ConcernOps.ReflectSchema(Reflection.SchemaFor(false), true), c.JsonSchema));
        }

        [Fact]
        public async Task UngroundedBioIsRejected()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { Result(bio: "I have grown close to Zorbax Quillfeather.") };
            Assert.Null(await Reflection.RunReflectionAsync(llm, AD, Ana(), NAMES, 1, 9000, DAY, nameTokens: TOKS));
        }

        [Fact]
        public async Task FailureAndJunkReturnNull()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { "not json" };
            Assert.Null(await Reflection.RunReflectionAsync(llm, AD, Ana(), NAMES, 1, 9000, DAY, nameTokens: TOKS));
            llm.Fail = true;
            Assert.Null(await Reflection.RunReflectionAsync(llm, AD, Ana(), NAMES, 1, 9000, DAY, nameTokens: TOKS));
        }

        [Fact]
        public async Task WithoutConcernsTheStrayConcernFieldsAreDropped()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { "{\"bio\": \"I am harder than I was, and tired of the cold.\", \"wants\": [], \"relations\": [], \"merge\": [], \"concern\": \"I fear the winter.\", \"resolved\": [\"c1\"]}" };
            var res = await Reflection.RunReflectionAsync(llm, AD, Ana(), NAMES, 1, 9000, DAY, nameTokens: TOKS);
            Assert.Equal("", res.Concern); Assert.Empty(res.Resolved);
        }
    }
}

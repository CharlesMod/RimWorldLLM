// Port of the test_concerns.py tests that need no Director/world: cap displacement, schema/prompt with the switch off,
// romantic worry about a minor dropped, blob round trip + trim.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class ConcernsPureTests
    {
        const long DAY = 60000;
        static readonly int[] None = new int[0];

        [Fact]
        public void CapDisplacesTheWeakestOnlyForAStrongerOne()
        {
            var m = new MindM(1);
            var a = ConcernOps.AddConcern(m, "First worry here.", None, "x", "", 0.3, 10, 2);
            var b = ConcernOps.AddConcern(m, "Second worry here.", None, "x", "", 0.6, 11, 2);
            Assert.Null(ConcernOps.AddConcern(m, "Third worry weaker.", None, "x", "", 0.2, 12, 2));
            var c = ConcernOps.AddConcern(m, "Fourth worry stronger.", None, "x", "", 0.9, 13, 2);
            Assert.NotNull(c); Assert.Equal("faded", a.Status); Assert.Equal("open", b.Status); Assert.Equal(2, ConcernOps.OpenConcerns(m).Count);
            Assert.Null(ConcernOps.AddConcern(m, "Fourth worry stronger.", None, "x", "", 0.9, 14, 2));
        }

        [Fact]
        public void ReflectionWithoutConcernsKeepsTodaysSchemaAndPrompt()
        {
            var msgs = Reflection.ReflectionMessages(new RimWorldAdapter(), new MindM(1, "Ana"), new Dictionary<int, string>(), 10, DAY);
            Assert.DoesNotContain("WORRIES", msgs[1].Item2); Assert.DoesNotContain(ConcernOps.ReflectConcernRequest, msgs[1].Item2);
            var baseSchema = Reflection.ReflectSchema();
            Assert.Same(baseSchema, ConcernOps.ReflectSchema(baseSchema, false));
        }

        [Fact]
        public void ARomanticWorryAboutAMinorFromReflectionIsDropped()
        {
            var ctx = new ConcernCtx
            {
                Known = new HashSet<string> { "bram" }, All = new HashSet<string> { "bram" }, PromptWords = new HashSet<string>(),
                CommonWords = new HashSet<string>(), Speaker = new HashSet<string> { "ana" }, Minor = true, MinorNames = new HashSet<string> { "bram" },
            };
            Assert.Equal("", ConcernOps.SanitizeConcern("I keep thinking about a kiss with Bram.", ctx));
            Assert.NotEqual("", ConcernOps.SanitizeConcern("I keep fearing the winter will break us.", ctx));
        }

        [Fact]
        public void BlobRoundTripOldBlobAndTrim()
        {
            var m = new MindM(1);
            ConcernOps.AddConcern(m, "I worry about Bram.", new[] { 2 }, "hurt", "healed:2", 0.6, 1, 2);
            var again = MindM.FromBlob(m.ToBlob(10, (int)DAY), 1);
            Assert.Equal("I worry about Bram.", again.Concerns[0].Text); Assert.Equal(1, again.ConcernCounter);
            var old = m.ToBlob(10, (int)DAY);
            foreach (var k in new[] { "concerns", "concern_counter", "grudges" }) old.Remove(k);
            var o = MindM.FromBlob(old, 1);
            Assert.Empty(o.Concerns); Assert.Empty(o.Grudges);
            for (int i = 0; i < 40; i++)
                m.Concerns.Add(new Concern { Id = "x" + i, Status = "faded", Text = new string('z', 500), Kind = "hurt" });
            Assert.True(MindM.FromBlob(m.ToBlob(10, (int)DAY, limit: 2000), 1).Concerns.Count <= 2);
        }
    }
}

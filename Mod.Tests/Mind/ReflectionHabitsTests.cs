// The five test_habits.py tests HabitsTests deferred for the reflection module. test_end_to_end_reflection_changes_a_tic_then_waits is split:
// the single call + tic change are tested here against RunReflectionAsync/ApplyReflection; the Director's "not asked inside the interval" step
// is the Director's (deferred, docs/csmind/parity-inner.md).
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class ReflectionHabitsTests
    {
        const long DAY = 60000;
        static readonly RimWorldAdapter AD = new RimWorldAdapter();

        static MindM Mind_()
        {
            var m = new MindM(1, "Ana");
            m.Voice = new VoiceCard { RegisterLine = "dry", Tics = new List<string> { "Short sentences", "Dry jokes", "Sighs first" } };
            return m;
        }
        static string Reply(string habit = "mutters about the cold")
        {
            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["bio"] = "The recent days have left a mark on me, and I am changing slowly.", ["wants"] = new[] { "Get warm" },
                ["relations"] = new object[0], ["merge"] = new object[0], ["habit"] = habit,
            });
        }
        static readonly Dictionary<int, string> NAMES = new Dictionary<int, string> { { 1, "Ana" } };
        static readonly Dictionary<int, ISet<string>> TOKS = new Dictionary<int, ISet<string>> { { 1, new HashSet<string> { "Ana" } } };

        [Fact]
        public void SchemaAndPromptOnlyWhenHabitsAsked()
        {
            Assert.False(Reflection.SchemaFor(false)["properties"]["habit"] != null);
            var s = Reflection.SchemaFor(true);
            Assert.NotNull(s["properties"]["habit"]); Assert.Equal("habit", (string)s["required"].Last());
            Assert.Null(Reflection.ReflectSchema()["properties"]["habit"]);
            var m = Mind_();
            string off = Reflection.ReflectionMessages(AD, m, NAMES, DAY, DAY)[1].Item2;
            string on = Reflection.ReflectionMessages(AD, m, NAMES, DAY, DAY, habits: true)[1].Item2;
            Assert.DoesNotContain("habit", off.ToLowerInvariant()); Assert.Contains("YOUR VERBAL HABITS NOW", on); Assert.Contains("Dry jokes", on);
            double est = (Sanitize.PySplit(on).Count() - Sanitize.PySplit(off).Count()) * 1.3;
            Assert.InRange(est, 20, 110);
            Assert.Equal("mutters about the cold", Reflection.ParseResult(Reply()).Habit);
            Assert.Equal("", Reflection.ParseResult("{\"bio\": \"x y z w\", \"wants\": [], \"relations\": [], \"merge\": []}").Habit);
        }

        [Fact]
        public void ApplyReflectionAppliesHabitOnlyWhenEnabled()
        {
            var res = new ReflectResult { Bio = "I am quieter now than before.", Wants = new List<string> { "a" }, Habit = "mutters about the cold" };
            var m = Mind_();
            Assert.Equal("", Reflection.ApplyReflection(m, res, 5 * DAY, new Dictionary<string, int>()).Habit ?? "");   // habits off: a stray field is ignored
            Assert.Equal("Short sentences", m.Voice.Tics[0]);
            var o = Reflection.ApplyReflection(m, res, 5 * DAY, new Dictionary<string, int>(), habitEveryDays: 3.0, day: DAY);
            Assert.Equal("mutters about the cold", o.Habit); Assert.Equal("mutters about the cold", m.Voice.Tics.Last());
        }

        [Fact]
        public async Task ReflectionCallAsksForAHabitAndChangesATic()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { Reply() };
            var m = Mind_();
            var before = m.Voice.Tics.ToList();
            var res = await Reflection.RunReflectionAsync(llm, AD, m, NAMES, 1, DAY, DAY, nameTokens: TOKS, habits: true);
            Reflection.ApplyReflection(m, res, DAY, new Dictionary<string, int>(), habitEveryDays: 3.0, day: DAY);
            Assert.Equal("mutters about the cold", m.Voice.Tics.Last()); Assert.NotEqual(before, m.Voice.Tics);
            Assert.NotNull(llm.Calls.Single().JsonSchema["properties"]["habit"]);
        }

        [Fact]
        public async Task OffSwitchIsTodaysCallAndABadHabitIsBlanked()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { Reply("mutters about the cold") };
            var m = Mind_();
            var res = await Reflection.RunReflectionAsync(llm, AD, m, NAMES, 1, DAY, DAY, nameTokens: TOKS, habits: false);
            var c = llm.Calls.Single();
            Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Reflection.ReflectSchema(), c.JsonSchema));
            Assert.DoesNotContain("habit", c.Messages[1].Text.ToLowerInvariant());
            Assert.Equal("", res.Habit); Assert.Empty(m.HabitHistory);
        }

        [Fact]
        public async Task BadHabitsDroppedButTheBioIsKept()
        {
            var llm = new FakeLlm();
            llm.Script["reflect"] = new List<string> { Reply("Zed Quillon taught me to whistle") };
            var m = Mind_();
            var res = await Reflection.RunReflectionAsync(llm, AD, m, NAMES, 1, DAY, DAY, nameTokens: TOKS, habits: true);
            Assert.NotNull(res); Assert.Equal("", res.Habit); Assert.StartsWith("The recent days", res.Bio);
            Reflection.ApplyReflection(m, res, DAY, new Dictionary<string, int>(), habitEveryDays: 3.0, day: DAY);
            Assert.Empty(m.HabitHistory); Assert.StartsWith("The recent days", m.EvolvingBio);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class SpeechThoughtTests
    {
        static readonly RimWorldAdapter A = new RimWorldAdapter();

        [Theory]
        [InlineData("Jessie and I had a little chat earlier.", true)]
        [InlineData("Tobias and Jessie engaged in a deep conversation.", true)]
        [InlineData("We were saying something about nothing much.", true)]
        [InlineData("Bram and I talked.", true)]
        [InlineData("Just some small talk and a lot of sighing.", true)]
        [InlineData("They had a long conversation again today.", true)]
        [InlineData("Bram talked about the raid wall.", false)]
        [InlineData("We had a long conversation about the debt.", false)]
        [InlineData("They had words over the stolen steel.", false)]
        [InlineData("My leg hurts too much to talk.", false)]
        [InlineData("The turnips are coming in nicely.", false)]
        [InlineData("", false)]
        public void TalkAboutTalkIsCaughtAndLegitUsesAreKept(string text, bool meta)
        {
            Assert.Equal(meta, Sanitize.IsTalkAboutTalk(text));
        }

        [Theory]
        [InlineData("mentions what things cost")]
        [InlineData("mentions seeing others talk")]
        [InlineData("keeps bringing up the weather")]
        [InlineData("talks about the silver stash")]
        [InlineData("chats about everything")]
        public void TopicAndMetaHabitsAreRefused(string habit)
        {
            Assert.False(Habits.IsStyleHabit(habit));
            var tokens = new Dictionary<int, ISet<string>> { { 1, new HashSet<string> { "Ana" } } };
            Assert.Equal("", Habits.CleanHabit(habit, "", tokens, 1, null, false, null));
        }

        [Theory]
        [InlineData("trails off after a quarrel")]
        [InlineData("clears their throat before bad news")]
        [InlineData("mutters about the cold")]
        public void StyleHabitsStay(string habit)
        {
            Assert.True(Habits.IsStyleHabit(habit));
        }

        [Fact]
        public void RoutineSocialIsToldFromOutcomesByMemoryAndEvent()
        {
            Assert.True(A.RoutineSocial("social", "I was part of it: Ana and Bram had a passing exchange while going about their day."));
            Assert.True(A.RoutineSocial("social", "I saw it: Ana -> Bram: DeepTalk."));
            Assert.True(A.RoutineSocial("conversation", "I talked with Bram."));
            Assert.False(A.RoutineSocial("social", "It happened to me: Bram -> Ana: insult."));
            Assert.False(A.RoutineSocial("social", "I was part of it: Ana had a deep talk with Bram and was rebuffed."));
            Assert.False(A.RoutineSocial("raid_start", "A raid came and we had a chat about it."));
            var told = new Memory { Etype = "social", Source = "told", Text = "Bram told me a secret in a chat." };
            Assert.False(MemoryOps.IsRoutineSocial(told, A));
            Assert.True(MemoryOps.IsRoutineSocial(new Memory { Etype = "conversation", Source = "saw", Text = "x" }, A));
            Assert.True(A.RoutineSocialEvent(new JObject { ["type"] = "social", ["summary"] = "Ana -> Bram: Chitchat.", ["data"] = new JObject { ["interaction"] = "Chitchat" } }));
            Assert.False(A.RoutineSocialEvent(new JObject { ["type"] = "social", ["summary"] = "Ana -> Bram: insult.", ["data"] = new JObject { ["interaction"] = "Insult" } }));
        }

        [Fact]
        public async System.Threading.Tasks.Task TheChronicleLeavesRoutineSocialOutButKeepsOutcomes()
        {
            using var w = new World(); await w.GoAsync();
            w.D.Cfg.SpeechChecks = true;
            w.Fb.AddEvent("social", new[] { new KeyValuePair<int, string>(1, "initiator"), new KeyValuePair<int, string>(2, "recipient") }, "Ana and Bram had a deep talk.",
                new[] { 1, 2 }, 0.6, null, new JObject { ["interaction"] = "DeepTalk" });
            w.Fb.AddEvent("social", new[] { new KeyValuePair<int, string>(1, "initiator"), new KeyValuePair<int, string>(2, "recipient") }, "Ana insulted Bram.",
                new[] { 1, 2 }, 0.6, null, new JObject { ["interaction"] = "Insult" });
            await w.D.StepAsync(); await w.D.DrainAsync();
            var notes = w.D.Chronicle.State.Pending.Select(n => n.Text).ToList();
            Assert.DoesNotContain(notes, n => n.Contains("deep talk"));
            Assert.Contains(notes, n => n.Contains("insulted"));
        }

        [Fact]
        public void TheRequestsAskForRightNowAndForgetTalkAboutTalk()
        {
            Assert.Contains("or right now", A.ThoughtRequest);
            Assert.Contains("act of talking itself", A.DiaryRequest);
            Assert.Contains("act of talking itself", A.ChronicleRequest);
            Assert.DoesNotContain("mutters about the cold", A.ReflectionHabitRequest);
            Assert.Contains("never a subject", A.ReflectionHabitRequest);
        }
    }
}

// Ports of the world-free parts of agent/tests/test_habits.py.
using System.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class HabitsTests
    {
        const long Day = 60000;

        static MindM MindWith(string[] tics = null, bool edited = false)
        {
            var v = new VoiceCard { RegisterLine = "dry", EditedByPlayer = edited };
            v.Tics = (tics ?? new[] { "Short sentences", "Dry jokes", "Sighs first" }).ToList();
            return new MindM { ActorId = 1, Name = "Ana", Voice = v };
        }

        [Fact]
        public void ReplacesTheOldestTicAndIsUndoable()
        {
            var m = MindWith();
            Assert.True(Habits.ApplyHabit(m, "mutters about the cold", 5 * Day, Day, 3.0));
            Assert.Equal(new[] { "Dry jokes", "Sighs first", "mutters about the cold" }, m.Voice.Tics);
            Assert.True(m.HabitHistory.Last().Removed == "Short sentences" && m.LastHabitGame == 5 * Day);
            // (block_b assertions deferred: Prompt module is a later wave)
            Assert.True(Habits.UndoHabit(m));
            Assert.Equal(new[] { "Short sentences", "Dry jokes", "Sighs first" }, m.Voice.Tics);
            Assert.Equal(-1, m.LastHabitGame);
            Assert.False(Habits.UndoHabit(m));
        }

        [Fact]
        public void AppendsWhileFewerThanThreeTics()
        {
            var m = MindWith(new[] { "Short sentences" });
            Assert.True(Habits.ApplyHabit(m, "hums when tired", Day, Day, 3.0));
            Assert.Equal(new[] { "Short sentences", "hums when tired" }, m.Voice.Tics);
            Assert.Equal("", m.HabitHistory.Last().Removed);
            Assert.True(Habits.UndoHabit(m));
            Assert.Equal(new[] { "Short sentences" }, m.Voice.Tics);
        }

        [Fact]
        public void AtMostOncePerIntervalAndNeverAfterAPlayerEditAndNoRepeats()
        {
            var m = MindWith();
            Assert.True(Habits.ApplyHabit(m, "one", 5 * Day, Day, 3.0));
            Assert.False(Habits.ApplyHabit(m, "two", 6 * Day, Day, 3.0));
            Assert.DoesNotContain("two", m.Voice.Tics);                                   // too soon
            Assert.True(Habits.ApplyHabit(m, "two", 8 * Day, Day, 3.0));
            Assert.False(Habits.ApplyHabit(m, "TWO", 20 * Day, Day, 3.0));                // already a tic
            var p = MindWith(edited: true);
            Assert.False(Habits.HabitAllowed(p, 99 * Day, Day, 3.0));
            Assert.False(Habits.ApplyHabit(p, "x y", 99 * Day, Day, 3.0));
            Assert.Equal(new[] { "Short sentences", "Dry jokes", "Sighs first" }, p.Voice.Tics);
        }

        [Fact]
        public void HistoryIsCappedAndTrimmedInTheBlob()
        {
            var m = MindWith();
            for (int i = 0; i < 10; i++) Habits.ApplyHabit(m, "habit number " + i, (10 * i + 5) * Day, Day, 3.0);
            Assert.True(m.HabitHistory.Count == 6 && m.Voice.Tics.Count == 3);
            var blob = m.ToBlob(100 * Day, (int)Day, limit: 1);          // force every trim step
            Assert.Empty((Newtonsoft.Json.Linq.JArray)blob["habit_history"]);
            Assert.Empty(MindM.FromBlob(blob, 1, "Ana").HabitHistory);
            Assert.Equal(-1, MindM.FromBlob(null, 1, "Ana").LastHabitGame);            // an old blob loads with the defaults
        }

        [Fact]
        public void HabitsLineAndCleanHabit()
        {
            Assert.Equal("Short sentences; Dry jokes; Sighs first", Habits.HabitsLine(MindWith()));
            Assert.Equal("none yet", Habits.HabitsLine(MindWith(new string[0])));
            var tokens = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.ISet<string>> {
                { 1, new System.Collections.Generic.HashSet<string> { "Ana" } } };
            Assert.Equal("", Habits.CleanHabit("  ", "", tokens, 1, null, false, null));
            Assert.Equal("", Habits.CleanHabit("hums", "", tokens, 1, null, false, null));                  // too short (min 2 words)
            Assert.Equal("mutters about the cold", Habits.CleanHabit("\"mutters about the cold.\"", "", tokens, 1, null, false, null));
        }

        // Deferred (need schema_for/reflection_messages/apply_reflection/parse_result of the reflection module, prompt.block_b,
        // RimWorldAdapter, or the Director world fixture): test_schema_and_prompt_only_when_habits_asked,
        // test_apply_reflection_applies_habit_only_when_enabled, test_end_to_end_reflection_changes_a_tic_then_waits,
        // test_off_switch_is_todays_call_and_ungrounded_or_bad_habits_are_blanked, test_bad_habits_dropped.
    }
}

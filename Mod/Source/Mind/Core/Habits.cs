// Port of agent/rimworldllm_agent/core/habits.py: the voice card's tics change rarely, at reflection, from what happened.
using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Mind
{
    public static class Habits
    {
        public const int MaxTics = 3;
        public const int HabitMinWords = 2;
        public const int HabitMaxWords = 10;

        /// <summary>Not on a player-edited card, and not again before everyDays game days have passed.</summary>
        public static bool HabitAllowed(Mind mind, long game, long day, double everyDays)
        {
            if (mind.Voice.EditedByPlayer) return false;
            return mind.LastHabitGame < 0 || game - mind.LastHabitGame >= everyDays * day;
        }

        /// <summary>The current tics for the reflection prompt (game text, cleaned; the caller wraps it with data_section).</summary>
        public static string HabitsLine(Mind mind)
        {
            string s = string.Join("; ", mind.Voice.Tics.Select(t => GameData.Clean(t, "label")));
            return s.Length > 0 ? s : "none yet";
        }

        /// <summary>The habit text, or "" when it is empty, ungrounded (invented names), romantic about a minor, or the wrong size.</summary>
        public static string CleanHabit(string raw, string prompt, IDictionary<int, ISet<string>> nameTokens, int actorId,
            ISet<string> commonWords, bool involvesMinor, ISet<string> minorNames)
        {
            if (string.IsNullOrEmpty(raw) || Sanitize.PyStrip(raw).Length == 0) return "";
            var allNames = new HashSet<string>();
            if (nameTokens != null) foreach (var v in nameTokens.Values) allNames.UnionWith(v);
            ISet<string> speaker;
            if (nameTokens == null || !nameTokens.TryGetValue(actorId, out speaker)) speaker = new HashSet<string>();
            var s = Sanitize.SanitizeProse(Sanitize.PyStrip(raw).Trim('"'), new HashSet<string>(), allNames, Sanitize.PromptCapWords(prompt),
                commonWords, speaker, HabitMinWords, HabitMaxWords, involvesMinor, minorNames);
            if (!s.Ok || s.Text.Length == 0) return "";
            if (!IsStyleHabit(s.Text)) return "";
            return s.Text.TrimEnd('.');
        }

        // A habit is how someone speaks, never a subject they keep returning to or a remark about talking itself.
        private static readonly Regex TopicHabit = new Regex(
            @"^\s*(always |often |keeps? |constantly |likes? to |tends? to )?(mentions?|mentioning|brings? up|bringing up|talks? about|talking about|goes? on about|going on about|comments? on|remarks? on|refers? to|notes?|points? out)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex MetaHabit = new Regex(
            @"\b(chat|chats|chatting|chitchat|conversations?|small talk|others? talk\w*|people talk\w*|(see|sees|seeing|hear|hears|hearing|watch\w*|notic\w*) [^.]{0,20}\b(talk\w*|chat\w*|speak\w*)|what (others|people|everyone) (say|said|are saying))\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>False for a topic habit ("mentions what things cost") and a meta habit ("mentions seeing others talk").</summary>
        public static bool IsStyleHabit(string habit)
        {
            if (TopicHabit.IsMatch(habit) || MetaHabit.IsMatch(habit)) return false;
            return !Salience.IsPriceTalk(habit);
        }

        /// <summary>Replace the oldest tic with habit (append while fewer than three). False when not allowed or a repeat.</summary>
        public static bool ApplyHabit(Mind mind, string habit, long game, long day, double everyDays)
        {
            habit = string.Join(" ", Sanitize.PySplit(habit));
            if (habit.Length == 0 || !HabitAllowed(mind, game, day, everyDays)) return false;
            var tics = mind.Voice.Tics;
            if (tics.Any(t => habit.ToLowerInvariant() == t.ToLowerInvariant())) return false;
            string removed = "";
            if (tics.Count >= MaxTics) { removed = tics[0]; tics.RemoveAt(0); }
            tics.Add(habit);
            mind.LastHabitGame = game;
            mind.HabitHistory.Add(new HabitChange { Game = game, Added = habit, Removed = removed });
            if (mind.HabitHistory.Count > Mind.HabitKeep) mind.HabitHistory = mind.HabitHistory.Skip(mind.HabitHistory.Count - Mind.HabitKeep).ToList();
            return true;
        }

        /// <summary>Undo the newest habit change: the added tic goes, the one it replaced comes back at the front.</summary>
        public static bool UndoHabit(Mind mind)
        {
            if (mind.HabitHistory.Count == 0) return false;
            var ch = mind.HabitHistory[mind.HabitHistory.Count - 1];
            mind.HabitHistory.RemoveAt(mind.HabitHistory.Count - 1);
            var tics = mind.Voice.Tics;
            tics.Remove(ch.Added);
            if (ch.Removed.Length > 0) tics.Insert(0, ch.Removed);
            mind.LastHabitGame = mind.HabitHistory.Count > 0 ? mind.HabitHistory[mind.HabitHistory.Count - 1].Game : -1;
            return true;
        }
    }
}

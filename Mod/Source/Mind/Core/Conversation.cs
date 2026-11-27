// Port of agent/rimworldllm_agent/core/conversation.py (SPEC section 7).
// Two windows, append-only turns, an opener obliges a reply only when it calls for one, and the end is by rule:
// turn cap, decaying continue chance, repetition check, actors apart, either actor's job changing, preemption.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public class Turn
    {
        public int Speaker;
        public string Name;
        public string Text;
        public string Intent = "none";
        public Turn(int speaker, string name, string text, string intent = "none")
        { Speaker = speaker; Name = name; Text = text; Intent = intent; }
    }

    public class Conversation
    {
        // Python \s (str.isspace) is wider than .NET \s: spell it out as Sanitize does.
        private const string Ws = "[\\t-\\r\\u001c-\\u001f \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]";
        private static readonly Regex Oblige = new Regex(
            "\\?" + Ws + "*$|^(?:can|could|will|would|do|did|are|is|why|what|where|who|how)\\b|" +
            "^(?:give|bring|get|come|help|stop|tell|look|move|sit|eat|go|pass)\\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        public static readonly HashSet<string> ObligingIntents = new HashSet<string> { "insult", "confide", "comfort" };

        /// <summary>Question, insult, request or confession calls for an answer.</summary>
        public static bool OpenerObliges(string line, string intent = "none")
        {
            return Oblige.IsMatch(Sanitize.PyStrip(line)) || ObligingIntents.Contains(intent);
        }

        private static HashSet<string> KeywordsOrWords(string s)
        {
            var k = MemoryOps.Keywords(s);
            if (k.Count > 0) return k;
            return new HashSet<string>(Sanitize.PySplit(s.ToLowerInvariant()));
        }

        public static bool Repetitive(string line, IList<string> prior, double threshold = 0.7)
        {
            var a = KeywordsOrWords(line);
            foreach (var p in prior.Skip(Math.Max(0, prior.Count - 3)))
            {
                var b = KeywordsOrWords(p);
                if (a.Count > 0 && b.Count > 0)
                {
                    int inter = a.Count(x => b.Contains(x));
                    int union = a.Count + b.Count - inter;
                    if ((double)inter / union >= threshold) return true;
                }
                if (Sanitize.PyStrip(line).ToLowerInvariant() == Sanitize.PyStrip(p).ToLowerInvariant()) return true;
            }
            return false;
        }

        public string Id;
        public int A;                 // opener
        public int B;
        public Dictionary<int, string> Names;
        public List<Turn> Turns = new List<Turn>();
        public bool Obliged;
        public string Ended = "";
        public int TurnCap = 6;
        public double ContinueP0 = 0.85;
        public double Decay = 0.7;
        public string Reunion = "";   // the grudge round this talk is the make-up or confrontation for (core/grudges.py)

        public Conversation(string id, int a, int b, Dictionary<int, string> names)
        { Id = id; A = a; B = b; Names = names; }

        public int NextSpeaker() { return Turns.Count % 2 == 0 ? A : B; }
        public int ListenerOf(int speaker) { return speaker == A ? B : A; }

        public Turn Add(int speaker, string text, string intent = "none")
        {
            var t = new Turn(speaker, Names[speaker], text, intent);
            Turns.Add(t);
            if (Turns.Count == 1) Obliged = OpenerObliges(text, intent);
            return t;
        }

        /// <summary>Append-only block F: every turn so far, own lines and heard speech alike.</summary>
        public List<(string, string)> TurnsForPrompt() { return Turns.Select(t => (t.Name, t.Text)).ToList(); }

        public double ContinueChance() { return ContinueP0 * Math.Pow(Decay, Math.Max(0, Turns.Count - 1)); }

        public string CheckEnd(PyRandom rng, bool apart = false, bool jobChanged = false, bool preempted = false)
        {
            return CheckEnd((Func<double>)rng.Random, apart, jobChanged, preempted);
        }

        /// <summary>Returns the end reason or "". Evaluated after each turn. The draw happens only when Python draws.</summary>
        public string CheckEnd(Func<double> random, bool apart = false, bool jobChanged = false, bool preempted = false)
        {
            if (preempted) return "preempted";
            if (apart) return "apart";
            if (jobChanged) return "job_changed";
            if (Turns.Count >= TurnCap) return "turn_cap";
            var last = Turns.Count > 0 ? Turns[Turns.Count - 1] : null;
            if (last != null && Repetitive(last.Text, Turns.Take(Turns.Count - 1).Select(t => t.Text).ToList())) return "repetition";
            if (Turns.Count == 1 && Obliged) return "";     // opener obliges a reply: the second turn happens
            if (random() > ContinueChance()) return "chance";
            return "";
        }

        public static string TemplateMemory(Conversation conv, int me)
        {
            string other = conv.Names[me == conv.A ? conv.B : conv.A];
            string last = conv.Turns.Count > 0 ? conv.Turns[conv.Turns.Count - 1].Text : "";
            var first = conv.Turns[0];
            string who = first.Speaker == me ? "I" : other;
            return "I talked with " + other + ". " + who + " started it: \"" + first.Text + "\" It ended on: \"" + last + "\"";
        }

        /// <summary>(feeling, history line) from the intents that went across; deterministic fallback.</summary>
        public static (string, string) TemplateRelation(Conversation conv, int me)
        {
            int otherId = me == conv.A ? conv.B : conv.A;
            var intents = conv.Turns.Where(t => t.Speaker == otherId).Select(t => t.Intent).ToList();
            string feel;
            if (intents.Any(i => i == "insult" || i == "slight")) feel = "Wary after the way they spoke to me.";
            else if (intents.Any(i => i == "kind" || i == "comfort" || i == "confide")) feel = "Warmer toward them after that talk.";
            else feel = "Nothing much changed.";
            return (feel, "Last talked about: " + GameData.CpPrefix(conv.Turns[0].Text, 60));
        }
    }
}

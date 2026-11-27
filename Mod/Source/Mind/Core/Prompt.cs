// Port of agent/rimworldllm_agent/core/prompt.py: prompt blocks A-G in a fixed order (most stable first), after bench/m0/lib.py.
//
// A: world rules, speech rules, output format, label glosses (adapter text; same bytes for every actor)
// B: fixed bio and voice card (never changes for this actor)
// C: evolving bio, wants, relations digest (changes only at reflection)
// D: 2-4 chosen memories and the ledger entries this actor knows (once per lease)
// E: scene (once per lease)
// F: turns so far (append-only)            G: voice reminder (one line) and the request (every turn)
//
// A-E form the prefix: built once at lease start and then frozen as a string, so it is byte-identical for the whole
// lease. Nothing in A-E may carry units, timestamps or per-turn data.
//
// The static class is PromptBlocks (not Prompt) because RimWorldLLM.Mind.ToolLoop already has a Prompt class.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public class Scene
    {
        public string Where = "somewhere nearby";
        public string When = "daytime";                    // time of day and weather, never a game count
        public List<string> Present = new List<string>();  // "Name (job)"
        public string State = "";
        public string Job = "idle";
        public List<string> Thoughts = new List<string>(); // "text (+/-n)"
        public string Trigger = "";
        public bool MinorSelf;                             // the speaker is under 18 (or of unknown age)
        public string SpeechNow = "";                      // "Right now: drunk..." from the current health/needs; empty when none applies
        public string SpeechShort = "";                    // the effective register phrase while a temporary state is active (for block G)
    }

    public static class PromptBlocks
    {
        public const int BioLineCap = 700;      // per line of the fixed bio (a full character card: backstory, traits, ideoligion, history)
        public const int EvolvingCap = 700;     // the evolving bio: reflection allows 90 words, about 600 characters

        /// <summary>How a line is said; decided in the intent pass (POST /say kind).</summary>
        public static readonly string[] Deliveries = { "normal", "shout", "whisper" };

        public const string AimRule = "aim ::= [a-zA-Z0-9 ,.'!?-]{8,100}";     // the inner aim: a plain first-person phrase
        public const string AimOnlyGrammar = "root ::= aim\n" + AimRule;
        public const string AimAsk = "what you privately want or fear beneath the line, about 12 words, first person, never said aloud";

        /// <summary>GBNF restricting the intent pass to the adapter's label set, then (optionally) the delivery, then (optionally,
        /// with a delivery) a free first-person aim.</summary>
        public static string GrammarFor(IEnumerable<string> labels, IEnumerable<string> deliveries = null, bool aim = false)
        {
            string labs = string.Join(" | ", labels.Select(i => "\"" + i + "\""));
            string g = "root ::= " + labs;
            var dl = deliveries == null ? new List<string>() : deliveries.ToList();
            if (dl.Count > 0)
            {
                g = "root ::= intent \" \" delivery" + (aim ? " \" \" aim" : "") +
                    "\nintent ::= " + labs +
                    "\ndelivery ::= " + string.Join(" | ", dl.Select(i => "\"" + i + "\"")) + (aim ? "\n" + AimRule : "");
            }
            return g;
        }

        /// <summary>A JSON value's str() when truthy, else null (the Python `x if x else None` reads).</summary>
        private static string Words(Mind mind)
        {
            JToken w = mind.Fixed.Extras == null ? null : mind.Fixed.Extras["words"];
            return PyJson.Truthy(w) ? PyJson.Str(w) : null;
        }

        public static string BlockB(Mind mind)
        {
            var v = mind.Voice;
            string s = "=== WHO YOU ARE ===\n" + GameData.DataSection("bio", GameData.Clean(mind.Fixed.Text, "line", BioLineCap, true));
            string words = Words(mind);
            string wordsLine = (words != null && !v.EditedByPlayer)
                ? "Words: " + GameData.Clean(words, "desc") + " (your style above still applies).\n" : "";   // a player-edited card: the player's wording stands
            s += "\n=== HOW YOU TALK ===\n" +
                 "Register: " + GameData.Clean(v.RegisterLine, "desc") + "\n" + wordsLine +
                 "Habits: " + string.Join("; ", v.Tics.Select(t => GameData.Clean(t, "label"))) + " (use a habit in at most one line in three)\n" +
                 "Example lines:\n" + string.Join("\n", v.Examples.Select(e => "- " + GameData.Clean(e, "line"))) + "\n";
            return s;
        }

        /// <summary>Changes only when a reflection runs: bio, wants and the relations digest are all written there.</summary>
        public static string BlockC(Mind mind, IDictionary<int, string> names = null, int maxRel = 6, int maxRep = 3, IList<string> worries = null)
        {
            var lines = new List<string>();
            int shown = 0;
            foreach (var r in mind.CRelations.Take(maxRel))
            {
                string op = r.Opinion.HasValue ? ", opinion " + (r.Opinion.Value >= 0 ? "+" : "-") + Math.Abs(r.Opinion.Value).ToString(CultureInfo.InvariantCulture) : "";
                string rep = (!string.IsNullOrEmpty(r.Reputation) && shown < maxRep)
                    ? " (people say: " + GameData.Clean(r.Reputation, "line", 90) + ")" : "";
                shown += rep.Length > 0 ? 1 : 0;
                lines.Add("- " + GameData.Name(r.Name) + op + ": " + GameData.Clean(r.Feeling, "line") +
                          (!string.IsNullOrEmpty(r.History) ? " " + GameData.Clean(r.History, "line") : "") + rep);
            }
            string body = "Becoming: " + GameData.Clean(string.IsNullOrEmpty(mind.EvolvingBio) ? "(nothing yet)" : mind.EvolvingBio, "desc", EvolvingCap) + "\n";
            body += "Wants: " + (mind.Wants.Count > 0 ? string.Join("; ", mind.Wants.Select(w => GameData.Clean(w, "label"))) : "none in particular") + "\n";
            if (!string.IsNullOrEmpty(mind.Notes))
                body += "Director's notes (written by the player): " + GameData.Clean(mind.Notes, "note") + "\n";
            if (worries != null && worries.Count > 0)     // ongoing concerns: block C changes only when one is born or closes
                body += "Worries: " + string.Join("; ", worries.Select(w => GameData.Clean(w, "line"))) + "\n";
            body += "Relations:\n" + (lines.Count > 0 ? string.Join("\n", lines) : "- none yet") + "\n";
            return "=== WHO YOU ARE BECOMING ===\n" + GameData.DataSection("character", body);
        }

        private static string MemoryLine(Memory m)
        {
            string tag;
            switch (m.Source)
            {
                case "heard": tag = " (heard)"; break;
                case "told": tag = " (told by " + GameData.Name(m.ToldBy) + ", not sure)"; break;
                default: tag = ""; break;
            }
            if (m.Source == "told" && m.Sources > 1)
                tag = " (heard from several people, led by " + GameData.Name(m.ToldBy) + ", not sure)";
            if (m.Visibility == "private") tag += " [private]";
            else if (m.Visibility == "told" && m.ToldTo.Count > 0)
                tag += " [private, told to " + string.Join(", ", m.ToldTo.Select(x => GameData.Name(x))) + "]";
            return "- " + GameData.Clean(m.Text, "event") + tag;
        }

        public static string BlockD(IEnumerable<Memory> memories, IEnumerable<string> ledgerLines, IList<string> thoughts = null, IList<string> talk = null)
        {
            string ms = string.Join("\n", memories.Select(MemoryLine));
            if (ms.Length == 0) ms = "- nothing in particular";
            string led = string.Join("\n", ledgerLines.Select(l => "- " + GameData.Clean(l, "event")));
            if (led.Length == 0) led = "- nothing ongoing that you know of";
            string outp = "=== WHAT YOU REMEMBER ===\n" + GameData.DataSection("memories", ms) +
                          "=== WHAT IS GOING ON THAT YOU KNOW OF ===\n" + GameData.DataSection("ongoing", led);
            if (thoughts != null && thoughts.Count > 0)       // the actor's own recent inner thoughts (fixed per lease, so cache-safe)
                outp += "=== YOUR RECENT THOUGHTS ===\n" + GameData.DataSection(
                    "thoughts", string.Join("\n", thoughts.Select(t => "- " + GameData.Clean(t, "line"))));
            if (talk != null && talk.Count > 0)               // shared lore and the pass-on hint: per lease, so cache-safe
                outp += "=== COMMON TALK ===\n" + GameData.DataSection("talk", string.Join("\n", talk.Select(c => GameData.Clean(c, "line", 160))));
            return outp;
        }

        public static string BlockE(Scene sc)
        {
            string C(string x, string kind) { return GameData.Clean(x, kind); }
            string th = string.Join("\n", sc.Thoughts.Select(t => "- " + C(t, "label")));
            if (th.Length == 0) th = "- nothing much";
            string present = string.Join(", ", sc.Present.Select(p => C(p, "label")));
            if (present.Length == 0) present = "nobody else";
            string body = "Where: " + C(sc.Where, "label") + "\nWhen: " + C(sc.When, "label") + "\n" +
                          "Present: " + present + "\n"
                          + (sc.MinorSelf ? "You are a minor (under 18).\n" : "")
                          + (!string.IsNullOrEmpty(sc.SpeechNow) ? GameData.Clean(sc.SpeechNow, "line", 500) + "\n" : "") +
                          "Your state: " + C(sc.State, "line") + "\nCurrent job: " + C(sc.Job, "label") + "\nYour current thoughts:\n" + th + "\n" +
                          "What just happened: " + C(sc.Trigger, "event") + "\n";
            return "=== THE SCENE ===\n" + GameData.DataSection("scene", body);
        }

        /// <summary>A-E as one string. Freeze it at lease start; never rebuild mid-lease.</summary>
        public static string BuildPrefix(string blockA, Mind mind, IEnumerable<Memory> memories, IEnumerable<string> ledgerLines, Scene sc,
                                         IList<string> thoughts = null, IList<string> talk = null, IList<string> worries = null)
        {
            if (!blockA.Contains(GameData.DataRule)) blockA = blockA.TrimEnd('\n') + "\n\n" + GameData.DataRule;
            if (!blockA.Contains(AgeGate.MinorRule)) blockA = blockA.TrimEnd('\n') + "\n\n" + AgeGate.MinorRule;
            if (Adult.Off() && !blockA.Contains(Adult.Rule)) blockA = blockA.TrimEnd('\n') + "\n\n" + Adult.Rule;
            return string.Join("\n", new[] { blockA, BlockB(mind), BlockC(mind, worries: worries), BlockD(memories, ledgerLines, thoughts, talk), BlockE(sc) });
        }

        public static string BlockF(IList<(string, string)> turns)
        {
            if (turns == null || turns.Count == 0) return "=== TURNS SO FAR ===\n(none yet)\n";
            return "=== TURNS SO FAR ===\n" + string.Join("\n", turns.Select(t => GameData.Name(t.Item1) + ": " + t.Item2)) + "\n";
        }

        /// <summary>One-line voice reminder plus the request. Re-sent every turn, so it stays short.
        /// Enforces the tic rule: habits allowed only on every third line.</summary>
        public static string BlockG(Mind mind, string name, int turnNo = 0, string request = null, string aim = null, string speech = null)
        {
            var v = mind.Voice;
            string ex = v.Examples.Count > 0 ? " Sound like: \"" + v.Examples[0] + "\"" : "";
            string habit = PyFmt.Mod(turnNo, 3) == 0 ? "Habits (this line): " + string.Join("; ", v.Tics) + "." : "No habits this line.";
            string s = "=== VOICE REMINDER ===\nYou are " + name + ". " + v.RegisterLine + " " + habit + ex + "\n";
            if (!string.IsNullOrEmpty(speech))    // while a temporary state (drunk, dazed...) is active
                s += "Your words right now: " + GameData.Clean(speech, "desc") + ".\n";
            else                                   // the lasting register, re-said each turn: small models follow a per-turn reminder far better
            {
                string words = Words(mind);
                if (words != null && !v.EditedByPlayer) s += "Your words: " + GameData.Clean(words, "desc") + ".\n";
            }
            if (!string.IsNullOrEmpty(aim))       // a fresh private aim from the inner stream (per turn, so it lives here and not in A-E)
                s += "Your private aim (never say it outright): " + GameData.Clean(aim, "line") + "\n";
            return s + (!string.IsNullOrEmpty(request) ? request : "=== REQUEST ===\nWrite " + name + "'s next line of speech now. One line only.");
        }

        public static List<(string, string)> LineMessages(string prefix, IList<(string, string)> turns, Mind mind, string name,
                                                          int turnNo = 0, string request = null, string aim = null, string speech = null)
        {
            return new List<(string, string)> { ("system", prefix), ("user", BlockF(turns) + "\n" + BlockG(mind, name, turnNo, request, aim, speech)) };
        }

        private static string Extra(string aimExtra)
        {
            return !string.IsNullOrEmpty(aimExtra) && aimExtra.Trim().Length > 0 ? " " + PyStr.Strip(aimExtra) : "";
        }

        /// <summary>The aim-only call (a line with no listener). aimExtra (aim_ground) adds the scene's tension.</summary>
        public static string AimRequest(string aimExtra = null)
        {
            return "=== INTENT ===\nAbout the line you just spoke: " + AimAsk + "." + Extra(aimExtra) + " Those words only.";
        }

        public static string IntentRequest(string listenerName, IList<string> deliveries = null, bool aim = false, string aimExtra = null)
        {
            string to = !string.IsNullOrEmpty(listenerName) ? " to " + listenerName : "";
            bool dl = deliveries != null && deliveries.Count > 0;
            if (dl && aim)
                return "=== INTENT ===\nLabel the line you just spoke" + to + " with one social label from the list, then how you " +
                       "said it (" + string.Join(", ", deliveries) + "), then " + AimAsk + ". Say shout only if you really raised your voice; " +
                       "whisper only if you kept it low. Most lines are normal." + Extra(aimExtra) + " Label, delivery and aim only.";
            if (dl)
                return "=== INTENT ===\nLabel the line you just spoke" + to + " with one social label from the list, then how you " +
                       "said it (" + string.Join(", ", deliveries) + "). Say shout only if you really raised your voice; whisper only if " +
                       "you kept it low. Most lines are normal. Label and delivery only.";
            return "=== INTENT ===\nLabel the line you just spoke" + to + " with one social label from the list. Label only.";
        }

        /// <summary>Same system+user prefix as the line pass (cache hit), then the line, then the label request.</summary>
        public static List<(string, string)> IntentMessages(IList<(string, string)> lineMsgs, string line, string listenerName,
                                                           IList<string> deliveries = null, bool aim = false, string aimExtra = null)
        {
            var r = new List<(string, string)>(lineMsgs);
            r.Add(("assistant", line));
            r.Add(("user", IntentRequest(listenerName, deliveries, aim, aimExtra)));
            return r;
        }

        public static List<(string, string)> AimMessages(IList<(string, string)> lineMsgs, string line, string aimExtra = null)
        {
            var r = new List<(string, string)>(lineMsgs);
            r.Add(("assistant", line));
            r.Add(("user", AimRequest(aimExtra)));
            return r;
        }
    }
}

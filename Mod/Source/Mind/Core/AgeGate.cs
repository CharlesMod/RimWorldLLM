// Port of agent/rimworldllm_agent/core/agegate.py.
// Age gate: nothing romantic or sexual is ever produced for or about an actor under 18 (biological age).
// Enforced in code at four points: prompt building, intent labels, the line sanitiser, the tool loop.
// An age that is missing or unreadable counts as a minor (fail closed).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public static class AgeGate
    {
        public const int MinorAge = 18;

        public const string MinorRule =
            "MINORS\n" +
            "- An actor marked as a minor (under 18) is a child or teenager. Nothing romantic, flirtatious or sexual ever " +
            "involves them: not in what they say, not in what is said to them, not in what is said about them. If it comes up " +
            "in the scene, they or the speaker deflect, refuse flatly or change the subject.\n";

        /// <summary>Python float(age): numbers, bools, numeric strings. False when unreadable.</summary>
        private static bool TryFloat(object age, out double a)
        {
            a = 0;
            if (age is JValue jv) age = jv.Type == JTokenType.Null || jv.Type == JTokenType.Undefined ? null : jv.Value;
            if (age == null) return false;
            switch (age)
            {
                case double d: a = d; return true;
                case float f: a = f; return true;
                case bool b: a = b ? 1 : 0; return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                    a = Convert.ToDouble(age, CultureInfo.InvariantCulture); return true;
                case string s:
                    {
                        s = s.Trim();
                        if (s.Length == 0) return false;
                        var t = s.Replace("_", "");
                        if (t.Length != s.Length && Regex.IsMatch(s, @"(^|[^0-9])_|_($|[^0-9])")) return false;
                        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                    }
                default: return false;
            }
        }

        /// <summary>True under 18. Missing, non-numeric or negative ages count as a minor.</summary>
        public static bool IsMinor(object age)
        {
            if (!TryFloat(age, out double a)) return true;
            return double.IsNaN(a) || a < MinorAge; // NaN is not an age
        }

        private static readonly string[] Words =
        {
            // sexual
            @"sex(?:y|ual|ually|ier)?", @"seduc\w*", @"arous\w*", @"horny", @"lust\w*", @"erotic\w*", @"orgasm\w*", @"intercourse",
            @"naked", @"nude\w*", @"strip(?:ping|tease)?", @"breasts?", @"genitals?", @"kinky", @"fetish\w*", @"turn(?:s|ed)? (?:me|you|him|her|them) on",
            @"turned on", @"sleep(?:ing)? (?:with|together)", @"(?:in|to|into) (?:my|your|his|her) bed", @"bed (?:me|you|her|him)",
            @"make love", @"making love", @"hook(?:ing)? up", @"one.night stand", @"bedroom eyes", @"undress\w*",
            // romantic
            @"romanc\w*", @"romantic\w*", @"flirt\w*", @"kiss\w*", @"smooch\w*", @"make out", @"making out", @"snuggl\w*", @"cuddl\w*",
            @"caress\w*", @"(?:hold|holding) (?:you|me|her|him) (?:close|tight)", @"i love you", @"love you", @"in love",
            @"my love", @"lovers?", @"sweetheart", @"darling", @"beloved", @"babe", @"honey bun", @"crush(?: on)?", @"dating", @"date with",
            @"go(?:ing)? on a date", @"ask(?:ing)? (?:you|her|him|them) out", @"marry (?:me|you|him|her)", @"marriage", @"propos(?:e|ed|ing) to (?:me|you|her|him|them)", @"marriage proposal",
            @"fianc\w+", @"betroth\w*", @"engaged to", @"(?:girl|boy)friend", @"be my (?:wife|husband|partner|lover)",
            @"gorgeous", @"attractive", @"handsome", @"you look (?:so |very |really )?(?:hot|sexy|beautiful|stunning|pretty)",
            @"(?:want|need|desire) (?:you|her|him) (?:so much|badly)", @"affair",
        };

        private const RegexOptions Opts = RegexOptions.CultureInvariant;
        private static readonly Regex Romantic_ = new Regex(@"\b(?:" + string.Join("|", Words) + @")\b", Opts | RegexOptions.IgnoreCase);
        private static readonly Regex Squash = new Regex("[​-‏‪-‮⁠-⁤⁦-⁩﻿­]", Opts);
        private static readonly Regex Spaced = new Regex(@"\b(?:[A-Za-z][\s._*-]+){2,}[A-Za-z]\b", Opts);
        private static readonly Regex SpacedJoin = new Regex(@"[\s._*-]+", Opts);
        private static readonly Regex WordTok = new Regex(@"[a-z0-9']+", Opts);

        private static char Leet(char c)
        {
            switch (c)
            {
                case '0': return 'o';
                case '1': return 'i';
                case '3': return 'e';
                case '4': return 'a';
                case '5': return 's';
                case '7': return 't';
                case '@': return 'a';
                case '$': return 's';
                case '!': return 'i';
                default: return c;
            }
        }

        /// <summary>NFKC after removing zero-width and format characters.</summary>
        public static string Norm(string text)
        {
            return Squash.Replace(text, "").Normalize(NormalizationForm.FormKC);
        }

        /// <summary>Romantic or sexual vocabulary anywhere in the text (also seen through letter-spacing and leetspeak).</summary>
        public static bool Romantic(string text)
        {
            string t = Norm(text);
            if (Romantic_.IsMatch(t)) return true;
            var sb = new StringBuilder(t.Length);
            foreach (char c in t) sb.Append(Leet(c));
            if (Romantic_.IsMatch(sb.ToString())) return true;
            string joined = Spaced.Replace(t, m => SpacedJoin.Replace(m.Value, ""));   // "s e x y", "s.e.x.y"
            return joined != t && Romantic_.IsMatch(joined);
        }

        public static bool Mentions(string text, IEnumerable<string> names)
        {
            var low = new HashSet<string>();
            foreach (Match m in WordTok.Matches(Norm(text).ToLowerInvariant().Replace("’", "'"))) low.Add(m.Value);
            foreach (var n0 in names)
            {
                string n = n0.ToLowerInvariant().Trim();
                if (n.Length > 0 && (low.Contains(n) || low.Contains(n.TrimEnd('\'', 's')))) return true;
            }
            return false;
        }

        /// <summary>The line pairs a minor with romantic or sexual vocabulary: the minor speaks, is spoken to, or is named in it.</summary>
        public static bool Violation(string text, bool involvesMinor = false, IEnumerable<string> minorNames = null)
        {
            if (!Romantic(text)) return false;
            return involvesMinor || Mentions(text, minorNames ?? Enumerable.Empty<string>());
        }

        /// <summary>The intent labels offered for this exchange; the first label (the 'nothing' label) always stays.</summary>
        public static List<string> LabelsFor(IList<string> labels, IEnumerable<string> romanticIntents, bool minorInvolved)
        {
            if (!minorInvolved) return new List<string>(labels);
            var bad = new HashSet<string>(romanticIntents);
            var r = new List<string>();
            for (int i = 0; i < labels.Count; i++) if (i == 0 || !bad.Contains(labels[i])) r.Add(labels[i]);
            return r;
        }

        public static bool RefuseIntent(string intent, IEnumerable<string> romanticIntents, bool minorInvolved)
        {
            return minorInvolved && new HashSet<string>(romanticIntents).Contains(intent);
        }

        /// <summary>The tool set a scene may use: romance-type tools are removed whenever a minor is involved.</summary>
        public static HashSet<string> AllowedTools(IEnumerable<string> names, IEnumerable<string> romanticTools, bool minorInvolved)
        {
            var set = new HashSet<string>(names);
            if (minorInvolved) set.ExceptWith(romanticTools);
            return set;
        }
    }

    public class GateStats
    {
        public int RefusedIntents;
        public int DroppedLines;
        public int SkippedEvents;
        public int SkippedPlayerText;
        public List<JObject> Trace = new List<JObject>();

        /// <summary>Appends {"what": what, ...extra}; keeps the last 200.</summary>
        public void Note(string what, JObject extra = null)
        {
            var o = new JObject { ["what"] = what };
            if (extra != null) foreach (var p in extra.Properties()) o[p.Name] = p.Value.DeepClone();
            Trace.Add(o);
            if (Trace.Count > 200) Trace.RemoveRange(0, Trace.Count - 200);
        }

        public JObject ToDict()
        {
            return new JObject
            {
                ["refused_intents"] = RefusedIntents,
                ["dropped_lines"] = DroppedLines,
                ["skipped_events"] = SkippedEvents,
                ["skipped_player_text"] = SkippedPlayerText,
            };
        }
    }
}

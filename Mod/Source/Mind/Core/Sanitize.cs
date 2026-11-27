// Port of agent/rimworldllm_agent/core/sanitize.py.
// Line sanitizer: a failed line is dropped, never shown. Strips empty thought blocks, quotes, stage directions and
// a name prefix. Drops refusals and disclaimers, names of actors that do not exist or are not known to the
// speaker, and over-long lines.
// Parity notes: Python's \s (str patterns) also matches U+001C..U+001F, which .NET's \s does not, so every \s
// below is spelled as the explicit class Ws (identical to str.isspace over the BMP). .NET \w additionally matches
// combining marks (Mn).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Mind
{
    /// <summary>Result of sanitizing one line: Text is null when dropped, Reason says why ("" if kept).</summary>
    public class Sanitized
    {
        public string Text;
        public string Reason;
        public Sanitized(string text, string reason = "") { Text = text; Reason = reason; }
        public bool Ok { get { return Text != null; } }
    }

    public static class Sanitize
    {
        public const int MaxWords = 35;
        public const int SecretRun = 5;     // consecutive words shared with a private memory that count as quoting it

        private const RegexOptions Opts = RegexOptions.CultureInvariant;
        // str.isspace() over the BMP, as a character class body
        private const string WsBody = "\\t-\\r\\u001c-\\u001f \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000";
        private const string Ws = "[" + WsBody + "]";
        private const string NotWs = "[^" + WsBody + "]";

        internal static readonly Regex WsRun = new Regex(Ws + "+", Opts);

        /// <summary>Python str.split(): runs of whitespace, no empty items.</summary>
        internal static string[] PySplit(string s)
        {
            return s.Split(WsChars, StringSplitOptions.RemoveEmptyEntries);
        }
        private static readonly char[] WsChars = "\u0009\u000a\u000b\u000c\u000d\u001c\u001d\u001e\u001f\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000".ToCharArray();
        /// <summary>Python str.strip() / rstrip() with no arguments.</summary>
        internal static string PyStrip(string s) { return s.Trim(WsChars); }
        internal static string PyRStrip(string s) { return s.TrimEnd(WsChars); }

        private static readonly Regex Refusal = new Regex(
            @"\bas an ai\b|\blanguage model\b|\bi(?:'m| am) (?:sorry|unable|not able)\b[^.!?]{0,40}\b(?:help|assist|continue|comply|provide|write|generate)\b" +
            @"|\bi (?:can't|cannot|won't|will not|am unable to) (?:help|assist|comply|continue|fulfil+|write|generate|provide|engage|roleplay|create)\b" +
            @"|\bi apologi[sz]e\b|\bcontent polic(?:y|ies)\b|\bmy guidelines\b|\bsafety guidelines\b" +
            @"|\bnot appropriate (?:for me )?to\b|\bdisclaimer\b|\bfictional (?:scenario|setting)\b|\bin this roleplay\b",
            Opts | RegexOptions.IgnoreCase);
        private static readonly Regex Stage = new Regex(@"\*[^*]*\*|\([^)]*\)|\[[^\]]*\]|<[^>]*>", Opts);
        private static readonly Regex NameToken = new Regex(@"[A-Z][a-z]+(?:['’][a-z]+)?", Opts);
        private static readonly Regex Tail = new Regex(@"['’][a-z]+$", Opts);      // 's  'll  'd  contraction tail of a name token
        private static readonly Regex Bullet = new Regex(@"^(?:[-–—>*•]+" + Ws + @"+|[-–—>]+(?=" + NotWs + "))", Opts);
        // capitalised words that start sentences or are ordinary vocabulary, never treated as names
        private static readonly Regex SubjectVerb = new Regex(@"^(?:'s|" + Ws + @"+(?:is|are|was|were|has|had|will|would|did|does|doesn't|didn't|won't|can't|must|should|could|" +
            @"says|said|went|died|took|saw|knows|needs|wants|looks|feels|goes|comes|gets|got|ran|stays|thinks)\b)", Opts);
        private static readonly HashSet<string> Common = new HashSet<string>
        {
            "i", "i'm", "i'll", "i've", "i'd", "okay", "ok", "yes", "no", "look", "well", "hm", "hmm", "right", "sure",
            "fine", "hey", "listen", "easy", "please", "damn", "oh", "ah", "so", "and", "but", "the", "a", "an", "this",
            "that", "what", "why", "how", "who", "where", "when", "not", "now", "just", "still", "maybe", "never",
            "always", "good", "bad", "stop", "wait", "come", "go", "get", "give", "take", "sit", "eat", "don't", "do",
            "did", "it's", "that's", "there", "here", "we", "you", "they", "he", "she", "my", "your", "our", "his",
            "her", "if", "then", "as", "at", "in", "on", "for", "with", "to", "of", "one", "two", "three", "let's",
            "let", "tell", "see", "hold", "keep", "pass", "move", "nobody", "nothing", "something", "someone",
            "cold", "hot", "dead", "alive", "sorry", "thanks", "thank", "god", "gods", "hell", "mr", "mrs",
            "doctor", "sir", "ma'am", "negative", "affirmative", "ha", "huh", "ugh", "ow", "um", "uh", "yeah", "nope",
            "of", "is", "are", "was", "were", "be", "can", "could", "would", "should", "will", "have", "has", "had",
        };

        /// <summary>'Nate 'Rook' Hollis' -> {'Nate', 'Rook', 'Hollis'}</summary>
        public static HashSet<string> NameTokens(string fullName)
        {
            var r = new HashSet<string>();
            foreach (Match m in Regex.Matches(fullName.Replace("'", " "), @"[A-Za-z][A-Za-z']*", Opts))
                if (GameData.CpLen(m.Value) > 1) r.Add(m.Value);
            return r;
        }

        private static List<string> NormWords(string t)
        {
            var r = new List<string>();
            foreach (Match m in Regex.Matches(t.ToLowerInvariant(), @"[a-z0-9']+", Opts)) r.Add(m.Value);
            return r;
        }

        /// <summary>True if the line quotes a secret verbatim: a run of SecretRun words in a row (or the whole of a
        /// shorter secret). Hinting (different words) passes.</summary>
        public static bool LeaksSecret(string text, IEnumerable<string> secrets)
        {
            var words = NormWords(text);
            foreach (var sec in secrets)
            {
                var sw = NormWords(sec);
                if (sw.Count == 0) continue;
                int n = Math.Min(SecretRun, sw.Count);
                var grams = new HashSet<string>();
                for (int i = 0; i < sw.Count - n + 1; i++) grams.Add(string.Join("\u0001", sw.GetRange(i, n)));
                for (int i = 0; i < words.Count - n + 1; i++)
                    if (grams.Contains(string.Join("\u0001", words.GetRange(i, n)))) return true;
            }
            return false;
        }

        private static readonly Regex TrailingLabel = new Regex(@"^(.*[.!?…])" + Ws + @"+([a-z_]+)" + Ws + @"*[.!?]?$", Opts);

        /// <summary>Cut one bare intent label left after the last sentence ("...before dark. kind" -> "...before dark.").</summary>
        public static string StripTrailingLabel(string t, IEnumerable<string> labels)
        {
            if (labels == null) return t;
            var set = new HashSet<string>(labels);
            if (set.Count == 0) return t;
            var m = TrailingLabel.Match(t);
            return m.Success && set.Contains(m.Groups[2].Value) ? PyRStrip(m.Groups[1].Value) : t;
        }

        private static readonly Regex SentenceRx = new Regex(@"[^.!?…]+[.!?…]+|[^.!?…]+$", Opts);
        private static readonly Regex KeyStrip = new Regex(@"[^a-z0-9 ]", Opts);

        /// <summary>A sentence said twice in one line or paragraph ("We eat now. We eat now.") keeps only its first saying.</summary>
        public static string DropRepeatedSentences(string t)
        {
            var seen = new HashSet<string>();
            var outl = new List<string>();
            foreach (Match m in SentenceRx.Matches(t))
            {
                string sent = m.Value;
                string key = PyStrip(KeyStrip.Replace(sent.ToLowerInvariant(), ""));
                if (key.Length > 0 && seen.Contains(key) && PySplit(key).Length >= 2) continue;
                seen.Add(key);
                outl.Add(PyStrip(sent));
            }
            return outl.Count > 0 ? string.Join(" ", outl) : t;
        }

        private static readonly Regex NamePrefix = new Regex(@"^([A-Z][\w' ]{0,30}):" + Ws + @"+(.*)$", Opts);
        private static readonly Regex LeadDash = new Regex(@"^[-–—>]+" + Ws + "*", Opts);
        private static readonly Regex EndsSentence = new Regex(@"[.!?…]$", Opts);
        private static readonly Regex TwoLetters = new Regex(@"[A-Za-z]{2}", Opts);
        private static readonly char[] QuoteChars = { '"', '“', '”', '‘', '’', '`' };

        /// <summary>
        /// speakerNames: the speaker's own name tokens. knownNames: name tokens of actors the speaker knows.
        /// allNames: name tokens of every actor in the world. promptWords: capitalised words from the speaker's prompt.
        /// truncated: the generation hit the token cap (a cut-off line is trimmed to its last sentence).
        /// intentLabels: a finished sentence followed by one bare label word is the model leaking the intent pass; the word is cut.
        /// involvesMinor / minorNames: the age gate (reason "minor_safety").
        /// narrationGuard: drop a line that begins with the speaker's own name and a verb.
        /// </summary>
        public static Sanitized SanitizeLine(string raw, ISet<string> speakerNames, ISet<string> knownNames, ISet<string> allNames,
            ISet<string> promptWords = null, bool truncated = false, int maxWords = MaxWords, ISet<string> commonWords = null,
            IList<string> secrets = null, bool narrationGuard = false, bool involvesMinor = false, ISet<string> minorNames = null,
            IEnumerable<string> intentLabels = null)
        {
            string t = PyStrip(Think.StripThought(raw));
            if (t.Length == 0) return new Sanitized(null, "empty");
            t = PyStrip(t.Split('\n')[0]);
            if (Refusal.IsMatch(t)) return new Sanitized(null, "refusal");
            t = Bullet.Replace(t, "");
            t = Stage.Replace(t, " ");
            t = PyStrip(WsRun.Replace(t, " "));
            // name prefix "Rook: ..." or "Nate 'Rook' Hollis: ..."
            var m = NamePrefix.Match(t);
            if (m.Success)
            {
                var toks = NameTokens(m.Groups[1].Value);
                if (toks.Overlaps(speakerNames) || toks.Overlaps(allNames)) t = m.Groups[2].Value;
            }
            t = PyStrip(PyStrip(t).Trim(QuoteChars));
            t = LeadDash.Replace(t, "");
            t = t.Replace("\"", "").Replace("“", "").Replace("”", "");
            t = StripTrailingLabel(t, intentLabels);
            t = DropRepeatedSentences(t);
            if (truncated && !EndsSentence.IsMatch(t))
            {
                int cut = Math.Max(Math.Max(t.LastIndexOf(". ", StringComparison.Ordinal), t.LastIndexOf("! ", StringComparison.Ordinal)),
                    t.LastIndexOf("? ", StringComparison.Ordinal));
                if (cut <= 0) return new Sanitized(null, "cut_off");
                t = t.Substring(0, cut + 1);
            }
            if (!TwoLetters.IsMatch(t)) return new Sanitized(null, "empty");
            if (Refusal.IsMatch(t)) return new Sanitized(null, "refusal");
            if (AgeGate.Violation(t, involvesMinor, minorNames ?? new HashSet<string>())) return new Sanitized(null, "minor_safety");
            if (Adult.Off() && Adult.Explicit(t)) return new Sanitized(null, "adult_off");   // the "Adult content" setting is off (Adult)
            if (PySplit(t).Length > maxWords) return new Sanitized(null, "too_long");
            if (narrationGuard && IsSelfNarration(t, speakerNames)) return new Sanitized(null, "self_narration");
            if (secrets != null && secrets.Count > 0 && LeaksSecret(t, secrets)) return new Sanitized(null, "leaks_secret");
            var extra = new HashSet<string>();
            if (commonWords != null) foreach (var w in commonWords) extra.Add(w.ToLowerInvariant());
            string bad = BadName(t, speakerNames, knownNames, allNames, promptWords ?? new HashSet<string>(), extra);
            if (bad != null) return new Sanitized(null, "unknown_name:" + bad);
            return new Sanitized(t);
        }

        private static readonly HashSet<string> NotVerb = new HashSet<string>
        {
            "and", "or", "but", "so", "then", "the", "a", "an", "i", "me", "my", "you", "he", "she", "we", "they", "it",
            "of", "to", "in", "on", "at", "for", "with", "from", "as", "if", "who", "that", "which",
        };
        private static readonly Regex SelfNarr = new Regex(@"^([A-Z][a-z]+)" + Ws + @"+([a-z][a-z']*)", Opts);

        /// <summary>True when the line starts with one of the speaker's own name tokens followed by a verb-like word:
        /// "Tuk feels the bandage." A vocative ("Tuk, ...") or a possessive ("Tuk's bow") is not narration.</summary>
        public static bool IsSelfNarration(string t, ISet<string> speakerNames)
        {
            var m = SelfNarr.Match(t);
            if (!m.Success || !speakerNames.Select(n => n.ToLowerInvariant()).Contains(m.Groups[1].Value.ToLowerInvariant())) return false;
            return !NotVerb.Contains(m.Groups[2].Value);
        }

        /// <summary>Lower-case forms to compare: "Moth's" -> moth's, moth; "Moths'" (cut to 'Moths') -> moths, moth.</summary>
        private static List<string> NameForms(string w)
        {
            string wl = w.ToLowerInvariant();
            string bs = Tail.Replace(wl, "");
            var o = new List<string> { wl, bs };
            if (bs.EndsWith("s", StringComparison.Ordinal) && GameData.CpLen(bs) > 3) o.Add(bs.Substring(0, bs.Length - 1));
            return o;
        }

        private static string BadName(string t, ISet<string> me, ISet<string> known, ISet<string> allNames, ISet<string> promptWords, ISet<string> extraCommon)
        {
            var lowerAll = new HashSet<string>(allNames.Select(n => n.ToLowerInvariant()));
            var lowerOk = new HashSet<string>(known.Union(me).Select(n => n.ToLowerInvariant()));
            var promptL = new HashSet<string>(promptWords.SelectMany(NameForms));
            foreach (Match m in NameToken.Matches(t))
            {
                string w = m.Value;
                var forms = NameForms(w);
                string shown = Tail.Replace(w, "");
                if (forms.Any(lowerOk.Contains)) continue;
                if (forms.Any(lowerAll.Contains)) return shown;            // a real actor the speaker does not know
                if (forms.Any(f => Common.Contains(f) || extraCommon.Contains(f) || promptL.Contains(f))) continue;
                if (forms.Take(2).Any(CommonWords.CommonSentenceStart.Contains) && SentenceStart(t, m.Index)) continue;   // an ordinary word with a capital
                if (SentenceStart(t, m.Index))
                {
                    // only a vocative at the start ("Zed, get back") counts as a name
                    string rest = t.Substring(m.Index + m.Length);
                    if (rest.StartsWith(",", StringComparison.Ordinal) || SubjectVerb.IsMatch(rest) || Tail.IsMatch(w)) return shown;
                    continue;
                }
                return shown;            // capitalised mid-sentence, not in the prompt: an invented name
            }
            return null;
        }

        private static bool SentenceStart(string t, int pos)
        {
            string before = PyRStrip(t.Substring(0, pos));
            return before.Length == 0 || ".!?…".IndexOf(before[before.Length - 1]) >= 0;
        }

        public static HashSet<string> PromptCapWords(string prefix)
        {
            var r = new HashSet<string>();
            foreach (Match m in NameToken.Matches(prefix)) r.Add(Tail.Replace(m.Value, ""));   // "Biscuit's" counts as "Biscuit"
            return r;
        }

        /// <summary>A paragraph (diary entry, chronicle, bio): one block, refusals and invented names dropped.</summary>
        public static Sanitized SanitizeProse(string raw, ISet<string> knownNames, ISet<string> allNames, ISet<string> promptWords,
            ISet<string> commonWords = null, ISet<string> speakerNames = null, int minWords = 6, int maxWords = 140,
            bool involvesMinor = false, ISet<string> minorNames = null)
        {
            string flat = string.Join(" ", PySplit(Think.StripThought(raw)));
            var s = SanitizeLine(flat, speakerNames ?? new HashSet<string>(), knownNames, allNames, promptWords, maxWords: maxWords,
                commonWords: commonWords, involvesMinor: involvesMinor, minorNames: minorNames);
            if (s.Ok && PySplit(s.Text).Length < minWords) return new Sanitized(null, "too_short");
            return s;
        }
    }
}

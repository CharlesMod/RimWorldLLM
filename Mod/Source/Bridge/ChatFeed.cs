using System;
using System.Collections.Generic;
using System.Text;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Verse-free writers for the chat log's non-conversation kinds (see ChatKinds for the data shape of each).
    /// Every method formats and appends to the given ChatLog; none throws on empty input (an empty line is skipped).
    /// </summary>
    public static class ChatFeed
    {
        public const int MaxWhy = 80, MaxDiary = 160, MaxArt = 160, MaxRecap = 480;

        static readonly HashSet<string> Connectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "since", "because", "as", "so", "to", "for", "after", "while" };
        // First words that are safe to lower-case (a name like "Ana" must keep its capital).
        static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "it", "it's", "its", "there", "he", "she", "they", "we", "this", "that", "these", "those",
              "his", "her", "their", "our", "my", "no", "some", "many", "every", "all", "both", "when", "if", "with",
              "without", "colonists", "everyone", "nobody", "someone", "people" };

        public static ChatEntry Say(ChatLog log, int tick, int speakerId, string speaker, string listener, string text)
        {
            return log.Add(ChatKinds.Colonist, text, tick, speakerId, speaker, listener);
        }

        /// <summary>kind: thought, dream or diary (anything else counts as thought). Diary text is shortened to ~160 chars.</summary>
        public static ChatEntry Thought(ChatLog log, int tick, string kind, int pawnId, string pawn, string text)
        {
            string who = ChatKinds.Thought;
            if (kind == ChatKinds.Dream) who = ChatKinds.Dream;
            else if (kind == ChatKinds.Diary) { who = ChatKinds.Diary; text = Shorten(text, MaxDiary); }
            return log.Add(who, text, tick, pawnId, pawn);
        }

        public static ChatEntry Chronicle(ChatLog log, int tick, int day, string text)
        {
            return log.Add(ChatKinds.Chronicle, text, tick, 0, "Day " + day);
        }

        public static ChatEntry Recap(ChatLog log, int tick, IList<string> lines)
        {
            if (lines == null) return null;
            var parts = new List<string>();
            foreach (var l in lines) { string t = Squash(l); if (t.Length > 0) parts.Add(t); }
            return log.Add(ChatKinds.Recap, Shorten(string.Join(" ", parts), MaxRecap), tick);
        }

        /// <summary>`Art: "Title" by Ana: short description`; each part is left out when missing.</summary>
        public static ChatEntry Art(ChatLog log, int tick, string artist, string title, string description)
        {
            artist = Squash(artist); title = Squash(title); description = Squash(description);
            if (title.Length == 0 && description.Length == 0) return null;
            var sb = new StringBuilder("Art");
            if (title.Length > 0) sb.Append(": \"").Append(title).Append('"');
            if (artist.Length > 0) sb.Append(" by ").Append(artist);
            string head = sb.ToString();
            if (description.Length == 0) return log.Add(ChatKinds.Art, Shorten(head, MaxArt), tick, 0, artist);
            const string sep = ": ";
            int room = MaxArt - head.Length - sep.Length;
            if (room < 20) return log.Add(ChatKinds.Art, Shorten(head, MaxArt), tick, 0, artist);
            return log.Add(ChatKinds.Art, head + sep + Shorten(description, room), tick, 0, artist);
        }

        /// <summary>A per-instance text the text desk stored (art, grave, book) as a Story line; other kinds return null
        /// (social and combat lines already show in the game's own logs). subject: the artist, or the dead for a grave.</summary>
        public static ChatEntry Written(ChatLog log, int tick, string kind, string subject, string title, string text)
        {
            switch (kind)
            {
                case TextKinds.Art: return Art(log, tick, subject, title, text);
                case TextKinds.Grave:
                {
                    subject = Squash(subject); text = Squash(text);
                    if (text.Length == 0) return null;
                    string head = subject.Length > 0 ? "Epitaph for " + subject : "Epitaph";
                    return log.Add(ChatKinds.Art, head + ": " + Shorten(text, Math.Max(20, MaxArt - head.Length - 2)), tick, 0, subject);
                }
                case TextKinds.Book:
                {
                    title = Squash(title); text = Squash(text);
                    if (title.Length == 0 && text.Length == 0) return null;
                    string head = title.Length > 0 ? "Book: \"" + title + "\"" : "Book";
                    if (text.Length == 0) return log.Add(ChatKinds.Art, Shorten(head, MaxArt), tick);
                    return log.Add(ChatKinds.Art, head + ": " + Shorten(text, Math.Max(20, MaxArt - head.Length - 2)), tick);
                }
                default: return null;
            }
        }

        /// <summary>what = plain English, past tense ("moved a chair"); why optional. Text = what + ", since why".</summary>
        public static ChatEntry Action(ChatLog log, int tick, string what, string why)
        {
            what = Squash(what);
            if (what.Length == 0) return null;
            return log.Add(ChatKinds.Action, what + WhyClause(why), tick);
        }

        /// <summary>"couldn't {what}: {reason}".</summary>
        public static ChatEntry Refused(ChatLog log, int tick, string what, string reason)
        {
            what = Squash(what); reason = Squash(reason);
            if (what.Length == 0) what = "do that";
            string text = "couldn't " + what + (reason.Length > 0 ? ": " + reason : "");
            return log.Add(ChatKinds.Refused, text, tick);
        }

        /// <summary>The "why" cleaned: whitespace squashed, at most 80 chars (cut at a word), trailing periods removed. May be "".</summary>
        public static string CleanWhy(string why)
        {
            string w = Squash(why);
            if (w.Length > MaxWhy)
            {
                int cut = w.LastIndexOf(' ', MaxWhy);
                w = (cut >= MaxWhy / 2 ? w.Substring(0, cut) : w.Substring(0, MaxWhy)).TrimEnd();
            }
            return w.TrimEnd('.', ' ', '!', ',', ';', ':');
        }

        /// <summary>"" when no why; ", " + why when it starts with a connector; else ", since " + why (first word lowered when plain).</summary>
        public static string WhyClause(string why)
        {
            string w = CleanWhy(why);
            if (w.Length == 0) return "";
            int sp = w.IndexOf(' ');
            string first = (sp < 0 ? w : w.Substring(0, sp)).Trim(',', ':', ';');
            if (Connectors.Contains(first)) return ", " + w;
            if (Plain.Contains(first) && char.IsUpper(w[0])) w = char.ToLowerInvariant(w[0]) + w.Substring(1);
            return ", since " + w;
        }

        static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>At most max chars, cut at a word boundary with an ellipsis when it had to cut.</summary>
        public static string Shorten(string s, int max)
        {
            s = Squash(s);
            if (s.Length <= max) return s;
            int cut = s.LastIndexOf(' ', max - 1);
            if (cut < max / 2) cut = max - 1;
            return s.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.') + "…";
        }
    }
}

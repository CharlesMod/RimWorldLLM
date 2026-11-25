using System;
using System.Collections.Generic;
using System.Text;

namespace HermesVoice.Bridge
{
    /// <summary>One line of a shared moment. PawnId 0 / empty Speaker = the narrator or an unattributed text.</summary>
    public sealed class ShareLine
    {
        public int PawnId;
        public string Speaker = "";
        public string Text = "";
        public bool Thought;      // an inner thought (drawn as a cloud, written "(thinking)")
    }

    /// <summary>What a Share button hands to the share window: a heading and the lines already shown to the player.</summary>
    public sealed class ShareDoc
    {
        public string Colony = "";
        public string Date = "";
        public string Title = "";   // e.g. "Diary of Anna" or "Chronicle"; may be empty
        public List<ShareLine> Lines = new List<ShareLine>();
    }

    /// <summary>Plain-text export, file naming, grouping of an exchange. No game types.</summary>
    public static class ShareFormat
    {
        public const string Footer = "— HermesVoice: local AI colonist dialogue for RimWorld";
        public const string CardFooter = "HermesVoice · local AI colonist dialogue for RimWorld";

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        public static string Header(ShareDoc d)
        {
            string c = Clean(d.Colony), dt = Clean(d.Date);
            if (c.Length > 0 && dt.Length > 0) return c + " — " + dt;
            return c.Length > 0 ? c : dt;
        }

        public static string LineText(ShareLine l)
        {
            string t = Clean(l.Text), who = Clean(l.Speaker);
            if (who.Length == 0) return t;
            return who + (l.Thought ? " (thinking): " : ": ") + t;
        }

        /// <summary>Clipboard text: colony and date, an optional title, speaker-labelled lines, then the footer when on.</summary>
        public static string Text(ShareDoc d, bool footer)
        {
            var sb = new StringBuilder();
            string h = Header(d);
            if (h.Length > 0) sb.Append(h).Append('\n');
            string title = Clean(d.Title);
            if (title.Length > 0) sb.Append(title).Append('\n');
            if (sb.Length > 0) sb.Append('\n');
            foreach (var l in d.Lines) sb.Append(LineText(l)).Append('\n');
            if (footer) sb.Append('\n').Append(Footer).Append('\n');
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>A file-system safe fragment: letters, digits, '-' and '_' kept, runs of anything else become one '-'.</summary>
        public static string SafeName(string s, int max = 40)
        {
            var sb = new StringBuilder();
            bool dash = false;
            foreach (char ch in s ?? "")
            {
                if (char.IsLetterOrDigit(ch) || ch == '_') { sb.Append(ch); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
                if (sb.Length >= max) break;
            }
            string r = sb.ToString().Trim('-');
            return r.Length == 0 ? "share" : r;
        }

        public static string FileName(string colony, string date, int n)
        {
            return SafeName(colony) + "-" + SafeName(date) + "-" + n + ".png";
        }

        /// <summary>The first colony-date-n.png (n from 1) for which exists(name) is false.</summary>
        public static string NextFileName(string colony, string date, Func<string, bool> exists)
        {
            for (int n = 1; n < 100000; n++)
            {
                string f = FileName(colony, date, n);
                if (!exists(f)) return f;
            }
            return FileName(colony, date, 100000);
        }

        /// <summary>Greedy word wrap with a caller-supplied width measure; a word wider than the line is split by characters.</summary>
        public static List<string> Wrap(string text, float maxWidth, Func<string, float> measure)
        {
            var result = new List<string>();
            foreach (string para in Clean(text).Split('\n'))
            {
                if (para.Trim().Length == 0) { result.Add(""); continue; }
                string cur = "";
                foreach (string word in para.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = word;
                    string trial = cur.Length == 0 ? w : cur + " " + w;
                    if (measure(trial) <= maxWidth) { cur = trial; continue; }
                    if (cur.Length > 0) { result.Add(cur); cur = ""; }
                    while (measure(w) > maxWidth && w.Length > 1)
                    {
                        int k = w.Length - 1;
                        while (k > 1 && measure(w.Substring(0, k)) > maxWidth) k--;
                        result.Add(w.Substring(0, k));
                        w = w.Substring(k);
                    }
                    cur = w;
                }
                if (cur.Length > 0) result.Add(cur);
            }
            return result;
        }

        /// <summary>
        /// The run of log entries that make up one exchange around index idx: neighbours (in time order) that involve the same
        /// two pawns, each at most maxGap ticks after the previous one. Arrays are parallel, oldest first.
        /// </summary>
        public static void ExchangeRange(int[] pawn, int[] listener, int[] tick, int idx, int maxGap, out int from, out int to)
        {
            from = to = idx;
            if (idx < 0 || idx >= pawn.Length) return;
            Func<int, int, bool> same = (i, j) =>
                (pawn[i] == pawn[j] && listener[i] == listener[j]) || (pawn[i] == listener[j] && listener[i] == pawn[j]);
            while (from > 0 && same(from - 1, idx) && tick[from] - tick[from - 1] <= maxGap) from--;
            while (to < pawn.Length - 1 && same(to + 1, idx) && tick[to + 1] - tick[to] <= maxGap) to++;
        }
    }

    public sealed class CardBlock
    {
        public ShareLine Line;
        public List<string> Wrapped;
        public float Y, Height;
    }

    public sealed class CardLayout
    {
        public float Width, Height;
        public float HeaderHeight;
        public float FooterY;            // top of the footer line; -1 when the footer is off
        public float TextX, TextWidth;   // where wrapped text starts and how wide it may be
        public int Dropped;              // lines that did not fit under MaxHeight
        public List<CardBlock> Blocks = new List<CardBlock>();
        public List<string> HeaderLines = new List<string>();
    }

    /// <summary>Layout maths for the share card, in pixels, independent of the game's font.</summary>
    public static class ShareCardLayout
    {
        public const float Pad = 20f, Gap = 8f, Portrait = 48f, Inner = 8f, MaxHeight = 1600f;

        public static CardLayout Compute(ShareDoc d, bool footer, float width, float lineH, Func<string, float> measure, float maxHeight = MaxHeight)
        {
            var lay = new CardLayout { Width = width, FooterY = -1f };
            lay.TextX = Pad + Portrait + Gap + Inner;
            lay.TextWidth = Math.Max(40f, width - lay.TextX - Pad - Inner);
            float y = Pad;
            float headW = width - 2 * Pad;
            string h = ShareFormat.Header(d);
            if (h.Length > 0) foreach (var s in ShareFormat.Wrap(h, headW, measure)) lay.HeaderLines.Add(s);
            if (!string.IsNullOrWhiteSpace(d.Title)) foreach (var s in ShareFormat.Wrap(d.Title, headW, measure)) lay.HeaderLines.Add(s);
            y += lay.HeaderLines.Count * lineH;
            if (lay.HeaderLines.Count > 0) y += Gap * 2;
            lay.HeaderHeight = y;

            float reserve = Pad + lineH + Gap + (footer ? lineH + Gap * 2 : 0f);
            for (int i = 0; i < d.Lines.Count; i++)
            {
                var l = d.Lines[i];
                var wrapped = ShareFormat.Wrap(l.Text, lay.TextWidth, measure);
                if (wrapped.Count == 0) wrapped.Add("");
                float nameH = string.IsNullOrWhiteSpace(l.Speaker) ? 0f : lineH;
                float bh = Math.Max(Portrait, 2 * Inner + nameH + wrapped.Count * lineH);
                if (y + bh + reserve > maxHeight && lay.Blocks.Count > 0) { lay.Dropped = d.Lines.Count - i; break; }
                lay.Blocks.Add(new CardBlock { Line = l, Wrapped = wrapped, Y = y, Height = bh });
                y += bh + Gap;
            }
            if (lay.Dropped > 0) y += lineH + Gap;
            if (footer) { lay.FooterY = y + Gap; y = lay.FooterY + lineH; }
            lay.Height = y + Pad;
            return lay;
        }
    }
}

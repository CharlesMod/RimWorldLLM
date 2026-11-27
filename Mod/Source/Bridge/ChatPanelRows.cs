using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>One drawn row of the chat panel.</summary>
    public sealed class PanelRow
    {
        public int Seq;            // chat entry seq; 0 for a live row
        public string Who;         // chat kind, or "narrator"/"colonist" for a live row
        public int To;
        public string Text;
        public float H;
        public bool Live;
        public float Progress = -1f;
        public float Alpha = 1f;
    }

    /// <summary>
    /// Keeps the panel's rows between frames so a frame with nothing new does no work (Verse-free, tested). The text of a chat
    /// entry is formatted once per seq and its wrapped height once per (width, font); the row list is rebuilt only when the log,
    /// the filters, the width or the font changed. Live rows are rebuilt only when the live version or the dots phase changed.
    /// </summary>
    public sealed class ChatPanelRows
    {
        sealed class Cached { public string Text; public float H; public int WidthKey = -1, FontKey = -1; }

        public const int MaxRows = 200;

        readonly Dictionary<int, Cached> _cache = new Dictionary<int, Cached>();
        readonly List<PanelRow> _chat = new List<PanelRow>();
        readonly List<PanelRow> _live = new List<PanelRow>();
        readonly List<PanelRow> _all = new List<PanelRow>();
        readonly List<float> _heights = new List<float>();

        int _lastSeq = -1, _count = -1, _firstSeq = -1, _filters = -1, _width = -1, _font = -1;
        int _liveVersion = -1, _livePhase = -1, _liveFilters = -1, _liveWidth = -1, _liveFont = -1;
        bool _dirty = true;

        public int TextBuilds, HeightBuilds, ChatRebuilds, LiveRebuilds;   // counters (tests, debug)

        /// <summary>The rows to draw, oldest first, live rows last. Valid until the next Sync.</summary>
        public IList<PanelRow> Rows { get { return _all; } }
        /// <summary>Heights matching <see cref="Rows"/>.</summary>
        public IList<float> Heights { get { return _heights; } }
        public int CacheCount { get { return _cache.Count; } }

        /// <summary>
        /// Brings the chat rows up to date. shown filters by kind; format makes a row's text; height wraps it at the current width.
        /// widthKey/fontKey identify the measuring context (width in whole UI units, the font enum). Returns true when the rows changed.
        /// </summary>
        public bool SyncChat(IReadOnlyList<ChatEntry> entries, int lastSeq, int filters, int widthKey, int fontKey,
            Func<string, bool> shown, Func<ChatEntry, string> format, Func<string, float> height)
        {
            int first = entries.Count > 0 ? entries[0].Seq : 0;
            if (lastSeq == _lastSeq && entries.Count == _count && first == _firstSeq && filters == _filters && widthKey == _width && fontKey == _font)
                return false;
            _lastSeq = lastSeq; _count = entries.Count; _firstSeq = first; _filters = filters; _width = widthKey; _font = fontKey;
            ChatRebuilds++;
            _chat.Clear();
            for (int i = entries.Count - 1; i >= 0 && _chat.Count < MaxRows; i--)
            {
                var e = entries[i];
                if (!shown(e.Who)) continue;
                Cached c;
                if (!_cache.TryGetValue(e.Seq, out c)) { _cache[e.Seq] = c = new Cached { Text = format(e) }; TextBuilds++; }
                if (c.WidthKey != widthKey || c.FontKey != fontKey) { c.H = height(c.Text); c.WidthKey = widthKey; c.FontKey = fontKey; HeightBuilds++; }
                _chat.Add(new PanelRow { Seq = e.Seq, Who = e.Who, To = e.To, Text = c.Text, H = c.H });
            }
            _chat.Reverse();
            if (_cache.Count > 2 * ChatLog.Max)
            {
                var drop = new List<int>();
                foreach (var k in _cache.Keys) if (k < first) drop.Add(k);
                foreach (var k in drop) _cache.Remove(k);
            }
            _dirty = true;
            return true;
        }

        /// <summary>
        /// Brings the live rows up to date: rebuilt only when the live version, the dots phase, the filters or the measuring
        /// context changed. make turns a LiveLine into a row (text and colour data); height wraps it.
        /// </summary>
        public bool SyncLive(int liveVersion, int phase, int filters, int widthKey, int fontKey, IList<LiveLine> lines,
            Func<string, bool> shown, Func<LiveLine, PanelRow> make, Func<string, float> height)
        {
            if (!_dirty && liveVersion == _liveVersion && phase == _livePhase && filters == _liveFilters && widthKey == _liveWidth && fontKey == _liveFont) return false;
            
            _liveVersion = liveVersion; _livePhase = phase; _liveFilters = filters; _liveWidth = widthKey; _liveFont = fontKey;
            _live.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (!shown(l.Who == "narrator" ? "narrator" : "colonist")) continue;
                var r = make(l);
                r.Live = true;
                r.H = height(r.Text);
                _live.Add(r);
            }
            LiveRebuilds++;
            Merge();
            _dirty = false;
            return true;
        }

        /// <summary>Call after SyncChat when no live sync follows (or always, cheap): merges when the chat rows changed.</summary>
        public void MergeIfDirty() { if (_dirty) { Merge(); _dirty = false; } }

        void Merge()
        {
            _all.Clear(); _heights.Clear();
            foreach (var r in _chat) { _all.Add(r); _heights.Add(r.H); }
            foreach (var r in _live) { _all.Add(r); _heights.Add(r.H); }
        }

        public void Clear()
        {
            _cache.Clear(); _chat.Clear(); _live.Clear(); _all.Clear(); _heights.Clear();
            _lastSeq = _count = _firstSeq = _filters = _width = _font = -1;
            _liveVersion = _livePhase = _liveFilters = _liveWidth = _liveFont = -1;
            _dirty = true;
        }
    }

    /// <summary>Remembers the last layout result so it is computed only when an input changed.</summary>
    public sealed class ChatLayoutCache
    {
        ChatLayout.Input _in;
        ChatLayout.Result _res;
        bool _have;
        public int Computes;

        public ChatLayout.Result Get(ChatLayout.Input i)
        {
            if (_have && Same(_in, i)) return _res;
            _in = i; _res = ChatLayout.Compute(i); _have = true; Computes++;
            return _res;
        }

        public void Invalidate() { _have = false; }

        static bool Same(ChatLayout.Input a, ChatLayout.Input b)
        {
            return a.ScreenW == b.ScreenW && a.ScreenH == b.ScreenH && a.Width == b.Width && a.Lines == b.Lines && a.LineH == b.LineH
                && a.Right == b.Right && a.Custom == b.Custom && a.FX == b.FX && a.FY == b.FY && a.FW == b.FW && a.FH == b.FH;
        }
    }

    /// <summary>Text of the two director/commentary kinds in the panel.</summary>
    public static class ChatRowFormat
    {
        /// <summary>A camera caption is shown as given ("Camera: a raid arrives"); a bare one gets the prefix.</summary>
        public static string Camera(string text, string prefix)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return text;
            return prefix + " " + text;
        }

        /// <summary>A narrator comment: italic, with the narrator's name unless the line already starts with it.</summary>
        public static string Comment(string text, string prefix)
        {
            text = (text ?? "").Trim();
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = prefix + " " + text;
            return "<i>" + text + "</i>";
        }
    }
}

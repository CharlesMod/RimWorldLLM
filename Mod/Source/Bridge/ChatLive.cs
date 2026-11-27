using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Bridge
{
    /// <summary>One speaker's live status as the chat panel draws it.</summary>
    public sealed class LiveLine
    {
        public string StreamId;
        public string Who;        // "narrator" | "colonist"
        public int To;            // the colonist's thing id for who == "colonist"
        public string Name;       // display name if the caller gave one ("" = resolve from To)
        public bool Typing;       // false = thinking (prefill, before the first token)
        public float Progress;    // prefill progress 0..1 while thinking; < 0 = unknown (plain dots)
        public string Text;       // the partial text as drawn (rate limited); "" while thinking
        public double Started;    // clock seconds the stream began
    }

    /// <summary>
    /// Live chat status: "Narrator is thinking…" while the prompt is processed, then "Narrator is typing…" with the
    /// partial text, replaced by the final chat line. One stream per speaker (narrator, or one colonist); a new stream
    /// for the same speaker replaces the old one. A stream with no update for <see cref="TimeoutSec"/> disappears by
    /// itself, so a crashed client never leaves a status stuck. Not saved with the game.
    /// Thread-safe: the bridge routes call it on the game thread, an in-process mind may call it from any thread.
    /// The final line of a stream ended off the game thread is queued and appended by <see cref="Pump"/>.
    /// </summary>
    public sealed class ChatLive
    {
        public const double TimeoutSec = 90.0;
        /// <summary>The drawn partial text changes at most this often (seconds); the latest text is always kept.</summary>
        public const double DrawIntervalSec = 0.25;
        public const int MaxEnded = 64;
        static readonly Regex IdRx = new Regex("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);

        sealed class Stream
        {
            public string Id, Who, Name;
            public int To;
            public bool Typing;
            public float Progress = -1f;
            public string Text = "", Drawn = "";
            public double Started, Updated, DrawnAt = double.NegativeInfinity;
        }

        sealed class Final { public string Who, Name, Text; public int To; }

        readonly object _lock = new object();
        readonly Func<double> _clock;
        readonly List<Stream> _streams = new List<Stream>();
        readonly Queue<string> _endedOrder = new Queue<string>();
        readonly HashSet<string> _ended = new HashSet<string>();
        readonly List<Final> _finals = new List<Final>();
        int _auto;
        int _version;

        public ChatLive() : this(null) { }

        /// <summary>clock: seconds, monotonic; defaults to a stopwatch.</summary>
        public ChatLive(Func<double> clock)
        {
            if (clock == null) { var sw = Stopwatch.StartNew(); clock = () => sw.Elapsed.TotalSeconds; }
            _clock = clock;
        }

        public static bool ValidId(string id) { return id != null && IdRx.IsMatch(id); }

        /// <summary>Validates a speaker; null when good, else the reason.</summary>
        public static string CheckSpeaker(string who, int to)
        {
            if (who == "narrator") return to == 0 ? null : "to is only for who=\"colonist\"";
            if (who == "colonist") return to > 0 ? null : "who=\"colonist\" needs to (the colonist's id)";
            return "who must be \"narrator\" or \"colonist\"";
        }

        // ------------------------------------------------------------------ in-process API

        /// <summary>Starts a stream in the thinking state and returns its new id. Replaces this speaker's old stream.</summary>
        public string Begin(string who, int to = 0, string name = null)
        {
            string id;
            lock (_lock) id = "live-" + (++_auto);
            string err = Status(id, who, to, name, "thinking");
            if (err != null) throw new ArgumentException(err);
            return id;
        }

        /// <summary>Prefill progress (0..1) for a thinking stream. False if the stream is gone.</summary>
        public bool Progress(string streamId, float progress)
        {
            lock (_lock)
            {
                var s = Find(streamId);
                if (s == null || s.Typing) return false;
                s.Progress = Clamp01(progress);
                s.Updated = _clock();
                _version++;
                return true;
            }
        }

        /// <summary>The first token arrived: thinking becomes typing. False if the stream is gone.</summary>
        public bool Typing(string streamId)
        {
            lock (_lock)
            {
                var s = Find(streamId);
                if (s == null) return false;
                s.Typing = true; s.Progress = -1f;
                s.Updated = _clock();
                _version++;
                return true;
            }
        }

        /// <summary>The whole text so far (not a delta); also marks the stream typing. Idempotent. False if the stream is gone or ended.</summary>
        public bool Partial(string streamId, string text)
        {
            lock (_lock)
            {
                var s = Find(streamId);
                if (s == null) return false;
                s.Typing = true; s.Progress = -1f;
                s.Text = VivJson.Clean(text, ChatLog.MaxText);
                s.Updated = _clock();
                _version++;
                return true;
            }
        }

        /// <summary>
        /// Ends a stream. With finalText the line becomes permanent in the chat log on the next <see cref="Pump"/>
        /// (narrator lines as who "narrator"; colonist lines as who "colonist" with the stream's name); the live line stays
        /// until then so nothing blinks. Without finalText the live line just goes. False if the stream was not live.
        /// </summary>
        public bool End(string streamId, string finalText = null)
        {
            lock (_lock)
            {
                var s = Find(streamId);
                if (s == null) { MarkEnded(streamId); return false; }
                string text = VivJson.Clean(finalText, ChatLog.MaxText);
                MarkEnded(streamId);
                _version++;
                if (text.Length == 0) { _streams.Remove(s); return true; }
                s.Typing = true; s.Progress = -1f; s.Text = text; s.Drawn = text; s.Updated = _clock();
                _finals.Add(new Final { Who = s.Who, To = s.To, Name = s.Name, Text = text });
                s.Id = null;   // detached: no more updates; Pump removes it
                return true;
            }
        }

        /// <summary>Drops a stream without a final line (same as status idle).</summary>
        public bool Cancel(string streamId) { return End(streamId, null); }

        /// <summary>Game thread: appends queued final lines to the log and drops their live lines. Returns how many.</summary>
        public int Pump(ChatLog log, int tick)
        {
            List<Final> finals;
            lock (_lock)
            {
                if (_finals.Count == 0) return 0;
                finals = new List<Final>(_finals);
                _finals.Clear();
                _streams.RemoveAll(x => x.Id == null);
                _version++;
            }
            foreach (var f in finals)
                log.Add(f.Who == "narrator" ? "narrator" : "colonist", f.Text, tick, f.Who == "narrator" ? 0 : f.To, f.Who == "narrator" ? null : f.Name);
            return finals.Count;
        }

        // ------------------------------------------------------------------ bridge route core

        /// <summary>
        /// The /chat/status core. state thinking starts (or restarts) the stream, typing marks the first token, idle ends it
        /// without a line. A thinking or typing for an id that already ended is ignored (returns null, no stream).
        /// progress: 0..1 or NaN for unknown; used with thinking. Returns null when accepted or ignored, else the reason.
        /// </summary>
        public string Status(string streamId, string who, int to, string name, string state, float progress = float.NaN)
        {
            if (!ValidId(streamId)) return "stream_id must be 1..64 chars of A-Z a-z 0-9 _ . : -";
            if (state != "thinking" && state != "typing" && state != "idle") return "state must be \"thinking\", \"typing\" or \"idle\"";
            if (state == "idle") { End(streamId, null); return null; }
            string err = CheckSpeaker(who, to);
            if (err != null) return err;
            lock (_lock)
            {
                if (_ended.Contains(streamId)) return null;
                double now = _clock();
                var s = Find(streamId);
                if (s == null)
                {
                    // a new stream replaces this speaker's live (not yet final) one
                    foreach (var old in _streams)
                        if (old.Id != null && old.Who == who && old.To == to) MarkEnded(old.Id);
                    _streams.RemoveAll(x => x.Id != null && x.Who == who && x.To == to);
                    s = new Stream { Id = streamId, Who = who, To = to, Started = now };
                    _streams.Add(s);
                }
                s.Name = VivJson.Clean(name, 40);
                s.Updated = now;
                _version++;
                if (state == "typing") { s.Typing = true; s.Progress = -1f; }
                else if (!s.Typing) s.Progress = float.IsNaN(progress) ? -1f : Clamp01(progress);
                return null;
            }
        }

        /// <summary>True when the id names a live stream.</summary>
        public bool IsLive(string streamId) { lock (_lock) return Find(streamId) != null; }

        /// <summary>Lines to draw, oldest stream first. Drops timed-out streams; the partial text is refreshed at most every DrawIntervalSec.</summary>
        public List<LiveLine> Snapshot()
        {
            var res = new List<LiveLine>();
            lock (_lock)
            {
                double now = _clock();
                for (int i = _streams.Count - 1; i >= 0; i--)
                    if (_streams[i].Id != null && now - _streams[i].Updated > TimeoutSec) { MarkEnded(_streams[i].Id); _streams.RemoveAt(i); }
                foreach (var s in _streams)
                {
                    if (s.Drawn != s.Text && now - s.DrawnAt >= DrawIntervalSec) { s.Drawn = s.Text; s.DrawnAt = now; }
                    res.Add(new LiveLine { StreamId = s.Id, Who = s.Who, To = s.To, Name = s.Name, Typing = s.Typing, Progress = s.Progress, Text = s.Drawn, Started = s.Started });
                }
            }
            return res;
        }

        /// <summary>
        /// The panel's per-frame read: no allocation unless something changed. Applies the time rules (timed-out streams go, the
        /// drawn text catches up every DrawIntervalSec), and when the stream state differs from <paramref name="seenVersion"/>
        /// refills <paramref name="dest"/> with fresh lines and updates seenVersion. Returns true when dest was refilled.
        /// </summary>
        public bool Refresh(ref int seenVersion, List<LiveLine> dest)
        {
            lock (_lock)
            {
                double now = _clock();
                for (int i = _streams.Count - 1; i >= 0; i--)
                    if (_streams[i].Id != null && now - _streams[i].Updated > TimeoutSec) { MarkEnded(_streams[i].Id); _streams.RemoveAt(i); _version++; }
                foreach (var s in _streams)
                    if (s.Drawn != s.Text && now - s.DrawnAt >= DrawIntervalSec) { s.Drawn = s.Text; s.DrawnAt = now; _version++; }
                if (seenVersion == _version) return false;
                seenVersion = _version;
                dest.Clear();
                foreach (var s in _streams)
                    dest.Add(new LiveLine { StreamId = s.Id, Who = s.Who, To = s.To, Name = s.Name, Typing = s.Typing, Progress = s.Progress, Text = s.Drawn, Started = s.Started });
                return true;
            }
        }

        public int Count { get { lock (_lock) return _streams.Count; } }

        /// <summary>True when ended streams wait for <see cref="Pump"/> to append their final lines.</summary>
        public bool HasFinals { get { lock (_lock) return _finals.Count > 0; } }

        public void Clear()
        {
            lock (_lock) { _streams.Clear(); _finals.Clear(); _ended.Clear(); _endedOrder.Clear(); _version++; }
        }

        // ------------------------------------------------------------------ helpers (call under the lock)
        Stream Find(string id)
        {
            if (id == null) return null;
            foreach (var s in _streams) if (s.Id == id) return s;
            return null;
        }

        void MarkEnded(string id)
        {
            if (id == null || !_ended.Add(id)) return;
            _endedOrder.Enqueue(id);
            while (_endedOrder.Count > MaxEnded) _ended.Remove(_endedOrder.Dequeue());
        }

        static float Clamp01(float p) { return float.IsNaN(p) ? -1f : p < 0f ? 0f : p > 1f ? 1f : p; }
    }
}

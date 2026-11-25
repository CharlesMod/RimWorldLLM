using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Core
{
    /// <summary>Ring buffer of the last N events with increasing sequence numbers.</summary>
    public sealed class EventRing
    {
        readonly int _capacity;
        readonly LinkedList<JObject> _items = new LinkedList<JObject>();
        readonly object _lock = new object();
        long _lastSeq;

        public EventRing(int capacity = Const.EventRingSize) { _capacity = capacity; }

        public long LastSeq { get { lock (_lock) return _lastSeq; } }
        public int Count { get { lock (_lock) return _items.Count; } }

        /// <summary>Stamps the event with the next seq, stores it, returns the seq.</summary>
        public long Add(JObject ev)
        {
            lock (_lock)
            {
                _lastSeq++;
                ev["seq"] = _lastSeq;
                _items.AddLast(ev);
                while (_items.Count > _capacity) _items.RemoveFirst();
                return _lastSeq;
            }
        }

        public sealed class Page
        {
            public List<JObject> Events = new List<JObject>();
            public long NextSeq;
            public bool Gap;
        }

        /// <summary>Events with seq greater than since, oldest first, at most max. Gap when since is older than the oldest kept event.</summary>
        public Page Since(long since, int max = Const.EventsPerCall)
        {
            lock (_lock)
            {
                var page = new Page { NextSeq = since < 0 ? 0 : since };
                if (since < 0) since = 0;
                long oldest = _items.Count > 0 ? (long)_items.First.Value["seq"] : _lastSeq + 1;
                // Events since+1 .. oldest-1 are gone when oldest > since+1.
                page.Gap = (oldest > since + 1 && _lastSeq > since) || since > _lastSeq; // since past the end: the mod restarted
                foreach (var e in _items)
                {
                    long s = (long)e["seq"];
                    if (s <= since) continue;
                    if (page.Events.Count >= max) break;
                    page.Events.Add((JObject)e.DeepClone());
                    page.NextSeq = s;
                }
                if (page.Events.Count == 0) page.NextSeq = since > _lastSeq ? _lastSeq : since;
                return page;
            }
        }

        public void Clear() { lock (_lock) { _items.Clear(); } } // seq keeps counting so an agent cursor never repeats
    }
}

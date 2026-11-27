using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    public struct RequestRecord
    {
        public long WhenUtcTicks;
        public string Method, Path;
        public int Status;
        public double Ms;
        public bool Authenticated;
    }

    /// <summary>The last N bridge requests (for the debug window) and when the agent last made an authenticated one. Thread-safe.</summary>
    public sealed class RequestLog
    {
        readonly int _capacity;
        readonly LinkedList<RequestRecord> _items = new LinkedList<RequestRecord>();
        readonly object _lock = new object();
        long _lastAuthTicks;

        public RequestLog(int capacity = 100) { _capacity = capacity; }

        public void Add(string method, string path, int status, double ms, bool authenticated)
        {
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                _items.AddLast(new RequestRecord { WhenUtcTicks = now, Method = method ?? "", Path = path ?? "", Status = status, Ms = ms, Authenticated = authenticated });
                while (_items.Count > _capacity) _items.RemoveFirst();
                if (authenticated) _lastAuthTicks = now;
            }
        }

        /// <summary>UTC ticks of the last authenticated request; 0 when there has been none.</summary>
        public long LastAuthenticatedTicks { get { lock (_lock) return _lastAuthTicks; } }

        /// <summary>Seconds since the last authenticated request, or -1 when there has been none.</summary>
        public double SecondsSinceAgent(long nowUtcTicks)
        {
            long t = LastAuthenticatedTicks;
            return t == 0 ? -1 : Math.Max(0, (nowUtcTicks - t) / (double)TimeSpan.TicksPerSecond);
        }

        /// <summary>Newest first.</summary>
        public List<RequestRecord> Recent(int max)
        {
            lock (_lock)
            {
                var l = new List<RequestRecord>();
                for (var n = _items.Last; n != null && l.Count < max; n = n.Previous) l.Add(n.Value);
                return l;
            }
        }

        public void Clear() { lock (_lock) { _items.Clear(); _lastAuthTicks = 0; } }
    }
}

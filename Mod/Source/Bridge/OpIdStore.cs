using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    /// <summary>Remembers write results by op_id for a window of real time, so a repeat gets the original result.</summary>
    public sealed class OpIdStore
    {
        readonly Func<double> _now;
        readonly double _ttl;
        readonly Dictionary<string, KeyValuePair<double, JObject>> _map = new Dictionary<string, KeyValuePair<double, JObject>>();
        readonly Queue<KeyValuePair<double, string>> _order = new Queue<KeyValuePair<double, string>>();
        readonly object _lock = new object();

        public OpIdStore(Func<double> nowSeconds, double ttlSeconds = Const.OpIdTtlSeconds)
        {
            _now = nowSeconds;
            _ttl = ttlSeconds;
        }

        public int Count { get { lock (_lock) { Prune(); return _map.Count; } } }

        public bool TryGet(string opId, out JObject original)
        {
            lock (_lock)
            {
                Prune();
                KeyValuePair<double, JObject> v;
                if (_map.TryGetValue(opId, out v)) { original = (JObject)v.Value.DeepClone(); return true; }
                original = null;
                return false;
            }
        }

        public void Put(string opId, JObject result)
        {
            lock (_lock)
            {
                Prune();
                double t = _now();
                _map[opId] = new KeyValuePair<double, JObject>(t, (JObject)result.DeepClone());
                _order.Enqueue(new KeyValuePair<double, string>(t, opId));
            }
        }

        public void Clear() { lock (_lock) { _map.Clear(); _order.Clear(); } }

        void Prune()
        {
            double cutoff = _now() - _ttl;
            while (_order.Count > 0 && _order.Peek().Key <= cutoff)
            {
                var e = _order.Dequeue();
                KeyValuePair<double, JObject> v;
                if (_map.TryGetValue(e.Value, out v) && v.Key <= cutoff) _map.Remove(e.Value);
            }
        }
    }
}

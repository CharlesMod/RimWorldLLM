using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>A string set that forgets its oldest member past a cap (first in, first out). For "already reported" markers
    /// that would otherwise grow for the whole life of a game. Not thread-safe; use on the game thread.</summary>
    public sealed class BoundedSet
    {
        readonly HashSet<string> _set = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> _order = new Queue<string>();
        public int Cap { get; private set; }

        public BoundedSet(int cap) { Cap = cap < 1 ? 1 : cap; }

        public int Count { get { return _set.Count; } }
        public bool Contains(string s) { return _set.Contains(s); }

        /// <summary>True when the member is new (and now stored).</summary>
        public bool Add(string s)
        {
            if (!_set.Add(s)) return false;
            _order.Enqueue(s);
            while (_set.Count > Cap) _set.Remove(_order.Dequeue());
            return true;
        }

        public void Clear() { _set.Clear(); _order.Clear(); }
    }

    /// <summary>A dictionary that forgets its oldest-inserted key past a cap. Re-setting a key keeps its place.</summary>
    public sealed class BoundedMap<TV>
    {
        readonly Dictionary<int, TV> _map = new Dictionary<int, TV>();
        readonly Queue<int> _order = new Queue<int>();
        public int Cap { get; private set; }

        public BoundedMap(int cap) { Cap = cap < 1 ? 1 : cap; }

        public int Count { get { return _map.Count; } }
        public bool TryGetValue(int key, out TV v) { return _map.TryGetValue(key, out v); }

        public TV this[int key]
        {
            set
            {
                if (!_map.ContainsKey(key)) _order.Enqueue(key);
                _map[key] = value;
                while (_map.Count > Cap) _map.Remove(_order.Dequeue());
            }
        }

        public void Clear() { _map.Clear(); _order.Clear(); }
    }
}

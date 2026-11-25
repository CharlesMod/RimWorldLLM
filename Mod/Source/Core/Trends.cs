using System.Collections.Generic;

namespace HermesVoice.Core
{
    /// <summary>Last few samples per named series, oldest first. Samples are taken at most once per interval.</summary>
    public sealed class Trends
    {
        readonly int _keep;
        readonly int _interval;
        readonly Dictionary<string, List<float>> _series = new Dictionary<string, List<float>>();
        readonly Dictionary<string, int> _lastTick = new Dictionary<string, int>();

        public Trends(int keep = 3, int intervalTicks = 15000) { _keep = keep; _interval = intervalTicks; }

        /// <summary>Records value when the interval has passed since the last sample of this series. Returns true if recorded.</summary>
        public bool Sample(string name, int tick, float value)
        {
            int last;
            if (_lastTick.TryGetValue(name, out last) && tick >= last && tick - last < _interval) return false;
            List<float> l;
            if (!_series.TryGetValue(name, out l)) _series[name] = l = new List<float>();
            l.Add(value);
            while (l.Count > _keep) l.RemoveAt(0);
            _lastTick[name] = tick;
            return true;
        }

        public float[] Get(string name)
        {
            List<float> l;
            return _series.TryGetValue(name, out l) ? l.ToArray() : new float[0];
        }

        public void Clear() { _series.Clear(); _lastTick.Clear(); }
    }
}

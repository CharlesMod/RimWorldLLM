using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>How busy the watch-mode camera is (setting watchPace: 0 calm, 1 normal, 2 lively).</summary>
    public enum WatchPace { Calm = 0, Normal = 1, Lively = 2 }

    /// <summary>
    /// Pure pacing: hold per shot kind and pace, and the no-repeat window (the same pawn or scene is not shown again
    /// within it unless urgent). Battles are held by the battle director as long as they last.
    /// </summary>
    public static class DirectorPacing
    {
        public static WatchPace FromInt(int v) { return v <= 0 ? WatchPace.Calm : v >= 2 ? WatchPace.Lively : WatchPace.Normal; }

        /// <summary>Seconds on a shot. Follow shots 10-20 s, scenes 8-15 s, events their dwell scaled by pace.</summary>
        public static double Hold(string kind, WatchPace pace)
        {
            if (kind == WatchKinds.Battle) return double.PositiveInfinity;
            if (kind == WatchKinds.Wander || kind == WatchKinds.Thread)
                return pace == WatchPace.Calm ? 20 : pace == WatchPace.Lively ? 10 : 15;
            if (SceneKinds.IsScene(kind))
                return pace == WatchPace.Calm ? 15 : pace == WatchPace.Lively ? 8 : 11;
            double k = pace == WatchPace.Calm ? 1.25 : pace == WatchPace.Lively ? 0.8 : 1.0;
            return WatchKinds.Dwell(kind) * k;
        }

        /// <summary>No-repeat window in seconds (calm 5 min, normal 4, lively 2.5).</summary>
        public static double NoRepeatSec(WatchPace pace) { return pace == WatchPace.Calm ? 300 : pace == WatchPace.Lively ? 150 : 240; }

        /// <summary>Block key of a shot: scenes by scene key, pawn shots by pawn; wander and place shots have none.</summary>
        public static string BlockKey(string kind, int pawnKey, string sceneKey)
        {
            if (kind == WatchKinds.Wander) return null;
            if (SceneKinds.IsScene(kind)) return sceneKey;
            return pawnKey >= 0 ? "p:" + pawnKey : null;
        }
    }

    /// <summary>Remembers what was shown when, for the no-repeat window.</summary>
    public sealed class RepeatGuard
    {
        readonly Dictionary<string, double> _shown = new Dictionary<string, double>();

        public void Clear() { _shown.Clear(); }

        public void Shown(string key, double now) { if (key != null) _shown[key] = now; }

        public bool Blocked(string key, double now, double windowSec, bool urgent)
        {
            if (urgent || key == null) return false;
            double at;
            return _shown.TryGetValue(key, out at) && now - at < windowSec;
        }

        public void Prune(double now, double windowSec)
        {
            if (_shown.Count < 64) return;
            foreach (var k in _shown.Where(kv => now - kv.Value >= windowSec).Select(kv => kv.Key).ToList()) _shown.Remove(k);
        }
    }

    /// <summary>Why a pawn has a story thread; the text follows the name in the caption ("Bram, after the funeral").</summary>
    public static class ThreadReasons
    {
        public const string Funeral = "after the funeral", Breakup = "after the breakup", Recovered = "after the break",
            Arrival = "newly arrived", Growth = "growing up";
        public const string LossPrefix = "after losing ";

        public static string Loss(string name) { return LossPrefix + (string.IsNullOrWhiteSpace(name) ? "someone close" : name.Trim()); }

        /// <summary>Thread length in game hours by reason.</summary>
        public static double Hours(string reason)
        {
            if (reason == null) return 4;
            if (reason.StartsWith(LossPrefix, StringComparison.Ordinal) || reason == Funeral) return 8;
            if (reason == Breakup || reason == Arrival) return 6;
            return 4;   // recovered from a break, growth moment
        }
    }

    /// <summary>
    /// Story threads: after a strong personal event a pawn scores higher for a few in-game hours, so the director
    /// returns to them. Boost decays linearly from MaxBoost to 1 over the thread. Clock = game ticks.
    /// </summary>
    public sealed class StoryThreads
    {
        public const int TicksPerHour = 2500;
        public const double MaxBoost = 3.0;
        public const int MaxThreads = 8;

        sealed class Thread { public int Id; public string Name, Reason; public long Start, End; }
        readonly List<Thread> _t = new List<Thread>();

        public int Count { get { return _t.Count; } }
        public void Clear() { _t.Clear(); }

        public void Start(int id, string name, string reason, long tick, double hours = -1)
        {
            if (id < 0 || string.IsNullOrWhiteSpace(reason)) return;
            if (hours <= 0) hours = ThreadReasons.Hours(reason);
            _t.RemoveAll(x => x.Id == id);
            _t.Add(new Thread { Id = id, Name = name, Reason = reason, Start = tick, End = tick + (long)(hours * TicksPerHour) });
            if (_t.Count > MaxThreads) _t.RemoveAt(0);   // oldest first
        }

        /// <summary>Change the reason of threads whose reason starts with prefix (e.g. loss to "after the funeral"), restarting them.</summary>
        public void Relabel(string prefix, string reason, long tick)
        {
            foreach (var x in _t.Where(x => x.Reason != null && x.Reason.StartsWith(prefix, StringComparison.Ordinal) && x.End > tick).ToList())
                Start(x.Id, x.Name, reason, tick);
        }

        public void Prune(long tick) { _t.RemoveAll(x => x.End <= tick); }

        public bool Has(int id, long tick) { return _t.Any(x => x.Id == id && x.End > tick); }

        public double Boost(int id, long tick)
        {
            foreach (var x in _t)
                if (x.Id == id && tick < x.End)
                {
                    double len = Math.Max(1, x.End - x.Start);
                    double left = Math.Min(1, Math.Max(0, (x.End - tick) / len));
                    return 1 + (MaxBoost - 1) * left;
                }
            return 1;
        }

        public string Caption(int id, long tick)
        {
            foreach (var x in _t)
                if (x.Id == id && tick < x.End)
                    return "Camera: " + (string.IsNullOrWhiteSpace(x.Name) ? "someone" : x.Name.Trim()) + ", " + x.Reason;
            return null;
        }

        /// <summary>Live thread pawn ids, strongest first.</summary>
        public List<int> Active(long tick)
        {
            return _t.Where(x => x.End > tick).OrderByDescending(x => Boost(x.Id, tick)).ThenBy(x => x.Id).Select(x => x.Id).ToList();
        }
    }
}

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    /// <summary>A validated POST /social_action body (no game types).</summary>
    public sealed class SocialRequest
    {
        public const int MaxTopicChars = 80;
        public const int AdultAge = 18;

        public int Pawn, Target;
        public string Action;      // "seek_out" | "avoid"
        public string Topic;       // "" when none or dropped

        /// <summary>Validates the body. Returns an error message, or null when ok (req is then set).</summary>
        public static string Parse(JObject body, out SocialRequest req)
        {
            req = null;
            if (body == null) return "body must be an object";
            JToken pawn = body["pawn"], target = body["target"], action = body["action"], topic = body["topic"];
            if (pawn == null || pawn.Type != JTokenType.Integer) return "pawn is required (pawn id, integer)";
            if (target == null || target.Type != JTokenType.Integer) return "target is required (pawn id, integer)";
            if (action == null || action.Type != JTokenType.String) return "action is required";
            string a = (string)action;
            if (a != "seek_out" && a != "avoid") return "action must be \"seek_out\" or \"avoid\"";
            if (pawn.Value<int>() == target.Value<int>()) return "target must be another pawn";
            string t = "";
            if (topic != null && topic.Type != JTokenType.Null)
            {
                if (topic.Type != JTokenType.String) return "topic must be a string";
                t = ((string)topic).Trim();
                if (t.Length > MaxTopicChars) t = t.Substring(0, MaxTopicChars);
            }
            req = new SocialRequest { Pawn = pawn.Value<int>(), Target = target.Value<int>(), Action = a, Topic = t };
            return null;
        }

        /// <summary>Second age guard: a topic is dropped when either pawn is under 18 (the agent gates this too). Nothing
        /// in this op is ever romantic, so this is the only place a minor changes behaviour.</summary>
        public static string GuardTopic(string topic, int pawnAge, int targetAge)
        {
            return pawnAge < AdultAge || targetAge < AdultAge ? "" : (topic ?? "");
        }
    }

    /// <summary>What the game says about the pawn and target, collected on the game thread; the decision is pure.</summary>
    public struct WantsFacts
    {
        public bool Enabled;
        public bool Action_SeekOut;           // false = avoid (avoid needs no reachability)
        public bool Spawned, SameMap, Dead, Downed, Drafted, Asleep, MentalState;
        public bool ForcedWork;               // prisoner or slave doing forced work
        public bool InCombat;
        public bool JobBlocks;                // a job that must not be interrupted
        public bool TargetSpawned, TargetReachable;
    }

    public static class WantsGate
    {
        /// <summary>The refusal reason, or null when the pawn may act on the want.</summary>
        public static string Refusal(WantsFacts f)
        {
            if (!f.Enabled) return "disabled";
            if (f.Dead) return "dead";
            if (!f.Spawned) return "not_spawned";
            if (!f.SameMap) return "other_map";
            if (f.Downed) return "downed";
            if (f.Drafted) return "drafted";
            if (f.Asleep) return "asleep";
            if (f.MentalState) return "mental_state";
            if (f.ForcedWork) return "forced_work";
            if (f.InCombat) return "in_combat";
            if (f.JobBlocks) return "busy";
            if (f.Action_SeekOut && (!f.TargetSpawned || !f.TargetReachable)) return "unreachable";
            return null;
        }
    }

    /// <summary>Pawn A keeps clear of pawn B until a game tick. Small, saved with the game, expires on game ticks.</summary>
    public sealed class AvoidTable
    {
        public const int TicksPerHour = 2500;
        public const int MaxEntries = 256;
        readonly Dictionary<long, int> _until = new Dictionary<long, int>();

        static long Key(int pawn, int target) { return ((long)pawn << 32) | (uint)target; }

        public int Count { get { return _until.Count; } }

        public void Add(int pawn, int target, int nowTick, int durationTicks)
        {
            Prune(nowTick);
            if (durationTicks <= 0) return;
            if (_until.Count >= MaxEntries && !_until.ContainsKey(Key(pawn, target))) return;
            _until[Key(pawn, target)] = nowTick + durationTicks;
        }

        public bool IsAvoiding(int pawn, int target, int nowTick)
        {
            int until;
            if (!_until.TryGetValue(Key(pawn, target), out until)) return false;
            if (nowTick < until) return true;
            _until.Remove(Key(pawn, target));
            return false;
        }

        public void Prune(int nowTick)
        {
            List<long> dead = null;
            foreach (var kv in _until) if (nowTick >= kv.Value) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _until.Remove(k);
        }

        public void Clear() { _until.Clear(); }

        /// <summary>Flat triples (pawn, target, until) for the save.</summary>
        public List<int> Export(int nowTick)
        {
            Prune(nowTick);
            var l = new List<int>();
            foreach (var kv in _until) { l.Add((int)(kv.Key >> 32)); l.Add((int)(kv.Key & 0xffffffffL)); l.Add(kv.Value); }
            return l;
        }

        public void Import(List<int> triples, int nowTick)
        {
            _until.Clear();
            if (triples == null) return;
            for (int i = 0; i + 2 < triples.Count; i += 3)
                if (triples[i + 2] > nowTick && _until.Count < MaxEntries) _until[Key(triples[i], triples[i + 1])] = triples[i + 2];
        }

        public static int HoursToTicks(float hours) { return (int)Math.Round(Math.Max(0f, Math.Min(hours, 24f)) * TicksPerHour); }
    }
}

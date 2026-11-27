using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    public sealed class LedgerEntry
    {
        public string Id;
        public string Kind;
        public string Key;
        public int OpenedTick;
        public int? Map;
        public string Summary;
        public List<int> Pawns = new List<int>();
        public JObject Data = new JObject();

        public JObject ToJson()
        {
            return new JObject
            {
                ["ledger_id"] = Id,
                ["kind"] = Kind,
                ["opened_tick"] = OpenedTick,
                ["map"] = Map.HasValue ? (JToken)Map.Value : JValue.CreateNull(),
                ["summary"] = Summary,
                ["pawns"] = new JArray(Pawns),
                ["data"] = Data
            };
        }
    }

    /// <summary>Open long-running facts (raids, quests, lodgers, prisoners, weather, funerals). Game thread only.</summary>
    public sealed class Ledger
    {
        /// <summary>Most open facts kept; opening one more drops the oldest (a raid whose raiders never die, a hidden quest...).</summary>
        public const int MaxOpen = 120;
        readonly List<LedgerEntry> _open = new List<LedgerEntry>();
        public int Counter; // saved by the game so ids stay unique across loads

        public IReadOnlyList<LedgerEntry> Open { get { return _open; } }

        public LedgerEntry Find(string key)
        {
            foreach (var e in _open) if (e.Key == key) return e;
            return null;
        }

        public LedgerEntry FindById(string id)
        {
            foreach (var e in _open) if (e.Id == id) return e;
            return null;
        }

        /// <summary>Opens an entry for a key; returns the existing one when the key is already open.</summary>
        public LedgerEntry OpenEntry(string kind, string key, int tick, int? map, string summary, IEnumerable<int> pawns = null, JObject data = null, string forcedId = null)
        {
            var existing = Find(key);
            if (existing != null) return existing;
            var e = new LedgerEntry
            {
                Id = forcedId ?? NextId(kind),
                Kind = kind, Key = key, OpenedTick = tick, Map = map, Summary = summary,
                Data = data ?? new JObject()
            };
            if (pawns != null) e.Pawns.AddRange(pawns);
            _open.Add(e);
            while (_open.Count > MaxOpen) _open.RemoveAt(0);
            return e;
        }

        /// <summary>Closes entries (of one kind, or all) opened more than maxAgeTicks ago (stuck facts). Returns how many.</summary>
        public int PruneOlderThan(int nowTick, int maxAgeTicks, string kind = null)
        {
            return _open.RemoveAll(e => (kind == null || e.Kind == kind) && nowTick - e.OpenedTick > maxAgeTicks);
        }

        public string NextId(string kind) { return kind + "-" + (++Counter); }

        public LedgerEntry Close(string key)
        {
            var e = Find(key);
            if (e != null) _open.Remove(e);
            return e;
        }

        public void Clear() { _open.Clear(); }

        public JArray ToJson()
        {
            var a = new JArray();
            foreach (var e in _open) a.Add(e.ToJson());
            return a;
        }
    }
}

// Port of agent/rimworldllm_agent/core/ledger.py.
// Event ledger: long-running facts kept alive by ledger_id, with per-actor knowledge.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public sealed class LedgerEntry
    {
        public string LedgerId;
        public string Etype;
        public string Summary;
        public long OpenedTime;
        public HashSet<int> KnownBy = new HashSet<int>();
        public bool Closed;
        internal long Seq;   // insertion order (Python dicts keep it)

        public LedgerEntry(string ledgerId, string etype, string summary, long openedTime)
        {
            LedgerId = ledgerId; Etype = etype; Summary = summary; OpenedTime = openedTime;
        }
    }

    public sealed class Ledger
    {
        public Dictionary<string, LedgerEntry> Entries = new Dictionary<string, LedgerEntry>();
        public Func<JObject, bool> IsEnd;
        long _seq;

        public Ledger(Func<JObject, bool> isEnd = null)
        {
            IsEnd = isEnd ?? (e => (Str(e, "type", "") ?? "").EndsWith("_end", StringComparison.Ordinal));
        }

        /// <summary>The entries in the order they were opened (Python dict iteration order).</summary>
        public List<LedgerEntry> EntriesInOrder() { return Entries.Values.OrderBy(e => e.Seq).ToList(); }

        // ---- Python dict.get / truthiness helpers ----
        static bool Has(JObject o, string k) { var t = o[k]; return t != null; }
        static string Str(JObject o, string k, string dflt)
        {
            var t = o[k];
            if (t == null) return dflt;
            if (t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        }
        static long Int(JToken t, long dflt)
        {
            if (t == null) return dflt;
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (long)(double)t;
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.String: return long.Parse(((string)t).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                default: throw new FormatException("int() of " + t.Type);
            }
        }
        static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null: return false;
                case JTokenType.String: return ((string)t).Length > 0;
                case JTokenType.Integer: return (long)t != 0;
                case JTokenType.Float: return (double)t != 0.0;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                case JTokenType.Object: return ((JObject)t).Count > 0;
                default: return true;
            }
        }
        static string KeyOf(JToken t) { return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None); }

        public void Apply(JObject ev, IEnumerable<int> eventActorIds = null)
        {
            var actorIds = eventActorIds ?? new int[0];
            var lidTok = ev["ledger_id"];
            if (!Truthy(lidTok)) return;
            string lid = KeyOf(lidTok);
            string etype = Str(ev, "type", "");
            bool isEnd = IsEnd(ev);
            Entries.TryGetValue(lid, out var e);
            if (isEnd)
            {
                if (e != null)
                {
                    e.Closed = true;
                    Entries.Remove(lid);
                }
                return;
            }
            if (e == null)
            {
                e = new LedgerEntry(lid, etype, Str(ev, "summary", ""), Int(ev["game"], 0)) { Seq = _seq++ };
                Entries[lid] = e;
            }
            else
            {
                e.Summary = Str(ev, "summary", e.Summary);
            }
            if (ev["witnesses"] is JArray wit)
                foreach (var w in wit) e.KnownBy.Add((int)Int(w, 0));
            foreach (var a in actorIds) e.KnownBy.Add(a);
        }

        /// <summary>Resync from the world's open ledger entries (after a gap or restart).</summary>
        public void LoadWorld(IEnumerable<JObject> entries)
        {
            foreach (var ent in entries)
            {
                var tok = Truthy(ent["ledger_id"]) ? ent["ledger_id"] : ent["id"];
                if (!Truthy(tok)) continue;
                string lid = KeyOf(tok);
                if (Entries.ContainsKey(lid)) continue;
                var e = new LedgerEntry(lid, Str(ent, "type", ""), Str(ent, "summary", ""), Int(ent["game"], 0)) { Seq = _seq++ };
                var kb = Has(ent, "known_by") ? ent["known_by"] : ent["witnesses"];
                if (kb is JArray arr)
                    foreach (var x in arr) e.KnownBy.Add((int)Int(x, 0));
                Entries[lid] = e;
            }
        }

        public void Tell(string ledgerId, int actorId)
        {
            if (Entries.TryGetValue(ledgerId, out var e)) e.KnownBy.Add(actorId);
        }

        /// <summary>Entries this actor knows about, in stable order (opening game, then id).</summary>
        public List<LedgerEntry> KnownTo(int actorId)
        {
            return Entries.Values.Where(e => e.KnownBy.Contains(actorId))
                .OrderBy(e => e.OpenedTime).ThenBy(e => e.LedgerId, StringComparer.Ordinal).ToList();
        }

        public void Clear() { Entries.Clear(); }
    }
}

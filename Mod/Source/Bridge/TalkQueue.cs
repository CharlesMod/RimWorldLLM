using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>Talk requests waiting for the agent (GET /talk). Game thread; removed when answered by POST /say with talk_id.</summary>
    public sealed class TalkQueue
    {
        const int Max = 100;
        readonly List<JObject> _items = new List<JObject>();
        int _next;

        public string Add(int pawnId, string text, string from)
        {
            string id = "t-" + (++_next);
            _items.Add(new JObject { ["id"] = id, ["pawn"] = pawnId, ["text"] = text, ["from"] = from });
            if (_items.Count > Max) _items.RemoveAt(0);
            return id;
        }

        public int Count { get { return _items.Count; } }

        public bool Contains(string id)
        {
            foreach (var i in _items) if ((string)i["id"] == id) return true;
            return false;
        }

        public JArray ToJson() { return new JArray(_items); }

        public bool Remove(string id)
        {
            for (int i = 0; i < _items.Count; i++)
                if ((string)_items[i]["id"] == id) { _items.RemoveAt(i); return true; }
            return false;
        }

        public void Clear() { _items.Clear(); }
    }
}

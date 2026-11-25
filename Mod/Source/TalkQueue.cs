using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HermesVoice
{
    /// <summary>Talk requests waiting for the agent (GET /talk). Game thread; removed when answered by POST /say with talk_id.</summary>
    public static class TalkQueue
    {
        const int Max = 100;
        static readonly List<JObject> Items = new List<JObject>();
        static int _next;

        public static string Add(int pawnId, string text, string from)
        {
            string id = "t-" + (++_next);
            Items.Add(new JObject { ["id"] = id, ["pawn"] = pawnId, ["text"] = text, ["from"] = from });
            if (Items.Count > Max) Items.RemoveAt(0);
            return id;
        }

        public static JArray ToJson() { return new JArray(Items); }

        public static bool Remove(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if ((string)Items[i]["id"] == id) { Items.RemoveAt(i); return true; }
            return false;
        }

        public static void Clear() { Items.Clear(); }
    }
}

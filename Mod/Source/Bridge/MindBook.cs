using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    public sealed class MindRecord
    {
        public string MindJson;   // opaque to the mod; null when none
        public int Rev;
        public List<KeyValuePair<int, string>> Thoughts = new List<KeyValuePair<int, string>>(); // tick, text
        public List<KeyValuePair<int, string>> Diaries = new List<KeyValuePair<int, string>>();  // tick, text
    }

    /// <summary>Per-pawn mind blobs (opaque JSON, rev-guarded, size-limited) and recent inner thoughts. Game thread.</summary>
    public sealed class MindBook
    {
        public const int MaxMindBytes = 32 * 1024;
        public const int MaxThoughts = 20;
        public const int MaxThoughtChars = 500;
        public const int MaxDiaries = 7;
        public const int MaxDiaryChars = 1500;
        public const string KindThought = "thought", KindDiary = "diary", KindDream = "dream";

        public static bool KnownKind(string kind) { return kind == KindThought || kind == KindDiary || kind == KindDream; }

        public readonly Dictionary<int, MindRecord> Records = new Dictionary<int, MindRecord>();

        MindRecord Rec(int id)
        {
            MindRecord r;
            if (!Records.TryGetValue(id, out r)) Records[id] = r = new MindRecord();
            return r;
        }

        public JObject Get(int id)
        {
            MindRecord r;
            Records.TryGetValue(id, out r);
            JToken mind = JValue.CreateNull();
            if (r != null && r.MindJson != null) mind = JToken.Parse(r.MindJson);
            return new JObject { ["mind"] = mind, ["rev"] = r != null ? r.Rev : 0 };
        }

        public enum PutResult { Ok, Conflict, TooLarge }

        /// <summary>Saves the mind if rev matches the stored rev; the new rev is returned in newRev.</summary>
        public PutResult Put(int id, int rev, JToken mind, out int newRev)
        {
            var r = Rec(id);
            newRev = r.Rev;
            if (rev != r.Rev) return PutResult.Conflict;
            string json = mind == null || mind.Type == JTokenType.Null ? null : mind.ToString(Newtonsoft.Json.Formatting.None);
            if (json != null && Encoding.UTF8.GetByteCount(json) > MaxMindBytes) return PutResult.TooLarge;
            r.MindJson = json;
            r.Rev++;
            newRev = r.Rev;
            return PutResult.Ok;
        }

        public void AddThought(int id, int tick, string text)
        {
            var r = Rec(id);
            if (text.Length > MaxThoughtChars) text = text.Substring(0, MaxThoughtChars);
            r.Thoughts.Add(new KeyValuePair<int, string>(tick, text));
            while (r.Thoughts.Count > MaxThoughts) r.Thoughts.RemoveAt(0);
        }

        /// <summary>A daily diary entry, kept apart from inner thoughts; the last 7 are kept.</summary>
        public void AddDiary(int id, int tick, string text)
        {
            var r = Rec(id);
            if (text.Length > MaxDiaryChars) text = text.Substring(0, MaxDiaryChars);
            r.Diaries.Add(new KeyValuePair<int, string>(tick, text));
            while (r.Diaries.Count > MaxDiaries) r.Diaries.RemoveAt(0);
        }

        public void Clear() { Records.Clear(); }
    }
}

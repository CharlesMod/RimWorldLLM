// Port of agent/rimworldllm_agent/core/minds.py: the actor mind (SPEC section 5) and its /mind blob.
// The pydantic models are C# classes whose [JsonProperty] names and declaration order match the Python
// fields. Loading goes through PyModel.Validate (pydantic lax-mode coercions, extra keys ignored, missing
// keys take the default, [PyRequired] keys must be present, [PyLiteral] checks Literal[...]); dumping goes
// through PyModel.Dump (model_dump) and PyModel.Compact (json.dumps(..., ensure_ascii=False, separators=(",", ":"))).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The field must be present when loading (pydantic: no default).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PyRequiredAttribute : Attribute { }

    /// <summary>str | None: a JSON null is accepted for this string property.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PyNullableAttribute : Attribute { }

    /// <summary>Literal["a", "b"]: the loaded string must be one of these.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PyLiteralAttribute : Attribute
    {
        public string[] Values;
        public PyLiteralAttribute(params string[] values) { Values = values; }
    }

    public class PyValidationException : Exception
    {
        public PyValidationException(string msg) : base(msg) { }
    }

    /// <summary>An insertion-ordered dict[str, Relation] (Python dicts keep insertion order, even after deletes).</summary>
    [JsonConverter(typeof(RelationMapConverter))]
    public sealed class RelationMap : IEnumerable<KeyValuePair<string, Relation>>
    {
        private readonly Dictionary<string, Relation> _d = new Dictionary<string, Relation>(StringComparer.Ordinal);
        private readonly List<string> _keys = new List<string>();
        public int Count { get { return _keys.Count; } }
        public bool ContainsKey(string k) { return _d.ContainsKey(k); }
        public bool TryGetValue(string k, out Relation r) { return _d.TryGetValue(k, out r); }
        public IEnumerable<string> Keys { get { return _keys.ToList(); } }
        public IEnumerable<Relation> Values { get { return _keys.Select(k => _d[k]).ToList(); } }
        public Relation this[string k]
        {
            get { return _d[k]; }
            set { if (!_d.ContainsKey(k)) _keys.Add(k); _d[k] = value; }
        }
        public bool Remove(string k)
        {
            if (!_d.Remove(k)) return false;
            _keys.Remove(k);
            return true;
        }
        public void Clear() { _d.Clear(); _keys.Clear(); }
        public IEnumerator<KeyValuePair<string, Relation>> GetEnumerator()
        {
            foreach (var k in _keys.ToList()) yield return new KeyValuePair<string, Relation>(k, _d[k]);
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    public sealed class RelationMapConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(RelationMap); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            w.WriteStartObject();
            foreach (var kv in (RelationMap)value) { w.WritePropertyName(kv.Key); s.Serialize(w, kv.Value); }
            w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var o = JObject.Load(r);
            var m = new RelationMap();
            foreach (var p in o.Properties()) m[p.Name] = p.Value.ToObject<Relation>(s);
            return m;
        }
    }

    /// <summary>pydantic-like load/dump for the model classes, driven by [JsonProperty] and the Py* attributes.</summary>
    public static class PyModel
    {
        private static readonly Dictionary<Type, List<PropertyInfo>> _props = new Dictionary<Type, List<PropertyInfo>>();

        private static List<PropertyInfo> Props(Type t)
        {
            lock (_props)
            {
                List<PropertyInfo> l;
                if (!_props.TryGetValue(t, out l))
                {
                    l = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.GetCustomAttribute<JsonPropertyAttribute>() != null && p.CanWrite)
                        .OrderBy(p => p.MetadataToken).ToList();
                    _props[t] = l;
                }
                return l;
            }
        }

        public static string Name(PropertyInfo p) { return p.GetCustomAttribute<JsonPropertyAttribute>().PropertyName; }

        // ---- load -------------------------------------------------------------------------
        public static T Validate<T>(JToken tok) where T : new() { return (T)Coerce(tok, typeof(T), "$"); }

        private static Exception Bad(string path, string what) { return new PyValidationException(path + ": " + what); }

        private static object Coerce(JToken t, Type type, string path)
        {
            var nn = Nullable.GetUnderlyingType(type);
            if (nn != null)
            {
                if (t == null || t.Type == JTokenType.Null) return null;
                return Coerce(t, nn, path);
            }
            if (t == null) throw Bad(path, "missing");
            if (type == typeof(JObject))
            {
                if (t.Type != JTokenType.Object) throw Bad(path, "not a dict");
                return t.DeepClone();
            }
            if (type == typeof(string))
            {
                if (t.Type != JTokenType.String) throw Bad(path, "not a str");
                return (string)t;
            }
            if (type == typeof(int) || type == typeof(long))
            {
                long v = ToLong(t, path);
                if (type == typeof(int))
                {
                    if (v < int.MinValue || v > int.MaxValue) throw Bad(path, "int out of range");
                    return (int)v;
                }
                return v;
            }
            if (type == typeof(double)) return ToDouble(t, path);
            if (type == typeof(bool)) return ToBool(t, path);
            if (type == typeof(RelationMap))
            {
                if (t.Type != JTokenType.Object) throw Bad(path, "not a dict");
                var m = new RelationMap();
                foreach (var p in ((JObject)t).Properties())
                    m[p.Name] = (Relation)Coerce(p.Value, typeof(Relation), path + "." + p.Name);
                return m;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (t.Type != JTokenType.Array) throw Bad(path, "not a list");
                var et = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                int i = 0;
                foreach (var item in (JArray)t) list.Add(Coerce(item, et, path + "[" + (i++) + "]"));
                return list;
            }
            // a model class
            if (t.Type != JTokenType.Object) throw Bad(path, "not an object");
            var o = (JObject)t;
            var inst = Activator.CreateInstance(type);
            foreach (var p in Props(type))
            {
                var name = Name(p);
                JToken v = o[name];
                if (v == null)
                {
                    if (p.GetCustomAttribute<PyRequiredAttribute>() != null) throw Bad(path + "." + name, "field required");
                    continue;
                }
                object val = (v.Type == JTokenType.Null && p.GetCustomAttribute<PyNullableAttribute>() != null)
                    ? null : Coerce(v, p.PropertyType, path + "." + name);
                var lit = p.GetCustomAttribute<PyLiteralAttribute>();
                if (lit != null && val != null && Array.IndexOf(lit.Values, (string)val) < 0) throw Bad(path + "." + name, "not a permitted literal");
                p.SetValue(inst, val);
            }
            return inst;
        }

        private static long ToLong(JToken t, string path)
        {
            switch (t.Type)
            {
                case JTokenType.Integer:
                    try { return (long)t; } catch (OverflowException) { throw Bad(path, "int too large"); }
                case JTokenType.Float:
                    {
                        double d = (double)t;
                        if (double.IsNaN(d) || double.IsInfinity(d) || d != Math.Floor(d)) throw Bad(path, "not an integer");
                        return (long)d;
                    }
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.String:
                    {
                        long v;
                        if (long.TryParse(((string)t).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) return v;
                        throw Bad(path, "not an integer string");
                    }
            }
            throw Bad(path, "not an int");
        }

        private static double ToDouble(JToken t, string path)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String:
                    {
                        double v;
                        if (double.TryParse(((string)t).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
                        throw Bad(path, "not a float string");
                    }
            }
            throw Bad(path, "not a float");
        }

        private static bool ToBool(JToken t, string path)
        {
            switch (t.Type)
            {
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer:
                    { long v = (long)t; if (v == 0) return false; if (v == 1) return true; break; }
                case JTokenType.Float:
                    { double v = (double)t; if (v == 0) return false; if (v == 1) return true; break; }
                case JTokenType.String:
                    switch (((string)t).Trim().ToLowerInvariant())
                    {
                        case "true": case "t": case "yes": case "y": case "on": case "1": return true;
                        case "false": case "f": case "no": case "n": case "off": case "0": return false;
                    }
                    break;
            }
            throw Bad(path, "not a bool");
        }

        // ---- dump -------------------------------------------------------------------------
        /// <summary>pydantic model_dump(): field order, nulls kept, floats stay floats.</summary>
        public static JToken Dump(object o)
        {
            if (o == null) return JValue.CreateNull();
            var type = o.GetType();
            if (o is string) return new JValue((string)o);
            if (o is int) return new JValue((int)o);
            if (o is long) return new JValue((long)o);
            if (o is double) return new JValue((double)o);
            if (o is bool) return new JValue((bool)o);
            if (o is JToken) return ((JToken)o).DeepClone();
            if (o is RelationMap)
            {
                var m = new JObject();
                foreach (var kv in (RelationMap)o) m[kv.Key] = Dump(kv.Value);
                return m;
            }
            if (o is IList)
            {
                var a = new JArray();
                foreach (var x in (IList)o) a.Add(Dump(x));
                return a;
            }
            var obj = new JObject();
            foreach (var p in Props(type)) obj[Name(p)] = Dump(p.GetValue(o));
            return obj;
        }

        /// <summary>json.dumps(x, ensure_ascii=False, separators=(",", ":")) of a JSON tree (what model_dump_json gives).</summary>
        public static string Compact(JToken t)
        {
            var sb = new StringBuilder();
            Write(sb, t);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Object:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (var p in ((JObject)t).Properties())
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteStr(sb, p.Name); sb.Append(':'); Write(sb, p.Value);
                        }
                        sb.Append('}');
                        return;
                    }
                case JTokenType.Array:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (var x in (JArray)t) { if (!first) sb.Append(','); first = false; Write(sb, x); }
                        sb.Append(']');
                        return;
                    }
                case JTokenType.String: WriteStr(sb, (string)t); return;
                case JTokenType.Integer: sb.Append(((JValue)t).ToString(CultureInfo.InvariantCulture)); return;
                case JTokenType.Float:
                    {
                        double d = (double)t;
                        if (double.IsNaN(d)) sb.Append("NaN");
                        else if (double.IsPositiveInfinity(d)) sb.Append("Infinity");
                        else if (double.IsNegativeInfinity(d)) sb.Append("-Infinity");
                        else sb.Append(PyFmt.Repr(d));
                        return;
                    }
                case JTokenType.Boolean: sb.Append((bool)t ? "true" : "false"); return;
                default: sb.Append("null"); return;
            }
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    public class FixedBio
    {
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("facts_hash")] public string FactsHash { get; set; } = "";
        [JsonProperty("extras")] public JObject Extras { get; set; } = new JObject();
    }

    public class VoiceCard
    {
        [JsonProperty("register_line")] public string RegisterLine { get; set; } = "";
        [JsonProperty("tics")] public List<string> Tics { get; set; } = new List<string>();
        [JsonProperty("examples")] public List<string> Examples { get; set; } = new List<string>();
        [JsonProperty("edited_by_player")] public bool EditedByPlayer { get; set; }
        [JsonProperty("llm_examples")] public bool LlmExamples { get; set; }
        [JsonProperty("third_person")] public bool? ThirdPerson { get; set; }

        /// <summary>sanitize.is_self_narration. Defaults to a faithful local copy; the Sanitize port may replace it.</summary>
        public static Func<string, ISet<string>, bool> SelfNarrationCheck = DefaultIsSelfNarration;

        private static readonly Regex _selfNarr = new Regex(@"^([A-Z][a-z]+)\s+([a-z][a-z']*)", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> _notVerb = new HashSet<string> {
            "and", "or", "but", "so", "then", "the", "a", "an", "i", "me", "my", "you", "he", "she", "we", "they", "it",
            "of", "to", "in", "on", "at", "for", "with", "from", "as", "if", "who", "that", "which" };

        public static bool DefaultIsSelfNarration(string t, ISet<string> speakerNames)
        {
            var m = _selfNarr.Match(t);
            if (!m.Success) return false;
            var lower = new HashSet<string>(speakerNames.Select(n => n.ToLowerInvariant()));
            if (!lower.Contains(m.Groups[1].Value.ToLowerInvariant())) return false;
            return !_notVerb.Contains(m.Groups[2].Value);
        }

        /// <summary>True if this voice refers to itself by name: the flag if set, else when at least one example starts with
        /// the speaker's own name followed by a verb.</summary>
        public bool SpeaksThirdPerson(ISet<string> names)
        {
            if (ThirdPerson.HasValue) return ThirdPerson.Value;
            return Examples.Count(e => SelfNarrationCheck(e.Trim(), names)) >= 1;
        }
    }

    public class BioDiff
    {
        [JsonProperty("rev")] public int Rev { get; set; }
        [PyRequired] [JsonProperty("game")] public long Game { get; set; }
        [PyRequired] [JsonProperty("before")] public string Before { get; set; }
        [PyRequired] [JsonProperty("after")] public string After { get; set; }
    }

    public class Memory
    {
        [PyRequired] [JsonProperty("id")] public string Id { get; set; }
        [PyRequired] [JsonProperty("game")] public long Game { get; set; }
        [PyRequired] [JsonProperty("text")] public string Text { get; set; }
        [PyLiteral("saw", "heard", "told")] [JsonProperty("source")] public string Source { get; set; } = "saw";
        [PyNullable] [JsonProperty("told_by")] public string ToldBy { get; set; }
        [JsonProperty("told_by_id")] public int? ToldById { get; set; }
        [JsonProperty("confidence")] public double Confidence { get; set; } = 1.0;
        [PyLiteral("private", "told", "public")] [JsonProperty("visibility")] public string Visibility { get; set; } = "public";
        [JsonProperty("told_to")] public List<string> ToldTo { get; set; } = new List<string>();
        [JsonProperty("importance")] public double Importance { get; set; } = 0.3;
        [JsonProperty("pinned")] public bool Pinned { get; set; }
        [JsonProperty("people")] public List<int> People { get; set; } = new List<int>();
        [JsonProperty("etype")] public string Etype { get; set; } = "";
        [JsonProperty("event_seq")] public long? EventSeq { get; set; }
        [JsonProperty("summary_of")] public int SummaryOf { get; set; }
        [JsonProperty("chain")] public List<string> Chain { get; set; } = new List<string>();
        [JsonProperty("hops")] public int Hops { get; set; }
        [JsonProperty("sources")] public int Sources { get; set; } = 1;
    }

    public class Relation
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("feeling")] public string Feeling { get; set; } = "";
        [JsonProperty("history")] public string History { get; set; } = "";
        [JsonProperty("opinion")] public int? Opinion { get; set; }
        [JsonProperty("updated_time")] public long UpdatedTime { get; set; }
    }

    /// <summary>One line of block C's relations digest, frozen at the last reflection.</summary>
    public class CRel
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("opinion")] public int? Opinion { get; set; }
        [JsonProperty("feeling")] public string Feeling { get; set; } = "";
        [JsonProperty("history")] public string History { get; set; } = "";
        [JsonProperty("reputation")] public string Reputation { get; set; } = "";
    }

    public class DiaryEntry
    {
        [PyRequired] [JsonProperty("day")] public int Day { get; set; }
        [PyRequired] [JsonProperty("game")] public long Game { get; set; }
        [PyRequired] [JsonProperty("text")] public string Text { get; set; }
    }

    public class InnerThought
    {
        [JsonProperty("game")] public long Game { get; set; }
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("kind")] public string Kind { get; set; } = "aim";
    }

    /// <summary>One evolution of the voice card's tics (core/habits.py): enough to undo it.</summary>
    public class HabitChange
    {
        [JsonProperty("game")] public long Game { get; set; }
        [JsonProperty("added")] public string Added { get; set; } = "";
        [JsonProperty("removed")] public string Removed { get; set; } = "";
    }

    /// <summary>An ongoing worry (core/concerns.py).</summary>
    public class Concern
    {
        [PyRequired] [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("about")] public List<int> About { get; set; } = new List<int>();
        [JsonProperty("kind")] public string Kind { get; set; } = "";
        [JsonProperty("born")] public long Born { get; set; }
        [PyLiteral("open", "resolved", "faded")] [JsonProperty("status")] public string Status { get; set; } = "open";
        [JsonProperty("resolve_hint")] public string ResolveHint { get; set; } = "";
        [JsonProperty("importance")] public double Importance { get; set; } = 0.5;
        [JsonProperty("closed")] public long Closed { get; set; }
        [JsonProperty("outcome")] public string Outcome { get; set; } = "";
    }

    /// <summary>A grudge held by this mind (core/grudges.py).</summary>
    public class Grudge
    {
        [PyRequired] [JsonProperty("id")] public string Id { get; set; }
        [PyRequired] [JsonProperty("against")] public int Against { get; set; }
        [JsonProperty("cause")] public string Cause { get; set; } = "";
        [JsonProperty("born")] public long Born { get; set; }
        [PyLiteral("avoid", "cooldown", "resolve", "done")] [JsonProperty("phase")] public string Phase { get; set; } = "avoid";
        [JsonProperty("renewals")] public int Renewals { get; set; }
        [JsonProperty("attempts")] public int Attempts { get; set; }
        [JsonProperty("next_try")] public long NextTry { get; set; }
        [JsonProperty("approach")] public string Approach { get; set; } = "";
        [JsonProperty("mover")] public int Mover { get; set; }
        [JsonProperty("ended")] public string Ended { get; set; } = "";
        [JsonProperty("closed")] public long Closed { get; set; }
    }

    public class Mind
    {
        public const int DefaultDayUnits = 60000;      // fallback only; the adapter supplies units_per_day
        public const double HalfLifeDays = 4.0;
        public const int BlobLimit = 32 * 1024;
        public const int BlobTarget = 30 * 1024;       // headroom for the envelope and later growth
        public const int Schema = 1;
        public const int BioHistoryKeep = 10;
        public const int DiaryKeep = 7;
        public const int InnerKeep = 6;                // default cap of the inner stream
        public const int HabitKeep = 6;

        [JsonProperty("schema_v")] public int SchemaV { get; set; } = Schema;
        [PyRequired] [JsonProperty("actor_id")] public int ActorId { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("fixed")] public FixedBio Fixed { get; set; } = new FixedBio();
        [JsonProperty("voice")] public VoiceCard Voice { get; set; } = new VoiceCard();
        [JsonProperty("evolving_bio")] public string EvolvingBio { get; set; } = "";
        [JsonProperty("bio_history")] public List<BioDiff> BioHistory { get; set; } = new List<BioDiff>();
        [JsonProperty("bio_rev")] public int BioRev { get; set; }
        [JsonProperty("c_relations")] public List<CRel> CRelations { get; set; } = new List<CRel>();
        [JsonProperty("diary")] public List<DiaryEntry> Diary { get; set; } = new List<DiaryEntry>();
        [JsonProperty("last_diary_day")] public int LastDiaryDay { get; set; } = -1;
        [JsonProperty("wants")] public List<string> Wants { get; set; } = new List<string>();
        [JsonProperty("memories")] public List<Memory> Memories { get; set; } = new List<Memory>();
        [JsonProperty("relations")] public RelationMap Relations { get; set; } = new RelationMap();
        [JsonProperty("notes")] public string Notes { get; set; } = "";
        [JsonProperty("notes_edited_by_player")] public bool NotesEditedByPlayer { get; set; }
        [JsonProperty("notes_rev")] public int NotesRev { get; set; }
        [JsonProperty("firsts")] public List<string> Firsts { get; set; } = new List<string>();
        [JsonProperty("unreflected_importance")] public double UnreflectedImportance { get; set; }
        [JsonProperty("last_reflect_time")] public long LastReflectTime { get; set; }
        [JsonProperty("mem_counter")] public int MemCounter { get; set; }
        [JsonProperty("last_line_time")] public long LastLineTime { get; set; }
        [JsonProperty("inner")] public List<InnerThought> Inner { get; set; } = new List<InnerThought>();
        [JsonProperty("habit_history")] public List<HabitChange> HabitHistory { get; set; } = new List<HabitChange>();
        [JsonProperty("last_habit_game")] public long LastHabitGame { get; set; } = -1;
        [JsonProperty("concerns")] public List<Concern> Concerns { get; set; } = new List<Concern>();
        [JsonProperty("concern_counter")] public int ConcernCounter { get; set; }
        [JsonProperty("grudges")] public List<Grudge> Grudges { get; set; } = new List<Grudge>();

        public Mind() { }
        public Mind(int actorId, string name = "") { ActorId = actorId; Name = name; }

        // ---- memories -------------------------------------------------------
        public string NextMemId()
        {
            MemCounter += 1;
            return "m" + MemCounter.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Importance decayed by age with a 4-day half-life; pinned memories do not decay.</summary>
        public double MemoryWeight(Memory m, long nowTime, int day = DefaultDayUnits)
        {
            if (m.Pinned) return Math.Max(m.Importance, 0.5) + 1.0;
            double ageDays = (double)Math.Max(0L, nowTime - m.Game) / day;
            return m.Importance * m.Confidence * Math.Pow(0.5, ageDays / HalfLifeDays);
        }

        public void AddBioDiff(long game, string newText)
        {
            if (newText != EvolvingBio)
            {
                BioRev += 1;
                BioHistory.Add(new BioDiff { Rev = BioRev, Game = game, Before = EvolvingBio, After = newText });
                BioHistory = Tail(BioHistory, BioHistoryKeep);
                EvolvingBio = newText;
            }
        }

        public bool UndoBio()
        {
            if (BioHistory.Count == 0) return false;
            var last = BioHistory[BioHistory.Count - 1];
            BioHistory.RemoveAt(BioHistory.Count - 1);
            EvolvingBio = last.Before;
            return true;
        }

        /// <summary>Refresh block C's relations digest from the live relations. Reflection only.
        /// <paramref name="reputations"/> maps a subject's actor id (as text) to the group's line about them.</summary>
        public void FreezeRelations(int limit = 6, IDictionary<string, string> reputations = null, int repLimit = 3)
        {
            var rels = Relations.OrderBy(kv => -Math.Abs(kv.Value.Opinion ?? 0)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                .Take(Math.Max(0, limit)).ToList();
            Func<string, string> rep = k =>
            {
                string v;
                return reputations != null && reputations.TryGetValue(k, out v) ? v : "";
            };
            var keep = new HashSet<string>(rels.Where(kv => !string.IsNullOrEmpty(rep(kv.Key))).Select(kv => kv.Key)
                                               .Take(Math.Max(0, repLimit)));
            CRelations = rels.Select(kv => new CRel
            {
                Name = kv.Value.Name, Opinion = kv.Value.Opinion, Feeling = kv.Value.Feeling, History = kv.Value.History,
                Reputation = keep.Contains(kv.Key) ? rep(kv.Key) : "",
            }).ToList();
        }

        public void AddDiary(int day, long game, string text)
        {
            Diary.Add(new DiaryEntry { Day = day, Game = game, Text = text });
            Diary = Tail(Diary, DiaryKeep);
            LastDiaryDay = day;
        }

        /// <summary>Python's lst[-n:].</summary>
        public static List<T> Tail<T>(List<T> l, int n)
        {
            return l.Count <= n ? new List<T>(l) : l.GetRange(l.Count - n, n);
        }

        // ---- serialisation --------------------------------------------------
        /// <summary>model_dump(): the mind as a JSON tree in field order.</summary>
        public JObject Dump() { return (JObject)PyModel.Dump(this); }

        /// <summary>model_dump_json(): compact, ensure_ascii off.</summary>
        public string DumpJson() { return PyModel.Compact(Dump()); }

        private int Size() { return Encoding.UTF8.GetByteCount(DumpJson()); }

        /// <summary>Dict ready for PUT /mind. Trims *this object* to fit: decayed memories first.</summary>
        public JObject ToBlob(long nowTime = 0, int day = DefaultDayUnits, int limit = BlobTarget)
        {
            if (Size() > limit)
            {
                // 1. ordinary memories, lowest decayed weight first
                var ordinary = Memories.Where(m => !m.Pinned)
                    .OrderBy(m => MemoryWeight(m, nowTime, day)).ThenBy(m => m.Game).ToList();
                foreach (var m in ordinary)
                {
                    if (Size() <= limit) break;
                    Memories.Remove(m);
                }
            }
            if (Size() > limit) Diary = Tail(Diary, 3);
            if (Size() > limit) BioHistory = Tail(BioHistory, 2);
            if (Size() > limit) BioHistory = new List<BioDiff>();
            if (Size() > limit) Inner = Tail(Inner, 3);
            if (Size() > limit)
            {
                foreach (var r in CRelations) r.Reputation = "";
                HabitHistory = Tail(HabitHistory, 2);
                Concerns = Tail(Concerns.Where(c => c.Status == "open").ToList(), 2);
                Grudges = Tail(Grudges.Where(g => g.Phase != "done").ToList(), 2);
            }
            if (Size() > limit)
            {
                // 2. pinned memories, least important and oldest first
                foreach (var m in Memories.OrderBy(m => m.Importance).ThenBy(m => m.Game).ToList())
                {
                    if (Size() <= limit) break;
                    Memories.Remove(m);
                }
            }
            if (Size() > limit) Diary = new List<DiaryEntry>();
            if (Size() > limit) Inner = new List<InnerThought>();
            if (Size() > limit) HabitHistory = new List<HabitChange>();
            if (Size() > limit)
            {
                var keep = Relations.OrderBy(kv => -kv.Value.UpdatedTime).Take(20).ToList();
                var nr = new RelationMap();
                foreach (var kv in keep) nr[kv.Key] = kv.Value;
                Relations = nr;
            }
            return Dump();
        }

        /// <summary>Mind.from_blob: None, an empty or an unreadable blob gives a fresh mind. Extra keys are ignored.</summary>
        public static Mind FromBlob(JObject blob, int actorId, string name = "")
        {
            if (blob == null || blob.Count == 0) return new Mind(actorId, name);
            Mind m;
            try { m = PyModel.Validate<Mind>(blob); }
            catch (Exception) { return new Mind(actorId, name); }   // unreadable blob: start a fresh mind
            m.ActorId = actorId;
            return m;
        }

        /// <summary>model_validate_json: throws PyValidationException / JsonException when invalid.</summary>
        public static Mind FromJson(string json) { return PyModel.Validate<Mind>(JToken.Parse(json)); }
    }
}

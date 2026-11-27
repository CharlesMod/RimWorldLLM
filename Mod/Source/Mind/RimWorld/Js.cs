// Python-flavoured reads of decoded JSON (snapshots, scenes, events), shared by the RimWorld adapter files.
// A JSON null reads as "missing", as Python's dict.get(key) returning None does.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    internal static class Js
    {
        public static JToken Get(JToken o, string key)
        {
            var obj = o as JObject;
            if (obj == null) return null;
            JToken t;
            if (!obj.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            return t;
        }

        public static JObject Obj(JToken t) { return t as JObject; }
        public static JObject ObjGet(JToken o, string key) { return Get(o, key) as JObject; }

        /// <summary>Iterate a list value (an array); anything else is empty.</summary>
        public static IEnumerable<JToken> Items(JToken t)
        {
            var a = t as JArray;
            return a == null ? Enumerable.Empty<JToken>() : (IEnumerable<JToken>)a;
        }

        public static IEnumerable<JToken> ItemsGet(JToken o, string key) { return Items(Get(o, key)); }

        public static bool Truthy(JToken t) { return t != null && PyJson.Truthy(t); }

        /// <summary>str(x or "")</summary>
        public static string S(JToken t) { return Truthy(t) ? PyJson.Str(t) : ""; }

        /// <summary>str(x) (None for missing)</summary>
        public static string Str(JToken t) { return t == null ? "None" : PyJson.Str(t); }

        /// <summary>str(o.get(key, default)) for a present value, the default when missing.</summary>
        public static string StrOr(JToken o, string key, string dflt)
        {
            var t = Get(o, key);
            return t == null ? dflt : PyJson.Str(t);
        }

        /// <summary>isinstance(x, (int, float)): includes bool unless noBool.</summary>
        public static bool IsNum(JToken t, bool noBool = false)
        {
            if (t == null) return false;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return true;
            return !noBool && t.Type == JTokenType.Boolean;
        }

        public static double Dbl(JToken t)
        {
            if (t.Type == JTokenType.Boolean) return (bool)t ? 1.0 : 0.0;
            return Convert.ToDouble(((JValue)t).Value, CultureInfo.InvariantCulture);
        }

        /// <summary>float(x): numbers and numeric strings.</summary>
        public static double Float(JToken t)
        {
            if (t != null && t.Type == JTokenType.String)
            {
                double d;
                if (PyFloat.TryParse((string)t, out d)) return d;
                throw new PyValueError("could not convert string to float: " + PyJson.StrRepr((string)t));
            }
            return Dbl(t);
        }

        /// <summary>int(x): integers, floats (truncated), numeric strings.</summary>
        public static int Int(JToken t)
        {
            if (t == null) throw new PyTypeError("int() argument must be a string or a number, not 'NoneType'");
            switch (t.Type)
            {
                case JTokenType.Integer: return (int)(long)t;
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.Float: return (int)Math.Truncate((double)t);
                case JTokenType.String:
                    {
                        int v;
                        if (int.TryParse(PyStr.Strip((string)t), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) return v;
                        throw new PyValueError("invalid literal for int() with base 10: " + PyJson.StrRepr((string)t));
                    }
                default: throw new PyTypeError("int() argument must be a string or a number");
            }
        }

        /// <summary>The plain .NET value of a scalar token (long, double, string, bool, null) for helpers that take object.</summary>
        public static object Raw(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.String: return (string)t;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Null: return null;
                default: return t;
            }
        }

        /// <summary>A job as text: a plain string, or the {def, label} object the pawn route sends (label first).</summary>
        public static string JobText(JToken j)
        {
            if (!Truthy(j)) return "";
            var o = j as JObject;
            if (o != null) return Truthy(Get(o, "label")) ? PyJson.Str(Get(o, "label")) : (Truthy(Get(o, "def")) ? PyJson.Str(Get(o, "def")) : "");
            return PyJson.Str(j);
        }
    }
}

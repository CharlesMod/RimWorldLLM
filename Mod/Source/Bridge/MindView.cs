using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Read-only, tolerant view of an agent mind blob for display. The blob is the agent's format: unknown keys are ignored
    /// and each field is read under the agent's real name (core/minds.py) or the older spelling from the UI brief.
    /// </summary>
    public sealed class MindView
    {
        public sealed class BioEntry { public int Rev; public int Tick; public string Text = ""; public string Before = ""; }
        public sealed class Memory
        {
            public string Text = "", Source = "", From = "", Visibility = "";
            public int Tick; public float Confidence = 1f, Importance; public bool Pinned;
        }
        public sealed class Relation { public string Id = "", Name = "", Feeling = "", History = ""; }

        public string FixedBio = "", VoiceRegister = "", EvolvingBio = "", Notes = "";
        public bool VoiceEditedByPlayer, NotesEditedByPlayer;
        public List<string> Tics = new List<string>(), Examples = new List<string>(), Wants = new List<string>();
        public List<BioEntry> History = new List<BioEntry>();
        public List<Memory> Memories = new List<Memory>();
        public List<Relation> Relations = new List<Relation>();

        static string Str(JToken t)
        {
            if (t == null) return "";
            switch (t.Type)
            {
                case JTokenType.String: return (string)t;
                case JTokenType.Null: case JTokenType.Undefined: return "";
                case JTokenType.Object: case JTokenType.Array: return "";
                default: return t.ToString();
            }
        }

        static JToken First(JObject o, params string[] keys)
        {
            if (o == null) return null;
            foreach (var k in keys) { var t = o[k]; if (t != null && t.Type != JTokenType.Null) return t; }
            return null;
        }

        static int Int(JToken t) { if (t == null) return 0; try { return t.Type == JTokenType.Float ? (int)(double)t : (int)(long)t; } catch { return 0; } }
        static float Flt(JToken t, float d) { if (t == null) return d; try { return (float)t; } catch { return d; } }
        static bool Bool(JToken t) { return t != null && t.Type == JTokenType.Boolean && (bool)t; }

        static void Strings(JToken t, List<string> into)
        {
            var a = t as JArray;
            if (a == null) return;
            foreach (var x in a)
            {
                var o = x as JObject;
                string s = o != null ? Str(First(o, "text", "want")) : Str(x);
                if (s.Length > 0) into.Add(s);
            }
        }

        public static MindView Parse(JToken blob)
        {
            var v = new MindView();
            var m = blob as JObject;
            if (m == null) return v;

            var fixedTok = First(m, "fixed_bio", "fixed");
            v.FixedBio = fixedTok is JObject ? Str(((JObject)fixedTok)["text"]) : Str(fixedTok);

            var voice = First(m, "voice_card", "voice") as JObject;
            if (voice != null)
            {
                v.VoiceRegister = Str(First(voice, "register", "register_line"));
                Strings(voice["tics"], v.Tics);
                Strings(voice["examples"], v.Examples);
                v.VoiceEditedByPlayer = Bool(voice["edited_by_player"]);
            }

            var evo = m["evolving_bio"];
            JToken hist = m["bio_history"];
            if (evo is JObject)
            {
                v.EvolvingBio = Str(((JObject)evo)["text"]);
                if (hist == null) hist = ((JObject)evo)["history"];
            }
            else v.EvolvingBio = Str(evo);
            var ha = hist as JArray;
            if (ha != null)
            {
                int n = 0;
                foreach (var h in ha)
                {
                    n++;
                    var o = h as JObject;
                    if (o == null) continue;
                    var e = new BioEntry
                    {
                        Rev = o["rev"] != null ? Int(o["rev"]) : n,
                        Tick = Int(First(o, "tick", "game")),
                        Text = Str(First(o, "text", "after")),
                        Before = Str(o["before"])
                    };
                    v.History.Add(e);
                }
            }

            Strings(m["wants"], v.Wants);

            var mems = m["memories"] as JArray;
            if (mems != null)
                foreach (var x in mems)
                {
                    var o = x as JObject;
                    if (o == null) continue;
                    v.Memories.Add(new Memory
                    {
                        Text = Str(o["text"]),
                        Tick = Int(First(o, "tick", "game")),
                        Source = Str(o["source"]),
                        From = Str(First(o, "from", "told_by")),
                        Confidence = Flt(o["confidence"], 1f),
                        Visibility = Str(o["visibility"]),
                        Importance = Flt(o["importance"], 0f),
                        Pinned = Bool(o["pinned"])
                    });
                }

            var rels = m["relations"] as JObject;
            if (rels != null)
                foreach (var p in rels.Properties())
                {
                    var o = p.Value as JObject;
                    if (o == null) continue;
                    string h;
                    var hs = o["history"] as JArray;
                    if (hs != null) { var parts = new List<string>(); Strings(hs, parts); h = string.Join(" ", parts.ToArray()); }
                    else h = Str(o["history"]);
                    v.Relations.Add(new Relation { Id = p.Name, Name = Str(o["name"]), Feeling = Str(o["feeling"]), History = h });
                }

            v.Notes = Str(m["notes"]);
            v.NotesEditedByPlayer = Bool(m["notes_edited_by_player"]);
            return v;
        }
    }

    /// <summary>The Director's notes write-back: sets notes, marks them player-edited and bumps a notes revision.</summary>
    public static class MindNotes
    {
        public const string EditedFlag = "notes_edited_by_player";
        public const string RevKey = "notes_rev";
        public const int MaxNotesChars = 2000;

        /// <summary>Applies the player's notes to a blob (in place) and returns it.</summary>
        public static JObject Apply(JObject mind, string notes)
        {
            if (notes == null) notes = "";
            if (notes.Length > MaxNotesChars) notes = notes.Substring(0, MaxNotesChars);
            mind["notes"] = notes;
            mind[EditedFlag] = true;
            int rev = 0;
            var r = mind[RevKey];
            if (r != null && r.Type == JTokenType.Integer) rev = (int)(long)r;
            mind[RevKey] = rev + 1;
            return mind;
        }
    }
}

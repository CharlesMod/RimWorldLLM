// Port of agent/rimworldllm_agent/games/rimworld/builders.py: fixed bio and voice card from GET /pawn data (SPEC section 5, 12.4).
// Deterministic templates first; an optional LLM pass writes the voice card's 3 example lines once (saved in the mind).
//
// DLC fields are read only when present: Ideology (ideo, ideo_role), Biotech (xenotype, genes), Royalty (royal_title).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class Builders
    {
        /// <summary>Label of a string-or-dict item.</summary>
        public static string Label(JToken x)
        {
            var o = x as JObject;
            if (o != null)
            {
                foreach (var k in new[] { "label", "title", "def", "name" })
                    if (Js.Truthy(Js.Get(o, k))) return PyJson.Str(Js.Get(o, k));
                return "";
            }
            return Js.Str(x);
        }

        /// <summary>One name part, cleaned; a part that was nothing but a planted instruction counts as absent.</summary>
        private static string NamePart(JToken x)
        {
            string t = GameData.Clean(Js.S(x), "name");
            return t.Contains(GameData.Filtered) ? t.Replace(GameData.Filtered, "").Trim(' ', '\'', '"', '-', ',', '.') : t;
        }

        /// <summary>Names are typed by the player (and by other mods): cleaned of instruction-like content, capped, never empty.</summary>
        public static string DisplayName(JObject p)
        {
            var n = Js.Get(p, "name");
            if (n != null && n.Type == JTokenType.String) { string s = (string)n; return s.Length > 0 ? GameData.Name(s) : ""; }
            string first = NamePart(Js.Get(n, "first")), nick = NamePart(Js.Get(n, "nick")), last = NamePart(Js.Get(n, "last"));
            string shortN = nick.Length > 0 ? nick : first;
            string outp;
            if (nick.Length > 0 && nick != first) outp = PyStr.Strip((first + " '" + nick + "' " + last).Replace("  ", " "));
            else { outp = PyStr.Strip(first + " " + last); if (outp.Length == 0) outp = shortN; }
            return GameData.Name(outp);
        }

        public static string ShortName(JObject p)
        {
            var n = Js.Get(p, "name");
            if (n != null && n.Type == JTokenType.String)
            {
                string s = (string)n;
                if (s.Length == 0) return "";
                var parts = PyStr.Split(s);
                return parts.Count == 0 ? "" : GameData.Name(parts[0]);
            }
            string raw = NamePart(Js.Get(n, "nick"));
            if (raw.Length == 0) raw = NamePart(Js.Get(n, "first"));
            return raw.Length > 0 ? GameData.Name(raw) : "";
        }

        /// <summary>{"child"|"adult": (title, desc)}; a bare string is a one-line backstory with no desc.</summary>
        public static Dictionary<string, (string Title, string Desc)> BsParts(JToken b)
        {
            var outp = new Dictionary<string, (string, string)>();
            if (b != null && b.Type == JTokenType.String)
            {
                if (((string)b).Length > 0) outp["child"] = ((string)b, "");
                return outp;
            }
            if (!(b is JObject)) return outp;
            foreach (var k in new[] { "child", "adult" })
            {
                var v = Js.Get(b, k);
                string t, d;
                if (v is JObject)
                {
                    t = Js.Truthy(Js.Get(v, "title")) ? PyJson.Str(Js.Get(v, "title")) : Js.S(Js.Get(v, "label"));
                    d = Js.Truthy(Js.Get(v, "desc")) ? PyJson.Str(Js.Get(v, "desc")) : Js.S(Js.Get(v, "description"));
                }
                else { t = Js.Truthy(v) ? PyJson.Str(v) : ""; d = ""; }
                if (t.Length > 0 || d.Length > 0) outp[k] = (t, d);
            }
            return outp;
        }

        /// <summary>One line (titles and the raw descs) for the voice-card keyword match and the facts.</summary>
        private static string Backstory(JToken b)
        {
            if (b != null && b.Type == JTokenType.String) return (string)b;
            var parts = new List<string>();
            var bp = BsParts(b);
            foreach (var (k, lab) in new[] { ("child", "Childhood"), ("adult", "Adulthood") })
            {
                (string t, string d) = bp.ContainsKey(k) ? bp[k] : ("", "");
                if (t.Length > 0 || d.Length > 0) parts.Add(lab + ": " + (d.Length > 0 ? t + ". " + d : t));
            }
            return string.Join(" ", parts);
        }

        private static string[] Cps(string s)
        {
            var l = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { l.Add(s.Substring(i, 2)); i++; }
                else l.Add(s[i].ToString());
            }
            return l.ToArray();
        }

        private static string Slice(string[] c, int a, int b)
        {
            a = Math.Max(0, Math.Min(a, c.Length)); b = Math.Max(a, Math.Min(b, c.Length));
            return string.Concat(c.Skip(a).Take(b - a));
        }

        /// <summary>At most cap characters, cut after the last sentence end (. ! ?) that fits; with none, cut at a word and add '...'.</summary>
        public static string TrimSentence(string text, int cap)
        {
            string tt = PyStr.CollapseSpaces(text ?? "");
            var t = Cps(tt);
            if (t.Length <= cap) return tt;
            string cut = Slice(t, 0, cap);
            var cc = Cps(cut);
            int lastEnd = -1;
            for (int i = 0; i < cc.Length; i++)
                if ((cc[i] == "." || cc[i] == "!" || cc[i] == "?") && i + 1 < t.Length && t[i + 1] == " ") lastEnd = i;
            if (lastEnd >= 15) return Slice(cc, 0, lastEnd + 1);
            int room = Math.Max(cap - 3, 1);
            string head;
            string headRaw = Slice(t, 0, room);
            if (room < t.Length && t[room] == " ") head = headRaw;
            else
            {
                int sp = headRaw.LastIndexOf(' ');
                string part = sp >= 0 ? headRaw.Substring(0, sp) : headRaw;
                head = part.Length > 0 ? part : headRaw;
            }
            return head.TrimEnd(' ', ',', ';', ':', '-') + "...";
        }

        public static string FirstSentence(string text)
        {
            string tt = PyStr.CollapseSpaces(text ?? "");
            var t = Cps(tt);
            for (int i = 0; i < t.Length; i++)
                if ((t[i] == "." || t[i] == "!" || t[i] == "?") && (i + 1 == t.Length || t[i + 1] == " ") && i >= 15)
                    return Slice(t, 0, i + 1);
            return tt;
        }

        /// <summary>One game string for the bio: cleaned (injection, markup), then trimmed at a sentence end to cap.</summary>
        private static string Field(JToken text, int cap, bool first = false) { return Field(Js.S(text), cap, first); }

        private static string Field(string text, int cap, bool first = false)
        {
            string t = GameData.Clean(text, "desc", Math.Max(cap * 3, 400));
            return TrimSentence(first ? FirstSentence(t) : t, cap);
        }

        public static List<string> TraitLabels(JObject p)
        {
            var r = new List<string>();
            foreach (var t in Js.ItemsGet(p, "traits"))
            {
                if (t is JObject) r.Add(Js.Truthy(Js.Get(t, "label")) ? PyJson.Str(Js.Get(t, "label")) : Js.StrOr(t, "def", ""));
                else r.Add(Js.Str(t));
            }
            return r;
        }

        public static HashSet<string> TraitDefs(JObject p)
        {
            var s = new HashSet<string>();
            foreach (var t in Js.ItemsGet(p, "traits")) s.Add((t is JObject ? Js.StrOr(t, "def", "") : Js.Str(t)).ToLowerInvariant());
            foreach (var l in TraitLabels(p)) s.Add(l.ToLowerInvariant());
            return s;
        }

        public static string SkillsText(JObject p)
        {
            var pm = new Dictionary<string, string> { { "none", "" }, { "minor", "*" }, { "major", "**" }, { "", "" } };
            var outp = new List<string>();
            foreach (var s in Js.ItemsGet(p, "skills"))
            {
                string pas = Js.StrOr(s, "passion", "");
                string mark; if (!pm.TryGetValue(pas, out mark)) mark = "";
                outp.Add(Js.Str(Js.Get(s, "skill")) + " " + Js.Str(Js.Get(s, "level")) + mark);
            }
            return string.Join(", ", outp);
        }

        private static readonly HashSet<string> RelKinds = new HashSet<string> {
            "parent", "child", "sibling", "spouse", "lover", "fiance", "fiancé", "fiancee", "ex-spouse", "ex-lover", "bond" };

        private static List<string> RelationKinds(JToken r)
        {
            var outp = new List<string>();
            foreach (var k in Js.StrOr(r, "kind", "").Split(','))
            {
                string ks = PyStr.Strip(k);
                if (RelKinds.Contains(ks.ToLowerInvariant())) outp.Add(ks);
            }
            return outp;
        }

        /// <summary>The relative's name, or "" when unknown (never a raw id).</summary>
        private static string RelativeName(JToken r, IDictionary<int, string> names)
        {
            var n = Js.Get(r, "name");
            if (Js.Truthy(n)) return (n.Type == JTokenType.String || n is JObject) ? GameData.Name(Label(n)) : "";
            var who = Js.Get(r, "pawn");
            if (who is JObject) return Label(who).Length > 0 ? GameData.Name(Label(who)) : "";
            string got;
            if (who != null && who.Type == JTokenType.Integer && names != null && names.TryGetValue((int)(long)who, out got) && !string.IsNullOrEmpty(got))
                return GameData.Name(got);
            return "";
        }

        private static JObject Ideo(JObject p) { return Js.Get(p, "ideo") as JObject ?? new JObject(); }

        private static (string, string) Meme(JToken m)
        {
            return (Label(m), m is JObject ? Js.S(Js.Get(m, "desc")) : "");
        }

        private static int CompareCp(string a, string b) { return PyJson.CompareCodePoints(a, b); }

        private static JArray SortedPairs(IEnumerable<(string, string)> items)
        {
            var l = items.ToList();
            l.Sort((x, y) => { int c = CompareCp(x.Item1, y.Item1); return c != 0 ? c : CompareCp(x.Item2, y.Item2); });
            return new JArray(l.Select(x => (JToken)new JArray(x.Item1, x.Item2)));
        }

        private static JToken Clone(JToken t) { return t == null ? JValue.CreateNull() : t.DeepClone(); }

        /// <summary>The game facts the fixed bio depends on; a change rebuilds it.</summary>
        public static JObject FactsOf(JObject p)
        {
            var h = Js.Get(p, "health");
            var bsd = new JObject();
            foreach (var kv in BsParts(Js.Get(p, "backstory"))) bsd[kv.Key] = new JArray(kv.Value.Title, kv.Value.Desc);
            var traits = SortedPairs(Js.ItemsGet(p, "traits").Select(t => (Label(t), t is JObject ? Js.Str(Js.Get(t, "desc") ?? JValue.CreateString("")) : "")));
            var missing = new JArray(Js.ItemsGet(h, "missing_parts").Select(m => Label(m)).OrderBy(x => x, Comparer<string>.Create(CompareCp)));
            var ideo = Ideo(p);
            var rels = SortedPairs(Js.ItemsGet(p, "relations").Where(r => r is JObject).Select(r =>
                (string.Join(",", RelationKinds(r)), Js.Str(Js.Truthy(Js.Get(r, "name")) ? Js.Get(r, "name") : Js.Get(r, "pawn")))));
            var f = new JObject
            {
                { "name", DisplayName(p) }, { "age", Clone(Js.Get(p, "age")) }, { "gender", Clone(Js.Get(p, "gender")) }, { "body", Clone(Js.Get(p, "body_type")) },
                { "xeno", Clone(Js.Get(p, "xenotype")) }, { "xeno_desc", Clone(Js.Get(p, "xenotype_desc")) },
                { "genes", new JArray(Js.ItemsGet(p, "genes").Select(g => Label(g))) },
                { "backstory", bsd }, { "traits", traits }, { "missing", missing },
                { "ideo_role", Clone(Js.Get(p, "ideo_role")) }, { "royal_title", Clone(Js.Get(p, "royal_title")) }, { "status", Clone(Js.Get(p, "status")) },
                { "ideo", new JObject {
                    { "name", Clone(Js.Get(ideo, "name")) },
                    { "memes", new JArray(Js.ItemsGet(ideo, "memes").Select(m => { var mm = Meme(m); return (JToken)new JArray(mm.Item1, mm.Item2); })) },
                    { "precepts", new JArray(Js.ItemsGet(ideo, "precepts").Select(x => Label(x))) } } },
                { "history", Clone(Js.Get(p, "history")) }, { "appearance", Clone(Js.Get(p, "appearance")) },
                { "relations", rels }, { "words", Register.BaseRegister(p) },
            };
            return f;
        }

        public static string FactsHash(JObject p)
        {
            string json = PyJson.Dumps(FactsOf(p), true, null, null, true);        // json.dumps(sort_keys=True), ensure_ascii on
            var hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(json));
            return string.Concat(hash.Select(b => b.ToString("x2"))).Substring(0, 12);
        }

        private static string Title(string s)
        {
            var sb = new StringBuilder();
            bool prevCased = false;
            foreach (char c in s)
            {
                bool cased = char.ToUpperInvariant(c) != c || char.ToLowerInvariant(c) != c;
                if (cased) sb.Append(prevCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)); else sb.Append(c);
                prevCased = cased;
            }
            return sb.ToString();
        }

        /// <summary>The pawn's character card: backstory, traits, ideoligion, xenotype, history. Every game string is cleaned and trimmed
        /// at a sentence end; the whole text is wrapped as data by PromptBlocks.BlockB.</summary>
        public static FixedBio BuildFixedBio(JObject p, IDictionary<int, string> names = null)
        {
            var h = Js.Get(p, "health");
            var lines = new List<string>();
            var dlc = new JObject();
            lines.Add("Name: " + DisplayName(p) + ", " + Js.Str(Js.Get(p, "age")) + ", " + Js.Str(Js.Get(p, "gender")) + ", body type " + Js.Str(Js.Get(p, "body_type")) + ".");
            if (AgeGate.IsMinor(Js.Raw(Js.Get(p, "age"))))
                lines.Add("Age group: minor, under 18 (hard fact). Nothing romantic or sexual ever involves them.");
            var bp = BsParts(Js.Get(p, "backstory"));
            foreach (var (k, lab) in new[] { ("child", "Childhood"), ("adult", "Adulthood") })
            {
                (string t, string d) = bp.ContainsKey(k) ? bp[k] : ("", "");
                string title = GameData.Clean(t, "label");
                string body = title.Length > 0 && d.Length > 0 ? title + ". " + Field(d, 300) : (title.Length > 0 ? title : Field(d, 300));
                if (body.Length > 0) lines.Add(lab + ": " + body);
            }
            var traits = new List<string>();
            foreach (var t in Js.ItemsGet(p, "traits"))
            {
                string lab = Label(t);
                if (lab.Length == 0) continue;
                lab = GameData.Clean(lab, "label");
                lab = lab != lab.ToLowerInvariant() ? lab : Title(lab);
                string d = t is JObject && Js.Truthy(Js.Get(t, "desc")) ? Field(Js.Get(t, "desc"), 160, true) : "";
                traits.Add(d.Length > 0 ? lab + " (" + d + ")" : lab);
            }
            if (traits.Count > 0) lines.Add("Traits: " + string.Join("; ", traits));
            if (Js.Truthy(Js.Get(p, "skills")))
                lines.Add("Skills (* minor passion, ** major passion): " + SkillsText(p));
            var ideo = Ideo(p);
            if (ideo.Count > 0)
            {
                var bits = new List<string>();
                if (Js.Truthy(Js.Get(ideo, "name"))) bits.Add("Ideoligion: " + GameData.Clean(Js.S(Js.Get(ideo, "name")), "label") + ".");
                var memes = new List<string>();
                foreach (var m in Js.ItemsGet(ideo, "memes").Take(8))
                {
                    var (lab0, d) = Meme(m);
                    string lab = GameData.Clean(lab0, "label");
                    if (lab.Length > 0)
                    {
                        string dd = d.Length > 0 ? Field(d, 100, true) : "";
                        memes.Add(dd.Length > 0 ? lab + " (" + dd + ")" : lab);
                    }
                }
                if (memes.Count > 0) bits.Add("Memes: " + string.Join("; ", memes) + ".");
                var precepts = Js.ItemsGet(ideo, "precepts").Take(8).Where(x => Label(x).Length > 0).Select(x => GameData.Clean(Label(x), "label")).ToList();
                if (precepts.Count > 0) bits.Add("Precepts: " + string.Join("; ", precepts) + ".");
                if (bits.Count > 0)
                {
                    lines.Add(string.Join(" ", bits));
                    dlc["ideo"] = GameData.Clean(Js.S(Js.Get(ideo, "name")), "label");
                }
            }
            if (Js.Truthy(Js.Get(p, "xenotype")))
            {
                string x = GameData.Clean(Js.Str(Js.Get(p, "xenotype")), "label");
                string xd = Js.Truthy(Js.Get(p, "xenotype_desc")) ? Field(Js.Get(p, "xenotype_desc"), 160, true) : "";
                lines.Add("Xenotype: " + x + (xd.Length > 0 ? " (" + xd + ")" : ""));
                dlc["xenotype"] = x;
                var genes = Js.ItemsGet(p, "genes").Where(g => Label(g).Length > 0).Select(g => GameData.Clean(Label(g), "label")).ToList();
                if (genes.Count > 0) { lines.Add("Genes: " + string.Join(", ", genes)); dlc["genes"] = new JArray(genes); }
            }
            if (Js.Truthy(Js.Get(p, "royal_title")))
            {
                string rt = GameData.Clean(Label(Js.Get(p, "royal_title")), "label");
                lines.Add("Noble title: " + rt);
                dlc["royal_title"] = rt;
            }
            var hist = Js.Get(p, "history") as JObject ?? new JObject();
            if (Js.Truthy(Js.Get(hist, "arrival"))) lines.Add("Joined the colony: " + Field(Js.Get(hist, "arrival"), 160));
            var tales = Js.ItemsGet(hist, "tales").Take(5).Where(t => Js.Truthy(t)).Select(t => Field(t, 120)).ToList();
            if (tales.Count > 0) lines.Add("Notable moments: " + string.Join("; ", tales.Where(t => t.Length > 0)));
            if (Js.Truthy(Js.Get(p, "appearance"))) lines.Add("Appearance: " + Field(Js.Get(p, "appearance"), 120));
            // temporary states (drunk, high, anaesthetised) live in the scene, not the frozen bio (they would go stale)
            var hl = Js.ItemsGet(h, "conditions").Where(c => !Register.IsTemporary(c)).Select(c => Label(c)).ToList();
            if (hl.Count > 0) lines.Add("Health: " + string.Join("; ", hl));
            var miss = Js.ItemsGet(h, "missing_parts").Select(m => Label(m)).ToList();
            if (miss.Count > 0) lines.Add("Missing: " + string.Join(", ", miss) + " (hard fact)");
            var status = Js.Get(p, "status");
            if (Js.Truthy(status) && !(status.Type == JTokenType.String && (string)status == "colonist"))
                lines.Add("Status: " + PyJson.Str(status) + " (hard fact)");
            if (Js.Truthy(Js.Get(p, "ideo_role")))
            {
                lines.Add("Ideology role: " + Label(Js.Get(p, "ideo_role")));
                dlc["ideo_role"] = Label(Js.Get(p, "ideo_role"));
            }
            var rel = new List<string>();
            foreach (var r in Js.ItemsGet(p, "relations"))
            {
                if (!(r is JObject)) continue;
                var kinds = RelationKinds(r);
                string who = RelativeName(r, names);
                if (kinds.Count > 0 && who.Length > 0) rel.Add(who + " (" + string.Join(", ", kinds.Select(k => k.ToLowerInvariant())) + ")");
            }
            if (rel.Count > 0) lines.Add("Key relatives: " + string.Join(", ", rel));
            dlc["words"] = Register.BaseWords(p);
            return new FixedBio { Text = string.Join("\n", lines), FactsHash = FactsHash(p), Extras = dlc };
        }

        // trait def/label (lowercase, substring) -> (register fragment, tic). A tic is a MANNER (how the pawn talks), never a topic
        // (what it talks about): a topic tic made every pawn of that trait talk about the same thing in every line.
        private static readonly (string Key, string Reg, string Tic)[] Traits = {
            ("abrasive", "blunt and needling", "Starts with 'Look.'"),
            ("kind", "warm, asks after people", "Calls people 'friend'"),
            ("psychopath", "flat and cold, little feeling", "Never apologises"),
            ("bloodlust", "relishes violence", "Grins at the rough parts"),
            ("greedy", "grasping, never satisfied", "Hedges before giving anything"),
            ("ascetic", "plain, wants little", "Says 'Enough.'"),
            ("jealous", "bitter, resentful of others", "Needles with a question"),
            ("nervous", "hesitant, trails off", "Trailing 'um...'"),
            ("tough", "stoic, understates pain", "Says 'Fine.'"),
            ("sanguine", "easy and upbeat", "Laughs it off"),
            ("pessimist", "expects the worst", "Says 'Wait for it.'"),
            ("optimist", "sees a bright side", "Says 'We'll see.'"),
            ("gourmand", "enjoys the pleasures", "Savours the word"),
            ("lazy", "slow, avoids work", "Sighs first"),
            ("industrious", "always busy, no time to spare", "Clipped sentences"),
            ("neurotic", "tense, hard on themself", "Repeats things twice"),
            ("cannibal", "unbothered by grisly things", "Dry understatement"),
            ("bookworm", "dry, bookish", "Precise wording"),
            ("wimp", "avoids pain, complains", "Winces aloud"),
            ("sickly", "tired, often unwell", "Coughs mid-sentence"),
            ("depressive", "heavy and flat", "Says 'Whatever.'"),
            ("volatile", "short-tempered", "Snaps mid-sentence"),
            ("night owl", "slow and groggy early on", "Yawns mid-sentence"),
            ("slowpoke", "slow-moving, patient", "Says 'Hold on.'"),
            ("creative", "imaginative, restless", "Odd comparisons"),
        };

        /// <summary>Deterministic: register from traits, 1-3 tics. Age and backstory fill gaps. No example lines: an example
        /// written by code is the same few sentences for every pawn; examples come only from the model or the player.</summary>
        public static VoiceCard BuildVoiceCard(JObject p)
        {
            var defs = TraitDefs(p);
            var reg = new List<string>(); var tics = new List<string>();
            foreach (var t in Traits)
                if (defs.Any(d => d.Contains(t.Key))) { reg.Add(t.Reg); tics.Add(t.Tic); }
            var ageTok = Js.Get(p, "age");
            double age = Js.Truthy(ageTok) ? Js.Dbl(ageTok) : 30;
            if (age < 18) reg.Add("young, quick, blunt about adults");
            else if (age >= 55) reg.Add("slow and weathered");
            string bs = Backstory(Js.Get(p, "backstory")).ToLowerInvariant();
            foreach (var (kw, r) in new[] { ("soldier", "clipped, military"), ("doctor", "clinical"),
                                            ("noble", "formal, a little superior"), ("slave", "guarded, short answers"), ("tribal", "plain and literal") })
                if (bs.Contains(kw)) { reg.Add(r); break; }
            string register = reg.Count > 0 ? "Comes across as " + string.Join("; ", reg.Take(3)) + "." : "Plain and brief.";
            var tl = tics.Distinct().Take(3).ToList();
            if (tl.Count == 0) tl = new List<string> { "Short sentences" };
            return new VoiceCard { RegisterLine = register, Tics = tl, Examples = new List<string>() };
        }

        /// <summary>Chattiness from traits, used by the director to scale ambient leases.</summary>
        public static double TalkWeight(JObject p)
        {
            var defs = TraitDefs(p);
            double w = 1.0;
            foreach (var (k, d) in new[] { ("kind", .3), ("sanguine", .2), ("optimist", .1), ("abrasive", .2), ("nervous", .1),
                                           ("ascetic", -.3), ("depressive", -.3), ("psychopath", -.2), ("slowpoke", -.1), ("lazy", -.1) })
                if (defs.Any(x => x.Contains(k))) w += d;
            return Math.Max(0.3, Math.Min(1.8, w));
        }

        public static string VoicePrompt(string bio, string register, string tics)
        {
            return "Write exactly 3 short lines (6 to 20 words each) of speech that this RimWorld colonist might " +
                   "say, one per line, no numbering, no quotes. Plain words, dry, no flowery language.\n" +
                   "Colonist:\n" + bio + "\nRegister: " + register + "\nHabits: " + tics + "\nLines:";
        }
    }
}

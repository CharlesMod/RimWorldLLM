// Port of agent/rimworldllm_agent/games/rimworld/textfill.py.
// Per-instance texts (artwork cards, grave inscriptions, book blurbs, social and combat log lines).
//
// The mod queues one text per thing (GET /text/queue, most important first, each with a context snapshot taken when the thing
// was made) and shows the game's own text until ours arrives (POST /text/fill). FillOnceAsync is the idle job core/idle runs
// last: queue -> a batch of up to three items of one kind -> one structured LLM call -> clean each text -> store.
// Backoff is reported to the director through host.TextFillWaitS; host.TextQueueLive tells core the queue answered.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class TextFill
    {
        public const int BatchMax = 3;
        public const int QueueN = 4;
        public const double WaitOkS = 10.0;           // after a fill: the next call may come this soon
        public const double WaitEmptyS = 30.0;        // the queue was empty
        public const double WaitUnsupportedS = 300.0; // an older mod answered 404
        public const double WaitErrorS = 30.0;

        public static readonly string SystemPrompt =
            "You write short texts for a RimWorld colony game: the words on an artwork's card, a gravestone, a book's blurb, or " +
            "one line in the social and combat log. Each text belongs to ONE specific thing, so build it from that thing's " +
            "context: who made it, what happened, what the weather was doing. Be specific and wry, in dry RimWorld humour, where " +
            "the context allows it, and plain where it does not. Never mean about real people. Plain prose only: no markup, no " +
            "quotation marks around the text, no emoji, no lists. Follow each item's length limits. A sample of the game's own " +
            "text is given only to show length and register: write something new, never copy it.\n" +
            "Names: in social and combat lines never write a literal name; refer to people only with the item's allowed " +
            "[TOKENS], written exactly as given, with square brackets, and use no other square brackets. Where an item allows no " +
            "tokens, use no square brackets at all. In art, grave and book texts the names in the context may be used.\n" +
            "Social lines are friendly and all-ages, always: nothing romantic or sexual, ever.\n" +
            "Reply with one JSON array: one object per item, {\"id\": the item's id, \"text\": ..., \"title\": ... only when the " +
            "item asks for a title}.\n\n" + GameData.DataRule;

        /// <summary>What makes each kind good, beyond "what this text is" from the mod.</summary>
        public static readonly Dictionary<string, string> KindHints = new Dictionary<string, string>
        {
            { "art", "Say what the piece shows (the tale it depicts, if any) and one concrete detail of its making; let the maker's " +
                     "mood or the day's events leak into it. The title is short and witty." },
            { "grave", "An epitaph as carved on the stone, in the voice of the people who buried them: short, may be darkly funny " +
                       "about who they were or how they died, never cruel." },
            { "book", "Keep the book's type: a novel's blurb is about its story (a plot shaped by the author's life and mood), a " +
                      "textbook or schematic is about its subject. The title fits the type." },
            { "social", "Say what was actually said or done: the topic, the jab or the kind word, concrete and drawn from the " +
                        "context. One sentence." },
            { "combat", "One sentence: the attack and its outcome, with one vivid concrete detail." },
        };
        /// <summary>These are creative texts: the structured default would make every artwork read alike.</summary>
        public const double Temperature = 0.9;

        public const string MinorLine = "A child or teenager is involved in this one: keep it innocent and wholesome, with nothing romantic, " +
                                        "sexual or frightening about them.";

        private static readonly Regex BadChars = new Regex(@"[<>{}]|\(\*", RegexOptions.CultureInvariant);
        private const string Quotes = "\"'`“”‘’«»";

        // ---- item field access (the items are the mod's JSON; Python dict.get semantics) ----
        private static JToken Tok(JObject it, string key) { JToken t; return it.TryGetValue(key, out t) ? t : null; }
        private static int IntOf(JObject it, string key, int dflt) { var t = Tok(it, key); return t == null ? dflt : Js.Int(t); }
        private static object Val(JObject it, string key, object dflt) { var t = Tok(it, key); return t == null ? dflt : Js.Raw(t); }

        /// <summary>The key a queue id is remembered under in host.TextTried (and by_id): the id's JSON text.</summary>
        public static string IdKey(JToken id) { return id == null ? "null" : id.ToString(Newtonsoft.Json.Formatting.None); }

        /// <summary>One context value as readable text: strings cleaned (they are game text), lists joined, dicts as k=v.</summary>
        private static string ValText(JToken v, int depth = 0)
        {
            if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.Undefined) return "";
            if (v.Type == JTokenType.String && (string)v == "") return "";
            if (v.Type == JTokenType.Array && !v.HasValues) return "";
            if (v.Type == JTokenType.Object && !v.HasValues) return "";
            if (v.Type == JTokenType.Boolean) return (bool)v ? "yes" : "no";
            if (v.Type == JTokenType.Object)
            {
                var parts = new List<string>();
                foreach (var kv in (JObject)v)
                {
                    string s = ValText(kv.Value, depth + 1);
                    if (s.Length > 0) parts.Add(kv.Key + "=" + s);
                }
                return string.Join(depth > 0 ? "; " : ", ", parts);
            }
            if (v.Type == JTokenType.Array)
            {
                var parts = new List<string>();
                foreach (var x in ((JArray)v).Take(8))
                {
                    string s = ValText(x, depth + 1);
                    if (s.Length > 0) parts.Add(s);
                }
                return string.Join(", ", parts);
            }
            return GameData.Clean(Js.Raw(v), "line", 160);
        }

        public static string ContextLines(JToken ctx)
        {
            var o = ctx as JObject;
            if (o == null) return Js.Truthy(ctx) ? GameData.Clean(Js.Raw(ctx), "line") : "(nothing)";
            var outl = new List<string>();
            foreach (var kv in o)
            {
                string s = ValText(kv.Value);
                if (s.Length > 0) outl.Add(GameData.Clean(kv.Key, "label", 30) + ": " + s);
            }
            string r = string.Join("\n", outl);
            return r.Length > 0 ? r : "(nothing)";
        }

        /// <summary>The first item plus up to two more of the same kind (the queue arrives sorted by priority).</summary>
        public static List<JObject> Batch(IList<JObject> items)
        {
            if (items.Count == 0) return new List<JObject>();
            var kind = Tok(items[0], "kind");
            return items.Where(i => JToken.DeepEquals(Tok(i, "kind") ?? JValue.CreateNull(), kind ?? JValue.CreateNull())).Take(BatchMax).ToList();
        }

        private static List<string> Tokens(JObject item)
        {
            var outl = new List<string>();
            var t = Tok(item, "tokens");
            if (!Js.Truthy(t)) return outl;
            foreach (var x in Js.Items(t))
            {
                string s = PyJson.Str(x).Trim('[', ']', ' ');
                if (s.Length > 0) outl.Add(s);
            }
            return outl;
        }

        private static bool WantsTitle(JObject item)
        {
            var f = Tok(item, "fields");
            if (!Js.Truthy(f)) return false;
            return Js.Items(f).Any(x => x.Type == JTokenType.String && (string)x == "title");
        }

        private static bool Minor(JObject item)
        {
            var ctx = Tok(item, "context") as JObject;
            return ctx != null && Js.Truthy(Tok(ctx, "minor_involved"));
        }

        public static List<(string Role, string Text)> Messages(IList<JObject> items)
        {
            var blocks = new System.Text.StringBuilder();
            int n = 0;
            foreach (var it in items)
            {
                n++;
                var toks = Tokens(it);
                bool title = WantsTitle(it);
                string kind = Tok(it, "kind") != null && Tok(it, "kind").Type == JTokenType.String ? (string)Tok(it, "kind") : null;
                string hint;
                if (kind == null || !KindHints.TryGetValue(kind, out hint)) hint = "Specific to the context.";
                var body = new List<string>
                {
                    "id: " + GameData.Clean(Val(it, "id", ""), "label", 80),
                    "kind: " + GameData.Clean(Val(it, "kind", ""), "label", 20),
                    "what this text is: " + GameData.Clean(Val(it, "about", ""), "line", 300),
                    "how to write it: " + hint,
                    "length: " + IntOf(it, "min_len", 0) + " to " + IntOf(it, "max_len", 300) + " characters"
                        + (title ? "; title up to " + IntOf(it, "title_max", 60) + " characters" : "; no title"),
                    "allowed tokens: " + (toks.Count > 0 ? string.Join(", ", toks.Select(t => "[" + t + "]")) : "none (no square brackets at all)"),
                };
                if (Minor(it)) body.Add(MinorLine);
                body.Add("context:\n" + ContextLines(Tok(it, "context")));
                string ex = "sample of the game's own text (length and register only): " + GameData.Clean(Val(it, "example", ""), "line", 400);
                if (title && Js.Truthy(Tok(it, "example_title"))) ex += "\nsample title: " + GameData.Clean(Js.Raw(it["example_title"]), "line", 80);
                body.Add(ex);
                blocks.Append("=== ITEM " + n + " ===\n" + GameData.DataSection("item", string.Join("\n", body)));
            }
            string req = "=== REQUEST ===\nWrite a new text for each item, specific to its context. Reply with the JSON array only.\n";
            return new List<(string, string)> { ("system", SystemPrompt), ("user", blocks + req) };
        }

        public static JObject Schema(IList<JObject> items)
        {
            var text = new JObject
            {
                ["type"] = "string",
                ["minLength"] = items.Min(i => IntOf(i, "min_len", 1)),
                ["maxLength"] = items.Max(i => IntOf(i, "max_len", 300)),
            };
            var props = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(items.Select(i => i["id"])) },
                ["text"] = text,
            };
            if (items.Any(WantsTitle))
                props["title"] = new JObject { ["type"] = "string", ["maxLength"] = items.Max(i => IntOf(i, "title_max", 60)) };
            return new JObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = items.Count,
                ["items"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray("id", "text") },
            };
        }

        private static string Tidy(JToken s)
        {
            string t = PyStr.CollapseSpaces(Js.S(s));
            while (PyStr.Len(t) >= 2 && Quotes.IndexOf(t[0]) >= 0 && Quotes.IndexOf(t[t.Length - 1]) >= 0)
                t = PyStr.Strip(t.Substring(1, t.Length - 2));
            return t;
        }

        private static bool BracketsOk(string t, List<string> tokens)
        {
            foreach (var tok in tokens) t = t.Replace("[" + tok + "]", "");
            return t.IndexOf('[') < 0 && t.IndexOf(']') < 0;
        }

        /// <summary>(text, title) ready to store, or null when the text must not be used.</summary>
        public static (string Text, string Title)? Clean(JObject item, JToken obj)
        {
            var o = obj as JObject;
            if (o == null) return null;
            var toks = Tokens(item);
            string text = Tidy(Tok(o, "text"));
            int len = PyStr.Len(text);
            if (!(IntOf(item, "min_len", 0) <= len && len <= IntOf(item, "max_len", 300))) return null;
            string title = null;
            if (WantsTitle(item))
            {
                title = Tidy(Tok(o, "title"));
                if (title.Length == 0 || PyStr.Len(title) > IntOf(item, "title_max", 60)) return null;
            }
            foreach (var t in new[] { text, title ?? "" })
                if (BadChars.IsMatch(t) || !BracketsOk(t, toks)) return null;
            var kindTok = Tok(item, "kind");
            bool social = kindTok != null && kindTok.Type == JTokenType.String && (string)kindTok == "social";
            if ((social || Minor(item)) && (AgeGate.Romantic(text) || AgeGate.Romantic(title ?? ""))) return null;
            return (text, title);
        }

        private static List<JObject> Parse(string raw)
        {
            JToken v;
            if (!PyJson.TryLoads(raw ?? "", out v)) return new List<JObject>();
            if (v is JObject) return new List<JObject> { (JObject)v };
            var a = v as JArray;
            return a == null ? new List<JObject>() : a.OfType<JObject>().ToList();
        }

        /// <summary>One pass: fetch, write one batch, store. Returns the number of texts stored. Sets host.TextFillWaitS.</summary>
        public static async Task<int> FillOnceAsync(ITextFillHost d, int slot)
        {
            d.TextFillWaitS = WaitOkS;
            JObject q;
            try { q = await d.Bridge.TextQueueAsync(QueueN).ConfigureAwait(false); }
            catch (BridgeError e)
            {
                if (e.Status == 404)
                {
                    d.TextQueueLive = false;
                    d.TextFillWaitS = WaitUnsupportedS;
                }
                else
                {
                    LlmLog.Warning("rimworldllm", "text queue read failed: " + e.Message);
                    d.TextFillWaitS = WaitErrorS;
                }
                return 0;
            }
            d.TextQueueLive = true;
            var tried = d.TextTried;           // ids written once are never asked for again this session
            if (tried.Count > 2000) tried.Clear();
            var items = new List<JObject>();
            var raw = Tok(q, "items");
            if (Js.Truthy(raw))
                foreach (var i in Js.Items(raw))
                {
                    var o = i as JObject;
                    if (o != null && Js.Truthy(Tok(o, "id")) && !tried.Contains(IdKey(o["id"]))) items.Add(o);
                }
            var group = Batch(items);
            if (group.Count == 0)
            {
                d.TextFillWaitS = WaitEmptyS;
                return 0;
            }
            int maxTokens = Math.Min(1500, group.Sum(i => IntOf(i, "max_len", 300) / 3 + 64));
            var msgs = Messages(group).Select(m => new LlmMessage(m.Role, m.Text)).ToList();
            var c = await d.Llm.CompleteAsync(msgs, slot, "textfill", maxTokens, new string[0], null, Temperature, null, Schema(group)).ConfigureAwait(false);   // LlmError goes up: the idle job backs off
            foreach (var i in group) tried.Add(IdKey(i["id"]));
            var byId = new Dictionary<string, JObject>();
            foreach (var i in group) byId[IdKey(i["id"])] = i;
            var done = new HashSet<string>();
            int stored = 0;
            foreach (var obj in Parse(c.Text))
            {
                var idTok = Tok(obj, "id");
                JObject item;
                if (idTok == null || !byId.TryGetValue(IdKey(idTok), out item)) continue;
                string key = IdKey(item["id"]);
                if (done.Contains(key)) continue;
                done.Add(key);
                var got = Clean(item, obj);
                if (got == null)
                {
                    d.Dropped.Add((-2, "textfill:" + Js.Str(Tok(item, "kind")) + ":rejected"));
                    continue;
                }
                try
                {
                    await d.Bridge.TextFillAsync(PyJson.Str(item["id"]), got.Value.Text, got.Value.Title).ConfigureAwait(false);
                    stored++;
                }
                catch (BridgeError e)
                {
                    LlmLog.Debug("rimworldllm", "text " + PyJson.Str(item["id"]) + " not stored: " + e.Message);   // unknown_id, bad_text: move on, never retry the id
                }
            }
            return stored;
        }
    }
}

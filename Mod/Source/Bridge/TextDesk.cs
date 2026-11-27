using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One kind of per-instance text: an artwork, a grave, a book, a social or combat log line.</summary>
    public sealed class TextKind
    {
        public string Id;
        public int Prio;            // higher is served first and dropped last
        public bool WantsTitle;
        public int MinLen, MaxLen, TitleMax;
        public string[] Tokens;     // [TOKEN]s the text may carry; the game fills them with names when it shows the text
        public string About;        // what the text is, for the prompt
        public int StoreCap;        // texts of this kind kept (oldest dropped)
        public bool DefaultOn;
    }

    public static class TextKinds
    {
        public const string Art = "art", Grave = "grave", Book = "book", Social = "social", Combat = "combat";

        public static readonly TextKind[] All =
        {
            new TextKind { Id = Art, Prio = 3, WantsTitle = true, MinLen = 40, MaxLen = 500, TitleMax = 60, Tokens = new string[0], StoreCap = 3000, DefaultOn = true,
                About = "an artwork's title and the description on its info card: what the piece shows and how it is made, as an art critic would put it" },
            new TextKind { Id = Grave, Prio = 3, MinLen = 10, MaxLen = 200, Tokens = new string[0], StoreCap = 1000, DefaultOn = true,
                About = "the epitaph carved on a grave for the person buried in it (one or two lines)" },
            new TextKind { Id = Book, Prio = 3, WantsTitle = true, MinLen = 60, MaxLen = 600, TitleMax = 80, Tokens = new string[0], StoreCap = 2000, DefaultOn = true,
                About = "a book's title and the blurb on its info card (what it is about and what it is like to read)" },
            new TextKind { Id = Social, Prio = 1, MinLen = 15, MaxLen = 220, Tokens = new[] { "INITIATOR", "RECIPIENT" }, StoreCap = 600, DefaultOn = true,
                About = "one line in the social log saying what one person said or did to another in this interaction, past tense" },
            new TextKind { Id = Combat, Prio = 0, MinLen = 15, MaxLen = 200, Tokens = new[] { "INITIATOR", "RECIPIENT" }, StoreCap = 300, DefaultOn = false,
                About = "one line in the combat log describing this attack and its outcome, past tense" },
        };

        public static TextKind Get(string id)
        {
            foreach (var k in All) if (k.Id == id) return k;
            return null;
        }
    }

    /// <summary>A text the game wants written: one instance, a context snapshot taken when it was made, the game's own text.</summary>
    public sealed class TextItem
    {
        public string Id;           // "art:123", "grave:456", "book:789", "log:1011"
        public string Kind;
        public long Seq;
        public int Tick;            // game tick when queued
        public int Served;          // times handed out by GET /text/queue
        public JObject Context = new JObject();
        public string Example = "", ExampleTitle = "";
    }

    public sealed class TextEntry
    {
        public string Id, Kind, Text, Title;
        public int Tick;
    }

    /// <summary>Validation for incoming texts: tidy, bounds, no markup, only the kind's [TOKENS].</summary>
    public static class TextValidate
    {
        static readonly Regex Space = new Regex(@"\s+");
        static readonly Regex Bad = new Regex(@"[<>{}]|\(\*|\(/");
        const string Quotes = "\"'`“”‘’«»";

        public static string Tidy(string s)
        {
            string t = Space.Replace(s ?? "", " ").Trim();
            while (t.Length >= 2 && Quotes.IndexOf(t[0]) >= 0 && Quotes.IndexOf(t[t.Length - 1]) >= 0) t = t.Substring(1, t.Length - 2).Trim();
            return t;
        }

        /// <summary>The cleaned text, or null with a reason (too_short, too_long, markup, brackets).</summary>
        public static string Clean(string text, int min, int max, string[] tokens, out string reason)
        {
            reason = null;
            string t = Tidy(text);
            if (t.Length < min) { reason = "too_short"; return null; }
            if (t.Length > max) { reason = "too_long"; return null; }
            if (Bad.IsMatch(t)) { reason = "markup"; return null; }
            string rest = t;
            foreach (var tok in tokens ?? new string[0]) rest = rest.Replace("[" + tok + "]", "");
            if (rest.IndexOf('[') >= 0 || rest.IndexOf(']') >= 0) { reason = "brackets"; return null; }
            return t;
        }
    }

    public static class TextTokens
    {
        /// <summary>Puts names in for [TOKEN]s; a token without a value becomes "someone".</summary>
        public static string Fill(string text, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0) return text;
            return Regex.Replace(text, @"\[([A-Z_]+)\]", m =>
            {
                string v;
                return values != null && values.TryGetValue(m.Groups[1].Value, out v) && !string.IsNullOrEmpty(v) ? v : "someone";
            });
        }
    }

    /// <summary>The per-instance text desk: the queue of texts wanted, the texts written, and the ids already asked for.
    /// The game queues an item when the thing is made (or first shown), returns its own text until a text is stored here,
    /// and then shows ours. Game thread only. Saved with the game by the host (Export/Import).</summary>
    public sealed class TextDesk
    {
        public const int QueueCap = 40;
        public const int TtlTicks = 120000;      // two game days in the queue at most
        public const int MaxServed = 3;          // handed out this often without a fill: dropped
        public const int TriedCap = 20000;

        public readonly List<TextItem> Queue = new List<TextItem>();
        readonly Dictionary<string, TextEntry> _book = new Dictionary<string, TextEntry>();
        readonly LinkedList<string> _bookOrder = new LinkedList<string>();
        readonly HashSet<string> _tried = new HashSet<string>();
        readonly Queue<string> _triedOrder = new Queue<string>();
        long _seq;

        /// <summary>Whether a kind is switched on (the settings). Off kinds are never queued, served or shown.</summary>
        public Func<string, bool> Enabled = k => true;

        public int Dropped;    // items dropped by the cap (diagnostics)

        public bool Tried(string id) { return _tried.Contains(id); }

        public TextEntry Get(string id)
        {
            TextEntry e;
            return id != null && _book.TryGetValue(id, out e) && Enabled(e.Kind) ? e : null;
        }

        /// <summary>Queues a text for one instance, once per id ever. A full queue drops its oldest lowest-priority item,
        /// or the new item when that is lower than everything queued. Returns whether it was queued.</summary>
        public bool Enqueue(TextItem item, int nowTick)
        {
            var kind = item == null ? null : TextKinds.Get(item.Kind);
            if (kind == null || string.IsNullOrEmpty(item.Id) || !Enabled(item.Kind) || _tried.Contains(item.Id) || _book.ContainsKey(item.Id)) return false;
            MarkTried(item.Id);
            Prune(nowTick);
            if (Queue.Count >= QueueCap)
            {
                int low = Queue.Min(i => Prio(i));
                if (kind.Prio < low) { Dropped++; return false; }
                var victim = Queue.Where(i => Prio(i) == low).OrderBy(i => i.Seq).First();
                Queue.Remove(victim);
                Dropped++;
            }
            item.Seq = ++_seq;
            item.Tick = nowTick;
            Queue.Add(item);
            return true;
        }

        static int Prio(TextItem i) { var k = TextKinds.Get(i.Kind); return k == null ? -1 : k.Prio; }

        void MarkTried(string id)
        {
            if (!_tried.Add(id)) return;
            _triedOrder.Enqueue(id);
            while (_triedOrder.Count > TriedCap) _tried.Remove(_triedOrder.Dequeue());
        }

        void Prune(int nowTick)
        {
            Queue.RemoveAll(i => nowTick - i.Tick > TtlTicks || i.Served >= MaxServed || !Enabled(i.Kind));
        }

        /// <summary>The next items to write, most important first (priority, then oldest). Each counts as served once.</summary>
        public List<TextItem> Next(int n, int nowTick)
        {
            Prune(nowTick);
            var list = Queue.OrderByDescending(Prio).ThenBy(i => i.Seq).Take(Math.Max(0, n)).ToList();
            foreach (var i in list) i.Served++;
            return list;
        }

        public TextItem Find(string id) { return Queue.FirstOrDefault(i => i.Id == id); }

        /// <summary>Stores a text for a queued item after validation. Error codes: unknown_id, bad_text (reason in detail).</summary>
        public TextEntry Fill(string id, string text, string title, int nowTick, out string error, out string detail)
        {
            error = detail = null;
            var item = Find(id);
            var kind = item == null ? null : TextKinds.Get(item.Kind);
            if (item == null || kind == null || !Enabled(item.Kind)) { error = "unknown_id"; detail = "no queued text " + id; return null; }
            string reason;
            string t = TextValidate.Clean(text, kind.MinLen, kind.MaxLen, kind.Tokens, out reason);
            if (t == null) { error = "bad_text"; detail = "text: " + reason; return null; }
            string ti = null;
            if (kind.WantsTitle && !string.IsNullOrWhiteSpace(title))
            {
                ti = TextValidate.Clean(title, 1, kind.TitleMax, new string[0], out reason);
                if (ti == null) { error = "bad_text"; detail = "title: " + reason; return null; }
            }
            var e = new TextEntry { Id = id, Kind = item.Kind, Text = t, Title = ti, Tick = nowTick };
            Queue.Remove(item);
            Store(e);
            return e;
        }

        void Store(TextEntry e)
        {
            if (_book.ContainsKey(e.Id)) _bookOrder.Remove(e.Id);
            _book[e.Id] = e;
            _bookOrder.AddLast(e.Id);
            var kind = TextKinds.Get(e.Kind);
            int cap = kind == null ? 100 : kind.StoreCap;
            if (Count(e.Kind) <= cap) return;
            for (var node = _bookOrder.First; node != null; node = node.Next)
            {
                if (_book[node.Value].Kind != e.Kind) continue;
                _book.Remove(node.Value);
                _bookOrder.Remove(node);
                break;
            }
        }

        public int Count(string kind) { return _book.Values.Count(e => e.Kind == kind); }
        public int Queued(string kind) { return Queue.Count(i => i.Kind == kind); }

        public void Clear()
        {
            Queue.Clear(); _book.Clear(); _bookOrder.Clear(); _tried.Clear(); _triedOrder.Clear(); Dropped = 0;
        }

        /// <summary>Wire shape of a queued item (GET /text/queue).</summary>
        public static JObject Wire(TextItem i)
        {
            var k = TextKinds.Get(i.Kind);
            var o = new JObject
            {
                ["id"] = i.Id, ["kind"] = i.Kind, ["prio"] = k.Prio,
                ["fields"] = k.WantsTitle ? new JArray("title", "text") : new JArray("text"),
                ["min_len"] = k.MinLen, ["max_len"] = k.MaxLen,
                ["tokens"] = new JArray(k.Tokens), ["about"] = k.About,
                ["example"] = i.Example ?? "", ["context"] = i.Context ?? new JObject()
            };
            if (k.WantsTitle) { o["title_max"] = k.TitleMax; o["example_title"] = i.ExampleTitle ?? ""; }
            return o;
        }

        public JObject Export()
        {
            return new JObject
            {
                ["seq"] = _seq,
                ["queue"] = new JArray(Queue.Select(i => new JObject
                {
                    ["id"] = i.Id, ["kind"] = i.Kind, ["seq"] = i.Seq, ["tick"] = i.Tick, ["served"] = i.Served,
                    ["context"] = i.Context, ["example"] = i.Example, ["example_title"] = i.ExampleTitle
                })),
                ["texts"] = new JArray(_bookOrder.Select(id => _book[id]).Select(e => new JObject
                {
                    ["id"] = e.Id, ["kind"] = e.Kind, ["text"] = e.Text, ["title"] = e.Title, ["tick"] = e.Tick
                })),
                ["tried"] = new JArray(_triedOrder.Skip(Math.Max(0, _triedOrder.Count - 5000)))
            };
        }

        public void Import(JObject o)
        {
            Clear();
            if (o == null) return;
            _seq = o.Value<long?>("seq") ?? 0;
            foreach (var t in (o["tried"] as JArray) ?? new JArray()) MarkTried((string)t);
            foreach (var t in (o["texts"] as JArray) ?? new JArray())
            {
                var e = new TextEntry { Id = (string)t["id"], Kind = (string)t["kind"], Text = (string)t["text"], Title = (string)t["title"], Tick = t.Value<int?>("tick") ?? 0 };
                if (!string.IsNullOrEmpty(e.Id) && TextKinds.Get(e.Kind) != null && !string.IsNullOrEmpty(e.Text)) Store(e);
            }
            foreach (var t in (o["queue"] as JArray) ?? new JArray())
            {
                var i = new TextItem
                {
                    Id = (string)t["id"], Kind = (string)t["kind"], Seq = t.Value<long?>("seq") ?? 0, Tick = t.Value<int?>("tick") ?? 0,
                    Served = t.Value<int?>("served") ?? 0, Context = t["context"] as JObject ?? new JObject(),
                    Example = (string)t["example"] ?? "", ExampleTitle = (string)t["example_title"] ?? ""
                };
                if (!string.IsNullOrEmpty(i.Id) && TextKinds.Get(i.Kind) != null && Queue.Count < QueueCap) { Queue.Add(i); MarkTried(i.Id); }
            }
        }
    }

    /// <summary>Puts a stored text onto the game's instance (book fields, art title, a log entry's cache). Returns null when
    /// done, or an error code ("gone") when the instance no longer exists.</summary>
    public interface ITextApplier
    {
        string Apply(TextEntry entry);
    }

    /// <summary>GET /text/queue and POST /text/fill.</summary>
    public static class TextRoutes
    {
        public const int MaxN = 8;

        public static void Register(ApiRouter r, Func<TextDesk> desk, Func<ITextApplier> applier, Func<int> nowTick)
        {
            r.Add("GET", "/text/queue", true, c =>
            {
                int n = 4;
                string q = c.Q("n");
                if (!string.IsNullOrEmpty(q) && (!int.TryParse(q, out n) || n < 1)) return ApiResult.Fail("bad_request", "n must be a positive integer");
                var d = desk();
                var items = d.Next(Math.Min(n, MaxN), nowTick());
                return ApiResult.Success(new JObject { ["pending"] = d.Queue.Count, ["items"] = new JArray(items.Select(TextDesk.Wire)) });
            });
            r.Add("POST", "/text/fill", true, c =>
            {
                var b = c.Body;
                string id = b["id"] != null && b["id"].Type == JTokenType.String ? (string)b["id"] : null;
                if (string.IsNullOrEmpty(id)) return ApiResult.Fail("bad_request", "id (a string from GET /text/queue) is required");
                if (b["text"] == null || b["text"].Type != JTokenType.String) return ApiResult.Fail("bad_request", "text (a string) is required");
                string title = b["title"] != null && b["title"].Type == JTokenType.String ? (string)b["title"] : null;
                string err, detail;
                var e = desk().Fill(id, (string)b["text"], title, nowTick(), out err, out detail);
                if (e == null) return ApiResult.Fail(err, detail);
                string applied = applier() == null ? null : applier().Apply(e);
                return ApiResult.Success(new JObject { ["stored"] = true, ["applied"] = applied == null, ["text"] = e.Text, ["title"] = e.Title });
            });
        }
    }
}

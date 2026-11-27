// Port of agent/rimworldllm_agent/core/chronicle.py: the narrator's community chronicle (SPEC section 7, milestone 5).
// A paragraph per game day, or at once on a major event, written from the ledger and the day's notable events. The old
// chronicle is rolled into a running summary by self-summary (RimGPT style), so the narrator's context stays under a fixed
// token budget. Nothing new, nothing written. The narrator never proposes or triggers events: it only describes.
// Wording comes from the adapter. chronicle.json / chronicle.jsonl / chronicle.md keep the Python formats (append-only logs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The adapter wording the chronicle needs (adapter.chronicle_request / chronicle_system / chronicle_summary_system).</summary>
    public interface IChronicleAdapter
    {
        string ChronicleRequest { get; }
        string ChronicleSystem { get; }
        string ChronicleSummarySystem { get; }
    }

    /// <summary>pydantic Note: value equality like a pydantic model (the Python removes notes with `n not in notes`).</summary>
    public sealed class ChronicleNote : IEquatable<ChronicleNote>
    {
        [JsonProperty("day"), PyRequired] public int Day { get; set; }
        [JsonProperty("game"), PyRequired] public long Game { get; set; }
        [JsonProperty("text"), PyRequired] public string Text { get; set; }
        [JsonProperty("severity")] public double Severity { get; set; } = 0.3;
        [JsonProperty("major")] public bool Major { get; set; }

        public bool Equals(ChronicleNote o)
        {
            return o != null && Day == o.Day && Game == o.Game && Text == o.Text && Severity.Equals(o.Severity) && Major == o.Major;
        }
        public override bool Equals(object o) { return Equals(o as ChronicleNote); }
        public override int GetHashCode() { return Day ^ Game.GetHashCode() ^ (Text ?? "").GetHashCode(); }
    }

    public sealed class ChronicleEntry
    {
        [JsonProperty("day"), PyRequired] public int Day { get; set; }
        [JsonProperty("game"), PyRequired] public long Game { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "day";      // "day" or "major"
        [JsonProperty("text"), PyRequired] public string Text { get; set; }
    }

    public sealed class ChronicleState
    {
        [JsonProperty("summary")] public string Summary { get; set; } = "";
        [JsonProperty("recent")] public List<ChronicleEntry> Recent { get; set; } = new List<ChronicleEntry>();
        [JsonProperty("pending")] public List<ChronicleNote> Pending { get; set; } = new List<ChronicleNote>();
        [JsonProperty("last_day")] public int LastDay { get; set; } = -1;     // newest day an entry covers
    }

    /// <summary>The next entry that is due: its kind, day and the notes it covers.</summary>
    public sealed class ChronicleDue
    {
        public string Kind;
        public int Day;
        public List<ChronicleNote> Notes;
    }

    public sealed class Chronicle
    {
        public const int KeepRecent = 3;                  // entries kept verbatim beside the running summary
        public const int RecentBudgetTokens = 1200;       // roll the oldest recent entry into the summary past this
        public const int SummaryMaxWords = 250;
        public const int ReserveTokens = 300;             // left for the completion inside the context budget

        public static int EstTokens(string text) { return PyStr.Len(text) / 4 + 1; }
        public static int MessagesTokens(IEnumerable<LlmMessage> msgs) { return msgs.Sum(m => EstTokens(m.Text) + 4); }

        private static readonly Regex SentenceSplit = new Regex(@"(?<=[.!?])\s", RegexOptions.CultureInvariant);

        public readonly string Dir;
        public readonly long UnitsPerDay;
        public string LastDrop = "";                      // why the last write was dropped by the sanitiser (read by idle)
        public ChronicleState State = new ChronicleState();
        public readonly List<ChronicleEntry> Log = new List<ChronicleEntry>();    // everything written this run (the file keeps all of it)

        public Chronicle(string dataDir = null, long unitsPerDay = 60000)
        {
            Dir = string.IsNullOrEmpty(dataDir) ? null : dataDir;
            UnitsPerDay = unitsPerDay;
            Load();
        }

        // ---- storage ----------------------------------------------------------
        public void Load()
        {
            if (Dir == null) return;
            try { State = PyModel.Validate<ChronicleState>(PyJson.Loads(File.ReadAllText(Path.Combine(Dir, "chronicle.json"), Encoding.UTF8))); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PyValidationException
                                      || e is PyJsonException || e is JsonException || e is FormatException)
            { State = new ChronicleState(); }
        }

        public void Save()
        {
            if (Dir == null) return;
            Directory.CreateDirectory(Dir);
            string tmp = Path.Combine(Dir, "chronicle.json.tmp");
            File.WriteAllText(tmp, PyModel.Compact(PyModel.Dump(State)), new UTF8Encoding(false));
            string dst = Path.Combine(Dir, "chronicle.json");
            File.Move(tmp, dst, true);
        }

        private static string EntryJson(ChronicleEntry e) { return PyModel.Compact(PyModel.Dump(e)); }

        private void AppendFile(ChronicleEntry e)
        {
            if (Dir == null) return;
            Directory.CreateDirectory(Dir);
            var utf8 = new UTF8Encoding(false);
            File.AppendAllText(Path.Combine(Dir, "chronicle.jsonl"), EntryJson(e) + "\n", utf8);
            File.AppendAllText(Path.Combine(Dir, "chronicle.md"),
                "## Day " + (e.Day + 1) + (e.Kind == "major" ? " (major event)" : "") + "\n\n" + e.Text + "\n\n", utf8);
        }

        private static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        /// <summary>The world was loaded from an earlier save: forget what has not happened yet.</summary>
        public void Rollback(long game)
        {
            var st = State;
            st.Recent = st.Recent.Where(e => e.Game <= game).ToList();
            st.Pending = st.Pending.Where(n => n.Game <= game).ToList();
            st.LastDay = st.Recent.Count > 0 ? st.Recent.Max(e => e.Day) : (int)Math.Min((long)st.LastDay, FloorDiv(game, UnitsPerDay));
            Save();
        }

        // ---- input ------------------------------------------------------------
        public void Note(long game, string text, double severity = 0.3, bool major = false)
        {
            State.Pending.Add(new ChronicleNote { Day = (int)FloorDiv(game, UnitsPerDay), Game = game, Text = text, Severity = severity, Major = major });
            State.Pending = Grounding.Tail(State.Pending, 200);
        }

        /// <summary>The next entry (kind, day, notes), or null when nothing new is waiting.</summary>
        public ChronicleDue Due(int today)
        {
            var p = State.Pending;
            if (p.Count == 0) return null;
            if (p.Any(n => n.Major)) return new ChronicleDue { Kind = "major", Day = p.Max(n => n.Day), Notes = p.ToList() };
            var past = p.Where(n => n.Day < today).ToList();
            if (past.Count > 0)
            {
                int d = past.Min(n => n.Day);
                return new ChronicleDue { Kind = "day", Day = d, Notes = p.Where(n => n.Day <= d).ToList() };
            }
            return null;
        }

        // ---- writing ----------------------------------------------------------
        private static ChronicleNote LeastSevere(List<ChronicleNote> notes)
        {
            ChronicleNote best = null;
            foreach (var n in notes)
            {
                if (best == null) { best = n; continue; }
                int c = n.Major.CompareTo(best.Major);
                if (c == 0) c = n.Severity.CompareTo(best.Severity);
                if (c == 0) c = (-n.Game).CompareTo(-best.Game);
                if (c < 0) best = n;
            }
            return best;
        }

        private static string TailCp(string s, int n)
        {
            int len = PyStr.Len(s);
            if (n >= len) return s;
            int skip = len - n;                          // code points to drop from the front
            int i = 0;
            while (skip > 0) { i += (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) ? 2 : 1; skip--; }
            return s.Substring(i);
        }

        /// <summary>System + summary + recent entries + open matters + the events, trimmed to fit budget tokens.</summary>
        public List<LlmMessage> BuildMessages(IChronicleAdapter adapter, string kind, int day, IList<ChronicleNote> notesIn,
                                              IList<string> ledgerLines, int budget, IList<string> loreLines = null)
        {
            var notes = notesIn.Select((n, i) => (n, i)).OrderBy(t => t.n.Game).ThenBy(t => t.i).Select(t => t.n).ToList();
            var recent = State.Recent.ToList();
            string summary = State.Summary;
            var ledger = ledgerLines.Take(12).ToList();
            var lore = loreLines ?? new List<string>();
            string names = string.Join("\n", Grounding.Tail(lore, 12).Select(t => "- " + GameData.Clean(t, "line", 100)));

            Func<List<LlmMessage>> build = () =>
            {
                string ev = string.Join("\n", notes.Select(n => "- " + GameData.Clean(n.Text, "event")));
                if (ev.Length == 0) ev = "- (nothing noted)";
                string led = string.Join("\n", ledger.Select(l => "- " + GameData.Clean(l, "event")));
                if (led.Length == 0) led = "- nothing ongoing";
                string rec = string.Join("\n\n", recent.Select(e => "Day " + (e.Day + 1) + ": " + e.Text));
                if (rec.Length == 0) rec = "(none yet)";
                string user = "=== THE STORY SO FAR ===\n" + (summary.Length > 0 ? summary : "(the chronicle begins)") + "\n"
                    + "=== RECENT ENTRIES ===\n" + rec + "\n"
                    + "=== ONGOING MATTERS ===\n" + GameData.DataSection("ongoing", led)
                    + "=== WHAT HAPPENED (day " + (day + 1) + ") ===\n" + GameData.DataSection("events", ev)
                    + (names.Length > 0 ? "=== SHARED NAMES (use these exact names when you refer to them) ===\n" + GameData.DataSection("names", names) : "")
                    + "=== REQUEST ===\n" + adapter.ChronicleRequest;
                return new List<LlmMessage>
                {
                    new LlmMessage("system", adapter.ChronicleSystem + "\n\n" + GameData.DataRule),
                    new LlmMessage("user", user),
                };
            };

            int limit = budget - ReserveTokens;
            while (MessagesTokens(build()) > limit)
            {
                if (notes.Count > 6) notes.Remove(LeastSevere(notes));          // drop the least severe event first
                else if (ledger.Count > 0) ledger.RemoveAt(ledger.Count - 1);
                else if (recent.Count > 0) recent.RemoveAt(0);
                else if (notes.Count > 1) notes.Remove(LeastSevere(notes));
                else if (summary.Length > 0) summary = TailCp(summary, Math.Max(200, limit * 2));
                else break;
            }
            return build();
        }

        public async Task<ChronicleEntry> WriteAsync(ILlmClient llm, IChronicleAdapter adapter, int slot, int today,
                                                     IList<string> ledgerLines, IDictionary<int, HashSet<string>> nameTokens,
                                                     int budget = 6000, int maxTokens = 220, ISet<string> commonWords = null,
                                                     ISet<string> minorNames = null, IList<string> loreLines = null)
        {
            var d = Due(today);
            if (d == null) return null;
            var msgs = BuildMessages(adapter, d.Kind, d.Day, d.Notes, ledgerLines, budget, loreLines);
            Completion c;
            try
            {
                c = await llm.CompleteAsync(msgs, slot, "chronicle", maxTokens, new string[0], null, 0.7).ConfigureAwait(false);
            }
            catch (LlmError) { return null; }
            var allnames = new HashSet<string>();
            if (nameTokens != null) foreach (var v in nameTokens.Values) allnames.UnionWith(v);
            var s = Sanitize.SanitizeProse(c.Text, allnames, allnames, Sanitize.PromptCapWords(msgs[1].Text),
                                           commonWords, null, 8, 150, false, minorNames);
            if (!s.Ok)
            {
                LastDrop = string.IsNullOrEmpty(s.Reason) ? "sanitize" : s.Reason;
                return null;
            }
            long game = d.Notes.Max(n => n.Game);
            var e = new ChronicleEntry { Day = d.Day, Game = game, Kind = d.Kind, Text = s.Text };
            var st = State;
            st.Pending = st.Pending.Where(n => !d.Notes.Contains(n)).ToList();
            st.Recent.Add(e);
            st.LastDay = Math.Max(st.LastDay, d.Day);
            Log.Add(e);
            AppendFile(e);
            Save();
            await RollAsync(llm, adapter, slot).ConfigureAwait(false);
            return e;
        }

        /// <summary>Fold the oldest recent entries into the running summary so the context stays bounded.</summary>
        public async Task RollAsync(ILlmClient llm, IChronicleAdapter adapter, int slot)
        {
            var st = State;
            while (st.Recent.Count > KeepRecent || (st.Recent.Count > 1 && st.Recent.Sum(e => EstTokens(e.Text)) > RecentBudgetTokens))
            {
                var old = st.Recent[0];
                st.Recent.RemoveAt(0);
                st.Summary = await FoldAsync(llm, adapter, slot, st.Summary, old).ConfigureAwait(false);
            }
            if (PyStr.Split(st.Summary).Count > SummaryMaxWords * 1.5)
                st.Summary = await FoldAsync(llm, adapter, slot, st.Summary, null).ConfigureAwait(false);
            Save();
        }

        private async Task<string> FoldAsync(ILlmClient llm, IChronicleAdapter adapter, int slot, string summary, ChronicleEntry entry)
        {
            var parts = new List<string> { "=== SUMMARY SO FAR ===\n" + (summary.Length > 0 ? summary : "(none)") };
            if (entry != null) parts.Add("=== ENTRY TO FOLD IN (day " + (entry.Day + 1) + ") ===\n" + entry.Text);
            parts.Add("=== REQUEST ===\nWrite the new running summary in at most " + SummaryMaxWords + " words, oldest events shortest. One paragraph, no lists.");
            try
            {
                var c = await llm.CompleteAsync(new List<LlmMessage> { new LlmMessage("system", adapter.ChronicleSummarySystem), new LlmMessage("user", string.Join("\n", parts)) },
                                                slot, "chronicle_summary", SummaryMaxWords * 2, new string[0], null, 0.3).ConfigureAwait(false);
                string t = PyStr.CollapseSpaces(c.Text);
                var w = PyStr.Split(t);
                if (w.Count >= 5) return string.Join(" ", w.Take((int)(SummaryMaxWords * 1.3)));
            }
            catch (LlmError) { }
            // fallback without the model: keep the tail of the old summary plus the entry's first sentence
            string first = entry != null ? SentenceSplit.Split(entry.Text)[0] : "";
            var joined = PyStr.Split(PyStr.Strip(summary + " " + first));
            return string.Join(" ", Grounding.Tail(joined, SummaryMaxWords));
        }
    }
}

// Port of agent/rimworldllm_agent/core/creative.py: creative pieces: optional plan-then-write, best-of-N with a pick, and a
// stock-phrase filter. The caller owns the model call (write): it builds the request, sanitises and age-gates the text and
// returns it or null. Compose only decides how many times to call it, with what extra note, and which result to keep.
// Nothing here is shown in game; a plan stays private. With every flag off (creative_plan false, creative_best_of 1,
// cliche_retry false) compose calls write("") once.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The creative_* settings creative.py reads off the config (getattr with defaults, so a bare object is all-off).</summary>
    public sealed class CreativeSettings
    {
        public bool Ground, Plan, ClicheRetry;
        public int BestOf = 1;
        public int BestOfN = 4;
        public string[] GroundFor = new string[0], PlanFor = new string[0], BestOfFor = new string[0];
        public string Rerank = "auto";

        public static CreativeSettings From(MindConfig c)
        {
            return new CreativeSettings
            {
                Ground = c.CreativeGround, Plan = c.CreativePlan, ClicheRetry = c.ClicheRetry,
                BestOf = c.CreativeBestOf, BestOfN = c.CreativeBestOfN,
                GroundFor = c.CreativeFor("ground"), PlanFor = c.CreativeFor("plan"), BestOfFor = c.CreativeFor("best_of"),
                Rerank = c.CreativeRerank,
            };
        }
    }

    /// <summary>What creative.py uses of the director (d.llm, d.cfg, d.creative_log).</summary>
    public interface ICreativeHost
    {
        ILlmClient Llm { get; }
        CreativeSettings Cfg { get; }
        /// <summary>The capped log of finished pieces (newest last, at most Creative.LogCap).</summary>
        List<PieceStats> CreativeLog { get; }
    }

    public sealed class PieceStats
    {
        public string Kind;
        public int Calls;                 // LLM calls made for this piece (plan, candidates, retries, rerank)
        public double SlotS;              // wall seconds spent in those calls
        public int Candidates;            // candidates that survived write()'s cleaning
        public int ClicheRetries;
        public bool Yielded;              // stopped early because should_yield() said a line is waiting
        public string Plan = "";          // the private plan (never shown in game)
        public int Picked;                // index of the chosen candidate
        public List<double> Scores = new List<double>();
        public PieceStats(string kind) { Kind = kind; }
    }

    public static class Creative
    {
        public const int LogCap = 200;
        public const int PlanMaxWords = 40;
        public const int MaxBestOf = 9;            // the rerank grammar is one digit

        // Stock phrases of small-model purple prose. They are phrases to AVOID (given to the model as an instruction and
        // counted against a candidate), never example text.
        public static readonly string[] Cliches =
        {
            "a testament to", "testament to", "tapestry", "eyes gleaming", "a reminder that", "serves as a reminder",
            "sent shivers", "palpable", "a symphony of", "dance of", "beacon of", "etched in", "unwavering",
            "whispers of", "in the face of adversity", "stands as", "a stark reminder", "echoes of", "the weight of",
            "forever changed", "heart of the community", "bond that", "indomitable",
        };
        private static readonly List<KeyValuePair<string, Regex>> ClicheRes = Cliches.Select(c =>
            new KeyValuePair<string, Regex>(c, new Regex(@"\b" + string.Join(@"\s+", c.Split(' ').Select(Regex.Escape)) + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToList();

        /// <summary>Distinct stock phrases found in the text (case-insensitive, word boundaries), in lexicon order.
        /// "testament to" inside "a testament to" counts once: the longer phrase hides the shorter.</summary>
        public static List<string> FindCliches(string text)
        {
            var found = ClicheRes.Where(kv => kv.Value.IsMatch(text ?? "")).Select(kv => kv.Key).ToList();
            return found.Where(c => !found.Any(o => c != o && o.Contains(c))).ToList();
        }

        // target length in words per piece kind
        public static readonly Dictionary<string, int> TargetWords = new Dictionary<string, int>
            { ["art"] = 40, ["lore"] = 4, ["recap"] = 45, ["eulogy"] = 40, ["vows"] = 35 };
        private const int DefaultTarget = 40;

        // auto_score weights
        public const double WFact = 1.0;           // per distinct content word of the facts reused (capped at FactCap words)
        public const int FactCap = 8;
        public const double WName = 1.5;           // per distinct known name used
        public const double WCliche = 3.0;         // per stock phrase
        public const double WLength = 4.0;         // times min(1.5, |words - target| / target)
        public const double WVerbatim = 4.0;       // a run of 8+ words copied from the facts (plus 0.5 per word beyond 8)
        public const int VerbatimRun = 8;
        public const double WRepeatWord = 0.5;     // per extra use of a 4+ letter word
        public const double WRepeatPair = 1.0;     // per extra use of a two-word phrase

        private static readonly HashSet<string> Stop = new HashSet<string>((
            "that this with from have were been they them their there then than what when where which while " +
            "who whom whose will would could should about after before into over under just only also some such very much more most " +
            "each both other another being does done made make like your yours ours here said says still even").Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        private static readonly Regex WordRe = new Regex(@"[a-z0-9']+", RegexOptions.CultureInvariant);

        private static List<string> Words(string t)
        {
            return WordRe.Matches((t ?? "").ToLowerInvariant().Replace("’", "'")).Cast<Match>().Select(m => m.Value).ToList();
        }

        private static HashSet<string> Content(List<string> words)
        {
            var o = new HashSet<string>();
            foreach (var w in words)
            {
                string s = w.Trim('\'');
                if (s.Length >= 4 && !Stop.Contains(w)) o.Add(s);
            }
            return o;
        }

        /// <summary>Longest run of consecutive words of a that also appears consecutively in b.</summary>
        private static int LongestRun(List<string> a, List<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int best = 0;
            int n = 1;
            while (n <= a.Count)
            {
                var wa = new HashSet<string>();
                for (int i = 0; i + n <= a.Count; i++) wa.Add(string.Join(" ", a.Skip(i).Take(n)));
                var wb = new HashSet<string>();
                for (int i = 0; i + n <= b.Count; i++) wb.Add(string.Join(" ", b.Skip(i).Take(n)));
                if (wa.Overlaps(wb)) { best = n; n++; }
                else break;
            }
            return best;
        }

        /// <summary>Higher is better. Deterministic. Weights are the W* constants above.</summary>
        public static double AutoScore(string text, string facts, IEnumerable<string> names, string kind = "")
        {
            var tw = Words(text);
            if (tw.Count == 0) return -100.0;
            var fw = Words(facts);
            var fc = Content(fw);
            double s = WFact * Math.Min(FactCap, Content(tw).Count(w => fc.Contains(w)));
            string low = string.Join(" ", tw);
            int used = 0;
            foreach (var n in new HashSet<string>(names.Where(x => !string.IsNullOrEmpty(x)).Select(x => string.Join(" ", Words(x)))))
                if (n.Length > 0 && Regex.IsMatch(low, @"(?<![a-z0-9'])" + Regex.Escape(n) + @"(?![a-z0-9'])", RegexOptions.CultureInvariant)) used++;
            s += WName * used;
            s -= WCliche * FindCliches(text).Count;
            int target;
            if (!TargetWords.TryGetValue(kind ?? "", out target)) target = DefaultTarget;
            s -= WLength * Math.Min(1.5, (double)Math.Abs(tw.Count - target) / target);
            int run = LongestRun(tw, fw);
            if (run >= VerbatimRun) s -= WVerbatim + 0.5 * (run - VerbatimRun);
            var counts = new Dictionary<string, int>();
            foreach (var w in tw)
                if (w.Length >= 4 && !Stop.Contains(w)) counts[w] = counts.TryGetValue(w, out int c0) ? c0 + 1 : 1;
            s -= WRepeatWord * counts.Values.Where(c => c > 1).Sum(c => c - 1);
            var pairs = new Dictionary<string, int>();
            for (int i = 0; i < tw.Count - 1; i++)
                if (tw[i].Length >= 3 || tw[i + 1].Length >= 3)
                {
                    string p = tw[i] + " " + tw[i + 1];
                    pairs[p] = pairs.TryGetValue(p, out int c1) ? c1 + 1 : 1;
                }
            s -= WRepeatPair * pairs.Values.Where(c => c > 1).Sum(c => c - 1);
            return PyRound.Round(s, 3);
        }

        private static readonly Regex PlanLead = new Regex(@"^\s*(?:plan|private plan)\s*[:\-]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A plan made safe: game-data cleaned, trimmed to PlanMaxWords, "" if empty or it trips the age gate.
        /// Compose does not know who is a minor, so any romantic or sexual vocabulary in a plan drops the whole plan.</summary>
        public static string CleanPlan(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = GameData.Clean(raw, "event", null, false);
            t = PlanLead.Replace(t, "");
            var w = PyStr.Split(t);
            if (w.Count > PlanMaxWords) t = string.Join(" ", w.Take(PlanMaxWords)).TrimEnd(' ', ',', ';', ':', '-');
            t = PyStr.Strip(t);
            if (t.Length == 0 || t.Contains(GameData.Filtered) || AgeGate.Violation(t, true)) return "";
            return t;
        }

        private static string PlanNote(string plan)
        {
            return GameData.DataSection("private_plan", "Private plan for this piece: " + plan)
                + "Use this plan; never quote it or mention a plan.\n";
        }

        private static string AvoidNote(string baseNote, List<string> phrases)
        {
            string avoid = "Avoid these stock phrases and anything like them: " + string.Join("; ", phrases) + ". Use plain, specific wording.";
            return (baseNote.Length > 0 ? baseNote + "\n" : "") + avoid + "\n";
        }

        private static void Log(ICreativeHost d, PieceStats st)
        {
            var log = d.CreativeLog;
            if (log == null) return;
            log.Add(st);
            if (log.Count > LogCap) log.RemoveRange(0, log.Count - LogCap);
        }

        private static async Task<int?> RerankAsync(ICreativeHost d, PieceStats st, List<string> cands, int? slot, int? actor)
        {
            int n = cands.Count;
            string body = string.Join("\n", cands.Select((c, i) => (i + 1) + ". " + GameData.Clean(c, "block")));
            string system = "You judge short pieces of game writing. Pick the best candidate. Best means: grounded in the data, " +
                            "concrete details, fresh wording, fits the piece. Answer with the number only.\n\n" + GameData.DataRule;
            string user = "=== CANDIDATES ===\n" + GameData.DataSection("candidates", body) + "=== REQUEST ===\nWhich number is best?";
            double t0 = AwayGate.MonotonicS();
            st.Calls++;
            Completion c;
            try
            {
                c = await d.Llm.CompleteAsync(new List<LlmMessage> { new LlmMessage("system", system), new LlmMessage("user", user) },
                                              slot, "rerank", 2, null, "root ::= [1-" + n + "]", null, actor).ConfigureAwait(false);
            }
            catch (LlmError) { return null; }
            finally { st.SlotS += AwayGate.MonotonicS() - t0; }
            var m = Regex.Match(c.Text ?? "", @"\d");
            if (m.Success)
            {
                int v = (int)char.GetNumericValue(m.Value[0]);
                if (v >= 1 && v <= n) return v - 1;
            }
            return null;
        }

        /// <summary>Is a creative feature ("ground", "plan", "best_of") on for this kind? The global field means every kind;
        /// creative_&lt;feature&gt;_for lists single kinds.</summary>
        public static bool On(ICreativeHost d, string feature, string kind)
        {
            var cfg = d.Cfg;
            bool g;
            string[] list;
            switch (feature)
            {
                case "ground": g = cfg.Ground; list = cfg.GroundFor; break;
                case "plan": g = cfg.Plan; list = cfg.PlanFor; break;
                case "best_of": g = (cfg.BestOf == 0 ? 1 : cfg.BestOf) > 1; list = cfg.BestOfFor; break;
                default: g = false; list = new string[0]; break;
            }
            return g || Array.IndexOf(list ?? new string[0], kind) >= 0;
        }

        /// <summary>Candidates for this kind: the global creative_best_of, else creative_best_of_n when the kind is listed, else 1.</summary>
        public static int BestOf(ICreativeHost d, string kind)
        {
            var cfg = d.Cfg;
            int n = cfg.BestOf == 0 ? 1 : cfg.BestOf;
            if (Array.IndexOf(cfg.BestOfFor ?? new string[0], kind) >= 0) n = Math.Max(n, cfg.BestOfN == 0 ? 4 : cfg.BestOfN);
            return Math.Max(1, Math.Min(MaxBestOf, n));
        }

        public static async Task<(string Text, PieceStats Stats)> ComposeAsync(ICreativeHost d, string kind, int? slot, int? actor,
            string facts, ISet<string> names, Func<string, Task<string>> write, Func<Task<string>> plan = null, Func<bool> shouldYield = null)
        {
            var cfg = d.Cfg;
            bool usePlan = On(d, "plan", kind) && plan != null;
            int nCands = BestOf(d, kind);
            string rerank = (string.IsNullOrEmpty(cfg.Rerank) ? "auto" : cfg.Rerank).ToLowerInvariant();
            bool retry = cfg.ClicheRetry;
            var st = new PieceStats(kind);

            async Task<T> Timed<T>(Func<Task<T>> fn)
            {
                double t0 = AwayGate.MonotonicS();
                st.Calls++;
                try { return await fn().ConfigureAwait(false); }
                finally { st.SlotS += AwayGate.MonotonicS() - t0; }
            }

            try
            {
                string note = "";
                if (usePlan)
                {
                    string raw;
                    try { raw = await Timed(plan).ConfigureAwait(false); }
                    catch (LlmError) { raw = null; }
                    st.Plan = CleanPlan(raw);
                    if (st.Plan.Length > 0) note = PlanNote(st.Plan);
                }

                var cands = new List<string>();
                for (int i = 0; i < nCands; i++)
                {
                    if (i > 0 && shouldYield != null && shouldYield())
                    {
                        st.Yielded = true;
                        break;
                    }
                    string t;
                    try { t = await Timed(() => write(note)).ConfigureAwait(false); }
                    catch (LlmError) when (cands.Count > 0) { break; }
                    if (!string.IsNullOrEmpty(t)) cands.Add(t);
                }
                st.Candidates = cands.Count;
                if (cands.Count == 0) return (null, st);

                Func<string, double> score = t => AutoScore(t, facts, names, kind);
                st.Scores = cands.Select(score).ToList();
                int pick = 0;                                           // tie: the earlier one
                for (int i = 1; i < cands.Count; i++) if (st.Scores[i] > st.Scores[pick]) pick = i;
                if (cands.Count > 1 && rerank == "llm")
                {
                    int? r = await RerankAsync(d, st, cands, slot, actor).ConfigureAwait(false);
                    if (r != null) pick = r.Value;
                }
                st.Picked = pick;
                string text = cands[pick];

                if (retry)
                {
                    var found = FindCliches(text);
                    if (found.Count > 0)
                    {
                        st.ClicheRetries++;
                        string t2;
                        try { t2 = await Timed(() => write(AvoidNote(note, found))).ConfigureAwait(false); }
                        catch (LlmError) { t2 = null; }
                        if (!string.IsNullOrEmpty(t2) && FindCliches(t2).Count < found.Count)
                        {
                            cands.Add(t2);
                            st.Scores.Add(score(t2));
                            st.Picked = cands.Count - 1;
                            text = t2;
                        }
                    }
                }
                return (text, st);
            }
            finally { Log(d, st); }
        }
    }
}

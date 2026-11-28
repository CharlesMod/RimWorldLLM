// turncost: what a turn costs when the steward and the pawns share one engine over many cycles. One persistent Director (steward on, the real
// HttpLlm for the pawns and the real tool backend for the steward, both writing DIR/calls.jsonl) on the FakeBridge. Each cycle the digest
// moves on, a few recorded events are fed so pawns talk, think and write diaries, game time advances so the steward's heartbeat fires, and the
// Director is stepped and drained until it settles. Prints a table by kind and writes DIR/summary.json and DIR/steward-prompts.jsonl.
// Linked into the harness and into Mod.Tests (a self-test runs it offline with FakeLlm and a scripted backend).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using RimWorldLLM.Tests.Mind.ReplayTool;

namespace RimWorldLLM.Harness
{
    /// <summary>Wraps the tool backend: every request's messages go to steward-prompts.jsonl (cycle, turn, step, kind), so consecutive prompts can be diffed.</summary>
    public sealed class PromptTap : IBackend, IDisposable
    {
        public readonly IBackend Inner;
        private readonly string _path;
        private readonly object _gate = new object();
        public int Cycle;
        private long _lastTurn = -1;
        private int _step;

        public PromptTap(IBackend inner, string path) { Inner = inner; _path = path; File.WriteAllText(path, ""); }

        public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                     int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
        {
            string kind = ToolCallKind.For(slot);
            long? turn = slot.HasValue ? ToolCallKind.Turn : null;
            int step;
            lock (_gate)
            {
                if (turn.HasValue && turn.Value != _lastTurn) { _lastTurn = turn.Value; _step = 0; }
                step = turn.HasValue ? _step++ : 0;
                var row = new JObject
                {
                    ["cycle"] = Cycle, ["kind"] = kind, ["turn"] = turn.HasValue ? new JValue(turn.Value) : JValue.CreateNull(), ["step"] = step,
                    ["slot"] = slot.HasValue ? new JValue(slot.Value) : JValue.CreateNull(),
                    ["messages"] = new JArray(messages.Select(m => m.DeepClone())),
                };
                if (tools != null && tools.Count > 0) row["tools"] = tools.DeepClone();
                File.AppendAllText(_path, PyJson.Dumps(row, ensureAscii: false) + "\n", new UTF8Encoding(false));
            }
            return Inner.ChatAsync(messages, grammar, tools, toolChoice, maxTokens, temperature, timeout, slot);
        }

        public void Dispose() { if (Inner is IDisposable d) d.Dispose(); }
    }

    /// <summary>Offline: records the stats the wrapped client reports to a CallLog, as HttpLlm does for a real call.</summary>
    public sealed class LoggedLlm : ILlmClient
    {
        private readonly ILlmClient _inner; private readonly CallLog _log;
        public LoggedLlm(ILlmClient inner, CallLog log) { _inner = inner; _log = log; }
        public async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                                    IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                                    int? actor = null, JObject jsonSchema = null)
        {
            var c = await _inner.CompleteAsync(messages, slot, kind, maxTokens, stop, grammar, temperature, actor, jsonSchema).ConfigureAwait(false);
            if (c.Stats != null) _log.Record(c.Stats);
            return c;
        }
    }

    public static class Turncost
    {
        public const string Usage =
            "turncost --llm-url URL --out DIR [--cycles N (20)] [--digests FILE[,FILE...]] [--events recording.jsonl] [--mode auto|grammar|json|native] [--model M] [--playing]\n" +
            "      [--events-per-cycle N (4)] [--hours-per-cycle H (6.5)] [--chat-every N (4)]\n" +
            "  One persistent Director (steward + pawns on one engine, FakeBridge) over N cycles. Each cycle: the next digest (one digest is mutated a little each cycle),\n" +
            "  the next recorded events (pawns talk, think, write diaries), game time advances so the steward wakes, then step and drain until settled.\n" +
            "  Without --playing the steward is an advisor and wakes on a chat line, so one is sent every cycle; with --playing it wakes on its game-time heartbeat\n" +
            "  and on a chat line every --chat-every cycles. Writes DIR/calls.jsonl, DIR/steward-prompts.jsonl, DIR/summary.json, DIR/data/. Prints a table by kind.";

        sealed class Options
        {
            public string Url, Out, Model = "", Mode = "auto", Events;
            public List<string> Digests = new List<string>();
            public int Cycles = 20, EventsPerCycle = 4, ChatEvery = 4;
            public double HoursPerCycle = 6.5;
            public bool Playing;
        }

        static string ShippedDir() => Path.Combine(AppContext.BaseDirectory, "turncost");

        static readonly string[] ChatLines =
        {
            "How are we doing on food?", "Anything we should build next?", "Can someone look at the medicine stock?",
            "Is everybody rested?", "What should research do after this?", "Keep an eye on the weather for me.",
        };

        /// <param name="makeLlm">builds the line/pawn client (default: HttpLlm at --llm-url, logging to the given CallLog); tests pass a FakeLlm wrapper.</param>
        /// <param name="makeBackend">builds the steward's tool backend (default: MindRunner.MakeToolBackend with the CallLog); tests pass a scripted fake that logs.</param>
        public static async Task<int> MainAsync(string[] argv, TextWriter o, TextWriter e, Func<MindConfig, CallLog, ILlmClient> makeLlm = null,
                                                Func<MindConfig, CallLog, IBackend> makeBackend = null)
        {
            var op = new Options();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string Next() { if (i + 1 >= argv.Length) throw new ArgumentException("expected a value after " + a); return argv[++i]; }
                try
                {
                    switch (a)
                    {
                        case "--llm-url": op.Url = Next(); break;
                        case "--out": op.Out = Next(); break;
                        case "--cycles": op.Cycles = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--digests": op.Digests.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                        case "--events": op.Events = Next(); break;
                        case "--model": op.Model = Next(); break;
                        case "--playing": op.Playing = true; break;
                        case "--events-per-cycle": op.EventsPerCycle = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--chat-every": op.ChatEvery = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--hours-per-cycle": op.HoursPerCycle = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--mode":
                            op.Mode = Next();
                            if (op.Mode != "auto" && op.Mode != "grammar" && op.Mode != "json" && op.Mode != "native") throw new ArgumentException("--mode: invalid choice: " + op.Mode);
                            break;
                        default: throw new ArgumentException("unrecognized argument: " + a);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { e.WriteLine("turncost: error: " + ex.Message); return 2; }
            }
            if (op.Out == null || (op.Url == null && (makeLlm == null || makeBackend == null))) { e.WriteLine("turncost: error: --llm-url and --out are required"); return 2; }
            if (op.Cycles < 1) { e.WriteLine("turncost: error: --cycles must be at least 1"); return 2; }
            if (op.Digests.Count == 0) op.Digests.Add(Path.Combine(ShippedDir(), "digest-day2.json"));
            op.Events ??= Path.Combine(ShippedDir(), "multiday.jsonl");
            foreach (string f in op.Digests.Concat(new[] { op.Events }))
                if (!File.Exists(f)) { e.WriteLine("turncost: error: no such file: " + f); return 2; }
            Run(op, o, makeLlm, makeBackend).GetAwaiter().GetResult();
            return 0;
        }

        /// <summary>The digest for a cycle: the next of several in turn, or, with one, that digest moved on a little (deterministically).</summary>
        public static JObject DigestFor(IReadOnlyList<JObject> digests, int cycle)
        {
            var d = (JObject)digests[cycle % digests.Count].DeepClone();
            if (digests.Count > 1) return d;
            if (d["date"] is JObject date)
            {
                int hour = (int)(date["hour"] ?? 12) + cycle;
                date["day"] = (int)(date["day"] ?? 1) + hour / 24;
                date["hour"] = hour % 24;
            }
            if (d["resources"] is JArray res && res.Count > 0)
            {
                var r = res[cycle % res.Count] as JObject;
                if (r?["count"] != null && r["count"].Type == JTokenType.Integer) r["count"] = Math.Max(0, (int)r["count"] + ((cycle % 2 == 0) ? -7 : 11) * (1 + cycle % 3));
            }
            if (d["facts"] is JObject facts && facts["day"] != null && d["date"] is JObject dd) facts["day"] = dd["day"];
            if (d["letters"] is JArray letters)
                letters.Add(new JObject { ["label"] = "Visitor passing through (cycle " + cycle + ")", ["type"] = "neutral" });
            else d["letters"] = new JArray(new JObject { ["label"] = "Visitor passing through (cycle " + cycle + ")", ["type"] = "neutral" });
            if (d["letters"] is JArray la && la.Count > 3) while (la.Count > 3) la.RemoveAt(0);
            return d;
        }

        static async Task Settle(Director d, int limit = 40)
        {
            for (int i = 0; i < limit; i++)
            {
                var before = (d.Idle.Log.Count, d.Dropped.Count, d.Said.Count);
                await d.StepAsync();
                await d.DrainAsync();
                if ((d.Idle.Log.Count, d.Dropped.Count, d.Said.Count) == before && !d.Idle.Active()) return;
            }
        }

        static async Task Run(Options op, TextWriter o, Func<MindConfig, CallLog, ILlmClient> makeLlm, Func<MindConfig, CallLog, IBackend> makeBackend)
        {
            Directory.CreateDirectory(op.Out);
            string data = Path.Combine(op.Out, "data");
            Directory.CreateDirectory(data);
            string callsPath = Path.Combine(op.Out, "calls.jsonl");
            File.WriteAllText(callsPath, "");
            var callLog = new CallLog(callsPath, long.MaxValue / 4);
            var digests = op.Digests.Select(f => (JObject)BridgeJson.Parse(File.ReadAllText(f))).ToList();
            var (pawns, events) = Replay.LoadJsonl(op.Events);
            events = events.OrderBy(x => x["seq"] == null ? 0 : (int)x["seq"]).ToList();

            var cfg = new MindConfig { DataDir = data, BridgeUrl = "http://fake", BridgeToken = "tok", Wants = false, Steward = true, StewardMinGapRealS = 0.0, StewardCombatGapRealS = 0.0, TurnGapTicks = 0, TurnGapFloorRealS = 0.0 };
            if (op.Url != null) cfg.Llm.BaseUrl = op.Url;
            if (!string.IsNullOrEmpty(op.Model)) cfg.Llm.Model = op.Model;
            var fb = new FakeBridge("tok") { Digest = digests[0] };
            foreach (var p in pawns) fb.Pawns[(int)p["id"]] = p;
            var ids = new SortedSet<int>();
            foreach (var ev in events)
            {
                foreach (var x in (ev["pawns"] as JArray) ?? new JArray()) ids.Add((int)x["id"]);
                foreach (var w in (ev["witnesses"] as JArray) ?? new JArray()) ids.Add((int)w);
            }
            foreach (int i in ids) if (!fb.Pawns.ContainsKey(i)) fb.AddPawn(i, "Pawn" + i);
            var adapter = new RimWorldAdapter();
            var bridge = new BridgeClient(fb, RimWorldAdapter.WireNames, "tok");
            ILlmClient llm = makeLlm != null ? makeLlm(cfg, callLog)
                : new HttpLlm(cfg.Llm, callLog, new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.Llm.TimeoutS + 30) });
            var tap = new PromptTap(makeBackend != null ? makeBackend(cfg, callLog) : MindRunner.MakeToolBackend(cfg, callLog), Path.Combine(op.Out, "steward-prompts.jsonl"));
            var d = new Director(cfg, bridge, llm, adapter, null, () => fb.Tick / 60.0, new PyRandom(1));
            int upd = adapter.UnitsPerDay;
            int cycleTicks = (int)Math.Round(op.HoursPerCycle / 24.0 * upd);
            int evPos = 0;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await d.StartAsync();
                d.ToolBackend = tap; d.ToolMode = op.Mode;
                fb.Playing = false;
                await d.Steward.StepAsync();                       // first sight: the chat cursor starts here
                fb.Playing = op.Playing;
                for (int c = 0; c < op.Cycles; c++)
                {
                    tap.Cycle = c;
                    fb.Digest = DigestFor(digests, c);
                    int start = fb.Tick;
                    for (int k = 0; k < op.EventsPerCycle && events.Count > 0; k++)
                    {
                        var ev = events[evPos % events.Count]; evPos++;
                        fb.Tick = start + (k + 1) * 120;
                        var actors = ((ev["pawns"] as JArray) ?? new JArray()).Select(p => new KeyValuePair<int, string>((int)p["id"], (string)p["role"] ?? "")).ToList();
                        var wit = ((ev["witnesses"] as JArray) ?? new JArray()).Select(w => (int)w).ToList();
                        fb.AddEvent((string)ev["type"], actors, (string)ev["summary"] ?? "", wit, ev["severity"] != null ? (double)ev["severity"] : 0.5,
                                    ev["ledger_id"] == null || ev["ledger_id"].Type == JTokenType.Null ? null : (string)ev["ledger_id"], ev["data"] as JObject);
                        await d.StepAsync();
                        await d.DrainAsync();
                    }
                    bool chat = !op.Playing || (op.ChatEvery > 0 && c % op.ChatEvery == op.ChatEvery - 1);
                    if (chat) fb.AddChat("player", ChatLines[c % ChatLines.Length]);
                    fb.Tick = start + Math.Max(cycleTicks, (op.EventsPerCycle + 1) * 120);   // time passes: the heartbeat comes due
                    await Settle(d);
                    o.WriteLine("cycle " + (c + 1) + "/" + op.Cycles + " done, " + callLog.Snapshot().Count + " calls so far");
                }
            }
            finally { tap.Dispose(); d.Dispose(); (llm as IDisposable)?.Dispose(); }
            o.WriteLine("ran " + op.Cycles + " cycles in " + wall.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");

            var rows = ReadRows(callsPath);
            var summary = Summarize(rows);
            File.WriteAllText(Path.Combine(op.Out, "summary.json"), summary.ToString(Newtonsoft.Json.Formatting.Indented));
            foreach (string line in Table(summary)) o.WriteLine(line);
        }

        public static List<JObject> ReadRows(string path)
        {
            var rows = new List<JObject>();
            foreach (string ln in File.ReadAllLines(path)) if (ln.Trim().Length > 0) rows.Add(JObject.Parse(ln));
            return rows;
        }

        static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
        }

        static double Num(JObject r, string k) => r[k] == null || r[k].Type == JTokenType.Null ? 0.0 : (double)r[k];

        static JObject Stats(List<JObject> rows)
        {
            var pn = rows.Select(r => Num(r, "prompt_n")).ToList();
            var cn = rows.Select(r => Num(r, "cache_n")).ToList();
            double sp = pn.Sum(), sc = cn.Sum();
            var timed = rows.Where(r => Num(r, "prompt_ms") > 0).ToList();
            double ms = timed.Sum(r => Num(r, "prompt_ms"));
            double raw = ms > 0 ? timed.Sum(r => Num(r, "prompt_n")) / (ms / 1000.0) : 0;
            double eff = ms > 0 ? timed.Sum(r => Num(r, "prompt_n") + Num(r, "cache_n")) / (ms / 1000.0) : 0;
            return new JObject
            {
                ["n"] = rows.Count, ["failed"] = rows.Count(r => r["ok"] != null && !(bool)r["ok"]),
                ["median_prompt_n"] = Median(pn), ["mean_prompt_n"] = pn.Count == 0 ? 0 : Math.Round(sp / pn.Count, 1), ["median_cache_n"] = Median(cn),
                ["hit_pct"] = sp + sc > 0 ? Math.Round(100.0 * sc / (sp + sc), 1) : 0, ["raw_prefill_tps"] = Math.Round(raw, 1), ["effective_prefill_tps"] = Math.Round(eff, 1),
                ["predicted_n_mean"] = rows.Count == 0 ? 0 : Math.Round(rows.Sum(r => Num(r, "predicted_n")) / rows.Count, 1),
            };
        }

        /// <summary>By kind, plus per steward turn (the sum of a turn's loop calls, grouped by the "turn" field).</summary>
        public static JObject Summarize(List<JObject> rows)
        {
            var kinds = new JObject();
            foreach (var g in rows.GroupBy(r => (string)r["kind"] ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
                kinds[g.Key] = Stats(g.ToList());
            var turns = new JObject();
            foreach (var g in rows.Where(r => ((string)r["kind"] ?? "").StartsWith("steward", StringComparison.Ordinal) && r["turn"] != null && r["turn"].Type != JTokenType.Null)
                                  .GroupBy(r => (string)r["kind"]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var per = g.GroupBy(r => (long)r["turn"]).Select(t => t.ToList()).ToList();
                var sumP = per.Select(t => t.Sum(r => Num(r, "prompt_n"))).ToList();
                var sumC = per.Select(t => t.Sum(r => Num(r, "cache_n"))).ToList();
                turns[g.Key] = new JObject
                {
                    ["turns"] = per.Count, ["calls_per_turn_mean"] = Math.Round(per.Average(t => (double)t.Count), 2),
                    ["median_prompt_n"] = Median(sumP), ["mean_prompt_n"] = Math.Round(sumP.Average(), 1), ["median_cache_n"] = Median(sumC),
                    ["median_wall_s"] = Math.Round(Median(per.Select(t => t.Sum(r => Num(r, "wall_s"))).ToList()), 2),
                };
            }
            return new JObject { ["calls"] = rows.Count, ["kinds"] = kinds, ["steward_turns"] = turns };
        }

        public static List<string> Table(JObject s)
        {
            string F(JToken t, string fmt = "F0") => ((double)t).ToString(fmt, CultureInfo.InvariantCulture);
            var o = new List<string>
            {
                "", string.Format(CultureInfo.InvariantCulture, "{0,-16} {1,5} {2,5} {3,10} {4,10} {5,10} {6,6} {7,10} {8,10}", "kind", "n", "fail", "med prompt", "mean prompt", "med cache", "hit%", "raw tok/s", "eff tok/s"),
            };
            foreach (var p in ((JObject)s["kinds"]).Properties())
            {
                var k = (JObject)p.Value;
                o.Add(string.Format(CultureInfo.InvariantCulture, "{0,-16} {1,5} {2,5} {3,10} {4,10} {5,10} {6,6} {7,10} {8,10}", p.Name, k["n"], k["failed"],
                    F(k["median_prompt_n"]), F(k["mean_prompt_n"], "F1"), F(k["median_cache_n"]), F(k["hit_pct"], "F1"), F(k["raw_prefill_tps"]), F(k["effective_prefill_tps"])));
            }
            var turns = (JObject)s["steward_turns"];
            if (turns.Count > 0)
            {
                o.Add("");
                o.Add(string.Format(CultureInfo.InvariantCulture, "{0,-16} {1,6} {2,12} {3,16} {4,14} {5,12}", "steward turn", "turns", "calls/turn", "med sum prompt_n", "med sum cache_n", "med wall s"));
                foreach (var p in turns.Properties())
                {
                    var t = (JObject)p.Value;
                    o.Add(string.Format(CultureInfo.InvariantCulture, "{0,-16} {1,6} {2,12} {3,16} {4,14} {5,12}", p.Name, t["turns"], F(t["calls_per_turn_mean"], "F2"),
                        F(t["median_prompt_n"]), F(t["median_cache_n"]), F(t["median_wall_s"], "F2")));
                }
            }
            return o;
        }
    }
}

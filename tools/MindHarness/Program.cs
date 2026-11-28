// MindHarness: console harness for the C# Mind port (ships nowhere). Commands: replay, speedsim, steward-try, comment-try.
// Usage: see Usage(). Anything that writes needs --data-dir (never defaulted to the real agent data dir).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Tests.Mind.ReplayTool;

namespace RimWorldLLM.Harness
{
    static class Program
    {
        static string Usage() => string.Join("\n",
            "usage: MindHarness <command> [options]",
            "  replay <recording.jsonl> [--llm URL --mode openai|native --model M --key K] [--data-dir D]",
            "      FakeLlm by default; with --llm a real HttpLlm, --data-dir is then required: it gets replay-calls.jsonl (the CallLog)",
            "      and replay-requests.jsonl (every raw HTTP request body and reply, plus one 'decision' row per LLM call).",
            "  speedsim [--speed N | --all] [--events FILE] [--slots N] [--seed N]   (virtual-time speed simulation)",
            "  steward-try --digest FILE --llm-url URL [--goals FILE] [--chat TEXT] [--playing] [--model M] [--mode auto|grammar|json|native] [--combat FILE [--combat-cycle]] [--trade-turn]",
            "  planner-sim --digest FILE --grid FILE --llm-url URL --out DIR [--cycles N] [--chat TEXT] [--model M] [--mode M]",
            "      steward cycles on a recorded digest; its room requests go through the base planner on a recorded map grid (ASCII)",
            "  turncost --llm-url URL --out DIR [--cycles N] [--digests F,F] [--events recording.jsonl] [--mode M] [--model M] [--playing]   (steward + pawns on one engine over many cycles; a table by kind. See Turncost.Usage)",
            "  stream-try --llm-url URL [--model M] [--key K]   (one streamed request; prints every stream event with its time)",
            "  comment-try --events FILE [--llm-url URL --model M --key K]   (documentary voice on sample events; FakeLlm without --llm-url)");

        static int Main(string[] a)
        {
            try { return MainAsync(a).GetAwaiter().GetResult(); }
            catch (Exception e) { Console.Error.WriteLine("MindHarness: " + e.GetType().Name + ": " + e.Message); return 1; }
        }

        static async Task<int> MainAsync(string[] a)
        {
            if (a.Length == 0 || a[0] == "-h" || a[0] == "--help") { Console.Error.WriteLine(Usage()); return a.Length == 0 ? 2 : 0; }
            var rest = a.Skip(1).ToArray();
            switch (a[0])
            {
                case "replay": return await ReplayAsync(rest);
                case "steward-try": return await StewardTry.MainAsync(rest, Console.Out, Console.Error);
                case "turncost": return await Turncost.MainAsync(rest, Console.Out, Console.Error);
                case "planner-sim": return await PlannerSim.MainAsync(rest, Console.Out, Console.Error);
                case "speedsim": return SpeedSim.Run(rest, Console.Out, Console.Error);
                case "comment-try": return await CommentTryAsync(rest);
                case "stream-try": return await StreamTryAsync(rest);
                default: Console.Error.WriteLine("MindHarness: unknown command " + a[0] + "\n" + Usage()); return 2;
            }
        }

        static async Task<int> CommentTryAsync(string[] a)
        {
            string events = null, url = null, model = "", key = "";
            for (int i = 0; i < a.Length; i++)
            {
                string Next() { if (i + 1 >= a.Length) throw new ArgumentException("expected a value after " + a[i]); return a[++i]; }
                switch (a[i])
                {
                    case "--events": events = Next(); break;
                    case "--llm-url": url = Next(); break;
                    case "--model": model = Next(); break;
                    case "--key": key = Next(); break;
                    default: throw new ArgumentException("unknown option " + a[i]);
                }
            }
            if (events == null) { Console.Error.WriteLine("comment-try: --events FILE is required"); return 2; }
            var list = JArray.Parse(File.ReadAllText(events)).OfType<JObject>().ToList();
            ILlmClient llm;
            if (url != null)
            {
                var lc = new LlmConfig { BaseUrl = url, Model = model, ApiKey = key };
                llm = new HttpLlm(lc, null, new HttpClient { Timeout = TimeSpan.FromSeconds(lc.TimeoutS + 30) });
            }
            else llm = new RimWorldLLM.Tests.Mind.Fakes.FakeLlm();
            foreach (var row in await RimWorldLLM.Tests.Mind.CommentTry.TryEventsAsync(list, llm))
                Console.Out.WriteLine(RimWorldLLM.Tests.Mind.CommentTry.Format(row));
            return 0;
        }

        static async Task<int> StreamTryAsync(string[] a)
        {
            string url = null, model = "", key = "";
            for (int i = 0; i < a.Length; i++)
            {
                string Next() { if (i + 1 >= a.Length) throw new ArgumentException("expected a value after " + a[i]); return a[++i]; }
                switch (a[i])
                {
                    case "--llm-url": url = Next(); break;
                    case "--model": model = Next(); break;
                    case "--key": key = Next(); break;
                    default: throw new ArgumentException("unknown option " + a[i]);
                }
            }
            if (url == null) { Console.Error.WriteLine("stream-try: --llm-url is required"); return 2; }
            var lc = new LlmConfig { BaseUrl = url, Model = model, ApiKey = key };
            var llm = new HttpLlm(lc, null, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var opts = new CallOptions
            {
                Sink = new StreamSink(e => Console.Out.WriteLine(
                    $"{t0.Elapsed.TotalSeconds,7:F3}s {e.Kind}" + (e.Kind == StreamEventKind.Progress ? $" {e.Processed}/{e.Total} cache {e.Cache}" : "") +
                    (e.Kind == StreamEventKind.Partial || e.Kind == StreamEventKind.Done || e.Kind == StreamEventKind.Error ? " " + e.Text : ""))),
            };
            var msgs = new List<LlmMessage> { new LlmMessage("system", "You are the narrator of a small colony. Answer in one short sentence."),
                                              new LlmMessage("user", "The player asks: what should we build first?") };
            var c = await llm.CompleteAsync(msgs, 0, "steward", 60, new string[0], null, null, null, null, opts);
            Console.Out.WriteLine("FINAL: " + c.Text);
            Console.Out.WriteLine("STATS: " + PyJson.Dumps(c.Stats.ToDict()));
            return 0;
        }

        /// <summary>Logs each raw request body and its reply to a JSONL file.</summary>
        sealed class LoggingHandler : DelegatingHandler
        {
            readonly Action<JObject> _sink;
            public LoggingHandler(Action<JObject> sink) : base(new HttpClientHandler()) { _sink = sink; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                string body = req.Content == null ? null : await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                var e = new JObject { ["type"] = "http", ["method"] = req.Method.Method, ["url"] = req.RequestUri.ToString(), ["request"] = body };
                try
                {
                    var resp = await base.SendAsync(req, ct).ConfigureAwait(false);
                    string rb = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    e["status"] = (int)resp.StatusCode; e["reply"] = rb;
                    _sink(e);
                    return resp;
                }
                catch (Exception ex) { e["error"] = ex.GetType().Name + ": " + ex.Message; _sink(e); throw; }
            }
        }

        static async Task<int> ReplayAsync(string[] a)
        {
            string path = null, url = null, mode = "openai", model = "", key = "", dataDir = null;
            for (int i = 0; i < a.Length; i++)
            {
                string Next() { if (i + 1 >= a.Length) throw new ArgumentException("expected a value after " + a[i]); return a[++i]; }
                switch (a[i])
                {
                    case "--llm": url = Next(); break;
                    case "--mode": mode = Next(); break;
                    case "--model": model = Next(); break;
                    case "--key": key = Next(); break;
                    case "--data-dir": dataDir = Next(); break;
                    default:
                        if (a[i].StartsWith("--", StringComparison.Ordinal) || path != null) { Console.Error.WriteLine("replay: unrecognized argument " + a[i]); return 2; }
                        path = a[i]; break;
                }
            }
            if (path == null) { Console.Error.WriteLine("replay: a recording path is required"); return 2; }
            if (mode != "openai" && mode != "native") { Console.Error.WriteLine("replay: --mode must be openai or native"); return 2; }
            if (url != null && dataDir == null) { Console.Error.WriteLine("replay: --llm writes logs, so --data-dir is required"); return 2; }
            var cfg = new MindConfig();
            ILlmClient llm = null;
            RecordingLlm rec = null;
            StreamWriter log = null;
            object gate = new object();
            try
            {
                if (url != null)
                {
                    Directory.CreateDirectory(dataDir);
                    cfg.DataDir = dataDir;
                    cfg.Llm.BaseUrl = url; cfg.Llm.Mode = mode; cfg.Llm.Model = model; cfg.Llm.ApiKey = key;
                    log = new StreamWriter(Path.Combine(dataDir, "replay-requests.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
                    Action<JObject> sink = e => { lock (gate) log.WriteLine(PyJson.Dumps(e)); };
                    var http = new HttpClient(new LoggingHandler(sink)) { Timeout = TimeSpan.FromSeconds(cfg.Llm.TimeoutS + 30) };
                    var inner = new HttpLlm(cfg.Llm, new CallLog(Path.Combine(dataDir, "replay-calls.jsonl")), http);
                    rec = new RecordingLlm(inner);
                    rec.OnEntry = e => sink(new JObject { ["type"] = "decision", ["kind"] = e["kind"], ["slot"] = e["slot"], ["actor"] = e["actor"], ["reply"] = e["reply"], ["error"] = e["error"], ["trace"] = e["trace"] });
                    llm = rec;
                }
                var items = await Replay.RunAsync(path, cfg, llm);
                foreach (string line in Replay.Format(items)) Console.WriteLine(line);
                if (rec != null) Console.Error.WriteLine("replay: " + rec.Log.Count + " LLM calls; logs in " + dataDir);
                return 0;
            }
            finally { log?.Dispose(); }
        }
    }
}

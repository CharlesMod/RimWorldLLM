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
            "  steward-try --digest FILE --llm-url URL [--goals FILE] [--chat TEXT] [--playing] [--model M] [--mode auto|grammar|json|native]",
            "  comment-try   (not wired: the commentary function is delta2's; see docs/csmind/parity-tools.md)");

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
                case "speedsim": return SpeedSim.Run(rest, Console.Out, Console.Error);
                case "comment-try":
                    Console.Error.WriteLine("comment-try: not wired (delta2's commentary function is not in this tree); see docs/csmind/parity-tools.md");
                    return 2;
                default: Console.Error.WriteLine("MindHarness: unknown command " + a[0] + "\n" + Usage()); return 2;
            }
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
                    rec.OnEntry = e => sink(new JObject { ["type"] = "decision", ["kind"] = e["kind"], ["slot"] = e["slot"], ["actor"] = e["actor"], ["reply"] = e["reply"], ["error"] = e["error"] });
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

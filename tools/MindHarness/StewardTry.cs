// Port of games/rimworld/steward_try.py: try the steward on a recorded digest, one cycle against the fake bridge, with a real (or injected) tool backend.
// Linked into the harness and into Mod.Tests (the test of steward_try runs it with a scripted backend).
// Not ported: the `dialogs` / `paused` arguments of try_once (its dialogs path needs FakeBridge.Dialogs, which comes with the "delta2" port; the CLI never passes them).
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;

namespace RimWorldLLM.Harness
{
    /// <summary>Wraps the tool backend to keep the first request (the system and user turns the model saw).</summary>
    public sealed class StewardTap : IBackend, IDisposable
    {
        public readonly IBackend Inner;
        public List<JObject> First;
        public StewardTap(IBackend inner) { Inner = inner; }

        public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                     int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
        {
            if (First == null && slot != null)       // the protocol probe of --mode auto carries no slot
                First = messages.Select(m => (JObject)m.DeepClone()).ToList();
            return Inner.ChatAsync(messages, grammar, tools, toolChoice, maxTokens, temperature, timeout, slot);
        }

        public void Dispose() { if (Inner is IDisposable d) d.Dispose(); }
    }

    public static class StewardTry
    {
        /// <summary>The result of one cycle (the dict try_once returns).</summary>
        public sealed class Outcome
        {
            public List<JObject> Messages;
            public JObject Trace = new JObject();
            public List<JObject> Chat = new List<JObject>();
            public List<KeyValuePair<string, JObject>> Acts = new List<KeyValuePair<string, JObject>>();
            public JObject Board;
            public List<string> DialogsLeft = new List<string>();
            public double WallS;                                                  // the seconds the cycle took
            public List<KeyValuePair<string, JObject>> SpeedActs = new List<KeyValuePair<string, JObject>>();   // the /act/time bodies
            public List<KeyValuePair<string, string>> Requests = new List<KeyValuePair<string, string>>();       // fb.requests
        }

        /// <param name="makeBackend">builds the tool backend (default: MindRunner.MakeToolBackend, the real one at llmUrl); tests pass a scripted fake.</param>
        /// <param name="wrapTransport">taps the fake bridge (goldens record the writes through it).</param>
        /// <param name="combat">a recorded GET /combat body: the game is in this fight (the digest gets it as its combat block)</param>
        /// <param name="combatCycle">run one small combat turn (the steward is playing and the fight is already known)</param>
        /// <param name="tradeTurn">run one trade turn (a trader arrived: the fake bridge's /vivarium lists the digest's traders); implies playing</param>
        public static async Task<Outcome> TryOnceAsync(JObject digest, JObject goals, string chat, bool playing, string llmUrl, string model = "", string mode = "auto",
                                                       Func<MindConfig, IBackend> makeBackend = null, Func<IBridgeTransport, IBridgeTransport> wrapTransport = null,
                                                       JObject combat = null, bool combatCycle = false, bool tradeTurn = false)
        {
            digest = digest == null ? new JObject() : new JObject(digest);
            if (combat != null && !digest.ContainsKey("combat")) digest["combat"] = combat.DeepClone();
            string tmp = Path.Combine(Path.GetTempPath(), "rimworldllm-steward-try-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var cfg = new MindConfig { DataDir = tmp, BridgeUrl = "http://fake", BridgeToken = "tok", Wants = false, Steward = true, StewardMinGapRealS = 0.0, StewardCombatGapRealS = 0.0 };
                cfg.Llm.BaseUrl = llmUrl;
                if (!string.IsNullOrEmpty(model)) cfg.Llm.Model = model;
                var fb = new FakeBridge("tok") { Digest = digest, Combat = combat };
                if (goals != null)
                    foreach (string k in new[] { "goals", "plan", "orders", "log" })
                        if (goals[k] != null) fb.GoalsBoard[k] = goals[k].DeepClone();
                var adapter = new RimWorldAdapter();
                IBridgeTransport transport = wrapTransport == null ? fb : wrapTransport(fb);
                var bridge = new BridgeClient(transport, RimWorldAdapter.WireNames, "tok");
                double real = 0.0;
                var d = new Director(cfg, bridge, new FakeLlm(), adapter, null, () => real, new PyRandom(1));
                await d.StartAsync();
                var tap = new StewardTap(makeBackend != null ? makeBackend(cfg) : MindRunner.MakeToolBackend(cfg));
                d.ToolBackend = tap; d.ToolMode = mode;
                double wall = 0.0;
                try
                {
                    fb.Playing = false;
                    await d.Steward.StepAsync();                 // first sight: the chat cursor starts here
                    fb.Playing = playing || combatCycle || tradeTurn;
                    if (combatCycle) d.Steward.CombatOn = true;         // as if the "combat started" cycle had run: the next wake is a combat turn
                    if (!string.IsNullOrEmpty(chat)) fb.AddChat("player", chat);
                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    await d.StepAsync();
                    await d.DrainAsync();
                    wall = t0.Elapsed.TotalSeconds;
                }
                finally { tap.Dispose(); d.Dispose(); }
                var trace = d.Steward.Stats.Trace.Count > 0 ? d.Steward.Stats.Trace[d.Steward.Stats.Trace.Count - 1] : new JObject();
                return new Outcome
                {
                    Messages = tap.First, Trace = trace, Chat = fb.Chat.ToList(), Acts = fb.ActLog.ToList(), Board = fb.GoalsBoard,
                    DialogsLeft = new List<string>(), WallS = wall,
                    SpeedActs = fb.ActLog.Where(a => a.Key == "time").ToList(), Requests = fb.Requests.ToList(),
                };
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
        }

        static string Content(JObject m) { return (string)m["content"]; }

        /// <summary>report(): the lines steward_try prints. Returns the exit code.</summary>
        public static int Report(Outcome o, TextWriter w)
        {
            var tr = o.Trace;
            if (o.Messages == null || o.Messages.Count == 0)
            {
                w.WriteLine("no cycle ran: " + (BridgeJson.Truthy(tr["error"], false) ? (string)tr["error"]
                    : BridgeJson.Truthy(tr["skipped"], false) ? "trade turn skipped: " + (string)tr["skipped"] : "the steward did not wake"));
                return 1;
            }
            int sysN = PyStr.Len(Content(o.Messages[0])), userN = PyStr.Len(Content(o.Messages[1]));
            w.WriteLine("SYSTEM PROMPT: " + sysN + " chars");
            w.WriteLine("USER TURN:\n" + Content(o.Messages[1]) + "\n");
            w.WriteLine("PROMPT: " + (sysN + userN) + " chars, about " + ((sysN + userN) / 4) + " tokens (system " + sysN + ", user " + userN + "; the tool list is part of the system prompt)");
            w.WriteLine("ELAPSED: " + o.WallS.ToString("F2", CultureInfo.InvariantCulture) + " s for the cycle (" + (tr["elapsed_s"] == null ? "?" : PyJson.Str(tr["elapsed_s"])) + " s in the tool loop)");
            bool pl = tr["playing"] != null && BridgeJson.Truthy(tr["playing"], false);
            string reason = tr["reason"] == null ? "None" : (string)tr["reason"];
            string outcome = tr["outcome"] == null ? "None" : (string)tr["outcome"];
            string ended = tr["reason_ended"] != null && !string.IsNullOrEmpty((string)tr["reason_ended"]) ? (string)tr["reason_ended"] : "";
            w.WriteLine("CYCLE: " + reason + " (" + (pl ? "playing" : "advisor") + "), ended " + outcome + " " + ended);
            foreach (var e in (tr["executed"] as JArray) ?? new JArray())
                w.WriteLine("TOOL " + (string)e[0] + " " + PyJson.Dumps(e[1]) + " -> " + PyJson.Dumps(e[2]));
            foreach (var e in o.Chat)
                if ((string)e["who"] != "player") w.WriteLine("CHAT [" + (string)e["who"] + "] " + (string)e["text"]);
            foreach (var f in (tr["failures"] as JArray) ?? new JArray()) w.WriteLine("REJECTED: " + (string)f);
            w.WriteLine("DIALOGS STILL OPEN: " + (o.DialogsLeft.Count == 0 ? "none" : string.Join(", ", o.DialogsLeft)));
            if (o.Acts.Count > 0)
                w.WriteLine("BRIDGE ACTS: " + PyJson.Dumps(new JArray(o.Acts.Select(a => (JToken)new JArray(a.Key, a.Value)))));
            if (BridgeJson.Truthy(tr["skipped"], false)) w.WriteLine("SKIPPED: " + (string)tr["skipped"]);
            if (tr["error"] != null && BridgeJson.Truthy(tr["error"], false)) w.WriteLine("ERROR: " + (string)tr["error"]);
            return 0;
        }

        public const string Usage =
            "steward-try --digest FILE [--goals FILE] [--chat TEXT] [--playing] --llm-url URL [--model M] [--mode auto|grammar|json|native]\n" +
            "      [--combat FILE [--combat-cycle]] [--trade-turn]   (a recorded GET /combat body; --combat-cycle runs one small combat turn and needs no --digest)\n" +
            "      --trade-turn runs one trade turn on the digest (needs --digest; implies --playing); SKIPPED is printed when food is critical or a fight is on.\n" +
            "  Runs one steward cycle on a recorded GET /digest body against the fake bridge with the real tool backend at URL; prints the system prompt size,\n" +
            "  the user turn, each tool call with its result, the chat lines and the REJECTED failures. Nothing here touches a game.";

        /// <summary>steward_try.main: arguments after the command word. Exit code 2 on a usage error (argparse).</summary>
        public static async Task<int> MainAsync(string[] argv, TextWriter stdout, TextWriter stderr, Func<MindConfig, IBackend> makeBackend = null)
        {
            string digest = null, goals = null, chat = "", url = null, model = "", mode = "auto", combat = null;
            bool playing = false, combatCycle = false, tradeTurn = false;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string Next() { if (i + 1 >= argv.Length) throw new ArgumentException("expected a value after " + a); return argv[++i]; }
                try
                {
                    switch (a)
                    {
                        case "--digest": digest = Next(); break;
                        case "--goals": goals = Next(); break;
                        case "--chat": chat = Next(); break;
                        case "--playing": playing = true; break;
                        case "--combat": combat = Next(); break;
                        case "--combat-cycle": combatCycle = true; break;
                        case "--trade-turn": tradeTurn = true; break;
                        case "--llm-url": url = Next(); break;
                        case "--model": model = Next(); break;
                        case "--mode":
                            mode = Next();
                            if (mode != "auto" && mode != "grammar" && mode != "json" && mode != "native") throw new ArgumentException("--mode: invalid choice: " + mode);
                            break;
                        default: throw new ArgumentException("unrecognized argument: " + a);
                    }
                }
                catch (ArgumentException e) { stderr.WriteLine("steward_try: error: " + e.Message); return 2; }
            }
            if (url == null) { stderr.WriteLine("steward_try: error: the following arguments are required: --llm-url"); return 2; }
            if (combatCycle && combat == null) { stderr.WriteLine("steward_try: error: --combat-cycle needs --combat"); return 2; }
            if (digest == null && !combatCycle) { stderr.WriteLine("steward_try: error: --digest is required (except with --combat-cycle)"); return 2; }
            if (tradeTurn && digest == null) { stderr.WriteLine("steward_try: error: --trade-turn needs --digest"); return 2; }
            if (chat.Length == 0 && !playing && !combatCycle && !tradeTurn) { stderr.WriteLine("steward_try: error: nothing would wake the steward: give --chat or --playing"); return 2; }
            var dj = digest == null ? new JObject() : (JObject)BridgeJson.Parse(File.ReadAllText(digest));
            var cj = combat == null ? null : (JObject)BridgeJson.Parse(File.ReadAllText(combat));
            var gj = goals == null ? null : (JObject)BridgeJson.Parse(File.ReadAllText(goals));
            var o = await TryOnceAsync(dj, gj, chat, playing, url, model, mode, makeBackend, null, cj, combatCycle, tradeTurn);
            return Report(o, stdout);
        }
    }
}

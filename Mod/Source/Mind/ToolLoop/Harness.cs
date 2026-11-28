// Port of core/toolloop/harness.py: capability probe and the one-object front door.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    public sealed class Capabilities
    {
        public bool Grammar;
        public bool NativeTools;
        /// <summary>A reply (text or a call) came back after a tool result, every time.</summary>
        public bool NativeRound2;
        public string Notes = "";

        public JObject ToDict() => new JObject { ["grammar"] = Grammar, ["native_tools"] = NativeTools, ["native_round2"] = NativeRound2, ["notes"] = Notes };
    }

    public static class ToolProbe
    {
        private static readonly JObject Ping = new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = "ping",
                ["description"] = "Report that you are here.",
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["who"] = new JObject { ["type"] = "string" } },
                    ["required"] = new JArray("who"),
                    ["additionalProperties"] = false,
                },
            },
        };
        private static readonly string[] Round2Prompts = { "Say hello to Ann by calling ping with who set to Ann.", "Call ping with who set to Bob.", "Use ping now, who is Cy." };

        private static readonly Dictionary<string, Capabilities> Cache = new Dictionary<string, Capabilities>();

        public static void ClearCache() { lock (Cache) Cache.Clear(); }

        private static JObject Msg(string role, string content) => new JObject { ["role"] = role, ["content"] = content };

        /// <summary>Ask the endpoint what it can do. Cached by key (default: the backend's base+model) for the process.</summary>
        public static async Task<Capabilities> ProbeAsync(IBackend backend, string key = null, bool force = false, int rounds = 3)
        {
            string k = key;
            if (k == null)
            {
                if (backend is IBackendIdentity id) k = id.Base + "|" + id.Model;
                else k = RuntimeHelpers.GetHashCode(backend).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|";
            }
            lock (Cache) if (!force && Cache.TryGetValue(k, out var hit)) return hit;
            var caps = new Capabilities();
            var notes = new List<string>();
            try
            {
                int ok = 0;
                foreach (var p in new[] { "Write anything you like.", "Say a color." })
                {
                    var r = await backend.ChatAsync(new[] { Msg("user", p) }, grammar: "root ::= \"zebra-7\" | \"quokka-9\"", maxTokens: 8, temperature: 0.8, timeout: 20).ConfigureAwait(false);
                    string t = PyStr.Strip(r.Text);
                    if (t == "zebra-7" || t == "quokka-9") ok++;
                }
                caps.Grammar = ok == 2;
            }
            catch (BackendError e) { notes.Add("grammar probe: " + e.Message); }
            try
            {
                int good = 0;
                bool anyCall = false;
                var tools = new JArray(Ping);
                foreach (var p in Round2Prompts.Take(Math.Max(rounds, 0)))
                {
                    var msgs = new List<JObject> { Msg("system", "You act by calling tools."), Msg("user", p) };
                    var r = await backend.ChatAsync(msgs, tools: tools, toolChoice: "auto", maxTokens: 60, temperature: 0.2, timeout: 30).ConfigureAwait(false);
                    if (r.ToolCalls.Count == 0) continue;
                    anyCall = true;
                    var tc = r.ToolCalls[0];
                    JObject args;
                    try
                    {
                        JToken a = tc.Arguments is JObject ? tc.Arguments : (tc.Arguments != null && tc.Arguments.Type == JTokenType.String ? PyJson.Loads((string)tc.Arguments) : throw new PyTypeError("the JSON object must be str, bytes or bytearray"));
                        args = a as JObject;
                        if (args == null || tc.Name != "ping") continue;
                    }
                    catch (Exception) { continue; }
                    var msgs2 = new List<JObject>(msgs)
                    {
                        new JObject
                        {
                            ["role"] = "assistant", ["content"] = "",
                            ["tool_calls"] = new JArray(new JObject
                            {
                                ["id"] = tc.Id, ["type"] = "function",
                                ["function"] = new JObject { ["name"] = "ping", ["arguments"] = PyJson.Dumps(args) },
                            }),
                        },
                        new JObject { ["role"] = "tool", ["tool_call_id"] = tc.Id, ["name"] = "ping", ["content"] = "ok" },
                    };
                    var r2 = await backend.ChatAsync(msgs2, tools: tools, toolChoice: "auto", maxTokens: 60, temperature: 0.2, timeout: 30).ConfigureAwait(false);
                    if (PyStr.Strip(r2.Text).Length > 0 || r2.ToolCalls.Count > 0) good++;
                }
                caps.NativeTools = anyCall;
                caps.NativeRound2 = anyCall && good == rounds;
            }
            catch (BackendError e) { notes.Add("native probe: " + e.Message); }
            caps.Notes = string.Join("; ", notes);
            lock (Cache) Cache[k] = caps;
            return caps;
        }

        /// <summary>auto: grammar when the endpoint has it. Otherwise native only if its round 2 works.</summary>
        public static string ChooseProtocol(Capabilities caps, string mode = "auto")
        {
            if (mode == "grammar" || mode == "json" || mode == "native") return mode;
            if (mode == "auto" && caps.Grammar) return "grammar";
            return caps.NativeTools && caps.NativeRound2 ? "native" : "json";
        }
    }

    /// <summary>Register tools once; RunAsync a loop per request. The protocol is probed once and cached.</summary>
    public sealed class ToolHarness
    {
        public IBackend Backend { get; }
        public List<ToolSpec> Tools { get; }
        public string Mode { get; }
        public string CacheKey { get; }
        public LoopOptions Limits { get; }
        public Capabilities Caps { get; private set; }
        public string Protocol { get; private set; }
        /// <summary>The calls.jsonl kind of this harness's loop calls (steward, steward_combat, steward_trade, wants ...).</summary>
        public string Kind { get; set; } = "toolloop";

        public ToolHarness(IBackend backend, IEnumerable<ToolSpec> tools, string mode = "auto", string cacheKey = null,
                           int maxSteps = 4, int maxRetries = 2, double timeBudgetS = 40.0, double stepTimeoutS = 20.0)
        {
            Tools = tools.ToList();
            if (Tools.Select(t => t.Name).Distinct().Count() != Tools.Count) throw new PyValueError("duplicate tool names");
            Backend = backend; Mode = mode; CacheKey = cacheKey;
            Limits = new LoopOptions { MaxSteps = maxSteps, MaxRetries = maxRetries, TimeBudgetS = timeBudgetS, StepTimeoutS = stepTimeoutS };
        }

        public async Task<string> EnsureAsync()
        {
            if (Protocol == null)
            {
                if (Mode == "grammar" || Mode == "json" || Mode == "native") Protocol = Mode;
                else
                {
                    using (ToolCallKind.Push("probe")) Caps = await ToolProbe.ProbeAsync(Backend, CacheKey).ConfigureAwait(false);
                    Protocol = ToolProbe.ChooseProtocol(Caps, Mode);
                }
            }
            return Protocol;
        }

        /// <summary>over: per-run overrides applied on a copy of the harness limits (Python: **over).</summary>
        public async Task<LoopResult> RunAsync(string system, string user, Func<string, JObject, Task<JToken>> executor,
                                               Func<string, JObject, Task<string>> validator = null, Func<LoopResult, LoopAction> fallback = null,
                                               Action<LoopOptions> over = null)
        {
            string proto = await EnsureAsync().ConfigureAwait(false);
            var o = Limits.Clone();
            over?.Invoke(o);
            using (ToolCallKind.Push(Kind, true)) return await Loop.RunAsync(Backend, Tools, system, user, executor, proto, validator, fallback, o).ConfigureAwait(false);
        }
    }
}

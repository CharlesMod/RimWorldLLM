// Port of core/toolloop/loop.py: every model step is constrained or validated, retried a bounded number of times, then replaced
// by a deterministic fallback. It always ends, never raises for a model fault, and returns a trace.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.ToolLoop
{
    /// <summary>A step in game terms: Args are in the model's own words (scale phrases), not game numbers. Python: Action.</summary>
    public sealed class LoopAction
    {
        public string Kind;                   // call | final | done
        public string Tool = "";
        public JObject Args = new JObject();
        public string Text = "";
        public LoopAction(string kind, string tool = "", JObject args = null, string text = "")
        {
            Kind = kind; Tool = tool; Args = args ?? new JObject(); Text = text;
        }
        /// <summary>A fixed fallback (Python passes an Action where a factory is also allowed).</summary>
        public Func<LoopResult, LoopAction> AsFallback() => _ => this;
    }

    public sealed class StepTrace
    {
        public int Step, Attempt;
        public string Protocol;
        public string Raw = "";
        public List<ToolCall> RawCalls = new List<ToolCall>();
        public string Kind = "";              // call | final | done | invalid | backend_error | timeout | away
        public string Error = "";
        public double Ms;
        public string Salvaged = "";

        public JObject ToDict() => new JObject
        {
            ["step"] = Step, ["attempt"] = Attempt, ["protocol"] = Protocol, ["raw"] = Raw,
            ["raw_calls"] = new JArray(RawCalls.Select(c => (object)new JObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.Arguments?.DeepClone() }).ToArray()),
            ["kind"] = Kind, ["error"] = Error, ["ms"] = Ms, ["salvaged"] = Salvaged,
        };
    }

    public sealed class Executed
    {
        public string Tool;
        public JObject Args;                  // model words
        public JObject Values;                // game values after translation
        public string Result = "";
        public string Error = "";             // executor fault (not a model fault)
        public bool Fallback;

        public JObject ToDict() => new JObject
        {
            ["tool"] = Tool, ["args"] = Args.DeepClone(), ["values"] = Values.DeepClone(), ["result"] = Result, ["error"] = Error, ["fallback"] = Fallback,
        };
    }

    public sealed class LoopResult
    {
        public string Outcome = "gave_up";    // final | done | stopped | fallback | gave_up
        public string Reason = "";
        public string FinalText = "";
        public List<Executed> Executed = new List<Executed>();
        public List<StepTrace> Trace = new List<StepTrace>();
        public string Protocol = "";
        public int Retries;
        public int Fallbacks;
        public List<string> Failures = new List<string>();
        public bool ImplicitDone;
        public List<string> Allowed = new List<string>();
        public double ElapsedS;

        public bool Completed => Outcome == "final" || Outcome == "done" || Outcome == "stopped";

        public JObject ToDict() => new JObject
        {
            ["outcome"] = Outcome, ["reason"] = Reason, ["final_text"] = FinalText,
            ["executed"] = new JArray(Executed.Select(e => (object)e.ToDict()).ToArray()),
            ["trace"] = new JArray(Trace.Select(t => (object)t.ToDict()).ToArray()),
            ["protocol"] = Protocol, ["retries"] = Retries, ["fallbacks"] = Fallbacks,
            ["failures"] = new JArray(Failures.Select(f => (object)f).ToArray()),
            ["implicit_done"] = ImplicitDone,
            ["allowed"] = new JArray(Allowed.Select(f => (object)f).ToArray()),
            ["elapsed_s"] = ElapsedS,
        };
    }

    /// <summary>run_loop's keyword arguments. Allowed: the tool names this scene may use (null: all); Slot pins a llama.cpp slot.</summary>
    public sealed class LoopOptions
    {
        public string Protocol = "grammar";
        public int MaxSteps = 4, MaxRetries = 2;
        public double TimeBudgetS = 40.0, StepTimeoutS = 20.0;
        public int FinalMax = 200, MaxTokens = 200;
        public double Temperature = 0.2;
        public bool FallbackAfterActions;
        public IEnumerable<string> Allowed;
        public int? Slot;
        /// <summary>Streaming (not in the Python): when set and the backend is an IStreamingBackend, each model step goes through
        /// its options overload, so the sink sees Sent/Queued/Progress/FirstToken/Partial. Null: the ordinary request, byte for byte.</summary>
        public CallOptions CallOptions;
        /// <summary>time.monotonic() in seconds; injectable for tests.</summary>
        public Func<double> Clock = AwayGate.MonotonicS;

        public LoopOptions Clone() => (LoopOptions)MemberwiseClone();
    }

    internal sealed class StepTimeout : Exception { }

    public static class Loop
    {
        public static readonly string[] Protocols = { "grammar", "json", "native" };
        public const string NextPrompt = "Reply with the next JSON object: another tool call, a final line, or done.";

        public static Func<string, JObject, Task<JToken>> Sync(Func<string, JObject, JToken> f) => (n, v) => Task.FromResult(f(n, v));
        public static Func<string, JObject, Task<string>> SyncCheck(Func<string, JObject, string> f) => (n, v) => Task.FromResult(f(n, v));

        private static string Fault(string prefix, Exception e) => PyStr.Head(prefix + PyNames.Of(e) + ": " + e.Message, 200);

        private static (string text, string err) OneLine(string text, bool tolerant, int maxLen)
        {
            string raw = text ?? "";
            string stripped = PyStr.Strip(raw);
            if ((stripped.Contains('\n') || stripped.Contains('\r')) && !tolerant) return ("", "the final line must be a single line");
            string t = PyStr.CollapseSpaces(raw);
            if (t.Length == 0) return ("", "the final line is empty");
            if (PyStr.Len(t) > maxLen) return ("", "the final line is longer than " + maxLen + " characters");
            if (Syntax.HasRawSyntax(t) || t.IndexOfAny(new[] { '<', '>', '{', '}' }) >= 0) return ("", "the final line contains tool syntax");
            return (t, "");
        }

        private static async Task<T> WaitFor<T>(Task<T> t, double seconds)
        {
            var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 0)))).ConfigureAwait(false);
            if (done != t)
            {
                _ = t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new StepTimeout();
            }
            return await t.ConfigureAwait(false);
        }

        private static string SeenKey(string tool, JObject values) => tool + PyJson.Compact(values, true, true);

        private sealed class Item
        {
            public LoopAction Action; public string Note; public JObject Values;
        }

        public static async Task<LoopResult> RunAsync(IBackend backend, IReadOnlyList<ToolSpec> toolsIn, string system, string user,
                                                      Func<string, JObject, Task<JToken>> executor, string protocol = "grammar",
                                                      Func<string, JObject, Task<string>> validator = null, Func<LoopResult, LoopAction> fallback = null,
                                                      LoopOptions options = null)
        {
            var o = options ?? new LoopOptions();
            if (protocol == null) protocol = o.Protocol;
            if (!Protocols.Contains(protocol)) throw new PyValueError("unknown protocol " + protocol);
            IReadOnlyList<ToolSpec> tools = toolsIn;
            if (o.Allowed != null)
            {
                var keep = new HashSet<string>(o.Allowed);
                tools = tools.Where(t => keep.Contains(t.Name)).ToList();
            }
            var res = new LoopResult { Protocol = protocol, Allowed = tools.Select(t => t.Name).ToList() };
            double tStart = o.Clock();
            double deadline = tStart + o.TimeBudgetS;
            var byName = new Dictionary<string, ToolSpec>();
            foreach (var t in tools) byName[t.Name] = t;
            bool tolerant = protocol != "grammar";
            JArray nativeTools = protocol == "native" ? new JArray(tools.Select(t => (object)t.OpenAiTool()).ToArray()) : null;
            var hist = new List<JObject>();
            if (protocol == "native") { hist.Add(Msg("system", system)); hist.Add(Msg("user", user)); }
            else { hist.Add(Msg("system", Prompt.RenderSystem(system, tools))); hist.Add(Msg("user", user)); }
            var seen = new HashSet<string>();
            int stepsDone = 0;
            string reason = "";
            try
            {
                while (true)
                {
                    if (stepsDone >= o.MaxSteps) { reason = "step_budget"; break; }
                    bool closing = stepsDone == o.MaxSteps - 1 && o.MaxSteps > 1;
                    string grammar = null;
                    if (protocol == "grammar") grammar = Grammar.Build(closing ? new List<ToolSpec>() : tools, o.FinalMax);
                    var stepMsgs = new List<JObject>(hist);
                    if (closing && protocol != "grammar")
                        stepMsgs.Add(Msg("user", "This is the last step: no more tool calls. Reply with a final line or done."));
                    (List<Item> actions, bool implicit_)? accepted = null;
                    string failReason = "";
                    int attempt = 0;
                    while (attempt <= o.MaxRetries)
                    {
                        double remaining = deadline - o.Clock();
                        if (remaining <= 0) { reason = "time_budget"; break; }
                        if (attempt > 0) res.Retries++;
                        var tr = new StepTrace { Step = stepsDone, Attempt = attempt, Protocol = protocol };
                        res.Trace.Add(tr);
                        double t0 = o.Clock();
                        Reply reply = null;
                        try
                        {
                            double to = Math.Min(o.StepTimeoutS, remaining);
                            string choice = nativeTools != null && nativeTools.Count > 0 ? "auto" : null;
                            Task<Reply> call = o.CallOptions != null && backend is IStreamingBackend sb
                                ? sb.ChatAsync(stepMsgs, grammar, nativeTools, choice, o.MaxTokens, o.Temperature, to, o.Slot, o.CallOptions)
                                : backend.ChatAsync(stepMsgs, grammar, nativeTools, choice, o.MaxTokens, o.Temperature, to, o.Slot);
                            reply = await WaitFor(call, to + 1.0).ConfigureAwait(false);
                        }
                        catch (StepTimeout) { tr.Kind = "timeout"; tr.Error = "the model took too long"; }
                        catch (BackendAway e) { tr.Kind = "away"; tr.Error = e.Message; }
                        catch (BackendError e) { tr.Kind = "backend_error"; tr.Error = e.Message; }
                        catch (Exception e) { tr.Kind = "backend_error"; tr.Error = Fault("", e); }
                        tr.Ms = (o.Clock() - t0) * 1000;
                        if (reply != null)
                        {
                            tr.Raw = reply.Text; tr.RawCalls = reply.ToolCalls;
                            var (actions, err, implicit_) = await Interpret(reply, protocol, byName, tolerant, o.FinalMax, validator, seen,
                                                                           res.Executed.Count == 0 && stepsDone == 0, res.Executed.Count > 0, closing).ConfigureAwait(false);
                            var notes = actions.Select(a => a.Note).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                            notes.Sort(StringComparer.Ordinal);
                            tr.Salvaged = string.Join("; ", notes);
                            if (!string.IsNullOrEmpty(err)) { tr.Kind = "invalid"; tr.Error = err; }
                            else { tr.Kind = actions.Count > 0 ? actions[0].Action.Kind : "done"; accepted = (actions, implicit_); }
                        }
                        if (accepted != null) break;
                        failReason = tr.Error;
                        res.Failures.Add("step " + stepsDone + " attempt " + attempt + ": " + tr.Error);
                        if (tr.Kind == "away") { reason = "away"; break; }
                        attempt++;
                        stepMsgs = new List<JObject>(hist)
                        {
                            Msg("assistant", PyStr.Head(reply != null && PyStr.Strip(reply.Text).Length > 0 ? reply.Text : "(no reply)", 600)),
                            Msg("user", "That reply was rejected: " + tr.Error + ". " + (protocol != "native" ? NextPrompt : "Try again.")),
                        };
                        if (closing && protocol != "grammar") stepMsgs.Add(Msg("user", "Remember: no more tool calls now."));
                    }
                    if (accepted == null)
                    {
                        if (reason != "time_budget" && reason != "away") reason = failReason.Contains("already") ? "repeat" : "retries_exhausted";
                        break;
                    }
                    var acts = accepted.Value.actions;
                    stepsDone++;
                    if (accepted.Value.implicit_) res.ImplicitDone = true;
                    var first = acts.Count > 0 ? acts[0].Action : new LoopAction("done");
                    if (first.Kind == "final") { res.Outcome = "final"; res.FinalText = first.Text; break; }
                    if (first.Kind == "done") { res.Outcome = "done"; break; }
                    await RunCalls(acts, byName, executor, res, seen, hist, protocol).ConfigureAwait(false);
                    if (acts.Any(a => a.Action.Kind == "call" && byName.TryGetValue(a.Action.Tool, out var sp) && sp.EndsTurn))
                    {
                        res.Outcome = "done";
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                res.Failures.Add(Fault("loop fault ", e));
                if (reason.Length == 0) reason = "loop_fault";
            }
            if (res.Outcome != "final" && res.Outcome != "done")
            {
                res.Reason = reason;
                if (res.Executed.Count > 0 && !o.FallbackAfterActions) res.Outcome = "stopped";
                else await ApplyFallback(fallback, byName, executor, res).ConfigureAwait(false);
            }
            res.ElapsedS = o.Clock() - tStart;
            return res;
        }

        private static JObject Msg(string role, string content) => new JObject { ["role"] = role, ["content"] = content };

        private static async Task<(List<Item> actions, string err, bool implicit_)> Interpret(Reply reply, string protocol, Dictionary<string, ToolSpec> byName,
            bool tolerant, int finalMax, Func<string, JObject, Task<string>> validator, HashSet<string> seen, bool firstStep, bool hadCalls, bool closing)
        {
            var steps = new List<(RawStep step, string note)>();
            if (protocol == "native" && reply.ToolCalls.Count > 0)
            {
                foreach (var tc in reply.ToolCalls)
                {
                    JToken a = tc.Arguments;
                    if (a != null && a.Type == JTokenType.String)
                    {
                        string s = (string)a;
                        try { a = PyStr.Strip(s).Length > 0 ? PyJson.Loads(s) : new JObject(); }
                        catch (PyJsonException) { return (new List<Item>(), "the arguments of " + tc.Name + " were not valid JSON", false); }
                    }
                    steps.Add((new RawStep("call") { Tool = tc.Name ?? "", Args = a }, ""));
                }
            }
            else
            {
                string text = reply.Text ?? "";
                if (protocol == "native")
                {
                    if (PyStr.Strip(text).Length == 0)
                    {
                        if (hadCalls) return (new List<Item>(), "", true);
                        return (new List<Item>(), "the reply was empty", false);
                    }
                    var (s, _) = StepParser.ParseText(text, new HashSet<string>(byName.Keys));
                    steps.Add(s != null ? (s, s.Salvaged) : (new RawStep("final") { Text = text }, ""));
                }
                else
                {
                    var (s, why) = StepParser.ParseText(text, new HashSet<string>(byName.Keys));
                    if (s == null) return (new List<Item>(), why, false);
                    steps.Add((s, s.Salvaged));
                }
            }
            var outp = new List<Item>();
            var batchSeen = new HashSet<string>();
            foreach (var (s, note) in steps)
            {
                if (s.Kind == "done") outp.Add(new Item { Action = new LoopAction("done"), Note = note, Values = new JObject() });
                else if (s.Kind == "final")
                {
                    var (t, err) = OneLine(s.Text, tolerant, finalMax);
                    if (err.Length > 0) return (new List<Item>(), err, false);
                    outp.Add(new Item { Action = new LoopAction("final", text: t), Note = note, Values = new JObject() });
                }
                else
                {
                    if (closing) return (new List<Item>(), "no more tool calls are allowed now; reply with a final line or done", false);
                    if (!byName.TryGetValue(s.Tool, out ToolSpec spec))
                        return (new List<Item>(), "unknown tool \"" + s.Tool + "\"; the tools are: " + string.Join(", ", byName.Keys), false);
                    var (values, err) = spec.CheckArgs(s.Args, tolerant);
                    if (err != null) return (new List<Item>(), err, false);
                    if (validator != null)
                    {
                        string msg;
                        try { msg = await validator(spec.Name, values).ConfigureAwait(false); }
                        catch (Exception e) { msg = "the check failed (" + PyNames.Of(e) + ")"; }
                        if (!string.IsNullOrEmpty(msg)) return (new List<Item>(), msg, false);
                    }
                    string key = SeenKey(spec.Name, values);
                    if (seen.Contains(key) || batchSeen.Contains(key)) return (new List<Item>(), spec.Name + " with these arguments was already done", false);
                    batchSeen.Add(key);
                    outp.Add(new Item { Action = new LoopAction("call", spec.Name, s.Args as JObject ?? new JObject()), Note = note, Values = values });
                }
            }
            if (outp.Count > 1 && outp.Any(a => a.Action.Kind != "call")) return (new List<Item>(), "mixed a final line or done with tool calls in one step", false);
            return (outp, "", false);
        }

        private static async Task RunCalls(List<Item> actions, Dictionary<string, ToolSpec> byName, Func<string, JObject, Task<JToken>> executor,
                                           LoopResult res, HashSet<string> seen, List<JObject> hist, string protocol)
        {
            var doneCalls = new List<(LoopAction a, Executed ex)>();
            foreach (var it in actions)
            {
                var a = it.Action;
                var ex = new Executed { Tool = a.Tool, Args = (JObject)a.Args.DeepClone(), Values = (JObject)it.Values.DeepClone() };
                try
                {
                    JToken r = await executor(a.Tool, it.Values).ConfigureAwait(false);
                    ex.Result = byName[a.Tool].RenderResult(r);
                }
                catch (Exception e)
                {
                    ex.Error = Fault("", e);
                    ex.Result = "error: " + ex.Error;
                }
                seen.Add(SeenKey(a.Tool, it.Values));
                res.Executed.Add(ex);
                doneCalls.Add((a, ex));
            }
            if (protocol == "native")
            {
                int baseIdx = res.Executed.Count - doneCalls.Count;
                var calls = new JArray();
                for (int i = 0; i < doneCalls.Count; i++)
                    calls.Add(new JObject
                    {
                        ["id"] = "call_" + (baseIdx + i), ["type"] = "function",
                        ["function"] = new JObject { ["name"] = doneCalls[i].a.Tool, ["arguments"] = PyJson.Dumps(doneCalls[i].a.Args, false) },
                    });
                hist.Add(new JObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = calls });
                for (int i = 0; i < doneCalls.Count; i++)
                    hist.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = "call_" + (baseIdx + i), ["name"] = doneCalls[i].a.Tool, ["content"] = doneCalls[i].ex.Result });
            }
            else
            {
                var d = new JObject { ["tool"] = doneCalls[0].a.Tool, ["args"] = doneCalls[0].a.Args };
                hist.Add(Msg("assistant", PyJson.Dumps(d, false)));
                string lines = string.Join("\n", doneCalls.Select(x => "Result of " + x.a.Tool + ": " + x.ex.Result));
                hist.Add(Msg("user", lines + "\n" + NextPrompt));
            }
        }

        private static async Task ApplyFallback(Func<LoopResult, LoopAction> fallback, Dictionary<string, ToolSpec> byName,
                                                Func<string, JObject, Task<JToken>> executor, LoopResult res)
        {
            LoopAction act = null;
            try { act = fallback?.Invoke(res); }
            catch (Exception e) { res.Failures.Add("fallback factory fault " + PyNames.Of(e)); }
            res.Outcome = act != null ? "fallback" : "gave_up";
            if (act == null) return;
            res.Fallbacks++;
            if (act.Kind == "final") res.FinalText = PyStr.CollapseSpaces(act.Text);
            else if (act.Kind == "call")
            {
                byName.TryGetValue(act.Tool, out ToolSpec spec);
                var (values, err) = spec != null ? spec.CheckArgs(act.Args, true) : (new JObject(), "unknown fallback tool");
                if (err != null) { res.Failures.Add("fallback invalid: " + err); return; }
                var ex = new Executed { Tool = act.Tool, Args = (JObject)act.Args.DeepClone(), Values = values, Fallback = true };
                try { ex.Result = spec.RenderResult(await executor(act.Tool, values).ConfigureAwait(false)); }
                catch (Exception e) { ex.Error = Fault("", e); }
                res.Executed.Add(ex);
            }
        }
    }
}

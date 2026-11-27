// Port of agent/tests/test_toolloop.py: the tool harness against a scripted fake model that misbehaves in every listed way.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    internal static class Fx
    {
        public static List<ToolSpec> Tools() => new List<ToolSpec>
        {
            new ToolSpec("say", "Say a line.", new[] { new Param("line", maxLen: 60) }),
            new ToolSpec("remember", "Store a fact.", new[] { new Param("fact"), new Param("importance", "scale", scale: Scale.Importance) }),
            new ToolSpec("set_mood", "Feelings.", new[] { new Param("target", "enum", choices: new[] { "Pip", "Lux" }), new Param("delta", "scale", scale: Scale.Change) },
                         new Dictionary<string, Scale> { ["opinion"] = Scale.Change }),
            new ToolSpec("count", "A number.", new[] { new Param("n", "int", lo: 1, hi: 5) }),
            new ToolSpec("do_nothing", "Nothing."),
        };

        public static List<ToolSpec> OddTools()
        {
            var sc = new Scale("mood", new[] { ("so-so", 0), ("grand", 7) });
            return new List<ToolSpec>
            {
                new ToolSpec("pick", "Pick \"one\"; \\ here.", new[]
                {
                    new Param("who", "enum", choices: new[] { "Pip", "Lux \"the\" fox", "Zoë\\x", "日本" }, description: "who"),
                    new Param("size", "int", lo: -5, hi: 60),
                    new Param("mood", "scale", scale: sc, required: false),
                    new Param("note", maxLen: 7, description: "a note", required: false),
                    new Param("big", "int", lo: 0, hi: 39, required: false),
                }),
            };
        }

        public static List<ToolSpec> TurnEnding() => Tools().Select(t => t.Name != "do_nothing" ? t : new ToolSpec("do_nothing", "Nothing.", endsTurn: true)).ToList();

        public static string J(string key, JToken v) => PyJson.Dumps(new JObject { [key] = v });
        public static string Call(string tool, params (string k, JToken v)[] args)
        {
            var a = new JObject();
            foreach (var (k, v) in args) a[k] = v;
            return PyJson.Dumps(new JObject { ["tool"] = tool, ["args"] = a });
        }
        public static (string, JToken) A(string k, object v) => (k, JToken.FromObject(v));
    }

    /// <summary>Replies come from a script: Reply, string, an Exception to raise, or a Func(messages) returning those. The last entry repeats.</summary>
    internal sealed class FakeBackend : IBackend
    {
        public sealed class CallRec { public IReadOnlyList<JObject> Messages; public string Grammar; public JArray Tools; public double Timeout; }
        private readonly List<object> _script;
        public readonly List<CallRec> Calls = new List<CallRec>();
        public FakeBackend(params object[] script) { _script = script.ToList(); }

        public async Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                           int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
        {
            Calls.Add(new CallRec { Messages = messages, Grammar = grammar, Tools = tools, Timeout = timeout });
            object item;
            if (_script.Count > 1) { item = _script[0]; _script.RemoveAt(0); } else item = _script[0];
            if (item is Func<IReadOnlyList<JObject>, object> f) item = f(messages);
            if (item is Exception e) throw e;
            if (item is Task t) { await t; item = null; }
            return item is Reply r ? r : new Reply { Text = (string)item };
        }
    }

    internal sealed class Rec
    {
        public readonly List<(string name, JObject values)> Calls = new List<(string, JObject)>();
        public Func<string, JObject, Task<JToken>> Executor => (n, v) =>
        {
            Calls.Add((n, v));
            if (n == "set_mood") return Task.FromResult<JToken>(new JObject { ["opinion"] = v.TryGetValue("delta", out var d) ? d : new JValue(0) });
            return Task.FromResult<JToken>(new JValue("ok"));
        };
        public List<string> Shown() => Calls.Select(c => c.name + "|" + PyJson.Dumps(c.values, false)).ToList();
    }

    public class ToolLoopTests
    {
        private static T Block<T>(Task<T> t) => t.GetAwaiter().GetResult();

        private static readonly object NoFallback = new object();

        private static (LoopResult r, Rec rec) Go(FakeBackend f, Rec rec = null, string protocol = "grammar", Action<LoopOptions> o = null,
                                                   Func<LoopResult, LoopAction> fallback = null, bool noFallback = false,
                                                   Func<string, JObject, Task<string>> validator = null, Func<string, JObject, Task<JToken>> executor = null,
                                                   List<ToolSpec> tools = null)
        {
            rec = rec ?? new Rec();
            if (fallback == null && !noFallback) fallback = new LoopAction("call", "do_nothing").AsFallback();
            var opts = new LoopOptions();
            o?.Invoke(opts);
            var r = Block(Loop.RunAsync(f, tools ?? Fx.Tools(), "You are a cook.", "Do things.", executor ?? rec.Executor, protocol, validator, fallback, opts));
            return (r, rec);
        }

        private static string J(string k, JToken v) => Fx.J(k, v);
        private static string Call(string tool, params (string, JToken)[] a) => Fx.Call(tool, a);
        private static (string, JToken) A(string k, object v) => Fx.A(k, v);

        [Fact]
        public void HappyPathCallThenFinalAndScaleTranslation()
        {
            var f = new FakeBackend(Call("remember", A("fact", "Lux stole bread"), A("importance", "very important")), J("final", "Noted."));
            var (r, rec) = Go(f);
            Assert.True(r.Outcome == "final" && r.FinalText == "Noted." && r.Retries == 0 && r.Fallbacks == 0);
            Assert.Equal(new[] { "remember|{\"fact\": \"Lux stole bread\", \"importance\": 4}" }, rec.Shown());
            var second = f.Calls[1].Messages;
            Assert.True(second.Last()["role"].ToString() == "user" && second.Last()["content"].ToString().Contains("Result of remember: ok"));
            Assert.True(second.All(m => (string)m["role"] != "tool") && f.Calls.All(c => !string.IsNullOrEmpty(c.Grammar)));
        }

        [Fact]
        public void NumbersShownBackAsWords()
        {
            var f = new FakeBackend(Call("set_mood", A("target", "Pip"), A("delta", "a little better")), J("done", true));
            var (r, rec) = Go(f);
            Assert.Equal(1, (int)rec.Calls[0].values["delta"]);
            Assert.Contains("Result of set_mood: {\"opinion\": \"a little better\"}", (string)f.Calls[1].Messages.Last()["content"]);
        }

        [Fact]
        public void EmptyRepliesRetryThenFallbackNeverSilent()
        {
            var f = new FakeBackend("");
            var (r, rec) = Go(f);
            Assert.True(r.Outcome == "fallback" && r.Fallbacks == 1 && r.Retries == 2 && f.Calls.Count == 3);
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
            Assert.True(r.Failures.Count == 3 && r.Failures.All(x => x.Contains("empty")));
        }

        [Fact]
        public void RetryPromptStatesTheReasonAndSecondTryRecovers()
        {
            var f = new FakeBackend("I think I will say something", Call("say", A("line", "Hello.")), J("done", true));
            var (r, rec) = Go(f);
            Assert.True(r.Retries == 1 && r.Fallbacks == 0);
            Assert.Equal(new[] { "say|{\"line\": \"Hello.\"}" }, rec.Shown());
            string last = (string)f.Calls[1].Messages.Last()["content"];
            Assert.True(last.Contains("rejected") && last.Contains("not one JSON object"));
        }

        [Fact]
        public void HallucinatedToolIsNeverExecuted()
        {
            var f = new FakeBackend(Call("launch_missiles", A("target", "Pip")));
            var (r, rec) = Go(f);
            Assert.Equal("fallback", r.Outcome);
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
            Assert.True(r.Failures[0].Contains("unknown tool") && r.Failures[0].Contains("say, remember"));
        }

        public static IEnumerable<object[]> BadCalls()
        {
            var bad = new List<string>
            {
                Call("count", A("n", 100)), Call("count", A("n", -20)), Call("count", A("n", "three")), Call("count", A("n", 2.5)),
                Call("remember", A("fact", "x"), A("importance", 100)), Call("remember", A("fact", "x"), A("importance", "pretty important")),
                Call("set_mood", A("target", "Zed"), A("delta", "worse")), Call("say", A("line", "")), Call("say", A("line", new string('x', 200))), Call("say"),
                Call("say", A("line", "hi"), A("extra", 1)), Call("say", A("line", "<|tool_call>call:say{x}")), Call("say", A("line", new[] { "a" })),
            };
            return bad.Select(b => new object[] { b });
        }

        [Theory, MemberData(nameof(BadCalls))]
        public void InvalidArgumentsNeverReachTheExecutorInGrammarMode(string bad)
        {
            var f = new FakeBackend(bad);
            var (r, rec) = Go(f);
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
            Assert.True(r.Outcome == "fallback" && r.Retries == 2);
        }

        [Fact]
        public void TolerantModeMapsFreeTextToTheNearestBinButStillRejectsRangeErrors()
        {
            var f = new FakeBackend(Call("remember", A("fact", "x"), A("importance", "pretty important")), Call("count", A("n", 100)), J("done", true));
            var (r, rec) = Go(f, protocol: "json");
            Assert.Equal("remember|{\"fact\": \"x\", \"importance\": 3}", rec.Shown()[0]);
            Assert.True(r.Retries == 1 && r.Outcome == "done");
        }

        [Fact]
        public void EndlessToolCallsEndAtTheStepBudgetAndLastStepForbidsCalls()
        {
            int n = 0;
            var f = new FakeBackend((Func<IReadOnlyList<JObject>, object>)(m => Call("say", A("line", "line " + n++))));
            var (r, rec) = Go(f, o: x => x.MaxSteps = 3);
            Assert.True(r.Outcome == "stopped" && rec.Calls.Count == 2 && r.Executed.Count == 2);
            Assert.Equal("retries_exhausted", r.Reason);
            string lastGrammar = f.Calls.Where(c => !string.IsNullOrEmpty(c.Grammar)).Last().Grammar;
            Assert.True(!lastGrammar.Contains("call0") && lastGrammar.Contains("final"));
        }

        [Fact]
        public void RepeatingTheSameCallIsRefusedAndEndsTheLoop()
        {
            var f = new FakeBackend(Call("say", A("line", "Hi.")));
            var (r, rec) = Go(f, o: x => x.MaxSteps = 6);
            Assert.Equal(new[] { "say|{\"line\": \"Hi.\"}" }, rec.Shown());
            Assert.True(r.Outcome == "stopped" && r.Reason == "repeat");
            Assert.Contains(r.Failures, x => x.Contains("already"));
        }

        [Fact]
        public void ACommentaryParamDoesNotMakeARepeatANewCall()
        {
            var ts = new List<ToolSpec>
            {
                new ToolSpec("count", "A number.", new[] { new Param("n", "int", lo: 1, hi: 5), new Param("why", maxLen: 40, required: false, sameCall: true) }),
                new ToolSpec("do_nothing", "Nothing."),
            };
            var f = new FakeBackend(Call("count", A("n", 2), A("why", "since one")), Call("count", A("n", 2), A("why", "since two")), Call("count", A("n", 3), A("why", "since one")));
            var (r, rec) = Go(f, o: x => x.MaxSteps = 6, tools: ts);
            Assert.Equal(new[] { 2, 3 }, rec.Calls.Select(c => (int)c.values["n"]).ToArray());      // the second n=2 is the same call with another reason: refused
            Assert.Contains(r.Failures, x => x.Contains("already"));
        }

        [Fact]
        public void FallbackAfterActionsIsOptIn()
        {
            var f = new FakeBackend(Call("say", A("line", "Hi.")), "garbage");
            var (r, rec) = Go(f, o: x => x.FallbackAfterActions = true);
            Assert.True(r.Outcome == "fallback" && rec.Shown().Last() == "do_nothing|{}");
            (r, rec) = Go(new FakeBackend(Call("say", A("line", "Hi.")), "garbage"));
            Assert.Equal("stopped", r.Outcome);
            Assert.Equal(new[] { "say|{\"line\": \"Hi.\"}" }, rec.Shown());
        }

        [Fact]
        public void TimeoutFallsBackWithinTheTimeBudget()
        {
            var f = new FakeBackend((Func<IReadOnlyList<JObject>, object>)(m => Task.Delay(30000)));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (r, rec) = Go(f, o: x => { x.TimeBudgetS = 0.6; x.StepTimeoutS = 0.2; });
            Assert.True(sw.Elapsed.TotalSeconds < 3 && r.Outcome == "fallback");
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
            Assert.Contains(r.Trace, t => t.Kind == "timeout");
        }

        public static IEnumerable<object[]> Faults() => new[]
        {
            new object[] { new BackendError("HTTP 500") }, new object[] { new InvalidOperationException("boom") },
            new object[] { new FormatException("x") }, new object[] { new System.IO.IOException("down") },
        };

        [Theory, MemberData(nameof(Faults))]
        public void BackendFaultsNeverRaise(Exception exc)
        {
            var (r, rec) = Go(new FakeBackend(exc));
            Assert.True(r.Outcome == "fallback" && !r.Completed);
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
            Assert.All(r.Trace, t => Assert.Equal("backend_error", t.Kind));
        }

        [Fact]
        public void ExecutorFaultIsNotAModelFaultAndIsToldToTheModel()
        {
            var f = new FakeBackend(Call("say", A("line", "Hi.")), J("final", "Sorry."));
            var (r, _) = Go(f, executor: (n, v) => throw new PyException("RuntimeError", "door jammed"));
            Assert.True(r.Outcome == "final" && r.Retries == 0 && r.Executed[0].Error.StartsWith("RuntimeError"));
            Assert.Contains("door jammed", (string)f.Calls[1].Messages.Last()["content"]);
        }

        [Fact]
        public void ValidatorHookCanRejectNamesAndTriggersARetryWithItsReason()
        {
            var f = new FakeBackend(Call("say", A("line", "Zed")), Call("say", A("line", "Pip")), J("done", true));
            var (r, rec) = Go(f, validator: Loop.SyncCheck((n, v) => (string)v["line"] == "Zed" ? "Zed is not here" : null));
            Assert.Equal(new[] { "say|{\"line\": \"Pip\"}" }, rec.Shown());
            Assert.Contains("Zed is not here", (string)f.Calls[1].Messages.Last()["content"]);
        }

        [Fact]
        public void RawToolSyntaxInAFinalLineIsRejected()
        {
            var f = new FakeBackend(J("final", "<|tool_call>call:say{line:<|\"|>hi<|\"|>}<tool_call|>"), J("final", "call:say{x:1}"), J("final", "Fine."));
            var (r, _) = Go(f);
            Assert.True(r.FinalText == "Fine." && r.Retries == 2);
        }

        [Fact]
        public void FinalLineMustBeSingleLineNonEmptyAndBounded()
        {
            var f = new FakeBackend(J("final", "a\nb"), J("final", "  "), J("final", new string('x', 300)), J("final", "ok"));
            var (r, _) = Go(f, o: x => x.MaxRetries = 3);
            Assert.True(r.FinalText == "ok" && r.Retries == 3);
        }

        [Fact]
        public void FallbackCanBeTheLastValidLineViaAFactory()
        {
            var (r, _) = Go(new FakeBackend("garbage"), fallback: res => new LoopAction("final", text: "I will think on it."));
            Assert.True(r.Outcome == "fallback" && r.FinalText == "I will think on it.");
        }

        [Fact]
        public void NoFallbackMeansGaveUpNotAnException()
        {
            var (r, rec) = Go(new FakeBackend("garbage"), noFallback: true);
            Assert.True(r.Outcome == "gave_up" && rec.Calls.Count == 0);
        }

        [Fact]
        public void InvalidFallbackIsRecordedNotExecuted()
        {
            var (r, rec) = Go(new FakeBackend("garbage"), fallback: new LoopAction("call", "count", new JObject { ["n"] = 99 }).AsFallback());
            Assert.True(rec.Calls.Count == 0 && r.Failures.Any(x => x.Contains("fallback invalid")));
        }

        [Fact]
        public void TheTraceRecordsEverything()
        {
            var f = new FakeBackend("nope", Call("say", A("line", "Hi.")), J("done", true));
            var (r, _) = Go(f);
            Assert.Equal(new[] { "0,0,invalid", "0,1,call", "1,0,done" }, r.Trace.Select(t => t.Step + "," + t.Attempt + "," + t.Kind).ToArray());
            Assert.True(r.Trace[0].Raw == "nope" && r.Trace[0].Error.Length > 0 && r.Trace.All(t => t.Ms >= 0));
            var d = r.ToDict();
            Assert.True((string)d["outcome"] == "done" && (string)d["trace"][0]["raw"] == "nope" && (string)d["executed"][0]["tool"] == "say");
        }

        // ------------------------------------------------------------ native protocol
        private static Reply NativeCall(string name, params (string, JToken)[] args)
        {
            var a = new JObject();
            foreach (var (k, v) in args) a[k] = v;
            return new Reply { ToolCalls = { new ToolCall { Id = "c1", Name = name, Arguments = new JValue(PyJson.Dumps(a)) } } };
        }

        [Fact]
        public void NativeProtocolEmptyReplyAfterAToolResultEndsTheLoopCleanly()
        {
            var f = new FakeBackend(NativeCall("say", A("line", "Hi.")), new Reply { Text = "" });
            var (r, rec) = Go(f, protocol: "native");
            Assert.True(r.Outcome == "done" && r.ImplicitDone);
            Assert.Equal(new[] { "say|{\"line\": \"Hi.\"}" }, rec.Shown());
            Assert.True((string)f.Calls[1].Messages.Last()["role"] == "tool" && f.Calls[0].Tools != null && f.Calls[0].Tools.Count > 0);
        }

        [Fact]
        public void NativeProtocolEmptyFirstReplyIsAFailureAndRawGemmaSyntaxIsSalvaged()
        {
            var f = new FakeBackend(new Reply { Text = "" }, new Reply { Text = "say{line:<|\"|>Hi there.<|\"|>}" }, new Reply { Text = "All done, boss." });
            var (r, rec) = Go(f, protocol: "native");
            Assert.Equal(new[] { "say|{\"line\": \"Hi there.\"}" }, rec.Shown());
            Assert.True(r.Outcome == "final" && r.FinalText == "All done, boss." && r.Retries == 1 && r.Trace.Any(t => t.Salvaged.Length > 0));
        }

        [Fact]
        public void NativeBadArgumentsAreRejectedLikeAnyOther()
        {
            var (r, rec) = Go(new FakeBackend(NativeCall("count", A("n", 100))), protocol: "native");
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
        }

        // ------------------------------------------------------------ fuzz
        private static readonly string[] Garbage =
        {
            "", " ", "{", "}", "null", "[]", "{}", "{\"tool\":null}", "{\"tool\":\"say\",\"args\":null}", "{\"tool\":\"say\",\"args\":{\"line\":5}}",
            Fx.Call("say", Fx.A("line", "ok")), Fx.Call("count", Fx.A("n", 3)), Fx.Call("count", Fx.A("n", 99)), Fx.J("final", "fine"), Fx.J("final", ""), Fx.J("done", true), Fx.J("done", false),
            "```json\n" + Fx.Call("say", Fx.A("line", "fenced")) + "\n```", "say{line:<|\"|>raw<|\"|>}", "<|tool_call>call:nope{}<tool_call|>", "\u0000\u0001", new string('é', 500),
        };

        public static IEnumerable<object[]> FuzzCases()
        {
            for (int seed = 0; seed < 40; seed++) foreach (var p in new[] { "grammar", "json", "native" }) yield return new object[] { seed, p };
        }

        [Theory, MemberData(nameof(FuzzCases))]
        public void FuzzLoopAlwaysTerminatesCleanlyWithValidExecutedCalls(int seed, string protocol)
        {
            int[] idx = FuzzIdx.Seeds[seed];
            var script = new List<object>();
            for (int i = 0; i < 30; i++) script.Add(Garbage[idx[i]]);
            int k = 30;
            script.Add((Func<IReadOnlyList<JObject>, object>)(m => Garbage[idx[k++]]));
            var f = new FakeBackend(script.ToArray());
            var (r, rec) = Go(f, protocol: protocol, o: x => { x.MaxSteps = 4; x.TimeBudgetS = 5; });
            Assert.Contains(r.Outcome, new[] { "final", "done", "stopped", "fallback" });
            Assert.True(f.Calls.Count <= 4 * 3 + 1);
            var specs = Fx.Tools().ToDictionary(t => t.Name);
            foreach (var (name, values) in rec.Calls)
            {
                Assert.True(specs.ContainsKey(name));
                var words = new JObject();
                foreach (var p in values.Properties())
                    words[p.Name] = p.Name == "importance" ? new JValue(Scale.Importance.ToWords((int)p.Value))
                                  : p.Name == "delta" ? new JValue(Scale.Change.ToWords((int)p.Value)) : p.Value;
                var (_, err) = specs[name].CheckArgs(words, false);
                Assert.True(err == null, name + " " + values + " " + err);
            }
            if (r.Outcome == "final")
                Assert.True(r.FinalText.Trim().Length > 0 && !r.FinalText.Contains('\n') && !r.FinalText.Contains('<'));
        }

        // ------------------------------------------------------------ scales, grammar, parsing, probe
        [Fact]
        public void ScaleBasics()
        {
            var imp = Scale.Importance; var chg = Scale.Change;
            Assert.True(imp.ToValue("Very Important") == 4 && imp.ToWords(5) == "critical");
            Assert.True(imp.ToWords(100) == "critical" && chg.ToWords(-20) == "much worse" && chg.ToWords(0.4) == "no change");
            Assert.Equal(2, imp.NearestBin(2.5).Value);
            Assert.Throws<PyKeyError>(() => imp.ToValue("pretty important"));
            foreach (var (text, want) in new (object, int)[] { ("pretty important", 3), ("extremely important", 4), ("a bit important", 2), ("Critical!", 5), ("unimportant", 1), (5, 5), (-4, 1), ("100", 5) })
                Assert.Equal(want, imp.Parse(text));
            foreach (var (text, want) in new (string, int)[] { ("slightly better", 1), ("a little bit worse", -1), ("lots better", 3), ("much much worse", -3), ("no difference", 0) })
                Assert.Equal(want, chg.Parse(text));
            Assert.Null(imp.Parse("zebra")); Assert.Null(imp.Parse("")); Assert.Null(imp.Parse(true));
            Assert.Throws<PyValueError>(() => new Scale("bad", new[] { ("a", 2), ("b", 1) }));
        }

        [Fact]
        public void GrammarAdmitsOnlyDeclaredThings()
        {
            var g = Grammar.Build(Fx.Tools(), 80);
            Assert.StartsWith("root ::= call0 | call1 | call2 | call3 | call4 | final | done", g);
            Assert.True(g.Contains("\\\"critical\\\"") && g.Contains("\"1\" | \"2\" | \"3\" | \"4\" | \"5\"") && g.Contains("\\\"Pip\\\""));
            Assert.DoesNotContain("call0", Grammar.Build(new List<ToolSpec>(), 10).Split('\n')[0]);
            Assert.Contains("[^\"\\\\\\n\\r\\t<>{} ]", g);
            Assert.Throws<PyValueError>(() => Grammar.Build(new List<ToolSpec>(), 200, false, false));
        }

        [Fact]
        public void ParseTextForms()
        {
            var names = new HashSet<string> { "say", "remember" };
            Assert.Equal("say", StepParser.ParseText(Call("say", A("line", "a")), names).step.Tool);
            Assert.Equal("x", (string)StepParser.ParseText("{\"remember\": {\"fact\": \"x\", \"importance\": 3}}", names).step.Args["fact"]);
            Assert.Equal("done", StepParser.ParseText("Sure! {\"done\": true} hope that helps", names).step.Kind);
            Assert.Equal("{\"line\": \"hey, you\"}", PyJson.Dumps(StepParser.ParseText("call:say{line:<|\"|>hey, you<|\"|>}", names).step.Args));
            Assert.Null(StepParser.ParseText("hello there", names).step); Assert.Null(StepParser.ParseText("", names).step);
        }

        [Fact]
        public void ParamAndToolDeclarationsAreChecked()
        {
            Assert.Throws<PyValueError>(() => new Param("x", "int"));
            Assert.Throws<PyValueError>(() => new Param("x", "scale"));
            Assert.Throws<PyValueError>(() => new ToolSpec("bad name", ""));
            Assert.Throws<PyValueError>(() => new ToolHarness(new FakeBackend(""), new[] { new ToolSpec("a", ""), new ToolSpec("a", "") }));
            var schema = Fx.Tools()[1].OpenAiTool()["function"]["parameters"];
            Assert.Equal(PyJson.Dumps(new JArray(Scale.Importance.Words.Select(w => (object)w).ToArray())), PyJson.Dumps(schema["properties"]["importance"]["enum"]));
        }

        private sealed class ProbeBackend : IBackend, IBackendIdentity
        {
            public string Base => "http://x/v1"; public string Model => "m";
            public static int N;
            public Task<Reply> ChatAsync(IReadOnlyList<JObject> messages, string grammar = null, JArray tools = null, string toolChoice = null,
                                         int maxTokens = 160, double temperature = 0.2, double timeout = 30.0, int? slot = null)
            {
                N++;
                if (!string.IsNullOrEmpty(grammar)) return Task.FromResult(new Reply { Text = "zebra-7" });
                if (tools != null && (string)messages.Last()["role"] == "user")
                    return Task.FromResult(new Reply { ToolCalls = { new ToolCall { Id = "1", Name = "ping", Arguments = new JValue("{\"who\":\"Ann\"}") } } });
                return Task.FromResult(new Reply { Text = "" });
            }
        }

        [Fact]
        public void ProbePicksProtocolsAndCaches()
        {
            ToolProbe.ClearCache();
            var caps = Block(ToolProbe.ProbeAsync(new ProbeBackend()));
            Assert.True(caps.Grammar && caps.NativeTools && !caps.NativeRound2);
            int n = ProbeBackend.N;
            Block(ToolProbe.ProbeAsync(new ProbeBackend()));
            Assert.Equal(n, ProbeBackend.N);
            Assert.True(ToolProbe.ChooseProtocol(caps) == "grammar" && ToolProbe.ChooseProtocol(caps, "tolerant") == "json");
            Assert.Equal("native", ToolProbe.ChooseProtocol(new Capabilities { NativeTools = true, NativeRound2 = true }, "tolerant"));
            Assert.Equal("json", ToolProbe.ChooseProtocol(new Capabilities()));
            ToolProbe.ClearCache();
        }

        [Fact]
        public void ToolHarnessRunsEndToEndWithProbe()
        {
            var f = new FakeBackend(Call("say", A("line", "Hi.")), J("done", true));
            var h = new ToolHarness(f, Fx.Tools(), mode: "grammar");
            var r = Block(h.RunAsync("s", "u", new Rec().Executor));
            Assert.True(r.Outcome == "done" && h.Protocol == "grammar");
        }

        // ---- per-scene allowed tool set
        [Fact]
        public void AllowedSetNarrowsGrammarPromptAndValidator()
        {
            var f = new FakeBackend(Call("set_mood", A("target", "Pip"), A("delta", "much worse")), Call("say", A("line", "Hello.")), J("done", true));
            var (r, rec) = Go(f, o: x => x.Allowed = new HashSet<string> { "say", "do_nothing" });
            Assert.Equal(new[] { "say", "do_nothing" }, r.Allowed);
            Assert.Equal(new[] { "say" }, rec.Calls.Select(c => c.name).ToArray());
            Assert.Contains(r.Failures, x => x.Contains("unknown tool \"set_mood\""));
            Assert.DoesNotContain("set_mood", f.Calls[0].Grammar);
            Assert.DoesNotContain("set_mood", (string)f.Calls[0].Messages[0]["content"]);
        }

        [Theory]
        [InlineData("grammar")] [InlineData("json")] [InlineData("native")]
        public void AllowedSetHoldsInEveryProtocol(string protocol)
        {
            FakeBackend f;
            if (protocol == "native")
                f = new FakeBackend(new Reply { ToolCalls = { new ToolCall { Id = "1", Name = "set_mood", Arguments = JObject.Parse("{\"target\":\"Pip\",\"delta\":\"worse\"}") } } },
                                    new Reply { ToolCalls = { new ToolCall { Id = "2", Name = "say", Arguments = JObject.Parse("{\"line\":\"Hi.\"}") } } }, new Reply { Text = "ok" });
            else f = new FakeBackend(Call("set_mood", A("target", "Pip"), A("delta", "worse")), Call("say", A("line", "Hi.")), J("done", true));
            var (r, rec) = Go(f, protocol: protocol, o: x => x.Allowed = new[] { "say" }, noFallback: true);
            Assert.All(rec.Calls, c => Assert.Equal("say", c.name));
            if (f.Calls[0].Tools != null && f.Calls[0].Tools.Count > 0)
                Assert.Equal(new[] { "say" }, f.Calls[0].Tools.Select(t => (string)t["function"]["name"]).ToArray());
        }

        [Fact]
        public void AllowedSetEmptyMeansNoToolMayRun()
        {
            var (r, rec) = Go(new FakeBackend(Call("say", A("line", "x"))), o: x => { x.Allowed = new string[0]; x.MaxRetries = 1; });
            Assert.Empty(rec.Calls);
        }

        [Fact]
        public void HarnessRunPassesAllowed()
        {
            var f = new FakeBackend(Call("set_mood", A("target", "Pip"), A("delta", "worse")), J("done", true));
            var h = new ToolHarness(f, Fx.Tools(), mode: "grammar");
            var rec = new Rec();
            var r = Block(h.RunAsync("s", "u", rec.Executor, over: x => x.Allowed = new HashSet<string> { "say" }));
            Assert.True(rec.Calls.Count == 0 && r.Allowed.SequenceEqual(new[] { "say" }));
        }

        // ---- a turn-ending tool
        [Theory]
        [InlineData("grammar")] [InlineData("json")] [InlineData("native")]
        public void ATurnEndingToolStopsTheLoopWithoutAskingAgain(string protocol)
        {
            object reply = protocol == "native" ? new Reply { Text = "", ToolCalls = { new ToolCall { Name = "do_nothing", Arguments = new JObject() } } } : (object)Call("do_nothing");
            var fake = new FakeBackend(reply);
            var rec = new Rec();
            var r = Block(Loop.RunAsync(fake, Fx.TurnEnding(), "You are a cook.", "Do things.", rec.Executor, protocol, null, new LoopAction("call", "do_nothing").AsFallback()));
            Assert.True(r.Outcome == "done" && r.Retries == 0 && r.Failures.Count == 0);
            Assert.True(fake.Calls.Count == 1);
            Assert.Equal(new[] { "do_nothing|{}" }, rec.Shown());
        }

        [Fact]
        public void WithoutEndsTurnTheSameModelMeetsTheRepeatRule()
        {
            var fake = new FakeBackend(Call("do_nothing"));
            var r = Block(Loop.RunAsync(fake, Fx.Tools(), "You are a cook.", "Do things.", new Rec().Executor, "grammar", null, new LoopAction("call", "do_nothing").AsFallback()));
            Assert.True(r.Retries > 0);
        }

        [Fact]
        public void ATurnEndingToolAfterARealActionKeepsTheAction()
        {
            var fake = new FakeBackend(Call("remember", A("fact", "the well is dry"), A("importance", "fairly important")), Call("do_nothing"));
            var rec = new Rec();
            var r = Block(Loop.RunAsync(fake, Fx.TurnEnding(), "You are a cook.", "Do things.", rec.Executor, "grammar", null, new LoopAction("call", "do_nothing").AsFallback()));
            Assert.True(r.Outcome == "done" && rec.Calls.Select(c => c.name).SequenceEqual(new[] { "remember", "do_nothing" }) && fake.Calls.Count == 2);
        }
    }
}

# csmind part "llm": parity with the Python agent

Ports `agent/rimworldllm_agent/core/` LLM-side modules to `Mod/Source/Mind/`. Python stays untouched in `agent/`.

## Files

| Python | C# |
|---|---|
| (json.dumps / json.loads / httpx `json=` / str / repr helpers) | `Py/PyJson.cs`, `Py/PyStr.cs`, `Py/PyErrors.cs` |
| `config.py` (LLMConfig, config load, data dir, legacy migration, bridge.json llm block) | `Llm/MindConfig.cs`, `MindConfig.Fields.cs` (generated), `TomlLite.cs`, `LlmLog.cs` |
| `calllog.py` | `Llm/CallLog.cs` |
| `llm.py` (HttpLLM, AwayGate, engine detection, samplers) | `Llm/LlmClient.cs` |
| `sampler_route.py` | `Llm/SamplerRoute.cs` |
| `model_tier.py` | `Llm/ModelTier.cs` |
| `endpoint_check.py` | `Llm/EndpointCheck.cs` |
| `toolloop/scale.py` `spec.py` `parse.py` `backend.py` `harness.py` `loop.py` | `ToolLoop/Scale.cs` `Spec.cs` `Parse.cs` `Backend.cs` `Harness.cs` `Loop.cs` |
| `fakes.py` (FakeLLM) | `Mod.Tests/Mind/Fakes/FakeLlm.cs` |

Generators and captured goldens live in `~/.local/share/rimworld-hermes-engine/csmind/llm/` (`gen_*.py`). Expected values in tests were produced by running the Python (venv, httpx 0.28.1), never typed by hand.

## Python tests: ported vs deferred

Ported test for test (xunit, PascalCase), all green:

- test_toolloop (all 38 test functions, including the 40 seeds x 3 protocols fuzz; the RNG draws come from `random.Random(seed)` captured as index tables), plus golden tests for grammar bytes, prompt text, tool schemas, `Scale.parse`, `parse_text`, `check_args`, `render_result`.
- test_sampler (all 12; the two OpenAIBackend ones live in `BackendTests`).
- test_yielded (all 7; the two backend ones in `BackendTests`).
- test_endpoint_llm, test_calllog_rotation, test_config_bridge_llm, test_data_dir_windows, test_legacy_data_dir (all).
- test_model_tier: `test_classify`, `test_engine_read_and_cache`, `test_engine_large_starter_and_failure_keeps_last`, `test_plain_endpoint_uses_cfg_model`, `test_classify_size_class`, `test_engine_size_class_read`.
- test_creative: `test_config_accepts_lists_for_per_kind_fields`.

Deferred (need the Director, world fixture or minds, owned by other parts; port them when those land):

- test_model_tier, the eight `world` fixture tests (`test_clouds_*` x4, `test_art_*` x4): they build a whole `World` (Director, creative round, art) and only read `tier.small` and `cfg` feature flags. The tier logic they depend on is covered by `ModelTier.FeatureOn` tests in `SamplerTests`/`EndpointLlmTests`; only the glue in the Director remains.
- test_gpu: live GPU against a real llama-server; out of scope (no GPU, no server).

Additional C# only tests: `BackendTests.RequestBytesMatchPython`, reply shape and error cases (checked against Python), `FakeLlmGoldenTests`, `PyJsonTests`, `WireGoldenTests` (exact request bytes for native and OpenAI modes in 7 configurations).

Counts: 316 C# tests added in `Mod.Tests/Mind` by this part (PyJson 16, Config 27, CallLog 4, WireGolden 7, Sampler 24, EndpointLlm 43, ToolLoopGolden 6, ToolLoop 175 with theory rows, Backend 13, FakeLlmGolden 1). Full suite 812 green, mod build 0 warnings.

## Behaviour differences from the Python (and why)

1. Timeouts are a per-request total. httpx's `timeout=` is per phase (connect/read/write/pool); .NET has no per-phase equivalent, so `HttpUtil.SendAsync` cancels the whole request after `timeoutS`. A slow-trickle reply that httpx would let through can time out here.
2. `HttpTransportException.Kind` stands for the httpx exception class name used in the error strings (`server unreachable (ConnectError)`, `unreachable (ReadTimeout)`). .NET exposes fewer distinctions: only `ConnectError`, `ReadTimeout` and `ReadError` are produced. Python can also say `ConnectTimeout`, `WriteTimeout`, `PoolTimeout`, `RemoteProtocolError`, etc.
3. Python exception names in error strings (`f"{type(e).__name__}: {e}"[:200]`, used by the loop for executor, backend and loop faults) come from `PyNames.Of`: `IPyNamed` types (`PyException` family, `LlmError` "LLMError", `LlmAway` "LLMAway", `BackendError`, `BackendAway`) carry the Python name; common .NET exceptions are mapped (InvalidOperation to RuntimeError, IOException to OSError, Format/Argument to ValueError, ...); anything else uses the .NET type name.
4. asyncio `wait_for` cancels the pending backend coroutine; the C# loop uses `Task.WhenAny` and leaves the orphaned backend task to end on its own (the backend honours its own `timeout`, `min(step_timeout, remaining)`), swallowing its exception.
5. Python's clock is `time.monotonic()`; the loop takes `LoopOptions.Clock` (default `AwayGate.MonotonicS`). The per-step timeout itself still uses real time.
6. API renames (the C# naming rules): `Action` to `LoopAction` (clashes with `System.Action`); `harness.probe/choose_protocol/_CACHE` to static `ToolProbe.ProbeAsync/ChooseProtocol/ClearCache`; `test_endpoint` to `EndpointCheck.TestEndpointAsync`; `IMPORTANCE/CHANGE` to `Scale.Importance/Scale.Change`; `run_loop(**kw)` to `Loop.RunAsync(..., LoopOptions)`; `ToolHarness.run(**over)` to `RunAsync(..., Action<LoopOptions> over)`. Executors and validators are async delegates (`Func<string,JObject,Task<JToken>>`); `Loop.Sync` / `Loop.SyncCheck` wrap a synchronous one. A fallback is `Func<LoopResult,LoopAction>`; `LoopAction.AsFallback()` makes a fixed one.
7. Values are Newtonsoft tokens in place of Python dicts: tool args and results are `JObject`/`JToken`, messages are `JObject`. Python ints of any size in `int` params are checked with `BigInteger` and stored as `long` once in range (lo/hi are `long`).
8. `OpenAIBackend`: tool-call `id`/`name` from a reply are stringified with `PyJson.Str` when they are not strings (Python would keep a number or dict as is); iterating a non-list truthy `tool_calls` raises an `AttributeError`/`TypeError` of the right Python name, with a simplified message for a dict.
9. `LoopResult.ToDict()` / `StepTrace.ToDict()` / `Executed.ToDict()` build the dict in field order by hand instead of `json.loads(json.dumps(self.__dict__))`; `raw_calls` entries are `{id, name, arguments}` objects.
10. `has_raw_syntax` and `str.lower()`: .NET `IgnoreCase|CultureInvariant` and `ToLowerInvariant()` stand in for Python's `re.I` and `str.lower()`; they differ only for a few exotic characters (Kelvin sign, dotted capital I).
11. `Scale.Of(name, phrases, values, start)` is a convenience in place of Python's `Scale.of` (kept with the same zip-to-shorter behaviour).
12. `HttpLlm` takes an `HttpClient`, a `Func<double>` clock and a `LogSink`; logging goes through `LlmLog` (`LlmLog.Sink` default, per-instance sinks for tests) instead of the `logging` module.

## Python behaviours kept on purpose (bugs or quirks that matter)

- A malformed OpenAI-mode reply (not a chat completion) raises an uncaught `Py*` exception from `HttpLlm.CompleteAsync` and writes no call-log row, as Python does (`KeyError`/`IndexError`/`TypeError` out of `data["choices"][0]...`). `PyAccess` reproduces the Python access errors.
- `Retry-After` uses Python float parsing, clamped to [0, 30] with Python's `max/min` NaN rules; a NaN raises `ValueError` from `round(nan)` inside `went_away`: no call-log row, and the gate does not block.
- `NamedSamplerLlm` has its own `AwayGate`.
- A plain 503 (not code "yielded") stays an `LlmError`; `BackendAway` is only raised for yielded.
- `stop=None` becomes `["\n"]`; any `mode` other than `"native"` runs OpenAI mode; the per-call `temperature` is ignored by `HttpLlm` (only `OpenAIBackend(honorTemperature: true)` sends it).
- Engine detection: unreachable returns null and is not cached; non-200 or a wrong shape caches false.
- `CallStats.ToDict` rounds with `PyRound` (half-even on the exact binary value, like Python's `round`).
- The tool loop's capability probe is cached for the process by `base|model` even when it failed with a `BackendError`.
- The loop never raises for a model fault, runs at most `max_steps * (1 + max_retries)` backend calls, and the closing step forbids calls (grammar without call rules / extra user message).

## Public C# API (for other parts)

Namespaces: `RimWorldLLM.Mind` (Llm files), `RimWorldLLM.Mind.Py`, `RimWorldLLM.Mind.ToolLoop`.

Client and wire (`Llm/LlmClient.cs`):
- `interface ILlmClient { Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48, IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null, int? actor = null, JObject jsonSchema = null); }`
- `LlmMessage(role, text)` (implicit from `(string, string)`), `Completion { Text, Stats }`, `LlmError(cause, status) : Exception` (`Cause`, `Status`), `LlmAway(message, retryAfter) : LlmError` (`AwayMessage`, `RetryAfter`).
- `HttpLlm(LlmConfig cfg, CallLog callLog = null, HttpClient client = null, LogSink log = null, Func<double> clock = null) : ILlmClient, IEngineClient` with `Cfg`, `CallLog`, `Gate`, `Base`, `IsEngineAsync()`, `FetchEngineStatusAsync()`, `Dispose()`.
- `IEngineClient { Task<bool> IsEngineAsync(); Task<JToken> FetchEngineStatusAsync(); }`
- `AwayGate(Func<double> clock = null, LogSink log = null)`: `Check()`, `WentAway(msg, retryAfter)`, `Ok()`, `Until`.
- `LlmWire`: `ParseYielded`, `GemmaPrompt`, `IsKnownGemma4`, `SamplerProfile`, `NormaliseBase`, `RemoveV1`, `EngineStatusUrl`, `DetectEngineAsync`, `SamplerFields`.
- `HttpUtil.NewClient()`, `GetAsync`, `PostJsonAsync`, `SendAsync`; `HttpReply { Status, Content, Headers, Text, Json() }`; `HttpTransportException(kind)`.
- `SamplerRoute.Route(ILlmClient, string name)` and `NamedSamplerLlm`.
- `ModelTier(MindConfig cfg, Func<double> clock)`: `Tier`, `Small`, `Model`, `Staging`, `SizeClass`, `Due()`, `RefreshAsync(ILlmClient)`; static `ModelTier.Classify(model, patterns, staging, sizeClass)`, `ModelTier.FeatureOn(tier, master, smallOk)`.
- `EndpointCheck.TestEndpointAsync(LlmConfig cfg, HttpClient client = null, double timeoutS = 20.0)` returning `EndpointResult { Status, Cause, BaseUrl, Models, CompletionMs, ModelMs, Ok }`.
- `CallLog(string path, long maxBytes, int keep)`: `Record(CallStats, JObject extra = null)`, `Snapshot()`, `CallLog.RotateFile`; `CallStats` (fields, `ToDict()`); `PyRound.Round`.
- `MindConfig.Load(path, env)`, `MindConfig.FromDict`, `MindConfig.DefaultDataDir(env)`, `MigrateLegacyDataDir`, `ReadBridgeJson`, `MindEnv.Process`, `LlmConfig` (generated fields), `CreativeFlag/CreativeFor/ActorSlots`.
- `LlmLog.Sink`, `LogSink(level, logger, message)`.

Tool loop (`ToolLoop/*.cs`):
- `Scale(name, bins)`, `Scale.Of(...)`, `Scale.Importance`, `Scale.Change`; `ToValue(phrase)`, `ToWords(number)`, `NearestBin(number)`, `Parse(object)`, `Has(object)`, `Words`, `Values`.
- `Param(name, kind = "text", description, maxLen, minLen, lo, hi, scale, choices, required)`: `Hint()`, `Gbnf()`, `JsonSchema()`, `Check(raw, tolerant)`.
- `ToolSpec(name, description, params, resultScales, endsTurn)`: `Signature()`, `OpenAiTool()`, `CheckArgs(raw, tolerant)`, `RenderResult(result)`.
- `Grammar.Build(tools, finalMax = 200, allowFinal = true, allowDone = true)`; `Prompt.RenderSystem(base, tools)`, `Prompt.RenderTools`, `Prompt.Protocol`; `Syntax.HasRawSyntax(text)`.
- `StepParser.ParseText(text, toolNames)` returning `(RawStep step, string why)`; `StepParser.JsonObjects`, `GemmaArgs`.
- `IBackend.ChatAsync(IReadOnlyList<JObject> messages, string grammar, JArray tools, string toolChoice, int maxTokens, double temperature, double timeout, int? slot)` returning `Reply { Text, ToolCalls, Finish, Reasoning, Ms }`; `ToolCall { Id, Name, Arguments }`; `BackendError`, `BackendAway`.
- `OpenAIBackend(baseUrl, model, apiKey, client, extra, engine, sampler, honorTemperature, clock, log)`.
- `Loop.RunAsync(backend, tools, system, user, executor, protocol, validator, fallback, LoopOptions)` returning `LoopResult { Outcome, Reason, FinalText, Executed, Trace, Protocol, Retries, Fallbacks, Failures, ImplicitDone, Allowed, ElapsedS, Completed, ToDict() }`; `LoopOptions { MaxSteps, MaxRetries, TimeBudgetS, StepTimeoutS, FinalMax, MaxTokens, Temperature, FallbackAfterActions, Allowed, Slot, Clock }`; `LoopAction(kind, tool, args, text)`.
- `ToolProbe.ProbeAsync(backend, key, force, rounds)`, `ToolProbe.ChooseProtocol(caps, mode)`, `ToolProbe.ClearCache()`; `Capabilities`; `ToolHarness(backend, tools, mode, cacheKey, maxSteps, maxRetries, timeBudgetS, stepTimeoutS)` with `EnsureAsync()` and `RunAsync(system, user, executor, validator, fallback, over)`.

Py helpers (`Py/`): `PyJson.Dumps/Compact/HttpxBody/Loads/Str/Repr/Truthy/StrRepr/FloatRepr/CompareCodePoints`, `PyStr.Strip/Split/CollapseSpaces/Len/Head/StrOf`, `PyNames.Of/Describe`, `PyException` family (`PyKeyError`, `PyIndexError`, `PyTypeError`, `PyAttributeError`, `PyValueError`), `PyAccess.Item/Get/TypeName`, `PyFloat.TryParse/PyStrip`.

Test support: `Mod.Tests/Mind/Fakes/FakeLlm.cs` (`FakeLlm : ILlmClient` with `Script`, `Calls`, `SlotPrompts`, `Fail`; `FakeCall`), `Fakes/MockHttp.cs` (`Mock.Client`, `MockResponse`, `Seen`), `TempDir`.

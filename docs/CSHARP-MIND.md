# The mind in C# (RimWorldLLM.Mind)

The agent that used to be the Python package `agent/rimworldllm_agent` (about 13.3k lines, httpx + pydantic, run by the
engine in a managed Python from a wheel) now lives inside the mod assembly as `RimWorldLLM.Mind`. Nothing is downloaded or
launched for it, no HTTP hop sits between the mind and the game, and in "My own endpoint" mode no engine runs at all.

**The last commit with the Python agent is `36515840a97cbbbf576ffdfd782d0726f07b7443`.** (The coordinator tags it. `bench/` runs against that commit; see
`bench/README.md`.) The golden data in `Mod.Tests/Mind/Data` was captured from the Python at that commit and is now the
reference the C# is held to; the capture scripts lived outside the repo (under the keeper's `~/.local/share`) and are not needed
again: the goldens are checked in.

## What the mind is

One long-lived loop per loaded game that watches the colony through the bridge contract (docs/BRIDGE-API.md), decides what
is worth saying or doing, asks the LLM, and writes the results back through the same contract: colonist speech and thoughts,
diaries, dreams, the chronicle, art descriptions, the recap on load, gossip/grudges/wants, the narrator's chat answers, and
(with "Narrator plays") the steward that runs the colony. It is game-agnostic in `Core/`, `Llm/`, `ToolLoop/`, `BridgeClient/`
and RimWorld-specific only in `Mind/RimWorld/` (the Python core/adapter split, kept and lint-checked).

## Layout

`Mod/Source/Mind/` is Verse-free (no UnityEngine, Verse or RimWorld types); `Mod.Tests` compiles it directly
(`<Compile Include="../Mod/Source/Mind/**/*.cs" />`).

| Folder | Holds |
|---|---|
| `Py/` | parity helpers: `PyRandom` (CPython's Mersenne Twister, so a seeded run draws the same numbers), `PyJson` (`json.dumps` byte-compatible output and tolerant loads), `PyFmt` (`str()`/`repr()` of floats, round, floor-div), `PyStr`, `PyErrors` (Python exception names for error strings) |
| `Core/` | the game-agnostic core: `Director` (+ `.Events/.Hosts/.Serve`), `Lease`, `Idle`, `Group`, `Passes`, `Inner`, `Speech`, `Prompt`, `Conversation`, `Minds`/`Memory`, creative jobs (`Chronicle`, `Art`, `Lore`, `Recap`, `Dreams`, `Gossip`, `Firsts`, `Grudges`, `Habits`, `Reflection`, `Concerns`, `Ledger`, `Bystanders`, `Grounding`, `Commentary`), `Wants`, `Steward` + `StewardBoard`, `Sanitize`, `AgeGate`, `Adult`, `MindRunner` (the run loop) |
| `Llm/` | `MindConfig` (+ `TomlLite`), `LlmClient` (non-stream) and `LlmStream` (SSE), `CallLog`, `EndpointCheck`, `ModelTier`, `SamplerRoute`, `FinalText`, `LlmLog` |
| `ToolLoop/` | the game-agnostic tool loop and GBNF grammar (`Spec`, `Scale`, `Parse`, `Backend`, `Loop`, `Harness`), namespace `RimWorldLLM.Mind.ToolLoop` (docs/TOOL-HARNESS.md) |
| `BridgeClient/` | `IBridge`, `BridgeClient` over an `IBridgeTransport` (HTTP transport and `InProcessTransport`), `BridgeTypes`; the test-only `FakeBridge` lives in `Mod.Tests` |
| `RimWorld/` | the RimWorld adapter (still Verse-free: it speaks the bridge contract): `Adapter`, prompt text, `Builders`, `ConcernRules`, steward digest/rules/tools/trade/combat/bio, `TextFill`, `Moments`, and embedded resources (`rules.json`, `steward_system.txt`, `steward_strategy.md`, `steward_combat.txt`, `steward_trade.txt`, `steward_biotech.md`) |

On the Verse side of the mod:
- `Mod/Source/Bridge/MindPlan.cs` (pure): what the mind needs to run, the `MindConfig` built from the mod's settings plus the config
  file, the restart key and the status line text.
- `Mod/Source/Bridge/MindSupervisor.cs` (pure): start/stop/restart of the background loop, retry after a failed start, the
  stop-and-await used by memory removal.
- `Mod/Source/Bridge/InProcessChatLive.cs`: the live "thinking... / typing..." chat line, straight onto the vivarium (no HTTP).
- `Mod/Source/RimWorld/MindHost.cs`: the game-side owner: reads the settings and the game state on the game thread, feeds
  `MindPlan`, ticks the supervisor, shows the status.
- `Mod/Source/Bridge/BridgeServer.cs`: the in-process door (`SubmitAsync`), see below.

## Threading

- **One background loop.** The mind runs on the thread pool, never on Unity's thread. Inside it a
  `ConcurrentExclusiveSchedulerPair`'s exclusive scheduler is the Director's single-threaded world (what asyncio's one thread was
  in Python), so library code needs no locks. `ConfigureAwait(false)` continuations escape to the pool until they return to the
  Director.
- **The game is touched only through `IBridge`.** Each call goes over `InProcessTransport` into `BridgeServer`'s queue and is served
  by `Pump` on the game thread inside the per-frame budget (2 ms). A mind that is busy for minutes costs a frame nothing.
- **Never throws into the game.** `MindHost.Tick` catches everything; `MindRunner` catches, logs each distinct message once and
  backs off; a failing start is retried after 15 s. A reload stops the running task with a bounded 2 s wait.
- **Lifecycle.** Starts when a game is loaded and the mod is enabled; stops at `GenScene.GoToMainMenu` and on mod disable; **waits at
  the main menu** (status "waiting for a game"). Local mode with no engine ready shows "waiting for the model" and a sleeping engine
  wakes on a request. A memory removal stops and awaits the mind before the data folder is deleted and restarts it afterwards.
- **Restart key.** Engine base URL, own endpoint (url/model/key), data dir, the config file's stamp (path|length|mtime) and the
  mirrored toggles. The local model name is deliberately not in it (a model swap must not restart the mind).

## The in-process bridge door

`BridgeRuntime.EnableInProcess()` makes `BridgeServer.SubmitAsync(method, path, query, body, ct)` available. A request goes into
the same `_pending` queue as an HTTP one and is served by `Pump` through `ApiRouter.Handle`, so op_id dedup, expiry,
`needs_game` and main-thread rules are identical. Refusals the door adds (413 too_large, 503 busy, 504 timeout, 503 no_game) and the
trust model are in docs/BRIDGE-API.md, "In-process use". The HTTP bridge stays for external tools and is opt-in.

## The LLM client

- **Endpoints.** Local mode: the engine's `/v1` and the selected model, 900 s timeout (the engine holds a request while a model
  loads). Own mode (`llmSource` = own): the player's endpoint and key called directly, 120 s, with no engine launched or attached.
  `Authorization: Bearer <key>` as before. Timeouts are per request, not per phase (httpx's were per phase).
- **Non-stream** (default and the fallback): byte-identical request bodies to the Python (`id_slot`, `grammar`, `tools`/`tool_choice`,
  `max_tokens`, `stop`, native vs OpenAI mode, `X-Easy-Sampler` when an easyEngine is detected). **Engine detection**
  (`EndpointCheck`) probes once and caches; unreachable is not cached.
- **Streaming** (new, opt-in per call by passing a sink; OpenAI mode only): the same body plus `stream`, `stream_options`
  (`include_usage`) and, only on a detected easyEngine, `return_progress`. SSE events become sink callbacks: Sent, Progress (prompt
  processed/total), FirstToken, Partial (decoded `FinalText`), Done, Error. The narrator's answer streams in both modes; the mod shows
  "thinking..." with progress, then "typing..." with the partial line. Timeout is the idle time between pieces. A server that
  ignores `stream` and returns JSON is read as a whole completion.
- **`X-Request-Id: rwl-<32 hex>`** on every request; the call log row gets `request_id`.
- **`id_slot` stability.** Sent from the same call sites as the Python: narrator/steward/commentary/recap use slot 0
  (`NarratorSlot`), colonists use the lease's slot 1..N. Nothing saves or restores slots.
- **Held requests and yielding.** A 503 with code `yielded` becomes `LlmAway` (Retry-After clamped 0..30, default 5) and an `AwayGate`
  keeps the mind from sending while the engine is away; other errors become `LlmError` with status, code and type. A held request
  (the engine loading a model) simply waits up to the timeout.
- **401** calls `LlmAuth.OnUnauthorized(status, body)`, which the host sets to `EngineAuthStatus.Note`: the mod's own pairing UI shows
  "Pair again" without the mind knowing about the bridge.
- **Call log** `calls.jsonl` rotates at 20 MB x 3, same row format.
- **Image content parts** (new, the look tool). A tool-loop message's `content` may be an array `[{"type":"text",...},
  {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,..."}}]`; the backend copies messages into the body as they
  are, so nothing else changes and a loop with no picture sends byte-identical bodies. `LoopOptions.TakeAttachment` hands the loop a
  picture after a step's tool results are in the history; `ImageParts.Attach` puts it in the last user message (grammar/json) or in an
  extra user message after the tool rows (native) and replaces any earlier picture, so a request carries one. Pictures live only in the
  loop history: never in `Executed.Result`, the trace, the steward trace, `calls.jsonl` or the board; `ImageParts.Redact` turns a data
  URI into `data:image/jpeg;base64,<N bytes>` for any log. The gate (`Llm/VisionProbe.cs`): engine status `"vision": true`, else the
  endpoint's `/props` `modalities.vision`; cached 60 s.
- **`ILookClient`** (`Llm/LookClient.cs`, new) is the one seam between a captured picture and the model: the gate (`OpenAsync`) and
  `PrepareAsync(mime, b64, kind)`, which returns the image content part. Today `DirectLookClient` (the `VisionProbe` gate; the picture
  as the game captured it, as a data URI). An engine look API (resize/tile to the token budget) can replace it without touching the
  capture, the camera or the steward (`Steward.Look`).

## Settings and the config file

Three layers, highest first: the mod's settings window (the rows marked "mod" in docs/SETTINGS.md), then the optional
`<data_dir>/config.toml` (else `config.json`; snake_case keys, `[llm]` and `[quotas]` tables), then defaults. The mod re-reads
the file every few seconds and restarts the mind when it changes; a broken file shows as an error in the status line and the
mind waits. Environment: `RIMWORLDLLM_DATA_DIR` moves the data folder and `RIMWORLDLLM_STEWARD_STRATEGY` points the steward at a
playbook file; the other `RIMWORLDLLM_*` variables of the Python era are read only by `MindConfig.Load`, which the harness and tests
use, never by the mod. Every setting the Python had is kept (docs/SETTINGS.md, "The mind's settings").

## Data directory and formats

`rimworldllm-agent` under `%LOCALAPPDATA%` (Windows), `$XDG_DATA_HOME` or `~/.local/share` (Linux), `~/Library/Application Support`
(macOS), or `RIMWORLDLLM_DATA_DIR`; the old `hermesvoice-agent` folder is moved on first start. The formats are the Python's, so memory
from a Python-era install loads unchanged: minds (pydantic `model_dump` JSON, same field order), diaries, `chronicle.jsonl` and
`chronicle.md` (always `\n` line ends), `calls.jsonl`, `config.toml`. The Storage tab and "Remove memory" use `AgentDataDir` with every guard kept.

## The harness: `tools/MindHarness`

A net10.0 console app that ships nowhere. Commands: `replay` (a recorded colony through the real Director with the `FakeLlm` unless
`--llm` is given), `speedsim` (the Director against a modelled game on virtual time; docs/SPEED.md), `steward-try` (one steward
cycle against a recorded digest), `comment-try`, and `stream-try` (a live streamed call with progress timings). Every writing
command needs `--data-dir` and never defaults to the real data folder.

## Module map (Python -> C#)

| Python (`agent/rimworldllm_agent`) | C# (`Mod/Source/Mind`) |
|---|---|
| `core/agegate`, `adult`, `gamedata`, `common_words`, `sanitize`, `llm.strip_thought`, `loopguard`, `lease`, `ledger`, `minds`, `memory`, `conversation`, `firsts`, `gossip`, `habits` | `Core/AgeGate.cs`, `Adult.cs`, `GameData.cs`, `CommonWords.cs`, `Sanitize.cs`, `Think.cs`, `LoopGuard.cs`, `Lease.cs`, `Ledger.cs`, `Minds.cs`, `Memory.cs`, `Conversation.cs`, `Firsts.cs`, `Gossip.cs`, `Habits.cs` |
| `core/config` | `Llm/MindConfig.cs`, `MindConfig.Fields.cs`, `TomlLite.cs` |
| `core/calllog`, `llm`, `endpoint_test`, `model_tier`, `sampler_route`, `fakes` (FakeLLM) | `Llm/CallLog.cs`, `LlmClient.cs` (+ `LlmStream.cs`, `FinalText.cs`, `LlmLog.cs`), `EndpointCheck.cs`, `ModelTier.cs`, `SamplerRoute.cs`; `FakeLlm` in `Mod.Tests` |
| `core/toolloop/*` | `ToolLoop/Scale.cs`, `Spec.cs`, `Parse.cs`, `Backend.cs`, `Loop.cs`, `Harness.cs` |
| `core/bridge`, `games/rimworld/fakes` (FakeBridge) | `BridgeClient/BridgeClient.cs`, `BridgeTransports.cs`, `InProcessTransport.cs`, `BridgeTypes.cs`; `FakeBridge` in `Mod.Tests`; `Mod/Source/Bridge/BridgeServer.cs` gains the door |
| `core/adapter` (GameAdapter contract), `prompt`, `passes`, `inner`, `speech`, `bystanders`, `concerns`, `grounding`, `wants`, `builders`, `steward_board` | `Core/GameAdapter.cs`, `Prompt.cs`, `Passes.cs`, `Inner.cs`, `Speech.cs`, `Bystanders.cs`, `Concerns.cs`, `Grounding.cs`, `Wants.cs`, `Builders.cs`, `StewardBoard.cs` |
| `games/rimworld/adapter`, `builders`, `register`, `concern_rules`, `steward_digest`, `steward_rules` (+ `rules.json`), `steward_tools`, `steward_build/bio/combat/trade`, `textfill`, `moments`, prompt `.txt/.md` | `RimWorld/Adapter.cs`, `AdapterText.cs`, `BlockAText.cs`, `Builders.cs`, `Register.cs`, `ConcernRules.cs`, `StewardDigest.cs`, `StewardRules.cs`, `StewardExpr.cs`, `StewardTools.cs`, `StewardBuild.cs`, `StewardBio.cs`, `StewardCombat.cs`, `StewardTrade.cs`, `RimWorldStewardKit.cs`, `TextFill.cs`, `Moments.cs`, `Js.cs`, `Resources/*` |
| `core/creative`, `chronicle`, `art`, `lore`, `recap`, `dreams`, `reflection`, `grudges`, `group`, `steward`, `idle`, `director`, `commentary` | `Core/Creative.cs`, `Chronicle.cs`, `Art.cs`, `Lore.cs`, `Recap.cs`, `Dreams.cs`, `Reflection.cs`, `Grudges.cs`, `Group.cs`, `Steward.cs`, `Idle.cs`, `Director*.cs`, `Commentary.cs`, `JobsCommon.cs`, `TextFillHost.cs` |
| `cli` (run loop), `replay`, `speedsim`, `steward_try`, `comment_try` | `Core/MindRunner.cs`, `Core/BridgeChatLive.cs`; `tools/MindHarness/*` and `Mod.Tests/Mind/Harness/Replay.cs` |
| `config.py` data dir and legacy move | `Llm/MindConfig.cs` (`DefaultDataDir`, `MigrateLegacyDataDir`) and `Mod/Source/Bridge/AgentDataDir.cs` |
| the engine's agent profile block, wheel, uv | removed (`EngineLauncher.BuildProfile` has no `agent` block) |
| `agent/tests/*` (about 1015 tests) | `Mod.Tests/Mind/*` (see below) |
| not ported on purpose | `narrator_cover`, caravans (tools, planners, prompt lines, tests), the CLI printers/argparse |

## Parity

- **The bar:** same behaviour as the Python; byte-identical prompts and request JSON where deterministic.
- **Tests.** The Python had about 1015 collected tests (956 pass, 1 skipped at the start of the port). Almost all are ported test for
  test under `Mod.Tests/Mind/`, same names in PascalCase; the director parity file counts about 987 ported or covered and about 28
  deferred at that moment, and later waves ported most of those (speedsim, wants wiring, the world tests). What was left unported is
  listed per area in the parity appendices ("deferred"): the CLI printers and argparse (`steward_try`/`comment_try`/`dialog_try`
  mains), the caravan tests (feature dropped), the steward `dialogs`/`paused` try arguments, and a few lint/bench-number tests that
  need the Python tree. New C# behaviour (streaming, X-Request-Id, 401 hook, in-process door, MindPlan/MindSupervisor, status) has
  its own tests (for example 92 stream tests). The mod's whole suite is about 3595 tests (Mod.Tests, about 2.5 minutes).
- **Goldens.** Prompts, request bodies, tool-loop traces, steward digests and decisions, jobs output and the e2e replays were captured
  from the Python at `36515840a97c` and are asserted byte for byte: the multiday replay (101 LLM calls, 120 writes) and the mixed replay
  (109 calls, 174 writes) match 100%; steward-try matches call for call.
- **Live comparison** (poe, GPU 0, an easyEngine test home, E4B RP model, the C# mind headless against the recorded colony through
  `tools/MindHarness`): the multiday replay gave, Python vs C# (warm), 107 vs 101 calls, 41 vs 39 lines said, 11 vs 11 diaries,
  3 vs 4 chronicle entries, 5 vs 5 reflections; mean wall time per call 0.96 s vs 0.93 s; no errors on either. Three `steward-try`
  runs produced byte-identical prompts and the same decisions. A streamed call showed Sent, Progress 1..39/39, FirstToken, partials and
  Done with a time to first token of 0.20 s. The live run found and fixed one bug (a null string value in `PyJson` threw in the
  harness logging path).

Detailed per-area notes (the appendices): [bridge](csmind/parity-bridge.md), [build](csmind/parity-build.md),
[contract](csmind/parity-contract.md), [delta](csmind/parity-delta.md), [delta2](csmind/parity-delta2.md),
[delta3](csmind/parity-delta3.md), [delta4](csmind/parity-delta4.md), [director](csmind/parity-director.md),
[group](csmind/parity-group.md), [idle](csmind/parity-idle.md), [inner](csmind/parity-inner.md), [jobs](csmind/parity-jobs.md),
[llm](csmind/parity-llm.md), [side](csmind/parity-side.md), [stream](csmind/parity-stream.md), [text](csmind/parity-text.md),
[tools](csmind/parity-tools.md), [wire](csmind/parity-wire.md). What to check in game: docs/release/CSHARP-MIND-PLAYTEST.md.

## What changed / differs from the Python

Consolidated from the parity appendices, grouped by area, each with the reason; the parenthesis names the appendix. Headline
differences: `narrator_cover` was dropped (nothing read its output; an item that would have been covered now waits in the lease
queue until it expires); caravans were never ported (the keeper's call; the Python had them off); streaming, `X-Request-Id`, the
401 hook and the in-process door are new; the Chronicle tiny-budget infinite loop is fixed; timeouts are per request rather than
per phase; regex, case-folding and Unicode corner cases follow .NET where they differ for exotic characters.

### LLM client and streaming

1. Timeouts are a per-request total (HttpUtil.SendAsync cancels the whole request after timeoutS); httpx timeouts are per phase, .NET has no equivalent, so a slow-trickle reply httpx lets through can time out. (parity-llm)
2. Error strings use `HttpTransportException.Kind`; only ConnectError, ReadTimeout and ReadError are produced. Python can also say ConnectTimeout, WriteTimeout, PoolTimeout, RemoteProtocolError, etc. (parity-llm)
3. Python exception names in error strings (`f"{type(e).__name__}: {e}"[:200]`) come from `PyNames.Of`: IPyNamed types carry the Python name (LLMError, LLMAway, BackendError, BackendAway), common .NET exceptions are mapped (InvalidOperation to RuntimeError, IOException to OSError, Format/Argument to ValueError), anything else uses the .NET type name. (parity-llm)
4. asyncio `wait_for` cancels the pending backend coroutine; the C# loop uses `Task.WhenAny` and leaves the orphaned backend task to end on its own (it honours `min(step_timeout, remaining)`), swallowing its exception. (parity-llm)
5. The loop clock is `LoopOptions.Clock` (default `AwayGate.MonotonicS`) instead of `time.monotonic()`; the per-step timeout itself still uses real time. (parity-llm)
6. API renames: `Action` to `LoopAction`; `harness.probe/choose_protocol/_CACHE` to `ToolProbe.ProbeAsync/ChooseProtocol/ClearCache`; `test_endpoint` to `EndpointCheck.TestEndpointAsync`; `IMPORTANCE/CHANGE` to `Scale.Importance/Scale.Change`; `run_loop(**kw)` to `Loop.RunAsync(..., LoopOptions)`; `ToolHarness.run(**over)` to `RunAsync(..., Action<LoopOptions> over)`; executors/validators are async delegates (`Loop.Sync`/`Loop.SyncCheck` wrap sync ones); fallback is `Func<LoopResult,LoopAction>`. (parity-llm)
7. Values are Newtonsoft tokens in place of Python dicts; Python ints of any size in `int` params are checked with BigInteger and stored as `long` once in range. (parity-llm)
8. `OpenAIBackend`: a tool-call `id`/`name` that is not a string is stringified with `PyJson.Str` (Python keeps a number or dict as is); iterating a non-list truthy `tool_calls` raises AttributeError/TypeError of the right Python name with a simplified message for a dict. (parity-llm)
9. `LoopResult.ToDict()`/`StepTrace.ToDict()`/`Executed.ToDict()` build the dict in field order by hand instead of `json.loads(json.dumps(self.__dict__))`; `raw_calls` entries are `{id, name, arguments}` objects. (parity-llm)
10. `has_raw_syntax` and `str.lower()` use .NET `IgnoreCase|CultureInvariant` and `ToLowerInvariant()`; they differ from Python only for a few exotic characters (Kelvin sign, dotted capital I). (parity-llm)
11. `Scale.Of(name, phrases, values, start)` is a C# convenience in place of Python's `Scale.of` (same zip-to-shorter behaviour). (parity-llm)
12. `HttpLlm` takes an HttpClient, a `Func<double>` clock and a LogSink; logging goes through `LlmLog` instead of the `logging` module. (parity-llm)
13. Kept bug/quirk: a malformed OpenAI-mode reply raises an uncaught Py* exception (KeyError/IndexError/TypeError) from `HttpLlm.CompleteAsync` and writes no call-log row, as Python does (`PyAccess` reproduces the errors). (parity-llm)
14. Kept: `Retry-After` uses Python float parsing, clamped to [0, 30] with Python max/min NaN rules; a NaN raises ValueError from `round(nan)` inside `went_away`, no call-log row, and the gate does not block. (parity-llm)
15. Kept: `NamedSamplerLlm` has its own `AwayGate`. (parity-llm)
16. Kept: a plain 503 (not code "yielded") stays an LlmError; BackendAway only for yielded. (parity-llm)
17. Kept: `stop=None` becomes `["\n"]`; any `mode` other than "native" runs OpenAI mode; per-call `temperature` is ignored by `HttpLlm` (only `OpenAIBackend(honorTemperature: true)` sends it). (parity-llm)
18. Kept: engine detection returns null when unreachable and is not cached; non-200 or wrong shape caches false. (parity-llm)
19. Kept: `CallStats.ToDict` rounds with `PyRound` (half-even on the exact binary value like Python `round`). (parity-llm)
20. Kept: the tool loop's capability probe is cached for the process by `base|model` even when it failed with a BackendError. (parity-llm)
21. Kept: the loop never raises for a model fault, runs at most `max_steps * (1 + max_retries)` backend calls, and the closing step forbids calls. (parity-llm)
22. NEW streaming (opt-in; non-stream stays default and fallback, its request bytes unchanged): with a sink and openai mode only (native never streams) the body is the non-stream body plus `"stream":true`, `"stream_options":{"include_usage":true}`, and `"return_progress":true` only when the endpoint is detected as easyEngine; stream requests also send `Accept: text/event-stream`. (parity-stream)
23. NEW X-Request-Id: every LLM request (stream or not, both modes, OpenAIBackend too) sends `X-Request-Id: rwl-<32 hex>` (36 bytes); `CallStats.RequestId` is appended last in the calls.jsonl row as `request_id` (absent when null). (parity-stream)
24. NEW: for a streamed call `TimeoutS` is an idle time (to the response headers, then between reads), not a total. (parity-stream)
25. NEW: a 200 with `Content-Type: application/json` (server ignored `stream`) is read as a whole chat completion. (parity-stream)
26. NEW stream errors: pre-stream `{"error":"text"}` and `{"error":{code,type,message}}` both map; 503 code "yielded" gives LlmAway (Retry-After clamped 0..30, default 5, gate engaged); 503 numeric code, 401 (WWW-Authenticate kept), 403, 502, other give `LlmError("HTTP <status>")` with Detail, Code, ErrorType, RetryAfterS; a string code in an object ("upstream_error") becomes the Cause. (parity-stream)
27. NEW: mid-stream `data: {"error":{...}}` is terminal even after a 200 ("yielded" gives LlmAway using `retry` seconds else 5; other string codes give `LlmError(code, status)`; numeric code gives `LlmError("HTTP <code>", code)`); close without `[DONE]` and without error gives `LlmError("stream ended without [DONE]")`; bad JSON chunk gives `LlmError("bad stream chunk")`. (parity-stream)
28. NEW: stream transport failure gives `LlmError("server unreachable (ReadTimeout|ConnectError|ReadError)")`; cancellation is an `OperationCanceledException` (not an LlmError) with call-log row error "cancelled" and the sink gets Error("cancelled"); one call-log row per call on every path, none when the gate refuses before sending; a sink exception is swallowed. (parity-stream)
29. NEW 401 hook: `LlmAuth.OnUnauthorized` (Action<int,string>) is called with (status, body) on any 401 in HttpUtil.SendAsync (HttpLlm and OpenAIBackend, non-stream) and in StreamSender (stream); MindHost.Make sets it to `EngineAuthStatus.Note`; the mind has no Bridge reference; `Authorization: Bearer <ApiKey>` unchanged; llmEngineId/pairedEngines not read. (parity-delta3)
30. id_slot: the C# sends `id_slot` from the same call sites as the Python (narrator/steward/commentary/recap slot 0 = cfg.NarratorSlot; actor lines the lease's slot 1..N); nothing sends /slots save or restore; no change needed. (parity-delta3)
31. Cancellation: no token reaches `ILlmClient`, so a preempted job's pending call runs on detached and its result is discarded; jobs await with `Task.WaitAsync(token)`; Chronicle.WriteAsync/RunWantsAsync/RunDreamAsync may mutate their own state after the detached call returns. (parity-idle)
32. Streaming is on for narrator answers in both modes when hosted by the mod; `return_progress` only when the engine is detected; a non-stream fallback is kept. (parity-wire)
33. `LoopOptions.CallOptions`: when set and the backend is `IStreamingBackend`, the call goes through the streaming overload. (parity-jobs)

### Config and settings

34. `config.py` is `MindConfig` with TomlLite; `from_config`'s Config coupling is split: `BridgeLocator.Resolve(bridgeUrl, bridgeToken, bridgeJsonPath, defaultBridgeJsonPath)` returns (url, token) and `Connect(...)` builds an HTTP BridgeClient (llm worker's config owns the arguments). (parity-llm, parity-bridge)
35. New config keys mirrored from delta commits: `text_fill: bool = True` (`MindConfig.TextFill`, after `art_max_tokens`); `commentary`, `commentary_min_gap_real_s` 180.0, `commentary_min_gap_game` 15000, `commentary_recap`; `steward_combat_gap_game_s/_real_s`. (parity-delta, parity-delta2, parity-delta3)
36. Delta4 adds no new config keys; docs/SETTINGS.md unchanged. (parity-delta4)
37. The Python agent never reads the trade settings (the mod enforces the caps); the C# Mind reads `Vv["settings"]` by name; nothing needed on the Mind side. (parity-delta3)
38. `Commentary.Enabled()` reads `Steward.Vv["settings"]["commentary"]` (default true) and needs `Vv.playing`. (parity-delta2)
39. Hosted by the mod: the Python wheel/uv/python process is gone; the `agent` block (wheel, python, args, llm_url_env, env) is removed from `EngineLauncher.BuildProfile`; spawn env no longer carries RIMWORLDLLM_BRIDGE_JSON; an old engine's "agent" status JSON is ignored. (parity-wire)
40. Local LLM `llm.timeout_s` = 900 (engine holds a request while a model loads; streamed: idle time); own endpoint (`llmSource` = own) called directly with 120 s, mod launches/attaches no engine; a file value for `llm.timeout_s` wins. (parity-wire)
41. Restart key: engine base URL, own endpoint (url/model/key), data dir, config file stamp, mirrored toggles; the local model name is not in the key (a model swap does not restart the mind). (parity-wire)
42. Mod settings (endpoint, pause_ambient_at_speed, wants, dreams, art_enabled, recap_enabled, text_fill, data_dir) override the file; `<data_dir>/config.toml` (else `config.json`) is read by MindConfig; bridge_json/url/token are dropped. (parity-wire)
43. Env vars: Python read many `RIMWORLDLLM_*` variables; C# reads only `RIMWORLDLLM_DATA_DIR` plus `RIMWORLDLLM_STEWARD_STRATEGY` (read by Steward.LoadStrategy, an extra variable in Core/Steward.cs). (parity-wire)
44. The mod no longer polls `GET /config` for the mind; the mind sees settings through the config build (restart on change). (parity-wire)
45. Known limit: the steward's fixed step (60 s) and budget (120 s) timeouts still cut a steward step during a long model load. (parity-wire)
46. Settings Model/Advanced shows "Narrator and colonists: running / waiting for the model / waiting for a game / off / error: <last error>" (error shown 120 s), replacing the "agent connected" reading. (parity-wire)
47. Memory removal: the mind is stopped and awaited before the data dir is deleted, restarted when a game is loaded (stale hold auto-releases at 600 s); works without an engine; every `AgentDataDir.Check` guard kept. (parity-wire)
48. `AgentDialOut` (agentUrl, sendColonyEvents) is kept on purpose as the documented push contract for external agents over the opt-in HTTP bridge. (parity-wire)
49. `LoadStrategy` normalises CRLF/CR to LF (Python text mode); "not read" notes use C# OS error class names (OSError/PermissionError mapped; OS text wording may differ). (parity-jobs)

### Bridge and in-process transport

50. `BridgeClient` takes an `IBridgeTransport` instead of `httpx.AsyncClient` + base url; the client still computes the `Authorization` header and passes it on `BridgeRequest.Authorization`. (parity-bridge)
51. Bug fixed: a reply whose JSON is not an object (e.g. `[1]`) is `bad_reply`; Python crashed with AttributeError on `body.get`. (parity-bridge)
52. `on_epoch_change` (declared, never called in Python) is not ported; `Bridge.epoch_changed` is `IBridge.EpochChanged` (get/set; the director clears it). (parity-bridge)
53. `aclose` is `CloseAsync` (disposes the transport). (parity-bridge)
54. `BridgeTransportException` and `HttpRequestException` map to `unreachable` (detail = message, or type name when empty); an HTTP client timeout maps to `unreachable` as httpx.TimeoutException did. (parity-bridge)
55. JSON is parsed with date parsing off (Python keeps date-like strings as str); numbers read as long/double; `int(x)` of a float truncates, as Python. (parity-bridge)
56. `put_goals(rev, **fields)` is `PutGoalsAsync(int rev, JObject fields, string opId)`; `act` bodies are copied (`DeepClone`) before op_id/expiry are added (Python `dict(body)` was shallow). (parity-bridge)
57. FakeBridge (test-only): Python `isinstance(x, int)` (accepts bool) is "JSON integer, not bool" in shape checks (schedule hours, work priorities); a bool is rejected in C#, accepted in Python. (parity-bridge)
58. NEW in-process door: `BridgeServer.EnableInProcess()`/`SubmitAsync(method, path, query, body, ct)` enqueues an InboundRequest with a TaskCompletionSource (continuations run asynchronously) into the same `_pending` queue; `Pump` serves via `ApiRouter.Handle` and `Envelope.Json`, so op_id dedup, expiry, `needs_game` and main-thread rules are unchanged. (parity-bridge)
59. NEW in-process refusals as HandleContext: 413 too_large (body over 64 KiB), GET /health from published state without a pump, 503 busy when the game thread has not pumped for `StalledAfter`, 504 timeout (abandons queued request) after `GameThreadTimeout`, 503 no_game when closed/stopped; in-process requests are trusted (no token) and logged in `Requests` as authenticated; the in-flight gate (16) is HTTP only. (parity-bridge)
60. `Stop()` closes both HTTP and the door; `Start()` closes only a previous listener; `BridgeRuntime.EnableInProcess()` makes a listener-less BridgeServer when none exists; Start/Stop of the HTTP side keep the door open; `FrameUpdate` already pumps whenever `Server != null`; no change in `Mod/Source/RimWorld/*.cs`; RWLBridge.Start returns early when the mod is disabled (in-process unaffected). (parity-bridge)
61. Hosting: `MindHost` owns a `MindSupervisor`; the mind runs on the thread pool and reaches the game only through `IBridge` over `InProcessTransport` served on the game thread by `FrameUpdate`; starts when a game is loaded and the mod enabled, stops at `GenScene.GoToMainMenu` and on mod disable; `Tick` never throws or blocks a frame; a failing start is retried after 15 s. (parity-wire)
62. Live chat: C# `InProcessChatLive` over `RWLBridge.Runtime.Vivarium.Live` (no HTTP) replaces ChatLive over HTTP; Begin for a narrator also queues `Budget.NewCycle` on the game thread; `End(id, text)` is a no-op because the final line posted to /chat with the stream id already ends the live line; `End(id, null)`/`Cancel` drop it; hook `MindRunner.ChatLiveFactory` is the only edit to Mind/Core. (parity-wire)
63. `core/bridge.py` additions mirrored: `text_queue(n)`, `text_fill(...)`, `dialogs()`, `combat()` with a speed guard (`Speed(JToken)` envelope guard), `look(path, params)` (query is an ordered list of pairs, not a dict), `post_chat` stream_id and `post_chat_status`. (parity-delta, parity-delta2, parity-delta3, parity-side)
64. Caravans route: the fake bridge answers `/act/caravan` with 409 "caravans are off". (parity-delta4)

### Director, scheduling and leases

65. asyncio is replaced by a `ConcurrentExclusiveSchedulerPair`; every entry point goes through RunOnLoop, spawn defers the start; library code using ConfigureAwait(false) continues on the thread pool until it returns to the Director. (parity-director)
66. Reload does a bounded 2 s wait for the running task instead of a cancel. (parity-director)
67. `MindRunner` (the `run` loop of cli.py): catches all errors, logs each distinct message once (LlmLog.Warning), backs off, retries; never closes the caller's transport. (parity-director)
68. Other cli commands (replay, steward_try, config printing, argparse) are not ported into the Mod; replay lives in the test harness; `RimWorldAdapter.ReplayAsync` throws NotSupportedException. (parity-director, parity-contract)
69. `BridgeChatLive` builds the non-narrator wire word as a private const (`ActorWho`) because the Core lint forbids game terms; compares `!= "narrator"`. (parity-director)
70. Host-interface type clashes are resolved by explicit implementations (long Day, IDictionary Minds, ISet MinorTokens, IList Dropped, IStewardIdle Idle); fields became get-only properties. (parity-director, parity-idle)
71. Idle host clashes: IdleRunner reads `Minds` through IWantsHost (Dictionary), `Dropped` through ITextFillHost (List, one list backs both), `MinorTokens()` through IArtHost. (parity-idle)
72. `StartActor` takes `Func<Lease, CancellationToken, Task>` (lease passed in; Python read `self.lease` when the coroutine first ran). (parity-idle)
73. art/chronicle `finally`: `Task = null` only when `Lease == null` and the job is still current (Python: lease None only). (parity-idle)
74. `Preempt(string)` returns bool; `IStewardIdle.Preempt` (void) explicit; `ActorsWantWindows()` (IIdleYield) and `ActorsWantWindows(bool starving)`. (parity-idle)
75. `chronicle not written` catch is BridgeError only (no AttributeError in C#); `Chronicle.WriteAsync` wants IChronicleAdapter so a private view delegates the three chronicle prompts. (parity-idle)
76. `TextFillFn()` calls `adapter.IdleFill()` only when text fill is on; null result means off. (parity-idle)
77. Unhandled idle job exceptions are logged (LlmLog.Warning) instead of left on the asyncio task; Python was single-threaded, so the Director's Spawn must marshal jobs onto the director context (IdleRunner takes no locks). (parity-idle)
78. Idle text fill: Director gets `TextQueueLive = false`, `TextFillWaitS = 0.0`; `TEXT_FILL_ACTOR = -2`, `TEXT_FILL_GAP_S = 10.0`; text fill is the lowest idle job; art stands down when the text queue answers (`TextQueueLive`); a 404 sets it false again. (parity-delta)
79. `TextTried` holds `IdKey(id)` strings (the id's JSON text) instead of raw ids; by-id lookup uses the same key (a non-string id in the reply never matches a string id, as in Python). (parity-delta)
80. `_thought` drops meta talk (`DropsMeta`) with drop log `thought:meta_talk`; the director vanilla line returns `Dropped("meta_talk")` and says nothing. (parity-delta)
81. Chitchat/vanilla-social adapter wording changes ported: `drops_meta(text, context)`, `_NO_META_RULE`, `first_person` chitchat rewrite, `vanilla_request`; RimWorld ReactionRequest `_NO_META_RULE` was missing after the inner port (workaround in InnerGoldenTests, to remove). (parity-delta, parity-inner)
82. `narrator_cover` REMOVED (dropped feature): `Director.NarratorCoverAsync`, `NarratorLines`, `CoverAllowed`, `_lastCover`, the cover loop in `Launch`, and the test `NarratorCoverageMinGapBypassedByDeathsAndMajorThreats` are gone (nothing read `narrator_lines`); kept `LeaseManager.NarratorShouldCover`, `IGameAdapter.NarratorPrompt` and `narrator_min_gap_*` config fields; an item that would have been covered now stays in the lease queue until it expires. (parity-delta2; note parity-director still lists NarratorCoverAsync in its API)
83. Commentary (new core/commentary.py and games/rimworld/moments.py) ported: `Task.cancel()` is a CancellationTokenSource, LLM call and POST wait via `WaitAsync(token)`, a preempt cancels the call and nothing is posted. (parity-delta2)
84. Commentary: `Seen` pruning (over 200 keys, drop the 100 oldest) uses LINQ stable sort on game time; Python dict order for equal times may differ (ties only); Moment `game` is int; `DayMoments` keys int; `Pick` returns the first of equal maxima as `max()`; a dropped line's reason is `commentary:<reason or sanitize>`; base `commentary_moment` returns null (Python getattr None). (parity-delta2)
85. Commentary wiring as Python: constructed before Steward; `Reset()` after `Idle.Preempt("reload")`; `PreemptIfWanted()` after `Idle.PreemptIfWanted()`; `Note(e)` after `Steward.Observe(e)` in ingest (before the silent-events skip); `RunOnce()` after `Idle.RunOnce()` in `Launch`. (parity-delta2)
86. Group: `WorkItem.Payload` is a JObject carrying the Gathering in a JRaw (use `Group.PayloadOf`/`GatheringOf`); `IGroupHost.RepeatCheck(IList<Turn> turns, int speaker)` takes turns; the `held` dict is a small private ordered class to keep Python insertion order; `except BaseException` becomes `catch (Exception)` with OperationCanceledException re-raised, others logged via `LlmLog.Warning("rimworldllm", "gathering failed: ...")` without traceback text. (parity-group)
87. Group: `int(e.get("game", now.game))`/`float(talk_frequency)` throw on a JSON null as Python `int(None)`; `GroupState.Told` values are long; `Env.Game` is int; `d._inner_kw` unused (no InnerKw member, no SpeakExtras); the sentence split uses an explicit Python `\s` class. (parity-group)
88. Lease: `Cls` enum PascalCase with `Name()` giving the Python name; insertion order through `*InOrder()` methods; `Conversation.CheckEnd` has PyRandom and Func<double> overloads. (parity-text)
89. Idle wants roll matches Python sha1; floor div/mod and float of bridge value mirrored (IdlePureTests). (parity-idle)
90. Test seams added to the Director (additive): `ListenerHook`, public `InnerKw/InnerArgs/InnerAfterSayAsync`, `LeaseManager.SubmitHook`, `PyRandom` virtual `Random()` + `RandomOverride`, `FakeBridge.OnRequest`, `HookLlm.Before/BeforeKind`, World ctor options; `Director` ctor takes optional `TaskScheduler underlying`, `Func<double, CancellationToken, Task> delay` (virtual time); `Director.Probes` (Say, Finish, Ingested hooks). (parity-director, parity-tools, parity-contract, parity-inner)
91. Bug fixed (also a latent production race): `Idle.Preempt` takes a local copy `var lease = Lease;` to fix a NullReferenceException race. (parity-tools)
92. Steward cadence: the "-> " automatic notes after acts are removed in core/steward.py (the game prints the greentext line itself); tests changed (`lines(w, "note") == []`). (parity-delta)
93. Trade turn: runs once per new trader id (sorted ordinally), after chat, dialogs and urgent events, before the heartbeat; never in a fight, while paused or in advisor mode; skipped (trader still marked seen) when food is under 3 days or on a critical starvation alert; does not move `LastGame`; constants `TradeSteps=4, TradeMaxTokens=300, TradeFinalMax=120, TradeBudgetS=60, TradeStepTimeoutS=30`. (parity-delta4)
94. Combat cadence in Steward: `IStewardCombatKit` is an optional interface mirroring `hasattr(kit, "combat_session")`; `IStewardTradeKit` likewise; `IStewardKit.StrategyExtra(JObject)` mirrors `getattr`. (parity-delta3, parity-delta4)
95. Sorting: ordinal string sorts differ from Python code-point sorts only for non-BMP text. (parity-inner)

### Speech and text sanitising

96. Sanitize uses an explicit whitespace class (Python `\s` includes U+001C-1F); .NET `\w` also matches combining marks; NFKC/case folding use .NET tables (rare code points may differ). (parity-text)
97. `str.casefold` is `ToLowerInvariant` plus sharp-s; `str.lower` is `ToLowerInvariant` (differs for the dotted capital I and Kelvin sign, per parity-side; "exotic Unicode" per parity-delta3). (parity-side, parity-delta3, parity-build)
98. Group sentence split uses an explicit Python `\s` class. (parity-group)
99. `Lore.cs` keeps a private copy of the refusal regex (Sanitize's is private). (parity-jobs)
100. Python `isinstance(x, (int, float))` includes bool and a JSON null reads as missing in every snapshot reader; both mirrored in `Js`; rounding is banker's rounding (`PyFmt.RoundInt`); `str.title()` mirrored. (parity-contract)
101. Float/number formatting helpers: PyRandom is CPython MT19937 (seeds 1/7/42/0/2^40+5 verified against Python 3.14); PyFmt provides Repr, Fixed, Round, RoundInt, FloorDiv, Mod; `PyJson.FloatRepr`, `CompareCodePoints`. (parity-text, parity-llm)
102. `ValText` for a non-container uses `Js.Raw` so numbers go through `GameData.Clean` like `gd.clean(v, ...)` (numbers print as Python str()). (parity-delta)
103. Python `int(it.get(...))` raising on odd values maps to `Js.Int` raising PyTypeError/PyValueError; the idle job treats it as any other exception (log + backoff). (parity-delta)
104. Compact JSON writer lives in `PyModel.Compact` (ensure_ascii off). (parity-text)
105. Dict job: the Python `build_scene` printed a `{def, label}` job as the dict repr; C# reads the label with `Js.JobText` (string, or `label`, falling back to `def`), then "idle" (bug fixed; golden data uses string jobs). (parity-contract)
106. Prompt wording files `AdapterText.cs` and `BlockAText.cs` are generated from the Python (gen_adapter_text.py) for byte-for-byte wording; `BlockAText.cs` gained the no-meta line by hand edit, checked by ContractGoldenTests. (parity-contract, parity-delta)

### Speech in context (2026-11-27)

The keeper's first run gave flat lines (median 10 words), a fixation on prices and silver, lines about random subjects ("Hickory wood
burns hot and fast."), and thoughts, diaries and a chronicle about the talking itself. What fixed each:

- **Voice card.** The code-written example lines are gone from every prompt. They were the same for all pawns ("Right. What now?"), and
  the Greedy one was about silver. A prompt now shows example lines only when the model or the player wrote them. Topic tics ("Mentions
  what things cost", "Talks about the next job") are replaced by manner tics. A saved card's old tics and register fragments are filtered
  on the way into the prompt (`PromptBlocks.PromptTics`/`PromptRegister`). A habit shows at most one line in three, counted per pawn.
  Reflection makes no topic or meta habits (`Habits.IsStyleHabit`).
- **Salience picker** (`Mind/Core/Salience.cs`, pure). It takes `SalientFact`s (kind, key, text, weight 0..1, age in game hours,
  listener, social) and picks 3 to 6. Score = weight x recency (half-life 4 h; a day for diaries) x 1.5 when the fact is about the
  listener (lines) x 0.35 for keys the pawn used in its last three prompts. Routine social facts count x0.1 in thoughts and diaries. A
  fact about price or value is never picked. One fact per kind comes first, then the rest by score. Floor 0.2. Min is reached only with
  real facts (weight 0.3+), never filler such as the time or a mild sky. `Mind/RimWorld/SalienceFacts.cs` builds the facts from the
  snapshot, scene, listener and recent events. It uses only what the game gives and puts states in words (no numbers).
- **Line prompt.** Two new sections ride on the frozen per-lease prefix. `WHAT STANDS OUT RIGHT NOW` holds the picked facts.
  `WHO YOU ARE TALKING TO` gives the listener's name, what they do and how they look, the relation and both opinions in words, and the
  last 2 to 3 lines between the two (in memory, newest last). The scene speaks in words ("dirty", "cold", "mood low"), not numbers.
- **Vanilla interactions.** The InteractionDef is the line's intent, with a tone. Chitchat and DeepTalk never pass the play-log
  sentence, because its topic is a random flavour noun. Insult and Slight pass it with "only if it fits". The Postfix's outcome
  thoughts are stated as "How it landed".
- **Length.** A passing remark is a few words. A death, injury, fight, raid, deep talk or romance may take up to three short
  sentences. Never more than 60 words (`Sanitize.MaxWords` 60, `LineMaxTokens` 96, stop "\n" kept). The request carries a per-line
  hint (`PromptBlocks.LengthHint`, `GameAdapter.EarnsLength`).
- **Repeats.** The last 20 lines of all speakers are kept, and a line too like another pawn's (0.8) is refused once with a retry. A line
  whose first two words open 2 of the last 8 lines is refused too. A thought too like one of the pawn's last 10 (0.6) is dropped, and
  so is a diary whose first sentence repeats one of the last 3. `MindConfig.SpeechChecks` (on; off only for the Python parity
  replays).
- **Thoughts, diaries, chronicle.** A thought gets `RIGHT NOW` (picked present state: job, room, low needs, pain, mood thoughts, a
  recent real event). Routine social memories (chitchat, deep talk, "X and Y talked") are kept out of thought, diary, dream and
  chronicle inputs unless they carry an outcome (insult, rebuff, romance, fight, a secret). `Sanitize.IsTalkAboutTalk` drops a
  thought, diary or chronicle entry whose subject is the talking itself.
- **Measured** on Campfire-26B over the same recorded scenes (2 passes, about 110 lines and 25 thoughts each). Price or silver
  lines went from 16 to 3 (all from the Greedy pawn). Random-topic lines went to none. Line words: median 10 in both, p90 13 to 24,
  max 17 to 31. Deep talk went from 11 to 24 words and romance from 8 to 24, while chitchat stays short. Line prompt median rose from
  1990 to 2130 tokens. Lantern-4B held the format: median 8 words, max 30, no rambling.

### Minds, memory and conversation

107. Minds use a hand-written pydantic-lax validator (`PyModel.Validate`); an invalid blob gives a fresh mind; `relations` is an insertion-ordered `RelationMap`. (parity-text)
108. Memory/Firsts use small adapter interfaces (`IMemoryAdapter`, `IFirstsAdapter`); memory.py module functions are in static `MemoryOps` (class Memory is the model). (parity-text)
109. `Adult.Enabled` is a static with `SetEnabled(object)`; the director calls it on config refresh; tests touching it share xunit collection "AdultState". (parity-text)
110. `memories.remove` uses reference removal (Python used equality; identical for distinct objects). (parity-inner)
111. `GrudgeEngine.BeginAsync` catches `BridgeError` only (Python also caught AttributeError). (parity-inner)
112. Pending reunions keyed by ordered (min,max) pair (Python frozenset/sorted tuple). (parity-inner)
113. SubtextKind and bystander template pick use sha1 as BigInteger (`Inner.Sha1Mod`); same values. (parity-inner)
114. Coroutines handed to `d._spawn` are `Func<Task>`; pydantic models are JsonProperty/PyRequired classes validated by `PyModel.Validate`; static class names `ConcernOps`, `BystanderOps`, `WantsOps` (`WantsOps.SystemText` renamed from SYSTEM). (parity-inner)
115. BlobTrimsDiariesBeforePinnedMemories test uses a smaller limit (Mind.Size is private). (parity-text)
116. Contract: the static prompt class is `PromptBlocks` (namespace ToolLoop already has `Prompt`); prompt functions take/return `List<(string,string)>` rather than `LlmMessage`; `Speech.RegisterPhraseAsync` takes adapter and bridge instead of the Director. (parity-contract)
117. Contract: `IGameAdapter` is in `RimWorldLLM.Mind`; `RimWorldLLM.Bridge.IGameAdapter` has the same name in another namespace (qualify when both used). (parity-contract)
118. `ProtocolNames.RimWorld` moved to `RimWorldAdapter.WireNames` (the core must not name the game); `RimWorldAdapter.DefaultBridgeJson()` reads `$HOME` like Python (`DefaultBridgeJsonFor(home)` for tests). (parity-contract, parity-bridge)
119. `GameAdapterBase` supplies getattr defaults (null means use core text; sets/lists empty; `FirstTrigger` default "The first time you have ever {phrase}: {summary}"); `StewardKit` initially null with `SilentEvents` literal `{"letter","alert"}` (later wired to RimWorldStewardKit). (parity-contract, parity-jobs)
120. Core lint: `SplitTests.CoreImportsNothingFromTheGame` and `CoreHasNoGameTerms` check no file under `Mod/Source/Mind/{Core,Llm,ToolLoop,BridgeClient}` references `RimWorldLLM.Mind.RimWorld` or matches the Python game-word regex per line. (parity-contract, parity-bridge)
121. Bystanders/Reflection/Wants/Grudges/Concerns/Inner/Passes ported with host interfaces (IConcernsHost, IWantsHost, IGrudgesHost) replacing the duck-typed director. (parity-inner)
122. Test host play clock stops on pause (test-only). (parity-inner)
123. Children (age < 13 by digest `pawns[].age`) are refused in `draft` (named) and `order`; `draft all` leaves them out; message "children stay out of fights". (parity-delta4)

### Creative jobs (art, lore, recap, dreams, chronicle, grounding)

124. Bug fixed: `Chronicle.BuildMessages` loops forever in Python when the budget is smaller than the fixed text and the summary is already at 200 code points; C# stops when the summary no longer shrinks (test ImpossiblyTinyBudgetStopsInsteadOfLooping). (parity-side)
125. chronicle.jsonl/.md are always written with `\n` (Python text mode writes `\r\n` on Windows); append-only chronicle files added. (parity-side)
126. pydantic `Note`/`Entry` are `ChronicleNote` (value equality) and `ChronicleEntry`. (parity-side)
127. Hosts are interfaces (`IGroundingHost`, `ICreativeHost`, `IChronicleAdapter`, `IGroundingAdapter`, `IIdleYield`); `d.cfg` getattr-with-default reads become `CreativeSettings` (`CreativeSettings.From(MindConfig)`). (parity-side)
128. `StewardBoard.Plain` takes `object`; 0, false and "" give "" as Python `text or ""`. (parity-side)
129. `Art.System` is named `Art.SystemText` (clashes with System namespace). (parity-jobs)
130. Jobs prompts are byte-identical (goldens from venv Python); RNG order identical. (parity-jobs)
131. C# cannot count a cancelled LLM call, so Python's `llm.cancelled == 1` assertion becomes "job ended, window released, nothing applied, still nothing applied after the gate opens". (parity-jobs)
132. `narrator_owner = "steward"` set by a test is reset by `StewardSession.Reset` on a reload step (same in C#); recap-waits test uses another owner string. (parity-jobs)
133. NEW live steward streaming (not in Python): with `Steward.Live` set the tool loop passes a sink: Sent/Queued -> Begin (thinking; a later step after typing returns to thinking via Progress(-1)); Progress -> Progress(processed/total, at most 4/s); FirstToken -> Typing; Partial -> Partial(FinalText.Decode(raw)), cut at 500, age-gate checked, throttled to 8/s with trailing text flushed at Done; final line posted to /chat with `stream_id` then End(id, line); a yielded call (BackendAway) ends with `Steward.PausedNote`; no line -> Cancel; thinking state refreshed every 30 s (`LiveRefresh`, limit 60 s); live-sink exceptions swallowed. With no sink: ordinary request and POST /chat/status thinking/idle as Python. (parity-jobs)
134. Art queue: the art job stands down once the text queue answers (`!d.TextQueueLive` added to the art clause of `anything_due()` and `_start_art()`). (parity-delta)
135. Textfill ported (games/rimworld/textfill.py to `TextFill`, host `ITextFillHost`); `LlmLog.Warning/Debug("rimworldllm", ...)` replace `log.warning/log.debug`. (parity-delta)

### Steward and rules

136. Rule conditions (`StewardExpr`, replaces Python `ast`): hand-written parser, same accepted set; all syntax errors say `syntax: invalid syntax`; disallowed constructs are reported where the parser meets them; `\N{...}` escapes refused (RuleError); division by zero or integer overflow gives "skip" (no rule fires) instead of raising; the most-common-schedule tie-break is first seen (Python's is set order, arbitrary). (parity-side)
137. Malformed digest items (wrong types) are treated as absent/empty where Python would raise (steward digest, BuildKit word lists, combat view lists). Strings and dicts are iterated as Python does in BuildKit word lists. (parity-side, parity-build, parity-delta3)
138. `rules` parameter: `StewardRules.FiredRules` and `AlertsSection` take an optional rules list where Python tests monkeypatch `load_rules`. (parity-side)
139. `StewardSession.RunAsync(tool, JObject values)` takes values `ToolSpec.Check` produced; errors are `{"error": ...}` exactly as Python strings. (parity-side)
140. Resources: steward_system.txt, rules.json, steward_strategy.md are `EmbeddedResource` with fixed LogicalNames (`RimWorldLLM.Mind.*`) read through `StewardResources.Text`; combat resource embedded as `RimWorldLLM.Mind.steward_combat.txt`; the Mod.Tests csproj compiles Mind/** and so needs the same declarations. (parity-side, parity-delta3)
141. Caravans NOT PORTED (keeper decision, never ported): no `caravan`/`caravan_plan` tools, planners, caravan prompt/strategy lines, CARAVANS digest block, caravan facts or caravan tests; `render_plan`, quest/destination/animal/pawn-name logic, `OPS/PURPOSES/MAX_PAWNS`, `_names/_where/_goods/_quest_check` not ported; `LOOKS = (trade_offer)`, `ACTS = (trade)`. (parity-delta3)
142. Because of 141, steward_system.txt and steward_strategy.md were NOT byte copies in delta3 (caravan pieces removed: heading "## Trade, caravans, quests" -> "## Trade and quests"; the "- Caravans: 2-3 pawns ..." line dropped; "(bills, trade, caravans, quests)" -> "(bills, trade, quests)", 12,197 bytes; system text "Trade works through trade_offer and trade ... a trade cannot be undone", "(trade when its tools are not listed)"). rules.json copied verbatim (growth.caravan_safety rule stays, harmless). (parity-delta3)
143. A digest `caravans[].can_trade` still makes a settlement a trade partner, as in Python; the `caravans` key stays in sample JSON and is ignored by the renderer. (parity-delta3)
144. Delta4: Python itself has caravans off, so the `nocaravan` capture patch is gone and goldens come from unpatched Python; the C# still has no caravan tool. (parity-delta4)
145. Steward `why` param on every acting tool (`WHY_MAX = 80`, cleaned with `gd.clean(.., "why", 80)`, stripped, sent only when non-empty); set_time no longer sends `reason`. (parity-delta)
146. `Param.same_call` / `ToolSpec.call_key` (`Param.SameCall`, `ToolSpec.CallKey`) used for both the repeat check and `RunCalls`. (parity-delta)
147. `ADVISOR_NOTE` wording ("you have no tool for") in `StewardKit.AdvisorNote`; trade/build/combat kits slot into `StewardSession` (`Trade`, `Build`, `Combat`, `afterFight`, `CannotFight`, draft "all", order move 0,0, `order` dropped when squad exists). (parity-delta3, parity-build)
148. Combat: target numbers (focus target, `group N`, bare ids) parsed as BigInteger then narrowed to long (Python ints unbounded); `int()` of text with underscores ("1_000") accepted by Python but not here (grammar never emits it); a wrong-typed list in the view is read as empty. (parity-delta3)
149. `DigestCaps` `{1500,1000,700,450}` defined in StewardCombat.cs with a seam in StewardDigest (merge to keep one definition); combat resource loader lives in StewardCombat.cs. (parity-delta3)
150. `StewardTools.cs` visibility changes for tests (additive): `StewardSession.VerbBody`, `CombatKit.Squad/Defend`, `Steward.CycleAsync` internal; `StewardFake.CallRec` gained `MaxTokens`; tests call `RunAsync` instead of `_verb_body`. (parity-delta3)
151. Build digest and BuildKit: wrong-type digest values (truthy non-dict `home`, non-iterable `can_build/stuff/ores/rooms/benches`, dict `rooms` slice) are empty/absent instead of raising; `str.lower()` is `ToLowerInvariant`. (parity-build)
152. Goldens regenerated after each delta, with per-golden notes: fake bridge note carries a random op id (both sides normalise to `op_id X`); bio e2e uses only rejected/refused calls since an acting bio verb's note has a random `op_id`; ELAPSED wall-clock line ignored in `E2eGoldenTests.StewardTryMatchesThePythonCallForCall`. (parity-build, parity-delta3, parity-delta4)
153. Python quirks kept (delta4): "the babys are: Dee" (naive plural in unknown-name error); `str.lower` semantics for genepack matching; pawn-id keys normalise 5, 5.0 and true alike (a string stays distinct). Capture-script stream ids contain the PID (existing e2e test tolerates them). (parity-delta4)
154. Delta3 harvest-days night-rest 0.55 factor lives in the mod's C# (VivariumFood.cs, CombatPure.cs), not agent/: nothing to port. (parity-delta3)
155. Delta3 test tweaks: "#4 stockpile all 36 cells" -> "food"; LOOSE count test scoped to the LOOSE line; TrimMatchesPython allows max(cap,220); `StewardBuildTests.PromptAndSize` limit 8500 -> 8800. (parity-delta3)
156. Dialogs: `choose` tool offered only when the digest has dialogs and the steward is not advisor; steward wakes on `dialog` also while paused; `silent_events` gains `dialog`. (parity-delta2)

### Tool loop

157. Loop behaviours are as in A1 items 4, 5, 8, 9, 21; additionally `Loop.cs` repeat rule uses `CallKey`. (parity-llm, parity-jobs)
158. Edits by the steward-test helper in delta-owned files (StewardTools `why`, ToolLoop/Spec.cs `SameCall`/`CallKey`, Loop.cs repeat rule via `CallKey`, steward_system.txt) duplicate delta's landed work; at merge take delta's version. (parity-jobs)
159. `ToolLoop` commentary param: a commentary param does not make a repeat a new call (ToolLoopTests +1). (parity-delta)
160. steward_try now prints `REJECTED: <f>` for each `tr.get("failures")` before `BRIDGE ACTS:`; the steward trace carries `failures`. (parity-delta)

### Adapter and RimWorld

161. `IGameAdapter : IMemoryAdapter, IFirstsAdapter` holds every member of the Python Protocol including those read with `getattr`; `NameInfo { Short, Tokens }`; message lists are `List<(string Role, string Text)>` tuples. (parity-contract)
162. `IdleFill()` returns `Func<ITextFillHost,int,Task<int>>` with base default null (Python `getattr(adapter, "idle_fill", None)`); `DropsMeta` base default false. (parity-delta)
163. Commentary adapter hooks (`CommentaryPrompt/Request/RecapRequest/Moment`) base null; RimWorld overrides in AdapterText.cs/Adapter.cs. (parity-delta2)
164. Embedded `Mod.Tests` test data and resources: combat/growth/digest samples embedded as `RimWorldLLM.Tests.<name>`. (parity-delta3)
165. `kit.strategy_extra(digest)` appended after the strategy; trade view `StewardDigest.TradeView(JObject d, IList<string> arrived)`; `Steps` is 13 four-int tuples. (parity-delta4)
166. Biotech/bio digest, trade2 (quests declined, walking trade) ported from steward_bio.py, steward_biotech.md, steward_trade.txt as byte copies. (parity-delta4)

### Tests and harness

167. Goldens are captured from the Python (venv, httpx 0.28.1), never typed by hand; generator scripts live under ~/.local/share/rimworld-hermes-engine/csmind/. (parity-llm, parity-contract, parity-build)
168. `Mod.Tests/Mind/Harness/Replay.cs` (replay.py plus cli.py's `_replay` printer) lives in the test project because it needs FakeBridge/FakeLlm; data `multiday.jsonl`. (parity-director)
169. MindHarness (tools/MindHarness, net10.0 exe, ships nowhere): `replay` uses FakeLlm unless `--llm` is given; `--data-dir` required for every writing command and never defaults to the real data dir; `speedsim` prints the same table; `SPEEDSIM_TRACE=1`, `SPEEDSIM_SETTLE_MS` (default 0.5); `comment-try` was only a dispatcher exiting with "not wired"; steward-try `dialogs`/`paused` args not ported. (parity-tools)
170. Speedsim: asyncio virtual-time loop replaced by a single-thread TaskScheduler (`VirtualTime`) with a jumping clock; ConfigureAwait(false) continuations escape to the thread pool, so the sim is not bit-deterministic (clock jumps after a pool quiescence check plus a 0.5 ms settle window; without it the clock jumped 3-6 s over a poll, the "3x polling gap"); output equals the Python table on lines, shed, conv, idle counts, within a few percent on delay and util; test bounds carry slack. (parity-tools)
171. E2E goldens: multiday 101 LLM calls / 120 writes and mixed 109 / 174 match 100% byte for byte; chitchat skipped pending delta2 wording; steward grammar/advisor/json/native skipped, 4/4 match once delta2's resources are synced. The steward stream id embeds the pid, normalised `steward-PID-`; the HttpLlm HTTP-body golden was deferred. (parity-tools)
172. `CommentTry` runs in Mod.Tests (FakeBridge lives there) and returns rows; the CLI print/argparse not ported; events from `comment_events_sample.json`. (parity-delta2)
173. Test-only seams and quirks: `PyRandom` unsealed with virtual `Random()`; `World` takes optional `rng` and `wrapAdapter` (a `DispatchProxy` over `IGameAdapter`, since `RimWorldAdapter` is sealed); `SwitchRng` returns 0.0 once switched on. (parity-contract)
174. World-test port notes (group): `bridge_cfg = {...}` becomes `D.BridgeCfg["events"] = ...` (mutated, not replaced); the preemption test sets `Preempted` in `World.Hook.Before` instead of wrapping `complete`; a `conv` JValue holding a null string has JSON type String so tests treat null/empty alike. (parity-group)
175. Other test notes: test_recap_waits_for_the_cycle spawns the recap through a late connect (`recap_open`) because `_recap_when_free` is private; model-tier `_inner_after_say` tests go through public `Director.SayAsync`; `Steward.UserText` called by reflection in test_user_turn_has_alerts; `JobsTestHost.cs` mirrors `Director.step`. (parity-side, parity-jobs)
176. Test files in namespace `RimWorldLLM.Tests.Mind` need `using MindM = RimWorldLLM.Mind.Mind;`. (parity-text)
177. `LeaseTests` takes `RimWorldAdapter.Priority` instead of a local priority table copy. (parity-contract)
178. `InnerGoldenTests` strips the `_NO_META_RULE` sentence from expected bystander goldens until the rule is restored (FINDING: AdapterText.cs ReactionRequest lacks it). (parity-inner)
179. `FakeBridge` flags added: `TextSupported`, `TextItems`, `TextFillLog`, `TextPendingExtra`, `OnTextQueue`, `CombatMissing`, `ActApply` internal, `Looks`, `Offers`, `TradeOffer`, `Dialogs`, `RefuseComment`. (parity-delta, parity-delta3, parity-delta2)
180. Not applicable: test_package_data_lists_strategy_files (pyproject; files are embedded). (parity-side)

### Notes on stale statements in the appendices
- `parity-director` still lists `NarratorCoverAsync` in the Director's API; `parity-delta2` removed it (item above).
- `parity-idle` says speedsim has no port and counts 12 tests; it was ported later in `tools/MindHarness/SpeedSim.cs` with 17 tests (`parity-tools`).
- Caravan wording differs between `parity-delta3` (resource text patched) and `parity-delta4` (patch dropped, Python has caravans off); the C# has no caravan tool either way.

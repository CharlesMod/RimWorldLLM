# Parity: core/director.py (+ idle.py, group.py glue, the `run` loop of cli.py, games/rimworld/replay.py) -> Core/Director*.cs, Core/MindRunner.cs

## Files
- `Core/Director.cs`, `Director.Events.cs`, `Director.Serve.cs`, `Director.Hosts.cs` (partial class; host interfaces implemented here).
- `Core/MindRunner.cs`: the `run` loop of cli.py. `Core/BridgeChatLive` (live chat bridge).
- `Mod.Tests/Mind/Harness/Replay.cs`: replay.py plus cli.py's `_replay` printer. It is in the test project because it needs FakeBridge and
  FakeLlm, which the Mod does not ship. `RimWorldAdapter.ReplayAsync` stays `NotSupportedException`. Data: `Mod.Tests/Mind/Data/multiday.jsonl`.
- `Mod.Tests/Mind/Fakes/World.cs`: the conftest `world` fixture (HookLlm + World).

## Public API
Director: PrepareAsync, SayAsync, PaceAsync, RepeatCheck, RememberOwn, BystandStart, RunConversationAsync, RunBystanderAsync, ServeAsync,
FinishConversationAsync, NarratorCoverAsync, SaveDirtyAsync, TurnCap (static), StartAsync, StepAsync, RunAsync, DrainAsync, Dispose.
Properties: Cfg, Bridge, Llm, Adapter, Day, Minds, Ledger, Leases, Gate, Wants, Chronicle, Lore, Art, Tier, DreamBook, Concerns, Grudges,
Idle, Group, CreativeLog, Dropped, TextTried.
MindRunner(cfg, adapter, transport, token="", clock=null, backoffS=5.0): Director, LastError, Restarts, Running, Start(), StopAsync(),
Dispose(), static MakeToolBackend(cfg) (null when Wants and Steward are both off).

## Differences from the Python
- asyncio is replaced by a `ConcurrentExclusiveSchedulerPair`; every entry point goes through RunOnLoop, spawn defers the start. Gap: library
  code that uses ConfigureAwait(false) continues on the thread pool until it returns to the Director.
- Reload does a bounded 2 s wait for the running task instead of a cancel.
- MindRunner: loop catches all errors, logs each distinct message once (LlmLog.Warning), backs off, retries. It never closes the caller's
  transport. The other cli commands (replay, steward_try, config printing, argparse) are not ported; replay lives in the test harness.
- BridgeChatLive builds the non-narrator wire word as a private const (`ActorWho`) because the Core lint forbids game terms; it compares `!= "narrator"`.
- Host-interface type clashes are resolved by explicit implementations (long Day, IDictionary Minds, ISet MinorTokens, IList Dropped, IStewardIdle Idle).
- Fields became get-only properties (host interfaces need them).

## Test seams added (additive)
Director.ListenerHook, public InnerKw/InnerArgs/InnerAfterSayAsync, LeaseManager.SubmitHook, PyRandom virtual Random() + RandomOverride,
FakeBridge.OnRequest, HookLlm.Before/BeforeKind, World ctor options (tweak, events, rng, wrapAdapter, llm).

## Tests
DirectorTests 13 (test_director), MindRunnerTests 3 (new), ReplayMultidayTests 2 (test_replay_multiday), plus world tests ported by helpers:
ContractWorldTests 27, ConversationWorldTests 30, InnerWorldTests 42, MindWorldTests 18, JobsWorldTests 33, StewardWorldTests 66,
CreativeWorldTests 16; test_group's 17 world tests are in the group part's files. test_lease has no world-fixture tests.

## Deferred (with reasons)
- test_speed_sim (17 collected): games/rimworld/speedsim.py is a standalone dev simulation, not ported.
- test_steward_more steward_try_cli (1): no C# CLI.
- test_wants_wiring (5): CLI/config wiring in cli.py; MindRunnerTests covers MakeToolBackend only.
- test_concerns raid_worriers: ConcernRules port, not the Director.
- test_loopguard offline_bench_numbers (1): needs bench/ result files.
- test_register bystander_prompt_has_the_register_line (1): unowned.
- test_gpu (1): out of scope. test_art `due_since` bookkeeping beyond the starving test.

## Count (honest, approximate)
Python collects 1015 tests; about 28 remain deferred (list above), so about 987 are ported or covered by a C# equivalent.

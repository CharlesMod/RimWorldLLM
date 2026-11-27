# Parity: core/idle.py -> Core/Idle.cs

`IdleRunner(IIdleHost d)`; `IIdleHost : IArtHost, IDreamHost, ILoreHost, IWantsHost, ITextFillHost` plus: `Ledger Ledger`,
`IDictionary<int,double> Weights`, `IDictionary<int,int> WantsLast`, `IDictionary<int,string> Reputations`, `IBackend ToolBackend`,
`string ToolMode`, `double Clock()`, `Now Now()`, `Task Spawn(Func<Task>)`, `void RefreshReputations()`.

## Clashes between the inherited host interfaces
Same name in several siblings, so `d.X` on IIdleHost is ambiguous; IdleRunner reads each through one named sub-interface, and the Director
implements them explicitly where types differ:
- `Minds`: IDictionary<int,Mind> (Art, Dream) vs Dictionary<int,Mind> (Wants). Idle uses the Dictionary one (IWantsHost).
- `Dropped`: IList<(int,string)> (Dream) vs List<(int Actor,string Reason)> (TextFill). Idle uses the List (ITextFillHost); one list must back both.
- `MinorTokens()`: HashSet<string> (Art, Dream, Lore) vs ISet<string> (Wants). Idle uses IArtHost's.
- Same type, only ambiguous by name: Bridge, Llm, Cfg, Adapter, Names, NameTokens, Chronicle, Dirty, Day (int), BridgeCfg, IsMinor, IdleLog (idle's own `Log`).

## Differences from the Python
- Cancellation: no token reaches ILlmClient, so a preempted job's pending call runs on detached and its result is discarded; every await in
  a job is `Task.WaitAsync(token)`, so the job sees OperationCanceledException at the same points CancelledError hit (art puts its job back).
  Chronicle.WriteAsync/RunWantsAsync/RunDreamAsync mutate their own state after the LLM returns, which a detached call may still do.
- `StartActor` takes a `Func<Lease, CancellationToken, Task>` (the lease is passed in; Python read `self.lease` when the coroutine first ran).
- art/chronicle `finally`: `Task = null` only when `Lease == null` and the job is still the current one (Python: lease None only).
- `Preempt(string)` returns bool; `IStewardIdle.Preempt` (void) is explicit. `ActorsWantWindows()` (IIdleYield) and `ActorsWantWindows(bool starving)`.
- `Chronicle.WriteAsync` wants IChronicleAdapter; IGameAdapter is not one, so a private view delegates the three chronicle prompts.
- `chronicle not written` catch is BridgeError only (no AttributeError in C#).
- `TextFillFn()` calls `adapter.IdleFill()` only when text fill is on (Python `make()`); a null result means off.
- Unhandled job exceptions are logged (LlmLog.Warning) instead of left on the asyncio task.
- Thread model: Python was single-threaded; the Director's Spawn must marshal jobs onto the director's context (IdleRunner takes no locks).

## Tests
C#: Mod.Tests/Mind/IdlePureTests.cs (wants roll vs Python sha1, floor div/mod, float of bridge value, constants).
C# world tests: Mod.Tests/Mind/JobsWorldTests.cs (test_textfill idle_* and art_idle_job_skipped_when_text_queue_live, test_art anything_due/starving/actors_want_windows/idle job,
test_diary_chronicle_pacing idle tests); MindWorldTests.cs (test_wants / test_wants_wiring due_wants, test_reflection idle.log); InnerWorldTests.cs (test_vanilla_social meta thought drop);
StewardWorldTests.cs (test_steward_more idle.task/kind/due_wants, test_steward_chat_status).
Deferred: test_art.py `due_since` bookkeeping beyond the starving test; test_speed_sim.py (needs a C# port of games/rimworld/speedsim.py, which does not exist yet; 12 tests, no Director involved).

# Parity: jobs (art, lore, recap, dreams, steward)

Python `agent/rimworldllm_agent/core/{art,lore,recap,dreams,steward}.py` -> `Mod/Source/Mind/Core/{Art,Lore,Recap,Dreams,Steward,JobsCommon}.cs`.
The steward kit wrapper lives in `Mod/Source/Mind/RimWorld/RimWorldStewardKit.cs` (Core imports nothing from the game).
Prompts are byte-identical (goldens captured from the venv Python, `Mod.Tests/Mind/Data/jobs-*-golden.json`); RNG order is identical.

## Host interfaces (each in its module's file; members named after the Python `d` attributes; the Director implements them all)
- `IArtHost`, `ILoreHost` {Creative, Ground, Bridge, Llm, Cfg, Adapter, Leases, Chronicle, Lore, Names, NameTokens, MinorTokens()},
  `IRecapHost` {Creative, Ground, Bridge, Llm, Cfg, Adapter, BridgeCfg, NameTokens, RecapEpoch, MinorTokens()},
  `IDreamHost` {Bridge, Llm, Cfg, Adapter, BridgeCfg, Names, Minds, NameTokens, DreamBook, Dropped, Dirty, IdleLog, Day, MinorTokens(), IsMinor(int)},
  `IStewardHost` {Bridge, Cfg, Adapter, Leases, ToolBackend, ToolMode, Idle, Gate, Dropped, Day, Clock(), Spawn(Func<Task>), MinorTokens()}.
- Steward also: `IStewardKit`, `IStewardSession`, `IStewardIdle` {Active(), Kind, Preempt(who)}, `StewardOutcome`, `IChatLive`.
- `ICreativeHost Creative` / `IGroundingHost Ground` are separate views the host exposes.

## Public API
- `Art` (ArtMessages, WriteArtAsync, Enabled, GameQuality, SystemText), `ArtJob`, `ArtQueue`.
- `Lore` (ParseEntry, Messages, CoinAsync, LoadAsync), `LoreBook` (Max 12), `LoreEntry`.
- `Recap` (Messages, ParseLines, FallbackLines, BuildAsync, RunAsync, LateDue + constants).
- `Dreams` (SleepEvent, DreamKind, Enabled, NightOf, Roll, DreamMemories, RunDreamAsync), `DreamJob`, `DreamBook`.
- `Steward` (LoadStrategy, Reset, Active, Observe, StepAsync, Live, LiveRefresh, PartialMinGapS, PausedNote, state fields), `StewardStats`.
- `IChatLive`: `Begin(who, pawnId?, name)->id`, `Progress(id, 0..1; negative = refresh)`, `Typing(id)`, `Partial(id, wholeText)`, `End(id, finalText)`, `Cancel(id)`.
- `LoopOptions.CallOptions` (ToolLoop/Loop.cs): when set and the backend is `IStreamingBackend`, the call goes through the streaming overload.

## Streaming (new, not in Python)
With `Steward.Live` set the tool loop passes a sink: Sent/Queued -> Begin (thinking; a later step after typing goes back to thinking via Progress(-1));
Progress -> Progress(processed/total, at most 4/s); FirstToken -> Typing; Partial -> Partial(FinalText.Decode(raw)), cut at 500, age-gate checked, throttled to
8/s with the trailing text flushed at Done; the final line is posted to /chat with `stream_id` and then End(id, line); a yielded call (BackendAway) ends with
`Steward.PausedNote`; no line -> Cancel. The thinking state is refreshed every 30 s (`LiveRefresh`, limit is 60 s). Live-sink exceptions are swallowed.
With no sink: no CallOptions, the ordinary request, POST /chat/status thinking/idle exactly as Python.

## Tests ported (xunit)
ArtTests 25, LoreTests 15, RecapTests 19, DreamsTests 21, StewardTests 42 (30 Python tests, Theory rows expanded), StewardMoreTests 9, StewardChatStatusTests 4,
StewardGoldenTests 71 (prompts, load_strategy, _clean_line), StewardStreamTests 12 (fake IChatLive). Host: `JobsTestHost.cs` (JobsWorld).
Ported: test_art 12/15, test_lore 9/10 (one partly), test_recap 14/14, test_dreams 15/16, test_steward 30/30, test_steward_more 9 of 14 ported (rest below),
test_steward_chat_status 4/5, the 15 test_creative_ground tests touching these modules.

## Deferred (reason: needs Director internals, no `world` fixture in C#)
- (ported by helper w5 into Mod.Tests/Mind/JobsWorldTests.cs over the World fixture: art preempted-job put back, pending art due/starving, starved art vs reactive work,
  dream preempted by actor work, lore SHARED NAMES + epoch-change reload, recap through the Director, recap waits for slot 0.)
- (test_creative_ground's 15 `world` tests ported by helper w7 into Mod.Tests/Mind/CreativeWorldTests.cs over the World fixture; nothing of it is deferred any more.)
- steward: all ported over the real Director in StewardWorldTests.cs (recap waits, wants off, idle yield incl. the preemption, letters/alerts through
  Director.ingest) except `test_steward_try_cli_runs_one_cycle` (no C# CLI: it drives games/rimworld/steward_try.py); the two digest tests are
  covered by StewardToolsTests and `test_client_bodies` by BridgeClientTests.

## Behaviour differences / notes
- World tests (JobsWorldTests): C# cannot count a cancelled LLM call (no token reaches `ILlmClient`; the idle job is cancelled with `Task.WaitAsync(token)` and the call is detached),
  so Python's `llm.cancelled == 1` is replaced by "job ended, window released, nothing applied, and still nothing applied after the gate opens".
- Python's `narrator_owner = "steward"` set by a test is reset by `StewardSession.Reset` on a reload step (same in C#); the recap-waits test uses another owner string.
- `Lore.cs` keeps a private copy of the refusal regex (Sanitize's is private).
- `LoadStrategy` normalises CRLF/CR to LF (Python text mode); "not read" notes use C# OS error class names (OSError/PermissionError mapped, wording of the OS text may differ).
- `Art.System` is named `Art.SystemText` (clashes with the System namespace).
- Test host mirrors `Director.step` (poll events, Observe, StepAsync, drain, reset on epoch change).
- Edits made by the steward-test helper in files owned by worker delta (StewardTools `why`, ToolLoop/Spec.cs `SameCall`/`CallKey`, Loop.cs repeat rule via `CallKey`,
  `Resources/steward_system.txt`) and in `Adapter.cs` (StewardKit now returns a `RimWorldStewardKit`; SilentEvents from it). They duplicate delta's landed work: at merge,
  take delta's version of those files (the steward goldens need delta's `steward_system.txt`); keep the Adapter StewardKit wiring (it replaces a TODO(csmind-merge)).

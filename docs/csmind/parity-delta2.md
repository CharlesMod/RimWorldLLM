# Parity: delta2 (Python agent changes after 7e72a24 ported into the C# Mind)

Python source: `git log --oneline 7e72a24..HEAD -- agent` (323e57a, bbd7696, 04352a9, e568d54, 2f81bc8, ded8259, f1ba55c, d54248b).
Commits on agent/csmind-delta2: 0cb90a5 (chitchat, loose materials, dialogs/choose, steward wake), a893556 (StewardDialogsTests),
cf6bd96 (documentary voice, config, Director wiring, comment_try, narrator_cover removal).

## File map
| Python | C# |
|---|---|
| adapter.py chitchat wording (323e57a), `silent_events` gains `dialog`, `_EVENT_CLASS["dialog"]` | `RimWorld/Adapter.cs`, `AdapterText.cs` regenerated (contract/gen_adapter_text.py, copy in csmind/delta2 with the three commentary strings added) |
| steward_digest.py loose line, RESOURCES `(+N loose, M forbidden)`, OPEN DIALOGS | `RimWorld/StewardDigest.cs` (`Loose` level, `DialogLines`) |
| steward_rules.py (loose counts as stock) | `RimWorld/StewardRules.cs` |
| steward_tools.py `choose` tool, dialog wake type, ActingNames, EventLine | `RimWorld/StewardTools.cs` |
| core/steward.py `dialog_pending`, wake on `dialog` also while paused | `Core/Steward.cs` (`DialogPending`) |
| core/bridge.py `dialogs()` | `IBridge.DialogsAsync()` (GET /dialogs), `BridgeClient` |
| steward_system.txt, strategy/steward_strategy.md | `RimWorld/Resources/*` copied byte for byte |
| core/commentary.py | `Core/Commentary.cs`: `Moment`, `CommentaryLine`, `ICommentaryHost`, `Commentary` |
| games/rimworld/moments.py | `RimWorld/Moments.cs`: `Moments.ForEvent(JObject, ICommentaryHost)` |
| adapter `commentary_prompt/_request/_recap_request`, `commentary_moment` | `IGameAdapter.CommentaryPrompt/CommentaryRequest/CommentaryRecapRequest/CommentaryMoment` (base: null), RimWorld overrides in `AdapterText.cs` and `Adapter.cs` |
| core/config.py `commentary`, `commentary_min_gap_real_s` 180.0, `commentary_min_gap_game` 15000, `commentary_recap` | `MindConfig.Commentary/CommentaryMinGapRealS/CommentaryMinGapGame/CommentaryRecap` (JSON keys and order as Python) |
| core/director.py wiring | `Core/Director.cs`, `Director.Events.cs`, `Director.Hosts.cs` (see below) |
| fakes.py FakeBridge (/dialogs, choose verb, who=comment) | `Mod.Tests/Mind/Fakes/FakeBridge.cs` (+ `Dialogs`, `RefuseComment`) |
| comment_try.py `try_events` | `Mod.Tests/Mind/CommentTry.cs` |

## Public API other workers call
- `Director.Commentary` : `Commentary` (property). `Commentary.Note(JObject e)`, `RunOnce()`, `PreemptIfWanted()`, `Reset()`, `Enabled()`, `Active()`,
  `Messages(mode, ms, day)`, state `Pending`, `Log` (`CommentaryLine {Mode, Kind, Day, Text}`), `Drops`, `Unsupported`, constants `MinWeight`, `DedupeGame`, ...
- `Director : ICommentaryHost` (Cfg, Bridge, Llm, Leases, Adapter, Day, Now(), Spawn, Dropped, NameTokens, MinorTokens(), IsMinor(int), IdleActive, StewardVv).
- `Moments.ForEvent(JObject e, ICommentaryHost d = null)` returns `Moment` or null.
- `IBridge.DialogsAsync()` returns `{dialogs, timeout_s}`; the `choose` tool is offered only when the digest has dialogs and the steward is not advisor.
- Director wiring, as the Python: constructed before Steward; `Commentary.Reset()` after `Idle.Preempt("reload")`; `PreemptIfWanted()` after `Idle.PreemptIfWanted()`;
  `Commentary.Note(e)` after `Steward.Observe(e)` in ingest (before the silent-events skip); `Commentary.RunOnce()` after `Idle.RunOnce()` in `Launch`.
- Mind side of `settings.commentary`: `Commentary.Enabled()` reads `Steward.Vv["settings"]["commentary"]` (default true) and needs `Vv.playing`. The mod already mirrors
  `narratorCommentary` into /vivarium settings (Mod/Source/Bridge/VivariumState.cs), nothing to do for wire.

## comment_try entry point (for the tools worker)
`RimWorldLLM.Tests.Mind.CommentTry.TryEventsAsync(IList<JObject> events, ILlmClient llm) : Task<List<CommentTryRow>>` (Mod.Tests/Mind/CommentTry.cs).
Rows: `Event, Kind, Mode ("moment"|"recap"), Line (null = no comment), Drop`. `CommentTry.Format(row)` prints the Python main()'s two lines.
Events come from `Mod.Tests/Mind/Data/comment_events_sample.json` (embedded as `RimWorldLLM.Tests.comment_events_sample.json`; same file as agent/tests/data).
The CLI (`--events`, optional `--llm-url/--model` to an HTTP llm, FakeLlm without) is the tools worker's. Python `test_comment_try_main_prints` and
`test_dialog_try_runs_one_cycle_on_samples` (the dialog_try CLI over dialogs_*.json) are deferred to it; the samples are embedded as `RimWorldLLM.Tests.dialogs_*.json`.

## narrator_cover (removed)
The Python director still generates a one-line narrator cover for a queued item with no window (`narrator_cover`), appends it to `d.narrator_lines`, and nothing reads
`narrator_lines` (docs/VIVARIUM.md says so; grep over agent/, Mod/, Mod.Tests finds no reader). Removed from C#: `Director.NarratorCoverAsync`, `Director.NarratorLines`,
`Director.CoverAllowed`, `_lastCover`, the cover loop in `Launch`, and the test `NarratorCoverageMinGapBypassedByDeathsAndMajorThreats`. Kept: `LeaseManager.NarratorShouldCover` (and its
LeaseTests, lease.py still has it), `IGameAdapter.NarratorPrompt` (the contract golden still pins it) and the `narrator_min_gap_*` config fields (config round trip).
Effect: an item that would have been covered now stays in the lease queue until it expires normally instead of being removed after the (unread) cover line.
Suite stayed green, including the multiday replay.

## Behaviour differences from the Python
- `Task.cancel()` is a `CancellationTokenSource`; the LLM call and the POST wait through `WaitAsync(token)`, like the idle jobs. A preempt cancels the call, nothing is posted.
- `Commentary.Seen` pruning (over 200 keys, drop the 100 oldest) orders by game time with LINQ's stable sort; Python's dict order for equal times may differ. Only ties are affected.
- The Moment's `game` is an `int` (the bridge envelope's type); `DayMoments` keys are `int`.
- `Pick` returns the first of equal (weight, game) maxima, as `max()` does.
- `commentary_moment` on an adapter without it: the base returns null (Python `getattr(..., None)`).
- A dropped line's `d.dropped` reason is `commentary:<reason or sanitize>` as Python.
- `CommentTry` runs in Mod.Tests (FakeBridge lives there) and returns rows; the CLI print/argparse is not ported here.

## Tests
C# added: StewardDialogsTests 6, CommentaryTests 14 (the 14 of 15 Python tests; the CLI-main one deferred), StewardRulesTests +1, StewardToolsTests +2; one removed (cover min gap). Total 2806 (baseline 2784).
Python tests ported: test_commentary 14/15, test_steward_dialogs 6/7 (dialog_try CLI deferred), the new test_steward_rules/test_steward_more/test_vanilla_social cases.
Goldens regenerated: ContractGoldenData.cs, steward_golden.json, jobs-steward-golden.json (scripts in ~/.local/share/rimworld-hermes-engine/csmind/delta2).

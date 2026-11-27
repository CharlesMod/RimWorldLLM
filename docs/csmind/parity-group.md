# Parity: group (core/group.py -> Mod/Source/Mind/Core/Group.cs)

Namespace `RimWorldLLM.Mind`, Verse-free. Same constants, regexes (the sentence split uses an explicit Python `\s` class),
branch order, sha1 roll (`Inner.Sha1Mod("gather:"+key, 1000)/1000`), strings and log JSON field order.

## C# surface
- `GroupState` (Seen, Over, Told, Started, Skipped, Lines, Rotations, Ends, Log), `Gathering` (ctor in the Python dataclass field order).
- static `Group`: `Kinds`, `Ceremonies`, `CeremonyCaps`, `StoryChars`, `StoryRepeatDays`, `Setting`, `SceneClass`, `Tier`, `TurnCap`, `PickStory`,
  `PlanSpeakers`, `NextSpeaker`, `Trigger`, `Request`, `CeremonyTurn`, `DetailsAsync`, `TemplateMemory`, `OwnsFuneral`, `HandleAsync`,
  `RunAsync`, `Finish`; helpers `PayloadOf(Gathering)` / `GatheringOf(WorkItem)`; internal `Sentences`.
- `IGroupHost` (in the same file): see the report; `ICreativeHost Creative` and `IGroundingHost Ground` are the separate views, as in the jobs hosts.

## Behaviour notes (differences from the Python)
- `WorkItem.Payload` is a `JObject`, the Python payload holds the live `Gathering`. The scene rides in a `JRaw` (`new JObject{["gathering"]=new JRaw(g)}`);
  build the item payload with `Group.PayloadOf(g)` (HandleAsync does) and read it with `Group.GatheringOf(item)`. Nothing serialises it.
- `_repeat_check(g, sp)` reads only `g.turns`, so `IGroupHost.RepeatCheck(IList<Turn> turns, int speaker)` takes the turns, not a Gathering.
- The `held` dict loses and regains keys while rotating windows and Python dicts keep insertion order; a small private ordered class keeps that
  order (min-by-last-spoke picks the first of equal keys, release order in the `finally` follows it).
- `except BaseException` -> `catch (Exception)`: `OperationCanceledException` re-raises (Python `CancelledError`), anything else is logged through
  `LlmLog.Warning("rimworldllm", "gathering failed: ...")` (no traceback text).
- `int(e.get("game", now.game))`/`float(talk_frequency)`: a JSON null value throws like the Python's `int(None)`/`float(None)` (missing key uses the default).
- `GroupState.Told` values are `long` (Python int); `Env.Game` is `int` in C#.
- `d._inner_kw` is not used by group.py, so no `InnerKw` member and no `SpeakExtras` (Passes.SpeakAsync takes individual arguments).

## Tests
C#: `Mod.Tests/Mind/GroupPureTests.cs` 25 rows (keys, rolls, trigger/request/template text, plan/next speaker, pick_story over a stub host, GroupState,
payload round trip), expected values captured from the Python with `~/.local/share/rimworld-hermes-engine/csmind/director-group/gen_group.py`.
test_group.py ported: `test_cap_by_speed_and_ceremony_floor`, `test_plan_order_and_no_repeat_in_a_row`.

All 17 world tests are ported in `Mod.Tests/Mind/ConversationWorldTests.cs` (same names in PascalCase: `MealSceneRotatesWindowsAndBlockFCarriesEveryTurn`,
`EachParticipantKeepsOneMemory`, `SpeedCapsMealTurnsAnd4xHasNoMealScene`, `StoryPickUsesChroniclePeopleAndIsNotRepeated`, `MealOpenerRetellsTheStoryInThePrompt`,
`FuneralLeadEulogyWithTokenCapAndOldEventIsSilent`, `FuneralEventStillReactsWhenGatheringsAreOff`, `WeddingAdultsSayVowsInOrder`,
`WeddingWithAMinorOrUnknownAgeHasNoScene`, `WeddingMinorGuestDoesNotSpeakAndNoMissingCouple`, `MinorAtAMealCarriesTheMinorRule`, `SpeechLeaderThenListeners`,
`SwitchesOffMeansNoSceneAndNoMemory`, `GatheringEndStopsASceneAndUrgentWorkPreempts`, `BudgetIsSpentPerLine`, `SceneEndsWhenTheOthersAreApart`,
`UrgentWorkPreemptsTheScene`). Nothing from test_group.py is deferred.
Port notes: `bridge_cfg = {...}` becomes `D.BridgeCfg["events"] = ...` (the dict is mutated, not replaced); the preemption test sets `Preempted` in
`World.Hook.Before` (before the first model call returns) instead of wrapping `complete` to set it after; a `conv` JValue holding a null string has JSON type String, so the
tests treat null/empty alike.

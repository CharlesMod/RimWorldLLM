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

Deferred (need the Director / `world` fixture; 17 tests):
- scene flow through step/drain with FakeBridge+FakeLLM: test_meal_scene_rotates_windows_and_block_f_carries_every_turn, test_each_participant_keeps_one_memory,
  test_speed_caps_meal_turns_and_4x_has_no_meal_scene, test_story_pick_uses_chronicle_people_and_is_not_repeated, test_meal_opener_retells_the_story_in_the_prompt,
  test_speech_leader_then_listeners, test_budget_is_spent_per_line, test_scene_ends_when_the_others_are_apart.
- ceremonies and age gate (Director._prepare/_say, creative flags, bridge age lookups): test_funeral_lead_eulogy_with_token_cap_and_old_event_is_silent,
  test_funeral_event_still_reacts_when_gatherings_are_off (Director.ingest + owns_funeral), test_wedding_adults_say_vows_in_order,
  test_wedding_with_a_minor_or_unknown_age_has_no_scene, test_wedding_minor_guest_does_not_speak_and_no_missing_couple, test_minor_at_a_meal_carries_the_minor_rule.
- switches and preemption (Director settings, leases): test_switches_off_means_no_scene_and_no_memory, test_gathering_end_stops_a_scene_and_urgent_work_preempts,
  test_urgent_work_preempts_the_scene.

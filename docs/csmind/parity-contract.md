# csmind parity: the game contract (adapter, prompt, speech, builders) and the RimWorld adapter

Python source: `agent/rimworldllm_agent/core/{adapter,prompt,speech,builders}.py` and `games/rimworld/{adapter,builders,register,concern_rules}.py` + `block_a.txt`.
C# target: `Mod/Source/Mind/Core/{GameAdapter,Prompt,Speech,Builders}.cs` (namespace `RimWorldLLM.Mind`) and `Mod/Source/Mind/RimWorld/*.cs` (namespace `RimWorldLLM.Mind.RimWorld`).

## File map

| Python | C# |
|---|---|
| `core/adapter.py` (GameAdapter Protocol, NameInfo; ProtocolNames stays in `BridgeClient/BridgeTypes.cs`) | `Core/GameAdapter.cs`: `IGameAdapter`, `GameAdapterBase`, `NameInfo` |
| `core/prompt.py` | `Core/Prompt.cs`: `Scene`, static `PromptBlocks` |
| `core/speech.py` | `Core/Speech.cs`: `Speech` |
| `core/builders.py` | `Core/Builders.cs`: `MindBuilders` |
| `games/rimworld/adapter.py` | `RimWorld/Adapter.cs` (logic) + `RimWorld/AdapterText.cs` (prompt wording) : `RimWorldAdapter` |
| `games/rimworld/builders.py` | `RimWorld/Builders.cs` (`Builders`) |
| `games/rimworld/register.py` | `RimWorld/Register.cs` (`Register`) |
| `games/rimworld/concern_rules.py` | `RimWorld/ConcernRules.cs` |
| `games/rimworld/block_a.txt` | `RimWorld/BlockAText.cs` (a string constant; no embedded resource, so the csproj is untouched) |
| (helpers for reading snapshot dicts) | `RimWorld/Js.cs` (internal) |

`AdapterText.cs` and `BlockAText.cs` are generated from the Python by `~/.local/share/rimworld-hermes-engine/csmind/contract/gen_adapter_text.py`, so the wording is byte for byte.

## Public C# API

- `IGameAdapter : IMemoryAdapter, IFirstsAdapter` holds every member of the Python Protocol, including the ones the Python read with `getattr` (thought/dream/reaction/art/lore wording, `BystanderTemplates`, `ArgumentIntents`, `RecapImportance`, `SilentEvents`, `StewardKit`, `ArgumentParties`, `GatherInfo`, `VanillaSocial`/`VanillaRequest`, `Concern*`, `SocialOffence`, `OffenceIntents`, `GrudgeStyle`, `CreativeCard`, `FirstTrigger`, `ReplayAsync`).
- `GameAdapterBase` supplies the getattr defaults: `null` means "use the core's own text"; sets and lists default to empty; `FirstTrigger` defaults to `"The first time you have ever {phrase}: {summary}"`.
- `NameInfo { Short, Tokens }`.
- `PromptBlocks`: `GrammarFor`, `BlockB/C/D/E/F/G`, `BuildPrefix`, `LineMessages`, `AimRequest`, `IntentRequest`, `IntentMessages`, `AimMessages`, constants `BioLineCap`, `EvolvingCap`, `Deliveries`, `AimRule`, `AimOnlyGrammar`, `AimAsk`. Message lists are `List<(string Role, string Text)>` tuples (value equality, so "the intent pass shares the line pass bytes" is `Assert.Equal`).
- `Speech.RegisterPhraseAsync(adapter, bridge, actorId, mind, snap)`, `Speech.RegisterLine(name, phrase)`.
- `MindBuilders.LlmExampleLinesAsync(llm, adapter, mind, slot)`, `MindBuilders.EnsureMindBuiltAsync(mind, snap, names, adapter, llm, useLlm)`.
- `RimWorldAdapter : GameAdapterBase`, `RimWorldAdapter.WireNames` (the RimWorld `ProtocolNames`), `RimWorldAdapter.DefaultBridgeJsonFor(home)`; `Builders`, `Register`, `ConcernRules` static classes with the Python function names in PascalCase.

## Python tests: ported vs deferred

All captured against the Python (`gen_golden.py` writes `Mod.Tests/Mind/ContractGoldenData.cs`; no value is typed by hand):

- `ContractGoldenTests` (79 cases with theory rows): for 26 pawns (default, rich, child, drunk, tired, dict job, injection payloads, old-mod shapes, relations, skill/gene/health register cases) the facts hash, fixed bio text/hash/extras, voice card, talk weight, speech register, register arithmetic, temp states, `speech_now`, snapshot name/age/job/relations/name info, creative card, voice prompt, three scenes each (scene fields and block E); for 6 minds every block (B, C with names/worries/caps, D with the memory mix, E, F, G in every argument shape), the whole prefix, line/intent/aim messages in every shape and `grammar_for`; for 24 events normalised bytes (key order), priority, importance, pinning, first person, first_of, argument parties, gather info, pair, vanilla social/request, concern births/resolutions, social offence, predicates, first trigger; the adapter constants, the wire names, `block_a`, and every wording property byte for byte (reflection over the property names).
- `ContractTests`: test_register (all 21 non-Director tests, 12-row parametrised skill table included), test_prompt (3 of 4), test_character_card (all 17 non-Director tests, 5 hash-edit rows), test_split (`core_event_normalised_from_wire`, `adapter_and_transports_satisfy_the_protocols` adapter half, the two core lints below).
- `ContractDeferredTests` (the earlier-deferred ones): test_agegate `prompt_carries_minor_flags_and_rule`, `prompt_marks_minors_present_and_adult_prompt_has_no_flag` (without the Director: the same prefix assertions from adapter-built scene and bio) plus unknown age in the scene; test_adult `prompt_rule_only_when_off`; test_gamedata `names_from_the_player_are_cleaned`, `every_prompt_block_carries_game_text_as_data`, `prefix_is_deterministic_for_a_lease`, and the `has_raw_syntax` assertion of `attack_is_neutralised` (for every attack row); test_memory_minds `importance_from_game_numbers_and_pinning`, `builders_deterministic_with_dlc`, `llm_voice_pass_written_once`; test_firsts `first_of_table`; the block B/C/D/A assertions that had waited for the prompt module (test_gossip_chain `duplicates_merge...` and `reputation_lines...in_block_c`, test_upkeep_gossip `told_by` / `[private, told to]` / `block_a_carries_the_secret_rule`, test_habits `replaces_the_oldest_tic` block B part); a `RememberEvent` check against the real adapter; the dict-job test.
- `LeaseTests` now takes `RimWorldAdapter.Priority` instead of its local copy of the priority table.
- C# core lint (`SplitTests.CoreImportsNothingFromTheGame`, `CoreHasNoGameTerms`): no file under `Mod/Source/Mind/{Core,Llm,ToolLoop,BridgeClient}` references `RimWorldLLM.Mind.RimWorld` or matches the Python game-word regex per line. To pass it `ProtocolNames.RimWorld` moved to `RimWorldAdapter.WireNames`.

Counts: 157 C# tests added by this part (1332 to 1489), mod build 0 warnings.

Deferred (need the Director / world fixture / modules owned by other parts; port with the Director wave):

- test_register: `drunk_pawn_gets_the_scene_line...`, `dream_prompt_has_the_register_line`, `diary_prompt...`, `thought_prompt...` (Director), `bystander_prompt_has_the_register_line` (core/bystanders).
- test_prompt: `lease_prefix_stable_through_director`.
- test_character_card: none left. test_split: `say_refusal_reasons...` was already ported; `gossip_names_a_third_actor...` and `every_write_carries_an_op_id` need the world fixture.
- test_agegate: the 12 tests that drive the Director (`_prepare`, `labels_for`, conversation, group, intent, vanilla, player talk, diary path).
- test_adult: `live_toggle_from_bridge_config_and_stats`, `missing_key_means_off`, `settings_refresh_on_their_own` (Director + bridge config).
- test_gamedata: `director_prompt_holds_planted_scene_text_as_data`, `idle_prompts_wrap_game_text_too` (reflection module).
- test_habits: the 2 reflection-module tests (`schema_and_prompt_only_when_habits_asked`, `apply_reflection...`) and the Director ones.
- test_firsts and test_loopguard Director tests; test_creative / test_clouds `world` tests.

## Behaviour differences from the Python (and why)

1. **Dict job.** The Python `build_scene` set `Scene.job = snap.get("job") or "idle"` and printed a present person's job the same way, so a `{def, label}` job (main c16670a) appeared as the dict's repr. The C# reads the job label with `Js.JobText` (string, or `label`, falling back to `def`), then `"idle"`. The golden data therefore uses string jobs for scenes; `snapshot_job` itself matches the Python (it already returned the label).
2. `StewardKit` returns `null` and `SilentEvents` is the literal `{"letter","alert"}` (`StewardKit.silent_events`) with a `TODO(csmind-merge)`; the steward kit belongs to the side part. `ReplayAsync` throws `NotSupportedException` (replay is ported with the director/cli wave).
3. The static prompt class is `PromptBlocks` (the namespace `RimWorldLLM.Mind.ToolLoop` already has a `Prompt`). Prompt functions take and return `List<(string, string)>` rather than `LlmMessage`.
4. `Speech.RegisterPhraseAsync` takes the adapter and the bridge instead of the Director.
5. `IGameAdapter` is in `RimWorldLLM.Mind`; `RimWorldLLM.Bridge.IGameAdapter` (the pump's game seam) has the same name in another namespace, so a file that uses both namespaces must qualify one (`InProcessTransportTests.TestGame` does).
6. `ProtocolNames.RimWorld` is now `RimWorldAdapter.WireNames` (the core must not name the game).
7. `RimWorldAdapter.DefaultBridgeJson()` reads `$HOME` like the Python (`DefaultBridgeJsonFor(home)` for tests).
8. Python `isinstance(x, (int, float))` includes bool, and a JSON null reads as missing (`dict.get` returning None) in every snapshot reader; both are mirrored in `Js`. Rounding is banker's rounding (`PyFmt.RoundInt`); `str.title()` is mirrored.

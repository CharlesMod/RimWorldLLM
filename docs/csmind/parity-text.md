# Parity: text part (wave 1)

Py helpers: PyRandom (CPython MT19937, verified seeds 1/7/42/0/2^40+5 against Python 3.14), PyFmt (Repr, Fixed, Round, RoundInt, FloorDiv, Mod).
Modules: AgeGate, Adult, GameData, CommonWords, Think, Sanitize, LoopGuard, Lease, Ledger, Minds, Memory, Firsts, Conversation, Gossip, Habits.

## Behaviour differences
- Sanitize uses explicit whitespace class (Python `\s` includes U+001C-1F); .NET `\w` also matches combining marks; NFKC/case folding from .NET tables (rare code points may differ).
- Adult.Enabled is a static with SetEnabled(object); director calls it on config refresh. Tests touching it share xunit collection "AdultState".
- Minds: hand-written pydantic-lax validator (PyModel.Validate); invalid blob -> fresh mind; `relations` is an insertion-ordered RelationMap. Compact JSON writer lives in PyModel.Compact (ensure_ascii off); a shared PyJson may replace it.
- Memory/Firsts use small adapter interfaces (IMemoryAdapter, IFirstsAdapter) until the GameAdapter lands. Module functions of memory.py are in static MemoryOps (class Memory is the model).
- Lease: Cls enum PascalCase, Name() gives Python name; insertion order via *InOrder() methods.
- Conversation.CheckEnd has PyRandom and Func<double> overloads (PyRandom is sealed).
- Test files in namespace RimWorldLLM.Tests.Mind need `using MindM = RimWorldLLM.Mind.Mind;`.
- BlobTrimsDiariesBeforePinnedMemories uses a smaller limit (Mind.Size is private).

## Deferred tests (need Director/world fixture, LLM, bridge or reflection module)
The contract part (docs/csmind/parity-contract.md) ported everything that needed only the adapter, prompt or builders. Still deferred:
- test_agegate: the 12 Director tests (`_prepare`, `labels_for`, conversation, group, intent, vanilla, player talk, diary path).
- test_adult: live_toggle_from_bridge_config_and_stats, missing_key_means_off, settings_refresh (Director + bridge config).
- test_gamedata: director_prompt_holds_planted_scene_text_as_data, idle_prompts_wrap_game_text_too (reflection module).
- test_sanitize: VoiceCard/passes halves of the guard tests.
- test_loopguard: offline_bench_numbers (bench files), 4 Director tests.
- test_memory_minds: none left.
- test_firsts: 7 Director tests (first_of_table is ported).
- test_habits: 2 reflection-module tests, 8 Director tests; test_gossip_chain/upkeep_gossip: none left (the Director tests are in `MindWorldTests.cs`).

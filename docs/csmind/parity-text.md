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

## Deferred tests (need Director/world fixture, LLM, bridge, RimWorldAdapter, prompt/reflection modules)
- test_agegate: prompt/labels/conversation/group/intent/vanilla/player-talk/diary-path tests (14+).
- test_adult: prompt_rule_only_when_off, live_toggle_from_bridge_config_and_stats, missing_key_means_off, settings_refresh.
- test_gamedata: names_from_the_player, every_prompt_block, prefix_is_deterministic_for_a_lease, director_prompt, idle_prompts, has_raw_syntax assertion.
- test_sanitize: VoiceCard/passes halves of the guard tests.
- test_loopguard: offline_bench_numbers (bench files), 4 Director tests. test_lease uses a local copy of the adapter priority table (re-point when adapter lands).
- test_memory_minds: importance_from_game_numbers_and_pinning, builders_deterministic_with_dlc, llm_voice_pass_written_once.
- test_firsts: 7 Director tests, first_of_table (adapter).
- test_habits: 2 reflection-module tests, 8 Director tests; test_gossip_chain/upkeep_gossip: Director tests, adapter tests; some block_b/c/d assertions dropped (prompt module not ported).

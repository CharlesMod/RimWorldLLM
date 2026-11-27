# The mind in C# (RimWorldLLM.Mind)

The agent that used to be the Python package `agent/rimworldllm_agent` (about 13.3k lines, httpx +
pydantic, run by the engine in a managed Python) now lives inside the mod assembly as
`RimWorldLLM.Mind`. Nothing is downloaded or launched for it, and no HTTP hop sits between the
mind and the game.

## Port plan

### Layout
- `Mod/Source/Mind/` is Verse-free (no UnityEngine, Verse or RimWorld types); `Mod.Tests` compiles
  it directly (`<Compile Include="../Mod/Source/Mind/**/*.cs" />`).
  - `Mind/Py/`: parity helpers. `PyRandom` (CPython's Mersenne Twister and `random` methods, so a
    seeded run draws the same numbers), `PyJson` (`json.dumps` byte-compatible output), `PyFmt`
    (Python `str()`/`repr()` of floats and f-string formats), `PyRe` notes.
  - `Mind/Core/`: the game-agnostic core, namespace `RimWorldLLM.Mind`.
  - `Mind/Llm/`: config, LLM client, call log, engine detection, model tier, sampler route, fakes.
  - `Mind/ToolLoop/`: the tool loop and GBNF grammar, namespace `RimWorldLLM.Mind.ToolLoop`.
  - `Mind/BridgeClient/`: `IBridge`, `BridgeClient` over an `IBridgeTransport`, `EventPoller`,
    the in-process transport and the test `FakeBridge`.
  - `Mind/RimWorld/`: the Python `games/rimworld` adapter (still Verse-free: it speaks the bridge
    contract), namespace `RimWorldLLM.Mind.RimWorld`.
- `Mod/Source/RimWorld/MindHost.cs` (Verse side): starts and stops the mind with the game.

### Conventions
- Python dict bodies stay `JObject` (Newtonsoft); pydantic models become C# classes whose JSON
  matches `model_dump` field for field and in order, so minds, chronicle and diaries already on
  disk or in saves load unchanged.
- `async def` becomes `async Task`; asyncio timeouts become `CancellationToken`s; the clock and the
  RNG are injected (`Func<double>` monotonic seconds, `PyRandom`), as in the Python.
- All number and string formatting uses the invariant culture.
- Every Python test gets an xunit counterpart under `Mod.Tests/Mind/` with the same name in
  PascalCase, or a line in `docs/csmind/parity-*.md` saying why not.

### Module map (Python -> C#) and order
| Wave | Python | C# |
|---|---|---|
| 1 text | agegate, adult, gamedata, common_words, sanitize (+llm.strip_thought), loopguard, lease, ledger, minds, memory, conversation, firsts, gossip, habits | Core/AgeGate.cs, Adult.cs, GameData.cs, CommonWords.cs, Sanitize.cs, Think.cs, LoopGuard.cs, Lease.cs, Ledger.cs, Minds.cs, Memory.cs, Conversation.cs, Firsts.cs, Gossip.cs, Habits.cs; Py/* |
| 1 llm | config, calllog, llm, fakes (FakeLLM), endpoint_test, model_tier, sampler_route, toolloop/* | Llm/MindConfig.cs, CallLog.cs, LlmClient.cs, FakeLlm.cs, EndpointCheck.cs, ModelTier.cs, SamplerRoute.cs; ToolLoop/Backend.cs, Harness.cs, Loop.cs, Parse.cs, Scale.cs, Spec.cs |
| 1 bridge | bridge, games/rimworld/fakes (FakeBridge) | BridgeClient/IBridge.cs, BridgeClient.cs, EventPoller.cs, InProcessTransport.cs, FakeBridge.cs; Bridge/BridgeServer.cs gains a transport-free submit path |
| 2 | adapter (GameAdapter contract), prompt, passes, inner, speech, bystanders, concerns, grounding, wants, builders, steward_board | Core/*.cs |
| 2 | games/rimworld: adapter, builders, register, concern_rules, steward_digest, steward_rules (+rules.json), steward_tools, prompt files | RimWorld/*.cs (rules.json and the .txt/.md prompts as embedded resources) |
| 3 | creative, chronicle, art, lore, recap, dreams, reflection, grudges, group, steward, idle, director | Core/*.cs |
| 3 | cli (run loop), replay, speedsim, steward_try | Core/MindRunner.cs, RimWorld/Replay.cs, SpeedSim.cs; tools/mind-harness console |
| 4 | wire-in | RimWorld/MindHost.cs, settings, EngineLauncher without the agent block |

Waves run in parallel inside themselves (one Sonnet worker per row); a wave starts once the one
before it is merged and green.

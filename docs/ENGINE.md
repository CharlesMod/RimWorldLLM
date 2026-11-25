# The local engine (easyEngine) in HermesVoice

The one-click local LLM engine lives in its own repo, **[easyEngine](https://github.com/CharlesMod/easyEngine)**,
because it is game-agnostic. Its design and contract (control API, game profile, CLI, the context
balancer, the model catalog) are in
[easyEngine/docs/ENGINE.md](https://github.com/CharlesMod/easyEngine/blob/main/docs/ENGINE.md).
This file covers only the RimWorld side.

To build the mod with the engine, check easyEngine out next to this repo and run its `build.sh`,
then `agent/build-wheel.sh` here.

## In game (`Mod/`)

- The mod ships the binaries in `Engine/<os>-<arch>/hermes-engine[.exe]` and the agent wheel
  in `Engine/agent/`. `build.sh` in the easyEngine checkout cross-compiles them into `dist/<os>-<arch>/`; the csproj deploy target copies them from `$(EasyEngineDir)` (default `../easyEngine`, a sibling checkout).
- `Mod/Source/Bridge/EngineLauncher.cs` and `EngineClient.cs` (no Verse references): pick the
  binary for the platform, set the exec bit on Unix, write the profile next to `bridge.json`,
  attach to a running engine or spawn one with `--parent-pid` (hidden window), and wrap the
  control API.
- Settings, Connection section: **LLM source: Local engine (default) | My own endpoint.**
  Local shows the engine's phase light and one line (model, slots × ctx, tok/s) and a
  **Set up / Change model…** button that opens `Window_EngineSetup` (the same content as the
  setup page: hardware, recommendation, fit verdicts, Install with progress, Cancel). My own
  endpoint keeps today's fields and Test button, saved to the engine as external mode and to
  `bridge.json` as today.
- First run: if the engine says `not_installed`, the mod shows one dismissible letter
  ("HermesVoice can set up a local model: Set up / Use my own endpoint / Later").
- Nothing here blocks the game thread: all engine calls are async with short timeouts, results
  marshalled to the main thread.


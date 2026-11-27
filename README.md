# RimWorldLLM

Local AI colonist dialogue for [RimWorld](https://rimworld.com/) 1.7. One click, no API key.

Your colonists talk, in their own voices: speech bubbles over their heads, real conversations,
private diaries, a colony chronicle, and inner thoughts that show what they want but don't say.
The lines come from what is actually happening in your colony (injuries, deaths, raids, mental
breaks, weddings, funerals, grudges, gossip), and the tone matches RimWorld's own.

It runs entirely on your own computer. It is free, needs no account and no API key, and nothing
about your colony leaves your machine.

**Version 2.1.0 beta.** Tested on Linux with NVIDIA cards. Windows, macOS and AMD/Intel builds
are included but not yet tested. Reports are very welcome (see [Reporting a problem](#reporting-a-problem)).

## Install

1. Install [Harmony](https://steamcommunity.com/workshop/file/filedetails/?id=2009463077)
   (Steam Workshop, or [GitHub](https://github.com/pardeike/HarmonyRimWorld/releases/latest)).
2. Install RimWorldLLM: from Nexus Mods, or download `RimWorldLLM-<version>.zip` from the
   [GitHub releases](https://github.com/CharlesMod/RimWorldLLM/releases) and unzip it into
   RimWorld's `Mods` folder, so you get `Mods/RimWorldLLM/About/About.xml`.
3. Enable Harmony, then RimWorldLLM (below Harmony) in the mod list, and restart.

## First run

1. Start or load a game. A letter offers to set up a local model: click **Set up**.
   (If you closed it: Options, Mod settings, RimWorldLLM, Model, **Change model...**)
2. The setup window shows your graphics card, memory and disk, and recommends a model.
   Click **Install**.
3. Your colonists start talking within a few minutes on a small **starter model** while the
   full model downloads. Speech is simpler for now; a line in the settings shows the download.
4. When the full model is ready, the mod switches over by itself at a quiet moment, tells you,
   and deletes the starter to free the space.

The engine starts with the game and stops when you quit (or if the game crashes).

## Hardware

| Your graphics card | Model you get | Notes |
|---|---|---|
| 16 GB or more | Gemma 4 26B RP (10.0 GB download) | The full experience. Uses about 12 GB of video memory. |
| 12 GB | the 26B with part of it in system memory, or E4B RP | Slower lines with the 26B. |
| 6 to 12 GB, laptops, Apple Silicon | Gemma 4 E4B RP (3.65 GB download) | Good speech; inner thoughts and art descriptions are plainer. Uses about 5 GB. |
| Very small card, or none | stock Gemma 4 E2B (3.1 GB download) | Works, but speech is simpler and repeats more. The mod warns you when it is in use. |

The engine measures your free video memory and leaves room for the game. You can change the
model at any time in the setup window. Speeds we measured: the 26B writes about 105 tokens (word pieces) per
second per conversation on an RTX 5080; E4B RP speaks a line in about 1.4 seconds
on an older Tesla P40.

### What is downloaded

| What | From | Size |
|---|---|---|
| One model, sized to your card | Hugging Face (`cmod/gemma4-rp-maxxed-GGUF`, `cmod/gemma4-e4b-rp-GGUF`, `unsloth/gemma-4-E2B-it-GGUF`) | 3.1 to 10.0 GB |
| llama.cpp (runs the model) | its official GitHub releases (`ggml-org/llama.cpp`, build b12680) | NVIDIA: about 0.8 GB download, 2 GB on disk. AMD/Intel (Vulkan) or Mac: under 0.1 GB |
| uv and Python 3.12 (for the mod's helper program) | uv's GitHub releases; Python and two small packages through uv | about 0.2 GB on disk |

Every download is checked against a fixed SHA-256 and resumes if interrupted. With an NVIDIA
card expect about 12 GB on disk for the 26B (about 16 GB at the peak, while the starter is still
there), or about 6 GB for E4B RP.

### Already have a GGUF model file?

Options, Mod settings, RimWorldLLM, Model, **Use my own GGUF file...**: pick the file with
**Browse...**, click **Check** to see whether it fits your card, then **Use this file**. The file
stays where it is (it is never copied or deleted); only the model server (llama.cpp) is downloaded.

### Already run a model server?

Options, Mod settings, RimWorldLLM, Model: choose **My own endpoint** and enter the URL of
any OpenAI-compatible server (llama.cpp, LM Studio, Ollama, vLLM, ...). Nothing is downloaded.

## Settings

Options, Mod settings, RimWorldLLM. Every option has a tooltip; nothing needs changing to get started.

- **Model:** what is running and how fast, **Change model...** (each model with its size, memory need and
  whether it fits your machine; switching keeps the current model talking until the new one is ready, then
  offers to delete the old one), **Use my own GGUF file...**, or **My own endpoint**.
- **Colonists:** how often they talk, how much lines nudge moods and opinions, wants, dreams, recaps, art,
  and which kinds of events get lines.
- **Display:** speech bubbles, inner-thought clouds, the director camera.
- **Content:** adult content (off by default). The age gate is always on.
- **Storage:** where the engine keeps its files and how much space they take, **Open folder**, delete single
  models, and **Remove downloaded files...** to free all of it.
- **Advanced:** bridge port, address and token, debug tools. Only needed for development.

The full list, with defaults, is in [docs/SETTINGS.md](docs/SETTINGS.md).

## Content settings

- **Adult content** is off by default. Turning it on asks you to confirm. When it is off,
  intimate moments fade to black.
- **The age gate is always on:** nothing romantic or sexual for or about any pawn under 18, in
  either mode. It is enforced in code, not only asked of the model.
- Text that comes from the game (names, backstories, tales) is treated as data, so a pawn named
  "ignore your instructions" is just a strange name.
- Each kind of moment (injuries, deaths, raids, art, sleep, gatherings, ...) has its own toggle
  in the settings, as do written art descriptions and the "Footer on shares".

## Sharing a moment

Log lines, conversations, diary pages and chronicle entries have a **Share** button: copy the
text or save a picture card to `SaveData/RimWorldLLM/Shares` in RimWorld's save-data folder.

## Privacy

- Everything runs on your machine. No telemetry, no accounts, no cloud.
- The only network traffic is the setup downloads listed above (Hugging Face, GitHub, and the
  Python packages uv fetches).
- The engine and the mod's bridge listen on `127.0.0.1` only.
- The helper keeps the colonists' memories and a log of model calls on your disk (see below).

## Uninstall

One button removes everything RimWorldLLM stored except the mod itself and its settings: Options, Mod settings,
RimWorldLLM, Storage, **Remove downloaded files...**. It stops the engine and the helper, deletes the models,
llama.cpp and Python, and (the box is ticked by default) the helper's memory of your colonists and its logs. The
engine keeps only its small log and state files. Your own GGUF file is never touched. Then unsubscribe from the mod,
or delete `Mods/RimWorldLLM`.

To do it by hand instead: quit RimWorld (the engine stops with it) and delete

- the engine folder:
  - Windows: `%LOCALAPPDATA%\HermesEngine`
  - Linux: `~/.local/share/hermes-engine`
  - macOS: `~/Library/Application Support/HermesEngine`
- the helper's folder (its memory and logs):
  - Windows: `%LOCALAPPDATA%\rimworldllm-agent`
  - Linux and macOS: `~/.local/share/rimworldllm-agent` (or `$XDG_DATA_HOME/rimworldllm-agent` if you set it)
- optionally `RimWorldLLM` in RimWorld's `Config` folder (settings) and in `SaveData` (shared pictures).

To free some space, delete single models you no longer use in Settings, Storage.

## Troubleshooting

| Problem | Try |
|---|---|
| No setup letter | Options, Mod settings, RimWorldLLM, Model, **Change model...** |
| The download stopped | Open the setup window and click **Retry**; it resumes where it left off. |
| "Not enough space" | The starter keeps working; free disk space and the full model download resumes by itself. |
| Colonists are quiet | Check the status line in Settings, Model. At 3x and 4x speed many moments pass before a line can be written; that is by design. |
| Speech is simple or repeats | You are on the starter (wait for the download) or on E2B (see Hardware). |
| The game stutters or runs out of video memory | The engine shrinks when the game needs memory. If it persists, choose a smaller model. |
| Antivirus warns about `hermes-engine` | It is the engine program shipped with the mod, built from the public [easyEngine](https://github.com/CharlesMod/easyEngine) source. See the checksums in the zip's `SHA256SUMS`. |
| It says the engine does not answer | The setup window shows the engine's log. Logs are in the engine folder's `logs` subfolder. |

### Reporting a problem

Please include: your OS, graphics card and its memory, the model shown in the settings, what you
expected and what happened, and these logs:

- RimWorld's `Player.log` (lines with `[RimWorldLLM]`):
  Windows `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`,
  Linux `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`,
  macOS `~/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`.
- `engine.log`, `llama-server.log` and `agent.log` from the engine folder's `logs` subfolder.

File it on the [GitHub issues](https://github.com/CharlesMod/RimWorldLLM/issues) page or the Nexus
Mods page. Logs do not contain account data, but they do contain your colonists' names and lines.

## Known limits

- Beta: tested on Linux with NVIDIA cards only so far. Windows, macOS and AMD/Intel are untested.
- English only. The 26B in particular is weaker in other languages.
- The best experience needs a 16 GB card; most people will get E4B RP, which is good but plainer.
- At 3x and 4x speed colonists speak less (many moments expire before a line is ready).
- The first setup is a large download (about 4 to 15 GB, depending on the card).
- RimWorld 1.7 only.

## Compatibility

- Requires Harmony.
- Does not need RimTalk and is meant to run alongside it.
- Other mods can add pawn context or ask a pawn to speak through a small
  [hook API](docs/HOOK-API.md).

## For developers

- [docs/DEVELOPING.md](docs/DEVELOPING.md): how the mod works, building, the event list,
  repository layout, running the agent by hand.
- [docs/BRIDGE-API.md](docs/BRIDGE-API.md): the bridge protocol and RimWorld schema.
- [docs/HOOK-API.md](docs/HOOK-API.md): the API for other mods.
- [docs/ENGINE.md](docs/ENGINE.md): the local engine on the RimWorld side; the engine itself is
  [easyEngine](https://github.com/CharlesMod/easyEngine).
- [docs/SPEC.md](docs/SPEC.md), [docs/SPEED.md](docs/SPEED.md),
  [docs/MODEL-BENCH.md](docs/MODEL-BENCH.md), [docs/E4B-PASS.md](docs/E4B-PASS.md),
  [docs/CREATIVE-ROUND.md](docs/CREATIVE-ROUND.md): design, speed and quality measurements.
- [agent/README.md](agent/README.md): the Python agent.
- Release: `scripts/package-release.sh`, [docs/release/](docs/release/).

## License

MIT, copyright (c) 2026 Charleszard. See [LICENSE](LICENSE). The engine
([easyEngine](https://github.com/CharlesMod/easyEngine)) is MIT too. The downloaded models carry
their own terms (Apache 2.0 with the [Gemma terms of use](https://ai.google.dev/gemma/docs/gemma_4_license)),
and llama.cpp and uv have their own licenses. The models are not made or endorsed by Google.

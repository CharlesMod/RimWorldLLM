# Nexus Mods page: RimWorldLLM 2.1.0 beta

Everything below the first rule is ready to paste. The page body is Nexus BBCode.

## Mod name

RimWorldLLM: Local AI Colonist Dialogue (one click, no API key)

## Summary (Nexus "brief overview", 255 characters max; this one is 219)

Colonists talk in their own voices: bubbles, conversations, diaries, inner thoughts. Runs fully on your PC: free, no API key, one-click setup picks a model for your GPU. Beta: tested on Linux + NVIDIA; Windows untested.

## Category and tags

- Category: **Gameplay** (if the RimWorld section lists a dialogue/immersion category, prefer that).
- Tags: AI, Dialogue, Immersion, Colonists, Roleplay, Storytelling, Local LLM, Quality of Life.
- Requirements tab: Harmony (link the Steam Workshop page and https://github.com/pardeike/HarmonyRimWorld/releases/latest).
- Adult content flag: **keeper decision** (see RELEASE-CHECKLIST.md). The mod's adult setting is off by default, but the
  downloaded models are general roleplay builds whose own cards are tagged not-for-all-audiences.
- Version: 2.1.0 beta. Game version: 1.7. File: `RimWorldLLM-2.1.0-beta.zip` (main file), with its `.sha256` in the description of the file.

---

[size=5][b]RimWorldLLM[/b][/size]
[size=3]Local AI colonist dialogue for RimWorld 1.7. One click, no API key.[/size]

[b][color=#e8a33d]Beta 2.1.0: tested on Linux with NVIDIA cards. Windows, macOS and AMD/Intel builds are included but have not been tested yet. If you try one, please tell us how it went.[/color][/b]

Your colonists talk, in their own voices. RimWorldLLM gives every pawn a mind and a way of speaking:

[list]
[*][b]Speech bubbles[/b] over their heads, in comic-style balloons.
[*][b]Conversations[/b] that go back and forth, and that you can join.
[*][b]Diaries[/b] each colonist keeps, and a [b]colony chronicle[/b].
[*][b]Inner thoughts[/b]: what a pawn wants or fears but does not say.
[*]Grudges, gossip, dreams, first-time reactions, eulogies at funerals, vows at weddings, and a "previously on" recap when you load a save.
[*][b]Share this moment[/b]: copy a conversation or diary page, or save it as a picture card.
[/list]

The lines come from what is actually happening in your colony, and the tone matches RimWorld's own: dry, grounded, sometimes grim.

[size=4][b]Runs on your own computer[/b][/size]
[list]
[*]Free. No API key, no account, no subscription.
[*]Everything stays on your machine. No telemetry, nothing about your colony is sent anywhere.
[*]The engine and the mod's bridge only listen on 127.0.0.1.
[/list]

[size=4][b]One click to set up[/b][/size]
On first start the mod offers to set up a local model. Click [b]Set up[/b], then [b]Install[/b]: it checks your graphics card, memory and disk, and downloads a model that fits. Your colonists start talking on a small starter model within a few minutes, while the full model downloads. The mod then switches over by itself and deletes the starter. The engine starts with the game and stops when you quit.

[size=4][b]Hardware[/b][/size]
[list]
[*][b]16 GB graphics card:[/b] Gemma 4 26B RP, the full experience (about 12 GB of video memory).
[*][b]12 GB card:[/b] the 26B with part of it in system memory (slower), or E4B RP.
[*][b]6 to 12 GB card, laptops, Apple Silicon:[/b] Gemma 4 E4B RP (about 5 GB). Good speech; inner thoughts and art descriptions are plainer.
[*][b]Very small card or none:[/b] stock Gemma 4 E2B. It works, but speech is simpler and repeats more; the mod tells you when it is in use.
[/list]
Measured: the 26B writes about 105 tokens per second per conversation on an RTX 5080; E4B RP speaks a line in about 1.4 seconds on an old Tesla P40.

[size=4][b]What is downloaded, and from where[/b][/size]
[list]
[*][b]One model[/b], sized to your card, from [b]Hugging Face[/b] (cmod/gemma4-rp-maxxed-GGUF 10.0 GB, cmod/gemma4-e4b-rp-GGUF 3.65 GB, or unsloth/gemma-4-E2B-it-GGUF 3.1 GB).
[*][b]llama.cpp[/b], which runs the model, from its [b]official GitHub releases[/b] (ggml-org/llama.cpp, build b12680). NVIDIA (CUDA): about 0.8 GB download, 2 GB on disk. AMD/Intel (Vulkan) or Mac: under 0.1 GB.
[/list]
Every download is checked against a fixed SHA-256 and resumes if interrupted. With an NVIDIA card expect about 12 GB on disk for the 26B (about 16 GB at the peak, while the starter is still there), or about 6 GB for E4B RP.

[size=4][b]About the executable in this mod[/b][/size]
The zip contains a small program, [b]easy-engine[/b] ([font=Courier New]Engine/<os>-<arch>/easy-engine[/font], [font=Courier New]easy-engine.exe[/font] on Windows). The mod's own mind (the part that keeps colonists' memories and writes their lines) runs inside the mod and needs no extra program. easy-engine is built from the public [url=https://github.com/CharlesMod/easyEngine]easyEngine[/url] source; the zip's [font=Courier New]BUILD[/font] file names the exact commit, and [font=Courier New]SHA256SUMS[/font] lists every file's checksum. It downloads llama.cpp from llama.cpp's own GitHub releases, and the models from Hugging Face. It is not code-signed yet, so Windows SmartScreen or a virus scanner may warn about it: that is because it is a new, unsigned program that downloads files, not because it does anything hidden. You can read or build it yourself.

[size=4][b]Where it lives and how to remove it[/b][/size]
[list]
[*]Windows: [font=Courier New]%LOCALAPPDATA%\EasyEngine[/font]
[*]Linux: [font=Courier New]~/.local/share/easy-engine[/font]
[*]macOS: [font=Courier New]~/Library/Application Support/EasyEngine[/font]
[/list]
The mod keeps colonists' memories and a log in [font=Courier New]%LOCALAPPDATA%\rimworldllm-agent[/font] on Windows and [font=Courier New]~/.local/share/rimworldllm-agent[/font] elsewhere. To remove everything: Mod settings, RimWorldLLM, Storage, [b]Remove downloaded files[/b] (tick the box to delete the memory and logs too), then delete or unsubscribe the mod. Or quit RimWorld and delete those folders by hand.

[size=4][b]Already run your own model server?[/b][/size]
Choose [b]My own endpoint[/b] in the mod settings and enter any OpenAI-compatible URL. Nothing is downloaded.

[size=4][b]Content[/b][/size]
[list]
[*][b]Adult content is off by default.[/b] Turning it on asks you to confirm. When it is off, intimate moments fade to black.
[*][b]A hard age gate is always on:[/b] nothing romantic or sexual for or about any pawn under 18, in either mode. It is enforced in code, not only asked of the model.
[*]Every kind of moment (injuries, deaths, raids, art, gatherings and more) can be switched off in the settings.
[/list]

[size=4][b]Requirements and compatibility[/b][/size]
[list]
[*]RimWorld 1.7.
[*][b]Harmony[/b] (required).
[*]Does not need RimTalk and is meant to run alongside it.
[*]Other mods can add context or ask a pawn to speak through a small hook API.
[/list]

[size=4][b]Install[/b][/size]
[list=1]
[*]Install Harmony.
[*]Had a test build called HermesVoice? Delete the old [font=Courier New]Mods/HermesVoice[/font] folder first; your settings and the colonists' memory carry over.
[*]Unzip [font=Courier New]RimWorldLLM-2.1.0-beta.zip[/font] into RimWorld's [font=Courier New]Mods[/font] folder, so you get [font=Courier New]Mods/RimWorldLLM/About/About.xml[/font].
[*]Enable Harmony, then RimWorldLLM below it, and restart.
[*]Load a game and click [b]Set up[/b] on the letter.
[/list]

[size=4][b]Known limits[/b][/size]
[list]
[*]Beta: only Linux with NVIDIA cards has been tested.
[*]English only.
[*]The best experience needs a 16 GB card.
[*]At 3x and 4x speed colonists speak less: many moments pass before a line can be written.
[*]The first setup is a large download (about 4 to 15 GB).
[/list]

[size=4][b]Reporting a problem[/b][/size]
Please post your OS, graphics card and memory, the model shown in the settings, what happened, and the logs: RimWorld's Player.log (the lines mentioning RimWorldLLM) and engine.log and llama-server.log from the engine folder's logs subfolder. The [url=https://github.com/CharlesMod/RimWorldLLM#troubleshooting]README[/url] lists where they are. Bugs here or on [url=https://github.com/CharlesMod/RimWorldLLM/issues]GitHub issues[/url].

[size=4][b]Source and license[/b][/size]
[list]
[*]Mod: [url=https://github.com/CharlesMod/RimWorldLLM]github.com/CharlesMod/RimWorldLLM[/url]
[*]Engine: [url=https://github.com/CharlesMod/easyEngine]github.com/CharlesMod/easyEngine[/url]
[/list]
The mod and the engine are MIT licensed, copyright (c) 2026 Charleszard. The downloaded models carry their own terms (Apache 2.0 with the [url=https://ai.google.dev/gemma/docs/gemma_4_license]Gemma terms of use[/url]; not made or endorsed by Google), and llama.cpp has its own license. Third-party notices are in the zip's Licenses folder.

# Changelog

## Unreleased: share an engine across your PCs

- **Same PC by default.** A fresh install runs the engine on this PC only (127.0.0.1): nothing is exposed, nothing asks to pair.
- **Share this PC's engine on my network** (Model, local engine, off by default). Your other PCs with RimWorldLLM can use it
  at home (LAN) or over Tailscale. It switches on live, shows the name and addresses with LAN and Tailscale badges and a QR
  of the hello URL, and asks in game "<PC> wants to use this PC's engine. Code: ..." with Allow and Deny (compare the code).
  A Paired PCs list has Remove. The first time, the Windows firewall (allow on private networks) or macOS asks about
  incoming connections.
- **An engine on another PC on my network** is a new LLM source. Find looks on the LAN (mDNS) and on Tailscale (online
  peers, once per press), shows each engine with a LAN or Tailscale badge, and pairs with a code. The token is kept per
  engine and sent only to it; the guest says "removed this PC" when the host removes it, and reconnects after restarts.
  Needs an engine with the share slice (older engines say "This engine can't share yet").
- New settings: `shareEngine`, `llmOwnKind`. What to check with two PCs: docs/release/PLAYTEST.md.

## Unreleased: the mind moves into the mod (C#)

- **Download bar on the main menu.** A first run on a clean machine now shows what is downloading and a 0-100 bar (updated once a
  second, no ETA) before any game is loaded. Models are shown by code name first (Lantern-4B, Campfire-26B, Bonfire-27B; said as Lantern, Campfire, Bonfire; Flashlight is the unused E2B).

The narrator's and colonists' mind (the agent) is now C# inside the mod, namespace `RimWorldLLM.Mind`. It was a Python program
that the engine installed and ran.

- **No Python.** No uv, no Python 3.12, no wheel: nothing is downloaded or launched for the mind, the zip is smaller and the
  engine profile has no agent block. The last commit with the Python agent is `36515840a97cbbbf576ffdfd782d0726f07b7443`.
- **My own endpoint needs no engine.** The mind calls your endpoint directly (with your key); the mod launches and attaches no
  engine in that mode. Chat is answered there too (the Python helper was never installed in own-endpoint mode).
- **A live narrator line.** Answers stream: "thinking..." with prompt progress, then "typing..." with the line growing, on an
  easyEngine (and as a streamed reply on other OpenAI-compatible endpoints; a non-stream fallback stays).
- **Memory carries over.** The data folder and every file format are unchanged: minds, diaries, the chronicle and `calls.jsonl`
  from a Python-era install load as they are. Storage and Remove memory work without an engine.
- **Status line.** Settings shows "Narrator and colonists: running / waiting for the model / waiting for a game / off / error".
  Edits to `config.toml` in the data folder restart the mind.
- **Never in a frame.** The mind runs on a background thread and reaches the game through the same queue the HTTP bridge uses
  (served per frame under a budget); the HTTP bridge is now opt-in for external tools. See docs/BRIDGE-API.md.
- **Differences from the Python** (full list in docs/CSHARP-MIND.md): `narrator_cover` is gone, caravans were not ported, the
  Chronicle no longer loops forever on a tiny budget, timeouts are per request, new `X-Request-Id` on every call.
- `bench/` is pinned to the last Python commit (bench/README.md). What to check in game: docs/release/CSHARP-MIND-PLAYTEST.md.

## 2.1.0 beta (first public release)

Tested on Linux with NVIDIA cards. Windows, macOS and AMD/Intel builds are included but untested.

### New: find and pair an easyEngine on another computer
- In My own endpoint, **Find engines on my network** lists easyEngines on the local network (or type its address).
  Connect, compare the six-digit code with the one on the engine's screen and press Allow there: the URL, model and
  key fill themselves. Nothing to type between computers. The game finds a paired engine again if its address
  changes, and says **Pair again** if the engine revokes the key. A typed endpoint works as before.

### New: Narrator plays (vivarium, phase 1)
- An eye toggle in the bottom-right play-settings row lets the narrator run the colony: work priorities, schedules,
  outfit/food/drug policies, beds, research, letters and quest choices, drafting and simple orders. You can still do
  anything yourself. Saved per game.
- Press **Enter** on the map to chat with the narrator (strategy, requests, standing orders); `@Name text` talks to a
  colonist. With the switch off the narrator answers as an advisor and does one task when you ask for it.
- The narrator keeps a goals board (Voices → Goals) you can read and edit, and logs every action it takes.
- Settings (Colonists → Narrator plays): it may pause for emergencies (on), it controls the speed (off), and how
  often it looks over the colony (every 6 game hours). Trade and combat micro come later.
- The chat panel can be dragged anywhere (hover strip) and resized (corner grip); "Reset chat panel position" in
  settings. Every generated line goes into it: colonist speech, thoughts, dreams, diary, chronicle, recap letters,
  art. Five filter chips (Talk, Thoughts, Narrator, Actions, Story) on the hover strip and in settings.
- Each thing the narrator does shows as its own greentext line, `> set Ana's Cooking to 1st, since meals are running
  low`; refused acts show dim with the reason.
- Colonists no longer remark on the act of chatting itself after a chitchat ("cut this chitchat").

### New: Narrator plays, phase 2 (building)
- The narrator can now build: rooms (it picks a site near the base: walls and a door), single things (a stove in the
  kitchen, beds, tables, coolers...), growing zones, stockpiles and dumps, bills at workbenches, and mining, chopping,
  harvesting and hauling orders. It states intent; the mod picks the cells, and never builds over beds or graves or
  tears down what you built.
- Everything it places is tagged: type `/undo` (or `/undo 3`) in the chat, or use the settings button, to take back
  its last actions (blueprints cancelled, zones and bills removed; finished buildings stay).
- Caps per look: 50 blueprints, 200 zone cells, 150 designations, 4 bills.

### New name: RimWorldLLM
- The mod was called HermesVoice in test builds; it is now **RimWorldLLM** (packageId `charleszard.rimworldllm`,
  folder `Mods/RimWorldLLM`). Delete the old `Mods/HermesVoice` folder when you install this one.
- Your settings carry over once from the old settings file (which is left in place), and the helper's memory folder
  moves from `hermesvoice-agent` to `rimworldllm-agent` on its first start (if both exist, both are kept).
- Saves made with a HermesVoice test build do not load the old mod's colony data.

### One-click local setup
- The mod now sets up its own local model: a letter on first start, then one **Install** click. It checks your graphics
  card, memory and disk, picks a model that fits, and downloads it with llama.cpp. No API key, no account, free.
- **Fast first run:** colonists start talking on a small starter model within minutes while the full model downloads;
  the mod switches over by itself and deletes the starter.
- Model ladder: Gemma 4 26B RP for 16 GB cards (12 GB with part of it in system memory), Gemma 4 E4B RP for 6-12 GB
  cards, laptops and Apple Silicon, and stock Gemma 4 E2B as a fallback with an in-game warning.
- The engine sizes itself to your free video memory, leaves room for the game, and starts and stops with the game.
- **My own endpoint** still works for anyone with their own OpenAI-compatible server.
- **Use my own GGUF file:** point the mod at a GGUF you already have (in-game file browser, a fit check for your card);
  it is served in place, never copied or deleted, and only llama.cpp is downloaded.
- Settings reorganised into Model, Colonists, Display, Content, Storage and Advanced, with a tooltip on every option.
  Changing model keeps the current one talking until the new one is ready, and shows the disk space needed first.
  The Storage tab shows what the engine keeps on disk and can remove it all in one click, including (ticked by default)
  the helper's memory of your colonists and its logs.
- The settings and setup windows show which graphics card the game and the model use, and how much room the model has
  (for example "Game on Intel Iris Xe · RTX 3050: 3.9 GB free for the model").
- Replaces the old external agent setup over the network; the agent now runs on your machine, started by the engine.

### Colonists with more inner life
- Inner thoughts (contrast clouds showing what a pawn wants or fears beneath the line), musings and worries.
- Grudges that lead pawns to avoid or seek each other out, gossip that spreads, colony nicknames for places and events.
- Dreams, habits, first-time reactions (first snow, first death, a beautiful room), bystanders who react.
- Gatherings: meals, parties, funeral eulogies, wedding vows, speeches.
- A "previously on" recap when you load a save.
- Wants with follow-through: a pawn may act on what it wants (seek someone out, avoid them).
- Each pawn's speech register follows the pawn: clever pawns use precise words, slow or drunk pawns and children use
  simpler ones.
- Pawns know their own story: backstory, traits, genes, ideoligion, appearance and notable tales.
- Written art descriptions for artworks, from the game's own tale text.

### Look and sharing
- Comic-style speech bubbles.
- **Share this moment**: copy a line, conversation, diary page or chronicle entry, or save it as a picture card
  (SaveData/HermesVoice/Shares), with an optional mod-name footer.

### Safety and content
- A hard age gate, enforced in code: nothing romantic or sexual for or about any pawn under 18, whatever the settings.
- Adult content is off by default and asks for confirmation when turned on; when off, intimate moments fade to black.
- Text from the game (names, backstories, tales) is treated as data, so instructions hidden in a name are ignored.
- A loop guard stops conversations from repeating themselves.

### Speed
- Stays responsive at 2x to 4x: moments that would be stale by the time a line is ready are skipped.

### License
- MIT, copyright (c) 2026 Charleszard. The engine (easyEngine) is MIT too; the downloaded models carry their own
  terms.

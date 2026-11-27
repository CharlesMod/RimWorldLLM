# Changelog

## 2.1.0 beta (first public release)

Tested on Linux with NVIDIA cards. Windows, macOS and AMD/Intel builds are included but untested.

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

# RimWorldLLM settings audit

Every setting the mod saves or shows, where it lives in the settings window, who it is for and what it does.
Audited 2026-11-26 on main cf0fd7f; the window was reorganised in the same round.

- Code: `Mod/Source/RimWorld/RimWorldLLMSettings.cs` (fields and `ExposeData`; each Scribe key is the field name),
  `Mod/Source/RimWorld/SettingsWindow.cs` (the window), `Window_EngineSetup.cs` (model picker),
  `Window_CustomGguf.cs` + `Window_FileBrowser.cs` (own GGUF file), text in `Mod/Languages/English/Keyed/RimWorldLLM.xml`.
- Who: **P** player, **PU** power user, **D** developer.
- Every control has a one-line tooltip.

## Tabs

| Tab | What it holds |
|---|---|
| Model (first) | On/off, the engine card (phase light, model, speed), Change model..., Use my own GGUF file..., Test, or My own endpoint |
| Colonists | How often they talk, how much lines nudge moods and opinions, quotes, wants, dreams, recap, art, which events get lines |
| Display | Speech bubbles, inner-thought clouds, director camera, share footer |
| Content | Adult content (with confirmation) and the always-on age gate line |
| Storage | Engine folder, size on disk, free space, Open folder, model files with Delete, Remove downloaded files... |
| Advanced | Bridge port, bind address, token, dial-out URL, push events, restart bridge, engine web page, debug window |

Before: Connection, Talk, Effects, Events, Bubbles, Advanced. The engine and model controls sat under Connection
next to bridge ports and tokens; they now have their own first tab, and the developer knobs moved to Advanced.

## Settings

### Model

| Setting (key) | Default | Who | What it does | Moved |
|---|---|---|---|---|
| `enabled` | true | P | Master switch: events, art, wants and dial-out stop when off | Connection -> Model |
| `llmSource` | "local" | P | Built-in engine, or own (An engine on another PC on my network / My own endpoint) | Connection -> Model |
| `llmOwnKind` | "" | - | Which kind of own source is picked: "pc" (An engine on another PC on my network) or "endpoint" (My own endpoint); "" = endpoint | **new** |
| `shareEngine` | false | P | Share this PC's engine on my network (local engine only); off = the engine serves this PC only | **new** |
| `llmBaseUrl`, `llmModel`, `llmApiKey` | "" | PU | Own OpenAI-compatible endpoint; used by the mind directly (the key stays in the mod settings) | Connection -> Model |
| `llmEngineId` | "" | - | Internal: the easyEngine (engine_id) the own endpoint was filled from by pairing; "" for a typed endpoint | **new** |
| `pairedEngines` | "" | - | Internal: JSON list of `{engine_id, token, client_id, name, address}`, one per paired engine; never logged | **new** |
| `engineAutoStart` | true | P | Start the engine with the game | Connection -> Model |
| `hideFallbackWarning` | false | P | Hide the small-model (E2B) warning | Connection -> Model |
| `customGgufPath` | "" | P | Last path typed or picked in Use my own GGUF file | **new** |
| `engineSetupDismissed` | false | - | Internal: the first-run letter was answered; no control | unchanged |

Model picker (Change model...): every catalog model with size, VRAM need, a fits / tight / won't-fit verdict for this
machine (from the engine's plan), Recommended, Installed / In use, the E2B fallback warning, and disk needed against free
space (the button is disabled with the reason when space is short). Switching downloads the new model in the background
while the current one keeps talking, then swaps; afterwards it offers to delete the old file.

Use my own GGUF file...: path field, Browse (in-game file browser, folders and `.gguf` files only), Check (the engine reads
the header: architecture, parameters, quant, trained context, size, fit verdict, estimated VRAM), Use this file, Stop using
it. The file is read in place; nothing copies, moves or deletes it. Without a native file dialog: Unity/Mono has no
reliable one on Windows, Linux and macOS, so the browser is in-game.

#### Sharing this PC's engine (host)

Off by default. With "Share this PC's engine on my network" off, the local engine listens on 127.0.0.1 only, nothing is
exposed and nobody is asked anything. The toggle (`shareEngine`) is shown with the local engine only; with another
source the status line reads "Sharing needs the local engine."

- **On:** the mod asks the running engine to listen on the private networks (LAN and Tailscale) with pairing on. It is
  live: no restart of the engine or the game. The toggle is the truth: an engine found shared while the toggle is off
  is put back to off.
- **The exposure line** under the toggle: "Any device on this network (a cafe's Wi-Fi too) can see this PC's engine name
  and model; only PCs you Allow can use it."
- **Status lines:** "Not shared: the engine serves this PC only." / "Changing sharing..." / "Shared as <name>." /
  "Sharing is on, but this PC has no private network up right now (no LAN or Tailscale address). It starts when one is." /
  "This engine can't share yet. Update RimWorldLLM on this PC." / "Couldn't change sharing: <reason>".
- **Other PCs find it at:** each address of this PC with a LAN or Tailscale badge. Click one to show its hello URL
  (`http://<address>:47480/engine/v1/hello`) as a QR code: scan it with a phone to check the engine answers (it returns a
  small JSON with the name and model). Other PCs find it by themselves; nothing needs typing there.
- **Allow / Deny:** when another PC asks to pair, a window "A PC wants to use this engine" opens in game: "<name> wants to
  use this PC's engine.", "Code: 493 201", "From <address>", "Allow only if the other PC shows the same code." Allow or
  Deny. The code must match the one the other PC shows big on its screen. If the other PC asks again, the window says
  "It asked again with a new code. Compare again." If it withdraws or the request is answered elsewhere: "The request
  ended (withdrawn on the other PC, or answered elsewhere)." The window does not pause the game and Esc does not answer it.
- **Waiting to pair:** every request still waiting, with Allow and Deny. After a Deny, the engine marks that address's
  later requests quiet for the rest of its life: no pop-up, but they still wait here, so a mistaken Deny can be undone.
- **Paired PCs:** the list under the toggle, "<name> (since <date>)", with **Remove**: it takes the PC's access away at
  once; that PC has to pair again.
- **Firewall:** the first time sharing is on, Windows asks whether easy-engine may use the network: allow it on private
  networks only. On a Mac, allow incoming connections when asked. (This is also in the toggle's tooltip.)
- **Provisional:** the engine calls behind this (live share switch, share addresses in status, the paired clients list and
  its removal) are the engine's share slice; an engine without it shows "This engine can't share yet."

#### Finding and pairing an engine (another PC, or My own endpoint)

For an easyEngine on another computer (the PC with the card): no URL or key to type. Pick the LLM source
**An engine on another PC on my network** (`llmSource` = own, `llmOwnKind` = pc; the other PC turns on sharing, see above).
"My own endpoint" keeps the URL, model and key fields and the Pair with this engine button below. In either:

1. **Find engines on my network** merges two searches, once per press (not continuously): mDNS for
   `_easy-engine._tcp.local.` for about 2 s (IPv4 first; when nothing answers, the shipped `easy-engine find --json`), and
   Tailscale: the game runs `tailscale status --json` (on PATH; `/Applications/Tailscale.app/Contents/MacOS/Tailscale` on
   macOS; `%ProgramFiles%\Tailscale\tailscale.exe` on Windows), takes the online peers and asks each one's
   `GET /engine/v1/hello` on port 47480 with a timeout of about 1 s, 4 at a time (IPv6 tailnet addresses too). No
   Tailscale installed or not running: that search is skipped quietly. Each engine is listed with its name, model, phase,
   address, a **LAN** or **Tailscale** badge, and `[paired]` when this game already holds a token for its engine_id. **Or its address** takes a typed
   `host[:port]` (default port 47480) and asks that engine's hello; it is the last fallback when nothing is found.
2. **Connect** calls hello. An engine that needs no token (a loopback engine in local mode, or `--open`) is used at once.
   Otherwise the game asks to pair as "RimWorld on <computer name>" and shows the six-digit code big ("493 201"):
   compare it with the code on the engine's screen (setup page, tray, or `easy-engine pair`) and press Allow there.
   Cancel withdraws the request. Denied on the engine ends it; a request the engine ended (it restarted, or a newer
   request from this computer replaced it) offers Try again once. A dropped connection is retried by itself. Hello is
   always asked first without a token (anonymous); a stored token is sent only to the engine_id that issued it.
3. **Paired:** the token is stored per engine_id in `pairedEngines` and the endpoint fields are filled: `llmBaseUrl` =
   the engine's address + `/v1`, `llmModel` = the `model_id` from hello (the selected model's catalog id; easyEngine
   871f821 and newer, `""` when none is selected or the model is a custom file, absent on older engines) or, when that
   is empty, the first model id the engine lists on `/v1/models` (its catalog id, which the agent's model rules need;
   left as it was when the engine is still loading). The same rule applies at game start and when adopting a key, `llmApiKey` = the token, and
   `llmEngineId` = the engine. The token is sent as `Authorization: Bearer` from then on; it never appears in a log.
4. **Staying connected:** at game start the paired engine is called at its last address with the token; if it moved, it
   is found again by engine_id over mDNS and the URL follows. A revoked or unknown token (also seen from a 401 with
   `token_revoked` / `token_invalid` on the Test button or the engine client) is dropped; when the host pressed Remove,
   the status line says "<name> removed this PC. Pair again to use its engine." Otherwise the status line says
   **Pair again** with a button (and a message once, in a running game). If the host is off or gone, the status line says
   so ("Nothing answered at <address>.") without freezing the game; it reconnects by itself after either side restarts. Typing another URL by hand lets go of the
   paired engine (its token stays stored for later).

5. **Pair with this engine** (shown under the status line when `llmBaseUrl` is set and is not already a paired engine,
   i.e. no `llmEngineId` or no stored token for it): for an endpoint already typed in, such as `http://sprinter:47480/v1`
   over a tailnet where mDNS does not reach. The engine root is the URL without a trailing `/v1` and slashes (no port
   means 47480); hello is called there. Not an easyEngine (hello fails or `engine` is not `easy-engine`): the line
   "This endpoint isn't an easyEngine; keep the key typed by hand" shows and nothing changes. An easyEngine whose hello
   accepts the current `llmApiKey` as a Bearer (`paired:true`, or no token needed) keeps that key: it is stored in
   `pairedEngines` under the engine_id and `llmEngineId` is set, with no pairing round. A master token may come back
   `paired:false` on hello, so an authenticated `GET /engine/v1/status` answering 200 counts as proof the key works.
   Otherwise the normal Connect flow runs (big code, Allow on the engine). The status line then reads "Paired with
   <name> at host:port". The key is never logged or shown. When hello carries a non-empty `model_id`, adopting also sets
   `llmModel` from it; otherwise the typed model stays.

The typed path is unchanged: any OpenAI-compatible server with a URL, model and key works as before; pairing only fills
those fields.

### Colonists

| Setting | Default | Who | What it does | Moved |
|---|---|---|---|---|
| `talkFrequency` | 1.0 | P | How often colonists talk (0-2); agent idle talk, director weight, group rolls | Talk -> Colonists |
| `pauseAmbientAtSpeed` | 4 | PU | Idle chatter pauses at this game speed and above (0-4) | Talk -> Colonists |
| `effects` | "normal" | P | How much lines nudge moods and opinions: off / light / normal (off also stops grudges) | Effects -> Colonists |
| `showQuotes` | true | P | Show lines in the log and on interactions | Effects -> Colonists |
| `voiceVanillaSocial` | true | P | Give voice to vanilla social interactions | Effects -> Colonists |
| `wantsEnabled`, `wantsAvoidHours` | true, 1.0 | P | Colonists' small wishes; how long a topic is avoided | Talk -> Colonists |
| `dreamsEnabled` | true | P | Dreams (also needs the Sleep event) | Talk -> Colonists |
| `recapOnLoad` | true | P | "Previously on..." recap when a save loads | Talk -> Colonists |
| `artDescriptions` | true | P | Art descriptions (also needs the Art event) | Talk -> Colonists |
| `textGen` | true | P | "Written flavour text" group: write text for single things (artworks, graves, books, log lines); also `GET /config` `text_fill` | new (texthook) |
| `textArt`, `textGraves`, `textBooks`, `textSocial` | true | P | Per kind: art titles and descriptions, grave epitaphs, book titles and blurbs, social log lines | new (texthook) |
| `textCombat` | false | P | Combat log lines (shots, misses, dodges, deflections; hits keep the game's text). Off by default: fights make many lines | new (texthook) |
| `evInjury` ... `evGatherings` (17) | true | P | Which event kinds get lines; each now has a tooltip | Events -> Colonists |
| `narratorMayPauseForEmergencies` | true | P | "Narrator plays" group: the narrator may pause the game for an emergency (and later unpause its own pause) | new (vivarium) |
| `narratorControlsSpeed` | false | P | "Narrator plays" group: the narrator may pause, unpause and change the speed at will; off = the player's speed is respected | new (vivarium) |
| `narratorSlowsForCombat` | true | P | "Narrator plays" group: when a fight starts the narrator sets normal speed and puts the player's speed back after (unless the player changed it); works without "controls the speed" | new (vivarium 3) |
| `narratorMayImplantGenes` | false | P | "Narrator plays" group, shown only with Biotech: the narrator may set a grown colonist to receive a xenogerm (always with a plan in the greentext); never children or prisoners. Off: it may still assemble xenogerms | new (vivarium biotech) |
| `narratorMayExecutePrisoners` | false | P | "Narrator plays" group: "The narrator may execute prisoners". Off: the prisoner tool has no execute mode and the route refuses it. On: it may set a prisoner to be executed, always with a reason in the greentext. `GET /config` `vivarium_may_execute_prisoners` | new (narrator tools) |
| `narratorMaySlaughterPets` | false | P | "Narrator plays" group: "The narrator may slaughter named pets". Off: it may slaughter only unnamed, unbonded colony animals. On: also animals with a name or a bond. `GET /config` `vivarium_may_slaughter_pets` | new (narrator tools) |
| `narratorMayLook` | true | P | "Narrator plays" group: "Narrator may look at the screen": the narrator may take a picture of the map (never the interface) when planning the base. Default on, used only when the engine supports images. Also `GET /config` `vision_allowed`; off makes `GET /vision` refuse with `vision_off` | new (vision) |
| `stewardHeartbeatHours` | 6 | P | "Narrator plays" group: how often the playing narrator looks over the colony on its own, in game hours (1-24) | new (vivarium) |
| `narratorAutosave` | true | P | "Narrator plays" group: while Narrator plays and the game's own autosave is off, the mod keeps rotating autosaves `RWL-Narrator-1..K` | new (unattended) |
| `narratorAutosaveDays` | 1 | P | Rotating autosave: every N in-game days (0.25-7, in quarter days) | new (unattended) |
| `narratorAutosaveKeep` | 5 | P | Rotating autosave: how many to keep (1-20); the oldest is overwritten, saves the mod did not make are never touched | new (unattended) |
| `narratorRestartMode` | 0 (ask) | P | "When the colony is lost": ask (dialog: new colony on this world / new world / stop), auto (same world if possible, else new world, after a delay), off | new (unattended) |
| `narratorCommentary` | true | P | "Narrator plays" group: the narrator adds a short documentary line at big moments (raids, deaths, births, weddings, masterworks, ship parts) and a recap line each game day; mirrored as `settings.commentary` in GET /vivarium (the mind reads it with default true) | new (unattended: voice) |
| `chatPanelShow` | true | U | "Narrator plays" group: the chat panel is always on screen | new (chat panel) |
| `chatPanelRight` | false | U | Default placement on the right (left of the date/alerts column) instead of the left; used until the panel is dragged | new (chat panel) |
| `chatPanelMedium` | false | U | Medium font instead of small | new (chat panel) |
| `chatPanelOpacity` | 0.3 | U | Background opacity when the mouse is away (0-0.8) | new (chat panel) |
| `chatPanelX`, `chatPanelY`, `chatPanelW`, `chatPanelH` | -1 | U | The panel's box as fractions of the screen, set by dragging its strip/grip and its corner; negative = default placement. "Reset chat panel position" sets them back to -1 | new (chat panel round 2) |
| `chatShowTalk`, `chatShowThoughts`, `chatShowNarrator`, `chatShowActions`, `chatShowStory` | true | U | The panel's five filters (also the chips on its hover strip): Talk = player + colonist lines; Thoughts = thoughts, dreams, diary; Narrator = narrator + notes; Actions = greentext acts + refusals; Story = chronicle, recap, art. They hide lines on the panel only | new (chat panel round 2) |
| `chatPanelWidth`, `chatPanelLines` | 420, 10 | - | Old width/lines sliders; still loaded from old saves, no longer shown or read (dragging replaced them) | removed from UI (round 2) |
| `narratorAnswerDialogs` | true | P | "Narrator plays" group: answer popups when the narrator plays. A choice window or letter the narrator leaves open past the timeout gets the safe default (docs/VIVARIUM.md "Unattended play: dialogs"). Off: popups wait for you | new (unattended dialogs) |
| `narratorDialogTimeoutS` | 90 | P | Real seconds (not game time: popups pause the game) before a popup gets the safe default; 10..300 in steps of 5. Reaches the agent as `settings.dialog_timeout_s` / `answer_dialogs` on `GET /vivarium` | new (unattended dialogs) |
| `narratorTrade` | true | P | "Narrator plays" group: the narrator may buy and sell with visiting caravans, orbital traders (powered comms console) and settlements its caravan has reached, within the caps below; mirrored as `settings.trade` | new (vivarium 4) |
| `narratorCaravans` | true | P | The narrator may form, send and cancel caravans (trade, visits, quest sites, deliveries); mirrored as `settings.caravans` | new (vivarium 4) |
| `narratorTradeMaxDeal` | 2000 | P | Largest single deal in silver of value (buys + sells), 100..20000 in steps of 100; a bigger deal is refused whole (`trade_max_deal`) | new (vivarium 4) |
| `narratorTradeMaxDay` | 6000 | P | Silver of value the narrator may trade per game day, 100..50000 (`trade_max_day`) | new (vivarium 4) |
| `narratorTradeDealsPerDay` | 3 | P | Deals per game day, 1..10 (`trade_deals_per_day`); the day's counters are saved with the game | new (vivarium 4) |
| `narratorKeepMedicine` | 10 | P | Medicine (all kinds together) the narrator never sells below, 0..50 (`keep_medicine`) | new (vivarium 4) |
| `narratorKeepFoodDays` | 10 | P | Days of food (from the colony's daily nutrition) the narrator never sells below, 0..30 (`keep_food_days`) | new (vivarium 4) |
| `narratorNeverSell` | "" | P | Comma-separated item words or defNames the narrator never sells (matched in the label or def, any case); `never_sell` as a list. Worn/equipped gear and forbidden items are never sold anyway | new (vivarium 4) |
| `narratorCaravanMaxPawns` | 3 | P | Most colonists on one narrator caravan unless the player asks, 1..6 (`caravan_max_pawns`). Rules that always hold: someone stays home, a fighter stays home, no children under 13, no downed/drafted/sick/broken pawns; the best doctor and the only cook stay unless the player asks | new (vivarium 4) |
| (button) | | | "Narrator plays" group: "Undo the narrator's last build" takes back its last placing action (as `/undo` in the chat); greyed when there is nothing to undo or no game | new (vivarium phase 2) |

The "Narrator plays" switch itself is not a mod setting: it is saved per game (GameComponent, `rwlVivarium`) and
sits in the bottom-right play-settings row, with a key `RWL_ToggleNarratorPlays` (unbound by default) under
Options > Controls. The trade and caravan rows show their sliders and the never-sell field only while trading or caravans is on. The three settings above reach the agent on `GET /vivarium` (`settings`) and `GET /config`
(`vivarium_may_pause`, `vivarium_controls_speed`, `vivarium_heartbeat_hours`). See docs/VIVARIUM.md.

### Display

| Setting | Default | Who | What it does |
|---|---|---|---|
| `showBubbles`, `colorBubbles`, `bubbleStyle`, `bubbleOpacity`, `comicLettering`, `bubbleDurationSec` | true, true, "comic", 1.0, false, 8 | P | Speech bubbles |
| `showThoughts`, `thoughtOpacity`, `thoughtPerPawnSec`, `thoughtGlobalSec` | true, 0.6, 120, 20 | P | Inner-thought clouds (comic style) |
| `directorCameraAllowed`, `directorCameraOnAtStart`, `directorPanSpeed` | true, false, 2.5 | P | Director camera |
| `watchMode`, `watchCaptions`, `watchPlayerPauseSec` | false, true, 20 | P | Watch mode (camera director for unattended play); `watchMode` is a mod setting, not per game |
| `watchZoom` | true | P | Watch mode zooms per shot (close for meals, deathbeds, art; mid for work; wide for raids, battles, ship parts) inside the player's zoom limits; the player's zoom comes back when watch mode turns off |
| `watchSelectFollowed` | false | P | Select the pawn a follow shot is on (opens the inspect pane, hence off); never drafts or changes anything else |
| `watchPace` | 1 | P | 0 calm, 1 normal, 2 lively: shot holds (follow 20/15/10 s, scenes 15/11/8 s, events x1.25/1/0.8) and the no-repeat window (5/4/2.5 min) |
| `watchSceneMeals`, `watchSceneRecreation`, `watchSceneWork`, `watchSceneNight`, `watchSceneMedical`, `watchSceneChildren`, `watchSceneRomance` | all true | P | Everyday scene groups for quiet stretches (work also covers prisoner talks and taming) |
| `shareFooter` | true | P | Footer on shared images (was under Talk) |

### Content

| Setting | Default | Who | What it does |
|---|---|---|---|
| `adultContent` | false | P | Adult themes between adults; turning it on needs the confirmation dialog (unchanged) |

The age gate is not a setting: a plain line on the tab says it is always on (`Mind/Core/AgeGate.cs`, mod Wants/Quotes guards).

### Storage (new tab, no saved settings)

With a game loaded, the first line counts the written texts in this game per kind, stored and (+ waiting in the queue).
Engine data folder, size per part (models, engine backends, runtime, downloads, logs), free space, Open folder, the model
files with size and a Delete button (not for the one in use), and Remove downloaded files...: stops the engine, deletes the
models, backends, runtime and downloads after a confirmation naming them and the space freed. The confirmation window also has a box, ticked by default, to delete the helper's memory and logs (its own folder, with the
size shown); that folder is emptied only after the engine has stopped the helper. Mod settings, the engine's logs and
the player's own GGUF file stay; the next Set up starts from scratch.

### Advanced

| Setting | Default | Who | What it does | Moved |
|---|---|---|---|---|
| `bridgePort` | 9765 | D | Bridge HTTP port (8765 migrates to 9765 on load) | Connection -> Advanced |
| `bindAddress` | "127.0.0.1" | D | Bridge bind address; warning when open without a token (now Keyed) | Connection -> Advanced |
| `sharedToken` | "" | D | Fixed bridge token (else generated); reveal / regenerate | Connection + Advanced -> Advanced |
| `agentUrl` | "" | D | Dial-out target for colony events | Events -> Advanced |
| `sendColonyEvents` | true | D | Master switch for dial-out pushes | Events -> Advanced |
| debug window | - | D | Dev mode only | unchanged |

### Saved but not in the window (unchanged, power user / developer via the config file)

`quoteMaxChars` 140, `attachRealFloorSec` 5, `attachWindowMinutes` 10, `interactionHigh`, `interactionIdle`
(def-name lists). All are read by the quote/attach code.

## Removed

- `language` ("en"): saved and sent in `/config`, but had no control and nothing in the mod or agent read it. Removed
  from the settings, `ExposeData` and `/config`. Old settings files that still have `<language>` load fine: Scribe
  ignores nodes nobody asks for.

## The mind's settings

The narrator and colonists' mind runs inside the mod (C#, background thread, talking to the game over the in-process door). It starts when a
game is loaded and the mod is enabled, and stops at the main menu or when the mod is disabled. Its status shows in the settings window
(Model tab and Advanced): "running", "waiting for the model", "waiting for a game", "off", or "error: ...".

Where a setting comes from, highest first:

1. The mod's own settings (the rows marked "mod" below). They are fixed by the mod and cannot be overridden from the file.
2. An optional file in the memory folder: `<data_dir>/config.toml`, else `<data_dir>/config.json`. Keys are the snake_case names below;
   nested tables `[llm]` and `[quotas]` use the short names (`llm.temperature` is `temperature` under `[llm]`). Unknown keys are ignored.
   The mod re-reads the file every few seconds; when it changes, the mind restarts with the new values. A broken file shows as an error
   in the status line and the mind waits (no crash) until it is fixed.
3. The defaults in the table.

The only environment variable is `RIMWORLDLLM_DATA_DIR` (moves the memory folder). One exception: the steward's strategy file can also be
chosen with `RIMWORLDLLM_STEWARD_STRATEGY` (it wins over `steward_strategy_path`).
The older `RIMWORLDLLM_LLM_*`, `_BRIDGE_*`, `_WINDOWS` and `_CONFIG` variables are not read by the mod; only the test harness (`tools/MindHarness`) honours them.

The LLM endpoint: in local mode the mind uses the engine's `/v1` and the selected model, with a 900 s request timeout (the engine holds a
request while a model loads; for a streamed answer the timeout is the idle time between pieces). In own mode (`llmSource` = own) it calls your
endpoint (`llmBaseUrl`, `llmModel`, `llmApiKey`) directly with a 120 s timeout, and the mod launches no engine. Put `timeout_s` under `[llm]`
in the file to change either.

| Key | Default | Set in | What |
|---|---|---|---|
| `llm.base_url` | http://127.0.0.1:8080 | mod (engine /v1, or your own endpoint) |  |
| `llm.api_key` | "" | mod (`llmApiKey`; empty for the engine) |  |
| `llm.model` | "" | mod (selected model, or `llmModel`) |  |
| `llm.mode` | openai | file |  |
| `llm.temperature` | null | file |  |
| `llm.top_k` | null | file |  |
| `llm.top_p` | null | file |  |
| `llm.min_p` | null | file |  |
| `llm.repeat_penalty` | null | file |  |
| `llm.engine` | null | file |  |
| `llm.line_max_tokens` | 96 | file |  |
| `llm.line_dry` | false | file |  |
| `llm.dry_multiplier` | 0.8 | file |  |
| `llm.dry_base` | 1.75 | file |  |
| `llm.dry_allowed_length` | 2 | file |  |
| `llm.dry_penalty_last_n` | 512 | file |  |
| `llm.timeout_s` | 900 (local), 120 (own) | file; mod default 900 local / 120 own |  |
| `quotas.actor_per_day` | 12 | file | lines per actor per game day |
| `quotas.pair_per_day` | 4 | file | conversations/lines per ordered pair per game day |
| `quotas.units_per_day` | 60000 | file |  |
| `quotas.min_gap_game_ambient` | 1500 | file | per actor, ambient class |
| `quotas.min_gap_real_s_ambient` | 20.0 | file |  |
| `quotas.ambient_share` | 0.75 | file | ambient chatter may use this share of an actor's daily lines |
| `data_dir` | null | mod (the memory folder, `RIMWORLDLLM_DATA_DIR`) |  |
| `narrator_slot` | 0 | file |  |
| `windows` | 2 | file | slots 1..windows |
| `config_refresh_real_s` | 10.0 | file | re-read the bridge /config (mod settings) this often, real seconds |
| `pause_ambient_at_speed` | 4 | mod (`pauseAmbientAtSpeed`) | ambient and social talk stop at this speed (4 = dev speed); the bridge's /config overrides |
| `poll_interval_s` | 1.0 | file | at 1x; shortened as the game speeds up (director.poll_delay) |
| `voice_lines_per_min` | 5.0 | file | the ceiling on spoken lines per real minute at every speed (urgent bypasses the gate) |
| `voice_burst` | 5.0 | file |  |
| `voice_fast_scale` | 0.5 | file | the ceiling is scaled by this at dev speed (4) |
| `turn_gap_ticks` | 150 | file | least game time between a conversation's turns (bubbles need reading time) |
| `turn_gap_floor_real_s` | 0.8 | file | ... and the least real time |
| `fast_summary_rate` | 360.0 | file | from this game rate (3x) a finished conversation is remembered from a template |
| `vanilla_attach_floor_s` | 5.0 | file | a line for the game's own interaction keeps this many real seconds to attach |
| `idle_starving_fraction` | 0.25 | file | idle work due for this share of a game day runs even beside ambient chatter |
| `event_min_severity` | 0.15 | file | below this never reaches the model |
| `ambient_min_severity` | 0.25 | file |  |
| `narrator_min_gap_real_s` | 120.0 | file |  |
| `reflection_every_days` | 3.0 | file |  |
| `reflection_importance` | 3.0 | file |  |
| `memory_floor` | 0.03 | file | unpinned memories decayed below this weight are dropped |
| `memory_merge_similarity` | 0.5 | file |  |
| `reflection_max_tokens` | 500 | file |  |
| `diary_enabled` | true | file |  |
| `diary_day_fraction` | 0.6 | file | write a day's entry once this much of the day has passed |
| `diary_max_tokens` | 160 | file |  |
| `wants` | true | mod (`wantsEnabled`) | wants with follow-through (Wants.cs); also needs the mod's wants_enabled |
| `chronicle_enabled` | true | file |  |
| `steward` | true | file | the vivarium steward (Steward.cs); it stays off against a mod without /vivarium |
| `steward_min_gap_real_s` | 20.0 | file | least real seconds between two steward cycles that are not chat (chat ignores it) |
| `steward_combat_gap_game_s` | 5.0 | file | a fight: least game seconds between two combat turns |
| `steward_combat_gap_real_s` | 4.0 | file | a fight: least real seconds after the last turn ended |
| `steward_strategy_path` | "" | file | a strategy playbook file for the steward prompt; env RIMWORLDLLM_STEWARD_STRATEGY wins; "" = the shipped one |
| `steward_strategy_max_bytes` | 12288 | file |  |
| `art_enabled` | true | mod (`artDescriptions`) | LLM art descriptions (Art.cs); also needs the mod's art_descriptions |
| `art_max_tokens` | 110 | file |  |
| `text_fill` | true | mod (`textGen`) | per-instance texts (the game's text queue; adapter.idle_fill); also needs the mod's text_fill not False |
| `chronicle_bridge` | true | file | also POST /chronicle (contract: the mod saves the chronicle with the game) |
| `chronicle_max_tokens` | 220 | file |  |
| `recap_enabled` | true | mod (`recapOnLoad`) | "Previously on..." recap after a save load (Recap.cs; also the mod's recap setting) |
| `narrator_context_tokens` | 6000 | file |  |
| `narrator_min_gap_game` | 6000 | file | coverage lines: min gap on both clocks; major events bypass |
| `shout_gap_real_s` | 45.0 | file | one speaker shouts at most this often (a calm scene cannot burst every line) |
| `thought_enabled` | true | file | short map thoughts (POST /thought kind "thought" + surface), idle work only |
| `thought_every_real_s` | 420.0 | file | per actor, at most one thought each this many real seconds |
| `thought_memory_window_days` | 1.0 | file | a thought draws on public memories this recent |
| `thought_max_chars` | 90 | file |  |
| `idle_retry_real_s` | 60.0 | file | backoff after a failed idle job |
| `use_llm_voice_cards` | false | file | optional pass for the 3 example lines |
| `repeat_guard` | true | file | a conversation turn that repeats an earlier line is refused (Loopguard.cs) |
| `repeat_retries` | 1 | file | retries with the reason in the prompt, then the talk ends with no line |
| `dreams` | true | mod (`dreamsEnabled`) | sleepers get one surreal dream cloud a night (Dreams.cs; also the mod's dreams setting) |
| `dream_chance` | 0.5 | file | per actor per night: chance the night brings a dream |
| `dreams_per_night` | 1 | file |  |
| `dream_max_tokens` | 30 | file |  |
| `dream_max_age_game` | 2500 | file | a dream not ready this long (game units, 1 game hour) after falling asleep is dropped |
| `inner_monologue` | true | file | master: aim in the intent pass, inner stream in the Mind, contrast clouds (Inner.cs) |
| `inner_stream_keep` | 6 | file | inner thoughts kept per Mind |
| `inner_solo_aim` | true | file | a line with no listener still gets an aim-only call |
| `small_model_patterns` | { e2b, e4b } | file | model ids containing any of these count as small (ModelTier.cs) |
| `inner_cloud_small` | true | file | contrast clouds on a small model (False = off there; the aim is still made and stored) |
| `art_small` | true | file | art descriptions on a small model (E4B, starter, E2B fallback): on (keeper 2026-11-26; E4B judged 2.9-3.0 after the creative round) |
| `inner_cloud` | true | file | sometimes show the aim as a thought cloud after a line |
| `inner_cloud_chance` | 0.35 | file |  |
| `inner_cloud_gap_real_s` | 90.0 | file | per actor, on the play clock |
| `inner_aim_fresh_days` | 0.25 | file | an aim guides the next line for this many game days |
| `gossip_chain` | true | file | retold gossip carries a source chain, decays per hop, merges duplicates; pass-on hint (Gossip.cs) |
| `gossip_hop_decay` | 0.8 | file | confidence factor per retelling beyond the first |
| `reputation` | true | file | "people say: ..." per actor in block C, rebuilt by rules (Gossip.cs) |
| `reputation_show` | 3 | file | at most this many relation lines carry a reputation |
| `lore` | true | file | shared lore: shared names coined after a major chronicle entry (Lore.cs) |
| `lore_max` | 12 | file | lore entries kept; the oldest fade |
| `habits` | true | file | reflection may evolve a voice-card tic (Habits.cs) |
| `habit_every_days` | 3.0 | file | at most one habit change per this many game days, per actor |
| `firsts` | true | file | first-time reactions (Firsts.cs; also the mod's first_cue event toggle) |
| `firsts_per_day` | 2 | file | first-time lines per actor per game day |
| `bystanders` | true | file | a bystander's reaction cloud to an argument (Bystanders.cs) |
| `bystander_gap_real_s` | 120.0 | file | per actor, on the play clock |
| `concerns` | true | file | ongoing worries per actor (Concerns.cs): born from events and reflection |
| `concerns_max` | 2 | file | open concerns per actor at most |
| `concern_fade_days` | 5.0 | file | an open concern nobody resolved fades after this many game days |
| `concern_keep_days` | 1.0 | file | a resolved or faded concern stays on record this long for the diary |
| `concern_loved_opinion` | 30 | file | opinion of someone at or above this makes their trouble a worry |
| `concern_payoff` | true | file | one spoken line when a worry is resolved |
| `grudges` | true | file | grudge and make-up loop (Grudges.cs); needs the mod's wants_enabled, effects on |
| `grudge_avoid_days` | 0.25 | file | the avoid phase, in game days (the mod's avoid table has its own hours setting) |
| `grudge_cooldown_days` | 0.75 | file | from the offence to the first seek-out |
| `grudge_retry_days` | 0.25 | file | between seek-out attempts the mod refused or that came to nothing |
| `grudge_max_attempts` | 3 | file | then the grudge fades |
| `grudge_avoid_floor_real_s` | 120.0 | file | real-time floors (play clock, stops while paused): at 3x and 4x a |
| `grudge_cooldown_floor_real_s` | 300.0 | file | grudge would otherwise cycle in a few real minutes. Each phase waits |
| `grudge_retry_floor_real_s` | 120.0 | file | for both its game-time length and this much play time |
| `grudge_recover_opinion` | 10 | file | opinion of the other at or above this counts as recovered (make up, not confront) |
| `gatherings` | true | file | group scenes and ceremonies (Group.cs; also the mod's gatherings event toggle) |
| `meal_talk` | true | file | meals (3+ eating in one room) may become a group scene |
| `group_max_speakers` | 4 | file | speakers in one group scene (two windows rotate between them) |
| `group_turns` | { 5, 4, 2, 0 } | file | meal/party turn cap at 1x, 2x, 3x, 4x (0 = no scene; ceremonies keep at least their lead) |
| `eulogy_max_tokens` | 70 | file | the lead's line at a funeral |
| `repeat_own_recent` | 5 | file | the speaker's own last lines (across conversations) kept for the check |
| `creative_ground` | false | file | richer data in art / recap / lore / eulogy and vow prompts (Art.cs, Recap.cs, Lore.cs, Group.cs) |
| `aim_ground` | true | file | (judge: aims +0.3..+0.6 on E4B) the aim request carries the scene's tension and one subtext kind (Inner.cs) |
| `creative_plan` | false | file | a one-line private plan before the piece (Creative.cs) |
| `creative_best_of` | 1 | file | candidates per piece (Creative.cs) |
| `creative_ground_for` | { lore, recap, eulogy } | file | judged better on E4B (art and vows were not); kinds ("art", "lore", "recap", "eulogy", "vow") that get creative_ground; the bool above means all kinds |
| `creative_plan_for` | { lore, eulogy } | file | kinds that get creative_plan (judged better on E4B) |
| `creative_best_of_for` | { recap } | file | kinds that get best-of creative_best_of_n candidates (recap: once per load) |
| `creative_best_of_n` | 4 | file | candidates for the kinds in creative_best_of_for |
| `creative_rerank` | auto | file | how the best of N is picked: "auto" (rules) or "llm" |
| `cliche_retry` | false | file | one retry when the piece holds a stock phrase |
| `aim_sampler` | "" | file | non-empty: the aim-carrying intent pass asks the engine for this sampler profile |

`bridge_json`, `bridge_url` and `bridge_token` are not used: the mind reaches the game through the in-process door and ignores them in the file.

## Engine-side state

The engine keeps its own settings in `<data>/state.json` (mode, external endpoint, linger, KV type, sampler policy,
selected model, the custom file `{path, name, size, mtime}`, the calibration book). The mod sets them through
`/engine/v1/settings`, `install`, `custom/use`; see docs/ENGINE.md.

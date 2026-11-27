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
| `llmSource` | "local" | P | Built-in engine or My own endpoint | Connection -> Model |
| `llmBaseUrl`, `llmModel`, `llmApiKey` | "" | PU | Own OpenAI-compatible endpoint; written to bridge.json for the agent (the key only there) | Connection -> Model |
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

#### Finding and pairing an engine (My own endpoint)

For an easyEngine on another computer (the PC with the card): no URL or key to type. In My own endpoint:

1. **Find engines on my network** browses mDNS for `_easy-engine._tcp.local.` for about 2 s (IPv4 first); when nothing
   answers, it runs the shipped `easy-engine find --json` instead. Each engine is listed with its name, model, phase and
   address, and `[paired]` when this game already holds a token for its engine_id. **Or its address** takes a typed
   `host[:port]` (default port 47480) and asks that engine's hello.
2. **Connect** calls hello. An engine that needs no token (a loopback engine in local mode, or `--open`) is used at once.
   Otherwise the game asks to pair as "RimWorld on <computer name>" and shows the six-digit code big ("493 201"):
   compare it with the code on the engine's screen (setup page, tray, or `easy-engine pair`) and press Allow there.
   Cancel withdraws the request. Denied on the engine ends it; a request the engine ended (it restarted, or a newer
   request from this computer replaced it) offers Try again once. A dropped connection is retried by itself.
3. **Paired:** the token is stored per engine_id in `pairedEngines` and the endpoint fields are filled: `llmBaseUrl` =
   the engine's address + `/v1`, `llmModel` = the model id the engine lists on `/v1/models` (its catalog id, which the
   agent's model rules need; left as it was when the engine is still loading), `llmApiKey` = the token, and
   `llmEngineId` = the engine. The token is sent as `Authorization: Bearer` from then on; it never appears in a log.
4. **Staying connected:** at game start the paired engine is called at its last address with the token; if it moved, it
   is found again by engine_id over mDNS and the URL follows. A revoked or unknown token (also seen from a 401 with
   `token_revoked` / `token_invalid` on the Test button or the engine client) is dropped, and the status line says
   **Pair again** with a button (and a message once, in a running game). Typing another URL by hand lets go of the
   paired engine (its token stays stored for later).

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
| `stewardHeartbeatHours` | 6 | P | "Narrator plays" group: how often the playing narrator looks over the colony on its own, in game hours (1-24) | new (vivarium) |
| `narratorAutosave` | true | P | "Narrator plays" group: while Narrator plays and the game's own autosave is off, the mod keeps rotating autosaves `RWL-Narrator-1..K` | new (unattended) |
| `narratorAutosaveDays` | 1 | P | Rotating autosave: every N in-game days (0.25-7, in quarter days) | new (unattended) |
| `narratorAutosaveKeep` | 5 | P | Rotating autosave: how many to keep (1-20); the oldest is overwritten, saves the mod did not make are never touched | new (unattended) |
| `narratorRestartMode` | 0 (ask) | P | "When the colony is lost": ask (dialog: new colony on this world / new world / stop), auto (same world if possible, else new world, after a delay), off | new (unattended) |
| `narratorCommentary` | true | P | "Narrator plays" group: the narrator adds a short documentary line at big moments (raids, deaths, births, weddings, masterworks, ship parts) and a recap line each game day; mirrored as `settings.commentary` in GET /vivarium (the agent reads it with default true) | new (unattended: voice) |
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

The age gate is not a setting: a plain line on the tab says it is always on (agent `core/agegate.py`, mod Wants/Quotes guards).

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

## Agent-side config

The agent polls the mod's `GET /config` every 10 s. Fields and their readers: `effects` (idle.py, grudges.py),
`events` (director.py, dreams.py, group.py, art.py), `talk_frequency` (idle.py, director.py, group.py),
`wants_enabled`, `recap` / `recap_open` (recap.py, director.py), `text_fill` (idle.py: the text filler), `adult_content` (adult.py; enforced in prompt.py and
sanitize.py), `dreams`, `art_descriptions`, `pause_ambient_at_speed` (director.py, lease.py).
The vivarium keys (`vivarium_*`) are sent for tools and logs; the steward reads the same values from
`GET /vivarium`. Sent but unread: `dlc` and the `llm` block (the agent takes its endpoint from bridge.json). Both kept: harmless,
and `llm` documents what the mod uses.

### Agent config for the steward (vivarium)

Agent config file / defaults (core/config.py):

| Key | Default | What |
|---|---|---|
| `steward` | true | The vivarium steward. It also stays off by itself against a mod without `/vivarium`. |
| `steward_min_gap_real_s` | 20 | Least real seconds between two steward cycles that are not chat (heartbeat, events). Chat ignores it. |
| `steward_combat_gap_game_s` | 5 | In a fight: least game seconds between two combat turns (60 ticks each). |
| `steward_combat_gap_real_s` | 4 | In a fight: least real seconds after the last combat turn ended. |
| `steward_strategy_path` | "" | A strategy playbook file for the steward prompt; "" = the shipped `games/rimworld/strategy/steward_strategy.md`. The env var `RIMWORLDLLM_STEWARD_STRATEGY` wins over it. A missing or empty chosen file leaves the section out. |
| `steward_strategy_max_bytes` | 12288 | The strategy section is cut on a line at this size. |

## Engine-side state

The engine keeps its own settings in `<data>/state.json` (mode, external endpoint, linger, KV type, sampler policy,
selected model, the custom file `{path, name, size, mtime}`, the calibration book). The mod sets them through
`/engine/v1/settings`, `install`, `custom/use`; see docs/ENGINE.md.

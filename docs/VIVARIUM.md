# Vivarium: the narrator plays the game

The keeper's words: "one more major feature: I want the ultimate rimworld vivarium. a checkbox on
the onscreen UI that when pressed, will allow the first slot "narrator" to drive the game. in every
way, it would take over for the user. running in whatever time scale we wanted by operating the UI
as normal. all the while, it does everything a normal player does. macro, micro, and we should be
able to type in the global chat with pressing the enter key to talk with it and strategize, ask it to
do things, do long term goals, short term micro, whatever."

This file holds the design (decided), the phases, and the phase 1 contract between the mod and the
agent. The wire details of the new routes are also in `docs/BRIDGE-API.md`; the new settings are in
`docs/SETTINGS.md`.

## Design (decided)

### 1. The switch
- A toggle in the bottom-right play-settings row (Harmony postfix on
  `PlaySettings.DoPlaySettingsGlobalControls(WidgetRow row, bool worldView)`), with an icon and the
  tooltip "Narrator plays: the narrator runs the colony; you can still act and talk to it".
- Saved per game (in the mod's GameComponent). A keybinding too (`RWL_ToggleNarratorPlays`, unbound by
  default).
- When on, the player is never locked out: anything the player does is just part of the world the
  narrator sees next.

### 2. The chat
- **The panel** (MOBA style): the chat log is always on screen at the side, translucent, with
  outlined text that reads over any ground. Left by default: just right of the resources list
  (x = 148 UI units) and above the inspect pane (bottom edge 208 above the screen's bottom); right:
  left of the date/alerts column. It never fades out; new lines briefly highlight. Under the mouse it
  turns nearly opaque and the wheel scrolls history; clicks pass through to the map everywhere except
  on the hover strip and the grips. It hides while a main tab window (architect, work, ...) is open,
  unless you are typing. Settings (Colonists → Narrator plays): show/hide, left/right default
  placement, small/medium font, background opacity, the five filters, "Reset chat panel position".
  Layout math: `Mod/Source/RimWorld/VivariumChatLayout.cs` (Verse-free, tested).
- **Move and resize:** under the mouse a thin title strip shows at the top of the box: its grip (or
  anywhere on the strip outside the chips) drags the panel anywhere on screen; the staircase grip in
  the bottom-right corner resizes width and height (the visible lines follow the height). The box is
  saved as fractions of the screen (`chatPanelX/Y/W/H`), so UI scale and resolution changes keep it in
  place; it is always clamped fully on screen, at least 220 wide and two lines tall. The input bar
  follows the box (below it, or above it at the bottom edge). Until the panel is first dragged it
  keeps the default placement; Reset returns to it.
- **Everything in the log:** besides the player's and the narrator's lines, every generated line goes
  into the chat log: colonist speech from `/say` ("Ana: …", "Ana → Bram: …" with a listener), thoughts
  ("Ana thinks: …", dim italic), dreams ("Ana dreams: …"), diary entries ("Ana's diary: …", shortened),
  chronicle entries ("Chronicle, Day 5: …"), recap letters ("Recap: …"), art descriptions
  (`Art: "Title" by Ana: …`), and the narrator's actions (greentext, below). Kinds (`who`): player,
  narrator, note, colonist, thought, dream, diary, chronicle, recap, art, action, refused
  (`Mod/Source/Bridge/ChatKinds.cs`). A reply to the player's `@Name` line is the same `/say` line: one copy.
- **Filters:** five groups, each a chip on the hover strip and a checkbox in settings (all on by
  default, saved): Talk (player, colonist), Thoughts (thought, dream, diary), Narrator (narrator, note),
  Actions (action, refused), Story (chronicle, recap, art). Filters hide lines on the panel only; the
  log keeps them.
- **Greentext actions:** every act the narrator takes is its own line in greentext green:
  `> set Ana's Cooking to 1st, since meals are running low`. The "what" is the mod's own plain-English
  account of what the hand did (never the model's claim); the ", since …" part is the `why` the model
  passed with the tool call (≤ 80 chars). A refused act shows dim: `> couldn't change the game speed:
  <reason>`.
- **Enter** (when no text field has focus and no window wants it) opens the one-line chat bar at the
  panel's bottom edge, same width; Enter sends, Esc closes. Lines: "You: …" (blue), "You → Ana: …"
  (teal), "Narrator: …" (gold), "Ana: …" (green), notes (grey), actions "> …" (green #8FB43A),
  refused actions "> couldn't …" (grey).
- **Live lines:** "Narrator is thinking…" (animated dots, or "… 74%" and a thin bar when the client
  reports prefill progress) while the prompt is processed, then the narrator's text streaming in,
  replaced by the final line. An `@Name` reply can show "Ana is thinking…" the same way. A status with
  no update for 90 s goes by itself. Routes `POST /chat/status` and `POST /chat/partial`, and the
  in-process API below; the contract is in BRIDGE-API.md. Today the Python steward posts `thinking`
  when its model call starts and `idle` when it ends (no token streaming); the C# mind streams.
- Messages go to the narrator. `@Name text` goes to a colonist through the existing talk path
  (TalkQueue → agent → `POST /say`), and the colonist's answer shows in the scrollback too.
- Chat history is saved with the game (the last 600 lines).
- **Narrator off (advisor mode):** the chat still works. The narrator answers as an advisor and,
  when the player's message asks for something concrete ("build a freezer", "draft everyone"), it
  **does just that one task** and says so. It does not take any other action, and it never wakes on
  events or the heartbeat while off. (Decision: "do the one task" rather than "ask to switch on";
  asking would add a round trip for the most common advisor request.)

### 3. The brain ("steward")
When playing, slot 0 is the steward's. The narrator's idle jobs (chronicle, art, lore, recap, and the
one-line narrator cover) yield: they run only while the steward is between cycles, and a cycle that
starts preempts a running one.

The steward loop runs on the agent side:
- **Wakes on:** a chat message (at once), important events (raid, a letter needing a choice, mental
  break, death, fire, starvation and medical alerts), finished long jobs (later phases), and a
  **heartbeat** every N in-game hours (default 6). The heartbeat is counted in game ticks, so a 3x or
  4x game wakes it more often in real time and it is never starved; a real-time floor between cycles
  (default 20 s) keeps it from flooding the model at ultrafast speed. While the game is paused nothing
  but chat wakes it.
- **Reads** a compact **colony digest** (`GET /digest`): date, season, weather, resources by category,
  food days, pawns (role, top skills, work priorities, health, mood, current job, drafted, schedule,
  policies), alerts, open letters with their choices, research state, threats on the map, rooms and
  power summary; and the **goals board** (`GET /goals`).
- **Goals board**, saved in the game: long-term and short-term goals, the current plan, standing orders
  from the player ("always keep 20 meals"), and a short log of what it did and why. The player reads
  and edits it in the Voices tab ("Goals" page).
- **Acts** through tools in a tool loop with a larger budget than the wants loop (8 steps, 120 s).
  One cycle = observe → decide → a few actions (each with a short `why`, shown as its greentext line)
  → a one-line chat note when worth saying, not a list of the actions. The cycle's final text is that
  line; "nothing to say" posts nothing.
- **Strategy knowledge** is a prompt section loaded from a file (see "Steward prompt" below), so a
  RimWorld playbook can be dropped in without code changes.

### 4. The hands (C#, game thread, through the pump)
Player verbs, done through the same code paths the UI uses (designators, Command gizmos, managers),
never dev-mode cheats. Each returns ok with a plain-English note the player reads as the greentext line
("set Ana's Cooking to 1st", "drafted Bram", "started researching Batteries"), or the reason the game
gave with what was attempted ("couldn't set Ana's Cooking: Ana is incapable of Cooking").
- **time:** pause/unpause, set speed. The player's speed is respected by default. The steward may
  pause (and later unpause its own pause) only for emergencies when "Narrator may pause for
  emergencies" is on (default on), and may change speed only when "Narrator controls speed" is on
  (default off).
- **work:** work priorities (switches the colony to manual priorities when needed), schedules,
  assignments (bed, outfit, drug and food policies).
- **research:** choose the current project.
- **letters and choices:** accept or decline quests, answer choice letters, dismiss letters.
- **drafting:** draft/undraft, move to a cell, attack a target, hold position.
- Later phases add designations, building, zones, bills, trade, caravans and animals.

### 5. Spatial help (phase 2+)
LLMs are weak at grids, so the model states intent and C# planners find cells: "a 5x7 bedroom next to
the main building", "stockpile for food in the freezer", "defend at the north choke". Planners are
deterministic and testable.

### 6. Micro (phase 3)
Real-time combat runs on a deterministic tactical controller in C# (cover, focus fire, retreat at low
health, rescue downed), with the steward setting stances and targets every few seconds. The LLM never
clicks per tick.

### 7. Honesty and safety
Every action is logged: the full action log (`GET /actions`, saved with the game, with each act's
`why`) plus one greentext chat line per act, refusals included. No
cheats, no dev mode, no reading hidden information the player couldn't see (the digest lists only
what the colony UI shows). The steward says when it can't do something.

## Phases
1. **Foundation:** the switch, the chat bar + scrollback + save, the steward loop with wake triggers
   and heartbeat, the colony digest route, the goals board (save + agent + a simple view), and hands
   for time, work priorities and schedules, policies/assignments, research, letters/choices,
   draft/undraft/move/attack/hold. The narrator's idle jobs yield while playing. Result: it runs the
   colony's work, research and choices, talks strategy, and drafts pawns.
2. **Building and land:** designations (mine, chop, harvest, hunt, tame, haul, deconstruct, cancel),
   blueprints via a room/structure planner, stockpile and grow zones, bills, power and temperature.
3. **Combat micro:** the tactical controller, stances, emergencies.
4. **Economy and travel:** trade, caravans, quests in depth, animals, prisoners.
5. **Long soak runs** (hours, 3x) and tuning.

## Phase 1 contract

### Pieces and where they live
| Piece | Code |
| --- | --- |
| Vivarium state (playing flag, settings mirror, chat log, goals board, action log), the routes, argument checking | `Mod/Source/Bridge/Vivarium*.cs` (Verse-free, tested in Mod.Tests) |
| The hands' game bodies, the digest builder | `Mod/Source/RimWorld/VivariumHands.cs`, `VivariumDigest.cs` (thin Verse code behind `IVivariumHands`) |
| Switch, chat bar, scrollback, Goals page, save, new events | `Mod/Source/RimWorld/Vivarium*.cs`, `MainTabWindow_RWLLog.cs`, `RimWorldLLMGameComponent.cs` |
| Steward loop, tools, prompt, digest renderer, slot-0 ownership | `agent/rimworldllm_agent/core/steward.py`, `core/steward_tools.py`, `games/rimworld/steward_*.txt`, `core/lease.py`, `core/idle.py`, `core/director.py` |

### Routes (summary; full shapes in BRIDGE-API.md)
| Route | Game? | What |
| --- | --- | --- |
| `GET /vivarium` | no | `playing`, `settings`, `chat_seq`, `goals_rev`, `actions_seq`, `paused_by_narrator` |
| `GET /digest` | yes | the colony digest |
| `GET /chat?since=N` | no | chat entries after seq N, `next_seq` |
| `POST /chat` | no | the steward posts a `narrator` line or a `note`; optional `stream_id` ends that live stream |
| `POST /chat/status` | no | live status: `who`, `to?`, `state` thinking/typing/idle, `stream_id`, `progress?` 0..1 |
| `POST /chat/partial` | no | the streaming text so far: `stream_id`, `text` (whole, not a delta) |
| `GET /goals` | no | the goals board with `rev` |
| `PUT /goals` | no | replace the board (needs the current `rev`; 409 `conflict` when stale) |
| `GET /actions?since=N` | no | the action log |
| `POST /act/time` | yes | `pause` and/or `speed`, `emergency` flag |
| `POST /act/work` | yes | `pawn`, `priorities` {work type: 0..4} |
| `POST /act/schedule` | yes | `pawn`, `from_hour`, `to_hour`, `assignment` |
| `POST /act/policy` | yes | `pawn`, `kind` outfit/food/drug, `name` |
| `POST /act/bed` | yes | `pawn`, `bed` (thing id from the digest) |
| `POST /act/research` | yes | `project` (defName or label) |
| `POST /act/letter` | yes | `letter` (id from the digest), `choice` (label, index, or "dismiss") |
| `POST /act/draft` | yes | `pawns` [ids], `drafted` |
| `POST /act/order` | yes | `pawn`, `order` move/attack/hold, `x`,`z` / `target` / `hold` |

Every `/act/*` write is a normal bridge write (op_id, optional expires_tick). Success:
`{"done": "<short note>", ...}`; a refusal is `409 refused` with `detail` = the reason the game or the
settings gave. Every `/act/*` takes an optional `why` (string, cleaned and cut to 80 chars; a non-string
is a 400). Every success is appended to the action log (with its `why`) and writes one `action` chat line
(`note` + ", since why"); a refusal writes one `refused` chat line; a 400 or no game writes nothing. Acts are allowed whether or not the narrator is playing
(advisor mode does one task).

### In-process chat streaming (for the C# mind)
The same live status without HTTP: `VivariumChat.Live` (Verse side), which is the Verse-free
`RWLBridge.Runtime.Vivarium.Live`, a `RimWorldLLM.Bridge.ChatLive`. Every method is thread-safe, so the
mind may call it from its worker thread.
```csharp
var live = VivariumChat.Live;                       // or runtime.Vivarium.Live
string id = live.Begin("narrator");                 // thinking; replaces the narrator's old stream
live.Progress(id, processed / (float)total);        // optional, from prompt_progress; 0..1
live.Typing(id);                                    // first token (Partial does this too)
live.Partial(id, textSoFar);                        // whole text so far; cheap, call per token
live.End(id, finalText);                            // final narrator line -> chat log on the next frame
// colonist: live.Begin("colonist", pawnId, "Ana"); End(id, text) logs a "colonist" line,
// or End(id) / Cancel(id) to drop the live line when the reply goes through the speech path.
```
`Begin` throws `ArgumentException` for a bad speaker. `Progress`/`Typing`/`Partial`/`End` return false
when the stream is gone (ended, replaced or timed out after 90 s without an update); keep calling
`Progress` during a prefill longer than a minute. `End(id, text)` queues the line, and the bridge's
per-frame update appends it on the game thread (the live line stays until then, so nothing blinks).
The HTTP routes call `Status(streamId, who, to, name, state, progress)` on the same object.

### New events (on the existing `/events` ring)
- `letter` — a letter arrived; `data` {letter, label, choices, quest}. Severity 0.6 for a choice
  letter, 0.3 otherwise.
- `alert` — a critical alert came up; `data` {alert, label}. Fire, starvation, colonist needs
  rescue/tending, low food, and other alerts the game marks critical.
The director ignores both for colonist speech; the steward wakes on them (alerts and choice letters
wake at once; plain letters only feed the next cycle).

### Steward prompt
The system prompt is built from files shipped in `games/rimworld/`:
1. `steward_system.txt` — role, rules (no cheats, the player's speed, honesty, advisor mode), how to use
   the tools and the goals board, and the chat-line style.
2. **Strategy section** — `steward_strategy.txt`, the compact playbook. It can be replaced without code
   changes: the agent reads `RIMWORLDLLM_STEWARD_STRATEGY` (or config `steward_strategy_path`) first,
   and falls back to the shipped file. An empty or missing file leaves the section out.
3. The tool protocol (rendered by the tool loop).
The user turn carries why it woke (chat text, events), the goals board, and the rendered digest.

### Phase 1 limits
(Phase 2 below adds designations, building, zones and bills.) No trade or caravans yet; draft orders are single commands
(no tactical controller). The digest is capped (about 6 KB rendered) and lists the top items only.

## Phase 1 status (what is built)

**Mod (C#).**
- `Mod/Source/Bridge/Vivarium.cs`, `VivariumState.cs`, `VivariumRoutes.cs`: the state (playing flag, settings
  mirror, chat log, goals board, action log, saved as one JSON string `rwlVivarium`), every route above, argument
  checks and the time gate. Covered by `Mod.Tests/VivariumTests.cs`.
- `Mod/Source/RimWorld/VivariumHands.cs`: the hands through vanilla calls (TickManager, workSettings, timetable,
  outfit/food/drug databases, CompAssignableToPawn, ResearchManager, LetterStack + DiaOption actions, drafter,
  Goto job, `FloatMenuUtility.GetAttackAction`). Pure helpers (`VivariumHandsPure.cs`, `VivariumChatParse.cs`,
  `VivariumFactsPure.cs`) are unit-tested; the game bodies are compile-checked only (no game here).
- `VivariumDigest.cs`, `VivariumFacts.cs`: `GET /digest` with the `facts` block (each section and each fact fails
  alone, logged once).
- `VivariumUi.cs`, `VivariumChat.cs`, `VivariumGoalsPage.cs`: the eye toggle in the play-settings row
  (`Mod/Textures/RimWorldLLM/UI/RWL_NarratorPlays.png`) and its key, the chat bar and scrollback, and the "Goals"
  tab of the Voices window (plan, goals, standing orders, the narrator's log, the last 30 actions).
- `VivariumEvents.cs`: the `letter` and `alert` events.
- Settings: the "Narrator plays" group in the Colonists tab (docs/SETTINGS.md).

**Agent (Python).**
- `core/steward.py`: the loop. One `GET /vivarium` per director step when idle. Wakes: chat (at once, playing or
  not, paused or not), urgent events (raid_start, mental_break, death, critical/fire/starvation/rescue alerts, a
  letter with choices; playing and unpaused), the heartbeat (`heartbeat_hours` of game time since the last cycle;
  playing and unpaused), and once after its own emergency pause. Non-chat cycles keep 20 real seconds apart. One
  cycle at a time. Against a mod without `/vivarium` it switches itself off.
- A cycle: `GET /digest` + `GET /goals` → system prompt (`steward_system.txt` + the strategy section + the tool
  protocol) and user turn (why it woke, playbook alerts, settings, the goals board, the rendered digest, ~6 KB cap)
  → the tool loop on slot 0 (8 steps, 120 s, 60 s a step) → at most 3 chat notes ("→ …", the bridge's own words),
  the final line as a `narrator` chat line ("done" posts nothing), one `PUT /goals` with the board edits and one
  log line (only if it acted). Advisor mode (not playing): one acting tool call at most.
- Slot 0: while a cycle runs `LeaseManager.narrator_owner = "steward"`; a running chronicle/art job is
  preempted, narrator cover and new lore wait, the load recap waits for the cycle to end.
- `games/rimworld/steward_tools.py` (the game tools: names → ids from the digest, wrong names come back to the
  model as errors), `steward_digest.py` (the renderer), `steward_rules.py` (the playbook rules), `steward_try.py`
  (one cycle against a recorded digest, for live checks: `python -m rimworldllm_agent.games.rimworld.steward_try
  --digest agent/tests/data/steward_digest_sample.json --chat "..." --playing --llm-url http://host:port/v1`).
- `letter`/`alert` events are silent for the director (no speech, memory or chronicle).

**The strategy playbook.** `games/rimworld/strategy/steward_strategy.md` is the compact playbook (written by the
strategy research task; full reference outside the repo) and `strategy/rules.json` holds 66 if-then rules over
the digest `facts`. The agent evaluates the rules each cycle with a whitelisted AST walker (no `eval`), and the
ones that fire (all priority-1, at most 8 in all, ~1.5 KB) go into the user turn as "PLAYBOOK ALERTS". Always-on
rules are left out (the playbook text covers them). A rule naming a research project the game does not define
(checked against the digest's `research_known`) stays off, with one warning per name. To swap the playbook,
point `RIMWORLDLLM_STEWARD_STRATEGY` (or `steward_strategy_path`) at another file.

**Not in phase 1:** designations, building, zones, bills, trade, caravans, animals, the tactical controller.
The steward is told to recommend those to the player instead of pretending.

**Live check (2026-11-27, poe P40, the keeper's 26B `gemma4-rp-maxxed-Q3_K_XL`, 3 slots × 32k, grammar
protocol, `steward_try` on `agent/tests/data/steward_digest_sample.json`).** System prompt ≈ 4.6k tokens
(steward rules + playbook 3.5k, tools ≈ 1.1k), rendered digest 0.8k tokens. A chat cycle ("we're low on food, keep
two cooks") took 14 s: a short-term goal, a plan, Eli's Cooking set to 1, one note and a line. A heartbeat took 11 s:
a bed for Eli, research Batteries, two notes, a line. In advisor mode a question about the quest got advice only
(after a prompt fix; before it, it rejected the quest), "put Bram on hunting first" did exactly that one task, and
"build a freezer" got "I can't build yet". Fixed from this check: advisor questions vs requests, priority churn,
doubled notes, and a grammar detail that sometimes made the final line a scrap (":").

## Phase 2: the builder's hands

Phase 2 gives the narrator designations, blueprints, a room planner, zones and areas, and bills, with a build
view in the digest so it can see what the colony lacks. The model states intent ("a medium bedroom", "a stove in
the kitchen", "a potato field"); C# planners pick the cells. Everything it places is tagged for undo.

### Pieces and where they live
- `Mod/Source/Bridge/VivariumBuild.cs` (Verse-free, tested): the argument records (`DesignateArgs`, `BuildArgs`,
  `RoomArgs`, `ZoneArgs`, `AreaArgs`, `BillArgs`, `Where`, `CellBox`), `NarratorMark` + `NarratorLedger` (undo, saved
  in the vivarium JSON as `ledger`), `BuildBudget` (per-cycle caps), `BuildPure` (room footprint, door side, sizes,
  site-candidate rings, the thing/stuff/plant word lists).
- `Mod/Source/Bridge/VivariumRoutesBuild.cs`: the routes below, shape checks, the cap gates, `UndoLast`.
- `Mod/Source/RimWorld/VivariumBuildHands.cs`: the game bodies (`IVivariumHands.Designate/Build/Room/Zone/Area/Bill/
  Benches/Undo`), the buildable-site finder.
- `Mod/Source/RimWorld/VivariumBuildView.cs`: the digest's `build` block.
- Agent: `games/rimworld/steward_build.py` (the `build`, `zone`, `designate`, `bill` tools), the digest renderer's
  BUILD lines, the playbook's build order in the steward prompt.

### Routes (POST bodies also take `op_id` and the optional `why`, as every `/act/*`)
Each hand replies like phase 1 (`{"done": "<plain what>"}` or 409 refused with the reason) and, when it placed
something, `undo_seq` (its ledger entry) and `left` (what the cycle caps still allow).

| Route | Body | Notes |
|---|---|---|
| `POST /act/designate` | `kind` mine\|chop\|harvest\|haul\|deconstruct\|cancel; where: `x,z,w,h` box, or `x,z,radius`, or `target` thing id; `ore` (mine only); `max` 1..200 (40); `player_asked` | Without a place: mine needs `ore` and takes the nearest such cells to home; chop takes trees blocking the narrator's blueprints and zones; harvest takes ripe wild plants near home; haul takes unstored items near home. deconstruct/cancel need a place. Deconstruct never touches player-made buildings unless `player_asked` (and the player wrote to the narrator in the last game hour), nor colonists' beds or graves. Cancel removes designations and the narrator's own blueprints. |
| `POST /act/build` | `thing` (word or defName), `stuff`?, `rot`? (0..3 / north..west), `x,z`? or `room`? | No place: a valid cell near home. In a room: an inside cell, against a wall if it fits; a cooler or vent goes into one of the room's plain walls, cold side in, with open air or another room behind it. Refuses unknown things, unfinished research, missing/invalid stuff, bad cells (with why), and footprints over beds or graves. |
| `POST /act/room` | `purpose` bedroom\|barracks\|kitchen\|dining\|storage\|freezer\|hospital\|workshop\|rec; `size` small\|medium\|large or `w,h` 3..11 (outer, walls included); `stuff`?; `door` n\|e\|s\|w\|auto; `x,z`? (south-west corner) | No corner: the site finder walks rings out from home for a box whose inside is clear and buildable (existing walls on the edge are reused). Checks every cell first and places nothing if any fails. Walls + one door (auto: facing home). |
| `POST /act/zone` | `kind` grow\|stockpile\|dump; `plant` (grow); `preset` food\|meds\|raw\|all; `priority`?; `size` or `w,h` ≤ 20; `x,z`? or `room`? | Grow: fertile, unroofed soil near home; refuses a plant that needs research or cannot grow here now. Stockpile in a room: its free inside cells. Dump: away from the rooms. |
| `POST /act/area` | `kind` home\|allowed; `op` add\|remove; `x,z,w,h`; `name` (allowed); `assign` [pawn ids] | Not a steward tool (home grows as colonists build; allowed areas are the player's); for scripts and later phases. |
| `POST /act/bill` | `op` add\|remove\|suspend\|resume; `bench`; `recipe` (add; or to pick a bill); `index` 1-based; `count` or `until`; `radius` | Add: the recipe must be one the bench takes. Until = "do until you have X". |
| `GET /benches` | | Every workbench: id, label, room, bills (index, recipe, mode, suspended), recipes it can take. |
| `GET /vivarium/undo` | | The newest 10 ledger entries and the count. No game needed. |
| `POST /vivarium/undo` | `n` 1..20 (1) | Takes back the narrator's last n placing actions where they still exist: blueprints and frames cancelled (materials drop), designations removed, its zones and zone/area cells removed, its bills deleted. Finished buildings stay. The player's: from the chat ("/undo", "/undo 3") or the settings button. |

**Caps (per steward cycle).** 50 blueprints, 200 zone/area cells, 150 designations, 4 bills. A cycle starts
when the steward posts `/chat/status` thinking (who narrator), or at the latest an in-game hour after the last
start. A room or zone that would go over is refused whole; designations stop at what is left.

**Marks.** A hand returns `NarratorMark`s: `blueprint` (thing defName + root cells; undo also finds the frame
that replaced the blueprint), `designation` (designation defName + things/cells), `zone` (a zone it made),
`zone_cells` (cells it added to an existing zone), `area_cells` (area label + cells it set), `bill` (bench id +
the bill's load id). The ledger keeps the newest 50 placing actions and is saved with the game.

### Digest `build` block
```json
"build": {
  "home": {"x": 120, "z": 98},
  "rooms": [{"id": 12, "role": "bedroom", "label": "Ana's bedroom", "w": 3, "h": 3, "cells": 9,
             "impressiveness": 24, "bed": true, "door": true, "roofed": true, "x": 118, "z": 101}],
  "missing": ["beds for 2 colonists", "stove", "freezer", "dining table", "food stockpile"],
  "benches": [{"id": 4410, "label": "fueled stove", "room": "Kitchen",
               "bills": ["cook simple meal x10", "cook simple meal until 15 (suspended)"],
               "recipes": ["simple meal", "fine meal", "pemmican"]}],
  "zones": [{"id": 3, "kind": "grow", "label": "Growing zone 1", "cells": 64, "plant": "potatoes"},
            {"id": 4, "kind": "stockpile", "label": "Stockpile zone 1", "cells": 36, "preset": "all", "priority": "normal"}],
  "can_build": ["wall", "door", "bed", "table", "chair", "stove", "campfire", "cooler"],
  "stuff": [{"stuff": "wood", "count": 280}, {"stuff": "steel", "count": 410}],
  "plants": ["potatoes", "rice", "corn", "healroot", "haygrass", "cotton"],
  "ores": [{"ore": "steel", "cells": 34, "dist": 18}, {"ore": "components", "cells": 4, "dist": 40}]
}
```
- `home`: the centre of the home area (beds, else the colonists' mean position).
- `rooms`: indoor rooms with a role (not outdoors, not tiny corridors), biggest-impressiveness first, at most 12;
  `x,z` is a cell inside the room; `w,h` the room's bounding box.
- `missing` (at most 8), from the playbook's build order: shelter (colonists without a roofed room), beds for N
  colonists, stove (or campfire), food stockpile, freezer (no room below 0 °C with food storage, when there is a
  cooler to build), dining table, growing zone, research bench. Computed by the Verse-free `BuildView.Missing`.
- `benches` at most 8 (bills at most 5 each, recipes at most 8 each); `zones` at most 10; `can_build` the
  `BuildPure.ThingAliases` words whose first buildable def is researched; `stuff` the building materials in stock
  (count > 0), most first, at most 8; `plants` the `BuildPure.PlantAliases` words sowable now, at most 10;
  `ores` resource rock within 60 cells of home (steel, components, silver, gold, plasteel, uranium, jade), nearest
  first.
- The agent renders it as a few BUILD lines inside the 6 KB digest cap (it shrinks first under pressure).

### Steward
Four tools, `why` on each (as the panel's acting tools): `build` (kind room|thing: purpose+size, or thing + room
id; stuff from `stuff`), `zone` (kind grow|stockpile|dump: plant, preset, size, room), `designate` (kind mine|chop|
harvest|haul|deconstruct|cancel: ore; deconstruct only when the player asked this cycle), `bill` (op, bench, recipe,
count/until). The prompt carries the playbook's build order (shelter → beds → food storage → stove → freezer →
table → research bench) and the safety lines: never build over beds or graves, never tear down player-made
buildings unless the player asked in chat, prefer the planners over coordinates.

### Phase 2 status (what is built)
- Mod: the routes, caps, ledger and undo (`VivariumBuild.cs`, `VivariumRoutesBuild.cs`; tests `VivariumBuildTests.cs`),
  the game bodies and site finder (`VivariumBuildHands.cs`, pure parts in `VivariumBuildHelpers.cs`, tests
  `VivariumBuildHelpersTests.cs`), the digest build view (`VivariumBuildView.cs`, pure `BuildView.cs`, tests
  `BuildViewTests.cs`), the chat's `/undo [n]` and the settings button. The game bodies are compile-checked against
  the 1.7 reference assemblies only; the playtest (docs/release/VIVARIUM-PLAYTEST.md, section C) is their first run.
- Agent: `steward_build.py` (tests `test_steward_build.py`), the BUILD lines in `steward_digest.py`, the build order
  in `strategy/steward_strategy.md`, the amended `food.no_freezer` rule. The steward gets no deconstruct or cancel
  (tearing down is the player's call); `player_asked` exists for scripts and later phases.
- Not yet: furnishing a planned room in the same turn (it has no id until built), power layout (conduits,
  generators placement), temperature control beyond one cooler, trade, caravans.

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL` through easy-engine `custom/use`,
2 slots x 32k, q4_0 KV, grammar protocol, `steward_try` against a fake bridge).** System prompt about 24 k chars
(tools 7.7 k of it, a third being the `why` description repeated on 13 tools); a cycle took about 20 s.
- Day 2, nothing built (3 colonists, sleeping spots): a goal, a plan, then a medium wood barracks, a bed, a stove,
  a small food stockpile (and once a research bench): the playbook's order.
- Day 5 (bedrooms and barracks, campfire, no freezer): a freezer room (small, steel), a table and chairs (in the
  barracks), a research bench, a "simple meal x30" bill at the campfire, research batteries.
- Advisor "we need a freezer, can you build one?": exactly one small steel freezer room and a line saying so.
  Advisor "tear down Bram's bedroom": it says it has no tool to tear buildings down.
- Fixed from this check: the model filled `thing` on a room call (the grammar writes every parameter) and lost a
  step to the error: a room now ignores thing/room; it put the freezer's cooler into an existing bedroom/barracks in
  the same turn: a cooler now needs a listed non-living room, the prompt says to furnish a planned room next turn,
  and the game hand mounts coolers and vents in the room's wall, cold side in.

## Phase 3: combat and defense

On the keeper's install (Core + Biotech) the reachable ending is the ship: research, build, survive 15 days of
raids after the reactor starts, launch. Phase 3 lets the narrator see a fight, command a squad from cover, and plan
defenses, with the game slowed while it fights. The model names intent ("move the squad to the chokepoint", "focus
the grenadiers", "sandbags on the north side"); C# finds the cells. A small controller in the game keeps the squad's
focus fire, pulls the wounded back and sends someone to carry the downed between the narrator's turns.

### Pieces and where they live
- `Mod/Source/Bridge/CombatPure.cs` (Verse-free, tested in `CombatPureTests.cs`): `CombatGrid` (walkable, sight,
  cover, roof, indoors, fog, door, buildable per cell), `LineOfFire` (Bresenham; walls block, sandbags do not),
  `CoverCalc.CoverFrom` (best neighbour within 45° of the shooter, 0 under 2 cells), `CoverFinder` (standing spots:
  cover toward the threat first, never open ground unless asked, sees the approach, spaced), `Approach` (BFS path,
  the narrowest gap on it, compass words), `CombatWords` (threat kinds and groups, heading, defense summary, mood
  risk, health words), `SquadState` + `SquadTactics` (the controller's brain).
- `Mod/Source/Bridge/CombatLayout.cs` (tested in `CombatLayoutTests.cs`): `DefenseLayout.Killbox` (a funnel of two
  wall arms outside a door with traps in it, a killzone inside, a sandbag line with shooter spots behind, turret
  spots at its ends), `LineAcross`, `TrapsOnPath`, `Buildable`, `TurretSpots`.
- `Mod/Source/Bridge/VivariumCombat.cs`: `SquadArgs`, `DefendArgs`, `CombatState` (the fight as last seen, the squad
  and the speed memory; squad and speed are saved in the vivarium JSON as `combat`), `CombatText`.
- `Mod/Source/Bridge/VivariumRoutesCombat.cs`: `GET /combat`, `POST /act/squad`, `POST /act/defend`; the speed rule
  is in `VivariumRoutes.Time`. Tests: `Mod.Tests/VivariumCombatTests.cs`.
- `Mod/Source/RimWorld/VivariumCombatHands.cs`: the game bodies (map → grid, the view, positions, orders as vanilla
  jobs, defenses as blueprints) and `VivariumCombatTicker` (every 250 game ticks from the GameComponent).
- Agent: `games/rimworld/steward_combat.py` (`squad`, `defend`, the COMBAT text), `steward_combat.txt` (the combat
  turn's prompt), the combat cadence and speed rule in `core/steward.py`, playbook lines in `steward_strategy.md`.

### Seeing the fight: `GET /combat` and the digest's `combat` block
```json
{"active": true, "home": {"x": 120, "z": 98},
 "threats": [{"id": 9201, "faction": "Pirates of Ix", "kind": "ranged", "count": 6, "where": "north-east, 38 cells from home",
              "dist": 38, "heading": "toward home", "labels": ["4 pirate gunners", "1 grenadier"], "ids": [9201, 9202]}],
 "fighters": [{"id": 101, "name": "Ana", "drafted": true, "health": "hurt, bleeding", "weapon": "assault rifle", "range": 31,
               "cover": "behind a wall", "mood": "minor", "job": "attacking pirate gunner", "where": "north, 12 cells from home"}],
 "others": [{"id": 105, "name": "Lu", "why": "child", "safe": true}],
 "downed": {"colonists": [{"id": 103, "name": "Cy", "bleeding": true, "where": "north, 20 cells from home"}], "enemies": 2},
 "fires": {"count": 3, "home": 2, "where": "south, 6 cells from home"},
 "defenses": "2 turrets (2 powered); base walled with 2 doors; chokepoint: north-east gap, 2 wide",
 "positions": ["cover", "chokepoint", "door", "wall_n", "wall_e", "wall_s", "wall_w", "home", "inside"],
 "squad": {"members": [101, 102], "stance": "fire_at_will", "focus_group": 9201, "retreat_x": 118, "retreat_z": 96, ...},
 "speed_rule": {"slowed": true, "before": 3}}
```
- `active`: awake hostiles on the map, or a fire in the home area (a downed colonist alone does not start a fight).
  Dormant mechs are listed with `"dormant": true` but do not make the fight active.
- Threat groups: same faction, members within 12 cells of each other; kind melee, ranged, mech, siege, infestation,
  manhunter or mixed; `heading` from where the members were one controller pass (250 ticks) ago; nearest first, 8 at
  most. A group's id is its smallest member id (the controller follows the group if that member dies).
- `fighters`: colonists who can fight (up, violence-capable, 13 or older, not sick in bed), drafted first; `cover`
  against the nearest awake hostile. `others`: everyone else who is up, with why and whether they stand under a
  roof indoors. `downed.colonists` leaves out those already in a bed.
- `defenses` in words; `chokepoint` only when the enemy has a real walking path to home with a gap of 3 or less.
- `positions`: the `at` words that make sense now (no chokepoint or door when there is none).
- `speed_rule` (not `speed`: the envelope carries the game speed under that name).
- The digest has the same object as `combat` only while the fight is active. `GET /vivarium` carries
  `combat: {active, hostiles, since_tick}` (refreshed every 250 game ticks) and the setting `slow_for_combat`.

### The squad: `POST /act/squad`
Body: `op` and, by op, `pawns` (ids; default the squad), `at` (one of the positions) or `x,z`, `room` (with door or
inside), `target` (focus: a threat group id or a hostile pawn id), `below` (retreat: the auto-retreat health, 0.1..0.6).
| op | What the game does |
|---|---|
| draft | drafts the listed fighters, or every colonist who can fight; they become the squad (a fresh one) |
| move | drafts them if needed and sends each to its own spot at `at` (CoverFinder) |
| hold | they stop where they are and shoot from there (fire at will stays on); the controller stops chasing focus targets |
| fire_at_will | fire at will on; the controller may move them to keep focus fire |
| focus | each member attacks the focused group's nearest member (or the one pawn); melee-only members are kept back from mechs; the controller keeps the focus as targets die |
| retreat | moves them to `at` (default inside) and makes that the squad's fall-back point; `below` sets when the controller pulls a wounded member back on its own |
| rescue | for each downed colonist outside a bed, the nearest colonist who can reach it carries it to a bed |
| extinguish | up to 4 firefighters nearest the home-area fires beat them out |
| flee | non-fighters (children, no-violence, the sick, not those in bed) go to roofed indoor cells away from the enemy and cower for an in-game hour |
| undraft | undrafts the drafted colonists (or the listed ones) and clears the squad; refused while awake enemies are within 30 cells of home |
Named fighters in any order but undraft join the squad, so pawns drafted with the plain `draft` hand are looked
after too. Orders place nothing and cost no caps; each success is an action-log entry and a greentext line, e.g.
`> sent Ana, Bram and Cy to the north-east chokepoint, since they come through the gap`.

**The controller** (`VivariumCombatTicker`, every 250 game ticks while the squad has members and the fight is on):
a drafted member under the squad's retreat health (or bleeding and close to it) goes to the fall-back point once
(greentext `> Ana fell back, badly hurt`; it rejoins when patched up); with a focus set, members not busy and not
mid-burst attack the focused group's nearest member; one healthy member within 15 cells carries each downed colonist
to a bed (greentext). It never moves a member who is walking, rescuing or firefighting on an order, and does nothing
in stance hold except retreat and rescue. Its acts are logged with `by` = `controller`.

### Defenses: `POST /act/defend`
Body: `kind` turret|sandbags|barricade|killbox|traps|wall; where: `side` n|e|s|w|threat (default threat: the
nearest awake group's side, else the last raid's side, else the map edge nearest home), or `at` chokepoint|entrance,
or `x,z`; `count` (turret 1..4, default 1; traps 1..8, default 4); `length` (sandbags/barricade/wall 1..20, default 7);
`stuff`.
- Lines (sandbags, barricade, wall) go across the side, three cells inside the base's outer wall on that side (the
  last player wall before the outdoors; never behind home), else 10 cells out from home.
- Turrets: mini-turret research, power on the net that a spot would connect to (refused with the watts), a spot in
  reach of a conduit that sees the approach.
- Traps: spike traps every other cell on the outdoor stretch of the walking path into the base from that side.
- Killbox: at the outside door nearest the side (or nearest `x,z`): funnel walls, traps in the funnel (when
  researched), the sandbag line inside (when researched); refused whole if it would go over the cycle's blueprints.
Everything is blueprints with `blueprint` marks: the phase 2 caps, `undo_seq`, `left`, and `/undo` apply.

### The fight's speed
`POST /act/time {"speed": 1, "combat": true}` slows to normal speed when the setting "The narrator slows the game
for fights" (`narratorSlowsForCombat`, default on) or "controls the speed" is on, and remembers the speed before.
`{"combat": true, "restore": true}` puts it back, only when the narrator slowed it and the game is still at 1x (if
the player changed it, it stays). An explicit speed from the narrator clears the memory. The memory is saved.

### The steward in a fight (agent)
- When `GET /vivarium` says `combat.active` (playing, not paused by the player): the speed rule in code (not the
  model), then one full "combat started" cycle (the digest with its COMBAT lines, 5 steps), then a **combat turn**
  every 5 game seconds and at least 4 real seconds after the last turn ended (`steward_combat_gap_game_s`,
  `steward_combat_gap_real_s`). A combat turn reads only `GET /combat` and the plan line, uses `steward_combat.txt`
  (~1.3 KB), the tools squad, defend, draft, note and do_nothing, 3 steps, 40 s, 160 tokens: about 1.4k tokens in
  all. Chat still wins at once; heartbeat and other events wait for the fight to end. When `combat.active` turns
  false: the speed is put back (if we slowed it and it is still 1x) and one full "combat over" cycle runs (undraft,
  rescue, tend, back to work). Advisor mode has no combat loop and no speed rule.
- While the digest has a combat view the single `order` tool is gone (squad orders name places; the grammar writes
  every field, and x=0, z=0 is now an error anyway). `defend` is there whenever the digest has a build block.
- Playbook lines (prompt and strategy): retreat the wounded first, fight from cover, focus the dangerous (grenadiers
  and rockets, then whoever is in melee with your shooters), never melee a centipede or mech, close doors and hold
  inside against manhunters, don't wake a mech cluster that is not coming, non-fighters flee indoors, undraft when
  clear.

### Phase 3 status
- Built and tested: the routes, state, speed rule and save (Mod.Tests 778 in all), the pure helpers, the agent's
  tools, cadence and prompt (agent pytest 1074 + 1 skipped). The game bodies are compile-checked against the 1.7
  reference assemblies and reviewed; the playtest (docs/release/VIVARIUM-PLAYTEST.md, section H) is their first run.
- Not yet: mortars and EMP, caravan/ambush fights, armor and weapon management, tactical micro beyond the
  controller (kiting, grenade avoidance), raids on other maps.

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL` through easy-engine `custom/use`,
2 slots x 32k, q4_0 KV, grammar protocol, `steward_try` against a fake bridge on recorded fights).**
Combat turns: about 1.4k tokens, 3.7 to 5.7 s each.
- Tribal melee raid from the west (walled base, west and north doors): draft the four fighters, `squad move at
  chokepoint` ("hold the west door against the melee raiders"), both runs.
- Pirate gunners and a grenadier from the north-east: draft Eli, move Dara and Eli (in the open) to cover.
- Mech cluster waking to the south, Cy badly hurt and bleeding, Dara downed: `squad retreat` Cy inside, `squad rescue`
  for Dara (after the prompt put retreat first; before, it moved Cy to cover).
Full cycles with a combat digest (about 8k tokens): 17 to 25 s; draft, chokepoint, hold, flee, focus the group by
number after one wrong number. Fixed from this check: pawns drafted with the plain draft hand never joined the
controller's squad (named fighters now join); `order move` with x=0, z=0 (no order tool in a fight, 0,0 refused);
`below` written as 90 by the grammar (clamped to 10..60%); undraft by name mid-fight (refused near enemies); the
"combat started" cycle capped at 5 steps.
## Unattended play: dialogs

Goal: with "Narrator plays" on, no popup stops a run. Every window that wants a player's choice is seen, shown to the
steward, and answered: by the model when it can, by a safe default when it does not.

**Pieces.** `Mod/Source/Bridge/VivariumDialogs.cs` (Verse-free: the `DialogRecord`, the `DialogBook` of what is open, the
`DialogPolicy` defaults, the chat-note text), `VivariumRoutesDialogs.cs` (`POST /act/choose` through the normal Act
pipeline, so each answer is a greentext line and an action-log entry; `DialogSweep`, the timeout sweep),
`Mod/Source/RimWorld/VivariumDialogHost.cs` (the game side: a scanner run from `GameComponentUpdate`, so it also runs while a
modal window has the game paused, at most every 0.35 real seconds; it turns windows and letters into records with stable
ids, publishes the `dialog` event once per new dialog, answers through `IVivariumHands.Choose`, and runs the sweep).
Routes and shapes: docs/BRIDGE-API.md. The agent reads the list from the digest's `dialogs` array and answers with the
`choose` tool; a `dialog` event wakes the steward at once, also while the game is paused (every other event only wakes
it while the game runs).

**Timeout.** Real seconds, since modal windows force-pause the game: setting `narratorDialogTimeoutS`, default 90 (10..300).
Past it, and only while Narrator plays is on and "Answer popups when the narrator plays" (`narratorAnswerDialogs`, default on)
is on, the sweep applies the safe default, adds the chat note `No answer in 90 s: chose "..." on "..." (default)` and the
action line (action `choose`, by `default`). A default that fails (window gone, option disabled, the game refused) is retried
twice more (every scan, about 3 times a second), then gives up with one chat note and the dialog stays open. A dialog with no
enabled option (and no naming field) is never auto-answered.

### Inventory

| What | Game type | Kind | Options the narrator sees | Safe default | Answered by |
|---|---|---|---|---|---|
| Refugee, wanderer, pod-crash survivor joins | `ChoiceLetter_AcceptJoiner` | `joiner` | the letter's choices (vanilla: accept, reject) | accept | the choice's `action()`, as the letter dialog does |
| Anomaly joiner | `ChoiceLetter_AcceptCreepJoiner` | `creep_joiner` | accept / capture / reject (by position) | decline (the third option, else the first safe) | the choice's `action()` |
| Visitors ask to stay | `ChoiceLetter_AcceptVisitors` | `visitors` | accept / reject | accept | the choice's `action()` |
| Betray visitors (obsolete in 1.7) | `ChoiceLetter_BetrayVisitors` | `betray_visitors` | accept / decline | decline | the choice's `action()` |
| Ransom demand | `ChoiceLetter_RansomDemand` | `ransom` | pay (risk "costs silver") / refuse | refuse | the choice's `action()` |
| Choose a pawn | `ChoiceLetter_ChoosePawn` | `choose_pawn` | one option per pawn | the first enabled | the choice's `action()` |
| Baby to child (colonist or slave) | `ChoiceLetter_BabyToChild` | `baby_to_child` | colonist / slave | the first (colonist) | the choice's `action()` |
| Other `ChoiceLetter` subclasses with choices | any | `letter` | the letter's choices | the first option with no risk | the choice's `action()` |
| Quest offer | plain `ChoiceLetter` with `quest` not yet accepted | `quest` | "Accept the quest" (risk "risk unknown") / "Decline the quest" | decline | accept: `Quest.Accept(a free colonist)`, then the letter is removed; decline: the letter's reject option |
| Growth moment (Biotech) | `ChoiceLetter_GrowthMoment`, window `Dialog_GrowthMomentChoices` | `growth_moment` | one option: take the first passion choices and the first trait | the same | `MakeChoices(first passions, first trait)`, close the window, remove the letter |
| Comms, event dialogs | `Dialog_NodeTree` (and `Dialog_NodeTreeWithFactionInfo`) | `node_tree` | the current node's `DiaOption`s (an option that only closes the tree has role close) | the free option that closes the tree, else the first enabled | `DiaOption.Activate()`, as the click does |
| Confirmations raised by events | `Dialog_MessageBox` | `message_box` | buttons A, B, C (A is risk "destructive" when flagged) | the non-destructive button, preferring the cancel/decline one | the button's action, then the window closes |
| Confirm | `Dialog_Confirm` | `message_box` | Confirm (risk "confirms an action") / Cancel | Cancel | the `onConfirm` action, or just close |
| Name the colony, settlement, faction | `Dialog_NamePlayerFaction`, `...Settlement`, `...FactionAndSettlement` (all `Dialog_GiveName`) | `naming` | none: fields `name` (or `faction`, `settlement`) with a max length | a fallback name from a small list, cut to the field | `IsValidName`, `Named(s)` (and `NamedSecond(s)`), close the window |

Not scanned, on purpose: windows the player opens himself (trade, caravan forming, ritual and ideology dialogs, rename of an
existing pawn, `Dialog_NamePawn`, options, save/load): they are not raised by an event and the narrator has its own hands
for the matching jobs; babies are named by the game. `ChoiceLetter_BabyBirth`, `_EntityDiscovered` and `_GameEnded`, and plain
letters with only Close/Jump/Postpone options, are informational and skipped (they do not block play). `Dialog_Negotiation`
and the like are node trees. Options that have no action and no link (they only open another window, such as "view in quests
tab") are listed as disabled ("only opens a window").

**The model's answer.** The steward's prompt tells it to answer open dialogs with `choose` (dialog id, option number or label;
for naming a short fitting name in `text`, `faction | settlement` for two names), to accept people who ask to join when the
colony can feed them and decline ransoms and quests it cannot judge. Chosen text is clipped to the field's maximum, and a name
the dialog rejects comes back as a refusal the model reads. `dialog_try` (`python -m rimworldllm_agent.games.rimworld.dialog_try
--dialogs agent/tests/data/dialogs_refugee.json [--paused] --llm-url ...`) runs one steward cycle against recorded
`GET /dialogs` bodies (samples for refugee, ransom, quest offer, colony naming are in `agent/tests/data/`).

**Known limits.** The roles of a letter's options are fixed by position (`DialogKinds.RolesFor`), because the labels are
translated: if vanilla's order differs the default picks the wrong one (check 27 in VIVARIUM-PLAYTEST.md). The growth moment
takes the first choices only (no model choice yet). The pawn who accepts a quest is the first free colonist. Hand-written
`Window` subclasses from other mods are not seen. The mod's own "colony lost" choice window is left to the player (ask
means ask); auto mode handles it by itself.

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL` through easy-engine `custom/use`, 2 slots x
32k, q4_0 KV, grammar protocol, `dialog_try --paused` on the four samples).** Each cycle answered its dialog with `choose`
as its first act, then went on with the colony: the refugee accepted ("since we need more hands"), the ransom refused
(option 1, "since we can't afford it"), the hosting quest declined ("since we have a food emergency"), and the naming
dialog got both names. Fixed from it: with the fallback name shown, the model copied it ("Ashford"); a naming dialog's
default is no longer shown to the model and the line says to invent its own names (it then named "Ferngrove"). Seen, not
fixed here: the grammar fills `option`/`text` on every call (harmless: a label or index is used, stray text is ignored),
and on these samples the model spent steps on a cooler for a not-yet-built freezer (phase 2's guard refuses it), so some
cycles ended without a final chat line.

## Unattended play: camera director

Watch mode makes the game watchable like an ant farm: the camera cuts between what is happening and says what it
is showing. Camera only: it never drafts, opens windows or changes the time speed, and selects the followed pawn
only with `watchSelectFollowed` on.

**Controls.** A camera button in the play-settings row beside the "Narrator plays" eye
(`Patch_NarratorSwitch`, icon `Textures/RimWorldLLM/UI/RWL_WatchMode.png`, made by
`~/.local/share/rimworld-hermes-engine/unattended/make_watch_icon.py`), a key (`RWL_ToggleWatchMode`, unbound),
and settings (`watchMode` default off, `watchCaptions` default on, `watchPlayerPauseSec` default 20). Decision:
`watchMode` is a mod setting (it is a viewing preference of the person at the screen), not saved per game. While
it is on it replaces the older "follow a talking pawn" director camera. Moving, zooming or edge-scrolling the camera
pauses the director for `watchPlayerPauseSec`; a jump from a letter or the log counts the same.

**Code.** Pure: `Mod/Source/Bridge/WatchDirector.cs` (`WatchKinds`, `WatchDirector`; tests `WatchDirectorTests.cs`).
Game side: `Mod/Source/RimWorld/WatchCamera.cs` (feeds, steering through `DirectorCamera.Pan`, captions) and
`CameraEvents.cs` (the three new events). Clock is real seconds.

**Scoring.** Interest = kind weight x (0.5 + event severity, clamped 0.5..1.5) x 0.5^(age / 20 s), expiring after
60 s. Variety: the same subject (pawn, or place for place events) shown in the last 90 s counts x0.25, the same kind
in the last 30 s x0.6. Below 0.8 the director stays put. A shot has a dwell (12 s for raid, death, birth, wedding,
funeral; 8 s for ship part, mental break, social fight, combat; 7 s otherwise, at normal pace). Urgent kinds (raid,
death, mental break) may cut in after 3 s when they score 1.3x the current shot; any event may cut into a scene or
filler shot after 3 s. Quiet stretches cycle everyday scenes, story threads and filler (next section).

| Event | Kind | Weight | Sample caption |
|---|---|---|---|
| `raid_start` (via the raid hook) | raid_start | 20 | Camera: a raid arrives from the east |
| `death` | death | 16 | Camera: Ana has died |
| `birth` | birth | 14 | Camera: Ana gives birth |
| `mental_break` | mental_break | 12 | Camera: Bram is having a mental break |
| `gathering` wedding | wedding | 12 | Camera: a wedding |
| `funeral`, `gathering` funeral | funeral | 11 | Camera: Bram's funeral |
| `ship_part` | ship_part | 10 | Camera: a ship part is finished (ship reactor) |
| `mental_break` with state SocialFighting | social_fight | 9 | Camera: Ana is in an argument |
| `injury` | combat | 8 | Camera: Cy is fighting |
| `gathering` party | party | 7 | Camera: a party |
| `masterwork` | masterwork | 6 | Camera: Ana finishes a legendary sculpture |
| `art_created` (player maker) | art_created | 4 | Camera: Ana finishes a work of art |
| (filler) | wander | 1 | none |

**Captions.** Each new shot of interest writes one chat line of kind `camera` (Story filter group): at most one per
12 s (4 s for urgent kinds), never the same text twice within 90 s, none for filler shots.

### Screen director: follows, scenes, battles, zoom, threads

Built on the scorer above (same dwell/caption rules for events). Pure code in `Mod/Source/Bridge/`:
`DirectorScenes.cs` (`SceneFinder`), `DirectorFraming.cs` (`DirectorZoom`, `FollowCam`, `BattleFrame`,
`BattleDirector`), `DirectorPacing.cs` (`DirectorPacing`, `RepeatGuard`, `StoryThreads`); tests
`Mod.Tests/ScreenDirectorTests.cs`. Game side stays in `WatchCamera.cs` (snapshots, hostile scan, thread polls) and
`DirectorCamera.Place`/`Cut` (direct camera moves that the "player moved it" check knows about). No Harmony patch on
input; the camera is moved with `CameraDriver.SetRootPosAndSize` and `JumpToCurrentMapLoc`, zoom limits come from
`CameraDriver.config.sizeRange`.

**Follow shots.** One pawn kept centred: a dead zone with hysteresis (starts moving when the pawn is 3 cells off
centre, settles within 0.5) and a critically damped spring (0.45 s), so small steps never shake the view and motion
eases in and out; more than 70 cells away is a cut. A new shot keeps the spring's speed (a glide, not a stop-start).
The shot ends at its hold, when the pawn despawns or leaves the map, or when the player touches the camera. Setting
`watchSelectFollowed` (default off) selects the followed pawn, without sound, drafting or anything else.

**Everyday scenes.** In quiet stretches (the shot is over), every 3 s at most, the director is offered the current
scenes from a light snapshot of colonists and prisoners (job, joy kind, work type, job target, room, awake/in bed,
age, partners), the strongest story-thread pawn, and one colonist as filler. Events always win: the best event that
clears the minimum score beats any scene, and an event may cut into a scene or filler after 3 s. A pawn shows in one
scene at a time (strongest kind first). Offers merge by scene key, variety for scenes is by scene key, not place.

| Scene kind | Group setting | Weight | Shot | Zoom | Sample caption |
|---|---|---|---|---|---|
| `scene_medical`: a doctor on `TendPatient` | medical | 3.5 | group | close | Camera: Ana tends to Bram |
| `scene_meal`: 2+ eating in one room (or within 8 cells outdoors) | meals | 3.0 | group | close | Camera: dinner in the barracks: Ana, Bram and Cy |
| `scene_romance`: partners within 6 cells, same room, both 18+, awake, not in bed or lovin | romance | 2.6 | group | close | Camera: Ana and Bram spend time together |
| `scene_child`: a child playing or learning (2+ together: one group) | children | 2.6 | follow/group | close | Camera: Tess has a lesson / Tess is playing |
| `scene_recreation`: 2+ adults on joy jobs together | recreation | 2.5 | group | close | Camera: a board game in the rec room: Ana and Bram |
| `scene_prisoner`: a warden job on a prisoner | work | 2.4 | group | close | Camera: Ana talks with the prisoner Vik |
| `scene_taming`: `Tame`/`Train` | work | 2.2 | follow | mid | Camera: Bram tames the muffalo |
| `scene_night`: awake 23:00-04:59, nobody else awake within 10 cells | night | 2.0 | follow | mid | Camera: Dara can't sleep / Dara works through the night |
| `scene_work`: 2+ within 10 cells on field, kitchen, construction, workshop or mining work | work | 1.8 | group | mid | Camera: Cy and Dara work the fields |
| `thread`: a story-thread pawn | | 2.5 | follow | mid | Camera: Bram, after the funeral |
| `wander`: filler colonist | | 1 | follow | mid | none |

Severity for group scenes is 0.5 + 0.25 per pawn (two pawns = 1.0, clamped 0.5..1.5). Group shots centre on the box of
the members' current positions and widen the zoom to fit them. Meal words follow the local hour (breakfast 5-10,
lunch 11-14, dinner 17-22, else "a meal").

**Battle framing.** Once a second, when the map has hostile targets: active hostile pawns
(`GenHostility.IsActiveThreatToPlayer`) and standing colonists. Engaged = within 28 cells of the other side. The
frame centres on the front line (midpoint of the two sides' centroids), and the size fits the box of every engaged
fighter (x1.2 + 3 cells), clamped to 16..55 and the player's limits. While anyone is engaged the fight is held (no
other shots); the only cut-away is a 4 s close-up of a colonist who goes down (at most one per 8 s, "Camera: Cy is
down"). After 8 s with nobody engaged a 6 s closing shot: "Camera: the raiders flee" (hostiles left), "... are
beaten" (none left), "no one is left standing" (no colonist standing); "the animals"/"the mechanoids" when all
hostiles are. The start says "Camera: the fight begins". Battle captions share the caption limit (urgent gap).

**Zoom.** Close 13, mid 22, wide 36 (root size), widened to fit groups, clamped to the player's own zoom range, eased
by a spring (0.8 s). Close: meals, medical, romance, children, prisoners, recreation, death, birth, art, masterwork,
funeral, wedding, breaks, downed. Wide: raid, battle, closing shot, ship part. Mid: the rest. `watchZoom` off leaves
the zoom alone. The player's zoom is remembered when watch mode turns on (and whenever they zoom themselves) and set
back when it turns off, by the button, the key or the settings window.

**Story threads.** A thread makes a pawn score higher (x3 at the start, falling linearly to x1) for a few in-game
hours, on every kind (events, scenes, thread shots), and quiet stretches offer a `thread` shot of them. Starts: a
death (colonists related to the victim or with opinion 30+: "after losing Ana", 8 h; the funeral ending relabels it
"after the funeral" and restarts it), a breakup (`social` event with interaction `Breakup`, adults only, 6 h), a new
colonist (6 h, "newly arrived"), a mental break ending (4 h, "after the break"), a child turning 7, 10 or 13 under
Biotech (4 h, "growing up"). At most 8 threads.

**Pacing** (`watchPace`). Holds: follow and thread 20/15/10 s (calm/normal/lively), scenes 15/11/8 s, events their
dwell x1.25/1/0.8, battles as long as they last. No-repeat: the same pawn (pawn shots) or scene (scene key) is not
shown again within 5/4/2.5 minutes unless the kind is urgent (raid, death, mental break); filler is exempt and keeps
the soft variety factor. The caption limit (12 s, 4 s urgent, no same text within 90 s) covers every caption.

**Cost.** Per frame only the spring math and one pawn position; scene snapshots only when a shot is over (every 3 s
at most), the hostile scan once a second (skipped when the map has no hostile targets), thread polls every 2 s.

**New events** (published through `EventBus`, so the agent sees them): `birth` (Biotech only, guarded by
`ModsConfig.BiotechActive`), `masterwork`, `ship_part`. Details in BRIDGE-API.md.

## Unattended play: stability and restart
For long sessions with nobody at the keyboard. Pure logic is under `Mod/Source/Bridge/` (tests `CapsTests`,
`UnattendedStabilityTests`, `ChatPanelCacheTests`, `SoakTests`); game bodies are in `VivariumStability.cs`.

**Rotating autosave.** If the game's own autosave is on (`Prefs.AutosaveIntervalDays`) the mod does nothing. If it
is off while Narrator plays, every `narratorAutosaveDays` in-game days it saves `RWL-Narrator-1..K`
(`narratorAutosaveKeep`), filling free slots then overwriting the oldest. Only names matching `RWL-Narrator-<1..20>`
are ever written; other saves are never deleted. It saves on the game thread through a long event, and holds
while a long event is running, saving is disabled, or a window prevents saving. An open raid holds it softly (at
most half a day past due).

**Caps.**

| Log / book | Cap |
|---|---|
| ChatLog | 600 lines x 500 chars |
| ActionLog | 500 |
| NarratorLedger | 50 |
| Ledger (open facts) | 120, plus age prune of raid (4 days) and weather (6 days) |
| MindBook records | 400 (LRU); 20 thoughts, 7 diaries per pawn; blob <= 32 KiB |
| ChronicleBook | 120 (lore 12) |
| ArtBook | 200 |
| AvoidTable | 256 |
| EventRing | 2000 |
| ConversationLog | 500 |
| TextDesk | queue 40; per-kind stores 3000/1000/2000/600/300; tried 20000 (export 5000) |
| FirstCues.Seen | 5000 |
| ColonyEvents.Quests | 500 |
| TextHook.Applied | 20000 |
| ChatLive | 64 ended ids; streams time out at 90 s |
| Panel row cache | 200 rows; cache <= 1200 |

Left unbounded on purpose: small warning-dedupe sets (facts, digest, build view) and the meal detector's room keys.
The soak test drives 20 game days over every cap in about 2 s and asserts the caps, a vivarium JSON under 1.5 MB
and a text-desk export under 3 MB.

**Panel.** Text and height are cached per entry, the layout and chip rects are cached, lists are reused, and the
live snapshot is read only when a `ChatLive` version changes (`ChatPanelRows`, `ChatLayoutCache`). The `camera`
kind draws dim blue-grey and the `comment` kind narrator gold italic.

**Colony lost.** Detected from the game-ending flag, or three polls with no free colonist and no carrying caravan
(after tick 2500). `colony_lost` (severity 1.0) is published once. The setting "When the colony is lost":
- ask (default): a dialog, New colony on this world / New world / Stop.
- auto: a chat note, after 20 s the same-world path; if the vanilla wanderers dialog is unanswered for 180 s, save
  the lost world and start a new one.
- off: nothing.
Leaving a world always saves it first as `<colony>-lost-<n>` (a name never reused; a failed save cancels the
leave). No save is ever deleted. A static `RestartHandoff` survives the scene change: Narrator plays stays on and
the chronicle gets a "story continues" entry. A new world uses the vanilla quick-start path from a
`MainMenuDrawer.Init` postfix.

Not run in a game yet: detection, dialog, save naming, quick-start world, handoff and the long-event autosave are
compile-checked only (see PLAYTEST section D).


## Unattended play: documentary voice
While the narrator plays, it also keeps a nature-documentary voice: one dry, warm sentence at a big moment, and one
recap sentence per finished game day. They arrive as chat lines `who: "comment"` (POST /chat; the mod shows them in the
Story filter group). Agent module `core/commentary.py` (small, with a clear contract for a C# port), the RimWorld
moment table in `games/rimworld/moments.py`, tests `tests/test_commentary.py`.
- **Moments and weights** (0..1; under 0.4 nothing is said): colony_lost 1.0, death 0.95, raid_start 0.9, ship_part 0.8,
  birth 0.75, wedding (gathering kind wedding) 0.7, legendary masterwork 0.7, mental_break 0.6, social fight (state
  SocialFighting) 0.5, masterwork 0.5, funeral 0.5, quest accepted / joiner (a `letter` or `dialog` whose text says so)
  0.5, party 0.4. Anything else (tend, social, other letters, other gatherings) is ignored. Urgent: colony_lost, death,
  raid_start, birth.
- **Dedupe and limits.** The same key (kind plus who) inside 2500 game units (about an hour) is dropped; raid_start is one
  key. A normal moment needs `commentary_min_gap_real_s` (180 s of play clock) and `commentary_min_gap_game` (15000 =
  6 game hours) since the last line; an urgent moment needs only a 30 s floor. At most 8 moments wait; a normal one older
  than half a day is dropped. The heaviest waiting moment speaks first.
- **Recap.** When the game day turns and the finished day had at least one moment, one sentence sums it up (the day's
  heaviest six moments, in order). One per day, the latest finished day only; it needs the 30 s floor only. The chronicle
  is not used (it is written per entry, not per day). `commentary_recap` turns it off.
- **When it runs.** Only when the vivarium answers, the narrator plays (`playing`), `settings.commentary` (the mod's
  `narratorCommentary`, default on) and the agent's `commentary` are on, and the narrator slot is free
  (`narrator_may_do_idle_work()`, no idle job running). A starting steward cycle cancels a running commentary call at
  once (`narrator_owner`): nothing is posted. It does not use the actor windows' queue.
- **The call.** `kind="narrator"`, slot 0, 48 tokens, stop at newline; system `adapter.commentary_prompt` plus the game
  data rule; the moment text goes through `gd.clean` inside a `<game_data>` block. The answer goes through
  `sanitize_line` (refusals, invented names, the age gate: a romantic or sexual line, or one naming a minor in it, is
  dropped); a dropped line is spent, not retried. A mod that refuses `who: "comment"` (400/404) is not asked again.
- **narrator_cover** (the old "all windows busy" one-liner) is untouched: it still fills `narrator_lines`, which nothing
  reads. Commentary does not use it.
- **Try it.** `python -m rimworldllm_agent.games.rimworld.comment_try --events agent/tests/data/comment_events_sample.json
  [--llm-url http://host:port/v1]` prints the line per recorded moment and the day recap (FakeLLM without --llm-url).
- Config: `commentary` (true), `commentary_min_gap_real_s` (180), `commentary_min_gap_game` (15000), `commentary_recap` (true).

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL`, `comment_try` on the sample, three runs).**
One documentary sentence per moment, in register: "From the eastern scrub, a marauding band of pirates emerges to test the
resilience of the small, stranded colony."; "The arrival of little Wren marks a sudden, fragile new beginning for Ana and
her weary band of survivors."; recap "Amidst the violence of a pirate raid and the loss of Bram, Ana welcomes baby Wren and
marries Cy, who celebrates their union with a masterwork longsword." The tend event is ignored as designed. One wedding
line of 21 was dropped by the sanitizer (now printed as `(dropped: reason)` by `comment_try`). Lines run 20-30 words; a
shorter cap is a tuning choice for the keeper.
## The narrator's view: reasons, food, clocks (2026-11-27)
The digest now says *why*, not just *what*: the audit and every field are in docs/NARRATOR-VIEW.md. The design rule
stays: short words (counts, reasons, deadlines, direction + distance), never coordinates or item lists; the hands
find the cells.
- `build.loose[].store`: none / full / far / ok, so loose steel with nowhere to go reads "(no stockpile takes it)",
  and the narrator makes a stockpile instead of mining more.
- `stuck[]` (at most 6): blueprints, frames and bills that can't go on, one reason per group: forbidden,
  unreachable, no one can build, material forbidden, waiting on material (need N, have M), bench has no power,
  no one does the work type, no ingredients.
- Pawn rows: `child`, `cannot` (capacity reasons such as no manipulation), `idle`, `break_risk` + `mood_why`. The
  work hand's refusal says why ("Cooking: Ana can't (Pyromaniac)").
- `food`: meals and raw food with the loose part, food about to rot, crops ready (and days for the rest), game
  nearby, hunters.
- `deadlines[]` (at most 6, soonest first): bleeding out, infections (winning or losing the race), rot, winter /
  growing season, expiring letters, rendered as "in 5 h" / "in 2 days".
- Rooms gain `temp_c`, `mountain`, `dirty`; facts gain the counts the rules read (`loose_unstorable`,
  `stuck_no_material`, `stuck_no_builder`, `bills_stuck`, `colonists_idle`, `colonists_children`, `meals`,
  `raw_food`, `food_rot_soon`, `crops_ready`, `huntable`, `days_to_winter`, `days_to_growing`, `bleed_out_hours`,
  `infections_losing`, `tattered_apparel`, `ancient_danger`, `unreachable_piles`).
- Nine new playbook rules and a strategy section ("Reading the digest's new lines"): stockpile when nothing takes a
  pile; fix the stuck reason rather than reorder; respect incapables and children; harvest and hunt before
  starving; act on deadlines.
- All of it is compile-checked against the 1.7 reference assemblies and unit-tested in the pure helpers; the
  playtest (docs/release/VIVARIUM-PLAYTEST.md, section I) is its first run in game.

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma-4-26B-A4B-it-abl-UD-Q3_K_XL` through easy-engine
`custom/use`, 2 slots x 32k, q4_0 KV, `steward_try` against a fake bridge).** The digest had three problems in it:
loose steel that no stockpile takes, 12 wall blueprints with no builder (Construction off), and Cy with no
manipulation. Dara was idle.
- Playing, two cycles: both times Construction went to Dara at priority 1 ("walls are waiting to be built"), never
  to Cy. Both times the narrator hauled the loose piles, and in one cycle it also made a stockpile ("loose steel
  needs a place to go"). It never mined. It added a table and a research bench from `missing`.
- Advisor, asked "put Cy on construction": "I can't do that; Cy has no manipulation and is incapable of
  construction."
- The system prompt was 25.1k chars. Outputs are in ~/.local/share/rimworld-hermes-engine/xlate/live/.

## Biotech: mechs, genes, children

The narrator plays the Biotech parts of the game: mechanitors and their mechs, pollution and wastepacks, genes and
xenogerms, hemogen, children and babies. Everything switches off when Biotech is not active: the digest has no `bio`
block, the steward gets no Biotech tool or strategy notes, and the routes refuse "Biotech is not active".

### Pieces and where they live
- `Mod/Source/Bridge/VivariumBio.cs` (Verse-free, tested in `Mod.Tests/VivariumBioTests.cs`): `IVivariumBioHands` (a
  separate interface: a hands object without it answers "Biotech hands are not available"), `XenogermArgs`, `BioPure`
  (bandwidth math, mech mode words, gene effect words, children's day presets and their age rules, the `missing` list,
  the pollution trend) and `BioWords` (note text).
- `Mod/Source/Bridge/VivariumRoutesBio.cs`: the nine `POST /act/*` routes below, through the same Act pipeline as every
  hand (op_id, `why`, greentext, 409 refusals, the action log).
- `Mod/Source/RimWorld/VivariumBioView.cs`: the digest's `bio` block. `Mod/Source/RimWorld/VivariumBioHands.cs`: the game
  bodies. The `waste` stockpile preset lives in the phase 2 zone hand (`VivariumBuildHands.cs`).
- Agent: `games/rimworld/steward_bio.py` (tools `mechs`, `genes`, `children`), the BIO lines in `steward_digest.py`,
  four playbook rules in `strategy/rules.json` (`bio.baby_hungry`, `bio.hemogen_low`, `bio.bandwidth_idle`,
  `bio.pollution_growing`, from facts derived in `steward_rules.py`), and `strategy/steward_biotech.md`, added to the
  strategy section only when the digest has a `bio` block (so other games pay nothing for it and the main strategy file
  stays under its 12 KB cap).

### What the narrator sees (`bio` block; wire shape in BRIDGE-API.md)
Mechanitors with bandwidth used/total and what is gestating, control groups and their modes; mechs by kind with their
job, energy and a problem word (low power, no recharger, downed, dormant, out of command range); gestators and
rechargers in words; the mech kinds a gestator could take now with their bandwidth. Pollution: wastepacks (and how many
lie in the home area outside a waste stockpile), polluted share of home and map, a day-over-day trend, toxic buildup,
whether an atomizer is built or researched, whether a waste stockpile exists. Genes: each colonist's xenotype with the
effects that matter for play in words ("fast, aggressive, needs go-juice"; "needs hemogen, deathrest"), hemogen percent
for hemogen carriers; hemogen packs, prisoners giving blood and who is low; genepacks (stored and loose), gene banks and
their space, the assembler's state, xenogerms on the map. Children: age, growth tier, learning need and what they are
learning now, whether there is a school desk; babies: age in days, food and hunger, who feeds them (the baby's feeding
tab, else "carers at childcare"); pregnancies of grown women only (visible ones, with days left); colonists with
Childcare on; and a short `missing` list (mech recharger, mech gestator, school desk, crib, waste stockpile, hemogen
packs, gene bank). The agent renders it as MECHS, GESTATORS/RECHARGERS, CAN GESTATE, POLLUTION, GENES, HEMOGEN,
GENEBANK, XENOGERMS, CHILDREN, BABIES, PREGNANT, CARERS and BIO MISSING lines; under the 6 KB cap they shrink after the
BUILD lines, and the last step keeps only BIO MISSING, hungry babies and low hemogen.

### The hands
| Route | Does | Refuses (examples) |
|---|---|---|
| `mech_mode` | a control group's work mode: work, escort, recharge, shutdown (dormant/self-shutdown accepted) | no mechanitor; no such group; already in that mode |
| `mech_group` | moves a mech to another of its overseer's groups (unassigns from the old one, checks it moved) | not a colony mech; no overseer; already there |
| `mech_gestate` | queues a gestation bill (repeat count 1..3) at an idle powered gestator that takes the recipe, for the mechanitor with the most free bandwidth | unknown kind (lists the game's); research missing (names it); no gestator ("build one first"); bandwidth: "not enough bandwidth: 5 of 6 used, a lifter needs 1 and 1 stays free" (used + gestating + queued unbound bills + the new mechs + 1 spare must fit; the game's own `HasBandwidthForBill` checks too). The bill is a ledger mark: `/undo` deletes it. |
| `genepack` | a free grown hauler carries loose genepacks (up to 4 a call) to a powered gene bank with space | no loose genepacks; no bank; banks full or unpowered; nobody can carry |
| `xenogerm` assemble | starts the gene assembler on named genepacks from the banks next to it, with a name and a plan (10..200 chars) the player reads in the greentext | no assembler; working; no power; unknown pack; duplicate gene; conflicting genes; complexity over the assembler's maximum; metabolism below the game's floor; archites missing |
| `xenogerm` implant | sets a grown free colonist to receive a xenogerm (a doctor implants it), with a plan | the setting "The narrator may implant xenogerms" is off (default); children always ("the narrator never implants genes in children"); prisoners and slaves; the colonist's beliefs; the game's own target check |
| `hemogen` | turns a prisoner's hemogen-farm interaction on (donate) or off (stop) | not the colony's prisoner; a child; hemogenic |
| `child_schedule` | a child's whole day from a preset: school, play, default (the hand builds the 24 hours from the child's age with `BioPure.ChildHours` and checks them with `CheckChildHours`) | not a child; babies (no timetable); a preset that breaks the age rules |
| `feed` | who feeds a baby and when (never, childcare, urgent = before other work); feeder 0 resets the baby's feeders to the carers' default | not a baby; the feeder is a child or can't feed |
| `child_care` | a grown colonist's Childcare priority (through the work hand, so priorities turn on and every pawn refreshes) | a child; incapable |

Children's days (ages: baby under 3, child 3..12; 13+ is not a child): every preset sleeps 21-6; school has recreation
17-19 (learning happens in the free "anything" hours), play has recreation 9-12 and 15-18, default has recreation 16-18
and, for ages 7..12 only, light work 13-15. The rules: babies at least 10 sleep hours and no work or recreation blocks;
children at least 9 sleep hours, at least 4 free hours, at most 3 work hours and none under 7. Children's work stays
where the game allows (the work hand already refuses what a child can't do); the steward's `draft` and `order` tools
refuse children ("children stay out of fights"), and drafting "all" skips them.

Pollution has no hand of its own: the zone tool gains preset `waste` (a wastepack-only stockpile placed like a dump,
12..30 cells from the base), and the build tool's words gain the Biotech buildings when researched (mech gestator,
large mech gestator, mech/basic/standard recharger, school desk, blackboard, crib, baby sleeping spot, toy box, gene
bank, gene assembler, gene extractor, growth vat, wastepack atomizer, band node, deathrest casket).

### Steward
Three tools, each only when the digest holds what it needs, each op too: `mechs` (op mode|group|gestate), `genes` (op
store|assemble|implant|donate; implant only when the digest says the player allows it and a xenogerm exists), `children`
(op schedule|feed|care). Names resolve to ids from the digest; an unknown name comes back listing the known ones. The
Biotech notes: a mechanitor early for labor mechs once Basic mechtech is done, keep one bandwidth free, a recharger
before gestating; wastepacks to a waste stockpile far from home and an atomizer once researched, never dump near home;
hemogen from packs or a prisoner donor, never strip food for it; children learn daily, babies are fed before work,
children never fight; genes only with a plan the player can read. The three tools add about 2.5 k characters of tool
text when all are offered (about 0.7..0.9 k for one).

### Limits
- The game bodies are compile-checked against the 1.7 reference assemblies only; the playtest (VIVARIUM-PLAYTEST.md,
  section J) is their first run.
- Mechs are not squad members: the combat squad drafts colonists and slaves only (drafting mechs needs the mechanitor
  in range and its own controller); mech control groups in escort mode follow their mechanitor into fights.
- No growth vats, no gene extraction, no deathrest management, no ovum/embryo work, no baby naming (the game names
  babies), no mech resurrection or boss calls. Hemogen comes from prisoner donation; hemogen packs are kept by the
  game's own hemogen-farm bills (no colonist donation).
- The pollution trend lives in memory: a loaded game starts "steady" and needs a game day to compare.

**Live check (2026-11-27, poe P40 GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL` through easy-engine `custom/use`, 2 slots x
32k, q4_0 KV, `steward_try --playing` on the four `bio_*` samples).** Prompt about 8.5 k tokens (system about 29 k chars
with the Biotech notes and tools), a cycle 11..30 s. With the samples' food emergency left in, the model spent its turns on
food and the freezer (sensible) and only the hungry baby got Biotech care, so the live digests were made calm (12 food
days, nothing missing in BUILD) to see the Biotech choices:
- Hungry baby, no feeder: every cycle (5 of 5) set Ana to feed Dee "urgent" first, then built a crib and a school desk
  for the 6-year-old when they were buildable.
- Mechanitor with 2 bandwidth free and no recharger: it built a mech recharger first (playbook order) and went on to
  bedrooms; asked in chat to use the spare bandwidth, it built the recharger and queued one lifter (keeping 1 free).
- Pollution growing, wastepacks near home, no waste stockpile: a waste stockpile in 3 of 7 cycles (other turns went to
  bedrooms and the table, the other P2-P4 alerts of the sample).
- Sanguophage at 15% hemogen with a grown prisoner: ignored in 7 cycles while the HEMOGEN line only said who needed it;
  once the line named "prisoners who could give blood: Cole" it set Cole to give blood in a heartbeat cycle, and at once
  when the player asked.
- Fixed from it: the fake bridge did not know the Biotech verbs; `feed` without a feeder reset the baby's feeders (the
  model sent "none"): feed now needs a named feeder; `bio.hemogen_low` is priority 1 and says "this turn"; the MECHS line
  says how much bandwidth is free; the HEMOGEN line names grown, non-hemogenic prisoners who could give blood (the mod
  sends them as `hemogen.prisoners`); the `genes` tool says op donate is blood from a prisoner.
- Seen, not fixed here: the grammar fills `count` and `until` on a bill (the bill hand refuses both and the model retries),
  and planned-freezer cooler attempts (phase 2's guard refuses them), as in the earlier live checks.

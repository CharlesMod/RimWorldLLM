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

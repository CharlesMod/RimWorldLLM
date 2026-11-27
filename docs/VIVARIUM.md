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
- **Enter** (when no text field has focus and no window wants it) opens a one-line chat bar above the
  bottom bar; Enter sends, Esc closes. A small scrollback panel shows the conversation: player lines,
  narrator lines, and short action notes such as "→ set Cook priority 1 for Ana".
- Messages go to the narrator. `@Name text` goes to a colonist through the existing talk path
  (TalkQueue → agent → `POST /say`), and the colonist's answer shows in the scrollback too.
- Chat history is saved with the game.
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
  One cycle = observe → decide → a few actions → a one-line chat note when worth saying (not every
  action). The cycle's final text is that line; "nothing to say" posts nothing.
- **Strategy knowledge** is a prompt section loaded from a file (see "Steward prompt" below), so a
  RimWorld playbook can be dropped in without code changes.

### 4. The hands (C#, game thread, through the pump)
Player verbs, done through the same code paths the UI uses (designators, Command gizmos, managers),
never dev-mode cheats. Each returns ok with a short note, or the reason the game gave.
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
Every action is logged: the full action log (`GET /actions`, saved with the game) plus chat notes. No
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
| `POST /chat` | no | the steward posts a `narrator` line or a `note` |
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
settings gave. Every success is appended to the action log; the bridge does not post chat notes on its
own (the steward decides what is worth a line). Acts are allowed whether or not the narrator is playing
(advisor mode does one task).

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
No designations, building, zones, bills, trade or caravans yet; draft orders are single commands
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

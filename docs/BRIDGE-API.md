# HermesVoice bridge API v2 (contract)

The contract between the mod (C#, in game) and the agent (Python, its own program). Both sides
build against this file. A change to it goes in its own commit, and both sides update together.
Background: `docs/SPEC.md` §3, §6, §8, §10.

The file has two parts. **Part 1, the protocol,** is game-agnostic: connection, auth, envelope,
writes, the event ring and ledger shape, and the say/thought/mind/talk semantics. It is
implemented by `Mod/Source/Bridge/` (no game references) and could serve another game.
**Part 2, the RimWorld schema,** holds what only RimWorld defines: the `config`, `colony`,
`pawn` and `scene` fields, the event types and how they are computed, and the intent effects.
It is implemented by `Mod/Source/RimWorld/`.

# Part 1: Protocol (game-agnostic)

## Connection

- **Default port 9765**, falling back through 9766–9774 when a port is taken. (8765 is used by
  RIMAPI, Antfarm and rimbridge.)
- On start, the mod writes **`<RimWorld config dir>/HermesVoice/bridge.json`**, and rewrites it
  on every restart of the bridge:
  `{"port": 9765, "bind": "127.0.0.1", "token": "<random per run>", "pid": 1234, "version": "2.0.0", "save_epoch": 3}`.
  The token is regenerated per run unless the player has set a fixed shared token in settings,
  which is needed when the agent runs on another machine.
- `bridge.json` also carries the agent's LLM endpoint, set in the mod's settings (Connection):
  `"llm": {"base_url": "http://host:4480/v1", "model": "gemma", "api_key": "..."}`. This is the only
  place the API key is written; **the key never travels through the bridge API** (`GET /config` says
  only `api_key_set`). The mod writes the file owner-only (mode 600 on Linux and macOS) and rewrites
  it when the player edits these fields. The agent re-reads `bridge.json` when `GET /config` changes
  or a request fails with `unauthorized` (the player can regenerate the token).
- **Auth:** every request carries `Authorization: Bearer <token>`. A token is mandatory: the
  bridge refuses to start without one. `?token=` is accepted only on GET.
- The v1 `/state` endpoint is gone; `GET /colony` replaces it. Default bind is 127.0.0.1; a remote agent needs a different bind address and a fixed token.
- No CORS headers. Body limit 64 KiB. JSON in and out, UTF-8.

## Envelope

Every reply is a JSON object containing at least:

```json
{"ok": true, "tick": 123456, "save_epoch": 3, "paused": false, "speed": 1}
```

- `speed` is the game's time speed: 0 paused, 1 normal, 2 fast, 3 superfast, 4 ultrafast. `paused` is also true when the game is force-paused (a dialog open) at a non-zero speed.
- `save_epoch` increments on every game load or new game, and is 0 in the main menu. When it
  changes, the agent drops its working copy of the minds and re-reads them with `GET /mind/{id}`.
- Errors: `{"ok": false, "error": "<code>", "detail": "...", ...envelope}` with an HTTP status.
  Codes: `unauthorized` 401, `not_found` 404, `bad_request` 400, `too_large` 413,
  `conflict` 409 (dead, despawned or wrong-map pawn), `expired` 410, `duplicate` 409,
  `no_game` 503, `busy` 503 (also returned at once when the game thread has run no frame for over 1 s, e.g. the window is in the background or loading; retry shortly), `timeout` 504, `internal` 500 (a bug in the mod; the detail says what).

## Writes: operation id and expiry

Every POST and PUT body carries:

```json
{"op_id": "<uuid from the agent>", "expires_tick": 123999}
```

- `op_id` is required on every write (`bad_request` without it). The mod remembers the op ids of writes that succeeded for 10 minutes of real time; a failed write (`conflict`, `expired`, ...) is not remembered, so a retry runs again.
- The mod remembers op ids for 10 minutes of real time. A repeat gets `duplicate` (409), with the
  original reply fields in `result`.
- A write arriving after `expires_tick` gets `expired`. `expires_tick` may be omitted for
  writes that never expire (player talk). The agent sets it from the expiry classes in
  SPEC §7.

## Endpoints

### `GET /health`
The envelope plus `{"mod": "HermesVoice", "version": "2.0.0", "game_loaded": true}`.
Unauthenticated, so the Test button and the agent can identify the bridge before auth.

### `GET /events?since=<seq>`
Events with a sequence number greater than `seq`, oldest first, at most 200 per call, plus
`next_seq`. The mod keeps the last 2,000 events in RAM. When `since` is older than that, the
reply has `"gap": true`. Each event:

```json
{"seq": 812, "tick": 123400, "type": "injury", "map": 0,
 "pawns": [{"id": 1, "role": "victim"}, {"id": 7, "role": "attacker"}],
 "summary": "Ana was shot in the left leg by a pirate.",
 "severity": 0.42, "witnesses": [1, 3, 9], "ledger_id": null, "data": {...}}
```

- Long-running facts carry a `ledger_id`. A matching `*_end` event closes the entry.
  `GET /colony` lists the open entries.
- The existing push to the agent URL stays, sending the same event objects.

### `POST /say`
```json
{"op_id": "...", "expires_tick": 124000, "pawn": 1, "listener": 3, "text": "Hold still.",
 "intent": "comfort", "conversation_id": "c-77", "combat": false, "about": null, "talk_id": null}
```
- `about`: the id of the third pawn a `gossip` line is about. Required for `gossip`; without
  it the effect is refused with `no_about`.
- `talk_id`: set when the line answers a `GET /talk` request, which removes that request.
- `vanilla_ref`: optional. The `ref` of a `social` event with `data.vanilla` (below): the line was
  written for one of the game's own interactions. The mod attaches the line to that interaction's
  log entry and thoughts (reply `applied.attached`: `attached`, or why not: `expired`, `unknown`,
  `wrongpair`, `alreadyattached`), shows the bubble, and applies **no** effect (`intent` is
  ignored; `refused.effect` is `vanilla_interaction`). A line that cannot attach is spoken as a normal line.
- One log entry per line. When an effect was applied it reads `Bram insulted Hana: "..."`
  (the quote is the sanitised line, at most 140 characters, setting "Show quotes in thoughts and log");
  otherwise `Bram to Hana: "..."`. The thoughts the effect gives carry the same quote and speaker.
- The mod checks, on the game thread: speaker alive, awake, spawned, on the listener's map
  and within hearing range. Otherwise `conflict`.
- The mod shows the bubble and writes a play-log entry (and a combat-log entry when `combat`).
- It applies the intent's effect under the C# caps (SPEC §8), unless effects are off.
- Reply: `{"applied": {"bubble": true, "log": true, "effect": "comfort", "thoughts": ["HV_Comforted"]}}`,
  or `{"refused": {"effect": "<reason>"}}` with the bubble still shown. Reasons: `pair_cap`
  (the per-pair daily cap is reached), `effects_off`, `not_negative` (`comfort` when the
  listener has no negative mood thought), `no_about`.

#### Social events for the game's own interactions
When `Voice the game's own social interactions` is on and both pawns are voiced colonists, a `social`
event for an interaction in the high or idle list (settings `interactionHigh`, `interactionIdle`; default
high: Insult, Slight, KindWords, DeepTalk, RomanceAttempt, MarriageProposal, Breakup, ConvertIdeoAttempt,
Reassure, Counsel; idle: Chitchat) carries extra `data`: `vanilla: true`, `ref` (string, send it back as
`vanilla_ref`), `tier` (`high` or `idle`), `vanilla_text` (the sentence the game's grammar wrote, the
line must stay on its subject), `window_ticks` (how long the line may take to attach; default 10 game
minutes, setting `attachWindowMinutes`), plus the usual `interaction` and `interaction_label`. High tier
events bypass the 60 s social cooldown; idle ones keep it. Romance-type interactions are never sent when
either pawn is under 18 years biological age. The game's own thoughts, opinions and fights are untouched.

### `POST /thought`
`{"op_id": "...", "pawn": 1, "text": "Should have stayed in bed.", "kind": "thought"}`. `kind` is
optional: `"thought"` (the default) is the inner monologue, `"diary"` is the pawn's daily diary
entry. Both are shown in the Mind tab; neither makes a bubble or an effect. The mod keeps them in
separate lists per pawn, so diaries do not push inner thoughts out: the last 20 thoughts and the
last 7 diary entries (older ones are dropped). Text is trimmed and cut at 500 characters for a
thought and 1,500 for a diary entry. An unknown `kind` is `bad_request`. Reply `{"stored": true}`.

### `POST /chronicle`
`{"op_id": "...", "day": 12, "text": "The colony buried Bram without speeches.", "kind": "day"}`.
One entry of the colony chronicle (the narrator's running history). `day` is an integer day index
on the game clock (0 is the first day; the same count the agent uses for diaries). `kind` is `"day"`
(the usual summary of a day) or `"major"` (written at once after a major event); it defaults to
`"day"`. The mod trims the text and cuts it at 2,000 characters, appends the entry, and keeps the
newest 120 (older ones are dropped). `text` empty, `day` not an integer or below 0, or an unknown
`kind` is `bad_request`. Reply `{"stored": true, "count": 37}` (the entries now held). The
chronicle belongs to the game: it is saved with it, so a reload brings back the chronicle as it
was at that save (the agent sees the `save_epoch` change).

### `GET /chronicle?since_day=<N>`
`{"entries": [{"day": 12, "kind": "day", "text": "...", "tick": 734000}, ...]}` (plus the envelope):
the entries whose `day` is at least `N` (default 0), oldest first, in the order stored. `tick` is
the game tick at which the mod received the entry. `since_day` that is not an integer is
`bad_request`. After a `save_epoch` change the agent can read this to see what the loaded save
holds.

### `GET /mind/{id}` and `PUT /mind/{id}`
The pawn's mind blob, opaque to the mod, saved in the save file.
- `GET` returns `{"mind": {...} | null, "rev": 4}`.
- `PUT {"op_id": "...", "rev": 4, "mind": {...}}` saves it, and replies with the new `rev`
  (or `conflict` if `rev` is stale). Size limit 32 KiB per pawn.
- **Player edits.** The Mind tab's Director's notes box edits the blob in place through the same
  rev: it sets `notes` (string, 2,000 characters at most), `notes_edited_by_player: true` and
  increments `notes_rev` (integer), leaving every other key untouched. The stored `rev` rises too,
  so the agent's next `PUT` with the old `rev` gets `conflict`. **On `conflict` the agent must
  re-read the blob and merge**: take `notes` from the mod's copy whenever `notes_edited_by_player`
  is true, then apply its own changes and `PUT` again. It must never write back an older `notes`.
- The mod only reads the blob for display, and reads each key under the agent's names (`fixed.text`,
  `voice.{register_line,tics,examples,edited_by_player}`, `evolving_bio`, `bio_history`, `wants`,
  `memories`, `relations`, `notes`); unknown keys are ignored.

### `GET /talk`
Talk requests waiting for the agent: player-typed lines, other mods' requests through the
hook API, and debug-window requests:
`{"requests": [{"id": "...", "pawn": 1, "text": "...", "from": "player"}]}` (plus the envelope).
A request is removed once the agent answers it with `POST /say` carrying `"talk_id"`.

## Threading (mod side)

All game state is read and written on the game thread, drained each frame in a
`Root.Update`-style hook under a per-frame time budget (2 ms), so it also works while paused.
The HTTP threads only parse, authenticate and wait. Timed-out requests are abandoned and
never applied later.

# Part 2: RimWorld schema

What the game-specific side fills in. Every field is optional for a consumer to read; DLC fields
appear only when that DLC is active.

## Snapshot endpoints

### `GET /config`
Settings the agent must honour:
`{"effects": "normal"|"light"|"off", "language": "en", "events": {"injury": true, ...},
"talk_frequency": 1.0, "pause_ambient_at_speed": 3, "dlc": ["Ideology", "Biotech", ...],
"llm": {"base_url": "http://host:4480/v1", "model": "gemma", "api_key_set": true}}`.
`llm` is the agent's OpenAI-compatible endpoint from the mod's settings. The key itself is never
sent here; the agent reads it from `bridge.json` (Part 1, Connection).

### `GET /colony`
The colony at a glance: maps, colonists and their ids, active event ledger entries
(see Events), season, weather, food and medicine trend numbers.

### `GET /pawn/{id}`
The full snapshot for the fixed bio and the scene (SPEC §6):
`id, name {first, nick, last}, age, gender, body_type, xenotype, genes[], backstory {child, adult},
traits[{def, label, degree}], ideo_role, title, skills[{skill, level, passion}],
health {conditions[], missing_parts[], pain, consciousness, downed}, needs {mood, food, rest, joy, ...},
mood {value, thoughts[{label, offset, cause_pawn}]}, job, gear {worn[], held},
relations[{pawn, kind, opinion}], status ("colonist"|"prisoner"|"slave"|"guest"|"lodger"),
faction, map, pos, room, alive, spawned`.
DLC fields appear only when that DLC is active.

### `GET /scene/{id}`
What this pawn perceives right now: `room {role, impressiveness, clean, temp}`,
`present[{pawn, job, distance, can_hear}]`, `time_of_day`, `weather`, `danger`,
`trends {food_days: [9,7,5], ...}`.

## Events: types, witnesses, severity

- `witnesses` are the pawn ids who saw or heard it. They are computed in C# from room, line
  of sight and hearing range × Hearing capacity.
- `severity` is in 0..1, from game numbers (thought mood offset, injury severity, death = 1).
- Types: `injury, death, mental_break, level_up, tend, social, raid_start, raid_end, quest,
  quest_end, lodger_arrive, lodger_leave, prisoner, funeral, weather_extreme, weather_extreme_end,
  thought, player_talk`. `quest` has `data.phase` `offered` or `accepted`; `prisoner` has
  `data.phase` `captured`, `released`, `recruited` or `died`; `funeral` has `data.phase`
  `start` or `end`. Ledgers: `raid` (`raid_start`..`raid_end`), `quest` (accepted..`quest_end`),
  `lodger` (`lodger_arrive`..`lodger_leave`), `prisoner`, `funeral`, `weather`
  (`weather_extreme`..`weather_extreme_end`).
- `thought` events are notable new mood thoughts only: absolute mood offset of at least 5.
  The mod's own `HV_` thoughts never produce one.
- Per-event toggles in settings apply to the ring as well as to the push. Events are throttled
  by per-pawn cooldowns (injury 30 s, social 60 s, tend 20 s, mental break and thought 10 s).
  The 20 per real minute cap applies only to the push to the agent URL, not to `/events`.
- After a game load, lodgers, prisoners and quests already present are learned silently (no
  arrive events); raids in progress are saved with the game and still end with `raid_end`.

## Diaries and the chronicle in the RimWorld mod

- Diary entries (`POST /thought` with `kind: "diary"`) and inner thoughts are saved per pawn with
  the game, as part of the pawn's mind record, and are shown in the pawn's Mind tab: the Diary
  section lists the last 7 entries, newest first, each with the game date of its arrival.
- The chronicle is saved with the game in the mod's game component (`hermesChronicle`), so loading
  an earlier save rolls it back with everything else; a new game starts it empty. It is shown as
  the second tab of the Voices window (the main-bar button), newest first, each entry with the
  game date of its `day`; `major` entries are highlighted.
- An entry's date is the real calendar date of the `day` index (day N starts at tick N x 60000).

## Intent effects (`POST /say`)

The `intent` field of `POST /say` is one of the intents in SPEC §8. The mod checks the listener
and applies the effect as XML ThoughtDefs under its caps (the `effects` setting, the per-pair
daily cap, stack limits). The `POST /say` checks of Part 1 map to RimWorld as: speaker alive,
awake, spawned and humanlike; listener alive, spawned, on the same map and within hearing range
(room or line of sight, distance against hearing range x Hearing capacity). `refused.effect`
carries the reason (`pair_cap`, `effects_off`, `not_negative`, ...) while the bubble still shows.

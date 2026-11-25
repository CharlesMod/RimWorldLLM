# HermesVoice bridge API v2 (contract)

The contract between the mod (C#, in game) and the agent (Python, its own program). Both sides
build against this file. A change to it goes in its own commit, and both sides update together.
Background: `docs/SPEC.md` §3, §6, §8, §10.

## Connection

- **Default port 9765**, falling back through 9766–9774 when a port is taken. (8765 is used by
  RIMAPI, Antfarm and rimbridge.)
- On start, the mod writes **`<RimWorld config dir>/HermesVoice/bridge.json`**, and rewrites it
  on every restart of the bridge:
  `{"port": 9765, "bind": "127.0.0.1", "token": "<random per run>", "pid": 1234, "version": "2.0.0", "save_epoch": 3}`.
  The token is regenerated per run unless the player has set a fixed shared token in settings,
  which is needed when the agent runs on another machine.
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
  `no_game` 503, `busy` 503, `timeout` 504, `internal` 500 (a bug in the mod; the detail says what).

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

### `GET /config`
Settings the agent must honour:
`{"effects": "normal"|"light"|"off", "language": "en", "events": {"injury": true, ...},
"talk_frequency": 1.0, "pause_ambient_at_speed": 3, "dlc": ["Ideology", "Biotech", ...]}`.

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
- Long-running facts carry a `ledger_id`. A matching `*_end` event closes the entry.
  `GET /colony` lists the open entries.
- The existing push to the agent URL stays, sending the same event objects.

### `POST /say`
```json
{"op_id": "...", "expires_tick": 124000, "pawn": 1, "listener": 3, "text": "Hold still.",
 "intent": "comfort", "conversation_id": "c-77", "combat": false}
```
- The mod checks, on the game thread: speaker alive, awake, spawned, on the listener's map
  and within hearing range. Otherwise `conflict`.
- The mod shows the bubble and writes a play-log entry (and a combat-log entry when `combat`).
- It applies the intent's effect under the C# caps (SPEC §8), unless effects are off.
- Reply: `{"applied": {"bubble": true, "log": true, "effect": "comfort", "thoughts": ["HV_Comforted"]}}`,
  or `{"refused": {"effect": "pair_cap"}}` with the bubble still shown.

### `POST /thought`
`{"op_id": "...", "pawn": 1, "text": "Should have stayed in bed."}`. The inner monologue,
shown in the Mind tab. No bubble, no effect.

### `GET /mind/{id}` and `PUT /mind/{id}`
The pawn's mind blob, opaque to the mod, saved in the save file.
- `GET` returns `{"mind": {...} | null, "rev": 4}`.
- `PUT {"op_id": "...", "rev": 4, "mind": {...}}` saves it, and replies with the new `rev`
  (or `conflict` if `rev` is stale). Size limit 32 KiB per pawn.

### `GET /talk`
Talk requests waiting for the agent: player-typed lines, other mods' requests through the
hook API, and debug-window requests. `[{ "id": "...", "pawn": 1, "text": "...", "from": "player" }]`.
A request is removed once the agent answers it with `POST /say` carrying `"talk_id"`.

## Threading (mod side)

All game state is read and written on the game thread, drained each frame in a
`Root.Update`-style hook under a per-frame time budget (2 ms), so it also works while paused.
The HTTP threads only parse, authenticate and wait. Timed-out requests are abandoned and
never applied later.

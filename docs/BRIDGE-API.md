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
  it when the player edits these fields. The agent reads `bridge.json` at start-up (not yet again on an
  `unauthorized` reply: a regenerated token needs an agent restart). It re-reads `GET /config` every
  `config_refresh_real_s` (10 s), so mod settings apply within seconds.
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
 "intent": "comfort", "kind": "normal", "conversation_id": "c-77", "combat": false, "about": null, "talk_id": null}
```
- `kind`: optional. How the line was delivered: `"normal"`, `"shout"` or `"whisper"`. The agent decides it
  (in the same constrained completion that labels the intent, so it costs no extra model call) and
  the mod only presents it. Absent or unknown means the agent did not say: the receiver uses its own
  heuristics (the intent, the punctuation and capitals of the text, `combat`). `"normal"` means "said at
  an ordinary volume": the receiver must not guess a shout from `!` or capitals. An agent should send
  `"shout"` only when the scene justifies it (combat, anger, alarm) and not on every line.
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
minutes, setting `attachWindowMinutes`) and `window_real_s` (or this many real seconds, whichever is longer; default 5,
setting `attachRealFloorSec`; at 4x the tick window alone is half a second), plus the usual `interaction` and `interaction_label`. High tier
events bypass the 60 s social cooldown; idle ones keep it. Romance-type interactions are never sent when
either pawn is under 18 years biological age. The game's own thoughts, opinions and fights are untouched.

### `POST /thought`
`{"op_id": "...", "pawn": 1, "text": "Should have stayed in bed.", "kind": "thought", "surface": "Should have stayed in bed."}`. `kind` is
optional: `"thought"` (the default) is the inner monologue, `"diary"` is the pawn's daily diary
entry, `"dream"` is a sleeping pawn's dream fragment. All are shown in the Mind tab; none makes an effect.
A `dream` is stored in the thoughts list like a thought and shown as the same cloud over the (sleeping) pawn,
paced like any thought cloud, unless the mod setting "Dreams while colonists sleep" is off (then it is stored, not drawn). `surface` is optional: one short
sentence (at most about 90 characters) that the receiver may show to players in a transient
display; the full `text` stays in the Mind tab. A `thought` with no `surface` is shown that way
only if `text` is already short; a `diary` is never shown that way. The agent fills `surface`
(its short "passing thought" job sends `text` and `surface` as the same line). The mod keeps them in
separate lists per pawn, so diaries do not push inner thoughts out: the last 20 thoughts and the
last 7 diary entries (older ones are dropped). Text is trimmed and cut at 500 characters for a
thought and 1,500 for a diary entry. An unknown `kind` (not thought, diary or dream) is `bad_request`. Reply `{"stored": true}`.

The agent's inner monologue (agent switch `inner_monologue`) uses this route only as before: an inner
*aim* (what the pawn privately wants, made after a spoken line) is sometimes posted as a `thought` with
`text` and `surface` the same short line, after the line's balloon (the mod's ThoughtPacing already
waits for it). It is rate-limited by the agent (one per pawn per 90 s of play clock, 35% chance, never at
the ambient-pause speed). No new field, route or mod change.
### `POST /art`
`{"op_id": "...", "thing": 4711, "text": "Hana carved this after the winter raid..."}`. Sets the description
shown for one artwork on its info card (replacing the game's generated tale text; the title stays).
`thing` is the artwork's `thingIDNumber` (from an `art_created` event's `data.thing_id`). The mod
trims the text, cuts it at 600 characters, and keeps the newest 200 descriptions in the save
(older ones are dropped; a save without them loads none). `thing` missing or not an integer, `text`
empty, or the thing is not an artwork is `bad_request`; a thing that is destroyed or not on a map
(or inside a minified piece) is `conflict`. When the setting "Show written art descriptions" is
off, the text is still stored but the card shows the game's own text; `GET /config` shows it as
`art_descriptions`. An older mod without this route answers 404.

### `POST /chronicle`
`{"op_id": "...", "day": 12, "text": "The colony buried Bram without speeches.", "kind": "day"}`.
One entry of the colony chronicle (the narrator's running history). `day` is an integer day index
on the game clock (0 is the first day; the same count the agent uses for diaries). `kind` is `"day"`
(the usual summary of a day), `"major"` (written at once after a major event) or `"lore"` (a shared
name the narrator coined for a place or event, text `"Name: what it refers to"`; not history: the Voices
window and the recap skip it, the mod keeps at most 12 and drops the oldest); it defaults to `"day"`. The mod trims the text and cuts it at 2,000 characters, appends the entry, and keeps the
newest 120 (older ones are dropped; lore entries count in the 120). `text` empty, `day` not an integer or below 0, or an unknown
`kind` is `bad_request`. Reply `{"stored": true, "count": 37}` (the entries now held). The
chronicle belongs to the game: it is saved with it, so a reload brings back the chronicle as it
was at that save (the agent sees the `save_epoch` change).

### `GET /chronicle?since_day=<N>`
`{"entries": [{"day": 12, "kind": "day", "text": "...", "tick": 734000}, ...]}` (plus the envelope):
the entries whose `day` is at least `N` (default 0), oldest first, in the order stored (kind `lore`
included: readers that want history skip it; the agent reads it back after a `save_epoch` change to
refill its lore cache). `tick` is
the game tick at which the mod received the entry. `since_day` that is not an integer is
`bad_request`. After a `save_epoch` change the agent can read this to see what the loaded save
holds.

### `POST /recap`
`{"op_id": "...", "epoch": 3, "lines": ["Ana dug the first trench.", "...", "..."]}`.
The "Previously on <colony>..." recap shown after a save is loaded. The agent writes it from the loaded
save's chronicle (`GET /chronicle`) once per `save_epoch` change. `lines` is 1 to 3 strings; the mod
collapses whitespace, drops empty lines, cuts each at 160 characters and keeps three. The mod shows
it as a neutral letter titled "Previously on <colony name>..." in the letter stack (never a dialog,
never a pause). It is accepted only when all hold, else it is refused with the code in brackets:
the player's `recap` setting is on (`refused`); `epoch` is the current `save_epoch` (`conflict`);
that epoch began with a loaded save, not a new game (`refused`); no recap was shown yet for this
epoch (`conflict`); and the load was at most 180 s of real time ago (`expired`, 410). `epoch` not an
integer, `lines` not an array of strings or empty after cleaning is `bad_request`. Reply
`{"shown": true}`. An agent that is refused simply drops the recap. The agent also skips the whole
step when `GET /config` says `"recap": false`.
`GET /config` also carries `"recap_open"`: true while a recap would still be accepted (loaded save, none shown
yet, inside the 180 s window, setting on). An agent that connects late (first start, or after a config refresh)
sends the recap for the current epoch when `recap_open` is true and it has not tried that epoch yet.

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

### `POST /social_action`
One small social move chosen by the agent's wants-with-follow-through window (agent/hermesvoice_agent/core/wants.py).
Body: `{op_id, pawn, action: "seek_out"|"avoid", target: <pawn id>, topic?: <short text, <= 80 chars>}`.
Reply `{ok: true, applied: true}` (plus `topic_dropped: true` when the topic was dropped, see below).

`seek_out`: the pawn takes the job `HV_SeekOut` (not player-forced; a current interruptible job resumes afterwards),
walks to chatting distance of the target and starts one normal vanilla interaction (Chitchat, or DeepTalk between two
adults) through `TryInteractWith`, so the existing vanilla-interaction hook voices it. The job gives up after 1500 game
ticks or 60 real seconds, and ends early if the target is despawned, downed, dead, in a mental state or hostile.
The topic is flavour for the agent's own line; the mod does not use it. Never a romance interaction.
The agent's grudge loop (core/grudges.py) also calls this endpoint directly from rules, not through the wants tool loop:
`avoid` at once after an insult, later `seek_out` with topic `apologise`, `make up` or `confront`. Because the mod does
not carry the topic into the chat, the agent keeps the pending reunion itself and, when the resulting `social` event
for that pair arrives, tells both pawns in the conversation trigger what the talk is about. No contract change.

`avoid`: for `wantsAvoidHours` game hours (setting, default 1) no social interaction starts between the pawn and the
target in either direction (a Harmony prefix on `TryInteractWith` answers "no", which also stops the game's random chitchat picking them).
Nobody walks away. The table is saved with the game and expires on game ticks.

Errors: `bad_request` (invalid body); `not_found` (unknown pawn or target); `refused` (HTTP 409, `reason` field and
`detail` carry one of `disabled`, `dead`, `not_spawned`, `other_map`, `downed`, `drafted`, `asleep`, `mental_state`,
`forced_work` (prisoner or slave doing forced work), `in_combat` (enemy target, melee threat, or an active hostile within
30 cells of the pawn, or of the target for seek_out; danger elsewhere on the map does not count), `busy` (player-forced job, a non-interruptible or blocked job, in a lord, resting for medical reasons),
`unreachable` (seek_out only: target not spawned, other map, hostile, or no path)). The agent does not retry a refusal.
`refused` is also the answer when the mod setting "Pawns act on their wants" is off (reason `disabled`); `GET /config`
shows it as `wants_enabled`. An older mod without this route answers 404 and the agent counts it as unsupported.

Age guard (second, mod-side): if either pawn is under 18 (`AgeBiologicalYears`) a given topic is dropped
(`topic_dropped: true`). No action in this op is romantic, and the mod never starts a romance interaction from it.

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
"talk_frequency": 1.0, "wants_enabled": true, "recap": true, "recap_open": false, "adult_content": false, "dreams": true, "art_descriptions": true, "pause_ambient_at_speed": 4, "dlc": ["Ideology", "Biotech", ...],
"llm": {"base_url": "http://host:4480/v1", "model": "gemma", "api_key_set": true}}`.
`adult_content` (bool, default `false`) is the mod's "Adult content" setting. Off: the agent adds a fade-to-black
rule to its system prompt and drops explicit sexual lines (reason `adult_off`). On: explicit content between adults
is allowed. The agent's age gate is never relaxed by it. The agent reads it on every config refresh; a missing key
means off.
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

`age` is the pawn's **biological** age in years (`AgeBiologicalYears`), not chronological. The agent's age gate
(`core/agegate.py`) reads it: under 18 is a minor, and a missing or non-numeric `age` counts as a minor, so
`age` must always be sent. Scene `present[]` entries carry no age; the agent reads it from each pawn's snapshot.

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
  thought, player_talk, art_created, sleep_start, first_cue, gathering, gathering_end`. `quest` has `data.phase` `offered` or `accepted`; `prisoner` has
  `data.phase` `captured`, `released`, `recruited` or `died`; `funeral` has `data.phase`
  `start` or `end`. Ledgers: `raid` (`raid_start`..`raid_end`), `quest` (accepted..`quest_end`),
  `lodger` (`lodger_arrive`..`lodger_leave`), `prisoner`, `funeral`, `weather`
  (`weather_extreme`..`weather_extreme_end`).
- `mental_break` with `data.state` `SocialFighting` also carries `data.target` (the other pawn's id) and lists that pawn
  as an actor with role `target`; the target is a witness-style participant, not a second subject.
- `art_created` fires when a colonist or slave finishes a piece of art (not spawned, traded or
  reinstalled art). `data`: `thing_id` (use with `POST /art`), `def_label`, `quality`, `title`,
  `description` (the game's own tale text, game text, at most 400 characters), `material` (the stuff label, `""` when none) and, when the art depicts a tale, `tale`: `{def, summary (at most 200 characters), day (game day it happened, may be absent), pawns (up to 3 names)}` (all game text; `tale` is left out when there is none or it cannot be read). The maker has role
  `maker` and witnesses it, as does anyone who normally witnesses. Severity is 0.2: the maker and witnesses remember it, but it does not start a spoken reaction. Toggle `events.art`; cooldown
  one game hour per maker.
- `sleep_start` fires when a colonist falls asleep in a bed (not downed, not anesthetized; a colonist first seen already asleep,
  as after a load, does not fire), at most once per pawn per 8 game hours. The sleeper has role `sleeper` and is the only
  witness (the mod lists it explicitly, because sleepers are never witnesses otherwise). Severity 0.1, not chronicle-worthy; the
  agent writes no memory for it, it only may queue one dream. Toggle `events.sleep` (also off when `dreams` is off).
  `GET /pawn/{id}` carries `asleep` (true while the pawn is not awake). The agent answers with `POST /thought` `kind: "dream"`.
- `first_cue` is a signal that a pawn may be seeing something for the first time. `data.cue` is one of
  `snow, eclipse, aurora, solar_flare, corpse, beautiful_room, no_table`. Map-wide cues (`snow`,
  `eclipse`, `aurora`, `solar_flare`) are `AllOnMap` events, sent once per map per session when the
  weather or game condition first shows; `corpse` (vanilla thoughts ObservedLayingCorpse and
  ObservedLayingRottingCorpse) and `no_table` (AteWithoutTable) go to that pawn only, even though their mood
  offset is below the `thought` floor; `beautiful_room` goes to an awake colonist standing in an indoor room
  of impressiveness 65 or more. The mod sends each (pawn or map, cue) once per session; the agent keeps the
  lasting record of who has had which first, so a repeat costs nothing. Severity 0.4. Toggle `events.first_cue`.
- `gathering` (`data.phase` `start`) and `gathering_end` (`data.phase` `end`, the ledger entry closes) announce people
  doing something together. `data`: `phase`, `kind` (`meal`, `party`, `wedding`, `funeral`, `speech`, `ritual`), `key` (stable id of this
  gathering, the same in start and end: `lord:<id>` or `meal:room<id>`), and, when known, `lead` (pawn id: party organizer,
  ritual officiant or leader, speaker), `label` (short game text: party, ritual or speech name; `shared meal`), `room` (room role label,
  meals), `ideo` (precept or ritual name, rituals and speeches), `deceased_name` (funerals), `couple` (`[id, id]`, weddings).
  `pawns`: every free colonist or slave of the player faction taking part with role `participant`; the lead has role `lead`; the two
  of a wedding have role `couple`; at a funeral the deceased, when still a Pawn (even as a corpse), has role `subject` and is not a
  participant (`deceased_name` is always set). Prisoners, guests and lodgers are never listed. `witnesses` are the participants, set
  explicitly (not by room or hearing), so a sleeper or someone out of the room is not one. At least 2 participants (meals: 3). Severity
  0.45 for meals and parties, 0.6 for weddings, funerals, rituals and speeches. Sources: `LordJob_Joinable_Party`,
  `LordJob_Joinable_MarriageCeremony`, `LordJob_Ritual` (Ideology; `funeral` when the precept is Funeral or FuneralNoCorpse, `speech` for
  `LordJob_Joinable_Speech`, which also carries the Royalty throne speech, else `ritual`), `LordJob_BestowingCeremony` (Royalty, kind `speech`,
  lead the bestower), and a meal poll every 120 ticks: 3 or more awake colonists with the Ingest job in one indoor room (pawns already in a
  party, wedding or ritual lord are not counted), at most one meal per room per 20000 ticks, ending when fewer than 2 still eat there or after 2500
  ticks. Lord gatherings start once 2 participants are present and end when the lord goes. Ledger `gathering`
  (`gathering`..`gathering_end`). Toggle `events.gathering`. Funeral overlap: the older `funeral` event and ledger are still sent
  as before; a funeral also produces a `gathering` of kind `funeral` alongside it, so a consumer that handles gatherings should skip the
  `funeral` event's reaction while `events.gathering` is on, to avoid two lines for one funeral.
- A bystander's cloud over an insult or slight is an ordinary `POST /thought` with `kind: "thought"` and a
  `surface`; the mod needs nothing new. The agent works out who heard a `/say` itself, from the `present`
  list of `GET /scene/{id}` and hearing range.
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

## Speech balloons and thought clouds (`kind`, `surface`)

How the RimWorld mod presents Part 1's `kind` on `POST /say` and `surface` on `POST /thought`
(comic bubble style; the classic style ignores both and shows no thought clouds).

- `POST /say` `kind` picks the balloon: `normal` is the round balloon, `shout` the spiked burst,
  `whisper` the dashed balloon in the small font. `normal` stops the mod from guessing a shout.
  When `kind` is missing or unknown the mod falls back to its heuristics, in this order: text markers
  (`*..*` or `(..)` thought, `[..]` caption), the intent (`insult` shout; `confide`, `gossip`
  whisper), then the text (two or more `!`, mostly capitals, or one `!` with `combat`).
  The agent also bounds shouting: a shout is kept only with combat, an `insult` or a major threat
  event, and a speaker's next shout within `shout_gap_real_s` (default 45 s) is sent as `normal`.
- `POST /thought` with `kind: "thought"` also shows a cloud balloon over the pawn when the setting
  "Show pawn thoughts on the map" is on (default on). The cloud shows `surface`, or `text` when
  `text` is at most 90 characters; longer text with no `surface` shows nothing. A `diary` never
  shows on the map. The cloud is drawn at 60% of the speech balloon's opacity (setting).
- Pacing (settings, defaults): one cloud per pawn at a time; at least 120 s between clouds over one
  pawn; at least 20 s between clouds anywhere. A cloud is never drawn over a pawn whose speech
  balloon is visible: it waits (one per pawn) and is dropped when it is more than 10 s old. A speech
  balloon replaces the pawn's cloud.

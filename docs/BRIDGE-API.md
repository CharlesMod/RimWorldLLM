# RimWorldLLM bridge API v2 (contract)

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
- On start, the mod writes **`<RimWorld config dir>/RimWorldLLM/bridge.json`**, and rewrites it
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
The envelope plus `{"mod": "RimWorldLLM", "version": "2.0.0", "game_loaded": true}`.
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
- Reply: `{"applied": {"bubble": true, "log": true, "effect": "comfort", "thoughts": ["RWL_Comforted"]}}`,
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

### `GET /text/queue?n=<N>`
Per-instance texts the game wants written (docs/TEXT-POOLS.md): one artwork, grave, book or log line each, queued when
the thing was made (or first shown), with a context snapshot taken then. `n` is 1-8 (default 4; more is cut to 8;
not a positive integer is `bad_request`). Reply:
`{"pending": 17, "items": [{"id": "art:4211", "kind": "art", "prio": 3, "fields": ["title", "text"], "min_len": 40,
"max_len": 500, "title_max": 60, "tokens": [], "about": "an artwork's title and the description ...", "example": "This
sculpture depicts ...", "example_title": "Blood and Grain", "context": {"piece": "small sculpture", "quality":
"excellent", "material": "marble", "tale_depicted": {...}, "maker": {"name": "Rook", "mood": "31%", "traits": [...],
"artistic_skill": 11, ...}, "nearby": ["Mira"], "colony_now": {"date": "...", "weather": "foggy rain", "recent":
[...]}}}]}`. Items come most important first (priority by kind: art, grave, book 3; social 1; combat 0; then oldest).
`id` is opaque: echo it back. `fields` is `["text"]` or `["title", "text"]` (`title_max` and `example_title` only then).
`tokens` are the `[TOKEN]`s the text may carry (`INITIATOR`, `RECIPIENT` for log lines; the game puts the names in when
it shows the line); no other square brackets are allowed. `example` is the game's own text for this very instance (empty
for graves: the game has none). `context` is free-form JSON (strings, numbers, lists, objects); `minor_involved: true`
marks an item that involves someone under 18 (the text must be all-ages). Each listing counts as one hand-out: an item
handed out three times without a fill leaves the queue, as does one older than two game days. Only switched-on kinds are
listed. An older mod answers 404.

### `POST /text/fill`
`{"op_id": "...", "id": "art:4211", "text": "This marble sculpture ...", "title": "A Gastric Misfortune"}`. Stores the
text for one queued item; from then on the game shows it for that instance (saved with the game). `title` only for kinds
whose `fields` include it (optional there; without it the game's title stays). Validation: whitespace collapsed and
wrapping quotes stripped; the text within `min_len`..`max_len` and the title within `title_max` characters; no `<`,
`>`, `{`, `}`, `(*`, `(/`; square brackets only as the item's tokens. Reply `{"stored": true, "applied": true, "text":
"...", "title": "..."}` (`text` and `title` as stored; `applied` false when the instance was not reachable now: the
game puts it on when the instance is next shown). Errors: `id` or `text` missing is `bad_request`; an id not in the queue
(never queued, expired, dropped, already filled, or its kind switched off) is `unknown_id` (409); a text that fails
validation is `bad_text` (400) with `detail` naming the rule (`text: too_long`, `title: markup` ...). An older mod answers
404.

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
One small social move chosen by the agent's wants-with-follow-through window (agent/rimworldllm_agent/core/wants.py).
Body: `{op_id, pawn, action: "seek_out"|"avoid", target: <pawn id>, topic?: <short text, <= 80 chars>}`.
Reply `{ok: true, applied: true}` (plus `topic_dropped: true` when the topic was dropped, see below).

`seek_out`: the pawn takes the job `RWL_SeekOut` (not player-forced; a current interruptible job resumes afterwards),
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
`{"effects": "normal"|"light"|"off", "events": {"injury": true, ...},
"talk_frequency": 1.0, "wants_enabled": true, "recap": true, "recap_open": false, "adult_content": false, "dreams": true, "art_descriptions": true, "text_fill": true, "pause_ambient_at_speed": 4, "dlc": ["Ideology", "Biotech", ...],
"llm": {"base_url": "http://host:4480/v1", "model": "gemma", "api_key_set": true}}`.
`adult_content` (bool, default `false`) is the mod's "Adult content" setting. Off: the agent adds a fade-to-black
rule to its system prompt and drops explicit sexual lines (reason `adult_off`). On: explicit content between adults
is allowed. The agent's age gate is never relaxed by it. The agent reads it on every config refresh; a missing key
means off.
`text_fill` (bool) is the mod's "Write flavour text for single things" switch (and the mod switch itself): false means
the agent's text filler stays idle. Missing (an older mod) means the agent decides from `GET /text/queue` alone.
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
  thought, player_talk, art_created, sleep_start, first_cue, gathering, gathering_end, birth, masterwork, ship_part`. `quest` has `data.phase` `offered` or `accepted`; `prisoner` has
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
- `birth` (Biotech only; hook `PregnancyUtility.ApplyBirthOutcome`) fires when a player-faction pawn gives birth. `data`: `pawn` `{id, name}` (the mother, also actor role `mother`). Severity 0.6.
- `masterwork` fires when a player pawn finishes a Masterwork or Legendary item (hook `QualityUtility.SendCraftNotification`). `data`: `thing` (thing id), `label`, `quality` (`masterwork` or `legendary`), `maker` `{id, name}` (also actor role `maker`). Severity 0.5.
- `ship_part` fires when a ship part building (a def with `CompProperties_ShipPart`) is completed from its frame. `data`: `thing` (def name), `label`. Everyone on the map knows. Severity 0.7.
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
  The mod's own `RWL_` thoughts never produce one.
- Per-event toggles in settings apply to the ring as well as to the push. Events are throttled
  by per-pawn cooldowns (injury 30 s, social 60 s, tend 20 s, mental break and thought 10 s).
  The 20 per real minute cap applies only to the push to the agent URL, not to `/events`.
- After a game load, lodgers, prisoners and quests already present are learned silently (no
  arrive events); raids in progress are saved with the game and still end with `raid_end`.

## Diaries and the chronicle in the RimWorld mod

- Diary entries (`POST /thought` with `kind: "diary"`) and inner thoughts are saved per pawn with
  the game, as part of the pawn's mind record, and are shown in the pawn's Mind tab: the Diary
  section lists the last 7 entries, newest first, each with the game date of its arrival.
- The chronicle is saved with the game in the mod's game component (`rwlChronicle`), so loading
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

# Vivarium routes (the narrator plays)

Design: `docs/VIVARIUM.md`. `/vivarium`, `/chat`, `/goals` and `/actions` are game-agnostic
(`Mod/Source/Bridge/Vivarium*.cs`); `/digest`, `/act/*` and the `letter`/`alert` events are the
RimWorld schema (`Mod/Source/RimWorld/Vivarium*.cs`). All replies carry the usual envelope.

### `GET /vivarium` (no game needed)
```json
{"playing": true, "paused_by_narrator": false, "chat_seq": 41, "goals_rev": 7, "actions_seq": 19,
 "undoable": 0, "combat": {"active": false, "hostiles": 0, "since_tick": -1},
 "settings": {"may_pause_for_emergencies": true, "controls_speed": false, "slow_for_combat": true, "heartbeat_hours": 6.0}}
```
The steward polls this every director step (cheap) to see the switch, new chat and settings.

### `GET /chat?since=<seq>` and `POST /chat`
GET returns `{"entries": [...], "next_seq": 42}` with the entries after `since` (oldest first; at most
the 600 kept). Entry: `{"seq": 42, "tick": 123456, "who": "player", "to": 0, "name": "", "text": "...",
"listener": "Bram"}` (`listener` only on colonist lines that had one). Kinds (`who`, `Mod/Source/Bridge/ChatKinds.cs`):
- `player` (typed in the chat bar; `to`/`name` = the `@Name` colonist, else 0/""), `narrator` (the
  steward's lines), `note` (system or model-written notes);
- `colonist`: every successful `/say` line (`to`/`name` = the speaker, `listener` = the addressee's
  label); a reply to an `@Name` line is this same entry, written once;
- `thought`, `dream`, `diary`: from `/thought` (`to`/`name` = the pawn; diary text shortened to ~160 chars);
- `chronicle` (`name` = "Day N"), `recap` (the letter's lines joined, ~480 chars), `art`
  (`Art: "Title" by Ana: description`, ~160 chars, `name` = the artist);
- `action`: one per successful `/act/*`: the hand's plain-English note plus ", since <why>"
  (no leading "> ": the panel adds it); `refused`: one per refused `/act/*`: "couldn't <what>: <reason>".
Only `player` lines with `to` == 0 are for the steward to answer; it ignores every other kind and the
`@Name` lines (those went through `/talk`).

POST body: `{"op_id": "...", "who": "narrator"|"note", "text": "...", "stream_id": "..."}` (text 1..500
chars after trim; `stream_id` optional). Reply `{"seq": 43}`. Any other `who` is `bad_request`: only the
game's chat bar writes `player` lines. With `stream_id` the line is the final text of that live stream
(below): the line becomes permanent and the live line goes. An unknown or already-ended `stream_id` is
fine (the line still posts); a malformed one is `bad_request`.

### `POST /chat/status` and `POST /chat/partial` (live "is thinking / is typing"; no game needed)
The chat panel shows one live line per speaker under the log: "Narrator is thinking…" while the prompt
is processed (prefill), then "Narrator is typing…" and the partial text as it streams, replaced by the
final line. Live state is never saved and a load clears it.

`POST /chat/status`: `{"op_id", "who": "narrator"|"colonist", "to": <colonist id>, "name": "Ana",
"state": "thinking"|"typing"|"idle", "stream_id": "...", "progress": 0.74}`
- `stream_id`: 1..64 chars of `A-Z a-z 0-9 _ . : -`, unique per stream (one model request). A new
  stream for the same speaker replaces the old one (the old id counts as ended).
- `who`: `narrator` (no `to`), or `colonist` with `to` = the colonist's thing id (> 0); `name` is
  optional (the panel looks the colonist up by id when it is missing). `who` is not needed for `idle`.
- `state`: `thinking` starts (or refreshes) the stream; `typing` marks the first token; `idle` ends
  the stream with no line (an error, a cancelled answer, or a tool-only cycle). `idle` for an unknown
  id is fine.
- `progress` (optional, with `thinking`): prefill progress 0..1, e.g. `processed / total` from the
  model server's `prompt_progress` chunks; the panel shows "… 74%" and a thin bar. Without it the
  panel shows plain animated dots (a player's own endpoint may not report progress). Ignored once
  typing. Outside 0..1 or not a number is `bad_request`.
- Reply `{"stream_id": "...", "state": "...", "live": true}`; `live` false means the id was already
  ended (a late update is ignored, not an error).

`POST /chat/partial`: `{"op_id", "stream_id", "text": "the whole text so far"}` (not a delta; at most
500 chars). Idempotent: the same text twice changes nothing. Also counts as `typing`. Reply
`{"stream_id": "...", "accepted": true}`; `accepted: false` when the stream is unknown or ended (a
partial after the final is ignored). The mod keeps the latest text but redraws it at most 4 times a
second, so a client may post every token, though every 100-250 ms is plenty.

A stream with no status or partial for 90 s disappears by itself, so a crashed client never leaves a
status stuck. A long prefill should repeat `thinking` (with fresh `progress`) at least every 60 s.

**The contract a streaming client follows** (one model request = one stream):
1. `POST /chat/status` `state: thinking` with a new `stream_id` when the request is sent; then, if the
   server reports prefill progress, `thinking` again with `progress` (at most a few times a second).
2. At the first token: `state: typing` (or straight to the first `/chat/partial`).
3. `POST /chat/partial` with the whole text so far as tokens arrive (throttled to ~4-10 per second).
4. Done: `POST /chat` with the final text and the same `stream_id` (narrator lines), or, for a colonist
   reply that goes through `/say`, `state: idle`. On an error or an empty answer: `state: idle`.
An older mod answers these routes `not_found`; a client treats that as "no live status" and carries on.

### `GET /goals` and `PUT /goals`
```json
{"rev": 7,
 "goals": [{"id": "g3", "text": "Get a freezer before summer", "horizon": "short", "status": "open", "by": "narrator"}],
 "plan": "Stockpile food, then research electricity.",
 "orders": [{"id": "o1", "text": "always keep 20 meals", "by": "player"}],
 "log": [{"tick": 120000, "text": "Set Cooking 1 for Ana: no cook on duty"}]}
```
`horizon`: `long` | `short`. `status`: `open` | `done` | `dropped`. `by`: `player` | `narrator`.
At most 20 goals, 20 orders, 50 log lines (oldest dropped); texts 1..300 chars.
PUT body: `{"op_id": "...", "rev": 7, "goals": [...], "plan": "...", "orders": [...], "log_add": ["..."]}`.
Every field but `rev` is optional; a given field replaces that part (`log_add` appends). Goals and
orders without an `id` get a new one; `by` defaults to `narrator` for new items and is kept for
existing ids. A stale `rev` is `409 conflict` with the current board under `board`; reply on success
is the new board.

### `GET /actions?since=<seq>`
`{"actions": [{"seq": 19, "tick": 123456, "verb": "work", "note": "set Ana's Cooking to 1st", "by": "narrator", "why": "since meals are running low"}], "next_seq": 20}`.
Every successful `/act/*` call is logged (cap 500, saved with the game). `by` is `narrator`; `why` only when the act carried one.

### `GET /digest` (game needed)
A compact colony read for the steward: only what the player could see in the UI. Lists are capped
(top N) so the rendered prompt stays near 6 KB.
```json
{"date": {"day": 5, "quadrum": "Aprimay", "year": 5500, "hour": 14, "season": "spring"},
 "weather": "Rain", "temperature": 12.5, "map": "Ferngrove",
 "time": {"paused": false, "speed": 2},
 "food_days": 6.2,
 "resources": [{"category": "food", "label": "simple meal", "count": 14}, {"category": "materials", "label": "steel", "count": 410}],
 "pawns": [{"id": 101, "name": "Ana", "role": "colonist", "age": 31,
            "skills": [{"skill": "Cooking", "level": 9, "passion": "major"}],
            "incapable": ["Violent"],
            "work": {"Cooking": 1, "Hauling": 3},
            "health": "fine", "downed": false, "mood": 64, "mental": "",
            "job": "Cooking simple meal", "drafted": false,
            "schedule_now": "work", "schedule": "SSSSSSWWWWWWWWWWWWWWJJSS",
            "outfit": "Anything", "food": "Lavish", "drug": "Social drugs", "bed": 3301}],
 "work_types": ["Firefighter", "Patient", "Doctor", "Cooking", "Hunting", "Construction", "Growing", "Mining", "PlantCutting", "Smithing", "Tailoring", "Art", "Crafting", "Hauling", "Cleaning", "Research"],
 "manual_priorities": true,
 "policies": {"outfit": ["Anything", "Worker"], "food": ["Lavish", "Fine"], "drug": ["Social drugs", "No drugs"]},
 "beds": [{"id": 3301, "label": "bed", "room": "Bedroom", "owner": "Ana"}],
 "alerts": [{"alert": "Alert_LowFood", "label": "Low food", "critical": false}],
 "letters": [{"id": 77, "label": "Quest: Ancient danger", "text": "...", "choices": ["Accept", "Reject"], "quest": true, "expires_in_hours": 30}],
 "research": {"current": {"def": "Electricity", "label": "electricity", "progress": 0.4},
              "available": [{"def": "Batteries", "label": "batteries", "cost": 700}]},
 "threats": [{"label": "Pirate raider", "count": 5, "hostile": true}],
 "rooms": {"bedrooms": 3, "barracks": 0, "hospital": false, "freezer": false},
 "power": {"produced": 1200, "used": 800, "stored": 0}}
```
- `schedule` has one letter per hour 0..23: `A` anything, `W` work, `J` joy, `S` sleep, `M` meditate.
- `work` lists only non-zero priorities (1 = highest). `work_types` are defNames in the game's order.
- Caps: 30 pawns (colonists, then slaves), 4 skills per pawn (highest), 25 resources, 15 research
  options, 10 alerts, 10 letters, 20 beds.
- `facts`: a flat block named exactly as the playbook's `digest_fields`
  (`agent/rimworldllm_agent/games/rimworld/strategy/rules.json`), for the agent's rule evaluation; the agent
  does not render it into the prompt. Filled (a key is left out when it cannot be computed): `day` (days
  since landing), `season` (spring/summer/fall/winter), `biome` (snake_case defName), `temp_c`, `storyteller`,
  `difficulty_threat_scale`, `wealth`, `food_days`, `dlc`; `colonists` (free adult colonists spawned),
  `colonists_capable_fighters`, `colonists_armed`, `colonists_downed`, `colonists_bleeding`, `colonists_sick`,
  `colonists_below_minor`, `colonists_below_major`, `mental_breaks_active`, `mood_avg`, `mood_min`,
  `recreation_avg`, `colonists_without_bedroom`, `best_cook_skill`, `best_doctor_skill`, `doctors_capable`;
  `prisoners`, `animals`, `beds_free`, `prisoner_beds_free`, `hospital_beds`; `raid_active`, `enemies_count`,
  `turrets`, `events` (active game conditions, snake_case), `fires`; `power_net_w`, `battery_wd`,
  `battery_capacity_wd`, `unroofed_electrics`; `medicine_total`, `medicine_industrial`, `steel`, `wood`,
  `components`, `silver`; `research_current`, `research_done` (finished defNames) and `research_known`
  (every ResearchProjectDef defName in this game: a playbook rule naming research the game lacks stays off).

### `POST /act/*` (game needed)
Normal writes (`op_id`, optional `expires_tick`). Shape errors are `400 bad_request`; anything the game
or the settings refuse is `409 refused` with `detail` = a short human reason. Success:
`{"done": "<short note>", ...extra}` and an action-log entry. The note is plain English, past tense, read
by the player as the greentext line ("set Ana's Cooking to 1st and Hauling to 3rd", "turned off Ana's
Hauling", "drafted Bram", "released Bram from the draft", "started researching Batteries", "paused the
game", "set the game speed to 2", "put Ana on sleep from 22:00 to 6:00", "gave Ana the bed Bed (3301)",
"had Ana wear the Worker outfit", "answered \"Title\" with Accept", "sent Ana to (40, 61)").

Every route takes an optional **`why`** (string; cleaned, cut at a word to 80 chars, trailing
punctuation dropped; a non-string is `400`). It is stored on the action-log entry and written into the
chat: success → an `action` entry `<note>, <why>` when `why` starts with since/because/as/so/to/for/after/
while, else `<note>, since <why>`; refusal (gate or hand) → a `refused` entry `couldn't <what>: <reason>`,
where `<what>` is the hand's account of what it tried, or a phrase from the arguments when the hand never
ran. A `400` or `no_game` writes nothing.

| Route | Body | Checks before the game is asked |
| --- | --- | --- |
| `/act/time` | `{"pause": true?, "speed": 1..4?, "emergency": false, "why": "..."}` | at least one of pause/speed. `speed` needs **controls_speed**. `pause: true` needs **controls_speed**, or `emergency: true` with **may_pause_for_emergencies** (then `paused_by_narrator` is set). `pause: false` needs **controls_speed** or `paused_by_narrator`. |
| `/act/work` | `{"pawn": 101, "priorities": {"Cooking": 1, "Hauling": 0}}` | 1..20 entries, each priority 0..4 |
| `/act/schedule` | `{"pawn": 101, "from_hour": 22, "to_hour": 6, "assignment": "sleep"}` | hours 0..23 / 0..24, assignment in anything/work/joy/sleep/meditate |
| `/act/policy` | `{"pawn": 101, "kind": "food", "name": "Fine"}` | kind in outfit/food/drug, name 1..60 chars |
| `/act/bed` | `{"pawn": 101, "bed": 3301}` | ids positive |
| `/act/research` | `{"project": "Batteries"}` | 1..80 chars |
| `/act/letter` | `{"letter": 77, "choice": "Accept"}` | choice 1..80 chars: a label, `#<index>`, or `dismiss` |
| `/act/draft` | `{"pawns": [101, 102], "drafted": true}` | 1..30 positive ids |
| `/act/order` | `{"pawn": 101, "order": "move", "x": 40, "z": 61}` / `{"order": "attack", "target": 5512}` / `{"order": "hold", "hold": true}` | order known; move needs x,z ≥ 0; attack needs target > 0 |

Details of the phase 1 implementation (Mod/Source/Bridge/VivariumRoutes.cs):
- `next_seq` on `/chat` and `/actions` is the last seq returned, used as the next `since` (as on `/events`);
  a `since` past the end is pulled back.
- `PUT /goals` is all-or-nothing: an over-cap list, an over-long text or a bad horizon/status is `400` and
  changes nothing. Ids are never reused.
- `/act/*` checks in this order: shape (`400`), no game (`503 no_game`), the time gate (`409 refused`), then the
  hand. With both `pause` and `speed` given, both must pass the gate. `paused_by_narrator` is set by any
  successful `pause: true` and cleared by any successful `pause: false`.
- `/act/schedule`: `from_hour == to_hour` is `400`; `0` to `24` is the whole day.
- `/act/order`: `hold` needs a boolean `hold`. Vanilla has no "hold position" order, so hold = stop the pawn's
  current job and turn fire-at-will off; `hold: false` turns fire-at-will back on. Orders need a drafted pawn.
- `/act/work` may partly succeed: the note says what was set and what was refused; it is a refusal only when
  nothing was set. It turns on manual work priorities when they were off, and says so.
- `/act/letter` runs the chosen option's action, as the letter dialog would; an option with no action (one that
  only opens a window) is refused; `dismiss` works only on letters vanilla lets you right-click away.

### Phase 2: the builder's hands (game needed except `GET /vivarium/undo`)
Same rules as the other `/act/*` (op_id, `400` shape, `503 no_game`, `409 refused` with a reason, `{"done": ...}`).
A hand that placed something also returns `undo_seq` (its entry in the narrator's undo ledger) and `left` (what
this cycle's caps still allow: `{"blueprints", "cells", "designations", "bills", "max": {...}}`). Full table, marks
and caps: docs/VIVARIUM.md "Phase 2".

| Route | Example body | Checks before the game is asked |
| --- | --- | --- |
| `/act/designate` | `{"kind": "mine", "ore": "steel", "max": 30}` / `{"kind": "chop", "x": 40, "z": 61, "w": 6, "h": 4}` / `{"kind": "haul", "x": 40, "z": 61, "radius": 8}` / `{"kind": "deconstruct", "target": 5512, "player_asked": true}` | kind known; x,z both or neither (0..1000); w,h need a corner; radius 1..40 needs a centre; `ore` only for mine; deconstruct/cancel need a box, radius or target; max 1..200; `player_asked` needs a player chat line to the narrator in the last 2500 ticks; designations left (cancel is never capped) |
| `/act/build` | `{"thing": "stove", "stuff": "steel", "room": 12}` / `{"thing": "Wall", "x": 40, "z": 61, "rot": "north"}` | thing 1..60 chars; rot 0..3 or north/east/south/west; a cell or a room, not both; blueprints left |
| `/act/room` | `{"purpose": "bedroom", "size": "medium", "stuff": "granite", "door": "auto"}` | purpose and size known; w,h 3..11 both or neither; door n/e/s/w/auto; no room id; blueprints left ≥ the room's wall count |
| `/act/zone` | `{"kind": "grow", "plant": "rice", "size": "large"}` / `{"kind": "stockpile", "preset": "food", "room": 12, "priority": "important"}` | kind known; grow needs plant (and only grow takes one); preset, priority, size known; w,h 1..20; a corner or a room, not both; cells left ≥ w·h (a room's floor is capped by the hand) |
| `/act/area` | `{"kind": "allowed", "name": "Inside", "op": "add", "x": 30, "z": 30, "w": 20, "h": 15, "assign": [101]}` | kind home/allowed; op add/remove; a full box; allowed needs a name; assign only for allowed, ≤ 30 ids; cells left ≥ the box |
| `/act/bill` | `{"op": "add", "bench": 4410, "recipe": "simple meal", "until": 20}` / `{"op": "suspend", "bench": 4410, "index": 1}` | op known; bench id; add needs recipe; others need index (1..30) or recipe; count 1..999 or until 1..9999, not both, only for add; radius 3..999; bills left (add) |
| `GET /benches` | | `{"benches": [{"id", "label", "room", "bills": [{"index", "recipe", "mode", "suspended"}], "recipes": [...]}]}` |
| `GET /vivarium/undo` | | `{"count": 3, "newest_first": [{"seq", "tick", "verb", "note", "marks"}]}` (10 newest) |
| `POST /vivarium/undo` | `{"n": 1}` | n 1..20; refused when the ledger is empty. Reply `{"done": "undid 1 narrator action: cancelled 15 blueprints", "undone": [notes], "undoable": 2}`; logged as action `undo` by `player`. |

`GET /vivarium` also carries `undoable` (the ledger's size). The ledger is saved with the vivarium state
(`ledger` in the save JSON); the caps are not saved. The digest gains a `build` block (docs/VIVARIUM.md).

### Phase 3: combat and defense (game needed)
Full shapes and behaviour: docs/VIVARIUM.md "Phase 3".

| Route | Example body | Checks before the game is asked |
| --- | --- | --- |
| `GET /combat` | | `{"active", "home", "threats": [{"id", "faction", "kind", "count", "where", "dist", "heading", "labels", "ids", "dormant"?}], "fighters", "others", "downed": {"colonists", "enemies"}, "fires": {"count", "home", "where"?}, "defenses", "positions", "squad", "speed_rule": {"slowed", "before"}}` |
| `/act/squad` | `{"op": "move", "at": "chokepoint"}` / `{"op": "focus", "target": 9201, "pawns": [101, 102]}` / `{"op": "retreat", "below": 0.4}` / `{"op": "undraft"}` | op in draft, move, hold, fire_at_will, focus, retreat, rescue, extinguish, flee, undraft; pawns ≤ 30 positive ids (duplicates dropped); at in cover, chokepoint, door, wall_n/e/s/w, home, inside; at or x,z, not both; move needs one of them; retreat defaults to at inside; room only with at door/inside; focus needs target; below 0.1..0.6. No caps. Reply adds `squad` (and `spots` for move/retreat). |
| `/act/defend` | `{"kind": "sandbags", "side": "n", "length": 7}` / `{"kind": "turret", "count": 2}` / `{"kind": "killbox", "at": "entrance"}` / `{"kind": "traps", "side": "threat"}` | kind known; side n/e/s/w/threat; at chokepoint/entrance; at or x,z, not both; count turret 1..4, traps 1..8; length 1..20; a killbox is not at a chokepoint; blueprints left. Placed things are `blueprint` marks: `undo_seq`, `left`, `/undo`. |
| `/act/time` (fight) | `{"speed": 1, "combat": true}` / `{"combat": true, "restore": true}` | combat takes speed 1 or restore, never pause; slow needs **slow_for_combat** or **controls_speed** (the game remembers the old speed, reply `before`); restore needs a remembered speed and the game still at 1x. |

`GET /vivarium` gains `"combat": {"active", "hostiles", "since_tick"}` and `settings.slow_for_combat`. The squad,
the remembered speed and the last raid side are saved in the vivarium JSON (`combat`). The digest gains `combat`
(the `GET /combat` object) while a fight is active.

### Events `letter` and `alert`
`letter`: `{"type": "letter", "severity": 0.6, "data": {"letter": 77, "label": "...", "choices": ["Accept", "Reject"], "quest": true}}`
(severity 0.3 when the letter has no choice). `alert`: `{"type": "alert", "severity": 0.8,
"data": {"alert": "Alert_Fire", "label": "Fire", "critical": true}}`, published when an alert first
becomes active (polled every 250 ticks; critical alerts and a short list: fire, starvation, low food,
colonist needs rescue, colonist needs tending, life-threatening illness). Both have empty `pawns`; the
director does not voice them.

### `GET /dialogs` and `POST /act/choose` (unattended play; game needed for the act)
`GET /dialogs` (no game needed) lists the choice windows and choice letters open now, as the mod's scanner last saw
them (a few times a second, also while a modal window has the game paused):
`{"dialogs": [{"id": "L4021", "kind": "joiner", "title": "Refugee", "text": "...(<=300)", "options": [{"index": 0,
"label": "Accept the refugee", "disabled": false, "risk": "costs silver"}], "fields": [{"name": "faction", "label":
"Faction name", "max_len": 40}], "default": "Accept the refugee", "timeout_s": 90, "open_s": 12, "pauses_game": false}],
"timeout_s": 90}`. `id` is `L<letter id>` for letters and `W<n>` for windows. `kind` is one of joiner, creep_joiner,
visitors, betray_visitors, ransom, choose_pawn, baby_to_child, growth_moment, quest, letter, node_tree, message_box,
naming. `disabled_reason` appears on a disabled option, `risk` (a few words) on a costly or destructive one. Naming
dialogs have `fields` and no options. `default` is what the safe default would pick. `open_s` is real seconds open.
The same list is the digest's `dialogs` array (omitted when none are open).
A `growth_moment` (Biotech birthday letter) also carries `growth`: `{"child": "Mia", "age": "7", "skills": "Shooting 2*, Social 3",
"traits": "Kind", "passions": ["Shooting", "Melee"], "max_passions": 2, "trait_choices": [{"label": "Tough", "description":
"Takes 25% less damage."}]}`. Its one `option` (0) still means "take the first choices".

`POST /act/choose` `{"dialog": "L4021", "option": 0 | "Accept the refugee" | "#0", "why": "since we have room"}` or, for
naming, `{"dialog": "W3", "text": "Ferngrove"}` / `{"dialog": "W3", "text": {"faction": "The Lanterns", "settlement":
"Ashford"}}`. Checks: `dialog` 1..64 chars; `option` an integer or a string, or `text` a string or an object of strings (each
1..60 chars; cut to the field's max_len); at least one of them (`400`). Then the usual `/act/*` path: `409 refused` with a
reason when the dialog is gone, the option is disabled or unknown, a choice dialog got `text` or a naming dialog got only
an `option`, or the game refused. A growth moment is answered with `{"dialog": "L5120", "passions": ["Shooting", "Melee"],
"trait": "Tough", "why": "since she likes guns"}` (not with `option`: both is `400`): `passions` 1..`max_passions` distinct offered skill
names, `trait` one offered label (case-insensitive); `409 refused` with the reason when too many, unknown, repeated or
missing, or the dialog is not a growth moment. The timeout default is unchanged: the first passions and the first trait. A success is an action (`choose`) and a greentext line. A name the dialog
rejects (too long, already taken) is refused with the reason.

### Event `dialog`
`{"type": "dialog", "severity": 0.6, "data": {"dialog": "L4021", "kind": "joiner", "title": "Refugee", "options":
["Accept the refugee", "Reject"], "default": "Accept the refugee", "timeout_s": 90}}`, published once when a dialog is first
seen. `pawns` is empty; the director does not voice it. The steward wakes on it even while the game is paused.

### Event `colony_lost`
`{"type": "colony_lost", "severity": 1.0, "data": {}}`, published once when the colony is gone (the game's
game-ending flag, or three polls in a row with no free colonist on a player map and none in a caravan, after
tick 2500). It is re-armed by a new game or load. Empty `pawns`. The mod then follows the "When the colony is
lost" setting (docs/VIVARIUM.md, "Unattended play: stability and restart"); it never deletes a save.

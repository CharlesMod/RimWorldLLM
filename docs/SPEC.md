# RimWorldLLM 2 — game-side design spec

Status: draft 1, 2026-11-24. Scope: the RimWorld side (mod, agent behaviour, how the model's
slots are used). The one-click engine and installer are a separate spec, written later.
Evidence for the claims here is in the research report (`docs/research/landscape-2026-11.md`).

## 1. What we are building

Colonists who each have a mind of their own: a voice you can tell apart, a memory of what they
lived through, opinions that their conversations actually change, and a private inner life the
player can look into. The model never plays the colony and never invents events. It remembers
and reacts to what RimWorld simulates, and feeds a small, capped set of social effects back in.

Goals, in priority order:

1. **Voice** — pawns sound different from each other and plain, not flowery. "Voice" here
   always means writing voice. There is no audio: everything is text in speech bubbles and logs.
2. **Grounding** — lines use what is true right now and never contradict the game.
3. **Consequence** — what is said changes moods and opinions, within limits the mod owns.
4. **Inner and outer life** — each pawn carries memories, wants and secrets across the whole save.
5. **Speed** — a line lands while the moment is still on screen; the game never waits.

Out of scope for this spec (the fence against scope creep):

- The engine, installer, model download and VRAM balancing.
- The model taking colony actions: jobs, work priorities, drafting, incidents, recruiting.
- An LLM storyteller that picks events.
- Text-to-speech and speech-to-text.
- Faction leaders, traders and comms-console diplomacy.
- Fine-tuning.

## 2. What the field does well, and what we take

Base mods:

| Mod | What it does well | What we take |
|---|---|---|
| RimTalk (142k subs) | Wide triggers (social, thoughts, battle, sleep, letters). Full pawn context with every category toggleable. Requests expire by type and urgent ones preempt. Stale callbacks are dropped. History keeps the trigger beside each past line and strips ambient data. In-game API log with edit and resend. Hook API that 100+ add-ons use. | Trigger breadth, expiry classes, preemption, the two history rules, the API log, a hook API. |
| RimMind-Dialogue | Output forced into one structured call (speech + mood tag + opinion change). Sanitizer so broken output never reaches a bubble. Closed mood tags with sizes clamped in C#. Cadence curve per pair, daily pair quotas, duplicate requests merged. Short lines (15–40 characters), "no polite positivity when suffering". | Closed effect set with C# magnitudes, the sanitizer, pair quotas and merging, the short-line and anti-positivity rules. |
| RimGPT | A narrator that skips its turn when nothing is new. Two narrator personas that hear each other. A summary the model rewrites itself. | Skip-if-nothing-new. Self-rewritten summaries for the colony chronicle. |
| EchoColony | Player chats directly with a pawn. Storyteller comments with a minimum gap that deaths and big threats bypass. | Player-to-pawn chat (milestone 5). The gap-with-bypass rule for the narrator. |
| RimChat | "Only explicit, announced effects count", so the model can't narrate effects that never happened. | Every effect is applied by the mod and reported back; nothing else counts. |
| AIColonists | The model picks only from options C# has verified. Gates count both game ticks and real seconds. | Verified-options pattern for effects. Dual-clock gates. |

Add-ons players install on top of RimTalk, which show what the base mods lack:

| Add-on | What it adds | What we take |
|---|---|---|
| Event+ (77k) | Keeps raid, quest and lodger facts in context for the event's whole duration, not for N messages. | An event ledger in the agent (§6). |
| Expand Memory (61k) | Layered memory with decay, importance and relevance scoring. | Scored memory, with importance taken from game numbers (§5). |
| Persona Director (51k) | Personas built from roles, genes, passions and relatives. Personas that evolve. A per-pawn notes box for the player. | Voice card built from game data, evolving bio, "director's notes" (§5, §9). |
| Lucid Chronicle (5k) | Each pawn writes a daily first-person summary; a colony chronicle on top. | Idle-time diaries and chronicle (§5). |
| DisplayOptimization, DynamicColors (24–30k) | Readable, per-pawn-coloured bubbles. | Per-pawn bubble colour and a tidy log (§9). |

What none of them has, and we add:

- **A persistent mind per pawn.** Every mod above builds a fresh prompt per event. Memory and
  persona are add-ons bolted to a stateless core.
- **Two pawns as two agents.** Everywhere else one model writes both sides of a conversation
  in one context.
- **Witness-only memory and traceable gossip.** No RimWorld mod limits what a pawn knows to
  what it saw or heard.
- **Prompts built for the cache.** No mod orders its prompt so the server can reuse it.
- **Measured voice distinctness.** Nobody tests whether pawns can be told apart.

## 3. Architecture

Three parts, with a strict split of duties:

| Part | Owns | Never does |
|---|---|---|
| **Mod** (C#, in game) | Senses: events, pawn data, who witnessed what. Display: bubbles, log, inspect pane. Effects: applying thoughts, with all sizes and caps. Saving minds inside the save file. | Call the model. Decide what anyone says. |
| **Agent** (its own program) | Minds: bios, memories, relations, wants. Direction: who speaks and when. Prompt building and slot leasing. | Touch game state except through the mod's endpoints. Set the size of any effect. |
| **Model server** (llama.cpp, Gemma 26B, 3 slots) | Text generation. | Hold anything that must survive: slots are disposable. |

The agent is its own small program, not part of the model server and not inside the game.
It ships and starts with the engine, so an ordinary player never sees it as a separate thing,
and the same program is what runs on another machine when the mod points at a remote host.
Keeping it separate means the model server stays stock llama.cpp, a crash in one does not
take down the other, and minds are edited through the in-game Mind tab, not through files.

Rules that follow from the split:

- **Reflexes live in the mod.** Witness lists, cooldowns, caps and expiry are computed in C# on
  the game thread. Nothing time-critical goes round the HTTP loop.
- **The game never pauses or waits for a line.** A late line is dropped (§7).
- **Minds are saved in the save file.** The agent is the working copy; the mod stores each
  pawn's mind as an opaque blob in the save. Reloading an old save rolls memories back with
  it, and a save shared to another machine keeps its colonists' minds.

**Engine and game, kept apart.** We are always building two things: a reusable engine and
the RimWorld part. A future game reuses the engine without RimWorld code.

| Engine (game-agnostic) | RimWorld part |
|---|---|
| LLM engine and installer (hive-mind based), ctx balancer, model builds | — |
| Agent core: LLM client, leases, prompt-block framework, minds, memory, ledger, conversations, sanitizer, intent pass, bridge protocol client | Agent adapter (`games/rimworld`): bio and voice-card templates, vocabulary, event mapping, block A rules, intent labels |
| Bridge protocol (envelope, auth, op ids, event stream, mind blobs, say/thought/talk) and its C# implementation with no game references | Mod: snapshots, witnesses, Harmony hooks, play log, effects, settings UI |

## 4. Slots and prompt layout

Three slots, each an independent context stream:

- **Slot 0, narrator.** Long-lived. Sees the whole colony. Writes the chronicle and the
  occasional third-person line. Does background work when idle.
- **Slots 1 and 2, pawn windows.** Idle until leased. An event leases one to inhabit a pawn
  for a while. A conversation leases both, one pawn in each, and the two talk to each other.

**Leasing rules**

- A lease is for one pawn and ends on a rule (§7), never on the model's say-so.
- A lease takes the first free pawn window. (Measured in M0: returning a pawn to its old slot
  gains nothing; the old and the other slot differ by 0.01 s.)
- Priority: player talk > death, mental break, raid start > injury, tend > social > level-up,
  ambient thought. A higher class may end a lower-class lease early.
- If both windows are busy and nothing can be preempted, the event waits with its expiry, or
  the narrator covers it in one third-person line.

**Prompt order for a pawn window** (most stable first, so the server reuses the longest prefix):

| Block | Content | Changes |
|---|---|---|
| A | World rules, speech rules, output format | Never (same bytes for every pawn) |
| B | Fixed bio and voice card | Never for this pawn |
| C | Evolving bio, wants, relations digest | Only at reflection |
| D | 2–4 chosen memories and the active events this pawn knows of | Once per lease |
| E | Scene: where, who is present, the pawn's state, the trigger | Once per lease |
| F | Turns so far | Append-only |
| G | Voice reminder (one line) and the request | Re-sent every turn; keep short, it is prefilled every turn |

Cache rules:

- Blocks A–E are fixed for the whole lease. No timestamps, tick counts or reordered lists in
  them. Memory is picked once at lease start, not per turn.
- Every request names its slot (`id_slot`). The server never picks.
- No saved KV states per pawn. A re-lease relies on the server's RAM prompt cache, so the
  server **must** run with `--cache-ram` (8192 MiB or more; about 180 MiB per cached lease).
  On Gemma's sliding-window cache a slot never reuses a displaced prefix by itself.
- No `--swa-full` (it does not fit at 64k per slot) and no speculative decoding (M0: n-gram
  drafts never fire on short lines; the E4B draft is accepted 24% of the time and is 2x slower).
- Every call logs prompt tokens, cached tokens and effective prefill, so cache misses show up.

**Two passes per line**, both in the same warm slot:

1. **Line.** Free text, stop at newline, hard token cap (start at 48).
2. **Intent.** A tiny constrained choice (1–3 tokens) from the effect set in §8, made about
   the line just written. Kept separate because style pressure makes structured output worse.
   Grammar-constrained (GBNF). The label glosses live in block A so the suffix stays short.
   M0: 27/30 correct, +0.29 s per line with the glosses in the suffix.

**Budgets and measured timings** (M0, Gemma 4 26B-A4B UD-Q3_K_XL, 3 slots × 65,536 ctx, q4_0 KV,
`--n-cpu-moe 6`): pawn prefix A–E about 1,700–1,850 tokens (block A about 840); narrator context
under 6,000 tokens, rolled by self-summary.

| Case | First token | Full line |
|---|---|---|
| Cold lease | 1.19 s | 1.46 s |
| Warm turn | 0.26 s | 0.45 s |
| Re-lease, same scene (RAM cache) | 0.19 s | 0.36 s |
| Re-lease, new scene (A–C reused) | 0.44 s | 0.66 s |

The three slots share one decode throughput (about 80 tok/s on this shape): running at once
costs time, it does not add speed. Narrator chronicle and diary work therefore waits for idle
windows (a 150-token chronicle takes 2.3 s alone, 5.4 s beside a conversation).

## 5. The pawn mind

One record per pawn, kept by the agent, saved by the mod:

| Part | What it is | Written when |
|---|---|---|
| **Fixed bio** | Who they are: backstory, traits, genes, body, age, key relatives. Built from game data. | Once, at first sight. Rebuilt only if the game facts change (new trait, lost limb). |
| **Voice card** | How they talk: one line on register, 1–3 tics (used in at most one line in three), 3 example lines. | Once. The player can edit it. |
| **Evolving bio** | Who they are becoming: a short paragraph. | At reflection. Each rewrite is kept as a diff the player can read and undo. |
| **Wants** | 1–3 current aims (resent X, win Y's respect, get off kitchen duty). | At reflection. |
| **Memories** | First-person, one or two sentences each, with tick, importance, who was there, source, visibility. | When the pawn witnesses or is told something. |
| **Relations** | Per other pawn: one line of feeling, one line of shared history. Sits beside RimWorld's own opinion number. | After a conversation ends; at reflection. |
| **Director's notes** | Free text from the player ("never forgives the raid on her brother"). | By the player. |

**Memory rules**

- **Witness only.** A pawn remembers an event only if the mod listed it as a witness (same
  room or within hearing or sight range), or if another pawn told it.
- **Source and confidence.** Memories are `saw`, `heard`, or `told by X`. Told memories carry
  lower confidence. That makes rumours traceable.
- **Visibility.** Each memory is `private`, `told to [names]` or `public`. Private memories
  are secrets: the pawn may hint at them, confide them (an effect, §8) or keep them.
- **Importance comes from the game**, not from a model call: the mood offset of the thought
  it caused, the severity of the injury, the opinion of the person involved, whether someone died.
- **Selection** scores recency, importance and relevance to the scene, and takes the top 2–4.
  More memory in the prompt makes voices more alike, so the cap is firm.
- **Decay and pinning.** Ordinary memories fade (start with a 4-day half-life). Deaths, betrayals
  and firsts are pinned. Similar old memories are merged into one summary line, then dropped.

**Reflection** runs in an idle pawn window when a pawn's unreflected importance passes a
threshold, or at least once every few days. It rewrites the evolving bio and wants, updates
relations, and merges memories. It is the only time block C changes, with one exception: a pawn's open
ongoing concerns (at most two, born from game events or a reflection, resolved by events or a reflection, faded after
a few days) show as one `Worries: ...` line in block C, so block C also changes when a concern is born or closes.
The grudge and make-up loop (an insult starts avoid, cooldown, then one pawn walks over to apologise, make up or
confront) is run by the agent from rules and calls `/social_action` directly; see `core/concerns.py` and `core/grudges.py`.

**Idle-time work**, lowest priority, dropped whenever a window is wanted: reflection, a daily
first-person diary entry per pawn, the narrator's colony chronicle.

## 6. Context: what the mod sends

**Events.** Each event carries: type, tick, map, the pawns involved, a one-line summary, a
severity number, and **witnesses** — the pawn ids who saw or heard it, computed in C# from
rooms, line of sight and hearing range times each pawn's Hearing.

**Event ledger.** The agent keeps long-running facts alive for as long as they are true, not
for N messages: an ongoing raid (faction, arrival, size), a siege, a quest and its lodgers,
a sick or downed colonist, a prisoner, a funeral, weather and season extremes. A pawn's
prompt gets only the ledger entries that pawn knows about.

**Pawn snapshot** (for the fixed bio and the scene block): name, age, body type, xenotype and
genes, backstory, traits, ideology role, skills by tier with passions, health conditions and
missing parts, current needs, mood and the top thoughts with their causes, current job,
gear worn and held, relations with opinion numbers, and status (colonist, prisoner, slave,
lodger, guest).

**Scene.** Room role and state, who else is present with what they are doing, time of day,
and trends rather than snapshots where a number matters (`food: 9 → 5 days, falling`).

**Grounding rules for the prompt**

- State hard facts the model keeps getting wrong: body type, missing limbs, who is dead, who
  is a lodger.
- The pawn may speak only of what is in its prompt. "I don't know" is allowed.
- Ambient scene data never enters saved history. Each past line keeps its trigger.
- A pawn the model names must exist and be known to the speaker, or the line is dropped.

## 7. Direction and pacing

Who speaks is decided mostly by rules, with the narrator slot used for judgement only where
rules can't decide.

- **Salience first.** Each event gets a score from game numbers. Low scores never reach the
  model.
- **Skip if nothing new.** A pawn whose state and surroundings haven't changed since it last
  spoke is not asked again.
- **Chattiness.** Each pawn has a talk weight from its traits. It scales how often ambient
  events lease a window for that pawn.
- **Quotas.** Per pair per day, and per pawn per day, so no two pawns monopolise the windows.
- **Dual clock.** Every window is `max(game units, real floor x units per second now)` with a hard real cap on a
  play clock that stands still while the game is paused. A voice budget (about 5 lines a real minute, halved at
  4x) decides how much is said; game-time expiry decides what is too stale. At 4x social and ambient talk stop;
  urgent, reactive and player talk still run. See `docs/SPEED.md`.
- **Expiry by class.** Player talk: never. Urgent (death, break, raid): 600 units, floor 3 s, cap 5 s. Event
  reactions: 3,000 units, floor 6 s, cap 60 s. Social and ambient: 1,200 units, floor 4 s, cap 20 s. A line for the
  game's own interaction attaches within its window (10 game minutes) or 5 real seconds, whichever is longer. An
  expired line is dropped silently; the memory of the event is already written.
- **Queue.** Bounded at 16. The most important (class, severity, age) takes the next voice token; duplicates
  (same actor, kind, listener) merge. Ambient needs a reserve of tokens so it never starves consequential talk.
- **Liveness check.** Before a line shows or an effect applies, the mod checks the speaker is
  alive, awake, on the map and still within range of the listener.

**Conversations** (both windows)

- Start only between pawns who are near each other and stay near. If they part, it ends.
- Each turn, the other pawn's line is appended to block F as heard speech.
- Only an opener that calls for an answer (question, insult, request, confession) obliges a reply.
- End by rule: a turn cap (6, then 4, 3, 2 at speeds 2, 3, 4; a minimum gap between turns of 0.8 s or 150 units), a chance of continuing that falls each turn, a
  repetition check, either pawn's job changing, or preemption.
- When it ends, each pawn's window writes one memory of it and one relation update. Then both
  leases end.

**Narrator**

- Speaks rarely: a minimum gap, bypassed for deaths and major threats.
- Never proposes or triggers events.
- Owns the chronicle, and covers events when both windows are busy.

## 8. Consequences

The model picks a label; the mod owns everything else. Starting set:

| Intent | Effect on listener | Effect on speaker |
|---|---|---|
| `none` | — | — |
| `kind` | Opinion of speaker up; small mood up | — |
| `comfort` | Mood up (only if listener has a negative thought); opinion up | — |
| `slight` | Opinion down | — |
| `insult` | Opinion down; mood down | Small opinion down from witnesses |
| `confide` | Opinion up; a private memory becomes `told to listener` | Opinion of listener up |
| `gossip` | Listener's opinion of the absent third pawn shifts a little; a `told by` memory | — |
| `boast` / `complain` | Small opinion shift by listener's traits | — |

Rules:

- **Sizes, durations, stacking and cooldowns are constants in C#.** Start at mood ±1–3 for
  under a day, opinion ±5 fading over about 10 days, stack 3, with a daily cap per pair.
- Effects are RimWorld thoughts, so the player sees them in the vanilla needs and social tabs,
  with the line that caused them.
- The mod replies `applied` or `refused (reason)`. The agent records only what was applied.
- The model cannot grant, promise or narrate an effect outside this table.
- Effects default **on**, with a strength slider (off, light, normal) in settings.
- Quotes: a thought or log entry made by a spoken line shows that line (sanitised, 140 characters,
  speaker named; up to 3 recent quotes on a stacked thought). The game's own interactions get a
  generated line too (BRIDGE-API, "Social events for the game's own interactions"); the line is
  attached to the vanilla log entry and thought, never adds an effect, and is dropped for attachment
  if it arrives after 10 game minutes. Removing the mod from a save loses the quoted thoughts and
  entries with a load error in the log, nothing else.
- A second tier (talking a pawn down from a break, nudging romance or a social fight) is
  deliberately left to a later version, after the first tier is tuned in play.

## 9. What the player sees

- **Bubbles** for anything said aloud, drawn as comic speech balloons (setting "Bubble style": Comic by default,
  or Classic for the old plain boxes). Text only, no audio. A balloon has a dark ink outline, an off-white
  fill, and a thin rim in the speaker's colour; its tail points at the speaker's head and flips or re-angles
  when screen edges or other balloons push the balloon away. Balloons pop in (about 120 ms from 85%) and fade
  out, freeze while the game is paused, and keep the duration setting. The look follows the line: normal speech
  is a round balloon; an insult, a doubled "!" or mostly-capitals line is a spiky burst; a confide or gossip
  intent is a dashed, smaller-text whisper; a thought (optional `kind` on `/say`, or text in `*..*` or `(..)`)
  is a cloud with trailing circles; a narrator caption (`kind` "narrator", or text in `[..]`) is a rectangular
  caption box. Settings: opacity, optional ALL-CAPS comic lettering. Textures are generated by
  `Mod/Textures/src/make_bubbles.py`; `Mod/Textures/src/preview.py` renders an offline preview
  (`docs/img/bubbles-preview.png`).
- **RimWorld's own logs.** Every spoken line is written into the vanilla play log as a proper
  log entry, so it shows in each pawn's Log tab (for speaker and listener) alongside vanilla
  social interactions, with the usual click-to-jump. If you blink and miss a bubble, you
  scroll back and read it there. Lines said during a fight also appear in the combat log.
  Entries are saved with the game.
- **Effects in the vanilla tabs.** A thought caused by a line shows in the Needs and Social
  tabs with the line that caused it as its description.
- **Conversation log** window: the whole colony's talk in order, with the trigger and any
  effect beside each line, filterable by pawn.
- **Inspect pane tab "Mind"**: the pawn's current thought (inner monologue, costs no speech),
  evolving bio with its diff history, wants, relations, memories with source and visibility,
  and the director's notes box.
- **Chronicle**: the narrator's running history of the colony.
- **Settings**: master switch; per-event toggles; talk frequency; effect strength; language;
  bubble options; connection (endpoint, token, Test light).
- **Debug window**: active leases, queue with expiries, per-call prompt and cached tokens,
  and an API log with edit and resend.

## 10. Bridge API changes

The bridge today has `/health`, `/state`, `/say` and event dial-out. Version 2:

- **Port.** Move off 8765 (RIMAPI and two other bridges use it). Try a new default, fall back
  across a short range, and write the chosen port and token to a file the agent reads.
- **Auth.** A token is required whenever the bridge listens beyond loopback. No CORS headers.
- **Every reply** carries the game tick and a `save_epoch` that changes on load, so the agent
  knows to re-read minds after a reload.
- **Every write** carries a client operation id and an expiry; duplicates and expired
  operations are refused.
- **Events**: keep the push, add `GET /events?since=N` so a restarted agent replays what it missed.
- **New endpoints**: `GET /pawn/{id}` (snapshot), `GET /scene/{id}`, `POST /say` (now with
  listener, intent and conversation id; replies `applied`/`refused`), `POST /thought`
  (inner monologue for the inspect pane), `GET`/`PUT /mind/{id}` (the saved blob),
  `GET /config` (settings the agent must honour).
- **Hook API** for other mods: register extra context for a pawn, and submit talk requests.
- **RimTalk**: if it is loaded, a pawn currently inhabited by a window does not also speak
  through RimTalk.

## 11. Milestones

Milestone 0 is done: results in `docs/M0-RESULTS.md`, harness in `bench/m0/`.

Each ends with something measured or playable. Measurements come first because four design
choices rest on untested assumptions.

| # | Milestone | Done when |
|---|---|---|
| 0 | **Measure** | We have numbers for: time to first line on a cold and a warm lease; whether the prefix is reused on re-lease (cached tokens); whether speculative decoding helps 15–40-token lines; 3 slots running at once; and a voice-distinctness score for a voice-card prompt against a plain persona prompt. |
| 1 | **Bridge v2 and senses** | New port and token file, tick and epoch on replies, operation ids, `/events?since`, pawn and scene snapshots, witness lists on events. |
| 2 | **Single voices** | Fixed bio and voice card built from game data. One window reacts to events in a distinct, grounded, short line. Expiry, quotas, dual clock and liveness in place. |
| 3 | **Minds** | Witness-only memories, selection, decay, reflection, evolving bio with diffs, minds saved in the save and rolled back on reload. "Mind" tab. |
| 4 | **Conversations and consequences** | Two windows talk. Rule-based endings. Intent pass, first-tier effects applied and reported, gossip and confiding. Effect sizes tuned in a real colony. |
| 5 | **Narrator and player** | Chronicle, diaries, narrator coverage, player-to-pawn chat, director's notes, debug window, RimTalk coexistence, hook API. |

## 12. Decisions (keeper, 2026-11-24)

1. **Agent location:** its own program, bundled and started by the engine (§3).
2. **Effects default on**, with the strength slider.
3. **English first.** Prompts and voice cards are English; other languages later.
4. **DLC content goes into the first voice-card builder:** Ideology roles and precepts,
   Biotech genes and xenotypes, Anomaly, Royalty titles, read only when the DLC is active.
5. **No audio, ever in this spec.** Speech is bubbles plus RimWorld's own logs (§9).

6. **Tone: it can be grim.** Voices match RimWorld itself: dry, blunt, matter-of-fact about
   raids, death, organ harvesting, cannibalism, slavery and breaks. No softening, no moralising
   asides, no melodrama. The game's own content is the limit.
7. **Model: gemma4-rp-maxxed**, built with the hive-mind process: Google's BF16 Gemma 4
   26B-A4B, abliterated by us (Heretic ARA, experts included), grafted into unsloth's
   UD-Q3_K_XL, expert-pruned for roleplay and ASCII-vocab-pruned. Target: model, three 64k
   slots and RimWorld on one 16 GB card with 1.5 GB kept for the OS. The model is decensored,
   adult content included. Consequences for this spec:
   - M0 ran on stock UD-Q3_K_XL. Stock Gemma gave 0 refusals in 60 grim RimWorld lines (so did
     abliterated Qwen), so the grim set alone does not separate them. The refusal check needs a
     harder probe set (adult content, explicit cruelty) before it can judge a build.
   - Every new build reruns the M0 harness (`bench/m0/`) to check grounding, format following,
     voice score and latency did not get worse.
   - The sanitizer treats a refusal or disclaimer as a failed line and drops it; it never
     reaches a bubble or a log.
   - Prompts must still work on a stock model, because players may point the mod at their
     own endpoint. There the tone degrades; nothing else should break.

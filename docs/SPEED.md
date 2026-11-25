# Game speed: 1x to 4x

RimWorld runs at 60, 180, 360 and 900 ticks a second at speeds 1, 2, 3 and 4 (4 is the dev speed). The model does not
speed up: about 80 ms to the first token warm, 0.1 to 0.25 s a line, 0.3 s cold, 0.11 s for the intent pass, three slots.
So everything the mod and the agent time must be thought of twice: in game units (what the player sees happen) and in
real seconds (what the machine and the player's eyes can do).

## The rules we settled on

1. **A window is `max(game units, real floor x units per second now)`, with a hard real cap.** The floor is re-read at
   the current speed, so the same real time is allowed at any speed. The cap runs on a **play clock** that stands still
   while the game is paused, so a pause never expires anything. The window starts when the event happened, not when the
   agent heard of it.
2. **Real time bounds how much is said; game time bounds how stale a line may be.** A `VoiceBudget` (token bucket on
   the play clock, 5 lines a minute, burst 5, halved at 4x) gates chatter. Urgent lines (death, break, raid, player
   talk) bypass it and may put it into debt. Items below urgent wait in a bounded queue (16) and the most important
   (class, severity, age) takes the next token; ambient chatter needs 3 tokens in hand so it can never starve a
   consequential line.
3. **Memory never depends on speech.** `ingest` writes every witnessed event into the witnesses' minds before any
   decision to speak. A dropped, shed or expired line loses a line, never a memory.
4. **A dropped line has no effect.** Effects are applied by the mod on `/say`; a line that is not sent applies nothing.
5. **Speed 4 may be quiet, but is safe.** `pause_ambient_at_speed` is 4 (was 3): social and ambient talk stop at 4x
   only. Urgent and reactive lines still run, at half the budget.

## Audit: every place time is handled

"Dual" means both game units and real seconds bound it. Behaviour is at speeds 2 / 3 / 4 relative to 1.

| Where | Unit | Dual clock | Speed 2 / 3 / 4 (after this work) |
|---|---|---|---|
| Request expiry by class (`core/lease.py`) | game units + real cap | yes (new) | urgent 600 units, floor 3 s; reactive 3000 units, floor 6 s; social/ambient 1200 units, floor 4 s; real caps 5/60/20/20 s on the play clock |
| Voice budget | real, play clock | real by design | 5 lines a minute at every speed, x0.5 at 4x |
| Queue | count | n/a | bounded at 16; weakest evicted, duplicates (actor, kind, listener) merged keeping max severity |
| Quotas per pair and per actor per day | game day | game | a game day is 5 min of play at 3x; the budget keeps the real rate bounded. Pair quota counts a talk once, not per line; none for the game's own interactions; ambient may use 0.75 of an actor's daily quota so consequential lines are never starved |
| Conversation turn gap | game units + real | yes (new) | `max(0.8 s, 150 units / rate)` between turns so bubbles can be read |
| Conversation turn cap | speed | by rate | 6 / 4 / 3 / 2 turns at 1x / 2x / 3x / 4x (from 3x memory is written from templates, not extra LLM calls) |
| Agent poll interval | real | adaptive | `poll_interval / sqrt(rate / 60)` so the ring is drained before it overruns |
| `expires_tick` on the wire | game ticks | via deadline | deadline plus 0.5 s of slack |
| Vanilla-interaction attach window (mod `PendingTable`, agent `react`) | game ticks + real | yes (new) | within `attachWindowMinutes` (417 ticks) OR `attachRealFloorSec` (5 s), whichever is longer; the mod sends `window_real_s` and the agent uses the same floor |
| Idle work (reflection, diary, chronicle) | game day | starving floor | due work that waited 0.25 game day runs beside chatter and yields only to reactive, urgent or player work; retry back-off on the play clock. Falls with speed per real minute but stays at one a pawn a day or better |
| Map thought job `_thought` (`core/idle.py`) | real | real | one per pawn per `thought_every_real_s` (420 s), the same at every speed |
| Shout gap `shout_gap_real_s` (`Director._delivery`) | real (raw clock) | real | 45 s per speaker, speed independent. It also runs through a pause, which only makes a shout slightly more likely right after one |
| Thought cloud pacing (mod `ThoughtPacing`) | real seconds as ticks at the current speed | yes (new) | per-pawn 120 s and global 20 s are `seconds x max(60, ticks per real second)`, so 20 s of watching at every speed (they were 10, 3.3 and 1.3 s). A waiting cloud is stale after `max(600 ticks, 10 s)` |
| `EventGate` cooldowns (injury 30 s, social 60 s, tend 20 s, mental 10 s, thought 10 s) | game ticks | game only | shorten to a third / sixth / fifteenth in real time. They only cut events (never memory); the push cap and the agent's queue bound what follows. Left as is; see "Wanted elsewhere" |
| Push cap `MaxPerRealMinute` 20 | real | real | extras stay in the ring and are polled |
| Event ring (2000) | count | n/a | no overrun in the simulation at 4x; the agent polls faster as the rate rises |
| Bridge requests (`GameThreadTimeout` 20 s, agent HTTP timeouts) | real | real | the unbounded wait after a claim is now bounded too (504) |
| Speech balloon lifetime (`bubbleDurationSec` 8) | game ticks | game only | 8 s at 1x, 2.7 s at 3x, 0.5 s at 4x. Reported, not changed (see below) |
| Harmony hooks | per call | n/a | the social prefix is cheap when nothing is voiced and skips its memory snapshot for chitchat that the cooldown would drop; nothing on the game thread waits for HTTP (the bridge server answers from a queue drained per frame) |

## Simulation

`agent/hermesvoice_agent/games/rimworld/speedsim.py` runs the real `Director` against a modelled game on a virtual
asyncio clock (a 25 minute run takes a second). The model has four pawns, three slots with the measured latencies, a
bridge with frame-bound latency, a ring that trims at 2000 and a model of the mod's attach table and cloud pacing. A
synthetic colony day: 40 thoughts, 6 level-ups, 22 of the game's own interactions (every fourth a high-tier one), 8
conversations, 8 injuries with tends, a mental break and a 40-event raid inside 1,080 ticks.
`PYTHONPATH=agent python -m hermesvoice_agent.games.rimworld.speedsim 5` prints the table.
`agent/tests/test_speed_sim.py` holds the bounds as tests: per speed (nothing lost, bounded queue, late share, memory,
effects), speed 3 voiced about like speed 1, the raid at 3x, a schedule of pauses and speed changes, the attach share,
a social-fight chain, the cloud gaps.

One game day and a half, same seed:

| | lines/real min | lines per game hour | late (generated, then too late) | delay p90 (game ticks) | lost events | memory misses | effects for dropped lines | max queue |
|---|---|---|---|---|---|---|---|---|
| before 1x | 2.9 | 2.0 | 0% | 133 | 0 | 0 | 0 | 5 |
| before 2x | 7.2 | 1.7 | 6% | 673 | 0 | 0 | 0 | 10 |
| before 3x | 6.9 | 0.8 | 0% | 1599 | 0 | 0 | 0 | 6 |
| before 4x | 14.0 | 0.7 | 0% | 3482 | 0 | 0 | 0 | 7 |
| after 1x | 2.0 | 1.4 | 0% | 234 | 0 | 0 | 0 | 10 |
| after 2x | 3.6 | 0.8 | 0% | 1320 | 0 | 0 | 0 | 11 |
| after 3x | 4.3 | 0.5 | 0% | 663 | 0 | 0 | 0 | 11 |
| after 4x | 4.7 | 0.2 | 0% | 560 | 0 | 0 | 0 | 8 |

What the numbers say, and what changed them. Before: at 3x all ambient and social talk stopped (the pause default was 3),
so what was heard was the quota leftovers; at 4x the per-day quotas were spent in 67 real seconds, giving 14 lines a real
minute of the oldest events (p90 delay 3,482 ticks, almost a minute of game time) and conversations were starved of
slots. After: a flat ceiling of about 5 lines a real minute, chosen from the most important waiting items; 3x is voiced
(4.3 a minute against 2.0 at 1x, because 1x is bound by the daily per-pawn quotas, not the ceiling; roughly twice, not
equal, and kept on purpose so a fast colony still sounds alive); every line is fresher (p90 delay 663 ticks at 3x against
1,599); 4x is quiet (ambient and social paused) but never silent. Idle work per game day: diaries 4 and the chronicle 1 at
every speed; reflections 28, 14, 11 and 5 at 1x to 4x (at least one a pawn a day at every speed). The lines per game hour
fall with speed by design: the player watches one real minute, not one game hour.

The vanilla-interaction attach share: in a busy colony day the high-tier share is 0.17 to 0.5 at any speed because most
of those lines are shed behind more important ones or "nothing new"; the share of lines that were sent and then attached
is what the attach window controls. With a sparse stream every line sent attaches at 1x, 2x and 3x. Under load at 3x the
5 s real floor attaches more than the ticks-only window (417 ticks is 1.2 s at 3x) in the test that sums three seeds.
At 4x the game's own interactions are not voiced at all (the pause). A chain of 60 insults in 20 s of 3x play is voiced a
handful of times, the queue never exceeds 16, nothing is lost from memory.

Clouds: the agent makes at most one thought per pawn per 420 real seconds, so clouds stay at 0.2 to 0.6 a real minute at
every speed in the colony day; with the mod's old tick-count gaps a burst of thoughts at 4x would show up to 20 a minute
(the test `test_cloud_gaps_are_real_seconds` models it); with real-second gaps at most 3.

## What only an in-game test can confirm

- That the 5 s attach floor really covers the delay from interaction to `/say` at 3x on a loaded map (the sim's bridge
  latency is modelled, 8 to 30 ms a call).
- The cost of the social prefix and the mood-thought hook with 20 or more pawns at 4x (the code is O(thoughts) per
  voiced interaction; we measured nothing in-engine).
- That nothing on the main thread stalls when the ring is polled while a raid is running (the sim is not the game).
- How the cloud and balloon look at 3x (readability), and whether 3 s of bubble at 3x is long enough to read.

## Wanted elsewhere (not changed here)

- `SpeechBubbles.cs` (bubbles branch): balloon lifetime is `bubbleDurationSec * 60` game ticks. Make it
  `max(duration ticks, duration s x ticks per real second / 2)`, or at least a floor of 4 real seconds at the current
  speed, with the same `60 * Find.TickManager.TickRateMultiplier` already used for the clouds.
- `EventGate.cs`: the per-kind cooldowns could use `ThoughtPacing.GapTicks` the same way; left because the agent's queue
  bound and budget already contain the effect, and the simulation shows no ring overrun.

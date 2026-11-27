# Vivarium playtest: the keeper's first run

The first in-game run of "Narrator plays" (docs/VIVARIUM.md). Everything here has only been compiled against the
1.7 reference assemblies and tested against fakes, so this run is the first real check. Use a throwaway colony
(a mid-game save with 4-6 colonists is ideal). Keep the agent log open (`agent.log`), and when a step fails note the
step number, what you saw, and any red error in the dev log.

Bridge calls below can be made with curl: the port and token are in `<RimWorld config>/RimWorldLLM/bridge.json`.

```
T=<token>; B=http://127.0.0.1:9765
curl -s -H "Authorization: Bearer $T" $B/vivarium
curl -s -H "Authorization: Bearer $T" -X POST $B/act/time -d '{"op_id":"x1","pause":true,"emergency":true}'
```

## A. The mod alone (agent may run or not)

1. **Settings.** Mod settings → Colonists → "Narrator plays" group: "may pause for emergencies" (on), "controls
   the game speed" (off), heartbeat slider 1-24 hours (6). Change each: `GET /config` shows `vivarium_may_pause`,
   `vivarium_controls_speed`, `vivarium_heartbeat_hours`, and `GET /vivarium` shows them under `settings`.
2. **The switch.** Bottom-right play-settings row: an eye icon with the tooltip "Narrator plays: the narrator runs
   the colony; you can still act and talk to it." Click: it lights up and the chat shows "Narrator plays: on";
   click again: "off". Not shown on the world map.
3. **The key.** Options → Controls → RimWorldLLM: bind "toggle Narrator plays"; the key toggles the same way.
4. **The chat bar.** Map in focus, no window open: press Enter. A one-line bar opens above the bottom bar with a hint.
   - Type text, Enter: the bar closes and "You: …" shows; the line is in `GET /chat`.
   - Esc closes the bar and does **not** open the main menu.
   - While typing, game hotkeys (Space, 1-4, letters for tabs) must **not** fire. ← most likely to need a fix.
   - Enter in any other text field, or with a dialog open, must not open the bar.
   - Try at UI scale 1 and 2: the bar and panel sit above the bottom bar, not over it.
5. **@Name.** `@Ana hi` (a real colonist, or a unique prefix): "You to Ana: hi"; `GET /talk` has it with
   `from: chat`; once the agent answers, Ana's line shows in green. `@Nobody hi` shows "No colonist named Nobody."
6. **Scrollback.** Last 8 lines, colours: you blue, narrator gold, notes grey, colonists green. It shows for about
   10 s after a new line and stays while the bar is open.
7. **Save/load.** Save, quit to menu, load: the switch state and chat history come back; old lines don't pop the
   panel. A save from before this version loads with the switch off and an empty chat.
8. **Goals tab** (Voices window → Goals): add a long- and a short-term goal; Done, Drop, Reopen, Remove; edit and
   save the plan; add and remove a standing order ("always keep 20 meals"). `GET /goals` shows the same board.
9. **Hands by hand** (`POST /act/*`, check the game UI after each; every success appears in `GET /actions` and in
   the Goals tab's action list):
   - time: with "controls speed" off, `speed` is refused; `pause` without `emergency` is refused; `pause` with
     `emergency: true` pauses; `pause: false` then unpauses. With "controls speed" on, speed 1-4 works.
   - work: `{"pawn":<id>,"priorities":{"Cooking":1}}` changes the Work tab (and turns manual priorities on if they
     were off); an incapable work type is refused with the reason.
   - schedule: `{"pawn":<id>,"from_hour":22,"to_hour":6,"assignment":"sleep"}` wraps past midnight.
   - policy: outfit / food / drug by name; a wrong name lists the real ones.
   - bed: assigns a bed (ids from `GET /digest` beds); a medical bed is refused.
   - research: sets the project; one with unmet prerequisites is refused with the reason.
   - letter: answer a quest letter by label, by `#0`, and `dismiss` on an info letter; a disabled option is refused.
   - draft: drafts and undrafts; a slave or a pacifist is skipped with the reason.
   - order: move (drafted pawn walks there), attack a raider (melee and **ranged** pawn), hold (stops, fire-at-will
     off), `hold:false` (fire-at-will on).
10. **Digest.** `GET /digest` on the live colony: every section (date, resources, pawns, work types, policies, beds,
    alerts, letters, research, threats, rooms, power, facts) within the caps. A raider in fog is **not** listed.
    It answers quickly (watch for a hitch when calling it repeatedly).
11. **Events.** A quest/choice letter → one `letter` event with its choices (`GET /events`). A fire, starvation or a
    downed colonist → one `alert` event per activation, not repeated; loading a save with an alert already active
    gives none.

## B. With the agent and the 26B

12. **Advisor mode** (switch off). Chat "what should we research next?": a narrator line answers, nothing changes in
    the game. Chat "put Ana on cooking": one action (Ana's Cooking priority), a "→ …" note, and a line saying so.
    Chat "build a freezer": it says it can't build yet and recommends how.
13. **Playing** (switch on, speed 1-2). Within seconds it takes a first look: a few actions or none, maybe a line.
    Then it looks again about every 6 game hours (faster at 3x). Watch for over-acting (changing priorities every
    look) — note it.
14. **Wake on events.** Trigger a raid (dev mode, or wait): it wakes at once, drafts fighters, maybe pauses (only
    with "may pause" on) and unpauses after. A quest letter: it answers or leaves it, and says why if it answers.
15. **Standing orders.** Chat "always keep at least 2 cooks": it records a standing order (Goals tab) and respects it
    in later looks.
16. **Idle jobs yield.** While it plays, the chronicle and art still appear between its looks (Voices → Chronicle),
    never cut mid-sentence into nonsense; colonists keep talking as before.
17. **Speed.** At 3x and 4x the game must not stutter from the steward (it reads the digest at most every ~20 s).
18. **Turn it off** mid-cycle: it finishes the current look and then only answers chat.

Report back: the step numbers that failed, the agent log around them, and how the narrator's play felt
(too busy, too quiet, sensible priorities?).

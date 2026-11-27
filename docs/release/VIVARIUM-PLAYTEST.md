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
   The bar's input is a small window that stops camera motion: while typing, WASD and the arrow keys must not pan
   the camera, and the speed keys, space (pause) and main-tab hotkeys must not fire. Esc closes only the bar.
   - Type text, Enter: the bar closes and "You: …" shows; the line is in `GET /chat`.
   - Esc closes the bar and does **not** open the main menu.
   - While typing, game hotkeys (Space, 1-4, letters for tabs) must **not** fire. ← most likely to need a fix.
   - Enter in any other text field, or with a dialog open, must not open the bar.
   - The bar opens at the chat panel's bottom edge, the panel's width.
5. **@Name.** `@Ana hi` (a real colonist, or a unique prefix): "You → Ana: hi"; `GET /talk` has it with
   `from: chat`; once the agent answers, Ana's line shows in green. `@Nobody hi` shows "No colonist named Nobody."
6. **The chat panel** (always on, MOBA style). Colours: you blue, you → colonist teal, narrator gold, notes grey,
   colonists green, acts greentext green, refused acts grey.
   - **Position, UI scale 1** (Options → Interface → UI scale; 1920x1080 expected numbers): left of screen at x 148,
     just right of the resources list; its bottom edge (the input bar slot) at y 872, about 208 above the screen's
     bottom, i.e. just above where the inspect pane opens when you select something. With 10 lines of small text the
     log box is ~230 tall (y ~612-842). It must not cover the resources list, the inspect pane, the architect menu
     button row, the bottom bar, the colonist bar, alerts, letters, or the date/time controls.
   - **UI scale 2** (960x540 UI units on 1080p): same x 148, bottom edge at y 332; it fits 8 small lines (box y
     ~116-302) and stays below the colonist bar. Check it doesn't cover the colonist bar with many colonists.
   - Settings → Colonists → Narrator plays: "Chat panel on the right" moves it left of the date/alerts column (at
     scale 1 it then sits lower, just above the bottom bar) while the panel has not been dragged; background
     opacity, larger text, and "Show the chat panel" off (then it shows only while typing) all apply at once.
   - **Readability:** read it over bright ground (sand, snow, a lit floor) and dark ground (night, rock, fog): text
     keeps a soft dark outline; the background stays faint. Note the opacity you end up liking.
   - It never fades away; a new line highlights for ~4 s.
   - **Mouse-over:** the panel goes nearly opaque; the wheel scrolls older lines (and does not zoom the map);
     moving away snaps back to the newest. **Click-through:** clicking or dragging a selection box over the panel
     still selects pawns/things under it; right-click orders work under it.
   - Opening a main tab (Architect, Work, …) hides the panel; Enter still shows it with the bar.
6a. **Move, resize, filters** (round 2).
   - Hover the panel: a thin dark strip with a three-bar grip and five chips (Talk, Thoughts, Narrator, Actions,
     Story) shows over its top, and a small staircase grip in the bottom-right corner. Away from the panel neither shows.
   - Drag the strip (grip or empty strip): the panel follows the mouse anywhere and can't leave the screen. Drag the
     corner: width and height change; more height shows more lines; it stops at ~220 wide / two lines. Releasing the
     mouse outside the panel ends the drag. Clicks and drags anywhere else on the panel still reach the map.
   - Press Enter: the input bar sits right under the moved panel (above it when the panel is at the bottom edge) and
     follows if you drag while it's open.
   - Change UI scale or resolution: the panel keeps its relative place and size. Save/quit/load: same place.
   - Settings → Colonists → Narrator plays → "Reset chat panel position": back to the default spot.
   - Click each chip: its lines vanish from the panel (the chip dims) and come back on a second click; the five
     checkboxes in settings match the chips. A chip click must not select or order anything on the map.
6b. **Everything in the log** (agent running). Within a few minutes the panel shows colonist speech ("Ana: …",
   "Ana → Bram: …"), thoughts ("Ana thinks: …" dim green, italic — if you see literal `<i>` tags, note it), dreams,
   diary lines ("Ana's diary: …"), and later chronicle ("Chronicle, Day N: …"), a recap letter ("Recap: …") and art
   (`Art: "Title" by Ana: …`). An `@Ana hi` answer shows once, not twice. A refused `/say` (a sleeping or dead
   speaker) adds nothing.
6c. **Greentext.** With the narrator playing (or one advisor task), each act it takes adds its own green line
   `> set Ana's Cooking to 1st, since meals are running low` (no "→" notes any more); the narrator's own line after
   it doesn't list the acts again. Try a refused act, e.g. `curl ... -X POST $B/act/time -d '{"op_id":"g1","speed":3,"why":"since nothing is happening"}'`
   with "controls the game speed" off: a grey `> couldn't change the game speed: …` line. Note any wording that
   reads oddly (pawn names, priorities, bed and policy names).
6d. **No meta chitchat.** Over a long run, no thought or line that only remarks on the talking itself ("cut this
   chitchat", "enough small talk").
7. **Save/load.** Save, quit to menu, load: the switch state and chat history come back; old lines don't
   highlight as new; no "is thinking" line survives a load. A save from before this version loads with the switch off and an empty chat.
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
    the game. Chat "put Ana on cooking": one action (Ana's Cooking priority), its green "> …" line, and a narrator line.
    Chat "build a freezer": it says it can't build yet and recommends how.
12a. **"Narrator is thinking…".** Send a chat line: within a second the panel's last line reads "Narrator is
    thinking" with animated dots, and it is replaced by the narrator's answer (or vanishes if the cycle ends with
    no line). Kill the agent mid-cycle: the thinking line disappears by itself within 90 s. By hand:
    `curl ... -X POST $B/chat/status -d '{"op_id":"t1","who":"narrator","state":"thinking","stream_id":"s1","progress":0.4}'`
    shows "… 40%" with a thin bar; then `$B/chat/partial -d '{"op_id":"t2","stream_id":"s1","text":"The wind"}'`
    shows the text streaming; `$B/chat -d '{"op_id":"t3","who":"narrator","text":"The wind is rising.","stream_id":"s1"}'`
    turns it into a normal line. `"who":"colonist","to":<pawn id>` shows "Ana is thinking…".
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

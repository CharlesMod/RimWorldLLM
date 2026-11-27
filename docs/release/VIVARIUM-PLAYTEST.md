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

## C. Phase 2: building, zones, bills, designations

Use an early colony (day 2-10, 3-5 colonists, wood and some stone, no freezer). `$B` and `$T` as above.

19. **Digest build view.** `curl -s -H "Authorization: Bearer $T" $B/digest | jq .build`: `home` near your beds;
    `rooms` lists your real rooms with the right role, size, bed/door/roofed; `missing` matches what the colony
    lacks (try: no stove → "stove"); `benches` shows each bench with its bills; `zones`, `can_build` (only researched
    things), `stuff` (your stock), `plants` (sowable now), `ores` (visible steel/components near home).
20. **Room.** `-X POST $B/act/room -d '{"op_id":"r1","purpose":"bedroom"}'`: a 5x5 ring of wall blueprints with one
    door facing the base appears on open ground near home, not over a bed, grave, tree, zone or building. The reply
    says where and of what. Try `"x":<a cell in a mountain>,"z":...`: refused with a reason, nothing placed.
21. **Thing.** `{"thing":"stove","room":<kitchen id from rooms>}` → a fueled stove blueprint inside that room against
    a wall, not in the doorway. `{"thing":"stoev"}` → refused, lists known things. `{"thing":"stove","stuff":"cloth"}`
    → refused ("can't be made of"). A thing needing research you lack → refused naming the project.
22. **Zones.** `{"kind":"grow","plant":"rice","size":"small"}` → a 5x5 rice field on fertile, unroofed soil. In winter
    → refused (season). `{"kind":"stockpile","preset":"food","room":<id>}` fills that room's free floor, food only.
    `{"kind":"dump"}` → a small dumping stockpile 12-30 cells away.
23. **Designations.** `{"kind":"mine","ore":"steel","max":10}` → 10 steel cells nearest home marked. `{"kind":"chop"}`
    with a blueprint on a tree → that tree marked to cut, and it yields wood. `{"kind":"harvest"}` → ripe wild
    berries/plants near home. `{"kind":"haul"}` → loose items near home. `{"kind":"deconstruct","target":<your
    wall>}` → refused/skipped ("player-made buildings"); with `"player_asked":true` and no chat line → refused.
24. **Bills.** `GET $B/benches`; `/act/bill {"op":"add","bench":<stove>,"recipe":"simple meal","until":10}` → the
    bill shows "Do until you have 10". suspend/resume/remove by `index`.
25. **Undo.** After 20-24, type `/undo` in the chat: the last action's things vanish (blueprints cancelled, zone gone,
    bill gone) and a note says what. `/undo 3` takes back three. A wall already built stays ("already built"). The
    settings button "Undo the narrator's last build" (Colonists → Narrator plays) does the same; greyed when there is
    nothing to undo. Save, load, `/undo` still works for actions before the save.
26. **Caps.** Place rooms until refused: after about 50 blueprints in one steward cycle (or one game hour) the next
    is refused "used up"; after an hour it works again.
27. **Steward builds.** Switch on, early colony missing a stove and a freezer: over a few looks it should follow the
    build order (shelter, beds, food stockpile, stove, freezer, table, research bench), one room or zone per look,
    never over beds/graves, never tearing anything down. Chat "build a freezer": a freezer room, then a cooler in it.
    Note anything silly (rooms in odd places, fields on rock, rooms overlapping planned rooms).

## D. Unattended play: dialogs

Narrator plays on, the agent running with the model, `$B` and `$T` as above. Use dev mode (Debug actions) to fire
incidents: `Incidents > Refugee chased`, `Wanderer joins`, `Ransom demand`, `Quest`, `Give quest`; change the settings
first: timeout 15 s in the mod settings (Narrator plays group).

26. **See.** Fire a refugee incident. `curl -s -H "Authorization: Bearer $T" $B/dialogs | jq` lists `L<id>` kind joiner
    with two options (Accept / Reject, nothing disabled). `GET /events` has a `dialog` event with the same id.
27. **Answer by the model.** Leave the agent running: within a few seconds a `> chose "Accept ..." on "Refugee", since ...`
    green line appears and the refugee joins. Check the order of the options in `/dialogs` against the real letter
    (index 0 must be the accepting one; if vanilla's order differs, the default picks the wrong one: report it).
28. **Paused window.** Fire the colony naming dialog (new game start, or Debug > "Name colony"/rename faction in the
    faction tab) while the game is running: the game is paused by the window. The steward still answers (reason `dialog` in
    its trace) with a fitting name; the window closes and the name shows in the faction tab.
29. **Default.** Stop the agent (or set the agent's choice off). Fire `Ransom demand`: after 15 s a chat note "No answer in
    15 s: chose "Refuse..." on ... (default)" and an action line; no silver is paid. Fire a refugee: after 15 s accepted.
    Fire a quest offer: declined (the letter disappears, the quest does not start).
30. **Message box and node tree.** Trigger a trader/faction comms dialog (Debug > "Make trader caravan arrive", open the
    comms console), leave it: after the timeout it closes via its harmless option. Raise a Dialog_MessageBox (for
    example "Abandon colony" confirm via the caravan tab) with the agent off: after the timeout the Cancel button is
    pressed, not the destructive one.
31. **Growth moment** (Biotech): a child turning 3, 7 or 10 gives a letter and a choice window. With the agent off, the
    default takes the first passion(s) and the first trait; the child's skills and traits show it. With the agent on,
    `GET /dialogs` shows `growth` (passions, how many, traits with descriptions, the child in brief) and
    `POST /act/choose {dialog, passions:[..], trait, why}` sets exactly those; too many passions or an unknown trait
    is refused with the reason and the child is unchanged.
32. **Toggle.** Turn "Answer popups when the narrator plays" off: nothing is auto-answered; the model's `choose` still
    works. Turn Narrator plays off: nothing is auto-answered either.
33. **A dialog the scanner cannot answer.** A letter with only "opens a window" options (disabled, "only opens a window")
    stays open with no default; after three tries (none, when no option is enabled) one chat note says so. Nothing loops.

## E. Unattended play: camera director (watch mode)

1. A camera button sits beside the "Narrator plays" eye; hover shows the tip; click: message "Watch mode on." Settings
   (Talk/Camera page) shows the same switch, reopened state matches. Optionally bind a key under Keyboard, RimWorldLLM.
2. With watch mode on and nothing happening, the camera cycles everyday scenes and colonists (section E2 below).
3. Dev mode: fire a raid; within a second the camera cuts to the raiders, chat gets "Camera: a raid arrives from the <side>".
4. Start a fight, a mental break and a social fight (dev tools) one after another: shots follow, the chat does not flood
   (at most one camera line per 12 s). The same colonist is not shown again and again while others have events.
5. Move or zoom the camera yourself: it stays still for the pause time (default 20 s), then resumes by itself.
6. A funeral, a wedding or a party each get a shot and a line. Kill a colonist: cut to the body, "Camera: X has died".
7. Biotech: a birth gives "Camera: X gives birth" and a `birth` event on GET /events. Without Biotech: no errors in the log.
8. A masterwork item and a finished ship part give shots and `masterwork` / `ship_part` events.
9. Watch mode off: the camera never moves by itself. Other maps: a shot on another map cuts there (camera only, no selection).

### E2. Screen director (follows, scenes, battles, zoom, threads)
Watch mode on, default settings, normal pace. Play at 1x, then repeat 3, 4 and 7 at 3x.
1. **Follow smoothness.** A follow shot (the filler colonist, or a thread): the camera keeps the pawn near the centre
   as they walk. It does not twitch when they take a step or turn, eases in and out (no sudden starts or stops), and
   never swings past them. Nothing makes you queasy. Report any shake or lurch and the speed it happened at.
2. **Dinner scene.** At a mealtime with 2+ colonists eating in one room: within a few seconds of the last shot ending,
   a close shot frames the table and the chat says "Camera: dinner in the <room>: <names>" (breakfast or lunch by the
   clock). Others to watch for: recreation together, two working side by side, a doctor tending, someone alone and
   awake at night ("can't sleep"), a child playing or learning, partners together, a prisoner chat, taming.
3. **Battle stays framed.** Dev raid next to colonists (or draft them toward it): once they are within about 28 cells
   of each other the view widens to show both sides, centred between them, and stays on the fight for as long as it
   lasts, with "Camera: the fight begins". Down a colonist: a short close-up ("Camera: X is down"), then back to the
   fight. When it ends: a closing shot, "Camera: the raiders flee" or "are beaten".
4. **Zoom feels right.** Meals and deaths close, work middle, raids and battles wide; never past your own zoom limits.
   Zoom yourself: the director pauses; turn watch mode off (button, key or settings): your zoom comes back.
5. **Threads return to a pawn.** Kill a colonist with family or a friend (dev): in the next quiet stretches the camera
   comes back to them ("Camera: Bram, after losing Ana", after the funeral "Camera: Bram, after the funeral"). Same for
   a recruit ("newly arrived") and a colonist after a mental break ("after the break").
6. **Pacing.** The same scene or pawn does not come back within about 4 minutes unless something urgent happens; at
   most one camera line per 12 s. Try pace calm and lively in settings: shots get longer/shorter.
7. **No stutter at 3x.** Frame rate with watch mode on and off at 3x in a busy colony: no visible hitch every few
   seconds (scene and battle scans).
8. **Settings.** Each scene group off removes that kind; "select the pawn the camera follows" selects without drafting.
Report: anything that felt jerky, scenes that were wrong or missing, captions that read badly.

## F. Unattended play: stability and restart
Needs the mod; the agent is optional except where noted. Switch Narrator plays on.

34. **Chat kinds.** `POST /chat` with who=comment and a camera-kind line: the comment shows gold italic, the camera
    line dim blue-grey, both under the Story filter.
35. **Panel cost.** Leave the panel open at 3x speed for a game day with a chat stream going: no stutter, no
    growing frame time. Scroll and resize still feel immediate.
36. **Rotating autosave.** Options: game autosave off. Run 3 game days (days slider 1, keep 3): saves
    `RWL-Narrator-1..3` appear, then the oldest is overwritten. Your own saves are untouched. No save starts
    during a long event, an open dialog, or in the middle of a raid (a raid holds it at most half a day).
37. **Game autosave on.** The mod does not save at all (the game handles it).
38. **Colony lost, ask.** Let everyone die (or use a dev kill). One `colony_lost` event; the dialog offers New
    colony on this world, New world, Stop. Vanilla's own game-over window may also show; either is fine.
39. **Same world.** Pick it: the vanilla wanderers dialog opens; accept it. A chronicle entry "story continues" and
    the chat carry on; Narrator plays is still on. Nothing was deleted.
40. **New world.** Pick it: the lost world is saved as `<colony>-lost-<n>` first (never overwriting), then the
    quick-start world loads; Narrator plays is on, the chronicle has the "story continues" entry.
41. **Auto.** Setting auto: a chat note, 20 s later the same-world dialog opens. Leave it: after 3 minutes the lost
    world is saved and a new world starts. Off: nothing happens.
42. **Save failed.** If the lost-world save is refused (disabled saving), the leave is cancelled with a chat note.

## G. Unattended play: documentary voice (agent with the 26B)
Needs the mod setting `narratorCommentary` (Colonists -> Narrator plays), on by default.
1. **On.** Narrator plays on, setting on. A raid starts: within a minute one short wry line appears in the chat (Story
   group, "comment" kind), one sentence, no quotes, only names that exist. A death right after it: a second line once 30 s
   have passed.
2. **Gaps.** Several ordinary moments (a funeral, a masterwork, a party) in a row: at most one line per 3 minutes and per 6
   game hours. Deaths, raids and births may follow within 30 s.
3. **Recap.** When the day turns, one recap sentence of the day's big moments, once per day, none on a quiet day.
4. **Yields.** Chat to the narrator while a comment is being written: the comment is dropped, the steward's answer is
   not delayed.
5. **Off.** Switch the setting off, or Narrator plays off: no more comment lines.
6. **Age gate.** A wedding or a birth involving a minor: no romantic wording, ever.
Report: how it read (too chatty, too quiet, too grim?), any line with an invented name or a quote.

## H. Phase 3: combat and defense

Use a colony with 4-6 colonists, some guns, walls around part of the base and one or two outside doors. `$B` and
`$T` as above. Raids on demand: dev mode → "Execute incident" → Raid (the narrator never uses dev mode; you do).
Save first so each step can be retried.

43. **Fight view.** Start a pirate raid and wait until they appear. `curl -s -H "Authorization: Bearer $T" $B/combat | jq`:
    `active` true; one threat group with the right faction, count, kind (ranged), `where` matching where they really
    are (compass from your base), `heading` "toward home" once they walk in; your fighters with weapon, range, health,
    cover that matches what you see (a pawn behind a wall says "behind a wall", one in the field "in the open");
    children/pacifists under `others`; `defenses` a sane sentence about your walls, turrets and doors. `GET /vivarium`
    shows `combat.active` true within ~4 game seconds of the raid's arrival. `jq .combat` on `/digest` has the same.
44. **Squad orders.** `-X POST $B/act/squad -d '{"op_id":"s1","op":"draft"}'` → every capable colonist drafted (not
    children, pacifists, the sick in bed), greentext names them. `{"op":"move","at":"cover"}` → each walks to its own
    cell next to a wall or sandbag on the enemy's side, none in the open, none two to a cell. Try `at` chokepoint,
    door, wall_n (etc.), inside: the spots should match the words (behind the gap, just inside the door, behind that
    wall, under a roof away from the enemy). A position that does not exist → refused with a reason, nobody moves.
45. **Focus and hold.** `{"op":"focus","target":<group id from /combat>}` → the squad shoots that group; as one dies
    they switch to the next of the same group without new orders. A melee-only colonist against mechs stays back
    (the note says so). `{"op":"hold"}` → they stop and shoot from where they stand; no chasing.
46. **The controller.** Set `{"op":"retreat","below":0.5}` then `{"op":"move","at":"cover"}`; let a colonist get hurt
    below half: they walk back to the retreat point by themselves with a greentext "Ana fell back, badly hurt", once.
    When a colonist goes down near a healthy squad member, that member carries them to a bed ("is carrying ... to
    safety"). Nothing should twitch every few seconds (orders re-issued while pawns are shooting).
47. **Rescue, fires, flee, undraft.** `rescue` with a downed colonist → someone carries them to a (medical) bed.
    Start a fire in the home area: `extinguish` → up to 4 firefighters go to it. `flee` → children and pacifists go
    indoors, away from the enemy, and stay a while. `undraft` while enemies are within 30 cells → refused; after the
    raid → everyone undrafted, squad cleared.
48. **Defenses.** `-X POST $B/act/defend -d '{"op_id":"d1","kind":"sandbags","side":"n"}'` → a line of 7 sandbag
    blueprints across the north side, a few cells inside your north wall (never outside it, never behind your base's
    middle). `{"kind":"turret"}` without the research → refused naming it; with research but no power → refused with
    the watts; with a conduit nearby → a turret blueprint that sees the way in. `{"kind":"traps","side":"e"}` → spike
    traps on the path into the base from the east, outdoors. `{"kind":"killbox"}` → funnel walls outside your door
    toward the enemy, traps in the funnel, a sandbag line inside. `/undo` removes each of these.
49. **The speed rule.** Play at 3x, Narrator plays on, "The narrator slows the game for fights" on (Colonists →
    Narrator plays). Start a raid: the game drops to 1x with a greentext line; when the last raider is gone or flees,
    it goes back to 3x. Repeat, but change the speed yourself mid-fight: it must not be changed back. Turn the
    setting off: no slow-down.
50. **Steward in a fight.** Narrator plays on, the 26B running. Start a tribal melee raid, a pirate raid and (with
    Royalty/Biotech mechs) a mech raid, one per save. Watch the chat: a "combat started" look drafts and places the
    squad, then short combat turns every few game seconds (orders only, no chatter), then a "combat over" look that
    undrafts, rescues and gets back to work. Check: wounded pulled back, nobody meleeing a centipede, non-fighters
    indoors, no orders repeated over and over, and roughly how long each combat turn takes (the agent log's
    `elapsed_s`; the live check measured 4-6 s).

Report back: the step numbers that failed, the agent log around them, and how the fights felt (too passive, too
twitchy, sensible?).

## I. The narrator's view: reasons, food, clocks

Any colony past day 3. `$B` and `$T` as above. Each step: read the digest field, then let the narrator look once
(switch on, or chat as advisor) and see that it acts on the reason.

51. **Loose with nowhere to go.** Make your only stockpile food-only, then mine or deconstruct until steel lies
    loose. `curl -s -H "Authorization: Bearer $T" $B/digest | jq .build.loose`: the steel pile shows `"store":"none"`
    (fill the stockpile instead and it reads `"full"`; a far-away stockpile reads `"far"`). The narrator should make
    or widen a stockpile for it, not mine more steel.
52. **Stuck building.** Place a few wall blueprints and turn Construction off for everyone. `jq .stuck`: a row
    "N wall blueprints ...: no one can build (construction off or incapable)"; facts `stuck_no_builder` true. Turn it
    back on, but place steel walls with no steel: "waiting on steel: need N, have M". Forbid the steel: "steel is
    forbidden: unforbid it". The narrator should fix that reason (enable Construction, unforbid, haul or mine), not
    shuffle priorities.
53. **Stuck bill.** A stove bill with no raw food on the map: a `stuck` row "bill: ... no ingredients (...)". An
    electric bench unpowered: "bench has no power".
54. **People.** A colonist incapable of a work type: ask the narrator in chat to make them do it; the refusal names
    why ("Cooking: X can't (backstory or trait)"). A pawn with a missing or broken hand/arm shows `cannot` "no
    manipulation" or "weak hands"; a child shows `child`. A colonist wandering shows `idle` and the IDLE line counts it.
55. **Food.** `jq .food`: meals and raw counts match the stock (loose shown apart); food left outside in summer shows
    in `rot_soon` with days; a field near harvest shows ready cells and days for the rest (days include the night
    rest: compare with the plant's own "grows in" tooltip); wild deer/boar near home appear in `game`.
56. **Clocks.** A bleeding colonist: `jq .deadlines` shows "X bleeds out" with hours close to the health tab's
    estimate. A colonist with flu or an infection: "winning/losing the race" with hours. Late summer in a temperate
    biome: "winter (crops die)" with days; in winter: "growing season starts". A quest letter: "... expires".
57. **Size.** With all of the above at once, the agent's log shows the rendered digest under 6000 chars, and the
    narrator's turn still fits its time.

Report back: the step numbers that failed, the agent log around them, and how the narrator's play felt
(too busy, too quiet, sensible priorities?).


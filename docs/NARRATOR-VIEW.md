# The narrator's view of the game

The steward (the narrator when it plays) never sees the map. It sees the digest: a JSON object the mod builds
(`GET /digest`, `Mod/Source/RimWorld/VivariumDigest.cs`) that the agent renders into about 1k tokens of short lines
(`agent/rimworldllm_agent/games/rimworld/steward_digest.py`), plus the playbook alerts that fire from its `facts`
(`steward_rules.py`, `strategy/rules.json`).

**Design rule.** The model sees short summaries in words: counts, reasons, deadlines, and direction plus distance.
It never sees coordinates or item lists. The hands find the exact cells.
- A tool call says *what* ("a stockpile for steel", "harvest the rice").
- The hand picks *where*.

That keeps the digest small, and it keeps the model from reasoning about cells it can't see.

**Why this audit.** In a playtest the narrator said "keep mining" while plenty of steel lay on the ground. Every count
came from `map.resourceCounter`, which counts stored items only. That was fixed in bbd7696: piles now carry distance
and direction. This audit went through every field the same way, asking four questions:
- Is it storage-only?
- Is it a raw number with no meaning attached?
- Is it missing a reason?
- Is it missing entirely?

## Audit

Key to the Problem column:
- **storage-only**: counts stored items only.
- **raw**: a number with no words or unit.
- **no reason**: says that something is wrong but not why.
- **missing**: nothing in the digest.

Priority: P1 = fixed in this pass (contract sections 1-6); P2 = fixed if time allowed (section 7, done); P3 = noted, not
fixed.

| Field | Game source | Problem | Fix | Pri | State |
|---|---|---|---|---|---|
| `resources[].count` | `resourceCounter.AllCountedAmounts` | storage-only | `loose`/`forbidden` beside it (bbd7696) | P1 | done before |
| facts `steel`/`wood`/`components`/`silver` | `VivariumLoose.Usable` | was storage-only | stored + loose, not forbidden (bbd7696) | P1 | done before |
| facts `medicine_total`/`_industrial` | `resourceCounter` | storage-only | count loose medicine too | P3 | open |
| `build.loose[]` piles | loose scan | no reason why they stay loose | `store`: none / full / far / ok; facts `loose_unstorable`, `loose_far` | P1 | done |
| blueprints/frames | (absent) | missing: unfinished builds were invisible | `stuck[]` groups with one reason each (forbidden, unreachable, no builder, material forbidden, waiting on material N/M); facts `blueprints_stuck`, `stuck_no_material`, `stuck_no_builder` | P1 | done |
| `build.benches[].bills` | `BillStack` | no reason a bill doesn't run | `stuck[]` bill rows: no power, no one does the work type, no ingredients (which); fact `bills_stuck` | P1 | done |
| pawn `incapable` | `WorkTags` disabled | no reason; work-hand refusal said only "incapable" | refusal reads "Cooking: Ana can't (Pyromaniac, too young)" from `GetReasonsForDisabledWorkType` | P1 | done |
| pawn capacities | (absent) | missing: a person with no hands still got crafting work | pawn `cannot`: no manipulation / weak hands / can't walk / can't see well / can't talk | P1 | done |
| children | (absent; only `age`) | missing | pawn `child`; fact `colonists_children` | P1 | done |
| pawn `job` | `curDriver.GetReport()` | fine, but uncapped and no idle signal | cut to 60 chars; pawn `idle`; fact `colonists_idle`; rendered as `IDLE` plus an `IDLE: n of m` line | P1 | done |
| `food_days` | `Routes.FoodDays` | raw, and alone: no idea what food there is | `food` block: meals/raw with the loose part, rot_soon, harvest (ready cells, days), game nearby, hunters; facts `meals`, `raw_food`, `food_rot_soon`, `crops_ready`, `huntable` | P1 | done |
| rot | (absent) | missing | `food.rot_soon`, plus a rot deadline | P1 | done |
| bleeding | `colonists_bleeding` count | no clock | deadline "Ana bleeds out in 5 h"; fact `bleed_out_hours` | P1 | done |
| infections | `colonists_sick` count | no clock, no race | deadline "Bo's flu: losing the race"; fact `infections_losing` | P1 | done |
| season | `date.season`, facts `season` | no clock; rules used `days_to_winter`/`days_to_growing` but nothing supplied them | twelfth temperatures give a deadline "winter (crops die) in 12 days" and "growing season starts"; facts `days_to_winter`, `days_to_growing` | P1 | done |
| `letters[].expires_in_hours` | `LetterWithTimeout` | fine, but not ranked against other clocks | also a deadline row ("X expires") | P1 | done |
| `build.rooms[]` | `Room` | no temperature, cleanliness or overhead mountain | `temp_c`, `mountain`, `dirty` (`DIRTY`, `under mountain`, `18C`) | P2 | done |
| pawn `mood` | `CurLevelPercentage` | raw 0..100 | `break_risk` (near / minor / major / extreme, from the pawn's own thresholds) and `mood_why` (worst two thoughts) | P2 | done |
| apparel condition | (absent) | missing | fact `tattered_apparel` (worn apparel below 50% HP) | P2 | done |
| ancient danger | (absent) | missing | fact `ancient_danger` (a visible ancient casket that still holds contents) | P2 | done (caskets only, not other shrine contents) |
| reachability of loose items | (absent) | missing | fact `unreachable_piles` (loose stacks the colony can't reach, first 300) | P2 | done |
| player names | zone `label`, `LabelShort` | fine: zones and bedrooms already carry the player's names | none | - | fine |
| `temperature` | `OutdoorTemp` | raw; the digest key has no unit (facts `temp_c` does) | rendered "12C" already | - | fine |
| `research.current.progress` | `ProgressPercent` 0..1 | raw | rendered as a percent already | - | fine |
| `alerts[].alert` | alert class name | code name, not text | the renderer uses `label` only | - | fine |
| `threats`, `raid_active`, `enemies_count` | hostile pawns | no raid/manhunter kind or size | owned by the combat worker (agent/viv3) | - | not ours |
| `power.*` vs facts `power_net_w` | `PowerOutput` | two different numbers, units unnamed | name the unit (W) in the render | P3 | open |
| `colonists_without_bedroom` | owned bed role | counts barracks as no bedroom; `build.missing` counts it as shelter | align the two | P3 | open |
| `doctors_capable` | `WorkTypeIsDisabled(Doctor)` | counts downed doctors | exclude downed | P3 | open |
| `rooms.freezer` | first 300 food things below 0 C | heuristic, no roof check | use the room's temperature and role | P3 | open |
| stockpile contents | `build.zones[]` preset only | missing fill level | partly covered by store = full | P3 | open |
| `date.day` vs facts `day` | quadrum day vs absolute day | same name, different meaning | documented; the rules read facts `day` | P3 | open |

## What the narrator reads now

From the sample digest (`agent/tests/data/steward_digest_sample.json`), new lines only:

```
DEADLINES: Bram bleeds out in 6 h; raw potatoes rot in 19 h; winter (crops die) in 12 days
FOOD: about 2.4 days of food; 14 meals (3 loose); 120 raw (40 loose); rots soon: raw potatoes 30 in 19 h; harvest: rice 12 cells ready, 28 more in 36 h; game: deer 3 30 cells W; 2 hunters
- Cy: 27y, mood 71, fine, near a minor break (why: ate without a table, low light), doing Researching electricity
    ...; cannot Dumb Labor; can't: no manipulation: can't build, cook or craft; ...
- Dara: 24y, mood 38, bruised (left arm), minor break risk (why: sleeping in a barracks, dirty room), IDLE
- Eli: 11y, mood 58, child, fine, doing Sowing potato plants
IDLE: 1 of 5 idle
STUCK: 2 solar generator blueprints 14 cells NE: waiting on components: need 8, have 6; bill: simple meal at fueled stove: no ingredients (raw food)
ROOMS (...): ... #15 barracks 5x4 imp 16 bed door roofed 3C under mountain DIRTY
LOOSE: steel 450 14 cells NE (no stockpile takes it); wood 120 9 cells S (only far stockpile)
```

## Budget

Every new list has a cap in C# and a per-level cap in Python that shrinks as the digest is cut to fit the 6000-char cap:
- `stuck`: 6/4/2/0.
- `deadlines`: 6/6/4/3.
- rot and harvest: 3/3/2/1.
- game: 3/2/1/0.
- `cannot`: 3/3/2/1.
- mood why: 2/2/1/0.
- Room flags drop at build level B3.

Sample sizes, measured with `render_digest`:

| Code | Sample | Size |
|---|---|---|
| bbd7696 | old | 3109 chars (~777 tokens) |
| new | old | 3109 chars (unchanged: no new fields, no new lines) |
| bbd7696 | extended | 3178 chars |
| new | extended | 3847 chars (~961 tokens) |

The extended sample has every new case at once. A calm colony adds only the FOOD details.

## Where the code is

Each C# part is a pure helper in `Mod/Source/Bridge/`, unit-tested in `Mod.Tests/`, plus a thin game-side scan in
`Mod/Source/RimWorld/`. Each scan is cached per map per tick and capped. Hooks are one line each in
`VivariumDigest.cs`, `VivariumFacts.cs` and `VivariumBuildView.cs`.

| Part | Pure helper (`Bridge/`) | Game side (`RimWorld/`) | Tests |
|---|---|---|---|
| loose, stuck, people, idle | `NarratorWork.cs` | `VivariumStuck.cs` | `NarratorWorkTests.cs` |
| food | `NarratorFood.cs` | `VivariumFood.cs` | `NarratorFoodTests.cs` |
| clocks | `NarratorClock.cs` | `VivariumDeadlines.cs` | `NarratorFoodTests.cs` |
| extras | `NarratorExtras.cs` | `VivariumExtras.cs` | `NarratorFoodTests.cs` |

On the agent side:
- The render is in `steward_digest.py`.
- The rules are in `strategy/rules.json`: `build.loose_no_stockpile`, `build.stuck_no_material`,
  `build.stuck_no_builder`, `work.bills_stuck`, `food.harvest_before_starving`, `food.hunt_before_starving`,
  `food.rot_soon`, `winter.coming` and `health.infection_losing`, plus an amended `health.bleeding`.
- The strategy section "Reading the digest's new lines" is in `strategy/steward_strategy.md`.

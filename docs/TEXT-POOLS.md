# Written flavour text: where RimWorld picks from fixed strings, and what we write instead

The keeper's ask: "the descriptions of art, where rimworld normally chooses from a set of strings, but we can infinitely
generate these instead. there are likely dozens of such places."

The file name says "pools" because the first plan was pools of pre-written entries per kind. The keeper replaced that
plan before it was built: **no prefilled corpus. Each text is written for one instance** (one artwork, one grave,
one book, one log line), queued when the thing is made, with a snapshot of what was going on, so the text can be
specific and situationally funny. This page holds the inventory (step 1) and the design as built.

## 1. Inventory (RimWorld 1.7, Core + Biotech)

Counted from the game's Defs: 184 RulePackDefs in 38 files, plus inline rules in ResearchProjectDef (91), TaleDef (82),
QuestScriptDef (58), InteractionDef (31), SkillDef (12), MemeDef (2), and maker fields on ThingDef, FeatureDef,
FactionDef, XenotypeDef, CultureDef, ManeuverDef and DamageDef. 72 word files under `Strings/` (about 616 KB) feed
them through `Rule_File`. Ideology, Royalty and Anomaly are not installed in the reference copy, so their memes,
precepts, rituals and psychic text are not listed (Ideology's meme/precept text would sit in the Ideology rows below).

Every one of these is resolved by `Verse.Grammar.GrammarResolver.Resolve(rootKeyword, GrammarRequest, ...)`. The
column "call site" is the game method that resolves and keeps (or re-resolves) the text.

| Kind | Source (defs / root keyword) | Call site | Context there | How often | Safe to replace? | Priority |
|---|---|---|---|---|---|---|
| Art description | `ArtDescriptionRoot_HasTale` / `_Taleless`, `ArtDescription_Sculpture/WeaponMelee/WeaponGun/Furniture/SarcophagusPlate`, `TalelessImages`, `ArtDescriptionUtility_Global` (171 rules); root `r_art_description` | `CompArt.GenerateImageDescription` (re-resolved on every read, seeded per piece) | the piece, material, quality, tale (pawns, date), maker via `JustCreatedBy` | each artwork made or shown | **Yes** (display only) | **1, shipped** |
| Art title | `NamerArtCommon`, `NamerArtWeapon` (+ empty children); root `r_art_name` | `CompArt.GenerateTitle` at init, kept in `titleInt` | same as above | each artwork | **Yes** (display; shown through the `Title` getter) | **1, shipped** |
| Sarcophagus plate | `ArtDescription_SarcophagusPlate`, init with the corpse | `CompArt.InitializeArtInternal(relatedThing = corpse)` | the buried pawn, how they died | each burial in a sarcophagus | **Yes** | **1, shipped (as art)** |
| Grave epitaph | none in vanilla (plain graves have no text) | `Building_Grave.TryAcceptThing` / `GetInspectString` | the buried pawn, death cause (noted at `Pawn.Kill`), mourners | each burial | **Yes** (additive line) | **1, shipped** |
| Book title | `Namer_TextBook`, `Namer_Schematic`, `Namer_Novel` (458 rules); root `title` | `Book.GenerateBook`, stored in `Book.title` | author (if written in the colony), quality, subjects | each book | **Yes** (display; saved by the game) | **2, shipped** |
| Book blurb | `Description_SkillBook/Schematic/Novel` (1,191 rules); root `desc` | `Book.GenerateBook` -> `descriptionFlavor`; `GenerateFullDescription` appends the effects | same | each book | **Yes** for the flavour part (the effects lines are rebuilt by the game) | **2, shipped** |
| Social log line | `InteractionDef.logRulesInitiator` (31 defs, `r_logentry`), `Sentence_*` extra packs, Biotech `LessonInteraction*` | `PlayLogEntry_Interaction.ToGameStringFromPOV_Worker` (cached per POV, re-resolved after load) | initiator, recipient, interaction def, opinions, relation | every interaction (many per minute) | **Yes, except**: romance/marriage/breakup/lovin' (age gate: left to the game), and entries with extra sentence packs (they report an outcome: a social fight, a recruit result) | **3, shipped** |
| Combat log line | `Combat_*` (CombatMelee/Ranged/Includes), `Maneuver_*` (45), `Damage_*`, `DamageEvent_*`, `Transition_*`, `Event_*` | `BattleLogEntry_*.GenerateGrammarRequest` + `LogEntry.ToGameStringFromPOV` | initiator, target, weapon, body parts hit, damage | every shot/blow (very many) | **Partly**: ranged fire and melee miss/dodge/deflect yes; hits, wounds, deaths, downs no (the line names the wound) | **3, shipped, default off** |
| Battle names | `Battle_Solo/Duel/Internal/War/Brawl`; root `r_battlename` | `Battle.GetName` | factions, pawns | per battle | Mostly (display), but short formulaic names; little to gain | not shipped |
| Tale text | `TaleDef.rulePack` (82, `tale_noun`, `image`, `circumstance_phrase`) | inside art descriptions; `Tale.ShortSummary` | the tale's pawns and things | per tale | Feeds art; as its own text it is barely shown | not shipped (used as art context) |
| Growth moment letter | Biotech `GrowthMomentFlavor` (162 rules); root `r_root` | `ChoiceLetter_GrowthMoment` | the child, traits, passions | per child birthday at 7/10/13 | Possible, but about children: kept vanilla | not shipped |
| Quest names | `QuestScriptDef.questNameRules` (21), `NamerQuestDefault` | `QuestGen` | slate values | per quest | Display, but resolved with slate constants; risky | not shipped |
| Quest descriptions, quest letters | `questDescriptionRules` (27), `questContentRules`, `customLetter*Rules` | `QuestGen` / `QuestPart_Letter` | slate values, rewards | per quest | **No**: carry `(*Threat)`, `(*Reward)` tags and mechanics the player needs | never |
| Research / skill subjects | `ResearchProjectDef.generalRules` (91), `SkillDef.generalRules` (12) | inside book text, talk topics | the project or skill | as part of other text | No (fragments, not texts) | never |
| Pawn names | `NamerPerson*`, xenohuman namers, `Names/*` files, `CultureDef.pawnNameMaker` | `PawnBioAndNameGenerator`, `NameGenerator.GenerateName` with uniqueness checks | gender, culture, xenotype | per pawn | **No**: names are keys (relations, uniqueness, saves) | never |
| Faction / settlement names | `NamerFaction*` (12), `NamerSettlement*` (11), Biotech faction namers | `NameGenerator.GenerateName` | faction def | at world gen | Not safe enough (uniqueness, world gen time) | not shipped |
| World features, planet, scenario | `NamerWorld`, `WorldFeatureNamerCommon` + 16 terrain namers, `NamerScenario` | world gen | terrain | world gen | Display, but generated before any model could answer | not shipped |
| Animal, mech, trader names | `NamerAnimalGeneric*`, `NamerMech`, `NamerTraderGeneral` | taming, mech gestation, trader arrival | species, gender | occasional | Names again (player renames them; keys in relations) | not shipped |
| Xenotype / genepack names | `NamerXenotype` (62), `NamerGenepack` | Biotech gene assembler, genepacks | genes | occasional | Names used as keys in xenotype lists | not shipped |
| Seeds, gameplay tips | `SeedGenerator`, `GameplayTips` | main menu / world gen | none | rare | No value | never |

Markup seen in rule text: no `<color>` and no `{0}` placeholders. `(*Name)`, `(*Threat)`, `(*Reward)`, `(*SectionTitle)`
and `(*DateTime)` tags appear only in quest scripts (23 lines) and a few defs: those are the "parsed back" text we leave
alone. Bracket tokens left for later resolution (`[PAWN_nameDef]`, `[INITIATOR_definite]`, `[RECIPIENT_nameDef]` ...)
are everywhere in tales, interactions and combat; ours never carry the game's tokens (see below).

## 2. Design as built

### The flow
1. **Made in the world.** A Harmony postfix sees the instance being made and queues one item on the text desk
   (`Mod/Source/Bridge/TextDesk.cs`, game side in `Mod/Source/RimWorld/TextHook.cs`):
   - art made by a colonist: `CompArt.JustCreatedBy` (the maker is known);
   - a sarcophagus plate: `CompArt.InitializeArtInternal` with the corpse;
   - a grave filled: `Building_Grave.TryAcceptThing` (a plain grave; a sarcophagus uses its art plate);
   - a book written in the colony: `Book.GenerateBook` with an author;
   - a log line: `PlayLog.Add` (social) and `BattleLog.Add` (combat).
2. **Context snapshot, taken then.** Who made it and their mood, traits, background, skill and current job; the room;
   who is nearby; the colony (name, date, weather, temperature, colonist count, the last four letters); for graves the
   dead pawn, the cause noted at `Pawn.Kill` (damage, by whom, weapon, culprit, what they were doing, their mood) and
   the mourners; for log lines both pawns, their opinions of each other and their relation. `minor_involved` is set when
   anyone involved is under 18. The game's own text for that very instance is kept as `example`.
3. **Never block.** The hook returns the game's text at once. Nothing calls the model from a hook.
4. **Written in the background.** The agent's lowest-priority idle job (`Mod/Source/Mind/RimWorld/TextFill.cs`) reads
   `GET /text/queue`, writes up to three items of one kind in one structured call on an actor slot (`Cls.IDLE`,
   preemptible), cleans each text and posts `POST /text/fill`.
5. **Ours from then on.** Stored per instance, keyed `art:<thingID>`, `grave:<graveID>:<deadPawnID>`,
   `book:<thingID>`, `log:<logID>`, and saved with the game (`rwlTexts`). Reads return it:
   - art: `GenerateImageDescription` and the `Title` getter;
   - grave: a line `Epitaph: ...` in the inspect pane;
   - book: the book's own `title` and `descriptionFlavor` fields are set (the game saves them) and the full
     description is rebuilt; a book out of reach when the text arrived gets it the next time its card is read;
   - log line: `LogEntry.ToGameStringFromPOV`, with `[INITIATOR]` / `[RECIPIENT]` filled with the current names; the
     entry's cached height is reset.
6. **First shown, for anything missed.** Art (inspect pane or card), graves (inspect pane), books (info card) and log
   lines (drawn) that were made before the mod ran, by traders or map generation, or dropped by the queue cap are queued
   the first time they are shown, once only (the desk remembers every id it ever queued, saved, up to 20,000).

### Queue rules
- Priority by kind: art, graves, books 3; social 1; combat 0. Served highest first, then oldest.
- Cap 40 items. A full queue drops its oldest lowest-priority item; a new item lower than everything queued is dropped.
- An item leaves after two game days unwritten, or after being handed out three times without a fill (the agent never
  retries an id it wrote once).
- Kept texts per kind: art 3,000, graves 1,000, books 2,000, social 600, combat 300 (oldest dropped: an old log line
  falls back to the game's text, which is what the log would show anyway).
- Per-kind switches in the settings (below). An off kind is not queued, not served, and not shown; its stored texts
  come back when it is switched on again.

### Validation (mod side, `TextValidate`)
Whitespace collapsed, wrapping quotes stripped; length within the kind's bounds (art 40-500, title 60; grave 10-200;
book 60-600, title 80; social 15-220; combat 15-200); no `<`, `>`, `{`, `}`, `(*`, `(/`; square brackets only as the
kind's tokens (`[INITIATOR]`, `[RECIPIENT]` for log lines; none elsewhere). The agent applies the same rules before
posting, plus the age gate's `romantic()` check on social lines and on anything with `minor_involved`.

### Why the game's tokens are not used
Our text could have carried the game's `[PAWN_nameDef]`-style tokens and been resolved through the game's own grammar.
Per-instance texts don't need it: art, grave and book texts are fixed once written (literal names from the snapshot are
right for that instance), and log lines only need the two names, which `TextTokens.Fill` puts in at read time. That
keeps the hook free of grammar calls and the validation simple.

### Safety
- Romance-ish interactions (`romance`, `marriage`, `breakup`, `lovin`, `flirt`, `seduc`, `proposal` in the def name)
  keep the game's line entirely (hard age gate; `TextLogRules.Unsafe`). Social lines are asked to be platonic and
  all-ages, and are dropped when `agegate.romantic()` trips.
- Lines that report an outcome (extra sentence packs) and combat lines naming a wound keep the game's text.
- Names, quests, letters and anything parsed back are never touched.
- Every hook is wrapped: any exception means the game's text.

### Live check (poe GPU 0, the 26B `gemma4-rp-maxxed-Q3_K_XL`, fresh engine home)
Hand-built queue items in the exact shape the mod records, run through the real `fill_once`. Vanilla vs written:

| Kind | The game's text | Written |
|---|---|---|
| art (Rook, pyromaniac, mood 31%, marble, tale: Tok vomited on a muffalo, foggy rain, boomalope just exploded) | *Blood and Grain*: "This sculpture depicts Tok being sick on a muffalo. The muffalo looks stoic. The piece has an ugly, almost painful aspect to it. Its abstract shape is reminiscent of a hammer." | *A Gastric Misfortune*: "This marble sculpture captures the exact moment of gastrointestinal failure as Tok projectile vomited onto a confused muffalo. The fine grain of the stone highlights the messy texture of the incident, though the sculptor's distracted gaze during the foggy rain suggests he was more interested in the nearby sparks of a boomalope explosion than his subject." |
| grave (Bram, greedy, tough, muffalo rancher, killed by a boomalope's blast while milking it) | (none) | "A tough rancher who finally met a beast that wouldn't be milked. He died as he lived, chasing profit." |
| book (novel by Mira, neurotic and kind, medical student) | *The Last Ember of Varn*: "This poorly written novel follows a miner as she tries to escape a crumbling city. The writing is clumsy, but the ending is touching." | *The Healer's Quiet Vigil*: "A gentle tale of healing and hope, following a young healer through the trials of a long winter. The prose is meticulous and soothing, reflecting the author's own compassionate nature, though a certain nervous tension lingers between the lines." |
| social, insult (Tok, abrasive, 22% -> Rook, pyromaniac) | "Tok insulted Rook's taste in sculpture." | "Tok insulted Rook for being a pyromaniac while eating a simple meal." |
| social, chitchat (Mira -> her daughter Lia, 9; `minor_involved`) | "Mira and Lia chatted about pets." | "Mira chatted with Lia about her favorite toys." |

Every item was stored on the first try (no validation drops). Art, graves and books read as specific to their moment;
social lines are correct but plain. The engine runs `json_schema` calls on its structured sampler profile, so the same
context gives the same text (the 0.9 temperature the filler asks for did not change the output); in play every
instance has its own context, so this matters less than it looks. See "Decisions" in the release playtest section.

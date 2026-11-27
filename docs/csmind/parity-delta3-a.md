# Parity notes: delta3 part A (combat)

Port of the combat part of `git diff 27bcd9d HEAD -- agent` (Python commits 4739023, cea6641, 49dde51, 861f43e) into `Mod/Source/Mind`.
Keeper decision: no caravans at all (see "Left out").

## File map
| Python | C# |
|---|---|
| `games/rimworld/steward_combat.py` (CombatKit, render_combat, DIGEST_CAPS) | `Mod/Source/Mind/RimWorld/StewardCombat.cs` (`CombatKit`, static `StewardCombat`: `SystemPrompt()`, `RenderCombat`, `DigestCaps`) |
| `steward_combat.txt` | `Mod/Source/Mind/RimWorld/Resources/steward_combat.txt`, embedded (`RimWorldLLM.Mind.steward_combat.txt`) in Mod, Mod.Tests and MindHarness csproj (pyproject package data analogue) |
| `core/steward.py` combat cadence, speed rule, kit interface | `Core/Steward.cs`: `IStewardCombatKit` (optional, mirrors `hasattr(kit, "combat_session")`), `CombatOn/CombatStart/CombatOver/Slowed`, `CombatStateAsync`, `RestoreSpeedAsync`, `CombatWake`, `RunCombatAsync`, constants |
| `core/bridge.py` `combat()`, speed guard | `BridgeClient/BridgeClient.cs` `CombatAsync()`, `Speed(JToken)` envelope guard; `BridgeTypes.cs` |
| `core/config.py` steward_combat_gap_game_s / _real_s, docs/SETTINGS.md | `Llm/MindConfig.Fields.cs`, `docs/SETTINGS.md`, `ConfigTests` |
| `steward_tools.py` (combat parts) | `RimWorld/StewardTools.cs` (session ctor `afterFight`, `CannotFight`, `Combat`, draft "all", order move 0,0, `order` dropped when squad exists, AdvisorNote text), `RimWorldStewardKit.cs` |
| `fakes.py` combat | `Mod.Tests/Mind/Fakes/FakeBridge.cs` (+ `CombatMissing` flag for a failing GET /combat, `ActApply` internal) |
| `steward_try.py --combat/--combat-cycle` | `tools/MindHarness/StewardTry.cs`, `Program.cs` usage |
| `tests/data/combat_*.json`, `combat_digest_*.json` | `Mod.Tests/Mind/Data/` embedded as `RimWorldLLM.Tests.<name>` |

Visibility changes for tests (all additive): `StewardSession.VerbBody`, `CombatKit.Squad/Defend`, `Steward.CycleAsync` are `internal`; `StewardFake.CallRec` gained `MaxTokens`.

## Behaviour differences
- Target numbers (focus target, `group N`, bare ids) are parsed as `BigInteger` then narrowed to `long`; Python ints are unbounded. Same results for any real id.
- Lower-casing uses `ToLowerInvariant`; Python `str.lower()` differs only for exotic Unicode.
- A wrong-typed list in the view (a string where a list is expected) is read as empty, as Python's `or []` does for falsy but not for a truthy wrong type (which would raise in Python). Never produced by the mod.
- `int()` of text with underscores (`"1_000"`) is accepted in Python and not here; the grammar never emits it.
- `DigestCaps` is defined in `StewardCombat.cs` as `{1500,1000,700,450}`; B owns `DIGEST_CAPS` in the digest, so the merge should keep one definition (point B's at `StewardCombat.DigestCaps` or delete mine).
- The combat resource loader lives in `StewardCombat.cs` so `StewardRules.cs` (B's) is untouched.

## Tests
Ported by the same name (PascalCase) in `Mod.Tests/Mind/StewardCombatTests.cs`: every test in `test_steward_combat.py` except the two below that run in `StewardTryCliTests.cs` with the three steward_try ones. `StewardBuildTests.PromptAndSize` limit 8500 -> 8800. Packaging test is the embedded-resource assertion.

Deferred to merge (written, `Skip` with a reason, or guarded by `CombatData.DigestHasCombat` so they switch on by themselves):
- `DigestGetsCombatLinesAfterAlertsAndKeepsThemUnderTheCap` (Skip): the digest COMBAT lines are B's.
- `PromptsAndPackagingSystemAndStrategyMentionCombat` (Skip): the system and strategy text are B's (the "squad"/"defend" lines, "Never melee a centipede").
- `COMBAT:` / "scythers" assertions on a full-digest user message (CombatStartIsAFullCycle..., ChatDuringAFight..., AdvisorMode..., StewardTryFullCombatCycle...) run only when the digest renders COMBAT.

Whole-port goldens were not regenerated; the tests that fail by golden (steward_golden, jobs-steward-golden, e2e-steward-*) are listed in the report.

## Left out
- Caravans (keeper decision): no caravan tools, planners, prompt lines or tests.
- Not combat, not mine: bio/child (CHILD_AGE, `_child`, ages, BioKit, strategy_extra) and trade (TradeKit, LOOKS, `look()`, trade_offer/trade, `--trade-turn`, narrator settings, growth `choose`) belong to C. `StewardTools.cs` carries a marked SEAM comment where the trade kit plugs in.

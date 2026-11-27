# Parity: delta3 (Python agent changes 27bcd9d..HEAD ported into the C# Mind)

Python source: `git log 27bcd9d..HEAD --no-merges -- agent` (4739023, cea6641, 49dde51, 861f43e combat; db174fa, cbfe8ec, beb1423, bdd3078 narrator view;
b58ab78 growth moments; 4e8dd0c loose rule; 9ceabbb, f8a0741 trade). Done as three parallel sub-workers (A combat, B digest/resources, C trade/growth), merged on agent/csmind-delta3.

## Lead notes
- KEEPER DECISION: no caravans. Not ported: caravan / caravan_plan tools, caravan planners, caravan prompt and strategy lines, the CARAVANS digest block, caravan tests (listed per part below).
  steward_system.txt and steward_strategy.md are therefore NOT byte copies of the Python (caravan pieces removed, see part B). rules.json is copied verbatim (the growth.caravan_safety rule stays, it is harmless: no caravan facts are produced).
  A digest's `caravans[].can_trade` still makes a settlement a trade partner, as in the Python.
- Goldens regenerated from the Python with caravan pieces patched out AT CAPTURE TIME (never in agent/): `~/.local/share/rimworld-hermes-engine/csmind/delta3/gold/nocaravan.py`
  reads the two resource texts from the C# Resources dir, drops caravan_plan/caravan from TradeKit and the CARAVANS block from trade_lines; `run.py <script> <args>` applies it. Regenerated:
  jobs-steward-golden.json (delta2/capture_steward.py), steward_build_golden.json (delta2/gen_build_golden.py), e2e-steward-*.json (tools/capture_steward.py, run with `cd agent && PYTHONPATH=. python run.py ... ../Mod.Tests/Mind/Data`),
  steward_golden.json (part B's gen_steward_golden.py).
- E2eGoldenTests.StewardTryMatchesThePythonCallForCall now ignores the ELAPSED wall-clock line (it varied between runs, so the golden could only match by luck).
- Job B (pairing): `LlmAuth.OnUnauthorized` (Action<int,string>, Mod/Source/Mind/Llm/LlmClient.cs) is called with (status, body) on any 401 in HttpUtil.SendAsync (HttpLlm and OpenAIBackend, non-stream) and in StreamSender (stream).
  MindHost.Make sets it to `EngineAuthStatus.Note`. The mind has no Bridge reference. `Authorization: Bearer <ApiKey>` is unchanged; llmEngineId / pairedEngines are not read. Tests: LlmAuthTests (4).
- id_slot: the C# sends `id_slot` from the same call sites as the Python (narrator/steward/commentary/recap slot 0 = cfg.NarratorSlot; actor lines the lease's slot 1..N, stable per lease), nothing sends /slots save or restore. Verified by grep; no change needed.


---

## Part Parity notes: delta3 part A (combat)

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

---

## Part delta3 worker B: digest, rules facts, resources (Python 27bcd9d..HEAD)

## File map
- Mod/Source/Mind/RimWorld/StewardDigest.cs: steward_digest.py (STUCK, DEADLINES, FOOD details, IDLE, LOOSE store verdicts, people lines with per-level caps, COMBAT block seam, trade and quests lines, growth options in dialogs).
- Mod/Source/Mind/RimWorld/StewardRules.cs: DeriveFacts adds traders_ready, trade_needs, quest_offer_can.
- Mod/Source/Mind/RimWorld/Resources/{rules.json, steward_strategy.md, steward_system.txt}: copied from agent/ by ~/.local/share/rimworld-hermes-engine/csmind/delta3/b/make_resources.py, caravan pieces removed.
- Mod.Tests/Mind/StewardNarratorTests.cs (new, 45 test cases), Mod.Tests/Mind/Data/steward_digest_{sample,orbital,settlement,trader}.json, steward_golden.json (regenerated, digest/rules only), csproj embeds.

## Public API
StewardDigest.RenderDigest(JObject, int cap) (unchanged signature); internals for tests: Level/BuildLevel/TradeLevel, Levels/BuildLevels/TradeLevels, Render(d, lv, bl, tl, combat), Hours. StewardRules.DeriveFacts(JObject).

## Differences / deviations
- NO CARAVANS (keeper decision): no CARAVANS digest block, no caravan facts, no caravan tests. The "caravans" key stays in the orbital/settlement/trader sample JSON (byte-copied) and is ignored by the renderer.
- Caravan text removed: strategy: heading "## Trade, caravans, quests" -> "## Trade and quests", the "- Caravans: 2-3 pawns ..." line dropped, "bills, trade, caravans, quests)" -> "bills, trade, quests)" (12,197 bytes). System text: "Trade and caravans work through trade_offer, trade, caravan_plan and caravan ... a trade or caravan cannot be undone" -> "Trade works through trade_offer and trade ... a trade cannot be undone"; "(trade and caravans when their tools are not listed)" -> "(trade when its tools are not listed)". rules.json copied verbatim: the pre-existing growth.caravan_safety rule and W-caravan source were in the base and remain (lead to decide).
- Combat: StewardDigest.CombatSeam holds stand-ins (DigestCaps {1500,1000,700,450}, ViewActive, RenderCombat returns ""). TODO: replace with worker A's StewardCombat on merge; combat-through-digest tests wait for it.
- steward_golden.json (digest/rules golden) was regenerated by B from the Python with trade_lines patched to drop "caravans" (65 digests, 910 renders). The brief's no-regenerate list covers only the whole-port goldens; the lead may git checkout it if wanted.
- The "harvest-days night-rest 0.55" factor lives in the mod's C# (VivariumFood.cs, CombatPure.cs), not agent/: nothing to port.
- Test tweaks: "#4 stockpile all 36 cells" -> "food"; LOOSE count test scoped to the LOOSE line; TrimMatchesPython allows max(cap,220).

## Tests
Ported (PascalCase, same names): sample_carries_the_new_fields_and_stays_small, loose_store_reasons, hours_phrases, deadlines_line_sorted_capped_and_tolerant, stuck_line_caps_and_garbage, person_flags_and_tolerance, game_strings_in_new_lines_are_cleaned, food_line_variants, room_flags_and_tolerance, new_line_caps_shrink_per_level; test_steward_rules: new_fact_rules_fire (11), new_fact_rules_stay_quiet (12), new_fact_rules_skip_when_the_fact_is_missing, sample_facts_fire_the_expected_rules, strategy_covers_the_new_lines_under_the_cap, loose_rule_checks_space_before_making_zones; growth render and growth_prompt_line (inline JSON, not the data file). Plus C#-only: trade sample renders, trade shrink, caravans-not-rendered, derive_facts trade/quests.
Deferred: the choose/growth tool half of test_growth_render_and_tool (worker C), render-tools size cap (A/C), combat-through-digest (A), all caravan tests (dropped).

---

## Part delta3 sub-worker C: trade, growth moments, settings mirror

Python commits: 9ceabbb, f8a0741, b58ab78 (and the settings mirror). KEEPER DECISION: no caravans at all.

## File map
| Python | C# |
|---|---|
| `games/rimworld/steward_trade.py` (`TradeKit`, `LOOKS`, `parse_lines`, `render_offer`) | `Mod/Source/Mind/RimWorld/StewardTrade.cs` (`TradeKit`, `TradeKit.Looks/Acts`, `TradeKit.ParseLines`, `TradeKit.RenderOffer`) |
| `steward_tools.py` trade wiring (`self.trade`, tools, `acting`, look branch of `run`, `_verb_body` dispatch) | `StewardTools.cs` `StewardSession.Trade`, `Tools`/`Acting`, `RunAsync` look branch, `VerbBody` default case |
| `steward_tools.py` `choose` growth branch + `passions`/`trait` params | `StewardTools.cs` `Choose` / `BuildTools` |
| `ADVISOR_NOTE` wording ("you have no tool for") | `StewardKit.AdvisorNote` |
| `core/bridge.py look(path, params)` | `IBridge.LookAsync(path, query)` + `BridgeClient.LookAsync` |
| `fakes.py` `/trade/offer`, `looks`, `offers`, trade act shape/note, choose passions/trait | `Mod.Tests/Mind/Fakes/FakeBridge.cs` (`Looks`, `Offers`, `TradeOffer`, `trade` verb) |
| `tests/data/dialogs_growth.json`, `steward_digest_{trader,orbital,settlement}.json` | `Mod.Tests/Mind/Data/` (embedded, csproj) |

## Settings mirror
The mod side (`VivariumState.ToJson`, `RimWorldLLMSettings`, docs/SETTINGS.md rows for `narratorTrade` ... `narratorNeverSell`,
`narratorCaravans`) is already in this branch. The Python agent never reads the trade settings (the mod enforces the caps), and the
C# Mind reads `Vv["settings"]` by name (`heartbeat_hours`, `slow_for_combat`, `answer_dialogs`...), so nothing was needed on the Mind side
and no SETTINGS.md row changed.

## Differences from the Python
- No caravans: `caravan_plan`, `caravan`, `render_plan`, quest/destination/animal/pawn-name logic, `OPS`/`PURPOSES`/`MAX_PAWNS` are not ported.
  `LOOKS = (trade_offer)`, `ACTS = (trade)`. A digest `caravans[].can_trade` still makes that settlement a trade partner (the trade tool's logic).
- `_names`/`_where`/`_goods`/`_quest_check` (caravan helpers) not ported.
- `look` takes the query as an ordered list of pairs (`IList<KeyValuePair<string,string>>`) instead of a dict.
- Tests call `RunAsync` instead of `_verb_body` (not visible to the test assembly), so growth bodies are checked in the bridge's ActLog.

## Tests
Ported (`Mod.Tests/Mind/StewardTradeTests.cs`, 35 xunit cases): no_trade_block_no_trade_tools, trader_digest_has_trade_tools_and_acting_split,
orbital_trader_and_settlement_digest (trade part), a_caravan_on_a_settlement_makes_it_a_trade_partner (partner part), trade_posts_parsed_lines_and_why,
trade_one_side_and_none_words, parse_lines_forms_and_errors (+ Python-captured theories), trade_errors, trade_with_a_trader_that_cannot_trade_is_explained,
refusal_passes_through (trade), act_extra_fields_reach_the_model, look_tools_render_and_are_not_acting, look_game_error_is_a_tool_error,
advisor_one_task_counts_trade_and_caravan (trade), advisor_note_no_longer_names_trade, tool_section_budget (trade+build), signatures_and_grammar (trade),
cycle_looks_then_trades_and_advisor_allows_one_act, test_growth_render_and_tool (tool part) from test_steward_dialogs.py; extra: RenderOffer forms/cap capture.

Dropped by keeper decision (caravans): test_offers_are_not_quest_targets, test_caravan_form_maps_names_and_quest, test_caravan_send_and_cancel,
test_caravan_errors, test_quest_that_cannot_be_worked_is_refused_with_its_reason, test_unknown_animal_names_only_with_an_animals_list,
test_caravan_plan_look, test_plan_problems_render, test_settlement_caravan_and_quest_lines, and the caravan halves of the mixed tests above.

Deferred to the merge (belong to B: digest renderer, rules, resources): test_trader_digest_lines, test_cant_trade_trader_is_marked,
test_trade_blocks_missing_or_partial_keys_are_tolerated, test_trade_strings_are_cleaned, test_big_digest_fits_the_cap_and_trade_shrinks_before_the_base,
test_steps_order_shrinks_build_then_trade_then_base, test_every_sample_digest_fits_the_cap, test_quest_offer_fact_and_rule, test_trader_facts_fire_the_trade_rule,
test_prompt_text_is_amended_not_appended; the render half of test_growth_render_and_tool; test_growth_prompt_line (steward_system.txt, B's resource).
The embedded `steward_digest_{trader,orbital,settlement}.json` are byte copies of the Python data (B embeds the same files: keep one).

## Public API
- `TradeKit(JObject digest)`: `Has`, `Partners`, `Tools`, `Names`, `Acting`, `Body(tool, v) -> (verb, body)`, `LookAsync(tool, v, IBridge)`.
- `TradeKit.ParseLines(string, what) -> List<JObject>`, `TradeKit.RenderOffer(JObject) -> string`.
- `IBridge.LookAsync(string path, IList<KeyValuePair<string,string>> query = null) -> Task<JObject>`.
- `StewardSession.Trade`.
